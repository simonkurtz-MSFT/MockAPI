using System.Text.Json;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationStateTests
{
    [Fact]
    public void GetEndpoint_FindsIdAfterNestedResponseWithoutAdvancingDeserializationReader()
    {
        var id = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes($$"""
            {
              "schemaVersion": "1.0",
              "endpoints": [{
                "name": "Selected endpoint",
                "enabled": true,
                "path": "/selected",
                "methods": ["GET"],
                "response": { "statusCode": 201, "headers": { "X-Values": ["first", "second"] }, "body": "raw body" },
                "id": "{{id}}"
              }]
            }
            """);
        var snapshot = ConfigurationStateSnapshot.CreateFromOwnedSerializedDocument(
            bytes, null, new ConfigurationState().Current.Endpoints, 1, false);

        var selected = snapshot.GetEndpoint(id);

        Assert.Equal(id, selected.Id);
        Assert.Equal("/selected", selected.Path);
        Assert.Equal("GET", Assert.Single(selected.Methods));
        Assert.Equal(201, selected.Response.StatusCode);
        Assert.Equal(new[] { "first", "second" }, selected.Response.Headers["X-Values"]);
        Assert.Equal("raw body", selected.Response.Body);
        Assert.Throws<InvalidOperationException>(() => snapshot.GetEndpoint(Guid.NewGuid()));
    }

    [Fact]
    public void GetEndpoint_ReturnsDetachedCanonicalDataFromCapturedRevision()
    {
        var state = new ConfigurationState();
        var first = CreateEndpoint("GET", "/first", "first");
        var selected = CreateEndpoint("POST", "/selected", " \r\n{\"raw\": true}\n") with
        {
            Response = new()
            {
                StatusCode = 201,
                Headers = new() { ["X-Z"] = ["first", "second"], ["X-A"] = ["value"] },
                ContentType = "application/json",
                Body = " \r\n{\"raw\": true}\n"
            }
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(first, selected), 0).Status);
        var captured = state.Current;
        var expected = captured.GetDocument().Endpoints[1];
        var actual = captured.GetEndpoint(selected.Id);
        Assert.Equal(JsonSerializer.Serialize(expected, MockApiJsonContext.Default.MockEndpointDefinition),
            JsonSerializer.Serialize(actual, MockApiJsonContext.Default.MockEndpointDefinition));
        Assert.IsAssignableFrom<IList<string>>(actual.Methods)[0] = "DELETE";
        actual.Response.Headers["X-Z"][0] = "mutated";
        Assert.Equal("POST", captured.GetEndpoint(selected.Id).Methods[0]);
        Assert.Equal("first", captured.GetEndpoint(selected.Id).Response.Headers["X-Z"][0]);

        state.TryReplace(CreateDocument(first), 1);
        Assert.Equal(selected.Id, captured.GetEndpoint(selected.Id).Id);
        Assert.Equal(first.Id, state.Current.GetEndpoint(first.Id).Id);
        Assert.Throws<InvalidOperationException>(() => state.Current.GetEndpoint(selected.Id));
        Assert.Throws<InvalidOperationException>(() => new ConfigurationState().Current.GetEndpoint(first.Id));
    }

    [Fact]
    public void GetEndpoint_DoesNotAllocateForUnrelatedLargeBodies()
    {
        var state = new ConfigurationState();
        var selected = CreateEndpoint("GET", "/selected", "small");
        var large = CreateEndpoint("GET", "/large", new string('x', 960 * 1024));
        state.TryReplace(CreateDocument(large, selected), 0);
        var captured = state.Current;
        for (var index = 0; index < 5; index++)
        {
            captured.GetEndpoint(selected.Id);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = captured.GetEndpoint(selected.Id);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(selected.Id, result.Id);
        Assert.InRange(allocated, 0, 8_192);
    }

    [Fact]
    public void ApiDescriptions_AreDetachedAndCanonicallyOrderedAcrossImportExport()
    {
        var descriptions = new Dictionary<string, string> { ["/z"] = " Z\r\n ", ["/a"] = "A", ["/A"] = "" };
        var document = CreateDocument(CreateEndpoint("GET", "/a/hello", "body")) with { ApiDescriptions = descriptions };
        var state = new ConfigurationState();
        state.TryReplace(document, 0);
        var bytes = state.Current.ExportUtf8();

        descriptions["/z"] = "changed";
        state.Current.GetDocument().ApiDescriptions!["/a"] = "changed";
        Assert.Equal(" Z\r\n ", state.Current.GetDocument().ApiDescriptions!["/z"]);
        Assert.Equal(new[] { "/A", "/a", "/z" }, state.Current.GetDocument().ApiDescriptions!.Keys);

        var imported = JsonSerializer.Deserialize(bytes, MockApiJsonContext.Default.MockApiConfigurationDocument)!;
        state.TryReplace(imported, 1);
        Assert.Equal(bytes, state.Current.ExportUtf8());
    }

    [Fact]
    public void ApiDescriptions_ProjectionIsOwnedImmutableAndSharedOnlyWithinRevision()
    {
        var descriptions = new Dictionary<string, string> { ["/z"] = " Z\r\n ", ["/a"] = "A", ["/A"] = "" };
        var state = new ConfigurationState();
        Assert.Null(state.Current.ApiDescriptions);
        Assert.Equal(ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/a/hello", "body")) with { ApiDescriptions = descriptions }, 0).Status);
        var dirty = state.Current;
        var projection = dirty.ApiDescriptions!;
        descriptions.Clear();
        dirty.GetDocument().ApiDescriptions!.Clear();

        Assert.Equal(new[] { "/A", "/a", "/z" }, projection.Keys);
        Assert.Equal("", projection["/A"]);
        Assert.Equal("A", projection["/a"]);
        Assert.Equal(" Z\r\n ", projection["/z"]);
        Assert.False(projection.ContainsKey("/Z"));
        var dictionary = Assert.IsAssignableFrom<IDictionary<string, string>>(projection);
        Assert.True(dictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => dictionary["/a"] = "changed");
        Assert.Throws<NotSupportedException>(() => dictionary.Clear());

        Assert.True(state.TryMarkPersisted(dirty.Revision));
        Assert.Same(projection, state.Current.ApiDescriptions);
        Assert.Equal(dirty.Revision, state.Current.Revision);
        Assert.Equal(dirty.ETag, state.Current.ETag);
        Assert.Equal(ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument() with { ApiDescriptions = [] }, dirty.Revision).Status);
        Assert.NotNull(state.Current.ApiDescriptions);
        Assert.Empty(state.Current.ApiDescriptions);
        Assert.NotSame(projection, state.Current.ApiDescriptions);
        Assert.Equal(3, projection.Count);

        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(), state.Current.Revision).Status);
        Assert.Null(state.Current.ApiDescriptions);
    }

    [Fact]
    public async Task ChangeNotifications_ObserveAtomicEditsAndPersistenceWithoutLostWakeups()
    {
        var state = new ConfigurationState();
        var initial = state.Current;
        var waiting = state.WaitForChangeAsync(initial);
        Assert.Same(waiting, state.WaitForChangeAsync(initial));
        Assert.False(waiting.IsCompleted);

        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/changed", "body")), 0);
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(state.WaitForChangeAsync(initial).IsCompleted);
        var dirty = state.Current;
        Assert.True(dirty.Endpoints.TryGet("GET", "/changed", out _));
        var saveWaiting = state.WaitForChangeAsync(dirty);
        Assert.True(state.TryMarkPersisted(dirty.Revision));
        await saveWaiting.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(dirty.Revision, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);

        var unchanged = state.WaitForChangeAsync(state.Current);
        Assert.True(state.TryMarkPersisted(dirty.Revision));
        Assert.False(state.TryMarkPersisted(0));
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/health", "invalid")), dirty.Revision);
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/stale", "body")), 0);
        Assert.False(unchanged.IsCompleted);
    }

    [Fact]
    public async Task TryReplace_WhenRevisionChangesBeforeWriteLockReturnsConflict()
    {
        var firstReachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var state = new ConfigurationState(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstReachedLock.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        });
        var firstTask = Task.Run(() => state.TryReplace(
            CreateDocument(CreateEndpoint("GET", "/first", "first")),
            0));
        await firstReachedLock.Task;

        var winner = state.TryReplace(CreateDocument(CreateEndpoint("GET", "/winner", "winner")), 0);
        releaseFirst.SetResult();
        var first = await firstTask;

        Assert.Equal(ConfigurationUpdateStatus.Applied, winner.Status);
        Assert.Equal(ConfigurationUpdateStatus.RevisionConflict, first.Status);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/winner", out _));
    }
    [Fact]
    public void TryReplace_AtomicallyPublishesDocumentRoutesRevisionAndDirtyState()
    {
        var state = new ConfigurationState();
        var endpoint = CreateEndpoint("GET", "/active", "body");

        var result = state.TryReplace(CreateDocument(endpoint), expectedRevision: 0);

        Assert.Equal(ConfigurationUpdateStatus.Applied, result.Status);
        var snapshot = state.Current;
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal("\"1\"", snapshot.ETag);
        Assert.True(snapshot.HasUnsavedChanges);
        Assert.True(snapshot.Endpoints.TryGet("get", "/active", out var runtimeEndpoint));
        Assert.Equal(endpoint.Id, runtimeEndpoint.Id);
        Assert.Equal(endpoint.Id, Assert.Single(snapshot.GetDocument().Endpoints).Id);
    }

    [Fact]
    public void TryReplace_InvalidCandidateLeavesAllStateUnchanged()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/original", "body")), 0).Status);
        var original = state.Current;

        var result = state.TryReplace(
            CreateDocument(CreateEndpoint("GET", "/health", "invalid")),
            original.Revision);

        Assert.Equal(ConfigurationUpdateStatus.ValidationFailed, result.Status);
        Assert.NotEmpty(result.Validation.Errors);
        Assert.Same(original, state.Current);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/original", out _));
    }

    [Fact]
    public void TryReplace_StaleRevisionLeavesAllStateUnchanged()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/current", "body")), 0).Status);
        var current = state.Current;

        var result = state.TryReplace(
            CreateDocument(CreateEndpoint("POST", "/stale", "body")),
            expectedRevision: 0);

        Assert.Equal(ConfigurationUpdateStatus.RevisionConflict, result.Status);
        Assert.Same(current, state.Current);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/current", out _));
        Assert.False(state.Current.Endpoints.TryGet("POST", "/stale", out _));
    }

    [Fact]
    public void MarkPersisted_ClearsDirtyStateOnlyForCurrentRevision()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/active", "body")), 0).Status);

        Assert.False(state.TryMarkPersisted(expectedRevision: 0));
        Assert.True(state.Current.HasUnsavedChanges);
        Assert.True(state.TryMarkPersisted(expectedRevision: 1));
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Equal(1, state.Current.Revision);
        Assert.Equal("\"1\"", state.Current.ETag);
    }

    [Fact]
    public void PublishedState_DoesNotReferenceMutableCandidateCollections()
    {
        var methods = new[] { "GET" };
        var headerValues = new[] { "original" };
        var endpoint = CreateEndpoint("GET", "/immutable", "body") with
        {
            Methods = methods,
            Response = new MockResponseDefinition
            {
                StatusCode = 200,
                Headers = new() { ["X-Value"] = headerValues },
                ContentType = "text/plain",
                Body = "body"
            }
        };
        var state = new ConfigurationState();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(endpoint), 0).Status);

        methods[0] = "POST";
        headerValues[0] = "changed";
        var exported = state.Current.GetDocument();
        exported.Endpoints[0].Response.Headers["X-Value"][0] = "also changed";

        Assert.True(state.Current.Endpoints.TryGet("GET", "/immutable", out var runtimeEndpoint));
        Assert.False(state.Current.Endpoints.TryGet("POST", "/immutable", out _));
        Assert.Equal("original", Assert.Single(Assert.Single(runtimeEndpoint.Headers).Values));
        Assert.Equal("original", state.Current.GetDocument().Endpoints[0].Response.Headers["X-Value"][0]);
    }

    [Fact]
    public void Export_IsDeterministicAndValidatesAgainstSchema()
    {
        var state = new ConfigurationState();
        var endpoint = CreateEndpoint("GET", "/export", "body") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 200,
                Headers = new()
                {
                    ["Z-Last"] = ["z"],
                    ["A-First"] = ["a"]
                },
                ContentType = "text/plain",
                Body = "body"
            }
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(endpoint), 0).Status);

        var first = state.Current.ExportUtf8();
        var second = state.Current.ExportUtf8();

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        var evaluation = ConfigurationSchemaFixture.Evaluate(json.RootElement);
        Assert.True(evaluation.IsValid, JsonSerializer.Serialize(evaluation));
        var text = JsonSerializer.Serialize(json.RootElement);
        Assert.True(text.IndexOf("A-First", StringComparison.Ordinal) < text.IndexOf("Z-Last", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportUtf8_ReturnsIndependentMutableCopies()
    {
        var state = new ConfigurationState();
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/detached", "body")), 0);
        var snapshot = state.Current;
        var first = snapshot.ExportUtf8();
        var second = snapshot.ExportUtf8();

        Assert.NotSame(first, second);
        first[0] = 0;

        Assert.Equal(second, snapshot.ExportUtf8());
        Assert.Equal("/detached", Assert.Single(snapshot.GetDocument().Endpoints).Path);
    }

    [Fact]
    public async Task WriteToAsync_WritesCapturedCanonicalBytesAndLeavesDestinationOpen()
    {
        var state = new ConfigurationState();
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/captured", "\u00e9\r\nbody")), 0);
        var snapshot = state.Current;
        var expected = snapshot.ExportUtf8();
        Assert.True(state.TryMarkPersisted(snapshot.Revision));
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/newer", "newer")), snapshot.Revision);
        using var destination = new MemoryStream();

        await snapshot.WriteToAsync(destination, CancellationToken.None);

        Assert.Equal(expected, destination.ToArray());
        Assert.True(destination.CanWrite);
        Assert.Equal("/newer", Assert.Single(state.Current.GetDocument().Endpoints).Path);
    }

    [Fact]
    public async Task WriteToAsync_RejectsNullDestinationAndPropagatesCancellation()
    {
        var snapshot = new ConfigurationState().Current;
        Assert.Throws<ArgumentNullException>(() => snapshot.WriteToAsync(null!, CancellationToken.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => snapshot.WriteToAsync(Stream.Null, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task OpenRead_ReturnsIndependentReadOnlyStreamsWithoutExposingSnapshotStorage()
    {
        var state = new ConfigurationState();
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/captured", "body")), 0);
        var snapshot = state.Current;
        var expected = snapshot.ExportUtf8();
        using var first = snapshot.OpenRead();
        using var second = snapshot.OpenRead();

        Assert.True(first.CanRead);
        Assert.True(first.CanSeek);
        Assert.False(first.CanWrite);
        Assert.Equal(expected.Length, first.Length);
        Assert.Equal(expected[0], first.ReadByte());
        Assert.Equal(0, second.Position);
        Assert.Throws<NotSupportedException>(() => first.WriteByte(0));
        Assert.Throws<NotSupportedException>(() => first.SetLength(0));
        var memoryStream = Assert.IsType<MemoryStream>(first);
        Assert.False(memoryStream.TryGetBuffer(out _));
        Assert.Throws<UnauthorizedAccessException>(() => memoryStream.GetBuffer());

        first.Dispose();
        state.TryReplace(CreateDocument(CreateEndpoint("GET", "/newer", "newer")), snapshot.Revision);
        using var destination = new MemoryStream();
        await second.CopyToAsync(destination);

        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(expected, snapshot.ExportUtf8());
        Assert.Equal("/newer", Assert.Single(state.Current.GetDocument().Endpoints).Path);
    }

    [Fact]
    public void DeserializeDocument_RejectsNullAndReadsValidDocument()
    {
        var document = CreateDocument(CreateEndpoint("GET", "/deserialize", "body"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);

        Assert.Single(ConfigurationStateSnapshot.DeserializeDocument(bytes).Endpoints);
        Assert.Throws<InvalidOperationException>(() => ConfigurationStateSnapshot.DeserializeDocument("null"u8));
    }

    [Fact]
    public void ConcurrentWritersWithSameRevision_HaveSingleWinner()
    {
        var state = new ConfigurationState();
        var results = new ConfigurationUpdateResult[100];

        Parallel.For(0, results.Length, index =>
        {
            results[index] = state.TryReplace(
                CreateDocument(CreateEndpoint("GET", $"/endpoint-{index}", index.ToString())),
                expectedRevision: 0);
        });

        Assert.Single(results, result => result.Status == ConfigurationUpdateStatus.Applied);
        Assert.Equal(99, results.Count(result => result.Status == ConfigurationUpdateStatus.RevisionConflict));
        Assert.Equal(1, state.Current.Revision);
        var document = state.Current.GetDocument();
        var endpoint = Assert.Single(document.Endpoints);
        Assert.True(state.Current.Endpoints.TryGet("GET", endpoint.Path, out _));
    }

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static MockEndpointDefinition CreateEndpoint(string method, string path, string body) => new()
    {
        Id = Guid.NewGuid(),
        Name = path,
        Enabled = true,
        Methods = [method],
        Path = path,
        Response = new MockResponseDefinition
        {
            StatusCode = 200,
            Headers = [],
            ContentType = "text/plain; charset=utf-8",
            Body = body
        }
    };

}
