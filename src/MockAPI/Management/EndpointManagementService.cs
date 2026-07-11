using MockAPI.Configuration;

namespace MockAPI.Management;

public sealed class EndpointManagementService(ConfigurationState state)
{
    public ConfigurationStateSnapshot Current => state.Current;

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
        MockEndpointDefinition? endpoint) =>
        update.Status switch
        {
            ConfigurationUpdateStatus.Applied =>
                ManagementOperationResult.Applied(update.Snapshot!, endpoint),
            ConfigurationUpdateStatus.ValidationFailed =>
                ManagementOperationResult.ValidationFailed(update.Validation),
            ConfigurationUpdateStatus.RevisionConflict =>
                ManagementOperationResult.RevisionConflict(snapshot: null),
            _ => throw new ArgumentOutOfRangeException(nameof(update), update.Status, null)
        };

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

public enum ManagementOperationStatus
{
    Applied,
    ValidationFailed,
    RevisionConflict,
    NotFound,
    AlreadyExists,
    IdMismatch
}

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
