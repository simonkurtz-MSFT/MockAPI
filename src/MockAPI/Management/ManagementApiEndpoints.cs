using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Management;

/// <summary>Maps the reserved, rate-limited management HTTP API onto domain services.</summary>
public static class ManagementApiEndpoints
{
    internal const string RateLimitPolicyName = "management";
    private const string BasePath = "/__mockapi/api";
    private const string ProblemBase = "https://mockapi.local/problems/";
    private static readonly ManagementJsonContext CompactJsonContext = new(
        new JsonSerializerOptions(ManagementJsonContext.Default.Options) { WriteIndented = false });

    /// <summary>Maps all management configuration, endpoint, and statistics routes under <c>/__mockapi/api</c>.</summary>
    /// <param name="app">The application receiving the routes.</param>
    /// <param name="service">The endpoint mutation service.</param>
    /// <param name="configuration">The configuration management and persistence service.</param>
    /// <param name="statistics">The process-local statistics collector.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
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

        MapConfigurationEndpoints(group, service, configuration);
        MapEndpointEndpoints(group, service);
        MapStatisticsEndpoints(group, statistics);
        var dashboardEvents = new DashboardEventStream(app.Services.GetRequiredService<ConfigurationState>(), statistics);
        group.MapGet("/dashboard/events", (HttpContext context, CancellationToken _) => dashboardEvents.WriteAsync(context))
            .WithName("StreamDashboardEvents")
            .WithSummary("Stream configuration changes and live statistics")
            .WithDescription("Sends current configuration status and statistics on connect, configuration events after edits or saves, coalesced statistics events, and idle keepalive comments. Reconnect to resynchronize; no event history is retained.")
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream");
    }

    private static void MapConfigurationEndpoints(
        RouteGroupBuilder group,
        EndpointManagementService service,
        ConfigurationManagementService configuration)
    {
        group.MapGet("/configuration", (HttpContext context, CancellationToken _) => WriteConfigurationStatusAsync(context, service))
            .WithName("GetConfigurationStatus")
            .WithSummary("Get the active configuration status")
            .WithDescription("Returns the active revision, strong ETag, API descriptions, and whether the configuration has unsaved changes.")
            .Produces<ConfigurationStatusResponse>(StatusCodes.Status200OK, "application/json");

        group.MapGet("/configuration/example", (HttpContext context, CancellationToken _) => WriteBuiltInConfigurationAsync(context, "example"))
            .WithName("GetBuiltInExample")
            .WithSummary("Get the built-in example configuration")
            .Produces<MockApiConfigurationDocument>(StatusCodes.Status200OK, "application/json");

        WithProblemResponses(
                group.MapPost("/configuration/example/merge", (HttpContext context, CancellationToken _) => MergeBuiltInConfigurationAsync(context, configuration, "example"))
                    .WithName("MergeBuiltInConfiguration")
                    .WithSummary("Merge the built-in example into the active configuration")
                    .WithDescription("Requires the current strong ETag in If-Match. Use the force query parameter only after reviewing a conflict response.")
                    .Produces<BuiltInMergeResponse>(StatusCodes.Status200OK, "application/json")
                    .Produces<BuiltInMergeResponse>(StatusCodes.Status409Conflict, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapPost("/configuration/validate", (HttpContext context, CancellationToken _) => ValidateConfigurationAsync(context, configuration))
                    .WithName("ValidateConfiguration")
                    .WithSummary("Validate a configuration without applying it")
                    .WithJsonRequestBody<MockApiConfigurationDocument>()
                    .Produces<ConfigurationValidationResponse>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType);

        WithProblemResponses(
                group.MapPut("/configuration/import", (HttpContext context, CancellationToken _) => ImportConfigurationAsync(context, configuration))
                    .WithName("ImportConfiguration")
                    .WithSummary("Replace the active configuration")
                    .WithDescription("Validates and atomically applies a complete configuration. Requires the current strong ETag in If-Match.")
                    .WithJsonRequestBody<MockApiConfigurationDocument>()
                    .Produces<ConfigurationStatusResponse>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapPut("/configuration/api-description", (HttpContext context, CancellationToken _) => SetApiDescriptionAsync(context, configuration))
                    .WithName("SetApiDescription")
                    .WithSummary("Edit a path-based API description")
                    .WithDescription("Requires the current strong ETag in If-Match. Preserves all endpoints and other API descriptions. An empty description explicitly clears the displayed text.")
                    .WithJsonRequestBody<ApiDescriptionRequest>()
                    .Produces<ConfigurationStatusResponse>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        group.MapGet("/configuration/export", (HttpContext context, CancellationToken _) => ExportConfigurationAsync(context, configuration))
            .WithName("ExportConfiguration")
            .WithSummary("Export the active MockAPI configuration")
            .Produces(StatusCodes.Status200OK, contentType: "application/json");

        WithProblemResponses(
                group.MapGet("/configuration/export/{format}", (HttpContext context, string format, bool? download, CancellationToken _) => ExportConfigurationAsync(context, configuration, format, download ?? true))
                    .WithName("ExportConfigurationFormat")
                    .WithSummary("Export the active configuration in a supported format")
                    .WithDescription("Downloads an attachment by default. Set the optional download query parameter to false to view the artifact inline in the browser.")
                    .Produces(StatusCodes.Status200OK, contentType: "application/octet-stream"),
                StatusCodes.Status404NotFound);

        WithProblemResponses(
                group.MapPost("/configuration/save", (HttpContext context, CancellationToken _) => SaveConfigurationAsync(context, configuration))
                    .WithName("SaveConfiguration")
                    .WithSummary("Persist the active configuration")
                    .WithDescription("Persists one immutable snapshot and requires its current strong ETag in If-Match.")
                    .Produces<ConfigurationSaveResponse>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status428PreconditionRequired,
                StatusCodes.Status500InternalServerError);
    }

    private static void MapEndpointEndpoints(RouteGroupBuilder group, EndpointManagementService service)
    {
        group.MapGet("/endpoints", (HttpContext context, CancellationToken _) => WriteEndpointsAsync(context, service))
            .WithName("GetEndpoints")
            .WithSummary("List the active mock endpoints")
            .Produces<MockEndpointDefinition[]>(StatusCodes.Status200OK, "application/json");

        WithProblemResponses(
                group.MapPost("/endpoints/bulk", (HttpContext context, CancellationToken _) => ApplyBulkEndpointOperationAsync(context, service))
                    .WithName("ApplyBulkEndpointOperation")
                    .WithSummary("Enable, disable, or delete multiple endpoints")
                    .WithDescription("Applies the operation atomically and requires the current strong ETag in If-Match.")
                    .WithJsonRequestBody<BulkEndpointRequest>()
                    .Produces(StatusCodes.Status204NoContent),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapGet("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => WriteEndpointAsync(context, service))
                    .WithName("GetEndpoint")
                    .WithSummary("Get one mock endpoint")
                    .Produces<MockEndpointDefinition>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status404NotFound);

        WithProblemResponses(
                group.MapPost("/endpoints", (HttpContext context, CancellationToken _) => CreateEndpointAsync(context, service))
                    .WithName("CreateEndpoint")
                    .WithSummary("Create a mock endpoint")
                    .WithDescription("Atomically creates an endpoint and requires the current strong ETag in If-Match.")
                    .WithJsonRequestBody<MockEndpointDefinition>()
                    .Produces<MockEndpointDefinition>(StatusCodes.Status201Created, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status409Conflict,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapPut("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => ReplaceEndpointAsync(context, service))
                    .WithName("ReplaceEndpoint")
                    .WithSummary("Replace a mock endpoint")
                    .WithDescription("Atomically replaces an endpoint and requires the current strong ETag in If-Match.")
                    .WithJsonRequestBody<MockEndpointDefinition>()
                    .Produces<MockEndpointDefinition>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapPut("/endpoints/{id:guid}/enabled", (HttpContext context, CancellationToken _) => SetEnabledAsync(context, service))
                    .WithName("SetEndpointEnabled")
                    .WithSummary("Enable or disable a mock endpoint")
                    .WithDescription("Atomically updates the enabled state and requires the current strong ETag in If-Match.")
                    .WithJsonRequestBody<EndpointEnabledRequest>()
                    .Produces<MockEndpointDefinition>(StatusCodes.Status200OK, "application/json"),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status413PayloadTooLarge,
                StatusCodes.Status415UnsupportedMediaType,
                StatusCodes.Status422UnprocessableEntity,
                StatusCodes.Status428PreconditionRequired);

        WithProblemResponses(
                group.MapDelete("/endpoints/{id:guid}", (HttpContext context, CancellationToken _) => DeleteEndpointAsync(context, service))
                    .WithName("DeleteEndpoint")
                    .WithSummary("Delete a mock endpoint")
                    .WithDescription("Atomically deletes an endpoint and requires the current strong ETag in If-Match.")
                    .Produces(StatusCodes.Status204NoContent),
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status412PreconditionFailed,
                StatusCodes.Status428PreconditionRequired);
    }

    private static void MapStatisticsEndpoints(RouteGroupBuilder group, RequestStatisticsCollector statistics)
    {
        group.MapGet("/statistics", (HttpContext context, CancellationToken _) => WriteStatisticsAsync(context, statistics))
            .WithName("GetStatistics")
            .WithSummary("Get aggregate and per-endpoint request statistics")
            .Produces<RequestStatisticsSnapshot>(StatusCodes.Status200OK, "application/json");

        group.MapGet("/statistics/events", (HttpContext context, CancellationToken _) => StreamStatisticsAsync(context, statistics))
            .WithName("StreamStatisticsEvents")
            .WithSummary("Stream request statistics as server-sent events")
            .Produces<string>(StatusCodes.Status200OK, "text/event-stream");

        group.MapPost("/statistics/reset", (HttpContext context, CancellationToken _) => ResetStatisticsAsync(context, statistics))
            .WithName("ResetAllStatistics")
            .WithSummary("Reset all process-local request statistics")
            .Produces(StatusCodes.Status204NoContent);

        WithProblemResponses(
                group.MapPost("/statistics/endpoints/{id:guid}/reset", (HttpContext context, CancellationToken _) => ResetEndpointStatisticsAsync(context, statistics))
                    .WithName("ResetEndpointStatistics")
                    .WithSummary("Reset statistics for one endpoint")
                    .Produces(StatusCodes.Status204NoContent),
                StatusCodes.Status404NotFound);
    }

    private static RouteHandlerBuilder WithProblemResponses(RouteHandlerBuilder builder, params int[] statusCodes)
    {
        foreach (var statusCode in statusCodes)
        {
            builder.Produces<ManagementProblemDetails>(statusCode, "application/problem+json");
        }

        return builder;
    }

    private static RouteHandlerBuilder WithJsonRequestBody<TRequest>(this RouteHandlerBuilder builder)
    {
        builder.AddOpenApiOperationTransformer(async (operation, context, cancellationToken) =>
        {
            _ = await context.GetOrCreateSchemaAsync(typeof(TRequest), null, cancellationToken);
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchemaReference(typeof(TRequest).Name, context.Document, null)
                    }
                }
            };
        });

        return builder;
    }

    private static async Task WriteBuiltInConfigurationAsync(HttpContext context, string name)
    {
        await using var resource = OpenBuiltInConfiguration(name);

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
        await using var resource = OpenBuiltInConfiguration(name);
        var builtIn = await ReadBuiltInConfigurationAsync(resource, name, context.RequestAborted);

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
        await WriteConfigurationUpdateAsync(context, service, result);
    }

    private static async Task SetApiDescriptionAsync(HttpContext context, ConfigurationManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service.Current);
        if (expectedRevision is null)
        {
            return;
        }

        var request = await ReadJsonAsync(context, ManagementJsonContext.Default.ApiDescriptionRequest);
        if (request is null)
        {
            return;
        }

        if (request.Path is null || request.Description is null)
        {
            await WriteProblemAsync(context, StatusCodes.Status400BadRequest, "invalid-json", "Invalid JSON",
                "API path and description must be strings.");
            return;
        }

        var result = service.SetApiDescription(request.Path, request.Description, expectedRevision.Value);
        await WriteConfigurationUpdateAsync(context, service, result);
    }

    private static async Task WriteConfigurationUpdateAsync(
        HttpContext context,
        ConfigurationManagementService service,
        ConfigurationUpdateResult result)
    {
        if (result.Status == ConfigurationUpdateStatus.ValidationFailed)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "validation-failed",
                "Configuration validation failed",
                "The configuration change was rejected and the active configuration was not modified.",
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
            ConfigurationStatusResponse.FromSnapshot(result.Snapshot!),
            ManagementJsonContext.Default.ConfigurationStatusResponse);
    }

    private static async Task ExportConfigurationAsync(
        HttpContext context,
        ConfigurationManagementService service,
        string? format = null,
        bool download = true)
    {
        var snapshot = service.Current;
        SetETag(context, snapshot);
        if (format is not null)
        {
            if (!ConfigurationExportService.TryExport(format, snapshot.GetDocument(), out var export,
                context.RequestServices.GetRequiredService<ApiKeySecurity>().Status.Enabled))
            {
                await WriteProblemAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    "export-format-not-found",
                    "Export format not found",
                    $"The export format '{format}' is not supported.",
                    snapshot);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = export!.ContentType;
            var disposition = download ? "attachment" : "inline";
            context.Response.Headers.ContentDisposition = $"{disposition}; filename={export.FileName}";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.Body.WriteAsync(export.Content, context.RequestAborted);
            return;
        }

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

    internal static async Task StreamStatisticsAsync(
        HttpContext context,
        RequestStatisticsCollector statistics)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";

        while (!context.RequestAborted.IsCancellationRequested)
        {
            var json = JsonSerializer.Serialize(
                statistics.GetSnapshot(),
                CompactJsonContext.RequestStatisticsSnapshot);
            await context.Response.WriteAsync("event: statistics\ndata: ", context.RequestAborted);
            await context.Response.WriteAsync(json, context.RequestAborted);
            await context.Response.WriteAsync("\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(2), context.RequestAborted)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
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
            ConfigurationStatusResponse.FromSnapshot(snapshot),
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

    private static async Task ApplyBulkEndpointOperationAsync(HttpContext context, EndpointManagementService service)
    {
        var expectedRevision = await ReadExpectedRevisionAsync(context, service);
        if (expectedRevision is null)
        {
            return;
        }

        var request = await ReadJsonAsync(
            context,
            ManagementJsonContext.Default.BulkEndpointRequest);
        if (request is null)
        {
            return;
        }

        if (request.EndpointIds is not { Count: > 0 and <= ConfigurationLimits.MaximumEndpoints })
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid-bulk-selection",
                "Invalid bulk selection",
                $"Select between 1 and {ConfigurationLimits.MaximumEndpoints} endpoints.",
                service.Current);
            return;
        }

        if (Enum.IsDefined(request.Operation) is false)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "invalid-bulk-operation",
                "Invalid bulk operation",
                "Operation must be enable, disable, or delete.",
                service.Current);
            return;
        }

        if (request.EndpointIds.Distinct().Count() != request.EndpointIds.Count)
        {
            await WriteProblemAsync(
                context,
                StatusCodes.Status400BadRequest,
                "duplicate-bulk-selection",
                "Duplicate bulk selection",
                "Each selected endpoint ID must appear only once.",
                service.Current);
            return;
        }

        var result = service.ApplyBulk(request.EndpointIds, request.Operation, expectedRevision.Value);
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

    internal static async Task<T?> ReadJsonAsync<T>(HttpContext context, JsonTypeInfo<T> typeInfo)
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

            return DeserializeRequired(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), typeInfo);
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

    internal static T DeserializeRequired<T>(ReadOnlySpan<byte> json, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(json, typeInfo) ?? throw new JsonException("A JSON request body is required.");

    internal static Stream OpenBuiltInConfiguration(string name) =>
        typeof(ManagementApiEndpoints).Assembly.GetManifestResourceStream(
            $"MockAPI.BuiltIns.{name}.json") ?? throw new InvalidOperationException(
                $"The built-in {name} configuration is unavailable.");

    internal static async Task<MockApiConfigurationDocument> ReadBuiltInConfigurationAsync(
        Stream resource,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return await JsonSerializer.DeserializeAsync(
            resource,
            MockApiJsonContext.Default.MockApiConfigurationDocument,
            cancellationToken) ?? throw new InvalidOperationException(
                $"The built-in {name} configuration could not be read.");
    }

    private static Task WriteOperationProblemAsync(
        HttpContext context,
        ManagementOperationResult result,
        EndpointManagementService service)
    {
        if (result.Status == ManagementOperationStatus.ValidationFailed)
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status422UnprocessableEntity,
                "validation-failed",
                "Configuration validation failed",
                "The endpoint change was rejected and the active configuration was not modified.",
                service.Current,
                result.Validation!.Errors);
        }

        if (result.Status == ManagementOperationStatus.RevisionConflict)
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status412PreconditionFailed,
                "revision-conflict",
                "Configuration revision conflict",
                "The active configuration changed after the supplied revision was read.",
                service.Current);
        }

        if (result.Status == ManagementOperationStatus.NotFound)
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status404NotFound,
                "endpoint-not-found",
                "Endpoint not found",
                "No endpoint exists with the requested ID.",
                result.Snapshot);
        }

        if (result.Status == ManagementOperationStatus.AlreadyExists)
        {
            return WriteProblemAsync(
                context,
                StatusCodes.Status409Conflict,
                "endpoint-already-exists",
                "Endpoint already exists",
                "An endpoint with the supplied ID already exists.",
                result.Snapshot);
        }

        return WriteProblemAsync(
            context,
            StatusCodes.Status400BadRequest,
            "endpoint-id-mismatch",
            "Endpoint ID mismatch",
            "The endpoint ID in the request body must match the route ID.",
            result.Snapshot);
    }

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
        context.Response.Headers.CacheControl = "no-store";
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
