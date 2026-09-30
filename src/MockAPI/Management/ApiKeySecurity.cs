using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using MockAPI.Configuration;

namespace MockAPI.Management;

/// <summary>Reports API-key enforcement without exposing the key or its hash.</summary>
/// <param name="Enabled">Whether mock requests require a key.</param>
/// <param name="Configured">Whether a key has been generated.</param>
/// <param name="ETag">The independent strong security-settings revision.</param>
public sealed record ApiSecurityStatus(bool Enabled, bool Configured, [property: JsonPropertyName("etag")] string ETag);

/// <summary>Requests an explicit change to mock API-key enforcement.</summary>
/// <param name="Enabled">Whether mock requests should require a key.</param>
public sealed record ApiSecurityRequest([property: JsonRequired] bool Enabled);

/// <summary>Returns a newly generated key exactly once, together with the committed settings.</summary>
/// <param name="Key">The secret callers must supply in X-MockAPI-Key.</param>
/// <param name="Status">The persisted security settings.</param>
public sealed record ApiKeyCreated(string Key, ApiSecurityStatus Status);

internal sealed record ApiSecurityDocument(
    [property: JsonRequired] bool Enabled,
    [property: JsonRequired] string? KeyHash,
    [property: JsonRequired] long Revision);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ApiSecurityDocument))]
internal sealed partial class ApiSecurityJsonContext : JsonSerializerContext;

internal sealed class ApiKeySecurity(MockApiOptions options, BlobClient? blobClient = null) : IDisposable
{
    internal const string HeaderName = "X-MockAPI-Key";
    private const int MaximumSecurityDocumentBytes = 4096;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private ApiSecurityDocument _current = new(options.RequireApiKey, null, 0);
    private readonly string _path = options.ConfigurationPath + ".security.json";
    private readonly BlobClient? _blobClient = blobClient ?? (options.ConfigurationBlobUri is null ? null : CreateBlobClient(options));

    internal ApiSecurityStatus Status
    {
        get
        {
            var snapshot = Volatile.Read(ref _current);
            return new(snapshot.Enabled, snapshot.KeyHash is not null, $"\"{snapshot.Revision}\"");
        }
    }

    internal bool Authorizes(HttpContext context)
    {
        var snapshot = Volatile.Read(ref _current);
        if (!snapshot.Enabled)
        {
            return true;
        }

        var values = context.Request.Headers[HeaderName];
        if (snapshot.KeyHash is null || values.Count != 1 || values[0] is not { Length: 43 } key)
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return CryptographicOperations.FixedTimeEquals(suppliedHash, Convert.FromHexString(snapshot.KeyHash));
    }

    internal async Task LoadAsync(CancellationToken cancellationToken)
    {
        byte[]? content;
        if (options.ConfigurationBlobUri is not null)
        {
            try
            {
                var response = await _blobClient!.DownloadStreamingAsync(cancellationToken: cancellationToken);
                await using var stream = response.Value.Content;
                if (response.Value.Details.ContentLength > MaximumSecurityDocumentBytes)
                {
                    throw new InvalidOperationException("API security settings exceed the maximum size.");
                }
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                content = buffer.ToArray();
            }
            catch (RequestFailedException exception) when (exception.Status == 404)
            {
                return;
            }
        }
        else
        {
            if (!File.Exists(_path))
            {
                return;
            }
            if (new FileInfo(_path).Length > MaximumSecurityDocumentBytes)
            {
                throw new InvalidOperationException("API security settings exceed the maximum size.");
            }
            content = await File.ReadAllBytesAsync(_path, cancellationToken);
        }

        var document = JsonSerializer.Deserialize(content, ApiSecurityJsonContext.Default.ApiSecurityDocument)
            ?? throw new InvalidOperationException("API security settings must contain a JSON document.");
        if (document.Revision < 1 || document.Revision == long.MaxValue || document.KeyHash is { } hash &&
            (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)))
        {
            throw new InvalidOperationException("API security settings contain an invalid revision or key hash.");
        }

        Volatile.Write(ref _current, document);
    }

    internal async Task<ApiKeyCreated?> UpdateAsync(
        string expectedETag,
        bool enabled,
        bool rotate,
        CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            if (Status.ETag != expectedETag)
            {
                return null;
            }

            var key = rotate
                ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                : null;
            var previous = Volatile.Read(ref _current);
            var hash = key is null ? previous.KeyHash : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
            var candidate = new ApiSecurityDocument(enabled, hash, checked(previous.Revision + 1));
            var content = JsonSerializer.SerializeToUtf8Bytes(candidate, ApiSecurityJsonContext.Default.ApiSecurityDocument);
            await PersistAsync(content, cancellationToken);
            // Publish only after durable replacement; failed saves never change request authorization.
            Volatile.Write(ref _current, candidate);
            return new ApiKeyCreated(key ?? string.Empty, Status);
        }
        finally
        {
            _writes.Release();
        }
    }

    private static BlobClient CreateBlobClient(MockApiOptions options)
    {
        var uri = new BlobUriBuilder(options.ConfigurationBlobUri!) { BlobName = new BlobUriBuilder(options.ConfigurationBlobUri!).BlobName + ".security.json" };
        return new BlobClient(uri.ToUri(), new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = options.ManagedIdentityClientId
        }));
    }

    private async Task PersistAsync(byte[] content, CancellationToken cancellationToken)
    {
        if (options.ConfigurationBlobUri is not null)
        {
            await _blobClient!.UploadAsync(BinaryData.FromBytes(content), overwrite: true, cancellationToken);
            return;
        }

        var target = Path.GetFullPath(_path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public void Dispose() => _writes.Dispose();
}

internal static class ApiSecurityEndpoints
{
    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup("/__mockapi/api/security")
            .WithTags("Security")
            .RequireRateLimiting(ManagementApiEndpoints.RateLimitPolicyName);
        group.MapGet("/", (HttpContext context, ApiKeySecurity security) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.ETag = security.Status.ETag;
            return Results.Json(security.Status, ManagementJsonContext.Default.ApiSecurityStatus);
        })
            .WithName("GetApiSecurity")
            .WithSummary("Get mock API-key security settings")
            .Produces<ApiSecurityStatus>()
            .Produces<ManagementProblemDetails>(401, "application/problem+json")
            .Produces<ManagementProblemDetails>(403, "application/problem+json");
        group.MapPut("/", (HttpContext context, ApiSecurityRequest request, ApiKeySecurity security, ILogger<ApiKeySecurity> logger) =>
            UpdateAsync(context, security, request.Enabled, false, logger))
            .WithName("SetApiSecurity")
            .WithSummary("Enable or explicitly disable mock API-key enforcement")
            .WithDescription("Requires administrator credentials and the current security settings strong ETag in If-Match. Persists before activation.")
            .Produces<ApiSecurityStatus>()
            .Produces<ManagementProblemDetails>(400, "application/problem+json")
            .Produces<ManagementProblemDetails>(415, "application/problem+json")
            .Produces<ManagementProblemDetails>(401, "application/problem+json")
            .Produces<ManagementProblemDetails>(403, "application/problem+json")
            .Produces<ManagementProblemDetails>(412, "application/problem+json")
            .Produces<ManagementProblemDetails>(428, "application/problem+json")
            .Produces<ManagementProblemDetails>(500, "application/problem+json");
        group.MapPost("/key", (HttpContext context, ApiKeySecurity security, ILogger<ApiKeySecurity> logger) =>
            UpdateAsync(context, security, true, true, logger))
            .WithName("RotateMockApiKey")
            .WithSummary("Generate a mock API key and revoke the previous key")
            .WithDescription("Requires administrator credentials and the current security settings strong ETag in If-Match. Enables enforcement and returns the secret only in this no-store response.")
            .Produces<ApiKeyCreated>()
            .Produces<ManagementProblemDetails>(401, "application/problem+json")
            .Produces<ManagementProblemDetails>(403, "application/problem+json")
            .Produces<ManagementProblemDetails>(412, "application/problem+json")
            .Produces<ManagementProblemDetails>(428, "application/problem+json")
            .Produces<ManagementProblemDetails>(500, "application/problem+json");
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext context, ApiKeySecurity security, bool enabled, bool rotate, ILogger logger)
    {
        context.Response.Headers.CacheControl = "no-store";
        var expected = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrEmpty(expected))
        {
            return Problem(428, "A current security settings ETag is required in If-Match.");
        }
        try
        {
            var result = await security.UpdateAsync(expected, enabled, rotate, context.RequestAborted);
            context.Response.Headers.ETag = security.Status.ETag;
            if (result is null)
            {
                return Problem(412, "Security settings changed. Reload Settings before trying again.");
            }
            return rotate
                ? Results.Json(result, ManagementJsonContext.Default.ApiKeyCreated)
                : Results.Json(result.Status, ManagementJsonContext.Default.ApiSecurityStatus);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or RequestFailedException or AuthenticationFailedException)
        {
            logger.LogError("API security settings could not be persisted ({ErrorType}).", exception.GetType().Name);
            return Problem(500, "Security settings could not be saved. Active authorization is unchanged.");
        }
    }

    internal static IResult Problem(int status, string detail) => Results.Json(
        new ManagementProblemDetails("https://mockapi.local/problems/api-security", "API security", status, detail, "/__mockapi/api/security"),
        ManagementJsonContext.Default.ManagementProblemDetails, statusCode: status, contentType: "application/problem+json");
}
