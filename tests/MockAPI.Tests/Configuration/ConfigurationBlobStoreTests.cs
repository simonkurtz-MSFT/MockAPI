using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationBlobStoreTests
{
    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var options = CreateOptions();

        Assert.Throws<ArgumentNullException>(() => new ConfigurationBlobStore(null!, _ => Task.FromResult<ConfigurationBlobRead?>(null), (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationBlobStore(options, null!, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => new ConfigurationBlobStore(options, _ => Task.FromResult<ConfigurationBlobRead?>(null), null!));
    }

    [Fact]
    public async Task LoadAsync_ValidBlobPublishesCleanState()
    {
        var document = CreateDocument("/loaded", "loaded");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        var state = new ConfigurationState();
        var store = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(
                new(new MemoryStream(bytes), bytes.Length)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);

        await store.LoadAsync(state, CancellationToken.None);

        Assert.Equal(1, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/loaded", out var endpoint));
        Assert.Equal("loaded", Encoding.UTF8.GetString(endpoint.Body.Span));
    }

    [Fact]
    public async Task LoadAsync_MissingBlobHonorsAllowEmptyConfiguration()
    {
        var allowedState = new ConfigurationState();
        var requiredState = new ConfigurationState();
        var allowed = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: true);
        var required = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);

        await allowed.LoadAsync(allowedState, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => required.LoadAsync(requiredState, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.FileNotFound, exception.Error);
        Assert.Equal(0, allowedState.Current.Revision);
    }

    [Fact]
    public async Task SaveAsync_UploadsSnapshotAndMarksRevisionPersisted()
    {
        ReadOnlyMemory<byte> uploaded = default;
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/saved", "saved"), expectedRevision: 0).Status);
        var store = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            (content, _) =>
            {
                uploaded = content.ToArray();
                return Task.CompletedTask;
            },
            allowEmptyConfiguration: true);

        var result = await store.SaveAsync(state, expectedRevision: 1, CancellationToken.None);

        Assert.Equal(1, result.Revision);
        Assert.True(result.IsCurrentRevision);
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Equal(state.Current.ExportUtf8(), uploaded.ToArray());
    }

    [Fact]
    public async Task LoadAsync_RejectsOversizedInvalidAndSemanticallyInvalidDocuments()
    {
        var oversized = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new MemoryStream(), ConfigurationLimits.MaximumDocumentBytes + 1L)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);
        var invalidJsonBytes = "{"u8.ToArray();
        var invalidJson = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new MemoryStream(invalidJsonBytes), invalidJsonBytes.Length)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);
        var invalidDocument = CreateDocument("/health", "reserved");
        var invalidDocumentBytes = JsonSerializer.SerializeToUtf8Bytes(
            invalidDocument,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        var invalidConfiguration = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new MemoryStream(invalidDocumentBytes), invalidDocumentBytes.Length)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);

        var oversizedException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => oversized.LoadAsync(new ConfigurationState(), CancellationToken.None));
        var jsonException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => invalidJson.LoadAsync(new ConfigurationState(), CancellationToken.None));
        var validationException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => invalidConfiguration.LoadAsync(new ConfigurationState(), CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.DocumentTooLarge, oversizedException.Error);
        Assert.Equal(ConfigurationPersistenceError.InvalidJson, jsonException.Error);
        Assert.Equal(ConfigurationPersistenceError.ValidationFailed, validationException.Error);
        Assert.NotNull(validationException.Validation);
        Assert.NotEmpty(validationException.Validation.Errors);
    }

    [Fact]
    public async Task LoadAsync_ReportsNullDocumentAndReadFailures()
    {
        var nullBytes = "null"u8.ToArray();
        var nullDocument = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new MemoryStream(nullBytes), nullBytes.Length)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);
        var requestFailure = CreateStore(
            _ => throw new RequestFailedException(500, "failed"),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);
        var authenticationFailure = CreateStore(
            _ => throw new AuthenticationFailedException("failed"),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);
        var ioFailure = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new ThrowingReadStream(), 1)),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: false);

        var nullException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => nullDocument.LoadAsync(new ConfigurationState(), CancellationToken.None));
        var requestException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => requestFailure.LoadAsync(new ConfigurationState(), CancellationToken.None));
        var authenticationException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => authenticationFailure.LoadAsync(new ConfigurationState(), CancellationToken.None));
        var ioException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => ioFailure.LoadAsync(new ConfigurationState(), CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.InvalidJson, nullException.Error);
        Assert.Equal(ConfigurationPersistenceError.IoFailure, requestException.Error);
        Assert.Equal(ConfigurationPersistenceError.IoFailure, authenticationException.Error);
        Assert.Equal(ConfigurationPersistenceError.IoFailure, ioException.Error);
    }

    [Fact]
    public async Task LoadAsync_RejectsStateChangeDuringDownload()
    {
        var document = CreateDocument("/loaded", "loaded");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        var state = new ConfigurationState();
        var store = new ConfigurationBlobStore(
            CreateOptions(),
            _ => Task.FromResult<ConfigurationBlobRead?>(new(new MemoryStream(bytes), bytes.Length)),
            (_, _) => Task.CompletedTask,
            onDocumentLoaded: _ =>
            {
                Assert.Equal(
                    ConfigurationUpdateStatus.Applied,
                    state.TryReplace(CreateDocument("/newer", "newer"), expectedRevision: 0).Status);
                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => store.LoadAsync(state, CancellationToken.None));

        Assert.Equal(ConfigurationPersistenceError.RevisionConflict, exception.Error);
    }

    [Fact]
    public async Task SaveAsync_RejectsRevisionMismatchAndReportsUploadFailures()
    {
        var state = CreateDirtyState();
        var mismatch = CreateStore(
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            (_, _) => Task.CompletedTask,
            allowEmptyConfiguration: true);

        var mismatchException = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
            () => mismatch.SaveAsync(state, expectedRevision: 0, CancellationToken.None));
        Assert.Equal(ConfigurationPersistenceError.RevisionConflict, mismatchException.Error);

        foreach (var failure in new Exception[]
        {
            new RequestFailedException(500, "failed"),
            new AuthenticationFailedException("failed"),
            new IOException("failed")
        })
        {
            var store = CreateStore(
                _ => Task.FromResult<ConfigurationBlobRead?>(null),
                (_, _) => Task.FromException(failure),
                allowEmptyConfiguration: true);
            var exception = await Assert.ThrowsAsync<ConfigurationPersistenceException>(
                () => store.SaveAsync(state, expectedRevision: 1, CancellationToken.None));
            Assert.Equal(ConfigurationPersistenceError.IoFailure, exception.Error);
        }
    }

    [Fact]
    public async Task SaveAsync_PreservesDirtyStateWhenConfigurationChangesDuringUpload()
    {
        var state = CreateDirtyState();
        var store = new ConfigurationBlobStore(
            CreateOptions(),
            _ => Task.FromResult<ConfigurationBlobRead?>(null),
            (_, _) => Task.CompletedTask,
            onSnapshotCaptured: _ =>
            {
                Assert.Equal(
                    ConfigurationUpdateStatus.Applied,
                    state.TryReplace(CreateDocument("/newer", "newer"), expectedRevision: 1).Status);
                return ValueTask.CompletedTask;
            });

        var result = await store.SaveAsync(state, expectedRevision: 1, CancellationToken.None);

        Assert.Equal(1, result.Revision);
        Assert.False(result.IsCurrentRevision);
        Assert.True(state.Current.HasUnsavedChanges);
    }

    [Fact]
    public async Task SdkConstructor_DownloadsUploadsAndHandlesMissingBlob()
    {
        var document = CreateDocument("/sdk", "sdk");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        var blobClient = new TestBlobClient(bytes);
        var store = new ConfigurationBlobStore(CreateOptions(), blobClient);
        var state = new ConfigurationState();

        await store.LoadAsync(state, CancellationToken.None);
        Assert.Equal(1, state.Current.Revision);

        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/uploaded", "uploaded"), expectedRevision: 1).Status);
        await store.SaveAsync(state, expectedRevision: 2, CancellationToken.None);
        Assert.Equal(state.Current.ExportUtf8(), blobClient.Uploaded.ToArray());

        blobClient.Missing = true;
        await store.LoadAsync(new ConfigurationState(), CancellationToken.None);
    }

    private static ConfigurationBlobStore CreateStore(
        Func<CancellationToken, Task<ConfigurationBlobRead?>> download,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> upload,
        bool allowEmptyConfiguration) =>
        new(
            new MockApiOptions
            {
                ConfigurationPath = "/unused/mockapi.json",
                AllowEmptyConfiguration = allowEmptyConfiguration
            },
            download,
            upload);

    private static MockApiOptions CreateOptions() => new()
    {
        ConfigurationPath = "/unused/mockapi.json",
        AllowEmptyConfiguration = true
    };

    private static ConfigurationState CreateDirtyState()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/saved", "saved"), expectedRevision: 0).Status);
        return state;
    }

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

    private sealed class TestBlobClient(byte[] content) : BlobClient
    {
        internal ReadOnlyMemory<byte> Uploaded { get; private set; }

        internal bool Missing { get; set; }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(
            BlobDownloadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Missing)
            {
                throw new RequestFailedException(404, "missing");
            }

            var details = BlobsModelFactory.BlobDownloadDetails(contentLength: content.Length);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(
                new MemoryStream(content),
                details);
            return Task.FromResult(Response.FromValue(result, new TestResponse()));
        }

        public override Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content,
            bool overwrite = false,
            CancellationToken cancellationToken = default)
        {
            Uploaded = content.ToMemory();
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(
                    new ETag("test"),
                    DateTimeOffset.UtcNow,
                    contentHash: null,
                    versionId: null,
                    encryptionKeySha256: null,
                    encryptionScope: null,
                    blobSequenceNumber: 0),
                new TestResponse()));
        }
    }

    private sealed class TestResponse : Response
    {
        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];

        protected override bool TryGetHeader(string name, out string value)
        {
            value = null!;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = null!;
            return false;
        }
    }

    private sealed class ThrowingReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("failed"));
    }
}
