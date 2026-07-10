using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Net.Http.Headers;
using MockAPI.Configuration;

namespace MockAPI.Management;

public static class ManagementApiEndpoints
{
    private const string BasePath = "/__mockapi/api";
    private const string EndpointsPath = BasePath + "/endpoints";
    private const string ProblemBase = "https://mockapi.local/problems/";

    public static void Map(WebApplication app, EndpointManagementService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(service);

        app.MapGet(BasePath + "/configuration", context => WriteConfigurationStatusAsync(context, service));
        app.MapGet(EndpointsPath, context => WriteEndpointsAsync(context, service));
        app.MapGet(EndpointsPath + "/{id:guid}", context => WriteEndpointAsync(context, service));
        app.MapPost(EndpointsPath, context => CreateEndpointAsync(context, service));
        app.MapPut(EndpointsPath + "/{id:guid}", context => ReplaceEndpointAsync(context, service));
        app.MapPut(EndpointsPath + "/{id:guid}/enabled", context => SetEnabledAsync(context, service));
        app.MapDelete(EndpointsPath + "/{id:guid}", context => DeleteEndpointAsync(context, service));
    }

    private static Task WriteConfigurationStatusAsync(HttpContext context, EndpointManagementService service)
    {
        var snapshot = service.Current;
        SetETag(context, snapshot);
        return WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new ConfigurationStatusResponse(snapshot.Revision, snapshot.ETag, snapshot.HasUnsavedChanges),
            ManagementJsonContext.Default.ConfigurationStatusResponse);
    }

    private static Task WriteEndpointsAsync(HttpContext context, EndpointManagementService service)
    {
        var snapshot = service.Current;
        SetETag(context, snapshot);
        return WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            snapshot.GetDocument().Endpoints.ToArray(),
            ManagementJsonContext.Default.MockEndpointDefinitionArray);
    }

    private static Task WriteEndpointAsync(HttpContext context, EndpointManagementService service)
    {
        var snapshot = service.Current;
        var endpointId = GetEndpointId(context);
        var endpoint = snapshot.GetDocument().Endpoints.FirstOrDefault(candidate => candidate.Id == endpointId);
        if (endpoint is null)
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "endpoint-not-found",
                "Endpoint not found",
                "No endpoint exists with the requested ID.",
                snapshot);
        }

        SetETag(context, snapshot);
        return WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            endpoint,
            ManagementJsonContext.Default.MockEndpointDefinition);
    }

    private static async Task CreateEndpointAsync(HttpContext context, EndpointManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service);
        if (expectedRevision is null)
        {
            return;
        }

        var endpoint = await ReadJsonAsync(
            context,
            ManagementJsonContext.Default.MockEndpointDefinition);
        if (endpoint is null)
        {
            return;
        }

        var result = service.Create(endpoint, expectedRevision.Value);
        if (result.Status != ManagementOperationStatus.Applied)
        {
            await WriteOperationProblemAsync(context, result, service);
            return;
        }

        SetETag(context, result.Snapshot!);
        context.Response.Headers.Location = $"{EndpointsPath}/{endpoint.Id}";
        await WriteJsonAsync(
            context,
            StatusCodes.Status201Created,
            result.Endpoint!,
            ManagementJsonContext.Default.MockEndpointDefinition);
    }

    private static async Task ReplaceEndpointAsync(HttpContext context, EndpointManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service);
        if (expectedRevision is null)
        {
            return;
        }

        var endpoint = await ReadJsonAsync(
            context,
            ManagementJsonContext.Default.MockEndpointDefinition);
        if (endpoint is null)
        {
            return;
        }

        var result = service.Replace(GetEndpointId(context), endpoint, expectedRevision.Value);
        if (result.Status != ManagementOperationStatus.Applied)
        {
            await WriteOperationProblemAsync(context, result, service);
            return;
        }

        SetETag(context, result.Snapshot!);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            result.Endpoint!,
            ManagementJsonContext.Default.MockEndpointDefinition);
    }

    private static async Task SetEnabledAsync(HttpContext context, EndpointManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service);
        if (expectedRevision is null)
        {
            return;
        }

        var request = await ReadJsonAsync(
            context,
            ManagementJsonContext.Default.EndpointEnabledRequest);
        if (request is null)
        {
            return;
        }

        var result = service.SetEnabled(GetEndpointId(context), request.Enabled, expectedRevision.Value);
        if (result.Status != ManagementOperationStatus.Applied)
        {
            await WriteOperationProblemAsync(context, result, service);
            return;
        }

        SetETag(context, result.Snapshot!);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            result.Endpoint!,
            ManagementJsonContext.Default.MockEndpointDefinition);
    }

    private static async Task DeleteEndpointAsync(HttpContext context, EndpointManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service);
        if (expectedRevision is null)
        {
            return;
        }

        var result = service.Delete(GetEndpointId(context), expectedRevision.Value);
        if (result.Status != ManagementOperationStatus.Applied)
        {
            await WriteOperationProblemAsync(context, result, service);
            return;
        }

        SetETag(context, result.Snapshot!);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task<long?> ReadExpectedRevisionAsync(
        HttpContext context,
        EndpointManagementService service)
    {
        var value = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(value))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status428PreconditionRequired,
                "precondition-required",
                "Precondition required",
                "Management writes require a quoted configuration revision in the If-Match header.",
                service.Current);
            return null;
        }

        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !long.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid-etag",
                "Invalid ETag",
                "If-Match must contain one strong ETag with a non-negative numeric revision.",
                service.Current);
            return null;
        }

        return revision;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        var mediaType = context.Request.ContentType?.Split(';', 2)[0].Trim();
        if (mediaType is null ||
            (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status415UnsupportedMediaType,
                "unsupported-media-type",
                "Unsupported media type",
                "Management request bodies must use application/json or a media type ending in +json.");
            return null;
        }

        if (context.Request.ContentLength > ConfigurationLimits.MaximumDocumentBytes)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status413PayloadTooLarge,
                "request-too-large",
                "Request too large",
                $"Management request bodies cannot exceed {ConfigurationLimits.MaximumDocumentBytes} bytes.");
            return null;
        }

        try
        {
            using var buffer = new MemoryStream();
            var bytes = new byte[64 * 1024];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
                if (count == 0)
                {
                    break;
                }

                if (buffer.Length + count > ConfigurationLimits.MaximumDocumentBytes)
                {
                    await WriteProblemAsync(
                        context,
                        StatusCodes.Status413PayloadTooLarge,
                        "request-too-large",
                        "Request too large",
                        $"Management request bodies cannot exceed {ConfigurationLimits.MaximumDocumentBytes} bytes.");
                    return null;
                }

                buffer.Write(bytes, 0, count);
            }

            return JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), typeInfo) ??
                throw new JsonException("A JSON request body is required.");
        }
        catch (JsonException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid-json",
                "Invalid JSON",
                "The request body is malformed or does not match the required contract.");
            return null;
        }
    }

    private static Task WriteOperationProblemAsync(
        HttpContext context,
        ManagementOperationResult result,
        EndpointManagementService service) =>
        result.Status switch
        {
            ManagementOperationStatus.ValidationFailed => WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "validation-failed",
                "Configuration validation failed",
                "The endpoint change was rejected and the active configuration was not modified.",
                service.Current,
                result.Validation!.Errors),
            ManagementOperationStatus.RevisionConflict => WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "revision-conflict",
                "Configuration revision conflict",
                "The active configuration changed after the supplied revision was read.",
                service.Current),
            ManagementOperationStatus.NotFound => WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "endpoint-not-found",
                "Endpoint not found",
                "No endpoint exists with the requested ID.",
                result.Snapshot),
            ManagementOperationStatus.AlreadyExists => WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "endpoint-already-exists",
                "Endpoint already exists",
                "An endpoint with the supplied ID already exists.",
                result.Snapshot),
            ManagementOperationStatus.IdMismatch => WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "endpoint-id-mismatch",
                "Endpoint ID mismatch",
                "The endpoint ID in the request body must match the route ID.",
                result.Snapshot),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Status, null)
        };

    private static Task WriteProblemAsync(
        HttpContext context,
        int status,
        string type,
        string title,
        string detail,
        ConfigurationStateSnapshot? snapshot = null,
        IReadOnlyList<ConfigurationValidationError>? errors = null)
    {
        if (snapshot is not null)
        {
            SetETag(context, snapshot);
        }

        context.Response.ContentType = "application/problem+json";
        return WriteJsonAsync(
            context,
            status,
            new ManagementProblemDetails(
                ProblemBase + type,
                title,
                status,
                detail,
                context.Request.Path,
                errors),
            ManagementJsonContext.Default.ManagementProblemDetails,
            preserveContentType: true);
    }

    private static async Task WriteJsonAsync<T>(
        HttpContext context,
        int status,
        T value,
        JsonTypeInfo<T> typeInfo,
        bool preserveContentType = false)
    {
        context.Response.StatusCode = status;
        if (!preserveContentType)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
        }

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            value,
            typeInfo,
            context.RequestAborted);
    }

    private static Guid GetEndpointId(HttpContext context) =>
        Guid.Parse((string)context.Request.RouteValues["id"]!, CultureInfo.InvariantCulture);

    private static void SetETag(HttpContext context, ConfigurationStateSnapshot snapshot) =>
        context.Response.GetTypedHeaders().ETag = new EntityTagHeaderValue(snapshot.ETag);
}