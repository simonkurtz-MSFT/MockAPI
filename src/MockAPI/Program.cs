using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);
var options = MockApiHostConfiguration.CreateOptions(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    builder.Environment.ContentRootPath);

builder.WebHost.ConfigureKestrel(MockApiHostConfiguration.ConfigureKestrel);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ConfigurationState>();
builder.Services.AddSingleton<ConfigurationFileStore>();
builder.Services.AddSingleton<EndpointManagementService>();
builder.Services.AddSingleton<ConfigurationManagementService>();
builder.Services.AddSingleton<RequestStatisticsCollector>();
MockApiHostConfiguration.AddRateLimiting(builder.Services, options);
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseRateLimiter();
var configuration = app.Services.GetRequiredService<ConfigurationState>();
var configurationStore = app.Services.GetRequiredService<ConfigurationFileStore>();
var endpointManagement = app.Services.GetRequiredService<EndpointManagementService>();
var configurationManagement = app.Services.GetRequiredService<ConfigurationManagementService>();
var statistics = app.Services.GetRequiredService<RequestStatisticsCollector>();

await configurationStore.LoadAsync(configuration, CancellationToken.None);

if (options.EnableDashboard)
{
    var version = MockApiHostConfiguration.GetVersion(typeof(Program).Assembly);
    var dashboardHtml = (await File.ReadAllTextAsync(
        Path.Combine(app.Environment.WebRootPath, "index.html"),
        CancellationToken.None)).Replace("{{VERSION}}", version, StringComparison.Ordinal);

    app.MapGet("/app.css", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(
            Path.Combine(app.Environment.WebRootPath, "app.css"),
            "text/css; charset=utf-8");
    })
        .ExcludeFromDescription();
    app.MapGet("/app.js", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(
            Path.Combine(app.Environment.WebRootPath, "app.js"),
            "text/javascript; charset=utf-8");
    })
        .ExcludeFromDescription();
    app.MapGet("/dashboard-core.js", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.File(
            Path.Combine(app.Environment.WebRootPath, "dashboard-core.js"),
            "text/javascript; charset=utf-8");
    })
        .ExcludeFromDescription();
    app.MapGet("/favicon.svg", (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(
            Path.Combine(app.Environment.WebRootPath, "favicon.svg"),
            "image/svg+xml");
    })
        .ExcludeFromDescription();
    app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return Results.Empty;
        }

        return Results.Text(dashboardHtml, "text/html; charset=utf-8");
    })
        .ExcludeFromDescription();
    app.MapGet("/__mockapi", () => Results.Redirect("/"))
        .ExcludeFromDescription();
}

if (options.EnableManagementApi)
{
    ManagementApiEndpoints.Map(app, endpointManagement, configurationManagement, statistics);
}

if (options.EnableManagementApi && options.EnableOpenApi)
{
    app.MapOpenApi("/__mockapi/openapi/{documentName}.json");
}

if (options.EnableManagementApi && options.EnableSwaggerUi)
{
    app.UseSwaggerUI(swagger =>
    {
        swagger.RoutePrefix = "__mockapi/swagger";
        swagger.SwaggerEndpoint("/__mockapi/openapi/v1.json", "MockAPI Management API v1");
        swagger.DocumentTitle = "MockAPI Management API";
    });
}

app.MapGet("/health/live", () => Results.Json(
    new HealthStatusResponse("healthy"),
    ManagementJsonContext.Default.HealthStatusResponse))
    .ExcludeFromDescription();
app.MapGet("/health/ready", () => Results.Json(
    new HealthStatusResponse("ready"),
    ManagementJsonContext.Default.HealthStatusResponse))
    .ExcludeFromDescription();

app.MapFallback("/{**path}", context =>
    MockRequestDispatcher.DispatchAsync(context, configuration, statistics))
    .ExcludeFromDescription();

app.Run();

public partial class Program;

internal static class MockApiHostConfiguration
{
    internal static void AddRateLimiting(IServiceCollection services, MockApiOptions options)
    {
        Func<HttpContext, RateLimitPartition<string>> partitioner = context =>
            RateLimitPartition.GetSlidingWindowLimiter(
                GetRateLimitPartitionKey(context),
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = options.ManagementPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                    QueueLimit = 0,
                    AutoReplenishment = true
                });

        services.AddRateLimiter(rateLimiter =>
        {
            rateLimiter.AddPolicy(ManagementApiEndpoints.RateLimitPolicyName, partitioner);
            rateLimiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ManagementProblemDetails(
                        "https://mockapi.local/problems/management-rate-limit",
                        "Management API rate limit exceeded",
                        StatusCodes.Status429TooManyRequests,
                        "Too many management requests were received. Retry after the indicated interval.",
                        context.HttpContext.Request.Path),
                    ManagementJsonContext.Default.ManagementProblemDetails,
                    contentType: "application/problem+json",
                    cancellationToken);
            };
        });
    }

    internal static MockApiOptions CreateOptions(
        IConfiguration configuration,
        bool isDevelopment,
        string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        var configurationPath = configuration[$"{MockApiOptions.SectionName}:ConfigurationPath"];
        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            configurationPath = isDevelopment
                ? Path.Combine(contentRootPath, "mockapi.json")
                : "/data/mockapi.json";
        }

        return new MockApiOptions
        {
            ConfigurationPath = configurationPath,
            AllowEmptyConfiguration = ReadBoolean(configuration, "AllowEmptyConfiguration"),
            EnableManagementApi = ReadBoolean(configuration, "EnableManagementApi"),
            EnableDashboard = ReadBoolean(configuration, "EnableDashboard"),
            EnableOpenApi = ReadBoolean(configuration, "EnableOpenApi"),
            EnableSwaggerUi = ReadBoolean(configuration, "EnableSwaggerUi"),
            ManagementPermitLimit = ReadPositiveInteger(configuration, "ManagementPermitLimit", 120)
        };
    }

    internal static string GetRateLimitPartitionKey(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    internal static string GetVersion(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? throw new InvalidOperationException(
                "The application informational version is unavailable.");
        return informationalVersion.Split('+', 2)[0];
    }

    internal static void ConfigureKestrel(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AddServerHeader = false;
    }

    private static bool ReadBoolean(IConfiguration configuration, string name)
    {
        var value = configuration[$"{MockApiOptions.SectionName}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return bool.TryParse(value, out var result)
            ? result
            : throw new InvalidOperationException($"MockApi:{name} must be 'true' or 'false'.");
    }

    private static int ReadPositiveInteger(IConfiguration configuration, string name, int defaultValue)
    {
        var value = configuration[$"{MockApiOptions.SectionName}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : throw new InvalidOperationException($"MockApi:{name} must be a positive integer.");
    }
}
