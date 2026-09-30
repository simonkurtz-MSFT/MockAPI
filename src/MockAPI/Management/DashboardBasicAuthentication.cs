using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Primitives;
using MockAPI.Configuration;

namespace MockAPI.Management;

internal sealed class DashboardBasicAuthentication(RequestDelegate next, MockApiOptions options)
{
    private const string Scheme = "Basic";
    private const string HashVersion = "v1";
    private const int MinimumIterations = 100_000;
    private const int SaltLength = 16;
    private const int HashLength = 32;
    private readonly PasswordHash? _passwordHash = ParseConfiguration(options);

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/__mockapi/api/security") && _passwordHash is null)
        {
            context.Response.Headers.CacheControl = "no-store";
            await ApiSecurityEndpoints.Problem(403,
                "Configure dashboard administrator credentials before managing API security.").ExecuteAsync(context);
            return;
        }

        if (_passwordHash is null || !IsAdministrativePath(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (TryReadCredentials(context.Request.Headers.Authorization, out var username, out var password)
            && FixedTimeEquals(username, options.DashboardUsername!)
            && VerifyPassword(password, _passwordHash.Value))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"MockAPI Dashboard\", charset=\"UTF-8\"";
        context.Response.Headers.CacheControl = "no-store";
    }

    internal static bool IsAdministrativePath(PathString path) =>
        path == "/"
        || path == "/app.css"
        || path == "/app.js"
        || path == "/brand-mark.svg"
        || path == "/dashboard-api-description.js"
        || path == "/dashboard-api-security.js"
        || path == "/dashboard-core.js"
        || path == "/dashboard-dom.js"
        || path == "/dashboard-editor-dialog.js"
        || path == "/dashboard-endpoint-editor.js"
        || path == "/dashboard-endpoint-table.js"
        || path == "/dashboard-layout.js"
        || path == "/dashboard-management.js"
        || path == "/dashboard-preferences.js"
        || path == "/dashboard-statistics.js"
        || path == "/dashboard-sync.js"
        || path == "/dashboard-test-blade.js"
        || path == "/dashboard-test-request.js"
        || path == "/dashboard-tutorial.js"
        || path == "/favicon.ico"
        || path == "/favicon.svg"
        || path.StartsWithSegments("/__mockapi");

    internal static void ValidateConfiguration(MockApiOptions options) =>
        _ = ParseConfiguration(options);

    private static PasswordHash? ParseConfiguration(MockApiOptions options)
    {
        var hasUsername = !string.IsNullOrWhiteSpace(options.DashboardUsername);
        var hasPasswordHash = !string.IsNullOrWhiteSpace(options.DashboardPasswordHash);
        if (hasUsername != hasPasswordHash)
        {
            throw new InvalidOperationException(
                "MockApi:DashboardUsername and MockApi:DashboardPasswordHash must be configured together.");
        }
        if (!hasUsername)
        {
            return null;
        }

        var parts = options.DashboardPasswordHash!.Split('.', StringSplitOptions.None);
        if (parts.Length != 4
            || parts[0] != HashVersion
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)
            || iterations < MinimumIterations)
        {
            throw new InvalidOperationException("MockApi:DashboardPasswordHash has an invalid format.");
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var hash = Convert.FromBase64String(parts[3]);
            if (salt.Length != SaltLength || hash.Length != HashLength)
            {
                throw new InvalidOperationException("MockApi:DashboardPasswordHash has an invalid format.");
            }
            return new PasswordHash(iterations, salt, hash);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "MockApi:DashboardPasswordHash has an invalid format.",
                exception);
        }
    }

    private static bool TryReadCredentials(
        StringValues authorizationValues,
        out string username,
        out string password)
    {
        username = string.Empty;
        password = string.Empty;
        var authorization = authorizationValues.ToString();
        if (!authorization.StartsWith($"{Scheme} ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[(Scheme.Length + 1)..]));
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return false;
            }
            username = decoded[..separator];
            password = decoded[(separator + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool VerifyPassword(string password, PasswordHash passwordHash)
    {
        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            passwordHash.Salt,
            passwordHash.Iterations,
            HashAlgorithmName.SHA256,
            HashLength);
        return CryptographicOperations.FixedTimeEquals(actualHash, passwordHash.Hash);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private readonly record struct PasswordHash(int Iterations, byte[] Salt, byte[] Hash);
}
