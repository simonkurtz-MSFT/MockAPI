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
                ["MockApi:AllowEmptyConfiguration"] = "false",
                ["MockApi:EnableManagementApi"] = "false",
                ["MockApi:EnableDashboard"] = "false",
                ["MockApi:EnableOpenApi"] = "false",
                ["MockApi:EnableSwaggerUi"] = "false",
                ["MockApi:ManagementPermitLimit"] = "321"
            })
            .Build();
        var explicitOptions = MockApiHostConfiguration.CreateOptions(configured, true, "C:/app");

        Assert.Equal(Path.Combine("C:/app", "mockapi.json"), development.ConfigurationPath);
        Assert.Equal("/data/mockapi.json", production.ConfigurationPath);
        Assert.True(development.AllowEmptyConfiguration);
        Assert.Equal(120, development.ManagementPermitLimit);
        Assert.Equal("custom.json", explicitOptions.ConfigurationPath);
        Assert.False(explicitOptions.AllowEmptyConfiguration);
        Assert.False(explicitOptions.EnableManagementApi);
        Assert.False(explicitOptions.EnableDashboard);
        Assert.False(explicitOptions.EnableOpenApi);
        Assert.False(explicitOptions.EnableSwaggerUi);
        Assert.Equal(321, explicitOptions.ManagementPermitLimit);
        Assert.Throws<ArgumentNullException>((Action)(() => MockApiHostConfiguration.CreateOptions(null!, true, "C:/app")));
        Assert.Throws<ArgumentException>(() => MockApiHostConfiguration.CreateOptions(empty, true, ""));
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
        Assert.StartsWith("1.0.0-alpha.1", MockApiHostConfiguration.GetVersion(typeof(Program).Assembly), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => MockApiHostConfiguration.GetVersion(dynamicAssembly));
        Assert.Throws<ArgumentNullException>(() => MockApiHostConfiguration.GetVersion(null!));
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
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
