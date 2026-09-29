using System.Text.Json.Nodes;
using System.Xml.Linq;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationExportServiceTests
{
    [Fact]
    public void OpenApiExport_EmptyApiMetadataOmitsTags()
    {
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            ApiDescriptions = [],
            Endpoints = []
        };
        Assert.True(ConfigurationExportService.TryExport("openapi", document, out var export));
        Assert.Null(JsonNode.Parse(export!.Content)!["tags"]);
    }

    [Fact]
    public void OpenApiExport_SeparatesApiDescriptionsFromOperationDescriptions()
    {
        var endpoint = new MockEndpointDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Hello",
            Description = "Operation description",
            Enabled = true,
            Methods = ["GET"],
            Path = "/ex/hello",
            Response = new() { StatusCode = 200, Headers = [], Body = "" }
        };
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            ApiDescriptions = new() { ["/unused"] = "Retained metadata", ["/ex"] = "API overview\nSecond line" },
            Endpoints = [endpoint, endpoint with { Id = Guid.NewGuid(), Path = "/EX/hello" }]
        };
        Assert.True(ConfigurationExportService.TryExport("openapi", document, out var export));
        var openApi = JsonNode.Parse(export!.Content)!;
        Assert.Equal("/ex", openApi["tags"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("API overview\nSecond line", openApi["tags"]![0]!["description"]!.GetValue<string>());
        var operation = openApi["paths"]!["/ex/hello"]!["get"]!;
        Assert.Equal("/ex", operation["tags"]![0]!.GetValue<string>());
        Assert.Equal("Operation description", operation["description"]!.GetValue<string>());
        Assert.Null(openApi["paths"]!["/EX/hello"]!["get"]!["tags"]);
    }

    [Fact]
    public void OpenApiExport_ExpandsRateLimitedResponses()
    {
        var endpoint = new MockEndpointDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Rate limited",
            Enabled = true,
            Methods = ["GET"],
            Path = "/rate-limited",
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = [],
                ContentType = "application/json; charset=utf-8",
                Body = "limited",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 1,
                    WindowSeconds = 60,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = 204,
                        ReasonPhrase = "No Content",
                        Headers = [],
                        Body = string.Empty
                    }
                }
            }
        };
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            Endpoints = [endpoint]
        };

        Assert.True(ConfigurationExportService.TryExport("openapi", document, out var export));
        var openApi = JsonNode.Parse(export!.Content)!;
        var responses = openApi["paths"]![endpoint.Path]!["get"]!["responses"]!;

        Assert.Equal("No Content", responses["204"]!["description"]!.GetValue<string>());
        Assert.Null(responses["204"]!["content"]);
        Assert.Equal("Mock response", responses["429"]!["description"]!.GetValue<string>());
        Assert.Equal("limited", responses["429"]!["content"]!["application/json"]!["example"]!.GetValue<string>());
    }

    [Fact]
    public void Validators_RejectMalformedJsonArtifacts()
    {
        AssertInvalid(() => ConfigurationExportService.ValidatePostman(new JsonObject()));
        AssertInvalid(() => ConfigurationExportService.ValidatePostman(new JsonObject
        {
            ["info"] = new JsonObject()
        }));
        AssertInvalid(() => ConfigurationExportService.ValidatePostman(new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            }
        }));

        AssertInvalid(() => ConfigurationExportService.ValidateInsomnia(new JsonObject()));
        AssertInvalid(() => ConfigurationExportService.ValidateInsomnia(new JsonObject { ["_type"] = "export" }));
        AssertInvalid(() => ConfigurationExportService.ValidateInsomnia(new JsonObject
        {
            ["_type"] = "export",
            ["__export_format"] = 4
        }));

        AssertInvalid(() => ConfigurationExportService.ValidateOpenApi(new JsonObject()));
        AssertInvalid(() => ConfigurationExportService.ValidateOpenApi(new JsonObject { ["openapi"] = "3.1.0" }));
        AssertInvalid(() => ConfigurationExportService.ValidateOpenApi(new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject()
        }));
    }

    [Fact]
    public void Validators_RejectMalformedTextAndXmlArtifacts()
    {
        AssertInvalid(() => ConfigurationExportService.ValidateCurl(string.Empty));
        AssertInvalid(() => ConfigurationExportService.ValidateK6(string.Empty));
        AssertInvalid(() => ConfigurationExportService.ValidateK6("export default function ()"));
        AssertInvalid(() => ConfigurationExportService.ValidateHttp(string.Empty));
        AssertInvalid(() => ConfigurationExportService.ValidateJMeter(new XDocument()));
        AssertInvalid(() => ConfigurationExportService.ValidateJMeter(
            new XDocument(new XElement("jmeterTestPlan"))));
        AssertInvalid(() => ConfigurationExportService.ValidateJMeter(
            new XDocument(new XElement("jmeterTestPlan", new XAttribute("version", "1.2")))));
    }

    private static void AssertInvalid(Action validate) =>
        Assert.Throws<InvalidOperationException>(validate);
}
