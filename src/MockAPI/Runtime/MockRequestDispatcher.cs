using Microsoft.AspNetCore.Http.Features;
using MockAPI.Configuration;

namespace MockAPI.Runtime;

/// <summary>Dispatches non-reserved HTTP requests against one captured configuration snapshot.</summary>
public static class MockRequestDispatcher
{
    /// <summary>Writes the configured exact-route response, aborts the connection, or returns <c>404</c>.</summary>
    /// <param name="context">The current HTTP request and response context.</param>
    /// <param name="configuration">The atomic configuration state used for route lookup.</param>
    /// <param name="statistics">The process-local collector updated for the dispatch outcome.</param>
    /// <returns>A task that completes after response bytes are written or the connection is aborted.</returns>
    /// <remarks>
    /// <c>HEAD</c> requests receive configured status and headers without response body bytes.
    /// Query strings do not participate in matching. Failed attempts retain the selected response status,
    /// or no status when failure occurs before response selection, including cancellation during the delay.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The request is aborted during the configured delay or while response bytes are being written.</exception>
    /// <exception cref="IOException">The response body cannot be written.</exception>
    public static async Task DispatchAsync(
        HttpContext context,
        ConfigurationState configuration,
        RequestStatisticsCollector statistics)
    {
        var path = GetPathValue(context.Request.Path);
        var requestId = context.Request.Headers["X-MockAPI-Dashboard-Request-Id"].FirstOrDefault();
        if (!configuration.Current.Endpoints.TryGet(context.Request.Method, path, out var endpoint))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            statistics.RecordUnmatched(context.Request.Method, path, requestId);
            return;
        }

        var outcome = RequestLogOutcome.FailedWrite;
        int? statusCode = null;
        var responseBytes = 0L;
        try
        {
            if (endpoint.Delay > TimeSpan.Zero)
            {
                await Task.Delay(endpoint.Delay, context.RequestAborted);
            }

            if (endpoint.Behavior == MockResponseBehavior.AbortConnection)
            {
                context.Abort();
                outcome = RequestLogOutcome.Aborted;
                return;
            }

            var response = endpoint.SelectResponse();
            statusCode = response.StatusCode;
            context.Response.StatusCode = response.StatusCode;
            if (response.ReasonPhrase is not null)
            {
                context.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = response.ReasonPhrase;
            }

            foreach (var header in response.Headers)
            {
                foreach (var value in header.Values)
                {
                    context.Response.Headers.Append(header.Name, value);
                }
            }

            if (response.ContentType is not null)
            {
                context.Response.ContentType = response.ContentType;
            }

            if (!HttpMethods.IsHead(context.Request.Method) && !response.Body.IsEmpty)
            {
                await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
                responseBytes = response.Body.Length;
            }

            outcome = RequestLogOutcome.Response;
        }
        finally
        {
            // Account once even when the delay or response write is cancelled.
            switch (outcome)
            {
                case RequestLogOutcome.Response:
                    statistics.RecordMatched(endpoint.Id, context.Request.Method, path, statusCode!.Value, responseBytes, requestId);
                    break;
                case RequestLogOutcome.Aborted:
                    statistics.RecordAbortedConnection(endpoint.Id, context.Request.Method, path, requestId);
                    break;
                default:
                    statistics.RecordFailedWrite(endpoint.Id, context.Request.Method, path, statusCode, requestId);
                    break;
            }
        }
    }

    internal static string GetPathValue(PathString path) => path.Value ?? string.Empty;
}
