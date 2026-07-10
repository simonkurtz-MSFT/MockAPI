using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MockAPI.Tests",
        Guid.NewGuid().ToString("N"));

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