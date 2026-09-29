namespace MockAPI.Configuration;

/// <summary>Loads and saves complete configuration snapshots through a persistence provider.</summary>
public interface IConfigurationStore
{
    /// <summary>Loads, validates, and atomically applies the persisted document.</summary>
    /// <param name="state">The configuration state that receives the loaded document.</param>
    /// <param name="cancellationToken">A token that cancels asynchronous persistence I/O.</param>
    /// <returns>A task that completes after the document is applied or an allowed missing document is accepted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationPersistenceException">The persisted content cannot be read, parsed, validated, or applied at its captured revision.</exception>
    Task LoadAsync(ConfigurationState state, CancellationToken cancellationToken);

    /// <summary>Persists one complete immutable snapshot and marks it persisted only if its revision remains current.</summary>
    /// <param name="state">The configuration state whose snapshot is persisted.</param>
    /// <param name="expectedRevision">The revision that must be current before persistence starts.</param>
    /// <param name="cancellationToken">A token that cancels asynchronous persistence I/O.</param>
    /// <returns>The persisted revision and whether that revision was still current when dirty state was cleared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedRevision"/> is negative.</exception>
    /// <exception cref="ConfigurationPersistenceException">The revision is stale or the snapshot cannot be persisted.</exception>
    Task<ConfigurationSaveResult> SaveAsync(
        ConfigurationState state,
        long expectedRevision,
        CancellationToken cancellationToken);
}

/// <summary>Reports the revision written by a save and whether it remained current afterward.</summary>
/// <param name="Revision">The immutable configuration revision written to persistence.</param>
/// <param name="IsCurrentRevision">Whether the written revision was still active and marked persisted after the write.</param>
public sealed record ConfigurationSaveResult(long Revision, bool IsCurrentRevision);
