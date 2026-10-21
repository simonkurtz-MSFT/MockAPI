using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Net.Http.Headers;

namespace MockAPI.Runtime;

internal static class MockRequestBodyValidator
{
    internal const int MaximumBodyBytes = 1024 * 1024;
    private const int ReadBufferBytes = 64 * 1024;
    internal const string SampleJsonBody =
        "{\"id\":\"sample-item-001\",\"name\":\"Example item\",\"status\":\"active\",\"quantity\":2,\"tags\":[\"sample\",\"dashboard\"],\"metadata\":{\"source\":\"MockAPI dashboard\"}}";
    internal const string InventorySampleJsonBody =
        "{\"sku\":\"SKU-1001\",\"quantity\":2,\"warehouseId\":\"WH-DEMO-01\"}";
    internal const string ShippingSampleJsonBody =
        "{\"service\":\"standard\",\"parcelWeightKg\":1.2,\"items\":[{\"sku\":\"SKU-1001\",\"quantity\":2}]}";

    internal static string GetSampleJsonBody(string path) => path switch
    {
        "/inventory/reservations" or "/inventory/reservations/res-demo-001" => InventorySampleJsonBody,
        "/shipping/shipments" or "/shipping/shipments/shp-demo-001" => ShippingSampleJsonBody,
        _ => SampleJsonBody
    };

    internal static bool HasSampleRequestBody(string method) =>
        HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method);

    internal static Task<MockRequestProblem?> ValidateAsync(HttpRequest request) =>
        ValidateAsync(request, ArrayPool<byte>.Shared);

    internal static async Task<MockRequestProblem?> ValidateAsync(HttpRequest request, ArrayPool<byte> bufferPool)
    {
        if (!HasSampleRequestBody(request.Method))
        {
            return null;
        }

        if (request.ContentLength > MaximumBodyBytes)
        {
            return TooLarge();
        }

        MediaTypeHeaderValue? contentType = null;
        if (request.ContentType is not null &&
            !MediaTypeHeaderValue.TryParse(request.ContentType, out contentType))
        {
            return InvalidBody("invalid-content-type", "Invalid Content-Type",
                "Content-Type must identify a single valid media type.");
        }

        var mediaType = contentType?.MediaType.Value;
        var isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) is true;
        // Each request rents its own scratch array and reuses it across that request's reads.
        // The shared pool is thread-safe; another request can rent this array only after it is returned.
        // Reuse reduces allocations once warm, but does not cap total memory held by concurrent requests.
        // Returned arrays may remain cached, so pooling does not necessarily reduce resident memory.
        var buffer = bufferPool.Rent(ReadBufferBytes);
        // Rent accumulation storage only when JSON bytes arrive. Empty and non-JSON bodies need no second buffer,
        // and keeping JSON as UTF-8 avoids allocating a decoded UTF-16 string.
        byte[]? jsonBuffer = null;
        var totalBytes = 0;
        try
        {
            while (true)
            {
                var count = await request.Body.ReadAsync(
                    buffer.AsMemory(0, ReadBufferBytes), request.HttpContext.RequestAborted);
                if (count == 0)
                {
                    break;
                }

                var previousTotalBytes = totalBytes;
                totalBytes += count;
                if (totalBytes > MaximumBodyBytes)
                {
                    return TooLarge();
                }

                if (isJson)
                {
                    // Check the actual size before growing storage. Finish reading before parsing so size and
                    // transport errors keep precedence over charset or JSON-syntax failures, even for streamed bodies.
                    jsonBuffer = EnsureJsonBufferCapacity(bufferPool, jsonBuffer, previousTotalBytes, totalBytes);
                    buffer.AsSpan(0, count).CopyTo(jsonBuffer.AsSpan(previousTotalBytes));
                }
            }

            if (totalBytes == 0)
            {
                return null;
            }

            var charset = contentType?.Charset.Value?.Trim('"');
            if (isJson && !string.IsNullOrEmpty(charset) &&
                !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
                !charset.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            {
                return new MockRequestProblem("https://mockapi.local/problems/unsupported-charset",
                    "Unsupported JSON charset", StatusCodes.Status415UnsupportedMediaType,
                    "Mock JSON request bodies must use UTF-8.");
            }

            if (jsonBuffer is not null && !IsValidJson(jsonBuffer.AsSpan(0, totalBytes)))
            {
                return InvalidJson();
            }

            return null;
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status400BadRequest)
        {
            return InvalidBody("invalid-request-body", "Invalid request body",
                "The request body could not be read as a complete HTTP payload.");
        }
        finally
        {
            // Clearing costs work proportional to rented capacity, but prevents request content surviving reuse.
            // Returning arrays in finally preserves reuse even when reading is cancelled or validation fails.
            bufferPool.Return(buffer, clearArray: true);
            if (jsonBuffer is not null)
            {
                bufferPool.Return(jsonBuffer, clearArray: true);
            }
        }
    }

    private static byte[] EnsureJsonBufferCapacity(
        ArrayPool<byte> bufferPool, byte[]? buffer, int storedBytes, int requiredBytes)
    {
        if (buffer is not null && buffer.Length >= requiredBytes)
        {
            return buffer;
        }

        // Geometric growth avoids renting and copying on every small read from a fragmented upload.
        // The caller has already enforced the body limit, and requested growth is capped at that limit.
        var capacity = buffer is null
            ? requiredBytes
            : Math.Max(requiredBytes, Math.Min(buffer.Length * 2, MaximumBodyBytes));
        var replacement = bufferPool.Rent(capacity);
        if (buffer is not null)
        {
            // Copy only populated bytes; return the replaced array immediately so later requests can reuse it.
            buffer.AsSpan(0, storedBytes).CopyTo(replacement);
            bufferPool.Return(buffer, clearArray: true);
        }

        return replacement;
    }

    private static bool IsValidJson(ReadOnlySpan<byte> json)
    {
        // Token traversal does not decode every string, so check all UTF-8 bytes independently.
        if (!Utf8.IsValid(json))
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(json);
            // Traverse the existing UTF-8 bytes without allocating JsonDocument token metadata or decoded strings.
            // Consume every token to preserve syntax checks and the default 64-level nesting limit.
            while (reader.Read())
            {
            }

            return reader.TokenType != JsonTokenType.None;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static MockRequestProblem InvalidBody(string type, string title, string detail) =>
        new($"https://mockapi.local/problems/{type}", title, StatusCodes.Status400BadRequest, detail);

    private static MockRequestProblem InvalidJson() =>
        InvalidBody("invalid-json", "Invalid JSON",
            "The request body must contain one valid UTF-8 JSON value with a maximum depth of 64.");

    private static MockRequestProblem TooLarge() =>
        new("https://mockapi.local/problems/request-too-large", "Request body too large",
            StatusCodes.Status413PayloadTooLarge, $"Mock request bodies cannot exceed {MaximumBodyBytes} bytes.");
}

internal sealed record MockRequestProblem(string Type, string Title, int Status, string Detail);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MockRequestProblem))]
internal sealed partial class MockRuntimeJsonContext : JsonSerializerContext;
