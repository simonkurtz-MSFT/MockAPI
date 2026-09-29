namespace MockAPI.Configuration;

/// <summary>Defines the enforced size and cardinality limits for MockAPI configuration documents.</summary>
public static class ConfigurationLimits
{
    /// <summary>Gets the maximum serialized UTF-8 size of a configuration document, in bytes.</summary>
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    /// <summary>Gets the maximum number of endpoint definitions in a configuration document.</summary>
    public const int MaximumEndpoints = 25;
    /// <summary>Gets the maximum endpoint name length, in UTF-16 characters.</summary>
    public const int MaximumNameLength = 200;
    /// <summary>Gets the maximum endpoint description length, in UTF-16 characters.</summary>
    public const int MaximumDescriptionLength = 4000;
    /// <summary>Gets the maximum number of HTTP methods assigned to one endpoint.</summary>
    public const int MaximumMethodsPerEndpoint = 8;
    /// <summary>Gets the maximum endpoint path length, in UTF-16 characters.</summary>
    public const int MaximumPathLength = 2048;
    /// <summary>Gets the minimum number of requests suggested by an endpoint for dashboard testing.</summary>
    public const int MinimumTestRequestCount = 1;
    /// <summary>Gets the maximum number of requests suggested by an endpoint for dashboard testing.</summary>
    public const int MaximumTestRequestCount = 5;
    /// <summary>Gets the maximum UTF-8 size of a configured response body, in bytes.</summary>
    public const int MaximumBodyBytes = 1024 * 1024;
    /// <summary>Gets the maximum number of response header fields configured for one endpoint.</summary>
    public const int MaximumHeadersPerEndpoint = 64;
    /// <summary>Gets the maximum UTF-8 size of one response header value, in bytes.</summary>
    public const int MaximumHeaderValueBytes = 8 * 1024;
    /// <summary>Gets the maximum combined UTF-8 size of response header names and values for one endpoint.</summary>
    public const int MaximumCombinedHeaderBytes = 32 * 1024;
}
