using System.Text.Json;

namespace MockAPI.Configuration;

public sealed class ConfigurationFileStore
{
    private readonly MockApiOptions _options;
    private readonly Func<CancellationToken, ValueTask> _onSnapshotCaptured;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ConfigurationFileStore(MockApiOptions options)
        : this(options, _ => ValueTask.CompletedTask)
    {
    }

    internal ConfigurationFileStore(
        MockApiOptions options,
        Func<CancellationToken, ValueTask> onSnapshotCaptured)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConfigurationPath);
        ArgumentNullException.ThrowIfNull(onSnapshotCaptured);

        _options = options;
        _onSnapshotCaptured = onSnapshotCaptured;
    }

    public async Task LoadAsync(ConfigurationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var expectedRevision = state.Current.Revision;

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
            await using var stream = new FileStream(
                _options.ConfigurationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
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

        var update = state.TryLoadPersisted(document, expectedRevision);
        if (update.Status == ConfigurationUpdateStatus.ValidationFailed)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.ValidationFailed,
                "The configuration file failed validation.",
                validation: update.Validation);
        }

        if (update.Status == ConfigurationUpdateStatus.RevisionConflict)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.RevisionConflict,
                "The active configuration changed while the configuration file was loading.");
        }
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return await SaveAsync(state, state.Current.Revision, cancellationToken);
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = state.Current;
            if (snapshot.Revision != expectedRevision)
            {
                throw new ConfigurationPersistenceException(
                    ConfigurationPersistenceError.RevisionConflict,
                    "The active configuration changed before it could be saved.");
            }

            await _onSnapshotCaptured(cancellationToken);
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
                Directory.CreateDirectory(directory);
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(snapshot.ExportUtf8(), cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, targetPath, overwrite: true);
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
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new ConfigurationSaveResult(
                snapshot.Revision,
                state.TryMarkPersisted(snapshot.Revision));
        }
        finally
        {
            _saveGate.Release();
        }
    }
}

public sealed record ConfigurationSaveResult(long Revision, bool IsCurrentRevision);