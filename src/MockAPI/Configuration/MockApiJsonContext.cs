using System.Text.Json.Serialization;

namespace MockAPI.Configuration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MockApiConfigurationDocument))]
public sealed partial class MockApiJsonContext : JsonSerializerContext;