namespace MockAPI.Management;

/// <summary>Writes compact JSON SSE frames without copying the payload into a second full-size string.</summary>
internal static class ManagementEventWriter
{
    internal static async Task WriteAsync(HttpContext context, string name, string json)
    {
        var cancellation = context.RequestAborted;
        await context.Response.WriteAsync($"event: {name}\ndata: ", cancellation);
        await context.Response.WriteAsync(json, cancellation);
        await context.Response.WriteAsync("\n\n", cancellation);
    }
}
