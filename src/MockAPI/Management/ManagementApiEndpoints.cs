using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Management;

public static class ManagementApiEndpoints
{
    internal const string RateLimitPolicyName = "management";
    private const string BasePath = "/__mockapi/api";
    private const string ProblemBase = "https://mockapi.local/problems/";
    private static readonly ManagementJsonContext CompactJsonContext = new(
        new JsonSerializerOptions(ManagementJsonContext.Default.Options) { WriteIndented = false });

    public static void Map(
        WebApplication app,
        EndpointManagementService service,
        ConfigurationManagementService configuration,
        RequestStatisticsCollector statistics)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(statistics);

        var group = app.MapGroup(BasePath)
            .WithTags("Management")
            .RequireRateLimiting(RateLimitPolicyName);
        group.MapGet("/configuration", (HttpContext context, CancellationToken _) => WriteConfigurationStatusAsync(context, service));
        group.MapGet("/configuration/template", (HttpContext context, CancellationToken _) => WriteBuiltInConfigurationAsync(context, "template"));
        group.MapGet("/configuration/example", (HttpContext context, CancellationToken _) => WriteBuiltInConfigurationAsync(context, "example"));
        group.MapPost("/configuration/template/merge", (HttpContext context, CancellationToken _) => MergeBuiltInConfigurationAsync(context, configuration, "template"));
        group.MapPost("/configuration/example/merge", (HttpContext context, CancellationToken _) => MergeBuiltInConfigurationAsync(context, configuration, "example"));
        group.MapPost("/configuration/validate", (HttpContext context, CancellationToken _) => ValidateConfigurationAsync(context, configuration));
        group.MapPut("/configuration/import", (HttpContext context, CancellationToken _) => ImportConfigurationAsync(context, configuration));
        group.MapGet("/configuration/export", (HttpContext context, CancellationToken _) => ExportConfigurationAsync(context, configuration));
        group.MapPost("/configuration/save", (HttpContext context, CancellationToken _) => SaveConfigurationAsync(context, configuration));
        group.MapGet("/endpoints", (HttpContext context, CancellationToken _) => WriteEndpointsAsync(context, service));
        group.MapGet("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => WriteEndpointAsync(context, service));
        group.MapPost("/endpoints", (HttpContext context, CancellationToken _) => CreateEndpointAsync(context, service));
        group.MapPut("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => ReplaceEndpointAsync(context, service));
        group.MapPut("/endpoints/{id:guid}/enabled", (HttpContext context, CancellationToken _) => SetEnabledAsync(context, service));
        group.MapDelete("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => DeleteEndpointAsync(context, service));
        group.MapGet("/statistics", (HttpContext context, CancellationToken _) => WriteStatisticsAsync(context, statistics));
        group.MapGet("/statistics/events", (HttpContext context, CancellationToken _) => StreamStatisticsAsync(context, statistics));
        group.MapPost("/statistics/reset", (HttpContext context, CancellationToken _) => ResetStatisticsAsync(context, statistics));
        group.MapPost("/statistics/endpoints/{id:guid}/reset", (HttpContext context, CancellationToken _) => ResetEndpointStatisticsAsync(context, statistics));
    }

    private static async Task WriteBuiltInConfigurationAsync(HttpContext context, string name)
    {
        await using var resource = typeof(ManagementApiEndpoints).Assembly.GetManifestResourceStream(
            $"MockAPI.BuiltIns.{name}.json") ?? throw new InvalidOperationException(
                $"The built-in {name} configuration is unavailable.");

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await resource.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private static async Task MergeBuiltInConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service,
        string name)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service.Current);
        if (expectedRevision is null)
        {
            return;
        }

        var force = bool.TryParse(context.Request.Query["force"], out var forceValue) && forceValue;
        await using var resource = typeof(ManagementApiEndpoints).Assembly.GetManifestResourceStream(
            $"MockAPI.BuiltIns.{name}.json") ?? throw new InvalidOperationException(
                $"The built-in {name} configuration is unavailable.");
        var builtIn = await JsonSerializer.DeserializeAsync(
            resource,
            MockApiJsonContext.Default.MockApiConfigurationDocument,
            context.RequestAborted) ?? throw new InvalidOperationException(
                $"The built-in {name} configuration could not be read.");

        var result = service.MergeBuiltIn(builtIn, expectedRevision.Value, force);
        if (result.Status == BuiltInMergeStatus.RevisionConflict)
        {
            await WriteRevisionConflictAsync(context, service.Current);
            return;
        }

        if (result.Status == BuiltInMergeStatus.ValidationFailed)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "validation-failed",
                "Merged configuration validation failed",
                "The built-in configuration was rejected and the active configuration was not modified.",
                service.Current,
                result.Validation.Errors);
            return;
        }

        var snapshot = result.Snapshot!;
        SetETag(context, snapshot);
        await WriteJsonAsync(
            context,
            result.Status == BuiltInMergeStatus.Conflict
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status200OK,
            new BuiltInMergeResponse(
                result.Status == BuiltInMergeStatus.Applied,
                force,
                snapshot.Revision,
                snapshot.ETag,
                snapshot.HasUnsavedChanges,
                result.Added,
                result.Updated,
                result.Skipped,
                result.Conflicts),
            ManagementJsonContext.Default.BuiltInMergeResponse);
    }

    private static async Task ValidateConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service)
    {
        var candidate = await ReadJsonAsync(
            context,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        if (candidate is null)
        {
            return;
        }

        var validation = service.Validate(candidate);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new ConfigurationValidationResponse(validation.IsValid, validation.Errors),
            ManagementJsonContext.Default.ConfigurationValidationResponse);
    }

    private static async Task ImportConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service.Current);
        if (expectedRevision is null)
        {
            return;
        }

        var candidate = await ReadJsonAsync(
            context,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        if (candidate is null)
        {
            return;
        }

        var result = service.Import(candidate, expectedRevision.Value);
        if (result.Status == ConfigurationUpdateStatus.ValidationFailed)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "validation-failed",
                "Configuration validation failed",
                "The imported configuration was rejected and the active configuration was not modified.",
                service.Current,
                result.Validation.Errors);
            return;
        }

        if (result.Status == ConfigurationUpdateStatus.RevisionConflict)
        {
            await WriteRevisionConflictAsync(context, service.Current);
            return;
        }

        SetETag(context, result.Snapshot!);
        await WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            new ConfigurationStatusResponse(
                result.Snapshot!.Revision,
                result.Snapshot.ETag,
                result.Snapshot.HasUnsavedChanges),
            ManagementJsonContext.Default.ConfigurationStatusResponse);
    }

    private static async Task ExportConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service)
    {
        var snapshot = service.Current;
        SetETag(context, snapshot);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.ContentDisposition = "attachment; filename=mockapi.json";
        await context.Response.Body.WriteAsync(snapshot.ExportUtf8(), context.RequestAborted);
    }

    private static async Task SaveConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service.Current);
        if (expectedRevision is null)
        {
            return;
        }

        try
        {
            var result = await service.SaveAsync(expectedRevision.Value, context.RequestAborted);
            var snapshot = service.Current;
            SetETag(context, snapshot);
            await WriteJsonAsync(
                context,
                StatusCodes.Status200OK,
                new ConfigurationSaveResponse(result.Revision, result.IsCurrentRevision),
                ManagementJsonContext.Default.ConfigurationSaveResponse);
        }
        catch (ConfigurationPersistenceException exception)
            when (exception.Error == ConfigurationPersistenceError.RevisionConflict)
        {
            await WriteRevisionConflictAsync(context, service.Current);
        }
        catch (ConfigurationPersistenceException)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status500InternalServerError,
                "persistence-failed",
                "Configuration save failed",
                "The active configuration could not be saved to the configured persistence location.",
                service.Current);
        }
    }

    private static Task WriteStatisticsAsync(HttpContext context, RequestStatisticsCollector statistics) =>
        WriteJsonAsync(
            context,
            StatusCodes.Status200OK,
            statistics.GetSnapshot(),
            ManagementJsonContext.Default.RequestStatisticsSnapshot);

    private static async Task StreamStatisticsAsync(
        HttpContext context,
        RequestStatisticsCollector statistics)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                var json = JsonSerializer.Serialize(
                    statistics.GetSnapshot(),
                    CompactJsonContext.RequestStatisticsSnapshot);
                await context.Response.WriteAsync("event: statistics\ndata: ", context.RequestAborted);
                await context.Response.WriteAsync(json, context.RequestAborted);
                await context.Response.WriteAsync("\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
    }

    private static Task ResetStatisticsAsync(HttpContext context, RequestStatisticsCollector statistics)
    {
        statistics.Reset();
        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
    }

    private static Task ResetEndpointStatisticsAsync(HttpContext context, RequestStatisticsCollector statistics)
    {
        if (!statistics.Reset(GetEndpointId(context)))
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "endpoint-statistics-not-found",
                "Endpoint statistics not found",
                "No statistics exist for the requested endpoint ID.");
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
        return Task.CompletedTask;
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
        context.Response.Headers.Location = $"{BasePath}/endpoints/{endpoint.Id}";
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
        => await ReadExpectedRevisionAsync(context, service.Current);

    private static async Task<long?> ReadExpectedRevisionAsync(
        HttpContext context,
        ConfigurationStateSnapshot snapshot)
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
                snapshot);
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
                snapshot);
            return null;
        }

        return revision;
    }

    private static Task WriteRevisionConflictAsync(
        HttpContext context,
        ConfigurationStateSnapshot snapshot) =>
        WriteProblemAsync(
            context,
            StatusCodes.Status412PreconditionFailed,
            "revision-conflict",
            "Configuration revision conflict",
            "The active configuration changed after the supplied revision was read.",
            snapshot);

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