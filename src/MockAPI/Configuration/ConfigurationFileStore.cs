using System.Text.Json;

namespace MockAPI.Configuration;

/// <summary>Loads and atomically saves complete configuration documents in the local file system.</summary>
/// <remarks>Saves flush a same-directory temporary file before replacing the target and are serialized per store instance.</remarks>
public sealed class ConfigurationFileStore : IConfigurationStore
{
    private readonly MockApiOptions _options;
    private readonly ConfigurationPersistenceCoordinator _coordinator;
    private readonly Func<string, CancellationToken, ValueTask<Stream>> _openRead;
    private readonly Action<string> _createDirectory;
    private readonly Action<string> _deleteFile;

    /// <summary>Initializes a local-file configuration store.</summary>
    /// <param name="options">Persistence options containing the target path and missing-file behavior.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configured local path is empty or whitespace.</exception>
    public ConfigurationFileStore(MockApiOptions options)
        : this(options, _ => ValueTask.CompletedTask)
    {
    }

    internal ConfigurationFileStore(
        MockApiOptions options,
        Func<CancellationToken, ValueTask> onSnapshotCaptured,
        Func<CancellationToken, ValueTask>? onDocumentLoaded = null,
        Func<string, CancellationToken, ValueTask<Stream>>? openRead = null,
        Action<string>? createDirectory = null,
        Action<string>? deleteFile = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationPath);
        ArgumentNullException.ThrowIfNull(onSnapshotCaptured);

        _options = options;
        _coordinator = new ConfigurationPersistenceCoordinator(
            onSnapshotCaptured,
            onDocumentLoaded ?? (_ => ValueTask.CompletedTask));
        _openRead = openRead ?? OpenReadAsync;
        _createDirectory = createDirectory ?? (path => Directory.CreateDirectory(path));
        _deleteFile = deleteFile ?? File.Delete;
    }

    /// <inheritdoc/>
    public async Task LoadAsync(ConfigurationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var expectedRevision = _coordinator.CaptureLoadRevision(state);

        if (!File.Exists(_options.ConfigurationPath))
        {
            if (_options.AllowEmptyConfiguration)
            {
                return;
            }

            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.FileNotFound,
                "The configured MockAPI configuration file does not exist.");
        }

        MockApiConfigurationDocument document;
        try
        {
            await using var stream = await _openRead(_options.ConfigurationPath, cancellationToken);
            if (stream.Length > ConfigurationLimits.MaximumDocumentBytes)
            {
                throw new ConfigurationPersistenceException(
                    ConfigurationPersistenceError.DocumentTooLarge,
                    $"The configuration file cannot exceed {ConfigurationLimits.MaximumDocumentBytes} UTF-8 bytes.");
            }

            document = await JsonSerializer.DeserializeAsync(
                stream,
                MockApiJsonContext.Default.MockApiConfigurationDocument,
                cancellationToken) ?? throw new ConfigurationPersistenceException(
                    ConfigurationPersistenceError.InvalidJson,
                    "The configuration file must contain a JSON document.");
        }
        catch (JsonException exception)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.InvalidJson,
                "The configuration file contains invalid JSON.",
                exception);
        }
        catch (IOException exception)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.IoFailure,
                "The configuration file could not be read.",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.IoFailure,
                "The configuration file could not be read.",
                exception);
        }

        await _coordinator.ApplyLoadedAsync(
            state,
            document,
            expectedRevision,
            "The configuration file failed validation.",
            "The active configuration changed while the configuration file was loading.",
            cancellationToken);
    }

    /// <summary>Persists the configuration revision that is current when the operation begins.</summary>
    /// <param name="state">The configuration state whose current snapshot is persisted.</param>
    /// <param name="cancellationToken">A token that cancels asynchronous file I/O.</param>
    /// <returns>The persisted revision and whether it remained current after replacement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationPersistenceException">The snapshot cannot be persisted.</exception>
    public async Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return await SaveAsync(state, state.Current.Revision, cancellationToken);
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
                var targetPath = Path.GetFullPath(_options.ConfigurationPath);
                var directory = Path.GetDirectoryName(targetPath) ??
                    throw new ConfigurationPersistenceException(
                        ConfigurationPersistenceError.IoFailure,
                        "The configuration path must have a parent directory.");
                var temporaryPath = Path.Combine(
                    directory,
                    $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

                try
                {
                    _createDirectory(directory);
                    await using (var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 64 * 1024,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await stream.WriteAsync(snapshot.ExportUtf8(), saveCancellationToken);
                        await stream.FlushAsync(saveCancellationToken);
                        stream.Flush(flushToDisk: true);
                    }

                    await AtomicFileReplacement.ReplaceAsync(
                        temporaryPath,
                        targetPath,
                        saveCancellationToken);
                }
                catch (IOException exception)
                {
                    throw new ConfigurationPersistenceException(
                        ConfigurationPersistenceError.IoFailure,
                        "The configuration file could not be saved.",
                        exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    throw new ConfigurationPersistenceException(
                        ConfigurationPersistenceError.IoFailure,
                        "The configuration file could not be saved.",
                        exception);
                }
                finally
                {
                    try
                    {
                        _deleteFile(temporaryPath);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

            },
            cancellationToken);
    }

    private static ValueTask<Stream> OpenReadAsync(string path, CancellationToken _) =>
        ValueTask.FromResult<Stream>(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
}
