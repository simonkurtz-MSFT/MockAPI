using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationAutosaveTests : IDisposable
{
    private const string BasePath = "/__mockapi/api";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MockAPI.Tests", Guid.NewGuid().ToString("N"));
    private byte[]? _blobBytes;
    private bool _saveFails;
    private int _saveCount;

    private string ConfigurationPath => Path.Combine(_directory, "mockapi.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationWrites_AutomaticallyPersistCompleteDocumentsToFileOrBlob(bool useBlob)
    {
        var store = CreateStore(useBlob);
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint();

        using var created = await SendAsync(client, HttpMethod.Post, "/endpoints", 0, endpoint);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 1);
        using var response = await client.GetAsync(endpoint.Path);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(["first", "second"], response.Headers.GetValues("X-Values"));
        Assert.Equal("raw\r\nbody", await response.Content.ReadAsStringAsync());
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, endpoint.Path));
        Assert.Equal(response.StatusCode, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        endpoint = endpoint with { Response = endpoint.Response with { Body = "updated" } };
        using var replaced = await SendAsync(client, HttpMethod.Put, $"/endpoints/{endpoint.Id}", 1, endpoint);
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 2);

        using var disabled = await SendAsync(client, HttpMethod.Put, $"/endpoints/{endpoint.Id}/enabled", 2,
            new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 3);

        using var enabled = await SendAsync(client, HttpMethod.Post, "/endpoints/bulk", 3,
            new { endpointIds = new[] { endpoint.Id }, operation = "enable" });
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 4);

        using var description = await SendAsync(client, HttpMethod.Put, "/configuration/api-description", 4,
            new { path = "/autosaved", description = "  persisted overview\r\n  " });
        Assert.Equal(HttpStatusCode.OK, description.StatusCode);
        using var descriptionJson = JsonDocument.Parse(await description.Content.ReadAsStringAsync());
        Assert.False(descriptionJson.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
        await AssertPersistedAsync(factory, store, useBlob, 5);

        var document = factory.Services.GetRequiredService<ConfigurationState>().Current.GetDocument();
        using var imported = await SendAsync(client, HttpMethod.Put, "/configuration/import", 5, document);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 6);

        using var merged = await SendAsync(client, HttpMethod.Post, "/configuration/example/merge", 6);
        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);
        using var mergeJson = JsonDocument.Parse(await merged.Content.ReadAsStringAsync());
        Assert.False(mergeJson.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
        await AssertPersistedAsync(factory, store, useBlob, 7);

        await using (var restartedWithEndpoints = CreateFactory(store))
        {
            using var restoredClient = restartedWithEndpoints.CreateClient();
            using var restoredResponse = await restoredClient.GetAsync(endpoint.Path);
            Assert.Equal(HttpStatusCode.Created, restoredResponse.StatusCode);
            Assert.Equal("updated", await restoredResponse.Content.ReadAsStringAsync());
            var restoredDocument = restartedWithEndpoints.Services.GetRequiredService<ConfigurationState>().Current.GetDocument();
            Assert.Equal(endpoint.Id, Assert.Single(restoredDocument.Endpoints, item => item.Path == endpoint.Path).Id);
        }

        var saveCount = _saveCount;
        using var identicalMerge = await SendAsync(client, HttpMethod.Post, "/configuration/example/merge", 7);
        Assert.Equal(HttpStatusCode.OK, identicalMerge.StatusCode);
        Assert.Equal(saveCount, _saveCount);

        using var deleted = await SendAsync(client, HttpMethod.Delete, $"/endpoints/{endpoint.Id}", 7);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 8);

        var remaining = factory.Services.GetRequiredService<ConfigurationState>().Current.GetDocument().Endpoints;
        using var bulkDeleted = await SendAsync(client, HttpMethod.Post, "/endpoints/bulk", 8,
            new { endpointIds = remaining.Select(item => item.Id).ToArray(), operation = "delete" });
        Assert.Equal(HttpStatusCode.NoContent, bulkDeleted.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob, 9);

        await using var restarted = CreateFactory(store);
        using var restartedClient = restarted.CreateClient();
        var restored = restarted.Services.GetRequiredService<ConfigurationState>().Current;
        Assert.Empty(restored.GetDocument().Endpoints);
        Assert.Equal("  persisted overview\r\n  ", restored.GetDocument().ApiDescriptions!["/autosaved"]);
        Assert.False(restored.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidOrStaleWrites_DoNotChangeThePersistedDocument(bool useBlob)
    {
        var store = CreateStore(useBlob);
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint();

        using var invalid = await SendAsync(client, HttpMethod.Post, "/endpoints", 0,
            endpoint with { Path = "/health" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(0, _saveCount);

        using var created = await SendAsync(client, HttpMethod.Post, "/endpoints", 0, endpoint);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var savedBytes = await ReadPersistedAsync(useBlob);
        using var stale = await SendAsync(client, HttpMethod.Delete, $"/endpoints/{endpoint.Id}", 0);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(savedBytes, await ReadPersistedAsync(useBlob));
        Assert.Equal(1, _saveCount);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    [InlineData("enabled")]
    [InlineData("delete")]
    [InlineData("bulk")]
    [InlineData("import")]
    [InlineData("merge")]
    [InlineData("description")]
    public async Task AutoSaveFailure_LeavesEveryMutationActiveAndAllowsManualRetry(string operation)
    {
        var store = CreateStore(useBlob: true);
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        var endpoint = CreateEndpoint();
        using var created = await SendAsync(client, HttpMethod.Post, "/endpoints", 0, endpoint);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        _saveFails = true;

        using var changed = operation switch
        {
            "create" => await SendAsync(client, HttpMethod.Post, "/endpoints", 1,
                endpoint with { Id = Guid.NewGuid(), Path = "/second" }),
            "replace" => await SendAsync(client, HttpMethod.Put, $"/endpoints/{endpoint.Id}", 1,
                endpoint with { Name = "Updated" }),
            "enabled" => await SendAsync(client, HttpMethod.Put, $"/endpoints/{endpoint.Id}/enabled", 1,
                new { enabled = false }),
            "delete" => await SendAsync(client, HttpMethod.Delete, $"/endpoints/{endpoint.Id}", 1),
            "bulk" => await SendAsync(client, HttpMethod.Post, "/endpoints/bulk", 1,
                new { endpointIds = new[] { endpoint.Id }, operation = "disable" }),
            "import" => await SendAsync(client, HttpMethod.Put, "/configuration/import", 1,
                new MockApiConfigurationDocument { SchemaVersion = "1.0", Endpoints = [] }),
            "merge" => await SendAsync(client, HttpMethod.Post, "/configuration/example/merge", 1),
            "description" => await SendAsync(client, HttpMethod.Put, "/configuration/api-description", 1,
                new { path = "/autosaved", description = "Active but unsaved" }),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        Assert.Equal(HttpStatusCode.InternalServerError, changed.StatusCode);
        Assert.Equal("\"2\"", changed.Headers.ETag!.Tag);
        Assert.Equal("application/problem+json", changed.Content.Headers.ContentType!.MediaType);
        var problemBody = await changed.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(problemBody);
        Assert.EndsWith("autosave-failed", problem.RootElement.GetProperty("type").GetString(), StringComparison.Ordinal);
        Assert.Contains("active", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-storage-error", problemBody, StringComparison.Ordinal);
        var current = factory.Services.GetRequiredService<ConfigurationState>().Current;
        Assert.Equal(2, current.Revision);
        Assert.True(current.HasUnsavedChanges);
        Assert.NotEqual(current.ExportUtf8(), _blobBytes);

        using var staleRetry = await SendAsync(client, HttpMethod.Post, "/configuration/save", 1);
        Assert.Equal(HttpStatusCode.PreconditionFailed, staleRetry.StatusCode);
        _saveFails = false;
        using var retried = await SendAsync(client, HttpMethod.Post, "/configuration/save", 2);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob: true, revision: 2);
        Assert.Equal(3, _saveCount);
    }

    [Fact]
    public async Task FileSaveFailure_LeavesCreatedRouteActiveAndALaterChangeSavesTheCompleteDocument()
    {
        var store = CreateStore(useBlob: false);
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        _saveFails = true;
        var endpoint = CreateEndpoint();

        using var created = await SendAsync(client, HttpMethod.Post, "/endpoints", 0, endpoint);

        Assert.Equal(HttpStatusCode.InternalServerError, created.StatusCode);
        using var mockResponse = await client.GetAsync(endpoint.Path);
        Assert.Equal("raw\r\nbody", await mockResponse.Content.ReadAsStringAsync());
        Assert.True(factory.Services.GetRequiredService<ConfigurationState>().Current.HasUnsavedChanges);
        Assert.False(File.Exists(ConfigurationPath));

        _saveFails = false;
        using var edited = await SendAsync(client, HttpMethod.Put, "/configuration/api-description", 1,
            new { path = "/autosaved", description = "Later change saves the endpoint too" });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await AssertPersistedAsync(factory, store, useBlob: false, revision: 2);
        Assert.Equal(endpoint.Id, Assert.Single(factory.Services.GetRequiredService<ConfigurationState>().Current.GetDocument().Endpoints).Id);
    }

    [Fact]
    public async Task RequestDisconnect_DoesNotCancelPersistenceAfterActivation()
    {
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ConfigurationBlobStore(
            new MockApiOptions { ConfigurationPath = ConfigurationPath, AllowEmptyConfiguration = true },
            _ => Task.FromResult<ConfigurationBlobRead?>(_blobBytes is null
                ? null
                : new(new MemoryStream(_blobBytes), _blobBytes.Length)),
            async (content, token) =>
            {
                Assert.False(token.CanBeCanceled);
                captured.TrySetResult();
                await release.Task;
                _blobBytes = content.ToArray();
            });
        await using var factory = CreateFactory(store);
        using var client = factory.CreateClient();
        using var cancellation = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, BasePath + "/endpoints")
        {
            Content = JsonContent.Create(CreateEndpoint())
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        var mutation = client.SendAsync(request, cancellation.Token);
        try
        {
            await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(factory.Services.GetRequiredService<ConfigurationState>().Current.HasUnsavedChanges);
            cancellation.Cancel();
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation);
        }
        finally
        {
            release.TrySetResult();
        }

        await factory.Services.GetRequiredService<ConfigurationManagementService>().AutoSaveAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await AssertPersistedAsync(factory, store, useBlob: true, revision: 1);
    }

    [Fact]
    public async Task AutoSave_ConcurrentRevisionsPersistTheWinnerWithoutOverwritingItWithAnOlderSnapshot()
    {
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = new List<byte[]>();
        var store = new ConfigurationBlobStore(
            new MockApiOptions { ConfigurationPath = ConfigurationPath, AllowEmptyConfiguration = true },
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            async (content, token) =>
            {
                if (uploads.Count == 0)
                {
                    captured.SetResult();
                    await release.Task.WaitAsync(token);
                }
                uploads.Add(content.ToArray());
            });
        var state = new ConfigurationState();
        var service = new ConfigurationManagementService(state, store);
        var endpoint = CreateEndpoint();
        var first = new MockApiConfigurationDocument { SchemaVersion = "1.0", Endpoints = [endpoint] };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(first, 0).Status);
        var firstSave = service.AutoSaveAsync(CancellationToken.None);
        await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = first with { Endpoints = [endpoint with { Name = "Second" }] };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(second, 1).Status);
        var secondSave = service.AutoSaveAsync(CancellationToken.None);
        var winner = first with { Endpoints = [endpoint with { Name = "Winner" }] };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(winner, 2).Status);
        release.SetResult();

        await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(3, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Equal(state.Current.ExportUtf8(), uploads[^1]);
        Assert.Equal("Winner", Assert.Single(state.Current.GetDocument().Endpoints).Name);
        var count = uploads.Count;
        await service.AutoSaveAsync(CancellationToken.None);
        Assert.Equal(count, uploads.Count);
    }

    [Fact]
    public async Task AutoSave_CancelledBeforePersistenceLeavesTheRevisionUnsaved()
    {
        var state = new ConfigurationState();
        state.TryReplace(new MockApiConfigurationDocument { SchemaVersion = "1.0", Endpoints = [CreateEndpoint()] }, 0);
        var service = new ConfigurationManagementService(state, CreateStore(useBlob: true));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.AutoSaveAsync(cancellation.Token));

        Assert.True(state.Current.HasUnsavedChanges);
        Assert.Equal(0, _saveCount);
    }

    private IConfigurationStore CreateStore(bool useBlob)
    {
        var options = new MockApiOptions { ConfigurationPath = ConfigurationPath, AllowEmptyConfiguration = true };
        if (useBlob)
        {
            return new ConfigurationBlobStore(options,
                _ => Task.FromResult<ConfigurationBlobRead?>(_blobBytes is null
                    ? null
                    : new(new MemoryStream(_blobBytes), _blobBytes.Length)),
                (content, _) =>
                {
                    _saveCount++;
                    if (_saveFails) throw new RequestFailedException(503, "private-storage-error");
                    _blobBytes = content.ToArray();
                    return Task.CompletedTask;
                });
        }

        return new ConfigurationFileStore(options, _ => ValueTask.CompletedTask,
            createDirectory: path =>
            {
                _saveCount++;
                if (_saveFails) throw new IOException("private-storage-error");
                Directory.CreateDirectory(path);
            });
    }

    private WebApplicationFactory<Program> CreateFactory(IConfigurationStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:ConfigurationPath", ConfigurationPath);
            builder.UseSetting("MockApi:RequireApiKey", "false");
            builder.UseSetting("MockApi:AllowEmptyConfiguration", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IConfigurationStore>();
                services.AddSingleton(store);
            });
        });

    private async Task AssertPersistedAsync(
        WebApplicationFactory<Program> factory, IConfigurationStore store, bool useBlob, long revision)
    {
        var current = factory.Services.GetRequiredService<ConfigurationState>().Current;
        Assert.Equal(revision, current.Revision);
        Assert.False(current.HasUnsavedChanges);
        Assert.Equal(current.ExportUtf8(), await ReadPersistedAsync(useBlob));
        var reloaded = new ConfigurationState();
        await store.LoadAsync(reloaded, CancellationToken.None);
        Assert.Equal(current.ExportUtf8(), reloaded.Current.ExportUtf8());
        Assert.False(reloaded.Current.HasUnsavedChanges);
    }

    private async Task<byte[]> ReadPersistedAsync(bool useBlob) =>
        useBlob ? _blobBytes! : await File.ReadAllBytesAsync(ConfigurationPath);

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string path, long revision, object? body = null)
    {
        using var request = new HttpRequestMessage(method, BasePath + path);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{revision}\"");
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static MockEndpointDefinition CreateEndpoint() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Automatically saved",
        Enabled = true,
        Methods = ["GET", "HEAD"],
        Path = "/autosaved/endpoint",
        Response = new MockResponseDefinition
        {
            StatusCode = 201,
            ContentType = "text/plain; charset=utf-8",
            Headers = new Dictionary<string, string[]> { ["X-Values"] = ["first", "second"] },
            Body = "raw\r\nbody"
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
