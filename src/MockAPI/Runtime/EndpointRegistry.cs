using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using MockAPI.Configuration;

namespace MockAPI.Runtime;

public sealed class EndpointRegistry
{
    private EndpointRegistrySnapshot _current = EndpointRegistrySnapshot.Empty;

    public EndpointRegistrySnapshot Current => Volatile.Read(ref _current);

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

public sealed class EndpointRegistrySnapshot
{
    private readonly FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> _endpointsByMethod;

    private EndpointRegistrySnapshot(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>> endpointsByMethod)
    {
        _endpointsByMethod = endpointsByMethod;
    }

    public static EndpointRegistrySnapshot Empty { get; } = new(
        FrozenDictionary<string, FrozenDictionary<string, RuntimeEndpoint>>.Empty);

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

public sealed class RuntimeEndpoint
{
    private readonly byte[] _body;

    private RuntimeEndpoint(
        Guid id,
        int statusCode,
        string? reasonPhrase,
        ImmutableArray<RuntimeResponseHeader> headers,
        string? contentType,
        byte[] body)
    {
        Id = id;
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        ContentType = contentType;
        _body = body;
    }

    public Guid Id { get; }

    public int StatusCode { get; }

    public string? ReasonPhrase { get; }

    public ImmutableArray<RuntimeResponseHeader> Headers { get; }

    public string? ContentType { get; }

    public ReadOnlyMemory<byte> Body => _body;

    internal static RuntimeEndpoint Create(MockEndpointDefinition definition)
    {
        var response = definition.Response;
        var headers = response.Headers
            .Select(pair => new RuntimeResponseHeader(pair.Key, [.. pair.Value]))
            .ToImmutableArray();

        return new RuntimeEndpoint(
            definition.Id,
            response.StatusCode,
            response.ReasonPhrase,
            headers,
            response.ContentType,
            Encoding.UTF8.GetBytes(response.Body));
    }
}

public readonly record struct RuntimeResponseHeader(string Name, ImmutableArray<string> Values);
