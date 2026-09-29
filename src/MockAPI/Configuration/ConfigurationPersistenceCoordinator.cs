namespace MockAPI.Configuration;

internal sealed class ConfigurationPersistenceCoordinator
{
    private readonly Func<CancellationToken, ValueTask> _onSnapshotCaptured;
    private readonly Func<CancellationToken, ValueTask> _onDocumentLoaded;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public ConfigurationPersistenceCoordinator(
        Func<CancellationToken, ValueTask> onSnapshotCaptured,
        Func<CancellationToken, ValueTask> onDocumentLoaded)
    {
        ArgumentNullException.ThrowIfNull(onSnapshotCaptured);
        ArgumentNullException.ThrowIfNull(onDocumentLoaded);
        _onSnapshotCaptured = onSnapshotCaptured;
        _onDocumentLoaded = onDocumentLoaded;
    }

    public long CaptureLoadRevision(ConfigurationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Current.Revision;
    }

    public async Task ApplyLoadedAsync(
        ConfigurationState state,
        MockApiConfigurationDocument document,
        long expectedRevision,
        string validationFailureMessage,
        string revisionConflictMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(document);

        await _onDocumentLoaded(cancellationToken);
        var update = state.TryLoadPersisted(document, expectedRevision);
        if (update.Status == ConfigurationUpdateStatus.ValidationFailed)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.ValidationFailed,
                validationFailureMessage,
                validation: update.Validation);
        }

        if (update.Status == ConfigurationUpdateStatus.RevisionConflict)
        {
            throw new ConfigurationPersistenceException(
                ConfigurationPersistenceError.RevisionConflict,
                revisionConflictMessage);
        }
    }

    public async Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        long expectedRevision,
        Func<ConfigurationStateSnapshot, CancellationToken, Task> persist,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        ArgumentNullException.ThrowIfNull(persist);

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
            await persist(snapshot, cancellationToken);
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
