using System.Text;
using System.Text.Json;

namespace MockAPI.Configuration;

/// <summary>Validates complete configuration documents against MockAPI semantic and size constraints.</summary>
public static class ConfigurationValidator
{
    private static readonly HashSet<string> ControlledHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Content-Length",
        "Date",
        "Host",
        "Server",
        "Transfer-Encoding",
        "Upgrade"
    };

    /// <summary>Validates a candidate without changing the active configuration.</summary>
    /// <param name="document">The complete candidate configuration document.</param>
    /// <returns>All validation errors, or a valid result when the complete candidate can be activated.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static ConfigurationValidationResult Validate(MockApiConfigurationDocument document) =>
        Validate(document, out _);

    internal static ConfigurationValidationResult Validate(
        MockApiConfigurationDocument document,
        out byte[] serializedDocument)
    {
        ArgumentNullException.ThrowIfNull(document);

        var errors = new List<ConfigurationValidationError>();

        if (document.SchemaVersion != "1.0")
        {
            AddError(errors, "schemaVersion", "const", "Schema version must be '1.0'.");
        }

        if (document.Endpoints.Count > ConfigurationLimits.MaximumEndpoints)
        {
            AddError(
                errors,
                "endpoints",
                "maximumItems",
                $"A configuration can contain at most {ConfigurationLimits.MaximumEndpoints} endpoints.");
        }

        var endpointIds = new HashSet<Guid>();
        var activeRoutes = new HashSet<(string Method, string Path)>();

        ValidateApiDescriptions(document.ApiDescriptions, errors);

        for (var endpointIndex = 0; endpointIndex < document.Endpoints.Count; endpointIndex++)
        {
            ValidateEndpoint(document.Endpoints[endpointIndex], endpointIndex, endpointIds, activeRoutes, errors);
        }

        serializedDocument = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        if (serializedDocument.Length > ConfigurationLimits.MaximumDocumentBytes)
        {
            AddError(
                errors,
                "$",
                "maximumBytes",
                $"The serialized configuration cannot exceed {ConfigurationLimits.MaximumDocumentBytes} UTF-8 bytes.");
        }

        return new ConfigurationValidationResult(errors);
    }

    private static void ValidateApiDescriptions(
        Dictionary<string, string>? descriptions,
        List<ConfigurationValidationError> errors)
    {
        if (descriptions is null)
        {
            return;
        }

        if (descriptions.Count > ConfigurationLimits.MaximumEndpoints)
        {
            AddError(errors, "apiDescriptions", "maximumProperties",
                $"A configuration can contain at most {ConfigurationLimits.MaximumEndpoints} API descriptions.");
        }

        foreach (var (groupPath, description) in descriptions)
        {
            var path = $"apiDescriptions[{groupPath}]";
            if (!IsApiGroupPath(groupPath))
            {
                AddError(errors, path, "format",
                    "API paths must be '/' or one absolute, non-reserved path segment, such as '/ctp'.");
            }

            if (description is null || description.Length > ConfigurationLimits.MaximumDescriptionLength)
            {
                AddError(errors, path, "maxLength",
                    $"API descriptions must be strings of at most {ConfigurationLimits.MaximumDescriptionLength} characters.");
            }
        }
    }

    internal static bool IsApiGroupPath(string path) =>
        path.StartsWith('/') &&
        path.Length <= ConfigurationLimits.MaximumPathLength &&
        (path == "/" || !IsReservedPath(path)) &&
        !path.AsSpan(1).Contains('/') &&
        !path.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '?' or '#' or '\\');

    private static void ValidateEndpoint(
        MockEndpointDefinition endpoint,
        int endpointIndex,
        HashSet<Guid> endpointIds,
        HashSet<(string Method, string Path)> activeRoutes,
        List<ConfigurationValidationError> errors)
    {
        var path = $"endpoints[{endpointIndex}]";

        if (endpoint.Id == Guid.Empty)
        {
            AddError(errors, $"{path}.id", "format", "Endpoint ID must be a non-empty UUID.");
        }
        else if (!endpointIds.Add(endpoint.Id))
        {
            AddError(errors, $"{path}.id", "unique", "Endpoint IDs must be unique.");
        }

        if (string.IsNullOrWhiteSpace(endpoint.Name))
        {
            AddError(errors, $"{path}.name", "minLength", "Endpoint name cannot be blank.");
        }
        else if (endpoint.Name.Length > ConfigurationLimits.MaximumNameLength)
        {
            AddError(
                errors,
                $"{path}.name",
                "maxLength",
                $"Endpoint name cannot exceed {ConfigurationLimits.MaximumNameLength} characters.");
        }

        if (endpoint.Description?.Length > ConfigurationLimits.MaximumDescriptionLength)
        {
            AddError(
            errors,
            $"{path}.description",
            "maxLength",
            $"Endpoint description cannot exceed {ConfigurationLimits.MaximumDescriptionLength} characters.");
        }

        ValidateMethods(endpoint, endpointIndex, activeRoutes, errors);
        ValidatePath(endpoint.Path, path, errors);
        ValidateRequestCount(endpoint.RequestCount, path, errors);
        ValidateResponse(endpoint.Response, path, errors);
    }

    private static void ValidateMethods(
        MockEndpointDefinition endpoint,
        int endpointIndex,
        HashSet<(string Method, string Path)> activeRoutes,
        List<ConfigurationValidationError> errors)
    {
        var methodsPath = $"endpoints[{endpointIndex}].methods";
        if (endpoint.Methods.Count == 0)
        {
            AddError(errors, methodsPath, "minItems", "At least one HTTP method is required.");
        }

        if (endpoint.Methods.Count > ConfigurationLimits.MaximumMethodsPerEndpoint)
        {
            AddError(
                errors,
                methodsPath,
                "maximumItems",
                $"An endpoint can define at most {ConfigurationLimits.MaximumMethodsPerEndpoint} methods.");
        }

        var endpointMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var methodIndex = 0; methodIndex < endpoint.Methods.Count; methodIndex++)
        {
            var method = endpoint.Methods[methodIndex];
            var methodPath = $"{methodsPath}[{methodIndex}]";
            if (!IsHttpToken(method))
            {
                AddError(errors, methodPath, "format", "HTTP method must be a valid token.");
                continue;
            }

            if (!endpointMethods.Add(method))
            {
                AddError(errors, methodPath, "unique", "Methods must be unique ignoring case.");
                continue;
            }

            if (endpoint.Enabled && !activeRoutes.Add((method.ToUpperInvariant(), endpoint.Path)))
            {
                AddError(errors, methodPath, "conflict", "An enabled endpoint already uses this method and path.");
            }
        }
    }

    private static void ValidatePath(
        string endpointPath,
        string path,
        List<ConfigurationValidationError> errors)
    {
        if (!endpointPath.StartsWith('/'))
        {
            AddError(errors, $"{path}.path", "format", "Endpoint path must start with '/'.");
        }

        if (endpointPath.Length > ConfigurationLimits.MaximumPathLength)
        {
            AddError(
                errors,
                $"{path}.path",
                "maxLength",
                $"Endpoint path cannot exceed {ConfigurationLimits.MaximumPathLength} characters.");
        }

        if (IsReservedPath(endpointPath))
        {
            AddError(errors, $"{path}.path", "reserved", "Endpoint path is reserved by MockAPI.");
        }
    }

    private static void ValidateRequestCount(
        int? requestCount,
        string endpointPath,
        List<ConfigurationValidationError> errors)
    {
        if (requestCount is not null &&
            requestCount is < ConfigurationLimits.MinimumTestRequestCount or > ConfigurationLimits.MaximumTestRequestCount)
        {
            AddError(
                errors,
                $"{endpointPath}.requestCount",
                "range",
                $"Request count must be between {ConfigurationLimits.MinimumTestRequestCount} and {ConfigurationLimits.MaximumTestRequestCount}.");
        }
    }

    private static void ValidateResponse(
        MockResponseDefinition response,
        string endpointPath,
        List<ConfigurationValidationError> errors)
    {
        var path = $"{endpointPath}.response";
        if (!Enum.IsDefined(response.Behavior))
        {
            AddError(errors, $"{path}.behavior", "value", "Response behavior is not supported.");
        }
        else if (response.Behavior == MockResponseBehavior.AbortConnection)
        {
            ValidateAbortConnection(response, path, errors);
            return;
        }

        if (response.StatusCode is null)
        {
            AddError(errors, $"{path}.statusCode", "required", "Status code is required for a normal response.");
        }
        else if (response.StatusCode is < 100 or > 599)
        {
            AddError(errors, $"{path}.statusCode", "range", "Status code must be between 100 and 599.");
        }

        ValidateResponseContent(
            response.ReasonPhrase,
            response.Headers,
            response.ContentType,
            response.Body,
            path,
            errors);

        if (response.RateLimit is not null)
        {
            ValidateRateLimit(response, path, errors);
        }
    }

    private static void ValidateRateLimit(
        MockResponseDefinition response,
        string responsePath,
        List<ConfigurationValidationError> errors)
    {
        var rateLimit = response.RateLimit!;
        var path = $"{responsePath}.rateLimit";
        if (response.StatusCode != StatusCodes.Status429TooManyRequests)
        {
            AddError(errors, responsePath, "rateLimit", "A rate-limited response must use status code 429.");
        }

        if (rateLimit.RequestLimit < 1)
        {
            AddError(errors, $"{path}.requestLimit", "range", "Request limit must be at least 1.");
        }

        if (rateLimit.WindowSeconds is < 1 or > 86400)
        {
            AddError(errors, $"{path}.windowSeconds", "range", "Window seconds must be between 1 and 86400.");
        }

        var success = rateLimit.SuccessResponse;
        var successPath = $"{path}.successResponse";
        if (success.StatusCode is < 200 or > 299)
        {
            AddError(errors, $"{successPath}.statusCode", "range", "Success status code must be between 200 and 299.");
        }

        ValidateResponseContent(
            success.ReasonPhrase,
            success.Headers,
            success.ContentType,
            success.Body,
            successPath,
            errors);
    }

    private static void ValidateResponseContent(
        string? reasonPhrase,
        Dictionary<string, string[]> headers,
        string? contentType,
        string body,
        string path,
        List<ConfigurationValidationError> errors)
    {
        if (reasonPhrase is not null && reasonPhrase.Any(char.IsControl))
        {
            AddError(errors, $"{path}.reasonPhrase", "format", "Reason phrase cannot contain control characters.");
        }

        ValidateHeaders(headers, path, errors);
        if (Encoding.UTF8.GetByteCount(body) > ConfigurationLimits.MaximumBodyBytes)
        {
            AddError(
                errors,
                $"{path}.body",
                "maximumBytes",
                $"Response body cannot exceed {ConfigurationLimits.MaximumBodyBytes} UTF-8 bytes.");
        }

        if (body.Length > 0 && string.IsNullOrWhiteSpace(contentType))
        {
            AddError(errors, $"{path}.contentType", "required", "Content type is required for a non-empty body.");
        }
    }

    private static void ValidateAbortConnection(
        MockResponseDefinition response,
        string path,
        List<ConfigurationValidationError> errors)
    {
        if (response.StatusCode is not null || response.ReasonPhrase is not null ||
            response.Headers.Count > 0 || response.ContentType is not null || response.Body.Length > 0 ||
            response.RateLimit is not null)
        {
            AddError(
                errors,
                path,
                "abortConnection",
                "An aborted connection cannot define a status code, reason phrase, headers, content type, body, or rate limit.");
        }
    }

    private static void ValidateHeaders(
        Dictionary<string, string[]> headers,
        string responsePath,
        List<ConfigurationValidationError> errors)
    {
        var headersPath = $"{responsePath}.headers";
        if (headers.Count > ConfigurationLimits.MaximumHeadersPerEndpoint)
        {
            AddError(
                errors,
                headersPath,
                "maximumProperties",
                $"A response can define at most {ConfigurationLimits.MaximumHeadersPerEndpoint} headers.");
        }

        var combinedBytes = 0;
        foreach (var (name, values) in headers)
        {
            var headerPath = $"{headersPath}[{name}]";
            combinedBytes += Encoding.UTF8.GetByteCount(name);

            if (!IsHttpToken(name))
            {
                AddError(errors, headerPath, "format", "Header name must be a valid token.");
            }
            else if (ControlledHeaders.Contains(name))
            {
                AddError(errors, headerPath, "controlled", "This response header is controlled by the server.");
            }

            if (values.Length == 0)
            {
                AddError(errors, headerPath, "minItems", "A configured header requires at least one value.");
            }

            for (var valueIndex = 0; valueIndex < values.Length; valueIndex++)
            {
                var value = values[valueIndex];
                var valueBytes = Encoding.UTF8.GetByteCount(value);
                combinedBytes += valueBytes;
                var valuePath = $"{headerPath}[{valueIndex}]";

                if (valueBytes > ConfigurationLimits.MaximumHeaderValueBytes)
                {
                    AddError(
                        errors,
                        valuePath,
                        "maximumBytes",
                        $"Header value cannot exceed {ConfigurationLimits.MaximumHeaderValueBytes} UTF-8 bytes.");
                }

                if (value.Contains('\r') || value.Contains('\n'))
                {
                    AddError(errors, valuePath, "format", "Header value cannot contain CR or LF characters.");
                }
            }
        }

        if (combinedBytes > ConfigurationLimits.MaximumCombinedHeaderBytes)
        {
            AddError(
                errors,
                headersPath,
                "maximumBytes",
                $"Combined header names and values cannot exceed {ConfigurationLimits.MaximumCombinedHeaderBytes} UTF-8 bytes.");
        }
    }

    private static bool IsReservedPath(string path) =>
        path == "/" ||
        IsPathOrDescendant(path, "/__mockapi") ||
        IsPathOrDescendant(path, "/health");

    private static bool IsPathOrDescendant(string path, string reservedPath) =>
        path.Equals(reservedPath, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{reservedPath}/", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpToken(string value) =>
        value.Length > 0 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~');

    private static void AddError(
        List<ConfigurationValidationError> errors,
        string path,
        string code,
        string message) => errors.Add(new ConfigurationValidationError(path, code, message));
}
