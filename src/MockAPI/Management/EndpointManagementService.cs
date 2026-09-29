using MockAPI.Configuration;

namespace MockAPI.Management;

/// <summary>Applies revision-protected endpoint mutations through the atomic configuration state.</summary>
/// <param name="state">The configuration state that owns endpoint definitions.</param>
public sealed class EndpointManagementService(ConfigurationState state)
{
    /// <summary>Gets the immutable active configuration snapshot.</summary>
    public ConfigurationStateSnapshot Current => state.Current;

    /// <summary>Adds one endpoint to the complete configuration.</summary>
    /// <param name="endpoint">The endpoint definition, including its new stable ID.</param>
    /// <param name="expectedRevision">The active revision required by the operation.</param>
    /// <returns>An atomic operation result; failure leaves the active configuration unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public ManagementOperationResult Create(
        MockEndpointDefinition endpoint,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ManagementOperationResult.RevisionConflict(snapshot);
        }

        var document = snapshot.GetDocument();
        if (document.Endpoints.Any(candidate => candidate.Id == endpoint.Id))
        {
            return ManagementOperationResult.AlreadyExists(snapshot);
        }

        return Apply(
            document with { Endpoints = [.. document.Endpoints, endpoint] },
            expectedRevision,
            endpoint.Id);
    }

    /// <summary>Replaces an endpoint while preserving its stable route identifier.</summary>
    /// <param name="endpointId">The endpoint ID addressed by the request path.</param>
    /// <param name="endpoint">The complete replacement, whose ID must equal <paramref name="endpointId"/>.</param>
    /// <param name="expectedRevision">The active revision required by the operation.</param>
    /// <returns>An atomic operation result; failure leaves the active configuration unchanged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public ManagementOperationResult Replace(
        Guid endpointId,
        MockEndpointDefinition endpoint,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ManagementOperationResult.RevisionConflict(snapshot);
        }

        if (endpoint.Id != endpointId)
        {
            return ManagementOperationResult.IdMismatch(snapshot);
        }

        var document = snapshot.GetDocument();
        var index = FindEndpoint(document, endpointId);
        if (index < 0)
        {
            return ManagementOperationResult.NotFound(snapshot);
        }

        var endpoints = document.Endpoints.ToArray();
        endpoints[index] = endpoint;
        return Apply(document with { Endpoints = endpoints }, expectedRevision, endpointId);
    }

    /// <summary>Changes whether one endpoint participates in request matching.</summary>
    /// <param name="endpointId">The stable endpoint ID.</param>
    /// <param name="enabled">The desired enabled state.</param>
    /// <param name="expectedRevision">The active revision required by the operation.</param>
    /// <returns>An atomic operation result.</returns>
    public ManagementOperationResult SetEnabled(
        Guid endpointId,
        bool enabled,
        long expectedRevision)
    {
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ManagementOperationResult.RevisionConflict(snapshot);
        }

        var document = snapshot.GetDocument();
        var index = FindEndpoint(document, endpointId);
        if (index < 0)
        {
            return ManagementOperationResult.NotFound(snapshot);
        }

        var endpoints = document.Endpoints.ToArray();
        endpoints[index] = endpoints[index] with { Enabled = enabled };
        return Apply(document with { Endpoints = endpoints }, expectedRevision, endpointId);
    }

    /// <summary>Deletes one endpoint by stable ID.</summary>
    /// <param name="endpointId">The endpoint ID to delete.</param>
    /// <param name="expectedRevision">The active revision required by the operation.</param>
    /// <returns>An atomic operation result.</returns>
    public ManagementOperationResult Delete(Guid endpointId, long expectedRevision)
    {
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ManagementOperationResult.RevisionConflict(snapshot);
        }

        var document = snapshot.GetDocument();
        var index = FindEndpoint(document, endpointId);
        if (index < 0)
        {
            return ManagementOperationResult.NotFound(snapshot);
        }

        var endpoints = document.Endpoints.Where(endpoint => endpoint.Id != endpointId).ToArray();
        var update = state.TryReplace(document with { Endpoints = endpoints }, expectedRevision);
        return FromUpdate(update, endpoint: null);
    }

    /// <summary>Enables, disables, or deletes a non-empty endpoint set as one configuration revision.</summary>
    /// <param name="endpointIds">The stable IDs that must all exist in the captured snapshot.</param>
    /// <param name="operation">The mutation applied to every selected endpoint.</param>
    /// <param name="expectedRevision">The active revision required by the operation.</param>
    /// <returns>An atomic operation result; missing IDs or validation failure apply no subset.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpointIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> is not supported.</exception>
    public ManagementOperationResult ApplyBulk(
        IReadOnlyCollection<Guid> endpointIds,
        BulkEndpointOperation operation,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(endpointIds);
        var snapshot = state.Current;
        if (snapshot.Revision != expectedRevision)
        {
            return ManagementOperationResult.RevisionConflict(snapshot);
        }

        var document = snapshot.GetDocument();
        var selectedIds = endpointIds.ToHashSet();
        var availableIds = document.Endpoints.Select(endpoint => endpoint.Id).ToHashSet();
        if (selectedIds.Count == 0 || selectedIds.IsSubsetOf(availableIds) is false)
        {
            return ManagementOperationResult.NotFound(snapshot);
        }

        var endpoints = operation switch
        {
            BulkEndpointOperation.Enable => document.Endpoints
                .Select(endpoint => selectedIds.Contains(endpoint.Id) ? endpoint with { Enabled = true } : endpoint)
                .ToArray(),
            BulkEndpointOperation.Disable => document.Endpoints
                .Select(endpoint => selectedIds.Contains(endpoint.Id) ? endpoint with { Enabled = false } : endpoint)
                .ToArray(),
            BulkEndpointOperation.Delete => document.Endpoints
                .Where(endpoint => selectedIds.Contains(endpoint.Id) is false)
                .ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported bulk endpoint operation."),
        };

        var update = state.TryReplace(document with { Endpoints = endpoints }, expectedRevision);
        return FromUpdate(update, endpoint: null);
    }

    private ManagementOperationResult Apply(
        MockApiConfigurationDocument document,
        long expectedRevision,
        Guid endpointId)
    {
        var update = state.TryReplace(document, expectedRevision);
        if (update.Status != ConfigurationUpdateStatus.Applied)
        {
            return FromUpdate(update, endpoint: null);
        }

        var endpoint = update.Snapshot!.GetDocument().Endpoints.Single(candidate => candidate.Id == endpointId);
        return ManagementOperationResult.Applied(update.Snapshot, endpoint);
    }

    private static ManagementOperationResult FromUpdate(
        ConfigurationUpdateResult update,
        MockEndpointDefinition? endpoint)
    {
        if (update.Status == ConfigurationUpdateStatus.Applied)
        {
            return ManagementOperationResult.Applied(update.Snapshot!, endpoint);
        }

        return update.Status == ConfigurationUpdateStatus.ValidationFailed
            ? ManagementOperationResult.ValidationFailed(update.Validation)
            : ManagementOperationResult.RevisionConflict(snapshot: null);
    }

    private static int FindEndpoint(MockApiConfigurationDocument document, Guid endpointId)
    {
        for (var index = 0; index < document.Endpoints.Count; index++)
        {
            if (document.Endpoints[index].Id == endpointId)
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>Identifies the outcome of an endpoint management mutation.</summary>
public enum ManagementOperationStatus
{
    /// <summary>The complete updated configuration was atomically published.</summary>
    Applied,
    /// <summary>The updated configuration failed validation.</summary>
    ValidationFailed,
    /// <summary>The expected revision was stale.</summary>
    RevisionConflict,
    /// <summary>One or more addressed endpoint IDs were absent.</summary>
    NotFound,
    /// <summary>A create operation reused an existing stable endpoint ID.</summary>
    AlreadyExists,
    /// <summary>A replacement body ID differed from the addressed endpoint ID.</summary>
    IdMismatch
}

/// <summary>Reports the outcome of an endpoint management mutation.</summary>
/// <param name="Status">The operation outcome.</param>
/// <param name="Snapshot">The resulting or relevant active snapshot when available.</param>
/// <param name="Endpoint">The created or replaced endpoint for an applied single-endpoint operation.</param>
/// <param name="Validation">Validation errors when the candidate configuration was rejected.</param>
public sealed record ManagementOperationResult(
    ManagementOperationStatus Status,
    ConfigurationStateSnapshot? Snapshot,
    MockEndpointDefinition? Endpoint,
    ConfigurationValidationResult? Validation)
{
    internal static ManagementOperationResult Applied(
        ConfigurationStateSnapshot snapshot,
        MockEndpointDefinition? endpoint) =>
        new(ManagementOperationStatus.Applied, snapshot, endpoint, null);

    internal static ManagementOperationResult ValidationFailed(ConfigurationValidationResult validation) =>
        new(ManagementOperationStatus.ValidationFailed, null, null, validation);

    internal static ManagementOperationResult RevisionConflict(ConfigurationStateSnapshot? snapshot) =>
        new(ManagementOperationStatus.RevisionConflict, snapshot, null, null);

    internal static ManagementOperationResult NotFound(ConfigurationStateSnapshot snapshot) =>
        new(ManagementOperationStatus.NotFound, snapshot, null, null);

    internal static ManagementOperationResult AlreadyExists(ConfigurationStateSnapshot snapshot) =>
        new(ManagementOperationStatus.AlreadyExists, snapshot, null, null);

    internal static ManagementOperationResult IdMismatch(ConfigurationStateSnapshot snapshot) =>
        new(ManagementOperationStatus.IdMismatch, snapshot, null, null);
}
