using System.Text.Json;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "MockAPI.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_MissingAllowedFileLeavesInitialStateClean()
    {
        var state = new ConfigurationState();
        var store = CreateStore("missing.json", allowEmptyConfiguration: true);

        await store.LoadAsync(state, CancellationToken.None);

        Assert.Equal(0, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Empty(state.Current.GetDocument().Endpoints);
    }

    [Fact]
    public async Task LoadAsync_MissingRequiredFileFails()
    {
        var state = new ConfigurationState();
        var store = CreateStore("missing.json", allowEmptyConfiguration: false);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.FileNotFound, exception.Error);
        Assert.Equal(0, state.Current.Revision);
    }

    [Fact]
    public async Task LoadAsync_ValidFilePublishesCleanState()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mockapi.json");
        await File.WriteAllTextAsync(path, Serialize(CreateDocument("/loaded", "loaded")));
        var state = new ConfigurationState();
        var store = CreateStore(path, allowEmptyConfiguration: false);

        await store.LoadAsync(state, CancellationToken.None);

        Assert.Equal(1, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/loaded", out var endpoint));
        Assert.Equal("loaded", System.Text.Encoding.UTF8.GetString(endpoint.Body.Span));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"endpoints\":[{}]}")]
    [InlineData("{\"schemaVersion\":\"2.0\",\"endpoints\":[]}")]
    public async Task LoadAsync_InvalidFileFailsWithoutChangingState(string content)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "invalid.json");
        await File.WriteAllTextAsync(path, content);
        var state = new ConfigurationState();
        var store = CreateStore(path, allowEmptyConfiguration: false);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Contains(
            exception.Error,
            new[] { ConfigurationPersistenceError.InvalidJson, ConfigurationPersistenceError.ValidationFailed });
        Assert.Equal(0, state.Current.Revision);
        Assert.Empty(state.Current.GetDocument().Endpoints);
    }

    [Fact]
    public async Task LoadAsync_OversizedFileIsRejectedBeforeParsing()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "oversized.json");
        await File.WriteAllBytesAsync(path, new byte[ConfigurationLimits.MaximumDocumentBytes + 1]);
        var state = new ConfigurationState();
        var store = CreateStore(path, allowEmptyConfiguration: false);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.DocumentTooLarge, exception.Error);
        Assert.Equal(0, state.Current.Revision);
    }

    [Fact]
    public async Task LoadAsync_LockedFileReturnsIoFailure()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "locked.json");
        await File.WriteAllTextAsync(path, Serialize(CreateDocument("/locked", "body")));
        await using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var state = new ConfigurationState();
        var store = CreateStore(path, allowEmptyConfiguration: false);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(0, state.Current.Revision);
    }

    [Fact]
    public async Task LoadAsync_UnauthorizedReadReturnsIoFailure()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "unauthorized.json");
        await File.WriteAllTextAsync(path, "{}");
        var state = new ConfigurationState();
        var store = CreateStore(
            path,
            openRead: (_, _) => ValueTask.FromException<Stream>(new UnauthorizedAccessException("denied")));

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
    }

    [Fact]
    public async Task LoadAsync_WhenRevisionChangesAfterReadReturnsRevisionConflict()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "revision.json");
        await File.WriteAllTextAsync(path, Serialize(CreateDocument("/loaded", "loaded")));
        var state = new ConfigurationState();
        var store = CreateStore(
            path,
            onDocumentLoaded: _ =>
            {
                Assert.Equal(
                    ConfigurationUpdateStatus.Applied,
                    state.TryReplace(CreateDocument("/concurrent", "concurrent"), 0).Status);
                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.RevisionConflict, exception.Error);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/concurrent", out _));
    }

    [Fact]
    public async Task SaveAsync_WritesDeterministicDocumentAndMarksRevisionPersisted()
    {
        var path = Path.Combine(_directory, "nested", "mockapi.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "old content");
        var state = new ConfigurationState();
        var update = state.TryReplace(CreateDocument("/saved", "saved"), expectedRevision: 0);
        Assert.Equal(ConfigurationUpdateStatus.Applied, update.Status);
        var store = CreateStore(path, allowEmptyConfiguration: true);

        var result = await store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(1, result.Revision);
        Assert.True(result.IsCurrentRevision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Equal(state.Current.ExportUtf8(), await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".mockapi.json.*.tmp"));
    }

    [Fact]
    public async Task SaveAsync_WhenRevisionChangesAfterCapture_DoesNotMarkNewRevisionPersisted()
    {
        var path = Path.Combine(_directory, "mockapi.json");
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/first", "first"), expectedRevision: 0).Status);
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ConfigurationFileStore(
            new MockApiOptions { ConfigurationPath = path, AllowEmptyConfiguration = true },
            async cancellationToken =>
            {
                snapshotCaptured.SetResult();
                await continueSave.Task.WaitAsync(cancellationToken);
            });

        var saveTask = store.SaveAsync(state, CancellationToken.None);
        await snapshotCaptured.Task;
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/second", "second"), expectedRevision: 1).Status);
        continueSave.SetResult();
        var result = await saveTask;

        Assert.Equal(1, result.Revision);
        Assert.False(result.IsCurrentRevision);
        Assert.Equal(2, state.Current.Revision);
        Assert.True(state.Current.HasUnsavedChanges);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("/first", saved.RootElement.GetProperty("endpoints")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task SaveAsync_ConcurrentSavesCannotLeaveOlderRevisionOnDisk()
    {
        var path = Path.Combine(_directory, "mockapi.json");
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/first", "first"), expectedRevision: 0).Status);
        var firstCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        var store = new ConfigurationFileStore(
            new MockApiOptions { ConfigurationPath = path, AllowEmptyConfiguration = true },
            async cancellationToken =>
            {
                if (Interlocked.Increment(ref captureCount) == 1)
                {
                    firstCaptured.SetResult();
                    await continueFirst.Task.WaitAsync(cancellationToken);
                }
            });

        var firstSave = store.SaveAsync(state, CancellationToken.None);
        await firstCaptured.Task;
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/second", "second"), expectedRevision: 1).Status);
        var secondSave = store.SaveAsync(state, CancellationToken.None);
        continueFirst.SetResult();
        var results = await Task.WhenAll(firstSave, secondSave);

        Assert.False(results[0].IsCurrentRevision);
        Assert.True(results[1].IsCurrentRevision);
        Assert.False(state.Current.HasUnsavedChanges);
        using var saved = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.Equal("/second", saved.RootElement.GetProperty("endpoints")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task SaveAsync_WhenParentPathIsAFile_ReturnsIoFailureAndLeavesStateDirty()
    {
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "blocker");
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/unsaved", "body"), expectedRevision: 0).Status);
        var store = CreateStore(Path.Combine(blocker, "mockapi.json"), allowEmptyConfiguration: true);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.SaveAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
        Assert.True(state.Current.HasUnsavedChanges);
        Assert.Equal("blocker", await File.ReadAllTextAsync(blocker));
    }

    [Fact]
    public async Task SaveAsync_RootPathWithoutParentReturnsIoFailure()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_directory))!;
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/unsaved", "body"), 0).Status);
        var store = CreateStore(root, allowEmptyConfiguration: true);

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.SaveAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
    }

    [Fact]
    public async Task SaveAsync_UnauthorizedDirectoryCreationReturnsIoFailure()
    {
        var path = Path.Combine(_directory, "unauthorized", "mockapi.json");
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/unsaved", "body"), 0).Status);
        var store = CreateStore(
            path,
            createDirectory: _ => throw new UnauthorizedAccessException("denied"));

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.SaveAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
        Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        Assert.True(state.Current.HasUnsavedChanges);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SaveAsync_TemporaryCleanupFailureDoesNotFailSuccessfulSave(bool unauthorized)
    {
        var path = Path.Combine(_directory, "cleanup", "mockapi.json");
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/saved", "body"), 0).Status);
        var store = CreateStore(
            path,
            deleteFile: _ =>
            {
                if (unauthorized)
                {
                    throw new UnauthorizedAccessException("denied");
                }

                throw new IOException("busy");
            });

        var result = await store.SaveAsync(state, CancellationToken.None);

        Assert.True(result.IsCurrentRevision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ConfigurationFileStore CreateStore(string path, bool allowEmptyConfiguration) =>
        new(new MockApiOptions
        {
            ConfigurationPath = Path.IsPathRooted(path) ? path : Path.Combine(_directory, path),
            AllowEmptyConfiguration = allowEmptyConfiguration
        });

    private ConfigurationFileStore CreateStore(
        string path,
        Func<CancellationToken, ValueTask>? onDocumentLoaded = null,
        Func<string, CancellationToken, ValueTask<Stream>>? openRead = null,
        Action<string>? createDirectory = null,
        Action<string>? deleteFile = null) =>
        new(
            new MockApiOptions
            {
                ConfigurationPath = Path.IsPathRooted(path) ? path : Path.Combine(_directory, path),
                AllowEmptyConfiguration = false
            },
            _ => ValueTask.CompletedTask,
            onDocumentLoaded,
            openRead,
            createDirectory,
            deleteFile);

    private static MockApiConfigurationDocument CreateDocument(string path, string body) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints =
        [
            new MockEndpointDefinition
            {
                Id = Guid.NewGuid(),
                Name = path,
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
            }
        ]
    };

    private static string Serialize(MockApiConfigurationDocument document) =>
        JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
}
