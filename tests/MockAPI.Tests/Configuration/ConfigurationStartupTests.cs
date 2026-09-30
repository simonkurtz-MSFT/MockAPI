using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MockAPI.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ConfigureKestrel_DisablesServerHeader()
    {
        var options = new Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions
        {
            AddServerHeader = true
        };

        MockApiHostConfiguration.ConfigureKestrel(options);

        Assert.False(options.AddServerHeader);
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.ConfigureKestrel(null!));
    }

    [Fact]
    public void CreateOptions_UsesEnvironmentDefaultsAndExplicitOverrides()
    {
        var empty = new ConfigurationBuilder().Build();
        var development = MockApiHostConfiguration.CreateOptions(empty, isDevelopment: true, "C:/app");
        var production = MockApiHostConfiguration.CreateOptions(empty, isDevelopment: false, "C:/app");
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MockApi:ConfigurationPath"] = "custom.json",
                ["MockApi:ConfigurationBlobUri"] = "https://storage.example/container/mockapi.json",
                ["MockApi:ManagedIdentityClientId"] = "00000000-0000-0000-0000-000000000001",
                ["MockApi:LogAnalyticsWorkspaceUri"] = "https://portal.azure.com/#resource/example",
                ["MockApi:AllowEmptyConfiguration"] = "false",
                ["MockApi:EnableManagementApi"] = "false",
                ["MockApi:EnableDashboard"] = "false",
                ["MockApi:EnableOpenApi"] = "false",
                ["MockApi:EnableSwaggerUi"] = "false",
                ["MockApi:ManagementPermitLimit"] = "321",
                ["MockApi:DashboardUsername"] = "operator",
                ["MockApi:DashboardPasswordHash"] = "hash"
            })
            .Build();
        var explicitOptions = MockApiHostConfiguration.CreateOptions(configured, true, "C:/app");

        Assert.Equal(Path.Combine("C:/app", "mockapi.json"), development.ConfigurationPath);
        Assert.Equal("/data/mockapi.json", production.ConfigurationPath);
        Assert.True(development.AllowEmptyConfiguration);
        Assert.Equal(120, development.ManagementPermitLimit);
        Assert.Equal("custom.json", explicitOptions.ConfigurationPath);
        Assert.Equal(
            new Uri("https://storage.example/container/mockapi.json"),
            explicitOptions.ConfigurationBlobUri);
        Assert.Equal("00000000-0000-0000-0000-000000000001", explicitOptions.ManagedIdentityClientId);
        Assert.Equal(
            new Uri("https://portal.azure.com/#resource/example"),
            explicitOptions.LogAnalyticsWorkspaceUri);
        Assert.False(explicitOptions.AllowEmptyConfiguration);
        Assert.False(explicitOptions.EnableManagementApi);
        Assert.False(explicitOptions.EnableDashboard);
        Assert.False(explicitOptions.EnableOpenApi);
        Assert.False(explicitOptions.EnableSwaggerUi);
        Assert.Equal(321, explicitOptions.ManagementPermitLimit);
        Assert.Equal("operator", explicitOptions.DashboardUsername);
        Assert.Equal("hash", explicitOptions.DashboardPasswordHash);
        Assert.Throws<ArgumentNullException>((Action)(() => MockApiHostConfiguration.CreateOptions(null!, true, "C:/app")));
        Assert.Throws<ArgumentException>(() => MockApiHostConfiguration.CreateOptions(empty, true, ""));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("http://storage.example/container/mockapi.json")]
    public void CreateOptions_RejectsInvalidHttpsUri(string value)
    {
        foreach (var settingName in new[] { "ConfigurationBlobUri", "LogAnalyticsWorkspaceUri" })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"MockApi:{settingName}"] = value
                })
                .Build();

            var exception = Assert.Throws<InvalidOperationException>(
                () => MockApiHostConfiguration.CreateOptions(configuration, true, "C:/app"));

            Assert.Equal(
                $"MockApi:{settingName} must be an absolute HTTPS URI.",
                exception.Message);
        }
    }

    [Fact]
    public void CreateLogAnalyticsWorkspaceLink_RendersOnlyConfiguredEncodedUri()
    {
        Assert.Empty(MockApiHostConfiguration.CreateLogAnalyticsWorkspaceLink(null));

        var link = MockApiHostConfiguration.CreateLogAnalyticsWorkspaceLink(
            new Uri("https://portal.azure.com/#resource/example?view=logs&source=dashboard"));

        Assert.Contains("Log Analytics workspace", link, StringComparison.Ordinal);
        Assert.Contains(
            "href=\"https://portal.azure.com/#resource/example?view=logs&amp;source=dashboard\"",
            link,
            StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", link, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateConfigurationStore_SelectsConfiguredBackend()
    {
        var fileOptions = new MockApiOptions { ConfigurationPath = "mockapi.json" };
        var blobOptions = new MockApiOptions
        {
            ConfigurationPath = "mockapi.json",
            ConfigurationBlobUri = new Uri("https://storage.example/container/mockapi.json"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000001"
        };

        Assert.IsType<ConfigurationFileStore>(MockApiHostConfiguration.CreateConfigurationStore(fileOptions));
        Assert.IsType<ConfigurationBlobStore>(MockApiHostConfiguration.CreateConfigurationStore(blobOptions));
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.CreateConfigurationStore(null!));
    }

    [Fact]
    public void HostMetadataHelpers_HandleKnownUnknownAndMissingValues()
    {
        var unknown = new DefaultHttpContext();
        var known = new DefaultHttpContext();
        known.Connection.RemoteIpAddress = new IPAddress([127, 0, 0, 1]);
        var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("NoInformationalVersion"),
            AssemblyBuilderAccess.Run);

        Assert.Equal("unknown", MockApiHostConfiguration.GetRateLimitPartitionKey(unknown));
        Assert.Equal("127.0.0.1", MockApiHostConfiguration.GetRateLimitPartitionKey(known));
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.GetRateLimitPartitionKey(null!));
        Assert.Equal("1.0.1", MockApiHostConfiguration.GetVersion(typeof(Program).Assembly));
        Assert.Equal(TimeSpan.Zero, MockApiHostConfiguration.GetBuildDate(typeof(Program).Assembly).Offset);
        Assert.Throws<InvalidOperationException>(() => MockApiHostConfiguration.GetVersion(dynamicAssembly));
        Assert.Throws<InvalidOperationException>(() => MockApiHostConfiguration.GetBuildDate(dynamicAssembly));
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.GetVersion(null!));
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.GetBuildDate(null!));
    }

    [Fact]
    public async Task Startup_ConfiguredFileIsActiveBeforeFirstRequest()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mockapi.json");
        await File.WriteAllTextAsync(path, Serialize(CreateDocument()));
        await using var factory = CreateFactory(path, allowEmptyConfiguration: false);

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/loaded-at-startup");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("loaded", await response.Content.ReadAsStringAsync());
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        Assert.Equal(1, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
    }

    [Fact]
    public void Startup_MissingRequiredFileFailsBeforeServingRequests()
    {
        var path = Path.Combine(_directory, "missing.json");
        using var factory = CreateFactory(path, allowEmptyConfiguration: false);

        var exception = Assert.Throws<ConfigurationPersistenceException>(() => factory.CreateClient());

        Assert.Equal(ConfigurationPersistenceError.FileNotFound, exception.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("not-a-number")]
    public void Startup_InvalidManagementPermitLimitFailsBeforeServingRequests(string value)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:ManagementPermitLimit", value));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Equal("MockApi:ManagementPermitLimit must be a positive integer.", exception.Message);
    }

    [Theory]
    [InlineData("AllowEmptyConfiguration")]
    [InlineData("EnableManagementApi")]
    [InlineData("EnableDashboard")]
    [InlineData("EnableOpenApi")]
    [InlineData("EnableSwaggerUi")]
    public void Startup_InvalidBooleanOptionFailsBeforeServingRequests(string option)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting($"MockApi:{option}", "invalid"));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Equal($"MockApi:{option} must be 'true' or 'false'.", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string path,
        bool allowEmptyConfiguration) =>
        new UnsecuredApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:ConfigurationPath", path);
            builder.UseSetting(
                "MockApi:AllowEmptyConfiguration",
                allowEmptyConfiguration.ToString());
        });

    private static MockApiConfigurationDocument CreateDocument() => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints =
        [
            new MockEndpointDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Loaded at startup",
                Enabled = true,
                Methods = ["GET"],
                Path = "/loaded-at-startup",
                Response = new MockResponseDefinition
                {
                    StatusCode = 202,
                    Headers = [],
                    ContentType = "text/plain; charset=utf-8",
                    Body = "loaded"
                }
            }
        ]
    };

    private static string Serialize(MockApiConfigurationDocument document) =>
        JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
}
