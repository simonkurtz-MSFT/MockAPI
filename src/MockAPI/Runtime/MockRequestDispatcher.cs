using Microsoft.AspNetCore.Http.Features;
using MockAPI.Configuration;

namespace MockAPI.Runtime;

public static class MockRequestDispatcher
{
    private static readonly byte[] RootResponse = "MockAPI is running.\n"u8.ToArray();

    public static async Task DispatchAsync(
        HttpContext context,
        ConfigurationState configuration,
        RequestStatisticsCollector statistics)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (!configuration.Current.Endpoints.TryGet(context.Request.Method, path, out var endpoint))
        {
            if (path == "/" &&
                (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.ContentLength = RootResponse.Length;
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    await context.Response.Body.WriteAsync(RootResponse, context.RequestAborted);
                }

                return;
            }

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