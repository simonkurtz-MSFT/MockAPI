using System.Text.Json;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationSerializationTests
{
    [Fact]
    public void GeneratedMetadata_RoundTripsCompleteDocumentWithoutChangingValues()
    {
        var endpointId = Guid.Parse("7b2d425d-75f1-4ded-a74e-503374a7e99e");
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            Endpoints =
            [
                new MockEndpointDefinition
                {
                    Id = endpointId,
                    Name = "Raw response",
                    Enabled = true,
                    Methods = ["GET", "POST"],
                    Path = "/api/raw",
                    Response = new MockResponseDefinition
                    {
                        StatusCode = 429,
                        ReasonPhrase = "Too Many Requests",
                        Headers = new() { ["Set-Cookie"] = ["first=1", "second=2"] },
                        ContentType = "application/json; charset=utf-8",
                        Body = "{not-valid-json}\r\n"
                    }
                }
            ]
        };

        var json = JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
        var roundTripped = JsonSerializer.Deserialize(
            json,
            MockApiJsonContext.Default.MockApiConfigurationDocument);

        Assert.NotNull(roundTripped);
        Assert.Equal(document.Schema, roundTripped.Schema);
        Assert.Equal(document.SchemaVersion, roundTripped.SchemaVersion);
        var endpoint = Assert.Single(roundTripped.Endpoints);
        Assert.Equal(endpointId, endpoint.Id);
        Assert.Equal(["GET", "POST"], endpoint.Methods);
        Assert.Equal(["first=1", "second=2"], endpoint.Response.Headers["Set-Cookie"]);
        Assert.Equal("{not-valid-json}\r\n", endpoint.Response.Body);
        Assert.Contains("\"$schema\"", json);
        Assert.Contains("\"schemaVersion\"", json);
    }

    [Fact]
    public void GeneratedMetadata_RejectsUnknownProperties()
    {
        const string json = """
            {
              "schemaVersion": "1.0",
              "endpoints": [],
              "unexpected": true
            }
            """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(json, MockApiJsonContext.Default.MockApiConfigurationDocument));
    }
}
