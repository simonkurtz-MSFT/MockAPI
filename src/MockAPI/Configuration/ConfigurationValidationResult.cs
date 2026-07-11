namespace MockAPI.Configuration;

public sealed record ConfigurationValidationError(string Path, string Code, string Message);

public sealed class ConfigurationValidationResult
{
    public ConfigurationValidationResult(IReadOnlyList<ConfigurationValidationError> errors)
    {
        Errors = errors;
    }

    public bool IsValid => Errors.Count == 0;

    public IReadOnlyList<ConfigurationValidationError> Errors { get; }
}
