namespace MockAPI.Configuration;

public static class ConfigurationLimits
{
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    public const int MaximumEndpoints = 25;
    public const int MaximumNameLength = 200;
    public const int MaximumMethodsPerEndpoint = 8;
    public const int MaximumPathLength = 2048;
    public const int MaximumBodyBytes = 1024 * 1024;
    public const int MaximumHeadersPerEndpoint = 64;
    public const int MaximumHeaderValueBytes = 8 * 1024;
    public const int MaximumCombinedHeaderBytes = 32 * 1024;
}