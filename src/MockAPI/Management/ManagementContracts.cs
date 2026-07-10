using System.Text.Json.Serialization;
using MockAPI.Configuration;

namespace MockAPI.Management;

public sealed record ConfigurationStatusResponse(
    long Revision,
    string ETag,
    bool HasUnsavedChanges);

public sealed record EndpointEnabledRequest(bool Enabled);

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
[JsonSerializable(typeof(ManagementProblemDetails))]
[JsonSerializable(typeof(MockEndpointDefinition))]
[JsonSerializable(typeof(MockEndpointDefinition[]))]
public sealed partial class ManagementJsonContext : JsonSerializerContext;