using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Net.Http.Headers;

namespace MockAPI.Runtime;

internal static class MockRequestBodyValidator
{
    internal const int MaximumBodyBytes = 1024 * 1024;
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

    internal static async Task<MockRequestProblem?> ValidateAsync(HttpRequest request)
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
        using var jsonBuffer = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var totalBytes = 0;
        try
        {
            while (true)
            {
                var count = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted);
                if (count == 0)
                {
                    break;
                }

                totalBytes += count;
                if (totalBytes > MaximumBodyBytes)
                {
                    return TooLarge();
                }

                if (isJson)
                {
                    jsonBuffer.Write(buffer, 0, count);
                }
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status400BadRequest)
        {
            return InvalidBody("invalid-request-body", "Invalid request body",
                "The request body could not be read as a complete HTTP payload.");
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

        if (isJson)
        {
            var json = jsonBuffer.GetBuffer().AsMemory(0, totalBytes);
            // JsonDocument defers decoding some strings, so validate every UTF-8 byte explicitly.
            if (!Utf8.IsValid(json.Span))
            {
                return InvalidJson();
            }

            try
            {
                // Parse syntax only. No application fields or values influence the configured response.
                using var document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return InvalidJson();
            }
        }

        return null;
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
