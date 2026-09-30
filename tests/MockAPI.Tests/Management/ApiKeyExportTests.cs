using System.Text;
using System.Text.Json.Nodes;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ApiKeyExportTests
{
    [Theory]
    [InlineData("postman")]
    [InlineData("insomnia")]
    [InlineData("openapi")]
    [InlineData("curl")]
    [InlineData("jmeter")]
    [InlineData("k6")]
    [InlineData("http")]
    public void ProtectedExports_DescribeHeaderAndUseSecretFreePlaceholders(string format)
    {
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints = [new MockEndpointDefinition
            {
                Id = Guid.NewGuid(), Name = "Protected", Enabled = true, Methods = ["GET"], Path = "/protected",
                Response = new() { StatusCode = 200, Headers = [], Body = "" }
            }]
        };
        Assert.True(ConfigurationExportService.TryExport(format, document, out var export, requireApiKey: true));
        var content = Encoding.UTF8.GetString(export!.Content);
        Assert.Contains("X-MockAPI-Key", content, StringComparison.Ordinal);
        Assert.DoesNotContain("keyHash", content, StringComparison.Ordinal);
        if (format == "openapi")
        {
            var json = JsonNode.Parse(content)!;
            Assert.Equal("apiKey", json["components"]!["securitySchemes"]!["MockApiKey"]!["type"]!.GetValue<string>());
            Assert.NotNull(json["security"]![0]!["MockApiKey"]);
        }
        else
        {
            Assert.Contains(format is "postman" or "insomnia" or "http" ? "mockApiKey" : "MOCKAPI_KEY", content, StringComparison.Ordinal);
        }

        Assert.True(ConfigurationExportService.TryExport(format, document, out var publicExport, requireApiKey: false));
        Assert.DoesNotContain("X-MockAPI-Key", Encoding.UTF8.GetString(publicExport!.Content), StringComparison.Ordinal);
    }
}
