using Microsoft.AspNetCore.Http.Features;
using MockAPI.Configuration;

namespace MockAPI.Runtime;

public static class MockRequestDispatcher
{
    public static async Task DispatchAsync(
        HttpContext context,
        ConfigurationState configuration,
        RequestStatisticsCollector statistics)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!configuration.Current.Endpoints.TryGet(context.Request.Method, path, out var endpoint))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            statistics.RecordUnmatched();
            return;
        }

        try
        {
            context.Response.StatusCode = endpoint.StatusCode;
            if (endpoint.ReasonPhrase is not null)
            {
                context.Features.Get<IHttpResponseFeature>()!.ReasonPhrase = endpoint.ReasonPhrase;
            }

            foreach (var header in endpoint.Headers)
            {
                foreach (var value in header.Values)
                {
                    context.Response.Headers.Append(header.Name, value);
                }
            }

            if (endpoint.ContentType is not null)
            {
                context.Response.ContentType = endpoint.ContentType;
            }

            var responseBytes = 0L;
            if (!HttpMethods.IsHead(context.Request.Method) && !endpoint.Body.IsEmpty)
            {
                await context.Response.Body.WriteAsync(endpoint.Body, context.RequestAborted);
                responseBytes = endpoint.Body.Length;
            }

            statistics.RecordMatched(endpoint.Id, endpoint.StatusCode, responseBytes);
        }
        catch
        {
            statistics.RecordFailedWrite(endpoint.Id, endpoint.StatusCode);
            throw;
        }
    }
}
