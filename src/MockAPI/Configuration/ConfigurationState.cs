using System.Globalization;
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

        if (Current.Revision != expectedRevision)
        {
            return ConfigurationUpdateResult.RevisionConflict();
        }

        var canonicalDocument = ConfigurationDocumentCanonicalizer.Clone(candidate);
        var validation = ConfigurationValidator.Validate(canonicalDocument, out var serializedDocument);
        if (!validation.IsValid)
        {
            return ConfigurationUpdateResult.ValidationFailed(validation);
        }

        var endpoints = EndpointRegistrySnapshot.Create(canonicalDocument);
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

    private ConfigurationStateSnapshot(
        byte[] serializedDocument,
        EndpointRegistrySnapshot endpoints,
        long revision,
        bool hasUnsavedChanges)
    {
        _serializedDocument = serializedDocument;
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

    /// <summary>Materializes a detached configuration document from the canonical serialized snapshot.</summary>
    /// <returns>A new document graph that callers may modify without affecting this snapshot.</returns>
    public MockApiConfigurationDocument GetDocument() =>
        DeserializeDocument(_serializedDocument);

    /// <summary>Copies the deterministic UTF-8 JSON representation of this snapshot.</summary>
    /// <returns>A new byte array that callers may modify without affecting this snapshot.</returns>
    public byte[] ExportUtf8() => [.. _serializedDocument];

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

    internal static ConfigurationStateSnapshot Create(
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
            endpoints,
            revision,
            hasUnsavedChanges);
    }

    internal static ConfigurationStateSnapshot CreateFromOwnedSerializedDocument(
        byte[] serializedDocument,
        EndpointRegistrySnapshot endpoints,
        long revision,
        bool hasUnsavedChanges)
    {
        return new ConfigurationStateSnapshot(
            serializedDocument,
            endpoints,
            revision,
            hasUnsavedChanges);
    }

    internal ConfigurationStateSnapshot WithUnsavedChanges(bool hasUnsavedChanges) =>
        new(_serializedDocument, Endpoints, Revision, hasUnsavedChanges);
}

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
