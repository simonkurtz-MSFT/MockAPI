namespace MockAPI.Configuration;

public sealed class ConfigurationPersistenceException : Exception
{
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

    public ConfigurationPersistenceError Error { get; }

    public ConfigurationValidationResult? Validation { get; }
}

public enum ConfigurationPersistenceError
{
    FileNotFound,
    DocumentTooLarge,
    InvalidJson,
    ValidationFailed,
    RevisionConflict,
    IoFailure
}
