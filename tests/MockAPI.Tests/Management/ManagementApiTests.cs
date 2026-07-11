using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;

namespace MockAPI.Tests.Management;

public sealed class ManagementApiTests
{
    private const string EndpointsPath = "/__mockapi/api/endpoints";

    [Fact]
    public async Task ManagementRateLimit_ReturnsProblemWithoutAffectingHealth()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        HttpResponseMessage? limited = null;

        for (var request = 0; request < 121; request++)
        {
            limited?.Dispose();
            limited = await client.GetAsync("/__mockapi/api/configuration");
        }

        using (limited)
        {
            await AssertProblemAsync(limited!, 429, "management-rate-limit");
            Assert.Equal(TimeSpan.FromSeconds(60), limited!.Headers.RetryAfter!.Delta);
        }
        using var health = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task Queries_ReturnCurrentStateAndQuotedETag()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var status = await client.GetAsync("/__mockapi/api/configuration");
        using var endpoints = await client.GetAsync(EndpointsPath);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("\"0\"", status.Headers.ETag!.Tag);
        using var statusJson = await ReadJsonAsync(status);
        Assert.Equal(0, statusJson.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal("\"0\"", statusJson.RootElement.GetProperty("etag").GetString());
        Assert.False(statusJson.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, endpoints.StatusCode);
        Assert.Equal("\"0\"", endpoints.Headers.ETag!.Tag);
        using var endpointsJson = await ReadJsonAsync(endpoints);
        Assert.Equal(JsonValueKind.Array, endpointsJson.RootElement.ValueKind);
        Assert.Empty(endpointsJson.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Create_ImmediatelyPublishesMockRoute()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/created", "created body");

        using var response = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        Assert.Equal($"{EndpointsPath}/{endpoint.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal("created body", await client.GetStringAsync("/created"));
        using var status = await client.GetAsync("/__mockapi/api/configuration");
        using var statusJson = await ReadJsonAsync(status);
        Assert.Equal(1, statusJson.RootElement.GetProperty("revision").GetInt64());
        Assert.True(statusJson.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
    }

    [Theory]
    [InlineData(null, 428, "precondition-required")]
    [InlineData("0", 400, "invalid-etag")]
    [InlineData("W/\"0\"", 400, "invalid-etag")]
    [InlineData("\"not-a-number\"", 400, "invalid-etag")]
    public async Task Write_InvalidOrMissingIfMatch_ReturnsProblem(
        string? etag,
        int expectedStatus,
        string expectedTypeSuffix)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/precondition", "body");

        using var response = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, etag);

        await AssertProblemAsync(response, expectedStatus, expectedTypeSuffix);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/precondition")).StatusCode);
    }

    [Fact]
    public async Task Write_StaleRevisionReturnsPreconditionFailedWithoutMutation()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var first = CreateEndpoint("/first", "first");
        var stale = CreateEndpoint("/stale", "stale");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, first, "\"0\"");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var response = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, stale, "\"0\"");

        await AssertProblemAsync(response, 412, "revision-conflict");
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/stale")).StatusCode);
        Assert.Equal("first", await client.GetStringAsync("/first"));
    }

    [Fact]
    public async Task Create_InvalidEndpointReturnsValidationErrorsWithoutMutation()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/__mockapi/shadow", "body");

        using var response = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");

        await AssertProblemAsync(response, 422, "validation-failed");
        using var problem = await ReadJsonAsync(response);
        var errors = problem.RootElement.GetProperty("errors");
        Assert.Contains(errors.EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "reserved");
        using var list = await client.GetAsync(EndpointsPath);
        Assert.Equal("\"0\"", list.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Create_DuplicateIdReturnsConflict()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/first", "first");
        using var first = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var duplicate = endpoint with { Path = "/duplicate", Name = "duplicate" };

        using var response = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, duplicate, "\"1\"");

        await AssertProblemAsync(response, 409, "endpoint-already-exists");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/duplicate")).StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsEndpointAndUnknownIdReturnsProblem()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/get-one", "body");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");

        using var response = await client.GetAsync($"{EndpointsPath}/{endpoint.Id}");
        using var missing = await client.GetAsync($"{EndpointsPath}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(endpoint.Id, json.RootElement.GetProperty("id").GetGuid());
        await AssertProblemAsync(missing, 404, "endpoint-not-found");
    }

    [Fact]
    public async Task Replace_MovesLiveRouteAndPreservesId()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/before", "before");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");
        var replacement = endpoint with
        {
            Name = "after",
            Path = "/after",
            Response = endpoint.Response with { Body = "after" }
        };

        using var response = await SendEndpointAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}",
            replacement,
            "\"1\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/before")).StatusCode);
        Assert.Equal("after", await client.GetStringAsync("/after"));
    }

    [Fact]
    public async Task Replace_MismatchedIdReturnsProblemWithoutMutation()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/unchanged", "unchanged");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");
        var replacement = endpoint with { Id = Guid.NewGuid(), Path = "/changed" };

        using var response = await SendEndpointAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}",
            replacement,
            "\"1\"");

        await AssertProblemAsync(response, 400, "endpoint-id-mismatch");
        Assert.Equal("unchanged", await client.GetStringAsync("/unchanged"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/changed")).StatusCode);
    }

    [Fact]
    public async Task Replace_StaleRevisionTakesPrecedenceOverIdMismatch()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/current", "current");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");
        var mismatched = endpoint with { Id = Guid.NewGuid(), Path = "/mismatched" };

        using var response = await SendEndpointAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}",
            mismatched,
            "\"0\"");

        await AssertProblemAsync(response, 412, "revision-conflict");
        Assert.Equal("current", await client.GetStringAsync("/current"));
    }

    [Fact]
    public async Task Enablement_ImmediatelyDisablesAndReEnablesRoute()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/toggle", "enabled");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");

        using var disabled = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}/enabled",
            "{\"enabled\":false}",
            "\"1\"");
        using var disabledRoute = await client.GetAsync("/toggle");
        using var enabled = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}/enabled",
            "{\"enabled\":true}",
            "\"2\"");

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal("\"2\"", disabled.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotFound, disabledRoute.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Equal("\"3\"", enabled.Headers.ETag!.Tag);
        Assert.Equal("enabled", await client.GetStringAsync("/toggle"));
    }

    [Fact]
    public async Task Delete_RemovesEndpointAndUnknownIdReturnsProblem()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/delete", "delete");
        using var created = await SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, endpoint, "\"0\"");

        using var deleted = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{EndpointsPath}/{endpoint.Id}",
            json: null,
            etag: "\"1\"");
        using var route = await client.GetAsync("/delete");
        using var missing = await SendJsonAsync(
            client,
            HttpMethod.Delete,
            $"{EndpointsPath}/{Guid.NewGuid()}",
            json: null,
            etag: "\"2\"");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("\"2\"", deleted.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotFound, route.StatusCode);
        await AssertProblemAsync(missing, 404, "endpoint-not-found");
    }

    [Fact]
    public async Task Write_MalformedOrUnknownJsonReturnsProblem()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var malformed = await SendJsonAsync(
            client,
            HttpMethod.Post,
            EndpointsPath,
            "{not json",
            "\"0\"");
        using var unknown = await SendJsonAsync(
            client,
            HttpMethod.Post,
            EndpointsPath,
            "{\"unexpected\":true}",
            "\"0\"");

        await AssertProblemAsync(malformed, 400, "invalid-json");
        await AssertProblemAsync(unknown, 400, "invalid-json");
    }

    [Fact]
    public async Task Write_OversizedBodyIsRejectedBeforeParsing()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var oversized = new string('x', ConfigurationLimits.MaximumDocumentBytes + 1);

        using var response = await SendJsonAsync(
            client,
            HttpMethod.Post,
            EndpointsPath,
            oversized,
            "\"0\"");

        await AssertProblemAsync(response, 413, "request-too-large");
    }

    [Fact]
    public async Task Write_NonJsonMediaTypeReturnsProblem()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, EndpointsPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");

        using var response = await client.SendAsync(request);

        await AssertProblemAsync(response, 415, "unsupported-media-type");
    }

    [Fact]
    public async Task ReplaceAndEnable_UnknownEndpointReturnNotFoundWithoutRevisionChange()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/missing", "missing");

        using var replaced = await SendEndpointAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}",
            endpoint,
            "\"0\"");
        using var enabled = await SendJsonAsync(
            client,
            HttpMethod.Put,
            $"{EndpointsPath}/{endpoint.Id}/enabled",
            "{\"enabled\":false}",
            "\"0\"");

        await AssertProblemAsync(replaced, 404, "endpoint-not-found");
        await AssertProblemAsync(enabled, 404, "endpoint-not-found");
        Assert.Equal("\"0\"", enabled.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task ManagementApi_CanBeDisabledWithoutDisablingMockRoutes()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:EnableManagementApi", "false"));
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint("/still-mocked", "available");
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(
                new MockApiConfigurationDocument
                {
                    SchemaVersion = "1.0",
                    Endpoints = [endpoint]
                },
                expectedRevision: 0).Status);
        using var client = factory.CreateClient();

        using var management = await client.GetAsync(EndpointsPath);
        using var mock = await client.GetAsync("/still-mocked");

        Assert.Equal(HttpStatusCode.NotFound, management.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mock.StatusCode);
        Assert.Equal("available", await mock.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConcurrentCreatesWithSameRevision_HaveOneWinner()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var first = CreateEndpoint("/race-first", "first");
        var second = CreateEndpoint("/race-second", "second");

        var responses = await Task.WhenAll(
            SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, first, "\"0\""),
            SendEndpointAsync(client, HttpMethod.Post, EndpointsPath, second, "\"0\""));
        using var firstResponse = responses[0];
        using var secondResponse = responses[1];

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        using var list = await client.GetAsync(EndpointsPath);
        using var json = await ReadJsonAsync(list);
        Assert.Single(json.RootElement.EnumerateArray());
    }

    private static MockEndpointDefinition CreateEndpoint(string path, string body) => new()
    {
        Id = Guid.NewGuid(),
        Name = path.Trim('/'),
        Enabled = true,
        Methods = ["GET"],
        Path = path,
        Response = new MockResponseDefinition
        {
            StatusCode = 200,
            Headers = [],
            ContentType = "text/plain; charset=utf-8",
            Body = body
        }
    };

    private static Task<HttpResponseMessage> SendEndpointAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        MockEndpointDefinition endpoint,
        string? etag) =>
        SendJsonAsync(
            client,
            method,
            path,
            JsonSerializer.Serialize(endpoint, MockApiJsonContext.Default.MockEndpointDefinition),
            etag);

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? json,
        string? etag)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        return client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        int expectedStatus,
        string expectedTypeSuffix)
    {
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var json = await ReadJsonAsync(response);
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("status").GetInt32());
        Assert.EndsWith(expectedTypeSuffix, json.RootElement.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("title").GetString()));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
}
