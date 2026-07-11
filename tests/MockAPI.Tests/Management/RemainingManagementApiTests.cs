using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class RemainingManagementApiTests : IDisposable
{
    private const string BasePath = "/__mockapi/api";
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MockAPI.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Validate_ReportsErrorsWithoutApplyingCandidate()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var candidate = CreateDocument(CreateEndpoint("/health", "invalid"));

        using var response = await SendDocumentAsync(client, HttpMethod.Post, $"{BasePath}/configuration/validate", candidate);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.False(json.RootElement.GetProperty("isValid").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("errors").EnumerateArray(), error =>
            error.GetProperty("code").GetString() == "reserved");
        using var status = await client.GetAsync($"{BasePath}/configuration");
        Assert.Equal("\"0\"", status.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Import_AtomicallyAppliesCandidateAndExportRoundTripsIt()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/imported", "imported body");
        var candidate = CreateDocument(endpoint);

        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            candidate,
            "\"0\"");
        using var exported = await client.GetAsync($"{BasePath}/configuration/export");

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal("\"1\"", imported.Headers.ETag!.Tag);
        Assert.Equal("imported body", await client.GetStringAsync("/imported"));
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/json", exported.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition!.DispositionType);
        var roundTrip = JsonSerializer.Deserialize(
            await exported.Content.ReadAsByteArrayAsync(),
            MockApiJsonContext.Default.MockApiConfigurationDocument)!;
        Assert.Equal(endpoint.Id, Assert.Single(roundTrip.Endpoints).Id);
    }

    [Fact]
    public async Task BuiltInConfigurations_AreAvailableAndExampleCanBeImported()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var templateResponse = await client.GetAsync($"{BasePath}/configuration/template");
        using var exampleResponse = await client.GetAsync($"{BasePath}/configuration/example");
        var template = JsonSerializer.Deserialize(
            await templateResponse.Content.ReadAsByteArrayAsync(),
            MockApiJsonContext.Default.MockApiConfigurationDocument)!;
        var example = JsonSerializer.Deserialize(
            await exampleResponse.Content.ReadAsByteArrayAsync(),
            MockApiJsonContext.Default.MockApiConfigurationDocument)!;

        Assert.Equal(HttpStatusCode.OK, templateResponse.StatusCode);
        Assert.Equal("no-store", templateResponse.Headers.CacheControl!.ToString());
        Assert.Empty(template.Endpoints);
        Assert.Equal(HttpStatusCode.OK, exampleResponse.StatusCode);
        Assert.Equal(4, example.Endpoints.Count);
        var exampleEndpoint = Assert.Single(
            example.Endpoints,
            endpoint => endpoint.Path == "/ex/rate-limited");

        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            example,
            "\"0\"");
        using var mocked = await client.GetAsync(exampleEndpoint.Path);
        using var hello = await client.GetAsync("/ex/hello");
        using var created = await client.PostAsync("/ex/orders", content: null);
        using var deleted = await client.DeleteAsync("/ex/orders/42");
        using var activeEndpoints = await client.GetAsync($"{BasePath}/endpoints");
        using var activated = await ReadJsonAsync(activeEndpoints);

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(4, activated.RootElement.GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.Equal("{\"message\":\"Hello from MockAPI\"}", await hello.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/ex/orders/42", created.Headers.Location!.OriginalString);
        Assert.Equal("{\"id\":42,\"status\":\"created\"}", await created.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode)429, mocked.StatusCode);
        Assert.Equal(["30"], mocked.Headers.GetValues("Retry-After"));
        Assert.Equal(["MockAPI", "checked-in-example"], mocked.Headers.GetValues("X-Mock-Source"));
        Assert.Equal("{\"error\":\"try again later\"}", await mocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task BuiltInConfigurations_MergeWithoutRemovingOrDuplicatingEndpoints()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var customEndpoint = CreateEndpoint("/custom", "custom");
        using var customImport = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(customEndpoint),
            "\"0\"");

        using var templateMerge = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/template/merge",
            "\"1\"");
        using var firstExampleMerge = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge",
            "\"1\"");
        using var secondExampleMerge = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge",
            "\"2\"");
        using var activeEndpoints = await client.GetAsync($"{BasePath}/endpoints");
        using var active = await ReadJsonAsync(activeEndpoints);

        Assert.Equal(HttpStatusCode.OK, templateMerge.StatusCode);
        Assert.Equal("\"1\"", templateMerge.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, firstExampleMerge.StatusCode);
        Assert.Equal("\"2\"", firstExampleMerge.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, secondExampleMerge.StatusCode);
        Assert.Equal("\"2\"", secondExampleMerge.Headers.ETag!.Tag);
        Assert.Equal(5, active.RootElement.GetArrayLength());
        Assert.Equal(5, active.RootElement.EnumerateArray().Select(endpoint => endpoint.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.Equal("custom", await client.GetStringAsync(customEndpoint.Path));
    }

    [Fact]
    public async Task BuiltInConfiguration_ConflictRequiresForceAndPreservesUnrelatedEndpoints()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var example = await ReadDocumentAsync(client, $"{BasePath}/configuration/example");
        var builtInHello = Assert.Single(example.Endpoints, endpoint => endpoint.Path == "/ex/hello");
        var changedHello = builtInHello with
        {
            Response = builtInHello.Response with { Body = "changed locally" }
        };
        var custom = CreateEndpoint("/custom", "custom");
        using var initialImport = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(changedHello, custom),
            "\"0\"");

        using var conflict = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge",
            "\"1\"");
        using var conflictJson = await ReadJsonAsync(conflict);
        using var endpointsAfterConflict = await client.GetAsync($"{BasePath}/endpoints");
        using var unchanged = await ReadJsonAsync(endpointsAfterConflict);

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.False(conflictJson.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal("different", Assert.Single(conflictJson.RootElement.GetProperty("conflicts").EnumerateArray()).GetProperty("kind").GetString());
        Assert.Equal("\"1\"", conflict.Headers.ETag!.Tag);
        Assert.Equal(2, unchanged.RootElement.GetArrayLength());
        Assert.Equal("changed locally", await client.GetStringAsync(changedHello.Path));

        using var forced = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge?force=true",
            "\"1\"");
        using var forcedJson = await ReadJsonAsync(forced);
        using var activeEndpoints = await client.GetAsync($"{BasePath}/endpoints");
        using var active = await ReadJsonAsync(activeEndpoints);

        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
        Assert.True(forcedJson.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(3, forcedJson.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, forcedJson.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(5, active.RootElement.GetArrayLength());
        Assert.Equal("{\"message\":\"Hello from MockAPI\"}", await client.GetStringAsync(builtInHello.Path));
        Assert.Equal("custom", await client.GetStringAsync(custom.Path));
    }

    [Fact]
    public async Task Import_InvalidCandidateLeavesPriorRouteActive()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var valid = CreateDocument(CreateEndpoint("/current", "current"));
        using var first = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            valid,
            "\"0\"");
        var invalid = CreateDocument(CreateEndpoint("/health", "invalid"));

        using var response = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            invalid,
            "\"1\"");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.Equal("current", await client.GetStringAsync("/current"));
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Save_PersistsCurrentRevisionAndClearsUnsavedState()
    {
        var path = Path.Combine(_directory, "nested", "mockapi.json");
        await using var factory = CreateFactory(path);
        using var client = factory.CreateClient();
        var candidate = CreateDocument(CreateEndpoint("/saved", "saved"));
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            candidate,
            "\"0\"");

        using var saved = await SendAsync(client, HttpMethod.Post, $"{BasePath}/configuration/save", "\"1\"");

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True(File.Exists(path));
        using var status = await client.GetAsync($"{BasePath}/configuration");
        using var statusJson = await ReadJsonAsync(status);
        Assert.False(statusJson.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
    }

    [Fact]
    public async Task Statistics_QueryAndResetExposeAggregateAndEndpointState()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/counted", "counted");
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoint),
            "\"0\"");
        using var matched = await client.GetAsync("/counted");
        using var unmatched = await client.GetAsync("/missing");

        using var response = await client.GetAsync($"{BasePath}/statistics");
        using var json = await ReadJsonAsync(response);
        Assert.Equal(2, json.RootElement.GetProperty("totalRequests").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("matchedRequests").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("unmatchedRequests").GetInt64());
        Assert.Equal(endpoint.Id, json.RootElement.GetProperty("endpoints")[0].GetProperty("endpointId").GetGuid());

        using var reset = await SendAsync(client, HttpMethod.Post, $"{BasePath}/statistics/reset");
        using var afterReset = await client.GetAsync($"{BasePath}/statistics");
        using var resetJson = await ReadJsonAsync(afterReset);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(0, resetJson.RootElement.GetProperty("totalRequests").GetInt64());
    }

    [Fact]
    public async Task Statistics_EndpointResetDoesNotChangeAggregateCounts()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/endpoint-reset", "body");
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoint),
            "\"0\"");
        using var matched = await client.GetAsync(endpoint.Path);

        using var reset = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/statistics/endpoints/{endpoint.Id}/reset");
        using var response = await client.GetAsync($"{BasePath}/statistics");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(1, json.RootElement.GetProperty("totalRequests").GetInt64());
        Assert.Empty(json.RootElement.GetProperty("endpoints").EnumerateArray());
    }

    [Fact]
    public async Task StatisticsEvents_StreamServerSentStatistics()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BasePath}/statistics/events");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);

        var eventLine = await reader.ReadLineAsync(cancellation.Token);
        var dataLine = await reader.ReadLineAsync(cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("event: statistics", eventLine);
        var data = Assert.IsType<string>(dataLine);
        Assert.StartsWith("data: ", data, StringComparison.Ordinal);
        using var payload = JsonDocument.Parse(data[6..]);
        Assert.Equal(0, payload.RootElement.GetProperty("totalRequests").GetInt64());
    }

    [Fact]
    public async Task HealthAndRootDashboardAreApplicationRoutes()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var dashboard = await client.GetAsync("/");
        using var stylesheet = await client.GetAsync("/app.css");
        using var script = await client.GetAsync("/app.js");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var liveJson = JsonDocument.Parse(await live.Content.ReadAsStringAsync());
        using var readyJson = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.Equal("healthy", liveJson.RootElement.GetProperty("status").GetString());
        Assert.Equal("ready", readyJson.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal("text/html", dashboard.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", dashboard.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
        Assert.Equal("text/css", stylesheet.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", stylesheet.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-store", script.Headers.CacheControl!.ToString());
        var html = await dashboard.Content.ReadAsStringAsync();
        var javascript = await script.Content.ReadAsStringAsync();
        Assert.Contains("MockAPI", html, StringComparison.Ordinal);
        Assert.Contains("Endpoint configuration", html, StringComparison.Ordinal);
        Assert.Contains("id=\"load-template-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"load-example-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-load-template-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-load-example-button\"", html, StringComparison.Ordinal);
        Assert.Contains("Load template", html, StringComparison.Ordinal);
        Assert.Contains("Load examples", html, StringComparison.Ordinal);
        Assert.Contains("id=\"test-blade\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"test-send\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"filter-status\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/simonkurtz-MSFT/MockAPI\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://www.linkedin.com/in/simonkurtz\">Simon Kurtz</a>", html, StringComparison.Ordinal);
        Assert.Contains("Version 1.0.0-alpha.1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{VERSION}}", html, StringComparison.Ordinal);
        Assert.Contains("loadBuiltInConfiguration(\"template\")", javascript, StringComparison.Ordinal);
        Assert.Contains("loadBuiltInConfiguration(\"example\")", javascript, StringComparison.Ordinal);
        Assert.Contains("/merge?force=true", javascript, StringComparison.Ordinal);
        Assert.Contains("openTestBlade", javascript, StringComparison.Ordinal);
    }

    [Fact]
    public void HealthStatusResponse_HasSourceGeneratedJsonMetadata()
    {
        var json = JsonSerializer.SerializeToElement(
            new HealthStatusResponse("ready"),
            ManagementJsonContext.Default.HealthStatusResponse);

        Assert.Equal("ready", json.GetProperty("status").GetString());
    }

    [Fact]
    public void Application_HasExpectedSemanticVersionMetadata()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;

        Assert.StartsWith("1.0.0-alpha.1", informationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_CanBeDisabledIndependently()
    {
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:EnableDashboard", "false"));
        using var client = factory.CreateClient();

        using var dashboard = await client.GetAsync("/");
        using var management = await client.GetAsync($"{BasePath}/configuration");

        Assert.Equal(HttpStatusCode.NotFound, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, management.StatusCode);
    }

    [Fact]
    public async Task OpenApi_DescribesManagementRoutesAndExcludesRuntimeRoutes()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/dynamic-openapi-test", "body");
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoint),
            "\"0\"");

        using var response = await client.GetAsync("/__mockapi/openapi/v1.json");
        using var json = await ReadJsonAsync(response);
        var paths = json.RootElement.GetProperty("paths");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration", out var configurationPath));
        Assert.True(configurationPath.TryGetProperty("get", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/template", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/example", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/template/merge", out var templateMerge));
        Assert.True(templateMerge.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/example/merge", out var exampleMerge));
        Assert.True(exampleMerge.TryGetProperty("post", out _));
        var endpointPath = paths.EnumerateObject().Single(path =>
            path.Name.StartsWith($"{BasePath}/endpoints/{{", StringComparison.Ordinal)
            && !path.Name.EndsWith("/enabled", StringComparison.Ordinal));
        Assert.True(endpointPath.Value.TryGetProperty("delete", out _));
        Assert.False(paths.TryGetProperty(endpoint.Path, out _));
        Assert.False(paths.TryGetProperty("/health/live", out _));
        Assert.False(paths.TryGetProperty("/", out _));
    }

    [Fact]
    public async Task OpenApiAndSwaggerUi_CanBeDisabledIndependently()
    {
        await using var openApiDisabledFactory = CreateFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:EnableOpenApi", "false"));
        using var openApiDisabledClient = openApiDisabledFactory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var missingDocument = await openApiDisabledClient.GetAsync("/__mockapi/openapi/v1.json");
        using var availableUi = await openApiDisabledClient.GetAsync("/__mockapi/swagger/index.html");

        await using var uiDisabledFactory = CreateFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:EnableSwaggerUi", "false"));
        using var uiDisabledClient = uiDisabledFactory.CreateClient();
        using var availableDocument = await uiDisabledClient.GetAsync("/__mockapi/openapi/v1.json");
        using var missingUi = await uiDisabledClient.GetAsync("/__mockapi/swagger/index.html");

        Assert.Equal(HttpStatusCode.NotFound, missingDocument.StatusCode);
        Assert.Equal(HttpStatusCode.OK, availableUi.StatusCode);
        Assert.Equal(HttpStatusCode.OK, availableDocument.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingUi.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private WebApplicationFactory<Program> CreateFactory(string? path = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:ConfigurationPath", path ?? Path.Combine(_directory, "mockapi.json"));
            builder.UseSetting("MockApi:AllowEmptyConfiguration", "true");
        });

    private static Task<HttpResponseMessage> SendDocumentAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        MockApiConfigurationDocument document,
        string? etag = null) =>
        SendAsync(
            client,
            method,
            path,
            etag,
            JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument));

    private static Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string? etag = null,
        string? json = null)
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

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

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

    private static async Task<MockApiConfigurationDocument> ReadDocumentAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize(
            await response.Content.ReadAsByteArrayAsync(),
            MockApiJsonContext.Default.MockApiConfigurationDocument)!;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
}
