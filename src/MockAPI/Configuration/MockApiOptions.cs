namespace MockAPI.Configuration;

/// <summary>Defines host, persistence, administrative-surface, and authentication settings for MockAPI.</summary>
public sealed class MockApiOptions
{
    /// <summary>Gets the configuration section name used by the options binder.</summary>
    public const string SectionName = "MockApi";

    /// <summary>Gets the local configuration file path used when <see cref="ConfigurationBlobUri"/> is absent.</summary>
    public required string ConfigurationPath { get; init; }

    /// <summary>Gets the optional Azure Blob URI used instead of local-file persistence.</summary>
    public Uri? ConfigurationBlobUri { get; init; }

    /// <summary>Gets the optional user-assigned managed identity client ID used for blob authentication.</summary>
    public string? ManagedIdentityClientId { get; init; }

    /// <summary>Gets the optional Azure portal URI for the deployment's Log Analytics workspace.</summary>
    public Uri? LogAnalyticsWorkspaceUri { get; init; }

    /// <summary>Gets a value indicating whether startup may continue when no persisted configuration exists.</summary>
    public bool AllowEmptyConfiguration { get; init; } = true;

    /// <summary>Gets a value indicating whether reserved management API routes are exposed.</summary>
    public bool EnableManagementApi { get; init; } = true;

    /// <summary>Gets a value indicating whether dashboard routes and assets are exposed.</summary>
    public bool EnableDashboard { get; init; } = true;

    /// <summary>Gets a value indicating whether the management OpenAPI document is exposed.</summary>
    public bool EnableOpenApi { get; init; } = true;

    /// <summary>Gets a value indicating whether Swagger UI is exposed.</summary>
    public bool EnableSwaggerUi { get; init; } = true;

    /// <summary>Gets the per-client management request permit limit for each one-minute sliding window.</summary>
    public int ManagementPermitLimit { get; init; } = 120;

    /// <summary>Gets whether mock calls require an API key before security settings are first persisted. Defaults to true.</summary>
    public bool RequireApiKey { get; init; } = true;

    /// <summary>Gets the optional Basic authentication username for administrative surfaces.</summary>
    public string? DashboardUsername { get; init; }

    /// <summary>Gets the optional versioned, salted PBKDF2 password hash for administrative surfaces.</summary>
    public string? DashboardPasswordHash { get; init; }
}
