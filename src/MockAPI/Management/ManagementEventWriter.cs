namespace MockAPI.Management;

/// <summary>Writes compact JSON SSE frames without copying the payload into a second full-size string.</summary>
internal static class ManagementEventWriter
{
    internal static async Task WriteAsync(HttpContext context, string name, string json, long? sequence = null)
    {
        var cancellation = context.RequestAborted;
        if (sequence is { } value)
        {
            await context.Response.WriteAsync($"id: {value.ToString(System.Globalization.CultureInfo.InvariantCulture)}\n", cancellation);
        }
        await context.Response.WriteAsync($"event: {name}\ndata: ", cancellation);
        await context.Response.WriteAsync(json, cancellation);
        await context.Response.WriteAsync("\n\n", cancellation);
    }
}
