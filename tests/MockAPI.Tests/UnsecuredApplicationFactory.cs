using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MockAPI.Tests;

/// <summary>Explicitly opts legacy dispatch scenarios out of the production API-key default.</summary>
internal sealed class UnsecuredApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _configurationPath = Path.Combine(Path.GetTempPath(), $"mockapi-unsecured-tests-{Guid.NewGuid():N}.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("MockApi:ConfigurationPath", _configurationPath);
        builder.UseSetting("MockApi:RequireApiKey", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            File.Delete(_configurationPath);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_configurationPath);
    }
}
