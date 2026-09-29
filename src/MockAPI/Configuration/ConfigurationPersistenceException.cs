namespace MockAPI.Configuration;

/// <summary>Represents a classified failure while loading or saving configuration persistence.</summary>
public sealed class ConfigurationPersistenceException : Exception
{
    /// <summary>Initializes a configuration persistence exception.</summary>
    /// <param name="error">The stable persistence error category.</param>
    /// <param name="message">A safe operator-facing description that does not expose storage details.</param>
    /// <param name="innerException">The underlying provider or I/O exception, when available.</param>
    /// <param name="validation">Validation errors when <paramref name="error"/> is <see cref="ConfigurationPersistenceError.ValidationFailed"/>.</param>
    public ConfigurationPersistenceException(
        ConfigurationPersistenceError error,
        string message,
        Exception? innerException = null,
        ConfigurationValidationResult? validation = null)
        : base(message, innerException)
    {
        Error = error;
        Validation = validation;
    }

    /// <summary>Gets the stable persistence error category.</summary>
    public ConfigurationPersistenceError Error { get; }

    /// <summary>Gets validation details when persisted content failed validation.</summary>
    public ConfigurationValidationResult? Validation { get; }
}

/// <summary>Classifies configuration persistence failures independently of provider-specific exceptions.</summary>
public enum ConfigurationPersistenceError
{
    /// <summary>The configured persistence target does not exist.</summary>
    FileNotFound,
    /// <summary>The persisted document exceeds the configured UTF-8 byte limit.</summary>
    DocumentTooLarge,
    /// <summary>The persisted content is not a valid configuration JSON document.</summary>
    InvalidJson,
    /// <summary>The complete persisted document failed semantic validation.</summary>
    ValidationFailed,
    /// <summary>The active revision changed before a load or save could be applied consistently.</summary>
    RevisionConflict,
    /// <summary>The persistence provider could not read or durably replace the document.</summary>
    IoFailure
}
