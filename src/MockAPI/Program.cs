using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);
var configurationPath = builder.Configuration[$"{MockApiOptions.SectionName}:ConfigurationPath"];
if (string.IsNullOrWhiteSpace(configurationPath))
{
    configurationPath = builder.Environment.IsDevelopment()
        ? Path.Combine(builder.Environment.ContentRootPath, "mockapi.json")
        : "/data/mockapi.json";
}

var allowEmptyValue = builder.Configuration[$"{MockApiOptions.SectionName}:AllowEmptyConfiguration"];
if (!string.IsNullOrWhiteSpace(allowEmptyValue) && !bool.TryParse(allowEmptyValue, out _))
{
    throw new InvalidOperationException("MockApi:AllowEmptyConfiguration must be 'true' or 'false'.");
}

var enableManagementApiValue = builder.Configuration[$"{MockApiOptions.SectionName}:EnableManagementApi"];
if (!string.IsNullOrWhiteSpace(enableManagementApiValue) && !bool.TryParse(enableManagementApiValue, out _))
{
    throw new InvalidOperationException("MockApi:EnableManagementApi must be 'true' or 'false'.");
}

var enableDashboardValue = builder.Configuration[$"{MockApiOptions.SectionName}:EnableDashboard"];
if (!string.IsNullOrWhiteSpace(enableDashboardValue) && !bool.TryParse(enableDashboardValue, out _))
{
    throw new InvalidOperationException("MockApi:EnableDashboard must be 'true' or 'false'.");
}

var enableOpenApiValue = builder.Configuration[$"{MockApiOptions.SectionName}:EnableOpenApi"];
if (!string.IsNullOrWhiteSpace(enableOpenApiValue) && !bool.TryParse(enableOpenApiValue, out _))
{
    throw new InvalidOperationException("MockApi:EnableOpenApi must be 'true' or 'false'.");
}

var enableSwaggerUiValue = builder.Configuration[$"{MockApiOptions.SectionName}:EnableSwaggerUi"];
if (!string.IsNullOrWhiteSpace(enableSwaggerUiValue) && !bool.TryParse(enableSwaggerUiValue, out _))
{
    throw new InvalidOperationException("MockApi:EnableSwaggerUi must be 'true' or 'false'.");
}

var options = new MockApiOptions
{
    ConfigurationPath = configurationPath,
    AllowEmptyConfiguration = string.IsNullOrWhiteSpace(allowEmptyValue) || bool.Parse(allowEmptyValue),
    EnableManagementApi = string.IsNullOrWhiteSpace(enableManagementApiValue) || bool.Parse(enableManagementApiValue),
    EnableDashboard = string.IsNullOrWhiteSpace(enableDashboardValue) || bool.Parse(enableDashboardValue),
    EnableOpenApi = string.IsNullOrWhiteSpace(enableOpenApiValue) || bool.Parse(enableOpenApiValue),
    EnableSwaggerUi = string.IsNullOrWhiteSpace(enableSwaggerUiValue) || bool.Parse(enableSwaggerUiValue)
};

builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ConfigurationState>();
builder.Services.AddSingleton<ConfigurationFileStore>();
builder.Services.AddSingleton<EndpointManagementService>();
builder.Services.AddSingleton<ConfigurationManagementService>();
builder.Services.AddSingleton<RequestStatisticsCollector>();
builder.Services.AddRateLimiter(rateLimiter =>
{
    rateLimiter.AddPolicy(ManagementApiEndpoints.RateLimitPolicyName, context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
                AutoReplenishment = true
            }));
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
    var informationalVersion = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? throw new InvalidOperationException(
            "The application informational version is unavailable.");
    var version = informationalVersion.Split('+', 2)[0];
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
