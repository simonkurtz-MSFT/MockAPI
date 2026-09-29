using System.Text.Json.Serialization;

namespace MockAPI.Configuration;

/// <summary>Represents one complete, versioned MockAPI configuration document.</summary>
/// <remarks>The document is validated and activated as a unit; no subset is applied on failure.</remarks>
public sealed record MockApiConfigurationDocument
{
    /// <summary>Gets the optional JSON Schema reference serialized as <c>$schema</c>.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>Gets the configuration contract version. Version 1 documents use <c>1.0</c>.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Gets optional descriptions keyed by case-sensitive first-segment paths such as <c>/ex</c> or <c>/</c>.</summary>
    /// <remarks>Metadata is independent of endpoint membership and survives deletion of a group's last endpoint.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? ApiDescriptions { get; init; }

    /// <summary>Gets the endpoint definitions in their deterministic serialization order.</summary>
    public required IReadOnlyList<MockEndpointDefinition> Endpoints { get; init; }
}

/// <summary>Defines a mock endpoint identified by a stable ID and matched by HTTP method and exact path.</summary>
public sealed record MockEndpointDefinition
{
    /// <summary>Gets the stable endpoint identifier used for edits and statistics attribution.</summary>
    public required Guid Id { get; init; }

    /// <summary>Gets the operator-facing endpoint name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets optional operator-facing descriptive text.</summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the endpoint participates in request matching.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Gets the HTTP methods matched case-insensitively by this endpoint.</summary>
    public required IReadOnlyList<string> Methods { get; init; }

    /// <summary>Gets the literal request path matched case-sensitively, excluding the query string.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the optional number of requests preselected when testing this endpoint in the dashboard.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RequestCount { get; init; }

    /// <summary>Gets the response or connection-abort behavior for a matched request.</summary>
    public required MockResponseDefinition Response { get; init; }
}

/// <summary>Defines the terminal response, connection behavior, and optional rate limit for an endpoint.</summary>
public sealed record MockResponseDefinition
{
    /// <summary>Gets the behavior applied to a matched request.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<MockResponseBehavior>))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MockResponseBehavior Behavior { get; init; }

    /// <summary>Gets the terminal HTTP status code, or <see langword="null"/> for connection-abort behavior.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Gets the optional HTTP/1.x reason phrase; HTTP/2 and HTTP/3 do not transmit it.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>Gets response header names mapped to ordered values for repeated-field wire semantics.</summary>
    public required Dictionary<string, string[]> Headers { get; init; }

    /// <summary>Gets the optional response media type written to the <c>Content-Type</c> header.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the response body encoded as UTF-8 without JSON interpretation.</summary>
    public required string Body { get; init; }

    /// <summary>Gets the optional sliding-window rate limit that selects a success response before the terminal response.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MockRateLimitDefinition? RateLimit { get; init; }
}

/// <summary>Defines a per-endpoint sliding-window request limit and its permitted-request response.</summary>
public sealed record MockRateLimitDefinition
{
    /// <summary>Gets the maximum permits available during one configured window.</summary>
    public required int RequestLimit { get; init; }

    /// <summary>Gets the sliding-window duration in seconds.</summary>
    public required int WindowSeconds { get; init; }

    /// <summary>Gets the response returned while a permit is available.</summary>
    public required MockSuccessResponseDefinition SuccessResponse { get; init; }
}

/// <summary>Defines the response returned for a rate-limited endpoint while a permit is available.</summary>
public sealed record MockSuccessResponseDefinition
{
    /// <summary>Gets the HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Gets the optional HTTP/1.x reason phrase; HTTP/2 and HTTP/3 do not transmit it.</summary>
    public string? ReasonPhrase { get; init; }

    /// <summary>Gets response header names mapped to ordered values for repeated-field wire semantics.</summary>
    public required Dictionary<string, string[]> Headers { get; init; }

    /// <summary>Gets the optional response media type written to the <c>Content-Type</c> header.</summary>
    public string? ContentType { get; init; }

    /// <summary>Gets the response body encoded as UTF-8 without JSON interpretation.</summary>
    public required string Body { get; init; }
}

/// <summary>Specifies whether a matched endpoint writes an HTTP response or aborts the connection.</summary>
public enum MockResponseBehavior
{
    /// <summary>Writes the configured HTTP status, headers, and body.</summary>
    [JsonStringEnumMemberName("response")]
    Response,

    /// <summary>Aborts the connection before writing response headers or body bytes.</summary>
    [JsonStringEnumMemberName("abortConnection")]
    AbortConnection
}
