using System.Text.Json.Serialization;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Management;

/// <summary>Reports the active configuration revision and persistence state.</summary>
/// <param name="Revision">The active in-process revision.</param>
/// <param name="ETag">The quoted strong entity tag serialized as <c>etag</c>.</param>
/// <param name="HasUnsavedChanges">Whether the active revision has not been confirmed persisted.</param>
/// <param name="ApiDescriptions">Optional case-sensitive path-based descriptions from this same revision.</param>
public sealed record ConfigurationStatusResponse(
    long Revision,
    [property: JsonPropertyName("etag")] string ETag,
    bool HasUnsavedChanges,
    IReadOnlyDictionary<string, string>? ApiDescriptions = null)
{
    internal static ConfigurationStatusResponse FromSnapshot(ConfigurationStateSnapshot snapshot) =>
        new(snapshot.Revision, snapshot.ETag, snapshot.HasUnsavedChanges, snapshot.GetDocument().ApiDescriptions);
}

/// <summary>Requests a revision-protected API description edit, independent of endpoint membership.</summary>
public sealed record ApiDescriptionRequest
{
    /// <summary>Gets the case-sensitive first-segment path, or <c>/</c> for root endpoints.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the freeform description; an empty string deliberately clears the displayed description.</summary>
    public required string Description { get; init; }
}

/// <summary>Requests an endpoint enabled-state change.</summary>
/// <param name="Enabled">The desired enabled state.</param>
public sealed record EndpointEnabledRequest(bool Enabled);

/// <summary>Requests one atomic mutation across a set of stable endpoint IDs.</summary>
public sealed record BulkEndpointRequest
{
    /// <summary>Gets the non-empty endpoint ID set; every ID must exist.</summary>
    public required IReadOnlyList<Guid> EndpointIds { get; init; }

    /// <summary>Gets the operation applied to every selected endpoint.</summary>
    public required BulkEndpointOperation Operation { get; init; }
}

/// <summary>Specifies an atomic bulk endpoint mutation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BulkEndpointOperation>))]
public enum BulkEndpointOperation
{
    /// <summary>Enables every selected endpoint.</summary>
    [JsonStringEnumMemberName("enable")]
    Enable,

    /// <summary>Disables every selected endpoint.</summary>
    [JsonStringEnumMemberName("disable")]
    Disable,

    /// <summary>Deletes every selected endpoint.</summary>
    [JsonStringEnumMemberName("delete")]
    Delete
}

/// <summary>Reports complete candidate validation without applying the candidate.</summary>
/// <param name="IsValid">Whether no validation errors were found.</param>
/// <param name="Errors">All validation errors in deterministic discovery order.</param>
public sealed record ConfigurationValidationResponse(
    bool IsValid,
    IReadOnlyList<ConfigurationValidationError> Errors);

/// <summary>Reports the outcome of persisting a configuration snapshot.</summary>
/// <param name="Revision">The revision written to persistence.</param>
/// <param name="IsCurrentRevision">Whether the written revision remained current and was marked persisted.</param>
public sealed record ConfigurationSaveResponse(
    long Revision,
    bool IsCurrentRevision);

/// <summary>Describes a built-in endpoint difference or route collision.</summary>
/// <param name="BuiltInEndpointId">The stable ID of the built-in endpoint.</param>
/// <param name="BuiltInName">The built-in endpoint name.</param>
/// <param name="Kind">The machine-readable conflict kind: <c>different</c> or <c>routeCollision</c>.</param>
/// <param name="ExistingEndpointId">The stable ID of the conflicting active endpoint.</param>
/// <param name="ExistingName">The conflicting active endpoint name.</param>
public sealed record BuiltInMergeConflict(
    Guid BuiltInEndpointId,
    string BuiltInName,
    string Kind,
    Guid ExistingEndpointId,
    string ExistingName);

/// <summary>Reports a built-in merge application or conflict preview.</summary>
/// <param name="Applied">Whether the operation created and activated a new revision.</param>
/// <param name="Forced">Whether explicit conflict replacement was requested.</param>
/// <param name="Revision">The resulting or unchanged active revision.</param>
/// <param name="ETag">The quoted strong entity tag serialized as <c>etag</c>.</param>
/// <param name="HasUnsavedChanges">Whether the reported revision has not been confirmed persisted.</param>
/// <param name="Added">The number of missing built-in endpoints added.</param>
/// <param name="Updated">The number of divergent built-in endpoints replaced.</param>
/// <param name="Skipped">The number of identical built-in endpoints skipped.</param>
/// <param name="Conflicts">Detected differences and route collisions.</param>
public sealed record BuiltInMergeResponse(
    bool Applied,
    bool Forced,
    long Revision,
    [property: JsonPropertyName("etag")] string ETag,
    bool HasUnsavedChanges,
    int Added,
    int Updated,
    int Skipped,
    IReadOnlyList<BuiltInMergeConflict> Conflicts);

/// <summary>Reports a process health or readiness state.</summary>
/// <param name="Status">The stable health status string returned on the wire.</param>
public sealed record HealthStatusResponse(string Status);

/// <summary>Represents an RFC 9457-style management API problem response.</summary>
/// <param name="Type">The problem type URI.</param>
/// <param name="Title">A concise problem category.</param>
/// <param name="Status">The HTTP status code for this occurrence.</param>
/// <param name="Detail">A safe operator-facing explanation.</param>
/// <param name="Instance">The request path identifying this occurrence.</param>
/// <param name="Errors">Optional structured configuration validation errors.</param>
public sealed record ManagementProblemDetails(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    IReadOnlyList<ConfigurationValidationError>? Errors = null);

/// <summary>Provides trimming-safe JSON metadata for management API request and response contracts.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ConfigurationStatusResponse))]
[JsonSerializable(typeof(ApiSecurityStatus))]
[JsonSerializable(typeof(ApiSecurityRequest))]
[JsonSerializable(typeof(ApiKeyCreated))]
[JsonSerializable(typeof(ApiDescriptionRequest))]
[JsonSerializable(typeof(EndpointEnabledRequest))]
[JsonSerializable(typeof(BulkEndpointRequest))]
[JsonSerializable(typeof(ConfigurationValidationResponse))]
[JsonSerializable(typeof(ConfigurationSaveResponse))]
[JsonSerializable(typeof(BuiltInMergeResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(ManagementProblemDetails))]
[JsonSerializable(typeof(RequestStatisticsSnapshot))]
[JsonSerializable(typeof(MockEndpointDefinition))]
[JsonSerializable(typeof(MockEndpointDefinition[]))]
// OpenAPI also needs metadata for query parameters, not just JSON request and response bodies.
[JsonSerializable(typeof(bool?))]
public sealed partial class ManagementJsonContext : JsonSerializerContext;
