using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class EndpointSynchronizationTests
{
    private const string BasePath = "/__mockapi/api/endpoints";

    [Fact]
    public async Task ManifestAndBatch_ExposeOrderedHashesAndOnlySelectedDefinitionsWithoutMutation()
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var document = ConfigurationReadTests.CreateState("text").Current.GetDocument();
        document = document with
        {
            Endpoints = [document.Endpoints[0], document.Endpoints[1] with { Enabled = false }]
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(document, 0).Status);
        var snapshot = state.Current;

        using var manifest = await ReadAsync(client, "/manifest", snapshot.ETag);
        Assert.True(manifest.Headers.CacheControl!.NoStore);
        Assert.Equal(snapshot.ETag, manifest.Headers.ETag!.Tag);
        var entries = await manifest.Content.ReadFromJsonAsync(ManagementJsonContext.Default.EndpointFingerprintArray);
        Assert.NotNull(entries);
        Assert.Equal(document.Endpoints.Select(endpoint => endpoint.Id), entries.Select(entry => entry.Id));
        Assert.All(entries, entry => Assert.Matches("^[A-F0-9]{32}$", entry.ContentHash));
        using var manifestJson = JsonDocument.Parse(await manifest.Content.ReadAsStringAsync());
        Assert.All(manifestJson.RootElement.EnumerateArray(), entry =>
            Assert.Equal(["id", "contentHash"], entry.EnumerateObject().Select(property => property.Name)));

        using var batch = await ReadAsync(client, "/batch", snapshot.ETag, new
        {
            endpoints = new[] { entries[1] }
        });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        Assert.True(batch.Headers.CacheControl!.NoStore);
        Assert.Equal(snapshot.ETag, batch.Headers.ETag!.Tag);
        var definitions = await batch.Content.ReadFromJsonAsync(ManagementJsonContext.Default.MockEndpointDefinitionArray);
        Assert.Equal(document.Endpoints[1].Id, Assert.Single(definitions!).Id);
        Assert.Equal(
            JsonSerializer.Serialize(document.Endpoints[1], MockApiJsonContext.Default.MockEndpointDefinition),
            JsonSerializer.Serialize(definitions![0], MockApiJsonContext.Default.MockEndpointDefinition));

        using var reversed = await ReadAsync(client, "/batch", snapshot.ETag, new
        {
            endpoints = entries.Reverse().ToArray()
        });
        var ordered = await reversed.Content.ReadFromJsonAsync(ManagementJsonContext.Default.MockEndpointDefinitionArray);
        Assert.Equal(document.Endpoints.Select(endpoint => endpoint.Id), ordered!.Select(endpoint => endpoint.Id));
        Assert.Same(snapshot, state.Current);
        using var invoked = await client.GetAsync(document.Endpoints[0].Path);
        Assert.Equal(document.Endpoints[0].Response.Body, await invoked.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Fingerprints_AreStableAcrossMetadataSavesAndOrderingButTrackEveryEndpointField()
    {
        var state = ConfigurationReadTests.CreateState("text");
        var original = state.Current;
        var manifest = original.EndpointManifest;
        Assert.Same(manifest, original.EndpointManifest);
        Assert.True(state.TryMarkPersisted(original.Revision));
        Assert.Same(manifest, state.Current.EndpointManifest);
        var document = state.Current.GetDocument();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(document with
        {
            ApiDescriptions = new() { ["/a"] = "Metadata only" },
            Endpoints = document.Endpoints.Reverse().ToArray()
        }, state.Current.Revision).Status);
        Assert.Equal(manifest.Reverse(), state.Current.EndpointManifest);

        var endpoint = document.Endpoints[0];
        MockEndpointDefinition[] changes =
        [
            endpoint with { Name = "Renamed" },
            endpoint with { Description = "New description" },
            endpoint with { Path = "/changed" },
            endpoint with { Methods = ["POST"] },
            endpoint with { Enabled = false },
            endpoint with { RequestCount = 2 },
            endpoint with { Response = endpoint.Response with { StatusCode = 201 } },
            endpoint with { Response = endpoint.Response with { ReasonPhrase = "Custom" } },
            endpoint with { Response = endpoint.Response with { ContentType = "application/json" } },
            endpoint with { Response = endpoint.Response with { Body = "x\r\nraw\t " } },
            endpoint with { Response = endpoint.Response with { Headers = new() { ["X-Fixture"] = ["second", "first"] } } },
            endpoint with { Response = endpoint.Response with { DelaySeconds = 1 } }
        ];
        foreach (var changed in changes)
        {
            Assert.Equal(ConfigurationUpdateStatus.Applied,
                state.TryReplace(document with { Endpoints = [changed, document.Endpoints[1]] }, state.Current.Revision).Status);
            Assert.NotEqual(manifest[0].ContentHash, state.Current.EndpointManifest[0].ContentHash);
            Assert.Equal(manifest[1], state.Current.EndpointManifest[1]);
        }
        Assert.Equal(manifest, original.EndpointManifest);
    }

    [Theory]
    [InlineData("/manifest", null, 428)]
    [InlineData("/manifest", "W/\"0\"", 400)]
    [InlineData("/manifest", "*", 400)]
    [InlineData("/manifest", "\"9\"", 412)]
    [InlineData("/batch", null, 428)]
    [InlineData("/batch", "W/\"0\"", 400)]
    [InlineData("/batch", "\"9\"", 412)]
    public async Task Reads_RequireTheExactStrongRevision(string path, string? etag, int expectedStatus)
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        using var response = await ReadAsync(client, path, etag, path == "/batch" ? new { endpoints = Array.Empty<EndpointFingerprint>() } : null);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("\"0\"", response.Headers.ETag!.Tag);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"endpoints\":null}")]
    [InlineData("{\"endpoints\":[]}")]
    [InlineData("{\"endpoints\":[null]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"00000000-0000-0000-0000-000000000000\",\"contentHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"contentHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"},{\"id\":\"00000001-0000-0000-0000-000000000000\",\"contentHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"invalid\",\"contentHash\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"contentHash\":null}]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"contentHash\":\"short\"}]}")]
    [InlineData("{\"endpoints\":[{\"id\":\"00000001-0000-0000-0000-000000000000\",\"contentHash\":\"GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG\"}]}")]
    [InlineData("{\"endpoints\":[],\"extra\":true}")]
    public async Task Batch_RejectsInvalidSelections(string json)
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BasePath}/batch")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(0, factory.Services.GetRequiredService<ConfigurationState>().Current.Revision);
    }

    [Fact]
    public async Task Batch_RejectsStaleManifestsAndUnknownIdsWithoutReturningPartialDefinitions()
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var document = ConfigurationReadTests.CreateState("text").Current.GetDocument();
        state.TryReplace(document, 0);
        using var manifest = await ReadAsync(client, "/manifest", "\"1\"");
        var selection = state.Current.EndpointManifest;
        state.TryReplace(document with { Endpoints = [document.Endpoints[0]] }, 1);
        using var stale = await ReadAsync(client, "/batch", manifest.Headers.ETag!.Tag, new
        {
            endpoints = selection
        });
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        using var unknown = await ReadAsync(client, "/batch", "\"2\"", new
        {
            endpoints = selection
        });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, problem.RootElement.ValueKind);
        Assert.False(problem.RootElement.TryGetProperty("endpoints", out _));
        Assert.Equal(2, state.Current.Revision);
    }

    [Fact]
    public async Task Batch_RejectsOldContentHashesEvenWhenARevisionIsReusedAfterRestart()
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var priorProcess = ConfigurationReadTests.CreateState("text");
        var document = priorProcess.Current.GetDocument();
        var changed = document.Endpoints[0] with { Response = document.Endpoints[0].Response with { Body = "Changed after restart" } };
        state.TryReplace(document with { Endpoints = [changed, document.Endpoints[1]] }, 0);
        Assert.Equal(priorProcess.Current.ETag, state.Current.ETag);
        using var rejected = await ReadAsync(client, "/batch", priorProcess.Current.ETag, new
        {
            endpoints = priorProcess.Current.EndpointManifest
        });
        Assert.Equal(HttpStatusCode.PreconditionFailed, rejected.StatusCode);
        using var accepted = await ReadAsync(client, "/batch", state.Current.ETag, new
        {
            endpoints = state.Current.EndpointManifest
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public void ManifestAndSelectedRead_DoNotMaterializeUnrelatedLargeBodies()
    {
        var state = ConfigurationReadTests.CreateState("text", bodyLength: 166000, endpointCount: 25);
        var document = state.Current.GetDocument();
        var small = document.Endpoints[0] with { Response = document.Endpoints[0].Response with { Body = "small" } };
        state.TryReplace(document with { Endpoints = [small, .. document.Endpoints.Skip(1)] }, state.Current.Revision);
        var selected = new HashSet<Guid> { small.Id };
        _ = state.Current.GetEndpoints(selected);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var manifest = state.Current.EndpointManifest;
        var batch = state.Current.GetEndpoints(selected);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(25, manifest.Count);
        Assert.Equal("small", Assert.Single(batch).Response.Body);
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    private static async Task<HttpResponseMessage> ReadAsync(HttpClient client, string path, string? etag, object? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, BasePath + path);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-Match", etag);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }
}
