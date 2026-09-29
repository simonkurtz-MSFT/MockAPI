using System.Text.Json.Serialization;

namespace MockAPI.Configuration;

/// <summary>Provides trimming-safe JSON metadata for the persisted MockAPI configuration contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MockApiConfigurationDocument))]
public sealed partial class MockApiJsonContext : JsonSerializerContext;
