using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Threading.RateLimiting;
using MockAPI.Configuration;

namespace MockAPI.Runtime;

/// <summary>Maintains an atomically replaceable immutable registry of enabled runtime endpoints.</summary>
public sealed class EndpointRegistry
{
    private EndpointRegistrySnapshot _current = EndpointRegistrySnapshot.Empty;

    /// <summary>Gets the immutable registry snapshot currently visible to request readers.</summary>
    public EndpointRegistrySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Validates a complete document and atomically publishes its enabled endpoints.</summary>
    /// <param name="document">The complete configuration document.</param>
    /// <returns>A valid result when published; otherwise, all validation errors and no registry change.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public ConfigurationValidationResult TryReplace(MockApiConfigurationDocument document)
    {
        var validation = ConfigurationValidator.Validate(document);
        if (!validation.IsValid)
        {
            return validation;
        }

        var replacement = EndpointRegistrySnapshot.Create(document);
        Interlocked.Exchange(ref _current, replacement);
        return validation;
    }
}

/// <summary>Provides immutable exact-path endpoint lookup for one validated configuration revision.</summary>
/// <remarks>HTTP methods are compared case-insensitively and paths case-sensitively.</remarks>
public sealed class EndpointRegistrySnapshot
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> _endpointsByMethod;

    private EndpointRegistrySnapshot(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> endpointsByMethod)
    {
        _endpointsByMethod = endpointsByMethod;
    }

    /// <summary>Gets an immutable registry containing no enabled endpoints.</summary>
    public static EndpointRegistrySnapshot Empty { get; } = new(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>>.Empty);

    /// <summary>Looks up an enabled endpoint by exact HTTP method and path.</summary>
    /// <param name="method">The request HTTP method, compared case-insensitively.</param>
    /// <param name="path">The request path without query data, compared case-sensitively.</param>
    /// <param name="endpoint">The matching immutable runtime endpoint when found.</param>
    /// <returns><see langword="true"/> when an enabled endpoint matches both values; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="method"/> or <paramref name="path"/> is <see langword="null"/>.</exception>
    public bool TryGet(string method, string path, out RuntimeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        if (_endpointsByMethod.TryGetValue(method, out var endpointsByPath) &&
            endpointsByPath.TryGetValue(path, out var match))
        {
            endpoint = match;
            return true;
        }

        endpoint = null!;
        return false;
    }

    internal static EndpointRegistrySnapshot Create(MockApiConfigurationDocument document)
    {
        var endpointsByMethod = new Dictionary<string, Dictionary<string, RuntimeEndpoint>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var definition in document.Endpoints)
        {
            if (!definition.Enabled)
            {
                continue;
            }

            var endpoint = RuntimeEndpoint.Create(definition);
            foreach (var method in definition.Methods)
            {
                if (!endpointsByMethod.TryGetValue(method, out var endpointsByPath))
                {
                    endpointsByPath = new Dictionary<string, RuntimeEndpoint>(StringComparer.Ordinal);
                    endpointsByMethod.Add(method, endpointsByPath);
                }

                endpointsByPath.Add(definition.Path, endpoint);
            }
        }

        var frozenEndpoints = endpointsByMethod.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenDictionary(StringComparer.Ordinal),
            StringComparer.OrdinalIgnoreCase);
        return new EndpointRegistrySnapshot(frozenEndpoints);
    }
}

/// <summary>Represents the immutable, wire-ready behavior for one enabled endpoint.</summary>
public sealed class RuntimeEndpoint
{
    private readonly byte[] _body;
    private readonly SlidingWindowRateLimiter? _rateLimiter;
    private readonly RuntimeSuccessResponse? _successResponse;

    private RuntimeEndpoint(
        Guid id,
        MockResponseBehavior behavior,
        int? statusCode,
        string? reasonPhrase,
        ImmutableArray<RuntimeResponseHeader> headers,
        string? contentType,
        byte[] body,
        SlidingWindowRateLimiter? rateLimiter,
        RuntimeSuccessResponse? successResponse)
    {
        Id = id;
        Behavior = behavior;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        ContentType = contentType;
        _body = body;
        _rateLimiter = rateLimiter;
        _successResponse = successResponse;
    }

    /// <summary>Gets the stable endpoint ID used for statistics attribution.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether the endpoint writes a response or aborts the connection.</summary>
    public MockResponseBehavior Behavior { get; }

    /// <summary>Gets the terminal HTTP status code, or <see langword="null"/> for connection-abort behavior.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the optional HTTP/1.x reason phrase.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>Gets the immutable ordered response headers, including repeated values.</summary>
    public ImmutableArray<RuntimeResponseHeader> Headers { get; }

    /// <summary>Gets the optional response media type.</summary>
    public string? ContentType { get; }

    /// <summary>Gets the immutable UTF-8 response body bytes.</summary>
    public ReadOnlyMemory<byte> Body => _body;

    internal RuntimeResponse SelectResponse()
    {
        if (_rateLimiter is null)
        {
            return new RuntimeResponse(StatusCode!.Value, ReasonPhrase, Headers, ContentType, Body);
        }

        _rateLimiter.TryReplenish();
        using var lease = _rateLimiter.AttemptAcquire();
        return lease.IsAcquired
            ? _successResponse!.Value.Response
            : new RuntimeResponse(StatusCode!.Value, ReasonPhrase, Headers, ContentType, Body);
    }

    internal static RuntimeEndpoint Create(MockEndpointDefinition definition)
    {
        var response = definition.Response;
        var headers = response.Headers
            .Select(pair => new RuntimeResponseHeader(pair.Key, [.. pair.Value]))
            .ToImmutableArray();
        var rateLimit = response.RateLimit;
        var limiter = rateLimit is null
            ? null
            : new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = rateLimit.RequestLimit,
                Window = TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                SegmentsPerWindow = Math.Min(rateLimit.WindowSeconds, 10),
                QueueLimit = 0,
                AutoReplenishment = false
            });
        RuntimeSuccessResponse? successResponse = rateLimit is null
            ? null
            : RuntimeSuccessResponse.Create(rateLimit.SuccessResponse);

        return new RuntimeEndpoint(
            definition.Id,
            response.Behavior,
            response.StatusCode,
            response.ReasonPhrase,
            headers,
            response.ContentType,
            Encoding.UTF8.GetBytes(response.Body),
            limiter,
            successResponse);
    }
}

internal readonly record struct RuntimeResponse(
    int StatusCode,
    string? ReasonPhrase,
    ImmutableArray<RuntimeResponseHeader> Headers,
    string? ContentType,
    ReadOnlyMemory<byte> Body);

internal readonly record struct RuntimeSuccessResponse(RuntimeResponse Response)
{
    public static RuntimeSuccessResponse Create(MockSuccessResponseDefinition response) => new(
        new RuntimeResponse(
            response.StatusCode,
            response.ReasonPhrase,
            response.Headers
                .Select(pair => new RuntimeResponseHeader(pair.Key, [.. pair.Value]))
                .ToImmutableArray(),
            response.ContentType,
            Encoding.UTF8.GetBytes(response.Body)));
}

/// <summary>Represents one response header field and its ordered wire values.</summary>
/// <param name="Name">The validated response header name.</param>
/// <param name="Values">The immutable ordered values appended for this header.</param>
public readonly record struct RuntimeResponseHeader(string Name, ImmutableArray<string> Values);
