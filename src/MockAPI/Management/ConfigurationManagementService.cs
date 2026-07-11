using System.Text.Json;
using MockAPI.Configuration;

namespace MockAPI.Management;

public sealed class ConfigurationManagementService(
    ConfigurationState state,
    ConfigurationFileStore store)
{
    public ConfigurationStateSnapshot Current => state.Current;

    public ConfigurationValidationResult Validate(MockApiConfigurationDocument candidate) =>
        ConfigurationValidator.Validate(candidate);

    public ConfigurationUpdateResult Import(
        MockApiConfigurationDocument candidate,
        long expectedRevision) =>
        state.TryReplace(candidate, expectedRevision);

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
        var conflicts = FindConflicts(current.Endpoints, builtIn.Endpoints);
        var skipped = builtIn.Endpoints.Count(endpoint =>
            current.Endpoints.Any(active => active.Id == endpoint.Id && EndpointsEqual(active, endpoint)));

        if (conflicts.Count != 0 && !force)
        {
            return BuiltInMergeResult.Conflict(snapshot, skipped, conflicts);
        }

        var builtInIds = builtIn.Endpoints.Select(endpoint => endpoint.Id).ToHashSet();
        var retained = current.Endpoints.Where(active =>
            !builtInIds.Contains(active.Id) &&
            (!force || !builtIn.Endpoints.Any(builtInEndpoint => RoutesOverlap(active, builtInEndpoint))));
        var additions = builtIn.Endpoints.Where(builtInEndpoint =>
            !current.Endpoints.Any(active => active.Id == builtInEndpoint.Id || RoutesOverlap(active, builtInEndpoint)));
        var changed = builtIn.Endpoints.Where(builtInEndpoint =>
            current.Endpoints.Any(active =>
                (active.Id == builtInEndpoint.Id && !EndpointsEqual(active, builtInEndpoint)) ||
                (active.Id != builtInEndpoint.Id && RoutesOverlap(active, builtInEndpoint))));
        var identical = builtIn.Endpoints.Where(builtInEndpoint =>
            current.Endpoints.Any(active => active.Id == builtInEndpoint.Id && EndpointsEqual(active, builtInEndpoint)));

        var mergedEndpoints = force
            ? retained.Concat(builtIn.Endpoints).ToArray()
            : current.Endpoints.Concat(additions).ToArray();
        if (mergedEndpoints.Length == current.Endpoints.Count &&
            mergedEndpoints.Zip(current.Endpoints).All(pair => EndpointsEqual(pair.First, pair.Second)))
        {
            return BuiltInMergeResult.NoChanges(snapshot, skipped);
        }

        var candidate = new MockApiConfigurationDocument
        {
            Schema = current.Schema,
            SchemaVersion = current.SchemaVersion,
            Endpoints = mergedEndpoints
        };
        var update = state.TryReplace(candidate, expectedRevision);
        return update.Status switch
        {
            ConfigurationUpdateStatus.Applied => BuiltInMergeResult.Applied(
                update.Snapshot!,
                additions.Count(),
                force ? changed.Count() : 0,
                identical.Count(),
                conflicts),
            ConfigurationUpdateStatus.ValidationFailed => BuiltInMergeResult.ValidationFailed(update.Validation),
            _ => BuiltInMergeResult.RevisionConflict()
        };
    }

    public Task<ConfigurationSaveResult> SaveAsync(
        long expectedRevision,
        CancellationToken cancellationToken) =>
        store.SaveAsync(state, expectedRevision, cancellationToken);

    private static IReadOnlyList<BuiltInMergeConflict> FindConflicts(
        IReadOnlyList<MockEndpointDefinition> activeEndpoints,
        IReadOnlyList<MockEndpointDefinition> builtInEndpoints)
    {
        var conflicts = new List<BuiltInMergeConflict>();
        foreach (var builtIn in builtInEndpoints)
        {
            var sameId = activeEndpoints.FirstOrDefault(active => active.Id == builtIn.Id);
            if (sameId is not null && !EndpointsEqual(sameId, builtIn))
            {
                conflicts.Add(new BuiltInMergeConflict(
                    builtIn.Id,
                    builtIn.Name,
                    "different",
                    sameId.Id,
                    sameId.Name));
            }

            foreach (var collision in activeEndpoints.Where(active =>
                         active.Id != builtIn.Id && RoutesOverlap(active, builtIn)))
            {
                conflicts.Add(new BuiltInMergeConflict(
                    builtIn.Id,
                    builtIn.Name,
                    "routeCollision",
                    collision.Id,
                    collision.Name));
            }
        }

        return conflicts;
    }

    private static bool EndpointsEqual(MockEndpointDefinition left, MockEndpointDefinition right) =>
        JsonSerializer.SerializeToUtf8Bytes(left, ManagementJsonContext.Default.MockEndpointDefinition)
            .AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(
                right,
                ManagementJsonContext.Default.MockEndpointDefinition));

    private static bool RoutesOverlap(MockEndpointDefinition left, MockEndpointDefinition right) =>
        string.Equals(left.Path, right.Path, StringComparison.Ordinal) &&
        left.Methods.Any(leftMethod => right.Methods.Contains(leftMethod, StringComparer.OrdinalIgnoreCase));
}

public enum BuiltInMergeStatus
{
    Applied,
    NoChanges,
    Conflict,
    ValidationFailed,
    RevisionConflict
}

public sealed record BuiltInMergeResult(
    BuiltInMergeStatus Status,
    ConfigurationStateSnapshot? Snapshot,
    int Added,
    int Updated,
    int Skipped,
    IReadOnlyList<BuiltInMergeConflict> Conflicts,
    ConfigurationValidationResult Validation)
{
    public static BuiltInMergeResult Applied(
        ConfigurationStateSnapshot snapshot,
        int added,
        int updated,
        int skipped,
        IReadOnlyList<BuiltInMergeConflict> conflicts) =>
        new(BuiltInMergeStatus.Applied, snapshot, added, updated, skipped, conflicts, new([]));

    public static BuiltInMergeResult NoChanges(ConfigurationStateSnapshot snapshot, int skipped) =>
        new(BuiltInMergeStatus.NoChanges, snapshot, 0, 0, skipped, [], new([]));

    public static BuiltInMergeResult Conflict(
        ConfigurationStateSnapshot snapshot,
        int skipped,
        IReadOnlyList<BuiltInMergeConflict> conflicts) =>
        new(BuiltInMergeStatus.Conflict, snapshot, 0, 0, skipped, conflicts, new([]));

    public static BuiltInMergeResult ValidationFailed(ConfigurationValidationResult validation) =>
        new(BuiltInMergeStatus.ValidationFailed, null, 0, 0, 0, [], validation);

    public static BuiltInMergeResult RevisionConflict() =>
        new(BuiltInMergeStatus.RevisionConflict, null, 0, 0, 0, [], new([]));
}