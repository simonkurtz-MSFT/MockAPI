namespace MockAPI.Configuration;

public sealed class MockApiOptions
{
    public const string SectionName = "MockApi";

    public required string ConfigurationPath { get; init; }

    public bool AllowEmptyConfiguration { get; init; } = true;

    public bool EnableManagementApi { get; init; } = true;

    public bool EnableDashboard { get; init; } = true;

    public bool EnableOpenApi { get; init; } = true;

    public bool EnableSwaggerUi { get; init; } = true;
}
