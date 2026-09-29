using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;

namespace MockAPI.Tests.Management;

public sealed class DashboardAuthenticationTests
{
    private const string Username = "operator";
    private const string Password = "test-password";

    [Fact]
    public async Task DashboardEvents_AuthenticateBeforeOpeningStream()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        const string path = "/__mockapi/api/dashboard/events";
        using var anonymous = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = CreateAuthenticatedRequest(path, Username, Password);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        cancellation.Cancel();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/brand-mark.svg")]
    [InlineData("/favicon.ico")]
    [InlineData("/favicon.svg")]
    [InlineData("/__mockapi/openapi-logo.svg")]
    [InlineData("/dashboard-core.js")]
    [InlineData("/dashboard-dom.js")]
    [InlineData("/dashboard-editor-dialog.js")]
    [InlineData("/dashboard-api-description.js")]
    [InlineData("/dashboard-endpoint-editor.js")]
    [InlineData("/dashboard-endpoint-table.js")]
    [InlineData("/dashboard-layout.js")]
    [InlineData("/dashboard-management.js")]
    [InlineData("/dashboard-preferences.js")]
    [InlineData("/dashboard-statistics.js")]
    [InlineData("/dashboard-sync.js")]
    [InlineData("/dashboard-test-blade.js")]
    [InlineData("/dashboard-test-request.js")]
    [InlineData("/dashboard-tutorial.js")]
    [InlineData("/__mockapi/api/configuration")]
    [InlineData("/__mockapi/api/configuration/export/openapi?download=false")]
    [InlineData("/__mockapi/openapi/v1.json")]
    [InlineData("/__mockapi/swagger/index.html")]
    public async Task AdministrativeRoutes_RequireValidCredentials(string path)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var anonymous = await client.GetAsync(path);
        using var invalidRequest = CreateAuthenticatedRequest(path, Username, "wrong-password");
        using var invalid = await client.SendAsync(invalidRequest);
        using var validRequest = CreateAuthenticatedRequest(path, Username, Password);
        using var valid = await client.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("Basic", anonymous.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, valid.StatusCode);
    }

    [Fact]
    public async Task HealthAndMockRoutes_RemainPublic()
    {
        await using var factory = CreateFactory();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        state.TryReplace(
            new MockApiConfigurationDocument
            {
                SchemaVersion = "1.0",
                Endpoints =
                [
                    new MockEndpointDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = "Public mock",
                        Enabled = true,
                        Methods = ["GET"],
                        Path = "/public-mock",
                        Response = new MockResponseDefinition
                        {
                            StatusCode = 200,
                            Headers = [],
                            ContentType = "text/plain; charset=utf-8",
                            Body = "public"
                        }
                    }
                ]
            },
            expectedRevision: 0);
        using var client = factory.CreateClient();

        using var health = await client.GetAsync("/health/ready");
        using var mock = await client.GetAsync("/public-mock");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
        Assert.Equal("public", await mock.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, "v1.100000.AAAAAAAAAAAAAAAAAAAAAA==.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("operator", null)]
    [InlineData("operator", "invalid")]
    [InlineData("operator", "v2.100000.AA==.AA==")]
    [InlineData("operator", "v1.invalid.AA==.AA==")]
    [InlineData("operator", "v1.99999.AA==.AA==")]
    [InlineData("operator", "v1.100000.AA==.AA==")]
    [InlineData("operator", "v1.100000.!.!")]
    public void InvalidAuthenticationConfiguration_FailsStartup(string? username, string? passwordHash)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (username is not null)
            {
                builder.UseSetting("MockApi:DashboardUsername", username);
            }
            if (passwordHash is not null)
            {
                builder.UseSetting("MockApi:DashboardPasswordHash", passwordHash);
            }
        });

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("MockApi:Dashboard", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("dXNlcm5hbWVub2NvbG9u")]
    public async Task AdministrativeRoutes_RejectMalformedBasicCredentials(string parameter)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", parameter);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AdministrativeRoutes_RejectWrongUsernameLength()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var request = CreateAuthenticatedRequest("/", "different-operator", Password);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var salt = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Password,
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            32);
        var passwordHash = $"v1.100000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:DashboardUsername", Username);
            builder.UseSetting("MockApi:DashboardPasswordHash", passwordHash);
        });
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        string path,
        string username,
        string password)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        return request;
    }
}
