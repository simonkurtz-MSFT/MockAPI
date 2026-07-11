using System.Text.Json.Serialization;

namespace MockAPI.Configuration;

public sealed record MockApiConfigurationDocument
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    public required string SchemaVersion { get; init; }

    public required IReadOnlyList<MockEndpointDefinition> Endpoints { get; init; }
}

public sealed record MockEndpointDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool Enabled { get; init; }

    public required IReadOnlyList<string> Methods { get; init; }

    public required string Path { get; init; }

    public required MockResponseDefinition Response { get; init; }
}

public sealed record MockResponseDefinition
{
    public required int StatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public required Dictionary<string, string[]> Headers { get; init; }

    public string? ContentType { get; init; }

    public required string Body { get; init; }
}
