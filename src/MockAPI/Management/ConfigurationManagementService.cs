using MockAPI.Configuration;

namespace MockAPI.Management;

public sealed class ConfigurationManagementService(
    ConfigurationState state,
    ConfigurationFileStore store)
{
    public ConfigurationStateSnapshot Current => state.Current;

    public ConfigurationValidationResult Validate(MockApiConfigurationDocument candidate) =>
        ConfigurationValidator.Validate(candidate);

    public ConfigurationUpdateResult Import(
        MockApiConfigurationDocument candidate,
        long expectedRevision) =>
        state.TryReplace(candidate, expectedRevision);

    public Task<ConfigurationSaveResult> SaveAsync(
        long expectedRevision,
        CancellationToken cancellationToken) =>
        store.SaveAsync(state, expectedRevision, cancellationToken);
}