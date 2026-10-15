using System.Text.Json.Nodes;
using System.Text;
using System.Xml.Linq;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationExportServiceTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public void Exports_ProvideOptionalSamplePayloadsWithoutChangingConfiguredResponses(string method)
    {
        const string sample =
            "{\"id\":\"sample-item-001\",\"name\":\"Example item\",\"status\":\"active\",\"quantity\":2,\"tags\":[\"sample\",\"dashboard\"],\"metadata\":{\"source\":\"MockAPI dashboard\"}}";
        var endpoint = new MockEndpointDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Payload",
            Enabled = true,
            Methods = ["GET", method],
            Path = "/payload",
            Response = new() { StatusCode = 400, ContentType = "application/problem+json", Headers = [], Body = "{\"configured\":true}" }
        };
        var document = new MockApiConfigurationDocument { SchemaVersion = "1.0", Endpoints = [endpoint] };
        JsonNode ExportJson(string format)
        {
            Assert.True(ConfigurationExportService.TryExport(format, document, out var export));
            return JsonNode.Parse(export!.Content)!;
        }
        string ExportText(string format)
        {
            Assert.True(ConfigurationExportService.TryExport(format, document, out var export));
            return Encoding.UTF8.GetString(export!.Content);
        }

        var postman = ExportJson("postman")["item"]!;
        Assert.Null(postman[0]!["request"]!["body"]);
        Assert.Equal(sample, postman[1]!["request"]!["body"]!["raw"]!.GetValue<string>());
        Assert.Contains(postman[1]!["request"]!["header"]!.AsArray(),
            header => header!["key"]!.GetValue<string>() == "Content-Type" && header["value"]!.GetValue<string>() == "application/json");

        var insomnia = ExportJson("insomnia")["resources"]!.AsArray().Where(resource =>
            resource!["_type"]!.GetValue<string>() == "request").ToArray();
        Assert.Null(insomnia[0]!["body"]);
        Assert.Equal(sample, insomnia[1]!["body"]!["text"]!.GetValue<string>());
        Assert.Equal("application/json", insomnia[1]!["body"]!["mimeType"]!.GetValue<string>());

        var openApi = ExportJson("openapi")["paths"]!["/payload"]!;
        Assert.Null(openApi["get"]!["requestBody"]);
        var operation = openApi[method.ToLowerInvariant()]!;
        Assert.False(operation["requestBody"]!["required"]!.GetValue<bool>());
        Assert.Contains("optional", operation["requestBody"]!["description"]!.GetValue<string>(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Example item", operation["requestBody"]!["content"]!["application/json"]!["example"]!["name"]!.GetValue<string>());
        Assert.NotNull(operation["responses"]!["413"]);
        Assert.NotNull(operation["responses"]!["415"]);
        Assert.Equal(endpoint.Response.Body,
            operation["responses"]!["400"]!["content"]!["application/problem+json"]!["example"]!.GetValue<string>());

        var curl = ExportText("curl");
        Assert.Contains($"--request {method} --header 'Content-Type: application/json' --data-raw '{sample}'", curl);
        var http = ExportText("http");
        Assert.Contains($"{method} {{{{baseUrl}}}}/payload\nContent-Type: application/json\n\n{sample}", http);
        var jmeter = XDocument.Parse(ExportText("jmeter"));
        Assert.Single(jmeter.Descendants("boolProp"), property =>
            property.Attribute("name")?.Value == "HTTPSampler.postBodyRaw" && property.Value == "true");
        Assert.Contains(jmeter.Descendants("stringProp"), property =>
            property.Attribute("name")?.Value == "Argument.value" && property.Value == sample);
        Assert.Contains(jmeter.Descendants("stringProp"), property =>
            property.Attribute("name")?.Value == "Header.value" && property.Value == "application/json");
        var k6 = ExportText("k6");
        var requestsJson = k6.Split("const requests = ", StringSplitOptions.None)[1].Split(";\n", StringSplitOptions.None)[0];
        var requests = JsonNode.Parse(requestsJson)!;
        Assert.Null(requests[0]!["body"]);
        Assert.Equal(sample, requests[1]!["body"]!.GetValue<string>());
        Assert.Equal("application/json", requests[1]!["headers"]!["Content-Type"]!.GetValue<string>());
        Assert.Contains("request.body, requestOptions", k6);
    }

    [Fact]
    public void Exports_UseReservationSampleForReservationEndpointsAcrossAllFormats()
    {
        const string sample =
            "{\"parkId\":\"starlight-gardens\",\"attractionId\":\"cloud-cruiser\",\"reservationDate\":\"2030-06-15\",\"reservationTime\":\"10:30\",\"partySize\":2,\"guest\":{\"name\":\"Alex Morgan\",\"email\":\"alex@example.com\"}}";
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints =
            [
                new MockEndpointDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Reserve a ride",
                    Enabled = true,
                    Methods = ["POST"],
                    Path = "/ctp/reservations",
                    Response = new() { StatusCode = 201, Headers = [], Body = "{}" }
                }
            ]
        };
        string ExportText(string format)
        {
            Assert.True(ConfigurationExportService.TryExport(format, document, out var export));
            return Encoding.UTF8.GetString(export!.Content);
        }

        var postman = JsonNode.Parse(ExportText("postman"))!;
        Assert.Equal(sample, postman["item"]![0]!["request"]!["body"]!["raw"]!.GetValue<string>());
        var insomnia = JsonNode.Parse(ExportText("insomnia"))!;
        Assert.Equal(sample, insomnia["resources"]!.AsArray()
            .Single(resource => resource!["_type"]!.GetValue<string>() == "request")!["body"]!["text"]!.GetValue<string>());
        var openApi = JsonNode.Parse(ExportText("openapi"))!;
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(sample),
            openApi["paths"]!["/ctp/reservations"]!["post"]!["requestBody"]!
                ["content"]!["application/json"]!["example"]));
        Assert.Contains($"--data-raw '{sample}'", ExportText("curl"));
        Assert.Contains(sample, ExportText("http"));
        var jmeter = XDocument.Parse(ExportText("jmeter"));
        Assert.Contains(jmeter.Descendants("stringProp"),
            property => property.Attribute("name")?.Value == "Argument.value" && property.Value == sample);
        var k6 = JsonNode.Parse(ExportText("k6").Split("const requests = ", StringSplitOptions.None)[1]
            .Split(";\n", StringSplitOptions.None)[0])!;
        Assert.Equal(sample, k6[0]!["body"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Endpoint description\nSecond line")]
    public void PostmanExport_PreservesNonNullEndpointDescriptions(string? description)
    {
        var endpoint = new MockEndpointDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Hello",
            Description = description,
            Enabled = true,
            Methods = ["GET", "POST"],
            Path = "/hello",
            Response = new() { StatusCode = 200, Headers = [], Body = "hello" }
        };
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints = [endpoint]
        };

        Assert.True(ConfigurationExportService.TryExport("postman", document, out var export));
        var collection = JsonNode.Parse(export!.Content)!;
        var items = collection["item"]!.AsArray();

        Assert.Equal(endpoint.Methods.Count, items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]!.AsObject();
            Assert.Equal(description is not null, item.ContainsKey("description"));
            Assert.Equal(description, item["description"]?.GetValue<string>());
            Assert.Equal(endpoint.Methods[index], item["request"]!["method"]!.GetValue<string>());
            Assert.Equal("{{baseUrl}}/hello", item["request"]!["url"]!["raw"]!.GetValue<string>());
        }
    }

    [Fact]
    public void OpenApiExport_UsesApplicationVersionFromAssemblyMetadata()
    {
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints = []
        };

        Assert.True(ConfigurationExportService.TryExport("openapi", document, out var export));
        var openApi = JsonNode.Parse(export!.Content)!;
        var expectedVersion = MockApiHostConfiguration.GetVersion(typeof(Program).Assembly);
        var exportedVersion = openApi["info"]!["version"]!.GetValue<string>();

        SemanticVersionAssert.IsValid(exportedVersion);
        Assert.Equal(expectedVersion, exportedVersion);
        Assert.Equal("3.1.0", openApi["openapi"]!.GetValue<string>());
    }

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
