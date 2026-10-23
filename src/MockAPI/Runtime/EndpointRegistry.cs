using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Unicode;
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

        var replacement = EndpointRegistrySnapshot.Create(document, Current);
        Interlocked.Exchange(ref _current, replacement);
        return validation;
    }
}

/// <summary>Provides immutable exact-path endpoint lookup for one validated configuration revision.</summary>
/// <remarks>HTTP methods are compared case-insensitively and paths case-sensitively.</remarks>
public sealed class EndpointRegistrySnapshot
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> _endpointsByMethod;
    private readonly FrozenDictionary<Guid, RuntimeEndpoint> _endpointsById;

    private EndpointRegistrySnapshot(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> endpointsByMethod,
        FrozenDictionary<Guid, RuntimeEndpoint> endpointsById)
    {
        _endpointsByMethod = endpointsByMethod;
        _endpointsById = endpointsById;
    }

    /// <summary>Gets an immutable registry containing no enabled endpoints.</summary>
    public static EndpointRegistrySnapshot Empty { get; } = new(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>>.Empty,
        FrozenDictionary<Guid, RuntimeEndpoint>.Empty);

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

    internal static EndpointRegistrySnapshot Create(
        MockApiConfigurationDocument document,
        EndpointRegistrySnapshot previous)
    {
        var endpointsByMethod = new Dictionary<string, Dictionary<string, RuntimeEndpoint>>(
            StringComparer.OrdinalIgnoreCase);
        var endpointsById = new Dictionary<Guid, RuntimeEndpoint>();

        foreach (var definition in document.Endpoints)
        {
            if (!definition.Enabled)
            {
                continue;
            }

            previous._endpointsById.TryGetValue(definition.Id, out var previousEndpoint);
            var endpoint = RuntimeEndpoint.Create(definition, previousEndpoint);
            endpointsById.Add(definition.Id, endpoint);
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
        return new EndpointRegistrySnapshot(frozenEndpoints, endpointsById.ToFrozenDictionary());
    }
}

/// <summary>Represents the immutable, wire-ready behavior for one enabled endpoint.</summary>
public sealed class RuntimeEndpoint
{
    private readonly ReadOnlyMemory<byte> _body;
    private readonly RollingWindowPermitBudget? _rateLimiter;
    private readonly RuntimeSuccessResponse? _successResponse;

    private RuntimeEndpoint(
        Guid id,
        MockResponseBehavior behavior,
        int? delaySeconds,
        int? statusCode,
        string? reasonPhrase,
        ImmutableArray<RuntimeResponseHeader> headers,
        string? contentType,
        ReadOnlyMemory<byte> body,
        RollingWindowPermitBudget? rateLimiter,
        RuntimeSuccessResponse? successResponse)
    {
        Id = id;
        Behavior = behavior;
        Delay = delaySeconds is null ? TimeSpan.Zero : TimeSpan.FromSeconds(delaySeconds.Value);
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

    /// <summary>Gets the intentional delay before the endpoint begins its configured response behavior.</summary>
    public TimeSpan Delay { get; }

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

        return _rateLimiter.TryAcquire()
            ? _successResponse!.Value.Response
            : new RuntimeResponse(StatusCode!.Value, ReasonPhrase, Headers, ContentType, Body);
    }

    internal static RuntimeEndpoint Create(
        MockEndpointDefinition definition,
        RuntimeEndpoint? previous = null,
        TimeProvider? timeProvider = null)
    {
        var response = definition.Response;
        var headers = RuntimeResponseCompilation.CreateHeaders(response.Headers, previous?.Headers ?? default);
        var body = RuntimeResponseCompilation.CreateBody(response.Body, previous?.Body);
        var rateLimit = response.RateLimit;
        // Share only immutable wire data. Each replacement still starts with fresh rate-limit permits.
        var limiter = rateLimit is null
            ? null
            : new RollingWindowPermitBudget(
                rateLimit.RequestLimit,
                TimeSpan.FromSeconds(rateLimit.WindowSeconds),
                timeProvider ?? TimeProvider.System);
        RuntimeSuccessResponse? successResponse = rateLimit is null
            ? null
            : RuntimeSuccessResponse.Create(rateLimit.SuccessResponse, previous?._successResponse);

        return new RuntimeEndpoint(
            definition.Id,
            response.Behavior,
            response.DelaySeconds,
            response.StatusCode,
            response.ReasonPhrase,
            headers,
            response.ContentType,
            body,
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
    public static RuntimeSuccessResponse Create(
        MockSuccessResponseDefinition response,
        RuntimeSuccessResponse? previous = null) => new(
        new RuntimeResponse(
            response.StatusCode,
            response.ReasonPhrase,
            RuntimeResponseCompilation.CreateHeaders(response.Headers, previous?.Response.Headers ?? default),
            response.ContentType,
            RuntimeResponseCompilation.CreateBody(response.Body, previous?.Response.Body)));
}

internal static class RuntimeResponseCompilation
{
    internal static ImmutableArray<RuntimeResponseHeader> CreateHeaders(
        Dictionary<string, string[]> headers,
        ImmutableArray<RuntimeResponseHeader> previous)
    {
        if (HeadersMatch(headers, previous))
        {
            return previous;
        }

        return headers.Select(pair => new RuntimeResponseHeader(pair.Key, [.. pair.Value])).ToImmutableArray();
    }

    private static bool HeadersMatch(
        Dictionary<string, string[]> headers,
        ImmutableArray<RuntimeResponseHeader> previous)
    {
        if (previous.IsDefault || headers.Count != previous.Length)
        {
            return false;
        }

        var index = 0;
        foreach (var header in headers)
        {
            var existing = previous[index++];
            if (!string.Equals(header.Key, existing.Name, StringComparison.Ordinal) ||
                !header.Value.AsSpan().SequenceEqual(existing.Values.AsSpan()))
            {
                return false;
            }
        }

        return true;
    }

    internal static ReadOnlyMemory<byte> CreateBody(string body, ReadOnlyMemory<byte>? previous)
    {
        if (previous is { } bytes && BodyMatches(body, bytes.Span))
        {
            return bytes;
        }

        return Encoding.UTF8.GetBytes(body);
    }

    private static bool BodyMatches(ReadOnlySpan<char> body, ReadOnlySpan<byte> previous)
    {
        // Compare the actual UTF-8 representation in bounded stack storage, including replacement fallback.
        // Retaining the source UTF-16 string would save comparison work but increase every snapshot's resident memory.
        Span<byte> scratch = stackalloc byte[512];
        while (!body.IsEmpty)
        {
            Utf8.FromUtf16(body, scratch, out var charactersRead, out var bytesWritten, replaceInvalidSequences: true);
            if (bytesWritten > previous.Length || !scratch[..bytesWritten].SequenceEqual(previous[..bytesWritten]))
            {
                return false;
            }

            body = body[charactersRead..];
            previous = previous[bytesWritten..];
        }

        return previous.IsEmpty;
    }
}

/// <summary>Represents one response header field and its ordered wire values.</summary>
/// <param name="Name">The validated response header name.</param>
/// <param name="Values">The immutable ordered values appended for this header.</param>
public readonly record struct RuntimeResponseHeader(string Name, ImmutableArray<string> Values);
