using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MockAPI.Configuration;

namespace MockAPI.Management;

internal sealed record ConfigurationExport(byte[] Content, string ContentType, string FileName);

internal static class ConfigurationExportService
{
    private const string BaseUrl = "{{baseUrl}}";

    internal static bool TryExport(
        string format,
        MockApiConfigurationDocument document,
        out ConfigurationExport? export)
    {
        ArgumentNullException.ThrowIfNull(document);

        export = format.ToLowerInvariant() switch
        {
            "postman" => JsonExport(CreatePostman(document), "application/json; charset=utf-8", "mockapi.postman_collection.json", ValidatePostman),
            "insomnia" => JsonExport(CreateInsomnia(document), "application/json; charset=utf-8", "mockapi.insomnia.json", ValidateInsomnia),
            "openapi" => JsonExport(CreateOpenApi(document), "application/json; charset=utf-8", "mockapi.openapi.json", ValidateOpenApi),
            "curl" => TextExport(CreateCurl(document), "text/x-shellscript; charset=utf-8", "mockapi.sh", ValidateCurl),
            "jmeter" => XmlExport(CreateJMeter(document), "application/xml; charset=utf-8", "mockapi.jmx", ValidateJMeter),
            "k6" => TextExport(CreateK6(document), "text/javascript; charset=utf-8", "mockapi.k6.js", ValidateK6),
            "http" => TextExport(CreateHttp(document), "text/plain; charset=utf-8", "mockapi.http", ValidateHttp),
            _ => null
        };
        return export is not null;
    }

    private static ConfigurationExport JsonExport(
        JsonNode node,
        string contentType,
        string fileName,
        Action<JsonNode> validate)
    {
        validate(node);
        return new ConfigurationExport(
            Encoding.UTF8.GetBytes(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })),
            contentType,
            fileName);
    }

    private static ConfigurationExport TextExport(
        string content,
        string contentType,
        string fileName,
        Action<string> validate)
    {
        validate(content);
        return new ConfigurationExport(Encoding.UTF8.GetBytes(content), contentType, fileName);
    }

    private static ConfigurationExport XmlExport(
        XDocument document,
        string contentType,
        string fileName,
        Action<XDocument> validate)
    {
        validate(document);
        return new ConfigurationExport(Encoding.UTF8.GetBytes(document.ToString()), contentType, fileName);
    }

    private static JsonObject CreatePostman(MockApiConfigurationDocument document) => new()
    {
        ["info"] = new JsonObject
        {
            ["name"] = "MockAPI",
            ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
        },
        ["variable"] = new JsonArray(new JsonObject { ["key"] = "baseUrl", ["value"] = "http://localhost:8080" }),
        ["item"] = new JsonArray(document.Endpoints.Where(endpoint => endpoint.Enabled)
            .SelectMany(endpoint => endpoint.Methods.Select(method => (JsonNode)CreatePostmanItem(endpoint, method))).ToArray())
    };

    private static JsonObject CreatePostmanItem(MockEndpointDefinition endpoint, string method)
    {
        var item = new JsonObject
        {
            ["name"] = $"{endpoint.Name} ({method})",
            ["request"] = new JsonObject
            {
                ["method"] = method,
                ["header"] = new JsonArray(),
                ["url"] = new JsonObject { ["raw"] = $"{BaseUrl}{endpoint.Path}", ["host"] = new JsonArray("{{baseUrl}}"), ["path"] = PathParts(endpoint.Path) }
            }
        };
        if (endpoint.Description is not null)
        {
            item["description"] = endpoint.Description;
        }

        if (endpoint.Response.Behavior == MockResponseBehavior.Response)
        {
            var expectedResponses = new JsonObject(GetResponses(endpoint).Select(response =>
                new KeyValuePair<string, JsonNode?>(
                    response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    response.Body)));
            item["event"] = new JsonArray((JsonNode)new JsonObject
            {
                ["listen"] = "test",
                ["script"] = new JsonObject
                {
                    ["type"] = "text/javascript",
                    ["exec"] = new JsonArray(
                        $"const expectedResponses = {expectedResponses.ToJsonString()};",
                        "pm.test('Status and body match a configured response', () => pm.expect(expectedResponses[String(pm.response.code)]).to.eql(pm.response.text()));")
                }
            });
        }

        return item;
    }

    private static JsonObject CreateInsomnia(MockApiConfigurationDocument document)
    {
        var workspaceId = "wrk_mockapi";
        var environmentId = "env_mockapi";
        var resources = new JsonArray(
            new JsonObject { ["_id"] = workspaceId, ["_type"] = "workspace", ["name"] = "MockAPI", ["scope"] = "collection" },
            new JsonObject { ["_id"] = environmentId, ["_type"] = "environment", ["parentId"] = workspaceId, ["name"] = "Base Environment", ["data"] = new JsonObject { ["baseUrl"] = "http://localhost:8080" } });
        foreach (var endpoint in document.Endpoints.Where(endpoint => endpoint.Enabled))
        {
            foreach (var method in endpoint.Methods)
            {
                var request = new JsonObject
                {
                    ["_id"] = $"req_{endpoint.Id:N}_{method.ToLowerInvariant()}",
                    ["_type"] = "request",
                    ["parentId"] = workspaceId,
                    ["name"] = $"{endpoint.Name} ({method})",
                    ["method"] = method,
                    ["url"] = $"{{{{ _.baseUrl }}}}{endpoint.Path}",
                    ["headers"] = new JsonArray()
                };
                if (endpoint.Description is not null)
                {
                    request["description"] = endpoint.Description;
                }

                resources.Add((JsonNode)request);
            }
        }

        return new JsonObject
        {
            ["_type"] = "export",
            ["__export_format"] = 4,
            ["__export_source"] = "MockAPI",
            ["resources"] = resources
        };
    }

    private static JsonObject CreateOpenApi(MockApiConfigurationDocument document)
    {
        var paths = new JsonObject();
        foreach (var endpoint in document.Endpoints.Where(endpoint => endpoint.Enabled))
        {
            var path = paths[endpoint.Path] as JsonObject ?? new JsonObject();
            paths[endpoint.Path] = path;
            foreach (var method in endpoint.Methods)
            {
                var response = endpoint.Response.Behavior == MockResponseBehavior.AbortConnection
                    ? new JsonObject
                    {
                        ["default"] = new JsonObject
                        {
                            ["description"] = "Connection aborted without an HTTP response",
                            ["x-mockapi-behavior"] = "abortConnection"
                        }
                    }
                    : new JsonObject(GetResponses(endpoint).Select(configuredResponse =>
                        new KeyValuePair<string, JsonNode?>(
                            configuredResponse.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            new JsonObject
                            {
                                ["description"] = configuredResponse.ReasonPhrase ?? "Mock response",
                                ["content"] = string.IsNullOrWhiteSpace(configuredResponse.ContentType) ? null : new JsonObject
                                {
                                    [configuredResponse.ContentType.Split(';', 2)[0]] = new JsonObject
                                    {
                                        ["schema"] = new JsonObject { ["type"] = "string" },
                                        ["example"] = configuredResponse.Body
                                    }
                                }
                            })));
                var operation = new JsonObject
                {
                    ["operationId"] = $"mock_{endpoint.Id:N}_{method.ToLowerInvariant()}",
                    ["summary"] = endpoint.Name,
                    ["responses"] = response
                };
                if (endpoint.Description is not null)
                {
                    operation["description"] = endpoint.Description;
                }

                var groupPath = "/" + endpoint.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (document.ApiDescriptions?.ContainsKey(groupPath) is true)
                {
                    operation["tags"] = new JsonArray(groupPath);
                }

                path[method.ToLowerInvariant()] = operation;
            }
        }

        var result = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["jsonSchemaDialect"] = "https://json-schema.org/draft/2020-12/schema",
            ["info"] = new JsonObject { ["title"] = "MockAPI", ["version"] = "1.0.0" },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "http://localhost:8080" }),
            ["paths"] = paths
        };
        if (document.ApiDescriptions is { Count: > 0 } descriptions)
        {
            result["tags"] = new JsonArray(descriptions.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (JsonNode)new JsonObject { ["name"] = pair.Key, ["description"] = pair.Value })
                .ToArray());
        }

        return result;
    }

    private static string CreateCurl(MockApiConfigurationDocument document)
    {
        var lines = new List<string> { "#!/usr/bin/env sh", "set -eu", "BASE_URL=${BASE_URL:-http://localhost:8080}", string.Empty };
        foreach (var endpoint in document.Endpoints.Where(endpoint => endpoint.Enabled))
        {
            foreach (var method in endpoint.Methods)
            {
                lines.Add($"curl --fail-with-body --request {method} --url \"$BASE_URL{endpoint.Path}\"");
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    private static XDocument CreateJMeter(MockApiConfigurationDocument document)
    {
        var plan = new XElement("hashTree");
        foreach (var endpoint in document.Endpoints.Where(endpoint => endpoint.Enabled))
        {
            foreach (var method in endpoint.Methods)
            {
                var responses = GetResponses(endpoint);
                var expectedStatuses = string.Join('|', responses.Select(response => response.StatusCode));
                plan.Add(
                    new XElement("HTTPSamplerProxy",
                        new XAttribute("guiclass", "HttpTestSampleGui"),
                        new XAttribute("testclass", "HTTPSamplerProxy"),
                        new XAttribute("testname", $"{endpoint.Name} ({method})"),
                        new XAttribute("enabled", "true"),
                        JMeterString("HTTPSampler.domain", "${HOST}"),
                        JMeterString("HTTPSampler.port", "${PORT}"),
                        JMeterString("HTTPSampler.protocol", "${SCHEME}"),
                        JMeterString("HTTPSampler.path", endpoint.Path),
                        JMeterString("HTTPSampler.method", method),
                        JMeterString("HTTPSampler.postBodyRaw", "false")),
                    new XElement("hashTree",
                        endpoint.Response.Behavior == MockResponseBehavior.AbortConnection ? null : new XElement("ResponseAssertion",
                            new XAttribute("guiclass", "AssertionGui"),
                            new XAttribute("testclass", "ResponseAssertion"),
                            new XAttribute("testname", $"Expected status {expectedStatuses}"),
                            new XAttribute("enabled", "true"),
                            new XElement("collectionProp", new XAttribute("name", "Asserion.test_strings"),
                                new XElement("stringProp", new XAttribute("name", "expected-statuses"), $"^({expectedStatuses})$")),
                            JMeterString("Assertion.test_field", "Assertion.response_code"),
                            new XElement("boolProp", new XAttribute("name", "Assertion.assume_success"), "false"),
                            new XElement("intProp", new XAttribute("name", "Assertion.test_type"), "1")),
                        new XElement("hashTree")));
            }
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement("jmeterTestPlan", new XAttribute("version", "1.2"), new XAttribute("properties", "5.0"), new XAttribute("jmeter", "5.6.3"),
                new XElement("hashTree",
                    new XElement("TestPlan", new XAttribute("guiclass", "TestPlanGui"), new XAttribute("testclass", "TestPlan"), new XAttribute("testname", "MockAPI"), new XAttribute("enabled", "true"),
                        JMeterString("TestPlan.comments", "Generated by MockAPI"),
                        new XElement("elementProp", new XAttribute("name", "TestPlan.user_defined_variables"), new XAttribute("elementType", "Arguments"),
                            new XElement("collectionProp", new XAttribute("name", "Arguments.arguments"),
                                JMeterVariable("SCHEME", "http"), JMeterVariable("HOST", "localhost"), JMeterVariable("PORT", "8080")))),
                    plan)));
    }

    private static string CreateK6(MockApiConfigurationDocument document)
    {
        var requests = new JsonArray(document.Endpoints.Where(endpoint => endpoint.Enabled)
            .SelectMany(endpoint => endpoint.Methods.Select(method => (JsonNode)new JsonObject
            {
                ["method"] = method,
                ["path"] = endpoint.Path,
                ["behavior"] = endpoint.Response.Behavior == MockResponseBehavior.AbortConnection ? "abortConnection" : "response",
                ["responses"] = endpoint.Response.Behavior == MockResponseBehavior.AbortConnection
                    ? null
                    : new JsonArray(GetResponses(endpoint).Select(response => (JsonNode)new JsonObject
                    {
                        ["status"] = response.StatusCode,
                        ["body"] = response.Body
                    }).ToArray())
            })).ToArray());
        var json = requests.ToJsonString();
        return $"import http from 'k6/http';\nimport {{ check }} from 'k6';\n\nconst baseUrl = __ENV.BASE_URL || 'http://localhost:8080';\nconst requests = {json};\n\nexport default function () {{\n  for (const request of requests) {{\n    const response = http.request(request.method, `${{baseUrl}}${{request.path}}`);\n    check(response, request.behavior === 'abortConnection' ? {{\n      'connection is aborted': (result) => result.status === 0,\n    }} : {{\n      'status and body match': (result) => request.responses.some((expected) => expected.status === result.status && expected.body === result.body),\n    }});\n  }}\n}}\n";
    }

    private static string CreateHttp(MockApiConfigurationDocument document)
    {
        var builder = new StringBuilder("@baseUrl = http://localhost:8080\n");
        foreach (var endpoint in document.Endpoints.Where(endpoint => endpoint.Enabled))
        {
            foreach (var method in endpoint.Methods)
            {
                builder.Append("\n### ").Append(endpoint.Name).Append(" (").Append(method).Append(")\n")
                    .Append(method).Append(" {{baseUrl}}").Append(endpoint.Path).Append('\n');
            }
        }
        return builder.ToString();
    }

    private static JsonArray PathParts(string path) => new(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(part => (JsonNode)part).ToArray());

    private static ExportResponse[] GetResponses(MockEndpointDefinition endpoint)
    {
        var response = endpoint.Response;
        if (response.Behavior == MockResponseBehavior.AbortConnection)
        {
            return [];
        }

        if (response.RateLimit is null)
        {
            return [new ExportResponse(response.StatusCode!.Value, response.ReasonPhrase, response.ContentType, response.Body)];
        }

        var success = response.RateLimit.SuccessResponse;
        return
        [
            new ExportResponse(success.StatusCode, success.ReasonPhrase, success.ContentType, success.Body),
            new ExportResponse(response.StatusCode!.Value, response.ReasonPhrase, response.ContentType, response.Body)
        ];
    }

    private static XElement JMeterString(string name, string value) => new("stringProp", new XAttribute("name", name), value);

    private static XElement JMeterVariable(string name, string value) => new("elementProp", new XAttribute("name", name), new XAttribute("elementType", "Argument"), JMeterString("Argument.name", name), JMeterString("Argument.value", value), JMeterString("Argument.metadata", "="));

    private sealed record ExportResponse(int StatusCode, string? ReasonPhrase, string? ContentType, string Body);

    internal static void ValidatePostman(JsonNode node) => Require(node["info"]?["schema"]?.GetValue<string>() == "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" && node["item"] is JsonArray, "Invalid Postman Collection 2.1 export.");
    internal static void ValidateInsomnia(JsonNode node) => Require(node["_type"]?.GetValue<string>() == "export" && node["__export_format"]?.GetValue<int>() == 4 && node["resources"] is JsonArray, "Invalid Insomnia v4 export.");
    internal static void ValidateOpenApi(JsonNode node) => Require(node["openapi"]?.GetValue<string>() == "3.1.0" && node["info"] is JsonObject && node["paths"] is JsonObject, "Invalid OpenAPI 3.1 export.");
    internal static void ValidateCurl(string content) => Require(content.StartsWith("#!/usr/bin/env sh\nset -eu\n", StringComparison.Ordinal), "Invalid cURL script export.");
    internal static void ValidateK6(string content) => Require(content.Contains("export default function ()", StringComparison.Ordinal) && content.Contains("http.request", StringComparison.Ordinal), "Invalid k6 script export.");
    internal static void ValidateHttp(string content) => Require(content.StartsWith("@baseUrl = ", StringComparison.Ordinal), "Invalid HTTP client export.");
    internal static void ValidateJMeter(XDocument document) => Require(document.Root?.Name == "jmeterTestPlan" && document.Root.Attribute("version")?.Value == "1.2" && document.Descendants("TestPlan").Any(), "Invalid JMeter JMX export.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
