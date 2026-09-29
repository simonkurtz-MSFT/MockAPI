namespace MockAPI.Configuration;

/// <summary>Describes one configuration validation failure.</summary>
/// <param name="Path">The configuration-property path associated with the failure.</param>
/// <param name="Code">The stable machine-readable validation code.</param>
/// <param name="Message">The operator-facing explanation of the failure.</param>
public sealed record ConfigurationValidationError(string Path, string Code, string Message);

/// <summary>Represents the complete set of errors found while validating one candidate document.</summary>
public sealed class ConfigurationValidationResult
{
    /// <summary>Initializes a validation result.</summary>
    /// <param name="errors">All validation errors for the candidate; an empty collection represents success.</param>
    public ConfigurationValidationResult(IReadOnlyList<ConfigurationValidationError> errors)
    {
        Errors = errors;
    }

    /// <summary>Gets a value indicating whether the candidate contains no validation errors.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Gets all validation errors in deterministic discovery order.</summary>
    public IReadOnlyList<ConfigurationValidationError> Errors { get; }
}
