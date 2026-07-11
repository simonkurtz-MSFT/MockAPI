using System.Text;
using System.Text.Json;

namespace MockAPI.Configuration;

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

    public static ConfigurationValidationResult Validate(MockApiConfigurationDocument document)
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

        for (var endpointIndex = 0; endpointIndex < document.Endpoints.Count; endpointIndex++)
        {
            ValidateEndpoint(document.Endpoints[endpointIndex], endpointIndex, endpointIds, activeRoutes, errors);
        }

        var documentBytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument).Length;
        if (documentBytes > ConfigurationLimits.MaximumDocumentBytes)
        {
            AddError(
                errors,
                "$",
                "maximumBytes",
                $"The serialized configuration cannot exceed {ConfigurationLimits.MaximumDocumentBytes} UTF-8 bytes.");
        }

        return new ConfigurationValidationResult(errors);
    }

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

        ValidateMethods(endpoint, endpointIndex, activeRoutes, errors);
        ValidatePath(endpoint.Path, path, errors);
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

    private static void ValidateResponse(
        MockResponseDefinition response,
        string endpointPath,
        List<ConfigurationValidationError> errors)
    {
        var path = $"{endpointPath}.response";
        if (response.StatusCode is < 100 or > 599)
        {
            AddError(errors, $"{path}.statusCode", "range", "Status code must be between 100 and 599.");
        }

        if (response.ReasonPhrase is not null && response.ReasonPhrase.Any(char.IsControl))
        {
            AddError(errors, $"{path}.reasonPhrase", "format", "Reason phrase cannot contain control characters.");
        }

        ValidateHeaders(response.Headers, path, errors);

        if (Encoding.UTF8.GetByteCount(response.Body) > ConfigurationLimits.MaximumBodyBytes)
        {
            AddError(
                errors,
                $"{path}.body",
                "maximumBytes",
                $"Response body cannot exceed {ConfigurationLimits.MaximumBodyBytes} UTF-8 bytes.");
        }

        if (response.Body.Length > 0 && string.IsNullOrWhiteSpace(response.ContentType))
        {
            AddError(errors, $"{path}.contentType", "required", "Content type is required for a non-empty body.");
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