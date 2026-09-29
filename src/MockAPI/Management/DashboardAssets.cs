using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace MockAPI.Management;

internal static class DashboardAssets
{
    private const string AssetVersionPlaceholder = "{{ASSET_VERSION}}";

    internal static string CreateVersion(string webRootPath, IEnumerable<string> assetNames)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in assetNames.Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(Path.Combine(webRootPath, name)));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void Map(
        WebApplication app,
        string route,
        string sourceName,
        string contentType,
        string version,
        bool replaceVersionPlaceholder = false)
    {
        var bytes = File.ReadAllBytes(Path.Combine(app.Environment.WebRootPath, sourceName));
        if (replaceVersionPlaceholder)
        {
            var content = Encoding.UTF8.GetString(bytes)
                .Replace(AssetVersionPlaceholder, version, StringComparison.Ordinal);
            bytes = Encoding.UTF8.GetBytes(content);
        }

        var etag = new EntityTagHeaderValue($"\"{Convert.ToHexString(SHA256.HashData(bytes))}\"");
        app.MapGet(route, (HttpContext context) =>
        {
            var requestedVersion = context.Request.Query["v"].ToString();
            context.Response.Headers.CacheControl = string.Equals(requestedVersion, version, StringComparison.Ordinal)
                ? "private, max-age=31536000, immutable"
                : "private, no-cache";
            return Results.File(bytes, contentType, entityTag: etag);
        }).ExcludeFromDescription();
    }
}
