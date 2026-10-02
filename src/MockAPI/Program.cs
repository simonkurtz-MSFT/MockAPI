using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.RateLimiting;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

var builder = WebApplication.CreateBuilder(args);
var options = MockApiHostConfiguration.CreateOptions(
    builder.Configuration,
    builder.Environment.IsDevelopment(),
    builder.Environment.ContentRootPath);
DashboardBasicAuthentication.ValidateConfiguration(options);

builder.WebHost.ConfigureKestrel(MockApiHostConfiguration.ConfigureKestrel);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ConfigurationState>();
builder.Services.AddSingleton<IConfigurationStore>(_ => MockApiHostConfiguration.CreateConfigurationStore(options));
builder.Services.AddSingleton<EndpointManagementService>();
builder.Services.AddSingleton<ConfigurationManagementService>();
builder.Services.AddSingleton<RequestStatisticsCollector>();
builder.Services.AddSingleton<ApiKeySecurity>();
MockApiHostConfiguration.AddRateLimiting(builder.Services, options);
builder.Services.ConfigureHttpJsonOptions(jsonOptions =>
{
    jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(0, ManagementJsonContext.Default);
    jsonOptions.SerializerOptions.TypeInfoResolverChain.Insert(1, MockApiJsonContext.Default);
});
builder.Services.AddOpenApi();

var app = builder.Build();
app.UseMiddleware<DashboardBasicAuthentication>();
app.UseRateLimiter();
var configuration = app.Services.GetRequiredService<ConfigurationState>();
var configurationStore = app.Services.GetRequiredService<IConfigurationStore>();
var endpointManagement = app.Services.GetRequiredService<EndpointManagementService>();
var configurationManagement = app.Services.GetRequiredService<ConfigurationManagementService>();
var statistics = app.Services.GetRequiredService<RequestStatisticsCollector>();

await configurationStore.LoadAsync(configuration, CancellationToken.None);
var apiSecurity = app.Services.GetRequiredService<ApiKeySecurity>();
await apiSecurity.LoadAsync(CancellationToken.None);

if (options.EnableDashboard)
{
    var version = MockApiHostConfiguration.GetVersion(typeof(Program).Assembly);
    var buildDate = MockApiHostConfiguration.GetBuildDate(typeof(Program).Assembly);
    string[] dashboardAssetNames =
    [
        "app.css",
        "app.js",
        "brand-mark.svg",
        "dashboard-api-description.js",
        "dashboard-api-security.js",
        "dashboard-core.js",
        "dashboard-dom.js",
        "dashboard-editor-dialog.js",
        "dashboard-endpoint-editor.js",
        "dashboard-endpoint-table.js",
        "dashboard-layout.js",
        "dashboard-management.js",
        "dashboard-preferences.js",
        "dashboard-statistics.js",
        "dashboard-sync.js",
        "dashboard-test-blade.js",
        "dashboard-test-request.js",
        "dashboard-tutorial.js",
        "favicon.svg",
        "openapi-logo.svg"
    ];
    var assetVersion = DashboardAssets.CreateVersion(app.Environment.WebRootPath, dashboardAssetNames);
    var logAnalyticsWorkspaceLink = MockApiHostConfiguration.CreateLogAnalyticsWorkspaceLink(
        options.LogAnalyticsWorkspaceUri);
    var dashboardHtml = (await File.ReadAllTextAsync(
        Path.Combine(app.Environment.WebRootPath, "index.html"),
        CancellationToken.None))
        .Replace("{{VERSION}}", version, StringComparison.Ordinal)
        .Replace("{{BUILD_DATE_ISO}}", buildDate.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal)
        .Replace(
            "{{BUILD_DATE_DISPLAY}}",
            buildDate.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            StringComparison.Ordinal)
        .Replace("{{ASSET_VERSION}}", assetVersion, StringComparison.Ordinal)
        .Replace("{{LOG_ANALYTICS_WORKSPACE_LINK}}", logAnalyticsWorkspaceLink, StringComparison.Ordinal);

    DashboardAssets.Map(app, "/app.css", "app.css", "text/css; charset=utf-8", assetVersion);
    foreach (var script in new[] { "app.js", "dashboard-api-description.js", "dashboard-api-security.js", "dashboard-core.js", "dashboard-dom.js", "dashboard-editor-dialog.js",
        "dashboard-endpoint-editor.js", "dashboard-endpoint-table.js", "dashboard-layout.js", "dashboard-management.js",
        "dashboard-preferences.js", "dashboard-statistics.js", "dashboard-sync.js", "dashboard-test-blade.js",
        "dashboard-test-request.js", "dashboard-tutorial.js" })
    {
        DashboardAssets.Map(
            app,
            $"/{script}",
            script,
            "text/javascript; charset=utf-8",
            assetVersion,
            replaceVersionPlaceholder: true);
    }
    DashboardAssets.Map(app, "/favicon.svg", "favicon.svg", "image/svg+xml", assetVersion);
    DashboardAssets.Map(app, "/favicon.ico", "favicon.svg", "image/svg+xml", assetVersion);
    DashboardAssets.Map(app, "/brand-mark.svg", "brand-mark.svg", "image/svg+xml", assetVersion);
    DashboardAssets.Map(
        app,
        "/__mockapi/openapi-logo.svg",
        "openapi-logo.svg",
        "image/svg+xml",
        assetVersion);
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
    ApiSecurityEndpoints.Map(app);
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

app.MapFallback("/{**path}", async context =>
{
    if (!apiSecurity.Authorizes(context))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.WWWAuthenticate = "ApiKey realm=\"MockAPI\"";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await MockRequestDispatcher.DispatchAsync(context, configuration, statistics);
})
    .ExcludeFromDescription();

app.Run();

/// <summary>Provides the public ASP.NET Core entry-point marker used by hosting and integration tooling.</summary>
public partial class Program;

internal static class MockApiHostConfiguration
{
    internal static IConfigurationStore CreateConfigurationStore(MockApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ConfigurationBlobUri is null)
        {
            return new ConfigurationFileStore(options);
        }

        var credentialOptions = new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = options.ManagedIdentityClientId
        };
        return new ConfigurationBlobStore(
            options,
            new BlobClient(options.ConfigurationBlobUri, new DefaultAzureCredential(credentialOptions)));
    }

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
            ConfigurationBlobUri = ReadOptionalHttpsUri(configuration, "ConfigurationBlobUri"),
            ManagedIdentityClientId = configuration[$"{MockApiOptions.SectionName}:ManagedIdentityClientId"],
            LogAnalyticsWorkspaceUri = ReadOptionalHttpsUri(configuration, "LogAnalyticsWorkspaceUri"),
            AllowEmptyConfiguration = ReadBoolean(configuration, "AllowEmptyConfiguration"),
            EnableManagementApi = ReadBoolean(configuration, "EnableManagementApi"),
            EnableDashboard = ReadBoolean(configuration, "EnableDashboard"),
            EnableOpenApi = ReadBoolean(configuration, "EnableOpenApi"),
            EnableSwaggerUi = ReadBoolean(configuration, "EnableSwaggerUi"),
            ManagementPermitLimit = ReadPositiveInteger(configuration, "ManagementPermitLimit", 120),
            RequireApiKey = ReadBoolean(configuration, "RequireApiKey"),
            DashboardUsername = configuration[$"{MockApiOptions.SectionName}:DashboardUsername"],
            DashboardPasswordHash = configuration[$"{MockApiOptions.SectionName}:DashboardPasswordHash"]
        };
    }

    internal static string CreateLogAnalyticsWorkspaceLink(Uri? workspaceUri)
    {
        if (workspaceUri is null)
        {
            return string.Empty;
        }

        var encodedUri = HtmlEncoder.Default.Encode(workspaceUri.AbsoluteUri);
        return $"""
          <span aria-hidden="true">·</span>
          <a href="{encodedUri}" target="_blank" rel="noopener noreferrer">Log Analytics workspace</a>
        """;
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

    internal static DateTimeOffset GetBuildDate(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var value = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "BuildDateUtc")?
            .Value;
        if (!DateTimeOffset.TryParseExact(
            value,
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var buildDate))
        {
            throw new InvalidOperationException("The application UTC build date is unavailable or invalid.");
        }

        return buildDate;
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

    private static Uri? ReadOptionalHttpsUri(IConfiguration configuration, string name)
    {
        var value = configuration[$"{MockApiOptions.SectionName}:{name}"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                ? uri
                : throw new InvalidOperationException($"MockApi:{name} must be an absolute HTTPS URI.");
    }
}
