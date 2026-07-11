using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationSchemaTests
{
    [Fact]
    public void CheckedInExample_ValidatesAgainstSchemaAndSemanticRules()
    {
        var json = File.ReadAllText(ConfigurationSchemaFixture.ExamplePath);

        AssertSchemaValid(json);

        var document = JsonSerializer.Deserialize(
            json,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(document);
        Assert.True(ConfigurationValidator.Validate(document).IsValid);

        Assert.Equal(4, document.Endpoints.Count);
        Assert.All(document.Endpoints, endpoint =>
            Assert.StartsWith("/ex/", endpoint.Path, StringComparison.Ordinal));

        var hello = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/hello");
        Assert.Equal(["GET"], hello.Methods);
        Assert.Equal(200, hello.Response.StatusCode);

        var created = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/orders");
        Assert.Equal(["POST"], created.Methods);
        Assert.Equal(201, created.Response.StatusCode);

        var rateLimited = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/rate-limited");
        Assert.Equal(["30"], rateLimited.Response.Headers["Retry-After"]);
        Assert.Equal("{\"error\":\"try again later\"}", rateLimited.Response.Body);

        var noContent = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/orders/42");
        Assert.Equal(["DELETE"], noContent.Methods);
        Assert.Equal(204, noContent.Response.StatusCode);
    }

    [Fact]
    public void CheckedInTemplate_ValidatesAgainstSchemaAndSemanticRules()
    {
        var json = File.ReadAllText(ConfigurationSchemaFixture.TemplatePath);

        AssertSchemaValid(json);

        var document = JsonSerializer.Deserialize(
            json,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(document);
        Assert.True(ConfigurationValidator.Validate(document).IsValid);
        Assert.Empty(document.Endpoints);
    }

    [Fact]
    public void GeneratedConfiguration_ValidatesAgainstSchema()
    {
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            Endpoints =
            [
                new MockEndpointDefinition
                {
                    Id = Guid.Parse("7b2d425d-75f1-4ded-a74e-503374a7e99e"),
                    Name = "Generated endpoint",
                    Enabled = false,
                    Methods = ["GET", "CUSTOM-METHOD"],
                    Path = "/generated",
                    Response = new MockResponseDefinition
                    {
                        StatusCode = 204,
                        Headers = [],
                        Body = string.Empty
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(
            document,
            MockApiJsonContext.Default.MockApiConfigurationDocument);

        AssertSchemaValid(json);
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("unknown-document-property")]
    [InlineData("unknown-endpoint-property")]
    [InlineData("missing-status-code")]
    [InlineData("invalid-id")]
    [InlineData("empty-name")]
    [InlineData("invalid-method")]
    [InlineData("duplicate-method")]
    [InlineData("relative-path")]
    [InlineData("invalid-status-code")]
    [InlineData("control-character-reason-phrase")]
    [InlineData("non-array-header-value")]
    public void Schema_RejectsRepresentativeStructuralViolations(string mutation)
    {
        var instance = JsonNode.Parse(File.ReadAllText(ConfigurationSchemaFixture.ExamplePath))!.AsObject();
        var endpoint = instance["endpoints"]![0]!.AsObject();
        var response = endpoint["response"]!.AsObject();

        switch (mutation)
        {
            case "unsupported-version":
                instance["schemaVersion"] = "2.0";
                break;
            case "unknown-document-property":
                instance["unexpected"] = true;
                break;
            case "unknown-endpoint-property":
                endpoint["unexpected"] = true;
                break;
            case "missing-status-code":
                response.Remove("statusCode");
                break;
            case "invalid-id":
                endpoint["id"] = "not-a-uuid";
                break;
            case "empty-name":
                endpoint["name"] = string.Empty;
                break;
            case "invalid-method":
                endpoint["methods"] = JsonNode.Parse("[\"GET BAD\"]");
                break;
            case "duplicate-method":
                endpoint["methods"] = JsonNode.Parse("[\"GET\",\"GET\"]");
                break;
            case "relative-path":
                endpoint["path"] = "api/example";
                break;
            case "invalid-status-code":
                response["statusCode"] = 600;
                break;
            case "control-character-reason-phrase":
                response["reasonPhrase"] = "Invalid\u0001Reason";
                break;
            case "non-array-header-value":
                response["headers"]!["Retry-After"] = "30";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        AssertSchemaInvalid(instance.ToJsonString());
    }

    [Fact]
    public void SchemaLimits_MatchRuntimeLimits()
    {
        var schema = JsonNode.Parse(File.ReadAllText(ConfigurationSchemaFixture.SchemaPath))!.AsObject();
        var endpoint = schema["$defs"]!["endpoint"]!.AsObject();
        var response = schema["$defs"]!["response"]!.AsObject();

        Assert.Equal("https://json-schema.org/draft/2020-12/schema", schema["$schema"]!.GetValue<string>());
        Assert.Equal(ConfigurationLimits.MaximumEndpoints, schema["properties"]!["endpoints"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumNameLength, endpoint["properties"]!["name"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumMethodsPerEndpoint, endpoint["properties"]!["methods"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumPathLength, endpoint["properties"]!["path"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumHeadersPerEndpoint, response["properties"]!["headers"]!["maxProperties"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumDocumentBytes, schema["x-maximumUtf8Bytes"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumBodyBytes, response["properties"]!["body"]!["x-maximumUtf8Bytes"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumHeaderValueBytes, schema["$defs"]!["headerValue"]!["x-maximumUtf8Bytes"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumCombinedHeaderBytes, response["properties"]!["headers"]!["x-maximumCombinedUtf8Bytes"]!.GetValue<int>());
    }

    private static void AssertSchemaValid(string json)
    {
        var result = Evaluate(json);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    private static void AssertSchemaInvalid(string json)
    {
        var result = Evaluate(json);
        Assert.False(result.IsValid);
    }

    private static EvaluationResults Evaluate(string json)
    {
        using var instance = JsonDocument.Parse(json);
        return ConfigurationSchemaFixture.Evaluate(instance.RootElement);
    }
}
