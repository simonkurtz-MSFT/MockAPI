using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

    [Theory]
    [InlineData("postman", "mockapi.postman_collection.json", "application/json")]
    [InlineData("insomnia", "mockapi.insomnia.json", "application/json")]
    [InlineData("openapi", "mockapi.openapi.json", "application/json")]
    [InlineData("curl", "mockapi.sh", "text/x-shellscript")]
    [InlineData("jmeter", "mockapi.jmx", "application/xml")]
    [InlineData("k6", "mockapi.k6.js", "text/javascript")]
    [InlineData("http", "mockapi.http", "text/plain")]
    public async Task PortableExport_ReturnsValidatedArtifact(string format, string fileName, string mediaType)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{BasePath}/configuration/export/{format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal(fileName, response.Content.Headers.ContentDisposition!.FileName);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("?download=false", "inline")]
    [InlineData("?download=true", "attachment")]
    public async Task OpenApiExport_RespectsDownloadPreference(string query, string disposition)
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{BasePath}/configuration/export/openapi{query}");
        using var json = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(disposition, response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("mockapi.openapi.json", response.Content.Headers.ContentDisposition.FileName);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.NotNull(response.Headers.ETag);
        Assert.Equal("3.1.0", json.RootElement.GetProperty("openapi").GetString());
    }

    [Fact]
    public async Task PortableExports_ExpandMethodsOmitDisabledEndpointsAndPreserveAssertions()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var enabled = CreateEndpoint("/portable", "expected body") with
        {
            Description = "Portable endpoint description",
            Methods = ["GET"],
            Response = CreateEndpoint("/unused", "expected body").Response with { StatusCode = 201 }
        };
        var enabledPost = enabled with { Id = Guid.NewGuid(), Methods = ["POST"] };
        var disabled = CreateEndpoint("/disabled", "not exported") with { Enabled = false };
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(enabled, enabledPost, disabled),
            "\"0\"");

        using var postmanResponse = await client.GetAsync($"{BasePath}/configuration/export/postman");
        using var postman = await ReadJsonAsync(postmanResponse);
        var items = postman.RootElement.GetProperty("item");
        Assert.Equal("https://schema.getpostman.com/json/collection/v2.1.0/collection.json", postman.RootElement.GetProperty("info").GetProperty("schema").GetString());
        Assert.Equal(2, items.GetArrayLength());
        Assert.All(items.EnumerateArray(), item =>
        {
            Assert.Equal("Portable endpoint description", item.GetProperty("description").GetString());
            Assert.Contains("/portable", item.GetProperty("request").GetProperty("url").GetProperty("raw").GetString());
            Assert.Contains("201", item.GetProperty("event")[0].GetProperty("script").GetProperty("exec")[0].GetString());
        });

        using var insomniaResponse = await client.GetAsync($"{BasePath}/configuration/export/insomnia");
        using var insomnia = await ReadJsonAsync(insomniaResponse);
        var insomniaRequests = insomnia.RootElement.GetProperty("resources").EnumerateArray()
            .Where(resource => resource.GetProperty("_type").GetString() == "request")
            .ToArray();
        Assert.Equal(2, insomniaRequests.Length);
        Assert.All(insomniaRequests, request =>
        {
            Assert.Equal("Portable endpoint description", request.GetProperty("description").GetString());
            Assert.Contains("/portable", request.GetProperty("url").GetString());
        });

        using var openApiResponse = await client.GetAsync($"{BasePath}/configuration/export/openapi");
        using var openApi = await ReadJsonAsync(openApiResponse);
        Assert.Equal("3.1.0", openApi.RootElement.GetProperty("openapi").GetString());
        var operationPath = openApi.RootElement.GetProperty("paths").GetProperty("/portable");
        Assert.True(operationPath.TryGetProperty("get", out _));
        Assert.True(operationPath.TryGetProperty("post", out _));
        Assert.Equal("Portable endpoint description", operationPath.GetProperty("get").GetProperty("description").GetString());
        Assert.False(openApi.RootElement.GetProperty("paths").TryGetProperty("/disabled", out _));

        var jmeter = XDocument.Parse(await client.GetStringAsync($"{BasePath}/configuration/export/jmeter"));
        Assert.Equal(2, jmeter.Descendants("HTTPSamplerProxy").Count());
        Assert.Equal(2, jmeter.Descendants("ResponseAssertion").Count());

        var k6 = await client.GetStringAsync($"{BasePath}/configuration/export/k6");
        Assert.Contains("expected.status === result.status && expected.body === result.body", k6, StringComparison.Ordinal);
        Assert.DoesNotContain("/disabled", k6, StringComparison.Ordinal);

        var curl = await client.GetStringAsync($"{BasePath}/configuration/export/curl");
        Assert.Contains("--request GET --url \"$BASE_URL/portable\"", curl, StringComparison.Ordinal);
        Assert.Contains("--request POST --url \"$BASE_URL/portable\"", curl, StringComparison.Ordinal);
        Assert.DoesNotContain("/disabled", curl, StringComparison.Ordinal);

        var http = await client.GetStringAsync($"{BasePath}/configuration/export/http");
        Assert.Contains("GET {{baseUrl}}/portable", http, StringComparison.Ordinal);
        Assert.Contains("POST {{baseUrl}}/portable", http, StringComparison.Ordinal);
        Assert.DoesNotContain("/disabled", http, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuiltInExample_ProvidesDashboardRequestCountHint()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var example = await ReadDocumentAsync(client, $"{BasePath}/configuration/example");

        var rateLimited = Assert.Single(example.Endpoints, endpoint => endpoint.Path == "/ex/rate-limited");
        Assert.Equal(5, rateLimited.RequestCount);
        Assert.All(
            example.Endpoints.Where(endpoint => endpoint.Path != "/ex/rate-limited"),
            endpoint => Assert.Null(endpoint.RequestCount));

        using var mergeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge");
        mergeRequest.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var mergeResponse = await client.SendAsync(mergeRequest);
        mergeResponse.EnsureSuccessStatusCode();

        var activeEndpoints = await client.GetFromJsonAsync(
            $"{BasePath}/endpoints",
            ManagementJsonContext.Default.MockEndpointDefinitionArray);
        Assert.NotNull(activeEndpoints);
        Assert.Equal(
            5,
            Assert.Single(activeEndpoints, endpoint => endpoint.Path == "/ex/rate-limited").RequestCount);
    }

    [Fact]
    public async Task PortableExports_RepresentAbortedConnectionsWithoutFabricatingAResponse()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var example = await ReadDocumentAsync(client, $"{BasePath}/configuration/example");
        var abortEndpoint = Assert.Single(
            example.Endpoints,
            endpoint => endpoint.Response.Behavior == MockResponseBehavior.AbortConnection);
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(abortEndpoint),
            "\"0\"");

        using var postmanResponse = await client.GetAsync($"{BasePath}/configuration/export/postman");
        using var postman = await ReadJsonAsync(postmanResponse);
        var postmanItem = Assert.Single(postman.RootElement.GetProperty("item").EnumerateArray());
        Assert.False(postmanItem.TryGetProperty("event", out _));

        using var openApiResponse = await client.GetAsync($"{BasePath}/configuration/export/openapi");
        using var openApi = await ReadJsonAsync(openApiResponse);
        var defaultResponse = openApi.RootElement
            .GetProperty("paths")
            .GetProperty(abortEndpoint.Path)
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("default");
        Assert.Equal("abortConnection", defaultResponse.GetProperty("x-mockapi-behavior").GetString());

        var jmeter = XDocument.Parse(await client.GetStringAsync($"{BasePath}/configuration/export/jmeter"));
        Assert.Single(jmeter.Descendants("HTTPSamplerProxy"));
        Assert.Empty(jmeter.Descendants("ResponseAssertion"));

        var k6 = await client.GetStringAsync($"{BasePath}/configuration/export/k6");
        Assert.Contains("\"behavior\":\"abortConnection\"", k6, StringComparison.Ordinal);
        Assert.Contains("result.status === 0", k6, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenApiExport_RepresentsEmptyResponseWithoutContentType()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/empty-response", string.Empty) with
        {
            Response = CreateEndpoint("/unused", string.Empty).Response with { ContentType = null }
        };
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoint),
            "\"0\"");

        using var response = await client.GetAsync($"{BasePath}/configuration/export/openapi");
        using var openApi = await ReadJsonAsync(response);
        var exportedResponse = openApi.RootElement
            .GetProperty("paths")
            .GetProperty(endpoint.Path)
            .GetProperty("get")
            .GetProperty("responses")
            .GetProperty("200");

        Assert.Equal(JsonValueKind.Null, exportedResponse.GetProperty("content").ValueKind);
    }

    [Fact]
    public async Task PortableExport_RejectsUnknownFormatWithProblemDetails()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{BasePath}/configuration/export/unknown");
        using var problem = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("https://mockapi.local/problems/export-format-not-found", problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task BuiltInExample_IsAvailableAndCanBeImported()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var exampleResponse = await client.GetAsync($"{BasePath}/configuration/example");
        var example = JsonSerializer.Deserialize(
            await exampleResponse.Content.ReadAsByteArrayAsync(),
            MockApiJsonContext.Default.MockApiConfigurationDocument)!;

        Assert.Equal(HttpStatusCode.OK, exampleResponse.StatusCode);
        Assert.Equal(7, example.Endpoints.Count);
        var exampleEndpoint = Assert.Single(
            example.Endpoints,
            endpoint => endpoint.Path == "/ex/rate-limited");
        var abortEndpoint = Assert.Single(
            example.Endpoints,
            endpoint => endpoint.Path == "/ex/abort-connection");

        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            example,
            "\"0\"");
        using var firstAllowed = await client.GetAsync(exampleEndpoint.Path);
        using var secondAllowed = await client.GetAsync(exampleEndpoint.Path);
        using var thirdAllowed = await client.GetAsync(exampleEndpoint.Path);
        using var fourthAllowed = await client.GetAsync(exampleEndpoint.Path);
        using var mocked = await client.GetAsync(exampleEndpoint.Path);
        using var hello = await client.GetAsync("/ex/hello");
        using var created = await client.PostAsync("/ex/orders", content: null);
        using var deleted = await client.DeleteAsync("/ex/orders/42");
        using var redirect = await client.GetAsync("/ex/redirect");
        using var serverError = await client.GetAsync("/ex/server-error");
        using var activeEndpoints = await client.GetAsync($"{BasePath}/endpoints");
        using var activated = await ReadJsonAsync(activeEndpoints);

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(7, activated.RootElement.GetArrayLength());
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetAsync(abortEndpoint.Path));
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
        Assert.Equal("{\"message\":\"Hello from MockAPI\"}", await hello.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/ex/orders/42", created.Headers.Location!.OriginalString);
        Assert.Equal("{\"id\":42,\"status\":\"created\"}", await created.Content.ReadAsStringAsync());
        Assert.All([firstAllowed, secondAllowed, thirdAllowed, fourthAllowed], response =>
            Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal("{\"status\":\"accepted\"}", await firstAllowed.Content.ReadAsStringAsync());
        Assert.Equal((HttpStatusCode)429, mocked.StatusCode);
        Assert.Equal(["10"], mocked.Headers.GetValues("Retry-After"));
        Assert.Equal(["MockAPI", "checked-in-example"], mocked.Headers.GetValues("X-Mock-Source"));
        Assert.Equal("{\"error\":\"try again later\"}", await mocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("/ex/hello", redirect.Headers.Location!.OriginalString);
        Assert.Equal(["MockAPI"], redirect.Headers.GetValues("X-Mock-Source"));
        Assert.Empty(await redirect.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, serverError.StatusCode);
        Assert.Equal(["MockAPI"], serverError.Headers.GetValues("X-Mock-Source"));
        Assert.Equal("application/json; charset=utf-8", serverError.Content.Headers.ContentType!.ToString());
        Assert.Equal("{\"error\":\"internal server error\"}", await serverError.Content.ReadAsStringAsync());
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

        Assert.Equal(HttpStatusCode.OK, firstExampleMerge.StatusCode);
        Assert.Equal("\"2\"", firstExampleMerge.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.OK, secondExampleMerge.StatusCode);
        Assert.Equal("\"2\"", secondExampleMerge.Headers.ETag!.Tag);
        Assert.Equal(8, active.RootElement.GetArrayLength());
        Assert.Equal(8, active.RootElement.EnumerateArray().Select(endpoint => endpoint.GetProperty("id").GetGuid()).Distinct().Count());
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
        Assert.Equal(6, forcedJson.RootElement.GetProperty("added").GetInt32());
        Assert.Equal(1, forcedJson.RootElement.GetProperty("updated").GetInt32());
        Assert.Equal(8, active.RootElement.GetArrayLength());
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
    public async Task Save_StaleRevisionAndPersistenceFailureReturnProblems()
    {
        var blocker = Path.Combine(_directory, "not-a-directory");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(blocker, "blocker");
        await using var factory = CreateFactory(Path.Combine(blocker, "mockapi.json"));
        using var client = factory.CreateClient();
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(CreateEndpoint("/dirty", "body")),
            "\"0\"");

        using var stale = await SendAsync(client, HttpMethod.Post, $"{BasePath}/configuration/save", "\"0\"");
        using var failed = await SendAsync(client, HttpMethod.Post, $"{BasePath}/configuration/save", "\"1\"");

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var staleProblem = await ReadJsonAsync(stale);
        Assert.EndsWith("revision-conflict", staleProblem.RootElement.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var failedProblem = await ReadJsonAsync(failed);
        Assert.EndsWith("persistence-failed", failedProblem.RootElement.GetProperty("type").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationOperations_MalformedJsonAndStaleMergeReturnProblems()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var malformedValidation = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/validate",
            json: "{not json");
        using var malformedImport = await SendAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            "\"0\"",
            "{not json");
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(CreateEndpoint("/revision", "body")),
            "\"0\"");
        using var staleMerge = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge",
            "\"0\"");

        Assert.Equal(HttpStatusCode.BadRequest, malformedValidation.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformedImport.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleMerge.StatusCode);
    }

    [Fact]
    public async Task ConfigurationOperations_EnforceBodyAndJsonMediaContracts()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var empty = await client.PostAsync(
            $"{BasePath}/configuration/validate",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        using var noMediaTypeRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BasePath}/configuration/validate")
        {
            Content = new ByteArrayContent("{}"u8.ToArray())
        };
        using var noMediaType = await client.SendAsync(noMediaTypeRequest);
        using var suffixRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BasePath}/configuration/validate")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(CreateDocument(), MockApiJsonContext.Default.MockApiConfigurationDocument),
                Encoding.UTF8,
                "application/problem+json")
        };
        using var suffix = await client.SendAsync(suffixRequest);
        using var chunkedRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BasePath}/configuration/validate")
        {
            Content = new UnknownLengthJsonContent(new byte[ConfigurationLimits.MaximumDocumentBytes + 1])
        };
        using var chunked = await client.SendAsync(chunkedRequest);

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, noMediaType.StatusCode);
        Assert.Equal(HttpStatusCode.OK, suffix.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
    }

    [Fact]
    public async Task ConfigurationAndEndpointWrites_RequirePreconditionsBeforeReadingBodies()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpointId = Guid.NewGuid();
        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/configuration/example/merge"),
            new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/configuration/import"),
            new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/configuration/save"),
            new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/endpoints/{endpointId}"),
            new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/endpoints/{endpointId}/enabled"),
            new HttpRequestMessage(HttpMethod.Delete, $"{BasePath}/endpoints/{endpointId}")
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.PreconditionRequired, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task BuiltInMerge_InvalidMergedDocumentLeavesActiveConfigurationUnchanged()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoints = Enumerable.Range(0, ConfigurationLimits.MaximumEndpoints - 3)
            .Select(index => CreateEndpoint($"/custom-{index}", $"body-{index}"))
            .ToArray();
        using var imported = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoints),
            "\"0\"");

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/configuration/example/merge",
            "\"1\"");
        using var active = await client.GetAsync($"{BasePath}/endpoints");
        using var activeJson = await ReadJsonAsync(active);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(endpoints.Length, activeJson.RootElement.GetArrayLength());
        Assert.Equal("\"1\"", response.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task Import_WhenRevisionChangesDuringBodyReadReturnsConflict()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = JsonSerializer.SerializeToUtf8Bytes(
            CreateDocument(CreateEndpoint("/slow-import", "slow")),
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{BasePath}/configuration/import")
        {
            Content = new GatedJsonContent(candidate, bodyStarted, releaseBody)
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var importTask = client.SendAsync(request);
        await bodyStarted.Task;
        using var concurrent = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(CreateEndpoint("/winner", "winner")),
            "\"0\"");
        releaseBody.SetResult();

        using var response = await importTask;

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.Equal("winner", await client.GetStringAsync("/winner"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/slow-import")).StatusCode);
    }

    [Fact]
    public async Task EndpointUpdates_MalformedBodiesReturnProblemsWithoutMutation()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint("/malformed-update", "body");
        using var created = await SendDocumentAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/configuration/import",
            CreateDocument(endpoint),
            "\"0\"");

        using var replace = await SendAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/endpoints/{endpoint.Id}",
            "\"1\"",
            "{not json");
        using var enabled = await SendAsync(
            client,
            HttpMethod.Put,
            $"{BasePath}/endpoints/{endpoint.Id}/enabled",
            "\"1\"",
            "{not json");

        Assert.Equal(HttpStatusCode.BadRequest, replace.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, enabled.StatusCode);
        Assert.Equal("body", await client.GetStringAsync(endpoint.Path));
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
        using var matched = await client.GetAsync("/counted?secret=not-retained");
        using var unmatched = await client.GetAsync("/missing");

        using var response = await client.GetAsync($"{BasePath}/statistics");
        using var json = await ReadJsonAsync(response);
        Assert.Equal(2, json.RootElement.GetProperty("totalRequests").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("matchedRequests").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("unmatchedRequests").GetInt64());
        Assert.Equal(endpoint.Id, json.RootElement.GetProperty("endpoints")[0].GetProperty("endpointId").GetGuid());
        var recentRequests = json.RootElement.GetProperty("recentRequests");
        var recentRequest = Assert.Single(recentRequests.EnumerateArray());
        Assert.Equal("/counted", recentRequest.GetProperty("path").GetString());
        Assert.Equal("response", recentRequest.GetProperty("outcome").GetString());
        Assert.Equal(1, recentRequest.GetProperty("transportAttempts").GetInt32());
        Assert.DoesNotContain("not-retained", recentRequests.GetRawText());

        using var reset = await SendAsync(client, HttpMethod.Post, $"{BasePath}/statistics/reset");
        using var afterReset = await client.GetAsync($"{BasePath}/statistics");
        using var resetJson = await ReadJsonAsync(afterReset);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(0, resetJson.RootElement.GetProperty("totalRequests").GetInt64());
        Assert.Empty(resetJson.RootElement.GetProperty("recentRequests").EnumerateArray());
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
    public async Task Statistics_MissingEndpointResetReturnsProblem()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await SendAsync(
            client,
            HttpMethod.Post,
            $"{BasePath}/statistics/endpoints/{Guid.NewGuid()}/reset");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var problem = await ReadJsonAsync(response);
        Assert.EndsWith(
            "endpoint-statistics-not-found",
            problem.RootElement.GetProperty("type").GetString(),
            StringComparison.Ordinal);
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
    public async Task StatisticsEvents_ClientCancellationClosesStream()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{BasePath}/statistics/events");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(stream);
        Assert.Equal("event: statistics", await reader.ReadLineAsync(cancellation.Token));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await reader.ReadLineAsync(cancellation.Token));
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
        using var dashboardCore = await client.GetAsync("/dashboard-core.js");
        using var dashboardEndpointEditor = await client.GetAsync("/dashboard-endpoint-editor.js");
        using var dashboardPreferences = await client.GetAsync("/dashboard-preferences.js");
        using var dashboardSync = await client.GetAsync("/dashboard-sync.js");
        using var dashboardTestRequest = await client.GetAsync("/dashboard-test-request.js");
        using var dashboardTutorial = await client.GetAsync("/dashboard-tutorial.js");
        using var legacyFavicon = await client.GetAsync("/favicon.ico");
        using var favicon = await client.GetAsync("/favicon.svg");
        using var brandMark = await client.GetAsync("/brand-mark.svg");

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
        Assert.Equal("no-cache, private", stylesheet.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", script.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardCore.StatusCode);
        Assert.Equal("text/javascript", dashboardCore.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardCore.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardEndpointEditor.StatusCode);
        Assert.Equal("text/javascript", dashboardEndpointEditor.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardEndpointEditor.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardPreferences.StatusCode);
        Assert.Equal("text/javascript", dashboardPreferences.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardPreferences.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardSync.StatusCode);
        Assert.Equal("text/javascript", dashboardSync.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardSync.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardTestRequest.StatusCode);
        Assert.Equal("text/javascript", dashboardTestRequest.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardTestRequest.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, dashboardTutorial.StatusCode);
        Assert.Equal("text/javascript", dashboardTutorial.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", dashboardTutorial.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, legacyFavicon.StatusCode);
        Assert.Equal("image/svg+xml", legacyFavicon.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", legacyFavicon.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("image/svg+xml", favicon.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", favicon.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.OK, brandMark.StatusCode);
        Assert.Equal("image/svg+xml", brandMark.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache, private", brandMark.Headers.CacheControl!.ToString());
        var html = await dashboard.Content.ReadAsStringAsync();
        var javascript = await script.Content.ReadAsStringAsync();
        Assert.Contains("MockAPI", html, StringComparison.Ordinal);
        Assert.Contains("id=\"load-example-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"empty-load-example-button\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Load template", html, StringComparison.Ordinal);
        Assert.Contains("Load examples", html, StringComparison.Ordinal);
        Assert.Contains("id=\"test-blade\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"test-send\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"filter-status\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://github.com/simonkurtz-MSFT/MockAPI\" target=\"_blank\" rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://www.linkedin.com/in/simonkurtz\" target=\"_blank\" rel=\"noopener noreferrer\"", html, StringComparison.Ordinal);
        Assert.Matches($@"Version {SemanticVersionAssert.Pattern}(?=\s|<)", html);
        Assert.Matches(
            "Built\\s+<time datetime=\"\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{7}\\+00:00\">\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2} UTC</time>",
            html);
        Assert.DoesNotContain("{{VERSION}}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{BUILD_DATE_", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{LOG_ANALYTICS_WORKSPACE_LINK}}", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Log Analytics workspace", html, StringComparison.Ordinal);
        Assert.DoesNotContain("loadBuiltInConfiguration(\"template\")", javascript, StringComparison.Ordinal);
        Assert.Contains("loadBuiltInConfiguration(\"example\")", javascript, StringComparison.Ordinal);
        Assert.Contains("/merge?force=true", javascript, StringComparison.Ordinal);
        Assert.Contains("createDashboardTestBlade", javascript, StringComparison.Ordinal);
        Assert.Contains("type=\"module\"", html, StringComparison.Ordinal);
        Assert.Matches("rel=\"icon\" href=\"/favicon\\.svg\\?v=[A-F0-9]{64}\"", html);
        Assert.Contains("name=\"theme-color\" content=\"#075ea8\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dashboard_LinksToConfiguredLogAnalyticsWorkspace()
    {
        const string workspaceUri = "https://portal.azure.com/#resource/subscriptions/example/resourceGroups/example/providers/Microsoft.OperationalInsights/workspaces/example/overview";
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:LogAnalyticsWorkspaceUri", workspaceUri));
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains(
            $"href=\"{workspaceUri}\" target=\"_blank\" rel=\"noopener noreferrer\">Log Analytics workspace</a>",
            html,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, HttpStatusCode.NotFound)]
    public async Task OpenApiLogo_IsAvailableOnlyWhenDashboardIsEnabled(bool enableDashboard, HttpStatusCode expectedStatus)
    {
        await using var factory = CreateFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("MockApi:EnableDashboard", enableDashboard.ToString()));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/__mockapi/openapi-logo.svg");

        Assert.Equal(expectedStatus, response.StatusCode);
        if (enableDashboard)
        {
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal("no-cache, private", response.Headers.CacheControl!.ToString());
            Assert.Contains("<svg", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
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
    public void Application_HasSemanticVersionMetadata()
    {
        var versionAttribute = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();

        Assert.NotNull(versionAttribute);
        SemanticVersionAssert.IsValid(versionAttribute.InformationalVersion);
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
        Assert.True(configurationPath.TryGetProperty("get", out var getConfiguration));
        Assert.Equal("GetConfigurationStatus", getConfiguration.GetProperty("operationId").GetString());
        Assert.Equal("Get the active configuration status", getConfiguration.GetProperty("summary").GetString());
        var configurationResponse = getConfiguration
            .GetProperty("responses")
            .GetProperty("200")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal(
            "#/components/schemas/ConfigurationStatusResponse",
            configurationResponse.GetProperty("$ref").GetString());
        var operations = paths
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Select(operation => operation.Value)
            .ToArray();
        Assert.Equal(24, operations.Length);
        var apiDescription = paths.GetProperty($"{BasePath}/configuration/api-description").GetProperty("put");
        Assert.Equal("SetApiDescription", apiDescription.GetProperty("operationId").GetString());
        Assert.Contains("If-Match", apiDescription.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal("#/components/schemas/ApiDescriptionRequest",
            apiDescription.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var status in new[] { "200", "400", "412", "413", "415", "422", "428", "500" })
        {
            Assert.True(apiDescription.GetProperty("responses").TryGetProperty(status, out _));
        }
        var dashboardEvents = paths.GetProperty($"{BasePath}/dashboard/events").GetProperty("get");
        Assert.Equal("StreamDashboardEvents", dashboardEvents.GetProperty("operationId").GetString());
        Assert.True(dashboardEvents.GetProperty("responses").GetProperty("200").GetProperty("content")
            .TryGetProperty("text/event-stream", out _));
        Assert.All(operations, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("operationId").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
        });
        Assert.Equal(
            operations.Length,
            operations.Select(operation => operation.GetProperty("operationId").GetString()).Distinct().Count());
        var automaticWrites = operations.Where(operation =>
            operation.GetProperty("operationId").GetString() is
                "CreateEndpoint" or "ReplaceEndpoint" or "SetEndpointEnabled" or "DeleteEndpoint" or
                "ApplyBulkEndpointOperation" or "ImportConfiguration" or "MergeBuiltInConfiguration" or "SetApiDescription")
            .ToArray();
        Assert.Equal(8, automaticWrites.Length);
        Assert.All(automaticWrites, operation =>
        {
            Assert.Contains("saved automatically", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Contains("active but unsaved", operation.GetProperty("description").GetString(), StringComparison.Ordinal);
            Assert.Equal("#/components/schemas/ManagementProblemDetails",
                operation.GetProperty("responses").GetProperty("500").GetProperty("content")
                    .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
        });

        var exportOperation = paths.GetProperty($"{BasePath}/configuration/export/{{format}}").GetProperty("get");
        var downloadParameter = exportOperation.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "download");
        Assert.Equal("query", downloadParameter.GetProperty("in").GetString());
        Assert.False(downloadParameter.TryGetProperty("required", out var required) && required.GetBoolean());
        Assert.Contains("inline", exportOperation.GetProperty("description").GetString(), StringComparison.Ordinal);

        var importOperation = paths
            .GetProperty($"{BasePath}/configuration/import")
            .GetProperty("put");
        Assert.Equal(
            "#/components/schemas/MockApiConfigurationDocument",
            importOperation
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString());
        Assert.Equal(
            "#/components/schemas/ManagementProblemDetails",
            importOperation
                .GetProperty("responses")
                .GetProperty("412")
                .GetProperty("content")
                .GetProperty("application/problem+json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString());
        Assert.False(paths.TryGetProperty($"{BasePath}/configuration/template", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/example", out _));
        Assert.False(paths.TryGetProperty($"{BasePath}/configuration/template/merge", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/configuration/example/merge", out var exampleMerge));
        Assert.True(exampleMerge.TryGetProperty("post", out _));
        Assert.True(paths.TryGetProperty($"{BasePath}/endpoints/bulk", out var bulkEndpointPath));
        Assert.True(bulkEndpointPath.TryGetProperty("post", out var bulkEndpointOperation));
        Assert.True(bulkEndpointOperation.GetProperty("responses").TryGetProperty("204", out _));
        var endpointPath = paths.EnumerateObject().Single(path =>
            path.Name.StartsWith($"{BasePath}/endpoints/{{", StringComparison.Ordinal)
            && !path.Name.EndsWith("/enabled", StringComparison.Ordinal));
        Assert.True(endpointPath.Value.TryGetProperty("delete", out _));
        Assert.True(
            paths.GetProperty($"{BasePath}/statistics/events")
                .GetProperty("get")
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .TryGetProperty("text/event-stream", out _));
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
        new UnsecuredApplicationFactory().WithWebHostBuilder(builder =>
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

    private sealed class UnknownLengthJsonContent : HttpContent
    {
        private readonly byte[] _content;

        public UnknownLengthJsonContent(byte[] content)
        {
            _content = content;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_content).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class GatedJsonContent : HttpContent
    {
        private readonly byte[] _content;
        private readonly TaskCompletionSource _started;
        private readonly TaskCompletionSource _release;

        public GatedJsonContent(byte[] content, TaskCompletionSource started, TaskCompletionSource release)
        {
            _content = content;
            _started = started;
            _release = release;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var split = _content.Length / 2;
            await stream.WriteAsync(_content.AsMemory(0, split));
            await stream.FlushAsync();
            _started.SetResult();
            await _release.Task;
            await stream.WriteAsync(_content.AsMemory(split));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
