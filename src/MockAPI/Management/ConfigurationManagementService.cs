using MockAPI.Configuration;

namespace MockAPI.Management;

/// <summary>Coordinates validation, import, built-in merge, and persistence operations for complete configurations.</summary>
/// <param name="state">The atomic configuration state.</param>
/// <param name="store">The configured persistence provider.</param>
public sealed class ConfigurationManagementService(
    ConfigurationState state,
    IConfigurationStore store)
{
    /// <summary>Gets the immutable active configuration snapshot.</summary>
    public ConfigurationStateSnapshot Current => state.Current;

    /// <summary>Validates a complete candidate without changing active state.</summary>
    /// <param name="candidate">The complete candidate document.</param>
    /// <returns>All validation errors, or a valid result when the candidate can be activated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public ConfigurationValidationResult Validate(MockApiConfigurationDocument candidate) =>
        ConfigurationValidator.Validate(candidate);

    /// <summary>Atomically replaces the active configuration when validation and revision checks succeed.</summary>
    /// <param name="candidate">The complete candidate document.</param>
    /// <param name="expectedRevision">The active revision required by the import.</param>
    /// <returns>The applied, validation-failed, or revision-conflict result.</returns>
    public ConfigurationUpdateResult Import(
        MockApiConfigurationDocument candidate,
        long expectedRevision) =>
        state.TryReplace(candidate, expectedRevision);

    /// <summary>Sets a path-based API description without changing endpoints or other API metadata.</summary>
    /// <param name="path">The case-sensitive group path, such as <c>/ctp</c>.</param>
    /// <param name="description">Freeform text, including an empty string to suppress a built-in description.</param>
    /// <param name="expectedRevision">The revision captured when the editor opened.</param>
    /// <returns>The atomic update result. Validation or revision failure leaves all active state unchanged.</returns>
    public ConfigurationUpdateResult SetApiDescription(string path, string description, long expectedRevision)
    {
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ConfigurationUpdateResult.RevisionConflict();
        }

        var document = snapshot.GetDocument();
        var descriptions = new Dictionary<string, string>(document.ApiDescriptions ?? [], StringComparer.Ordinal)
        {
            [path] = description
        };
        return state.TryReplace(document with { ApiDescriptions = descriptions }, expectedRevision);
    }

    /// <summary>Merges a built-in document by stable ID while preserving unrelated active endpoints.</summary>
    /// <param name="builtIn">The complete built-in document whose endpoints are merged.</param>
    /// <param name="expectedRevision">The active revision required by the merge.</param>
    /// <param name="force">Whether divergent same-ID endpoints and route collisions are replaced after explicit confirmation.</param>
    /// <returns>An atomic merge result; conflicts, validation errors, and stale revisions leave active state unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builtIn"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedRevision"/> is negative.</exception>
    public BuiltInMergeResult MergeBuiltIn(
        MockApiConfigurationDocument builtIn,
        long expectedRevision,
        bool force)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return BuiltInMergeResult.RevisionConflict();
        }

        var current = snapshot.GetDocument();
        var analysis = AnalyzeMerge(current.Endpoints, builtIn.Endpoints);

        if (analysis.Conflicts.Count != 0 && !force)
        {
            return BuiltInMergeResult.Conflict(snapshot, analysis.Identical.Count, analysis.Conflicts);
        }

        var builtInIds = builtIn.Endpoints.Select(endpoint => endpoint.Id).ToHashSet();
        var retained = current.Endpoints.Where(active =>
            ShouldRetain(active, builtInIds, builtIn.Endpoints, force));

        var mergedEndpoints = force
            ? retained.Concat(builtIn.Endpoints).ToArray()
            : current.Endpoints.Concat(analysis.Additions).ToArray();
        var descriptions = new Dictionary<string, string>(current.ApiDescriptions ?? [], StringComparer.Ordinal);
        foreach (var (path, description) in builtIn.ApiDescriptions ?? [])
        {
            // Even a deliberately empty local description wins over the built-in default.
            descriptions.TryAdd(path, description);
        }

        var metadataAdded = descriptions.Count != (current.ApiDescriptions?.Count ?? 0);
        if (!metadataAdded && mergedEndpoints.Length == current.Endpoints.Count &&
            mergedEndpoints.Zip(current.Endpoints).All(pair => EndpointsEqual(pair.First, pair.Second)))
        {
            return BuiltInMergeResult.NoChanges(snapshot, analysis.Identical.Count);
        }

        var candidate = new MockApiConfigurationDocument
        {
            Schema = current.Schema,
            SchemaVersion = current.SchemaVersion,
            ApiDescriptions = descriptions.Count == 0 ? current.ApiDescriptions : descriptions,
            Endpoints = mergedEndpoints
        };
        var update = state.TryReplace(candidate, expectedRevision);
        return update.Status switch
        {
            ConfigurationUpdateStatus.Applied => BuiltInMergeResult.Applied(
                update.Snapshot!,
                analysis.Additions.Count,
                force ? analysis.Changed.Count : 0,
                analysis.Identical.Count,
                analysis.Conflicts),
            ConfigurationUpdateStatus.ValidationFailed => BuiltInMergeResult.ValidationFailed(update.Validation),
            _ => BuiltInMergeResult.RevisionConflict()
        };
    }

    /// <summary>Persists the specified active revision through the configured store.</summary>
    /// <param name="expectedRevision">The active revision required before persistence begins.</param>
    /// <param name="cancellationToken">A token that cancels persistence I/O.</param>
    /// <returns>The persisted revision and whether it remained current afterward.</returns>
    /// <exception cref="ConfigurationPersistenceException">The revision is stale or persistence fails.</exception>
    public Task<ConfigurationSaveResult> SaveAsync(
        long expectedRevision,
        CancellationToken cancellationToken) =>
        store.SaveAsync(state, expectedRevision, cancellationToken);

    /// <summary>Automatically persists active changes, including revisions that supersede an in-flight save.</summary>
    /// <param name="cancellationToken">A token that cancels persistence I/O.</param>
    /// <returns>A task completing when the current revision is confirmed persisted.</returns>
    /// <exception cref="ConfigurationPersistenceException">Persistence fails; active changes remain unsaved.</exception>
    public async Task AutoSaveAsync(CancellationToken cancellationToken)
    {
        while (state.Current.HasUnsavedChanges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = state.Current.Revision;
            try
            {
                await store.SaveAsync(state, revision, cancellationToken);
            }
            catch (ConfigurationPersistenceException exception)
                when (exception.Error == ConfigurationPersistenceError.RevisionConflict)
            {
                // Retry persistence, not the mutation: a newer active revision now owns the saved document.
            }
        }
    }

    private static MergeAnalysis AnalyzeMerge(
        IReadOnlyList<MockEndpointDefinition> activeEndpoints,
        IReadOnlyList<MockEndpointDefinition> builtInEndpoints)
    {
        var additions = new List<MockEndpointDefinition>();
        var changed = new List<MockEndpointDefinition>();
        var identical = new List<MockEndpointDefinition>();
        var conflicts = new List<BuiltInMergeConflict>();
        foreach (var builtIn in builtInEndpoints)
        {
            var sameId = activeEndpoints.FirstOrDefault(active => active.Id == builtIn.Id);
            var isIdentical = sameId is not null && EndpointsEqual(sameId, builtIn);
            if (isIdentical)
            {
                identical.Add(builtIn);
            }
            else if (sameId is not null)
            {
                changed.Add(builtIn);
                conflicts.Add(new BuiltInMergeConflict(
                    builtIn.Id,
                    builtIn.Name,
                    "different",
                    sameId.Id,
                    sameId.Name));
            }

            var routeCollisions = activeEndpoints.Where(active =>
                active.Id != builtIn.Id && RoutesOverlap(active, builtIn)).ToArray();
            if (routeCollisions.Length != 0 && sameId is null)
            {
                changed.Add(builtIn);
            }

            foreach (var collision in routeCollisions)
            {
                conflicts.Add(new BuiltInMergeConflict(
                    builtIn.Id,
                    builtIn.Name,
                    "routeCollision",
                    collision.Id,
                    collision.Name));
            }

            if (sameId is null && routeCollisions.Length == 0)
            {
                additions.Add(builtIn);
            }
        }

        return new MergeAnalysis(additions, changed, identical, conflicts);
    }

    private static bool EndpointsEqual(MockEndpointDefinition left, MockEndpointDefinition right) =>
        left.Id == right.Id &&
        string.Equals(left.Name, right.Name, StringComparison.Ordinal) &&
        string.Equals(left.Description, right.Description, StringComparison.Ordinal) &&
        left.Enabled == right.Enabled &&
        left.Methods.SequenceEqual(right.Methods, StringComparer.Ordinal) &&
        string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
        left.RequestCount == right.RequestCount &&
        ResponsesEqual(left.Response, right.Response);

    private static bool ResponsesEqual(MockResponseDefinition left, MockResponseDefinition right) =>
        left.Behavior == right.Behavior &&
        left.StatusCode == right.StatusCode &&
        string.Equals(left.ReasonPhrase, right.ReasonPhrase, StringComparison.Ordinal) &&
        HeadersEqual(left.Headers, right.Headers) &&
        string.Equals(left.ContentType, right.ContentType, StringComparison.Ordinal) &&
        string.Equals(left.Body, right.Body, StringComparison.Ordinal);

    private static bool HeadersEqual(
        IReadOnlyDictionary<string, string[]> left,
        IReadOnlyDictionary<string, string[]> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (name, values) in left)
        {
            if (!right.TryGetValue(name, out var rightValues) ||
                !values.SequenceEqual(rightValues, StringComparer.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RoutesOverlap(MockEndpointDefinition left, MockEndpointDefinition right) =>
        string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
        left.Methods.Any(leftMethod => right.Methods.Contains(leftMethod, StringComparer.OrdinalIgnoreCase));

    internal static bool ShouldRetain(
        MockEndpointDefinition active,
        IReadOnlySet<Guid> builtInIds,
        IReadOnlyList<MockEndpointDefinition> builtInEndpoints,
        bool force) =>
        !builtInIds.Contains(active.Id) &&
        (!force || !builtInEndpoints.Any(builtInEndpoint => RoutesOverlap(active, builtInEndpoint)));

    private sealed record MergeAnalysis(
        IReadOnlyList<MockEndpointDefinition> Additions,
        IReadOnlyList<MockEndpointDefinition> Changed,
        IReadOnlyList<MockEndpointDefinition> Identical,
        IReadOnlyList<BuiltInMergeConflict> Conflicts);
}

/// <summary>Identifies the outcome of a built-in endpoint merge.</summary>
public enum BuiltInMergeStatus
{
    /// <summary>The merged candidate was atomically activated.</summary>
    Applied,
    /// <summary>Every built-in endpoint was already identical and no revision was created.</summary>
    NoChanges,
    /// <summary>Divergent definitions or route collisions require explicit force confirmation.</summary>
    Conflict,
    /// <summary>The merged candidate failed complete-document validation.</summary>
    ValidationFailed,
    /// <summary>The expected revision was stale.</summary>
    RevisionConflict
}

/// <summary>Reports an attempted built-in endpoint merge.</summary>
/// <param name="Status">The merge outcome.</param>
/// <param name="Snapshot">The resulting or conflict-preview snapshot when available.</param>
/// <param name="Added">The number of missing built-in endpoints added.</param>
/// <param name="Updated">The number of divergent built-in endpoints replaced.</param>
/// <param name="Skipped">The number of identical built-in endpoints left unchanged.</param>
/// <param name="Conflicts">The detected same-ID differences and route collisions.</param>
/// <param name="Validation">Validation errors for a failed merged candidate.</param>
public sealed record BuiltInMergeResult(
    BuiltInMergeStatus Status,
    ConfigurationStateSnapshot? Snapshot,
    int Added,
    int Updated,
    int Skipped,
    IReadOnlyList<BuiltInMergeConflict> Conflicts,
    ConfigurationValidationResult Validation)
{
    /// <summary>Creates a successful applied merge result.</summary>
    /// <param name="snapshot">The atomically published merged snapshot.</param>
    /// <param name="added">The number of endpoints added.</param>
    /// <param name="updated">The number of endpoints replaced.</param>
    /// <param name="skipped">The number of identical endpoints skipped.</param>
    /// <param name="conflicts">Conflicts resolved by a forced merge.</param>
    /// <returns>An applied result.</returns>
    public static BuiltInMergeResult Applied(
        ConfigurationStateSnapshot snapshot,
        int added,
        int updated,
        int skipped,
        IReadOnlyList<BuiltInMergeConflict> conflicts) =>
        new(BuiltInMergeStatus.Applied, snapshot, added, updated, skipped, conflicts, new([]));

    /// <summary>Creates a no-change result for an idempotent merge.</summary>
    /// <param name="snapshot">The unchanged active snapshot.</param>
    /// <param name="skipped">The number of identical endpoints skipped.</param>
    /// <returns>A no-change result.</returns>
    public static BuiltInMergeResult NoChanges(ConfigurationStateSnapshot snapshot, int skipped) =>
        new(BuiltInMergeStatus.NoChanges, snapshot, 0, 0, skipped, [], new([]));

    /// <summary>Creates a conflict-preview result without changing active state.</summary>
    /// <param name="snapshot">The unchanged active snapshot.</param>
    /// <param name="skipped">The number of identical endpoints skipped.</param>
    /// <param name="conflicts">The differences and route collisions requiring confirmation.</param>
    /// <returns>A conflict result.</returns>
    public static BuiltInMergeResult Conflict(
        ConfigurationStateSnapshot snapshot,
        int skipped,
        IReadOnlyList<BuiltInMergeConflict> conflicts) =>
        new(BuiltInMergeStatus.Conflict, snapshot, 0, 0, skipped, conflicts, new([]));

    /// <summary>Creates a result for a merged candidate that failed validation.</summary>
    /// <param name="validation">The complete validation result.</param>
    /// <returns>A validation-failed result.</returns>
    public static BuiltInMergeResult ValidationFailed(ConfigurationValidationResult validation) =>
        new(BuiltInMergeStatus.ValidationFailed, null, 0, 0, 0, [], validation);

    /// <summary>Creates a stale-revision result.</summary>
    /// <returns>A revision-conflict result.</returns>
    public static BuiltInMergeResult RevisionConflict() =>
        new(BuiltInMergeStatus.RevisionConflict, null, 0, 0, 0, [], new([]));
}
