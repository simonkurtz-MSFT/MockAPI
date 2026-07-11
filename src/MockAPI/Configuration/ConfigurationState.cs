using System.Globalization;
using System.Text.Json;
using MockAPI.Runtime;

namespace MockAPI.Configuration;

public sealed class ConfigurationState
{
    private readonly object _writeGate = new();
    private ConfigurationStateSnapshot _current = ConfigurationStateSnapshot.CreateInitial();

    public ConfigurationStateSnapshot Current => Volatile.Read(ref _current);

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
        var validation = ConfigurationValidator.Validate(canonicalDocument);
        if (!validation.IsValid)
        {
            return ConfigurationUpdateResult.ValidationFailed(validation);
        }

        var endpoints = EndpointRegistrySnapshot.Create(canonicalDocument);
        lock (_writeGate)
        {
            var current = _current;
            if (current.Revision != expectedRevision)
            {
                return ConfigurationUpdateResult.RevisionConflict();
            }

            var revision = checked(current.Revision + 1);
            var replacement = ConfigurationStateSnapshot.Create(
                canonicalDocument,
                endpoints,
                revision,
                hasUnsavedChanges);
            Volatile.Write(ref _current, replacement);
            return ConfigurationUpdateResult.Applied(replacement);
        }
    }

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
                Volatile.Write(ref _current, current.WithUnsavedChanges(hasUnsavedChanges: false));
            }

            return true;
        }
    }
}

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

    public EndpointRegistrySnapshot Endpoints { get; }

    public long Revision { get; }

    public string ETag { get; }

    public bool HasUnsavedChanges { get; }

    public MockApiConfigurationDocument GetDocument() =>
        JsonSerializer.Deserialize(
            _serializedDocument,
            MockApiJsonContext.Default.MockApiConfigurationDocument) ??
        throw new InvalidOperationException("The active configuration could not be materialized.");

    public byte[] ExportUtf8() => [.. _serializedDocument];

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
        return new ConfigurationStateSnapshot(
            serializedDocument,
            endpoints,
            revision,
            hasUnsavedChanges);
    }

    internal ConfigurationStateSnapshot WithUnsavedChanges(bool hasUnsavedChanges) =>
        new(_serializedDocument, Endpoints, Revision, hasUnsavedChanges);
}

public enum ConfigurationUpdateStatus
{
    Applied,
    ValidationFailed,
    RevisionConflict
}

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
        Endpoints = document.Endpoints.Select(CloneEndpoint).ToArray()
    };

    private static MockEndpointDefinition CloneEndpoint(MockEndpointDefinition endpoint) => new()
    {
        Id = endpoint.Id,
        Name = endpoint.Name,
        Enabled = endpoint.Enabled,
        Methods = endpoint.Methods.ToArray(),
        Path = endpoint.Path,
        Response = new MockResponseDefinition
        {
            StatusCode = endpoint.Response.StatusCode,
            ReasonPhrase = endpoint.Response.ReasonPhrase,
            Headers = endpoint.Response.Headers
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
            ContentType = endpoint.Response.ContentType,
            Body = endpoint.Response.Body
        }
    };
}
