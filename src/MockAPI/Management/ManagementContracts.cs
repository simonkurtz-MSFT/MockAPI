using System.Text.Json.Serialization;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Management;

public sealed record ConfigurationStatusResponse(
    long Revision,
    [property: JsonPropertyName("etag")] string ETag,
    bool HasUnsavedChanges);

public sealed record EndpointEnabledRequest(bool Enabled);

public sealed record ConfigurationValidationResponse(
    bool IsValid,
    IReadOnlyList<ConfigurationValidationError> Errors);

public sealed record ConfigurationSaveResponse(
    long Revision,
    bool IsCurrentRevision);

public sealed record BuiltInMergeConflict(
    Guid BuiltInEndpointId,
    string BuiltInName,
    string Kind,
    Guid ExistingEndpointId,
    string ExistingName);

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

public sealed record HealthStatusResponse(string Status);

public sealed record ManagementProblemDetails(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    IReadOnlyList<ConfigurationValidationError>? Errors = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ConfigurationStatusResponse))]
[JsonSerializable(typeof(EndpointEnabledRequest))]
[JsonSerializable(typeof(ConfigurationValidationResponse))]
[JsonSerializable(typeof(ConfigurationSaveResponse))]
[JsonSerializable(typeof(BuiltInMergeResponse))]
[JsonSerializable(typeof(HealthStatusResponse))]
[JsonSerializable(typeof(ManagementProblemDetails))]
[JsonSerializable(typeof(RequestStatisticsSnapshot))]
[JsonSerializable(typeof(MockEndpointDefinition))]
[JsonSerializable(typeof(MockEndpointDefinition[]))]
public sealed partial class ManagementJsonContext : JsonSerializerContext;
