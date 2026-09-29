using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;

namespace MockAPI.Configuration;

/// <summary>Loads and saves complete configuration documents in one Azure Blob.</summary>
/// <remarks>Uploads replace the blob as one operation; concurrent saves are serialized per store instance.</remarks>
public sealed class ConfigurationBlobStore : IConfigurationStore
{
    private readonly MockApiOptions _options;
    private readonly Func<CancellationToken, Task<ConfigurationBlobRead?>> _download;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _upload;
    private readonly ConfigurationPersistenceCoordinator _coordinator;

    /// <summary>Initializes an Azure Blob configuration store.</summary>
    /// <param name="options">Persistence options, including missing-document behavior.</param>
    /// <param name="blobClient">The authenticated client for the configured blob.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="blobClient"/> is <see langword="null"/>.</exception>
    public ConfigurationBlobStore(MockApiOptions options, BlobClient blobClient)
        : this(
            options,
            async cancellationToken =>
            {
                try
                {
                    var response = await blobClient.DownloadStreamingAsync(cancellationToken: cancellationToken);
                    return new ConfigurationBlobRead(
                        response.Value.Content,
                        response.Value.Details.ContentLength);
                }
                catch (RequestFailedException exception) when (exception.Status == StatusCodes.Status404NotFound)
                {
                    return null;
                }
            },
            async (content, cancellationToken) =>
            {
                await blobClient.UploadAsync(
                    BinaryData.FromBytes(content),
                    overwrite: true,
                    cancellationToken);
            })
    {
    }

    internal ConfigurationBlobStore(
        MockApiOptions options,
        Func<CancellationToken, Task<ConfigurationBlobRead?>> download,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> upload,
        Func<CancellationToken, ValueTask>? onSnapshotCaptured = null,
        Func<CancellationToken, ValueTask>? onDocumentLoaded = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(download);
        ArgumentNullException.ThrowIfNull(upload);
        _options = options;
        _download = download;
        _upload = upload;
        _coordinator = new ConfigurationPersistenceCoordinator(
            onSnapshotCaptured ?? (_ => ValueTask.CompletedTask),
            onDocumentLoaded ?? (_ => ValueTask.CompletedTask));
    }

    /// <inheritdoc/>
    public async Task LoadAsync(ConfigurationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var expectedRevision = _coordinator.CaptureLoadRevision(state);
        ConfigurationBlobRead? blob;
        try
        {
            blob = await _download(cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            throw CreateIoFailure("read", exception);
        }
        catch (AuthenticationFailedException exception)
        {
            throw CreateIoFailure("read", exception);
        }

        if (blob is null)
        {
            if (_options.AllowEmptyConfiguration)
            {
                return;
            }

            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.FileNotFound,
                "The configured MockAPI configuration blob does not exist.");
        }

        await using var content = blob.Content;
        if (blob.Length > ConfigurationLimits.MaximumDocumentBytes)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.DocumentTooLarge,
                $"The configuration blob cannot exceed {ConfigurationLimits.MaximumDocumentBytes} UTF-8 bytes.");
        }

        MockApiConfigurationDocument document;
        try
        {
            document = await JsonSerializer.DeserializeAsync(
                content,
                MockApiJsonContext.Default.MockApiConfigurationDocument,
                cancellationToken) ?? throw new ConfigurationPersistenceException(
                    ConfigurationPersistenceError.InvalidJson,
                    "The configuration blob must contain a JSON document.");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.InvalidJson,
                "The configuration blob contains invalid JSON.",
                exception);
        }
        catch (IOException exception)
        {
            throw CreateIoFailure("read", exception);
        }

        await _coordinator.ApplyLoadedAsync(
            state,
            document,
            expectedRevision,
            "The configuration blob failed validation.",
            "The active configuration changed while the configuration blob was loading.",
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        return await _coordinator.SaveAsync(
            state,
            expectedRevision,
            async (snapshot, saveCancellationToken) =>
            {
                try
                {
                    await _upload(snapshot.ExportUtf8(), saveCancellationToken);
                }
                catch (RequestFailedException exception)
                {
                    throw CreateIoFailure("saved", exception);
                }
                catch (AuthenticationFailedException exception)
                {
                    throw CreateIoFailure("saved", exception);
                }
                catch (IOException exception)
                {
                    throw CreateIoFailure("saved", exception);
                }

            },
            cancellationToken);
    }

    private static ConfigurationPersistenceException CreateIoFailure(string operation, Exception exception) =>
        new(
            ConfigurationPersistenceError.IoFailure,
            $"The configuration blob could not be {operation}.",
            exception);
}

internal sealed record ConfigurationBlobRead(Stream Content, long Length);
