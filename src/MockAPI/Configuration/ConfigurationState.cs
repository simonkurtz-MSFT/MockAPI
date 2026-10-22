using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MockAPI.Runtime;

namespace MockAPI.Configuration;

/// <summary>Owns the active configuration and publishes validated immutable replacements atomically.</summary>
/// <remarks>Readers are lock-free and observe either the complete prior snapshot or the complete replacement.</remarks>
public sealed class ConfigurationState
{
    private readonly object _writeGate = new();
    private readonly Action? _beforeWriteLock;
    private ConfigurationStateSnapshot _current = ConfigurationStateSnapshot.CreateInitial();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes state with an empty valid configuration at revision zero.</summary>
    public ConfigurationState()
    {
    }

    internal ConfigurationState(Action beforeWriteLock)
    {
        _beforeWriteLock = beforeWriteLock;
    }

    /// <summary>Gets the immutable snapshot currently visible to readers.</summary>
    public ConfigurationStateSnapshot Current => Volatile.Read(ref _current);

    // Capture under the publication lock so a change between reading and waiting cannot be lost.
    internal Task WaitForChangeAsync(ConfigurationStateSnapshot observed)
    {
        lock (_writeGate)
        {
            return ReferenceEquals(observed, _current) ? _changed.Task : Task.CompletedTask;
        }
    }

    private void Publish(ConfigurationStateSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        var changed = _changed;
        _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.SetResult();
    }

    /// <summary>Validates and atomically activates a complete candidate when the expected revision is current.</summary>
    /// <param name="candidate">The complete candidate document; it is cloned before publication.</param>
    /// <param name="expectedRevision">The active revision required for the replacement.</param>
    /// <returns>An applied result with the new dirty snapshot, all validation errors, or a revision conflict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedRevision"/> is negative.</exception>
    /// <exception cref="OverflowException">Incrementing the current revision exceeds <see cref="long.MaxValue"/>.</exception>
    public ConfigurationUpdateResult TryReplace(
        MockApiConfigurationDocument candidate,
        long expectedRevision) =>
        TryReplace(candidate, expectedRevision, hasUnsavedChanges: true);

    internal ConfigurationUpdateResult TryLoadPersisted(
        MockApiConfigurationDocument candidate,
        long expectedRevision) =>
        TryReplace(candidate, expectedRevision, hasUnsavedChanges: false);

    private ConfigurationUpdateResult TryReplace(
        MockApiConfigurationDocument candidate,
        long expectedRevision,
        bool hasUnsavedChanges)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        var observed = Current;
        if (observed.Revision != expectedRevision)
        {
            return ConfigurationUpdateResult.RevisionConflict();
        }

        var canonicalDocument = ConfigurationDocumentCanonicalizer.Clone(candidate);
        var validation = ConfigurationValidator.Validate(canonicalDocument, out var serializedDocument);
        if (!validation.IsValid)
        {
            return ConfigurationUpdateResult.ValidationFailed(validation);
        }

        var endpoints = EndpointRegistrySnapshot.Create(canonicalDocument, observed.Endpoints);
        _beforeWriteLock?.Invoke();
        lock (_writeGate)
        {
            var current = _current;
            if (current.Revision != expectedRevision)
            {
                return ConfigurationUpdateResult.RevisionConflict();
            }

            var revision = checked(current.Revision + 1);
            var replacement = ConfigurationStateSnapshot.CreateFromOwnedSerializedDocument(
                serializedDocument,
                canonicalDocument.ApiDescriptions,
                endpoints,
                revision,
                hasUnsavedChanges);
            Publish(replacement);
            return ConfigurationUpdateResult.Applied(replacement);
        }
    }

    /// <summary>Clears dirty state only when the specified revision is still current.</summary>
    /// <param name="expectedRevision">The revision known to have been persisted.</param>
    /// <returns><see langword="true"/> when that revision is current; otherwise, <see langword="false"/> without changing state.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="expectedRevision"/> is negative.</exception>
    public bool TryMarkPersisted(long expectedRevision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);

        lock (_writeGate)
        {
            var current = _current;
            if (current.Revision != expectedRevision)
            {
                return false;
            }

            if (current.HasUnsavedChanges)
            {
                Publish(current.WithUnsavedChanges(hasUnsavedChanges: false));
            }

            return true;
        }
    }
}

/// <summary>Represents one immutable, self-consistent configuration revision and its runtime endpoint registry.</summary>
public sealed class ConfigurationStateSnapshot
{
    private readonly byte[] _serializedDocument;
    private readonly Lazy<IReadOnlyList<EndpointFingerprint>> _endpointManifest;

    private ConfigurationStateSnapshot(
        byte[] serializedDocument,
        IReadOnlyDictionary<string, string>? apiDescriptions,
        EndpointRegistrySnapshot endpoints,
        long revision,
        bool hasUnsavedChanges,
        Lazy<IReadOnlyList<EndpointFingerprint>>? endpointManifest = null)
    {
        _serializedDocument = serializedDocument;
        // Hash canonical bytes once, without materializing response bodies. Save-only snapshots
        // share this lazy projection; neither it nor the hashes retain a prior snapshot.
        _endpointManifest = endpointManifest ?? new(() => CreateEndpointManifest(serializedDocument));
        ApiDescriptions = apiDescriptions;
        Endpoints = endpoints;
        Revision = revision;
        ETag = string.Create(CultureInfo.InvariantCulture, $"\"{revision}\"");
        HasUnsavedChanges = hasUnsavedChanges;
    }

    /// <summary>Gets the immutable enabled-endpoint registry built from this revision.</summary>
    public EndpointRegistrySnapshot Endpoints { get; }

    /// <summary>Gets the monotonically increasing in-process revision.</summary>
    public long Revision { get; }

    /// <summary>Gets the strong HTTP entity tag for <see cref="Revision"/>, including quotes.</summary>
    public string ETag { get; }

    /// <summary>Gets a value indicating whether this revision has not been confirmed persisted.</summary>
    public bool HasUnsavedChanges { get; }

    /// <summary>Gets immutable, ordinally ordered API descriptions from this revision, or <see langword="null"/> when absent.</summary>
    /// <remarks>Preserves empty metadata and exact description text. Persistence-status-only replacements share this projection.</remarks>
    public IReadOnlyDictionary<string, string>? ApiDescriptions { get; }

    /// <summary>Materializes a detached configuration document from the canonical serialized snapshot.</summary>
    /// <returns>A new document graph that callers may modify without affecting this snapshot.</returns>
    public MockApiConfigurationDocument GetDocument() =>
        DeserializeDocument(_serializedDocument);

    internal IReadOnlyList<EndpointFingerprint> EndpointManifest => _endpointManifest.Value;

    private static IReadOnlyList<EndpointFingerprint> CreateEndpointManifest(byte[] serializedDocument)
    {
        var manifest = new List<EndpointFingerprint>();
        var reader = new Utf8JsonReader(serializedDocument);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("endpoints"))
            {
                continue;
            }

            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var id = ReadEndpointId(reader);
                var start = checked((int)reader.TokenStartIndex);
                reader.Skip();
                var length = checked((int)reader.BytesConsumed) - start;
                var hash = SHA256.HashData(serializedDocument.AsSpan(start, length));
                manifest.Add(new(id, Convert.ToHexString(hash.AsSpan(0, 16))));
            }
            break;
        }
        return manifest.AsReadOnly();
    }

    internal MockEndpointDefinition[] GetEndpoints(IReadOnlySet<Guid> endpointIds)
    {
        var endpoints = new List<MockEndpointDefinition>();
        var reader = new Utf8JsonReader(_serializedDocument);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("endpoints"))
            {
                continue;
            }

            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (endpointIds.Contains(ReadEndpointId(reader)))
                {
                    endpoints.Add(JsonSerializer.Deserialize(ref reader, MockApiJsonContext.Default.MockEndpointDefinition)!);
                }
                else
                {
                    reader.Skip();
                }
            }
            break;
        }
        return endpoints.ToArray();
    }

    internal MockEndpointDefinition GetEndpoint(Guid endpointId)
    {
        var reader = new Utf8JsonReader(_serializedDocument);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("endpoints"))
            {
                continue;
            }

            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (ReadEndpointId(reader) == endpointId)
                {
                    // Deserialize from the captured canonical bytes, but do not materialize unrelated bodies.
                    return JsonSerializer.Deserialize(ref reader, MockApiJsonContext.Default.MockEndpointDefinition)!;
                }
                reader.Skip();
            }
            break;
        }

        throw new InvalidOperationException("The captured configuration does not contain the requested endpoint.");
    }

    private static Guid ReadEndpointId(Utf8JsonReader reader)
    {
        // Probe a value copy so the caller can still deserialize from the object's opening token.
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.ValueTextEquals("id"))
            {
                reader.Read();
                return reader.GetGuid();
            }
            reader.Skip();
        }
        throw new InvalidOperationException("A canonical endpoint is missing its ID.");
    }

    /// <summary>Copies the deterministic UTF-8 JSON representation of this snapshot.</summary>
    /// <returns>A new byte array that callers may modify without affecting this snapshot.</returns>
    public byte[] ExportUtf8() => [.. _serializedDocument];

    internal ValueTask WriteToAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.WriteAsync(_serializedDocument.AsMemory(), cancellationToken);
    }

    // SDK consumers may seek and retry reads, but cannot write or expose the owned buffer.
    internal Stream OpenRead() => new MemoryStream(
        _serializedDocument, 0, _serializedDocument.Length, writable: false, publiclyVisible: false);

    internal static MockApiConfigurationDocument DeserializeDocument(ReadOnlySpan<byte> serializedDocument) =>
        JsonSerializer.Deserialize(
            serializedDocument,
            MockApiJsonContext.Default.MockApiConfigurationDocument) ??
        throw new InvalidOperationException("The active configuration could not be materialized.");

    internal static ConfigurationStateSnapshot CreateInitial()
    {
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            Endpoints = []
        };
        return Create(
            document,
            EndpointRegistrySnapshot.Empty,
            revision: 0,
            hasUnsavedChanges: false);
    }

    private static ConfigurationStateSnapshot Create(
        MockApiConfigurationDocument document,
        EndpointRegistrySnapshot endpoints,
        long revision,
        bool hasUnsavedChanges)
    {
        var serializedDocument = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        return CreateFromOwnedSerializedDocument(
            serializedDocument,
            document.ApiDescriptions,
            endpoints,
            revision,
            hasUnsavedChanges);
    }

    internal static ConfigurationStateSnapshot CreateFromOwnedSerializedDocument(
        byte[] serializedDocument,
        Dictionary<string, string>? ownedApiDescriptions,
        EndpointRegistrySnapshot endpoints,
        long revision,
        bool hasUnsavedChanges)
    {
        // Both inputs belong to the same canonical document. Its private metadata dictionary
        // is never exposed mutably; strings are immutable, so a read-only wrapper is sufficient.
        var apiDescriptions = ownedApiDescriptions is null
            ? null
            : new ReadOnlyDictionary<string, string>(ownedApiDescriptions);
        return new ConfigurationStateSnapshot(
            serializedDocument,
            apiDescriptions,
            endpoints,
            revision,
            hasUnsavedChanges);
    }

    internal ConfigurationStateSnapshot WithUnsavedChanges(bool hasUnsavedChanges) =>
        new(_serializedDocument, ApiDescriptions, Endpoints, Revision, hasUnsavedChanges, _endpointManifest);
}

/// <summary>Identifies one endpoint's content in an ordered configuration manifest.</summary>
/// <param name="Id">The full stable endpoint ID, unchanged by edits.</param>
/// <param name="ContentHash">The first 128 bits of SHA-256 over canonical endpoint JSON, encoded as 32 uppercase hexadecimal characters.</param>
public sealed record EndpointFingerprint(Guid Id, string ContentHash);

/// <summary>Identifies the outcome of an optimistic complete-configuration replacement.</summary>
public enum ConfigurationUpdateStatus
{
    /// <summary>The complete candidate was validated and atomically published.</summary>
    Applied,
    /// <summary>The candidate was rejected and the active snapshot was unchanged.</summary>
    ValidationFailed,
    /// <summary>The expected revision was stale and the active snapshot was unchanged.</summary>
    RevisionConflict
}

/// <summary>Reports the outcome of a configuration replacement.</summary>
/// <param name="Status">The replacement outcome.</param>
/// <param name="Validation">The validation result; populated with errors for validation failure.</param>
/// <param name="Snapshot">The newly published snapshot for an applied update; otherwise, <see langword="null"/>.</param>
public sealed record ConfigurationUpdateResult(
    ConfigurationUpdateStatus Status,
    ConfigurationValidationResult Validation,
    ConfigurationStateSnapshot? Snapshot)
{
    internal static ConfigurationUpdateResult Applied(ConfigurationStateSnapshot snapshot) =>
        new(ConfigurationUpdateStatus.Applied, new ConfigurationValidationResult([]), snapshot);

    internal static ConfigurationUpdateResult ValidationFailed(ConfigurationValidationResult validation) =>
        new(ConfigurationUpdateStatus.ValidationFailed, validation, null);

    internal static ConfigurationUpdateResult RevisionConflict() =>
        new(ConfigurationUpdateStatus.RevisionConflict, new ConfigurationValidationResult([]), null);
}

internal static class ConfigurationDocumentCanonicalizer
{
    public static MockApiConfigurationDocument Clone(MockApiConfigurationDocument document) => new()
    {
        Schema = document.Schema,
        SchemaVersion = document.SchemaVersion,
        ApiDescriptions = document.ApiDescriptions?
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        Endpoints = document.Endpoints.Select(CloneEndpoint).ToArray()
    };

    private static MockEndpointDefinition CloneEndpoint(MockEndpointDefinition endpoint) => new()
    {
        Id = endpoint.Id,
        Name = endpoint.Name,
        Description = endpoint.Description,
        Enabled = endpoint.Enabled,
        Methods = endpoint.Methods.ToArray(),
        Path = endpoint.Path,
        RequestCount = endpoint.RequestCount,
        Response = new MockResponseDefinition
        {
            Behavior = endpoint.Response.Behavior,
            DelaySeconds = endpoint.Response.DelaySeconds,
            StatusCode = endpoint.Response.StatusCode,
            ReasonPhrase = endpoint.Response.ReasonPhrase,
            Headers = endpoint.Response.Headers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
            ContentType = endpoint.Response.ContentType,
            Body = endpoint.Response.Body,
            RateLimit = endpoint.Response.RateLimit is null
                ? null
                : new MockRateLimitDefinition
                {
                    RequestLimit = endpoint.Response.RateLimit.RequestLimit,
                    WindowSeconds = endpoint.Response.RateLimit.WindowSeconds,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = endpoint.Response.RateLimit.SuccessResponse.StatusCode,
                        ReasonPhrase = endpoint.Response.RateLimit.SuccessResponse.ReasonPhrase,
                        Headers = endpoint.Response.RateLimit.SuccessResponse.Headers
                            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                            .ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value.ToArray(),
                                StringComparer.Ordinal),
                        ContentType = endpoint.Response.RateLimit.SuccessResponse.ContentType,
                        Body = endpoint.Response.RateLimit.SuccessResponse.Body
                    }
                }
        }
    };
}
