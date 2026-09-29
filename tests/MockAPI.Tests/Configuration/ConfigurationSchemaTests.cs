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

        Assert.Equal(7, document.Endpoints.Count);
        Assert.Equal("This API demonstrates some of MockAPI's capabilities.", document.ApiDescriptions!["/ex"]);
        Assert.All(document.Endpoints, endpoint =>
            Assert.StartsWith("/ex/", endpoint.Path, StringComparison.Ordinal));

        var hello = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/hello");
        Assert.Equal(["GET"], hello.Methods);
        Assert.Equal(200, hello.Response.StatusCode);

        var created = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/orders");
        Assert.Equal(["POST"], created.Methods);
        Assert.Equal(201, created.Response.StatusCode);

        var rateLimited = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/rate-limited");
        Assert.Equal(5, rateLimited.RequestCount);
        Assert.Equal(["10"], rateLimited.Response.Headers["Retry-After"]);
        Assert.Equal("{\"error\":\"try again later\"}", rateLimited.Response.Body);
        Assert.Equal(4, rateLimited.Response.RateLimit!.RequestLimit);
        Assert.Equal(10, rateLimited.Response.RateLimit.WindowSeconds);
        Assert.Equal(200, rateLimited.Response.RateLimit.SuccessResponse.StatusCode);
        Assert.Equal("{\"status\":\"accepted\"}", rateLimited.Response.RateLimit.SuccessResponse.Body);

        var noContent = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/orders/42");
        Assert.Equal(["DELETE"], noContent.Methods);
        Assert.Equal(204, noContent.Response.StatusCode);

        var redirect = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/redirect");
        Assert.Equal(["GET"], redirect.Methods);
        Assert.Equal(302, redirect.Response.StatusCode);
        Assert.Equal(["/ex/hello"], redirect.Response.Headers["Location"]);
        Assert.Empty(redirect.Response.Body);

        var serverError = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/server-error");
        Assert.Equal(["GET"], serverError.Methods);
        Assert.Equal(500, serverError.Response.StatusCode);
        Assert.Equal("{\"error\":\"internal server error\"}", serverError.Response.Body);

        var abort = Assert.Single(document.Endpoints, endpoint => endpoint.Path == "/ex/abort-connection");
        Assert.Equal(MockResponseBehavior.AbortConnection, abort.Response.Behavior);
        Assert.Null(abort.Response.StatusCode);
    }

    [Fact]
    public void AoaiSample_ImplementsBackendResponseMatrix()
    {
        var json = File.ReadAllText(ConfigurationSchemaFixture.AoaiPath);

        AssertSchemaValid(json);

        var document = JsonSerializer.Deserialize(
            json,
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(document);
        Assert.True(ConfigurationValidator.Validate(document).IsValid);
        Assert.Equal(15, document.Endpoints.Count);
        Assert.All(document.Endpoints, endpoint =>
        {
            Assert.Equal(["POST"], endpoint.Methods);
            Assert.StartsWith("/aoai/", endpoint.Path, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Description));
        });

        var responseStatuses = document.Endpoints
            .Where(endpoint => endpoint.Response.Behavior == MockResponseBehavior.Response)
            .Select(endpoint => endpoint.Response.StatusCode)
            .Order()
            .ToArray();
        Assert.Equal<int?>([200, 400, 401, 403, 404, 408, 409, 409, 429, 499, 500, 502, 503, 504], responseStatuses);

        var persistentConflict = Assert.Single(document.Endpoints, endpoint =>
            endpoint.Path == "/aoai/409/chat/completions");
        Assert.DoesNotContain("Retry-After", persistentConflict.Response.Headers.Keys);

        var transientConflict = Assert.Single(document.Endpoints, endpoint =>
            endpoint.Path == "/aoai/409-retry-after/chat/completions");
        Assert.Equal(["1"], transientConflict.Response.Headers["Retry-After"]);

        var transportFailure = Assert.Single(document.Endpoints, endpoint =>
            endpoint.Path == "/aoai/transport-failure/chat/completions");
        Assert.Equal(MockResponseBehavior.AbortConnection, transportFailure.Response.Behavior);
        Assert.Null(transportFailure.Response.StatusCode);
    }

    [Fact]
    public void GeneratedConfiguration_ValidatesAgainstSchema()
    {
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            ApiDescriptions = new() { ["/generated"] = "Generated API\nDescription" },
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
    [InlineData("/", true)]
    [InlineData("/ex", true)]
    [InlineData("/EX", true)]
    [InlineData("ex", false)]
    [InlineData("/ex/", false)]
    [InlineData("/ex/nested", false)]
    [InlineData("/ex?query", false)]
    [InlineData("/ex#fragment", false)]
    [InlineData("/ex\\path", false)]
    [InlineData("/white space", false)]
    public void ApiDescriptionPaths_UseSingleAbsoluteSegments(string path, bool valid)
    {
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            ApiDescriptions = new() { [path] = "Description" },
            Endpoints = []
        };
        var json = JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.Equal(valid, Evaluate(json).IsValid);
        Assert.Equal(valid, ConfigurationValidator.Validate(document).IsValid);
    }

    [Theory]
    [InlineData(25, 4000, true)]
    [InlineData(26, 4000, false)]
    [InlineData(1, 4001, false)]
    public void ApiDescriptions_SchemaAndRuntimeEnforceLimits(int count, int length, bool valid)
    {
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            ApiDescriptions = Enumerable.Range(0, count).ToDictionary(index => $"/api{index}", _ => new string('x', length)),
            Endpoints = []
        };
        var json = JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.Equal(valid, Evaluate(json).IsValid);
        Assert.Equal(valid, ConfigurationValidator.Validate(document).IsValid);
    }

    [Fact]
    public void AbortConnectionResponse_RejectsHttpResponseFields()
    {
        var instance = JsonNode.Parse(File.ReadAllText(ConfigurationSchemaFixture.ExamplePath))!.AsObject();
        var abort = Assert.Single(
            instance["endpoints"]!.AsArray(),
            endpoint => endpoint!["path"]!.GetValue<string>() == "/ex/abort-connection");
        var response = abort!["response"]!.AsObject();
        response["statusCode"] = 200;

        AssertSchemaInvalid(instance.ToJsonString());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(6, false)]
    public void RequestCount_AcceptsOnlySupportedValues(int requestCount, bool expectedIsValid)
    {
        var instance = JsonNode.Parse(File.ReadAllText(ConfigurationSchemaFixture.ExamplePath))!.AsObject();
        instance["endpoints"]![0]!["requestCount"] = requestCount;

        var result = Evaluate(instance.ToJsonString());

        Assert.Equal(expectedIsValid, result.IsValid);
    }

    [Theory]
    [InlineData("unsupported-version")]
    [InlineData("unknown-document-property")]
    [InlineData("unknown-endpoint-property")]
    [InlineData("missing-status-code")]
    [InlineData("invalid-id")]
    [InlineData("empty-name")]
    [InlineData("description-too-long")]
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
            case "description-too-long":
                endpoint["description"] = new string('d', ConfigurationLimits.MaximumDescriptionLength + 1);
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
        Assert.Equal(ConfigurationLimits.MaximumDescriptionLength, endpoint["properties"]!["description"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumMethodsPerEndpoint, endpoint["properties"]!["methods"]!["maxItems"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumPathLength, endpoint["properties"]!["path"]!["maxLength"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MinimumTestRequestCount, endpoint["properties"]!["requestCount"]!["minimum"]!.GetValue<int>());
        Assert.Equal(ConfigurationLimits.MaximumTestRequestCount, endpoint["properties"]!["requestCount"]!["maximum"]!.GetValue<int>());
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
