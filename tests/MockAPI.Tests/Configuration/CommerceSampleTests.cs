using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class CommerceSampleTests
{
    [Theory]
    [InlineData("trailhead-inventory.json", "/inventory", 11)]
    [InlineData("trailhead-shipping.json", "/shipping", 14)]
    [InlineData("trailhead-aoai.json", "/aoai", 16)]
    public void Sample_ValidatesAgainstSchemaAndRoundTripsDeterministically(
        string fileName, string apiPath, int endpointCount)
    {
        var json = File.ReadAllText(ConfigurationSchemaFixture.GetSamplePath(fileName));
        Assert.InRange(Encoding.UTF8.GetByteCount(json), 1, ConfigurationLimits.MaximumDocumentBytes);
        AssertSchemaValid(json);
        var document = ReadSample(fileName);

        Assert.True(ConfigurationValidator.Validate(document).IsValid);
        Assert.Equal(endpointCount, document.Endpoints.Count);
        Assert.NotNull(document.ApiDescriptions);
        Assert.Contains("static", document.ApiDescriptions[apiPath], StringComparison.OrdinalIgnoreCase);
        Assert.All(document.Endpoints, endpoint =>
        {
            Assert.True(endpoint.Enabled);
            Assert.StartsWith(apiPath + "/", endpoint.Path, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(endpoint.Description));
            if (endpoint.Response.ContentType?.Contains("json", StringComparison.Ordinal) == true)
            {
                using var body = JsonDocument.Parse(endpoint.Response.Body);
                Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
                if (endpoint.Response.ContentType.StartsWith("application/problem+json", StringComparison.Ordinal))
                {
                    Assert.Equal(endpoint.Response.StatusCode, body.RootElement.GetProperty("status").GetInt32());
                    Assert.Equal(endpoint.Response.ReasonPhrase, body.RootElement.GetProperty("title").GetString());
                    Assert.Equal("about:blank", body.RootElement.GetProperty("type").GetString());
                }
            }
        });

        var serialized = JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument);
        AssertSchemaValid(serialized);
        var roundTrip = JsonSerializer.Deserialize(serialized, MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(roundTrip);
        Assert.Equal(serialized, JsonSerializer.Serialize(roundTrip, MockApiJsonContext.Default.MockApiConfigurationDocument));
    }

    [Theory]
    [InlineData("trailhead-inventory.json")]
    [InlineData("trailhead-shipping.json")]
    [InlineData("trailhead-aoai.json")]
    public void Sample_EndpointInformationExplainsBusinessUseCaseBeforeTechnicalImplementation(string fileName)
    {
        var document = ReadSample(fileName);

        Assert.All(document.Endpoints, endpoint =>
        {
            Assert.NotNull(endpoint.Description);
            Assert.StartsWith("Description:\n", endpoint.Description, StringComparison.Ordinal);
            var sections = endpoint.Description.Split("\n\nTechnical implementation:\n", StringSplitOptions.None);
            Assert.Equal(2, sections.Length);
            Assert.False(string.IsNullOrWhiteSpace(sections[0]["Description:\n".Length..]));
            Assert.False(string.IsNullOrWhiteSpace(sections[1]));
            Assert.InRange(endpoint.Description.Length, 1, ConfigurationLimits.MaximumDescriptionLength);
        });
    }

    [Fact]
    public void InventoryAndShipping_CanFormOneValidConfigurationWithoutIdOrRouteConflicts()
    {
        var inventory = ReadSample("trailhead-inventory.json");
        var shipping = ReadSample("trailhead-shipping.json");
        var combined = inventory with
        {
            ApiDescriptions = inventory.ApiDescriptions!
                .Concat(shipping.ApiDescriptions!)
                .ToDictionary(pair => pair.Key, pair => pair.Value),
            Endpoints = [.. inventory.Endpoints, .. shipping.Endpoints]
        };

        Assert.Equal(25, combined.Endpoints.Count);
        Assert.Equal(25, combined.Endpoints.Select(endpoint => endpoint.Id).Distinct().Count());
        Assert.True(ConfigurationValidator.Validate(combined).IsValid);
        AssertSchemaValid(JsonSerializer.Serialize(combined, MockApiJsonContext.Default.MockApiConfigurationDocument));
    }

    [Theory]
    [InlineData("trailhead-aoai.json", "/aoai/health")]
    [InlineData("trailhead-inventory.json", "/inventory/health")]
    [InlineData("trailhead-shipping.json", "/shipping/health")]
    public async Task Sample_OnlyHealthSupportsHeadAndReturnsAnEmpty200(string fileName, string healthPath)
    {
        var document = ReadSample(fileName);
        var health = Assert.Single(document.Endpoints, endpoint => endpoint.Methods.Contains("HEAD"));
        Assert.Equal(healthPath, health.Path);
        Assert.Equal(["HEAD"], health.Methods);
        Assert.Equal(200, health.Response.StatusCode);
        Assert.Null(health.Response.ContentType);
        Assert.Empty(health.Response.Body);
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Head, healthPath);

        using var response = await client.SendAsync(request);

        await AssertResponseAsync(response, health.Response, HttpMethod.Head);
        using var get = await client.GetAsync(healthPath);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Theory]
    [InlineData("trailhead-inventory.json", "/inventory/items/SKU-1001")]
    [InlineData("trailhead-shipping.json", "/shipping/shipments/shp-demo-001")]
    public async Task Inspection_ServesGetButDoesNotMatchHead(string fileName, string path)
    {
        var endpoint = FindEndpoint(ReadSample(fileName), "GET", path);
        Assert.Equal(["GET"], endpoint.Methods);
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();

        using var get = await client.GetAsync(path);
        await AssertResponseAsync(get, endpoint.Response, HttpMethod.Get);
        using var request = new HttpRequestMessage(HttpMethod.Head, path);
        using var head = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("trailhead-inventory.json", "GET", "/inventory/items/unknown", 404)]
    [InlineData("trailhead-inventory.json", "POST", "/inventory/demo-faults/insufficient-stock", 409)]
    [InlineData("trailhead-inventory.json", "POST", "/inventory/demo-faults/invalid-reservation", 422)]
    [InlineData("trailhead-inventory.json", "GET", "/inventory/demo-faults/warehouse-unavailable", 503)]
    [InlineData("trailhead-shipping.json", "GET", "/shipping/tracking/unknown", 404)]
    [InlineData("trailhead-shipping.json", "POST", "/shipping/demo-faults/invalid-shipment", 422)]
    [InlineData("trailhead-shipping.json", "POST", "/shipping/demo-faults/service-unavailable", 503)]
    [InlineData("trailhead-shipping.json", "GET", "/shipping/demo-faults/carrier-timeout", 504)]
    public void Sample_ProvidesTheDocumentedFailureStatus(string fileName, string method, string path, int statusCode)
    {
        var endpoint = FindEndpoint(ReadSample(fileName), method, path);

        Assert.Equal(statusCode, endpoint.Response.StatusCode);
        Assert.Equal("application/problem+json; charset=utf-8", endpoint.Response.ContentType);
    }

    [Theory]
    [InlineData("trailhead-inventory.json")]
    [InlineData("trailhead-shipping.json")]
    public void Sample_AdvertisedResourceLinksResolveToSuccessfulGetEndpoints(string fileName)
    {
        var document = ReadSample(fileName);
        foreach (var endpoint in document.Endpoints)
        {
            if (endpoint.Response.Headers.TryGetValue("Location", out var locations))
            {
                AssertSuccessfulLookup(document, Assert.Single(locations));
            }
            if (endpoint.Response.ContentType != "application/json; charset=utf-8")
            {
                continue;
            }

            using var body = JsonDocument.Parse(endpoint.Response.Body);
            foreach (var propertyName in new[] { "itemUrl", "trackingUrl", "labelUrl" })
            {
                if (body.RootElement.TryGetProperty(propertyName, out var link))
                {
                    AssertSuccessfulLookup(document, link.GetString());
                }
            }
            if (body.RootElement.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    AssertSuccessfulLookup(document, item.GetProperty("url").GetString());
                }
            }
        }
    }

    [Fact]
    public void Inventory_ListAndDetailSnapshotsHaveConsistentStockQuantities()
    {
        var document = ReadSample("trailhead-inventory.json");
        var listing = FindEndpoint(document, "GET", "/inventory/items");
        using var body = JsonDocument.Parse(listing.Response.Body);

        Assert.Equal(2, body.RootElement.GetProperty("items").GetArrayLength());
        foreach (var item in body.RootElement.GetProperty("items").EnumerateArray())
        {
            var detail = FindEndpoint(document, "GET", item.GetProperty("url").GetString()!);
            using var detailBody = JsonDocument.Parse(detail.Response.Body);
            var snapshot = detailBody.RootElement;
            var available = snapshot.GetProperty("quantityAvailable").GetInt32();
            Assert.Equal(item.GetProperty("sku").GetString(), snapshot.GetProperty("sku").GetString());
            Assert.Equal(item.GetProperty("quantityAvailable").GetInt32(), available);
            Assert.Equal(
                snapshot.GetProperty("quantityOnHand").GetInt32() - snapshot.GetProperty("quantityReserved").GetInt32(),
                available);
            Assert.Equal(available == 0 ? "out_of_stock" : "in_stock", snapshot.GetProperty("status").GetString());
        }
    }

    [Fact]
    public void Shipping_ShipmentTrackingAndLabelUseTheSameFictionalIdentifiers()
    {
        var document = ReadSample("trailhead-shipping.json");
        var shipment = FindEndpoint(document, "GET", "/shipping/shipments/shp-demo-001");
        using var body = JsonDocument.Parse(shipment.Response.Body);
        var tracking = FindEndpoint(document, "GET", body.RootElement.GetProperty("trackingUrl").GetString()!);
        using var trackingBody = JsonDocument.Parse(tracking.Response.Body);
        var label = FindEndpoint(document, "GET", body.RootElement.GetProperty("labelUrl").GetString()!);
        var delivered = FindEndpoint(document, "GET", "/shipping/tracking/TRACK-DEMO-DELIVERED");
        using var deliveredBody = JsonDocument.Parse(delivered.Response.Body);

        Assert.Equal("shp-demo-001", body.RootElement.GetProperty("id").GetString());
        Assert.Equal(body.RootElement.GetProperty("trackingId").GetString(), trackingBody.RootElement.GetProperty("trackingId").GetString());
        Assert.Equal("label_created", trackingBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("delivered", deliveredBody.RootElement.GetProperty("status").GetString());
        Assert.Contains("Shipment: shp-demo-001", label.Response.Body, StringComparison.Ordinal);
        Assert.Contains("Tracking: TRACK-DEMO-001", label.Response.Body, StringComparison.Ordinal);
        Assert.Contains("Not valid for shipment.", label.Response.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trailhead-inventory.json")]
    [InlineData("trailhead-shipping.json")]
    [InlineData("trailhead-aoai.json")]
    public async Task Startup_ServesEveryStaticResponseWithExactHeadersAndBodyBytes(string fileName)
    {
        var document = ReadSample(fileName);
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        foreach (var endpoint in document.Endpoints)
        {
            if (endpoint.Response.Behavior == MockResponseBehavior.AbortConnection || endpoint.Response.RateLimit is not null)
            {
                continue;
            }
            foreach (var method in endpoint.Methods)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.Path + "?ignored=true");
                var stopwatch = Stopwatch.StartNew();
                using var response = await client.SendAsync(request);
                await AssertResponseAsync(response, endpoint.Response, request.Method);
                if (endpoint.Response.DelaySeconds is { } delaySeconds)
                {
                    Assert.True(stopwatch.Elapsed >= TimeSpan.FromSeconds(delaySeconds));
                }
            }
        }
    }

    [Theory]
    [InlineData("trailhead-inventory.json")]
    [InlineData("trailhead-shipping.json")]
    [InlineData("trailhead-aoai.json")]
    public async Task NativeExport_PreservesTheSampleAndValidatesAgainstSchema(string fileName)
    {
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();

        var exportedJson = await client.GetStringAsync("/__mockapi/api/configuration/export");

        AssertSchemaValid(exportedJson);
        var exported = JsonSerializer.Deserialize(exportedJson, MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(exported);
        Assert.Equal(
            JsonSerializer.Serialize(ReadSample(fileName), MockApiJsonContext.Default.MockApiConfigurationDocument),
            JsonSerializer.Serialize(exported, MockApiJsonContext.Default.MockApiConfigurationDocument));
    }

    [Theory]
    [InlineData("trailhead-inventory.json", "/inventory/reservations", "/inventory/reservations/res-demo-001")]
    [InlineData("trailhead-shipping.json", "/shipping/shipments", "/shipping/shipments/shp-demo-001")]
    public async Task CreationAndCancellation_LeaveStaticLookupsUnchanged(
        string fileName, string creationPath, string lookupPath)
    {
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        var beforeCreation = await client.GetStringAsync(lookupPath);
        using var payload = new StringContent("{\"scenario\":\"ignored\",\"quantity\":999}", Encoding.UTF8, "application/json");

        using var created = await client.PostAsync(creationPath, payload);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(lookupPath, created.Headers.Location!.OriginalString);
        Assert.Equal(beforeCreation, await created.Content.ReadAsStringAsync());
        using var canceled = await client.DeleteAsync(lookupPath);
        Assert.Equal(HttpStatusCode.NoContent, canceled.StatusCode);
        Assert.Equal(beforeCreation, await client.GetStringAsync(lookupPath));
        if (fileName == "trailhead-inventory.json")
        {
            var item = FindEndpoint(ReadSample(fileName), "GET", "/inventory/items/SKU-1001");
            Assert.Equal(item.Response.Body, await client.GetStringAsync(item.Path));
        }
    }

    [Theory]
    [InlineData("trailhead-inventory.json", "/inventory/demo-faults/invalid-reservation")]
    [InlineData("trailhead-shipping.json", "/shipping/demo-faults/invalid-shipment")]
    [InlineData("trailhead-aoai.json", "/aoai/200/chat/completions")]
    public async Task MalformedJson_OverridesTheConfiguredBusinessValidationFault(string fileName, string path)
    {
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        using var payload = new StringContent("{broken", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(path, payload);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Headers.Contains("X-Mock-Source"));
    }

    [Fact]
    public async Task ShippingRateLimit_AllowsTwoRequestsThenReturnsTheConfigured429()
    {
        var endpoint = FindEndpoint(ReadSample("trailhead-shipping.json"), "GET", "/shipping/demo-faults/tracking-rate-limit");
        Assert.Equal(3, endpoint.RequestCount);
        Assert.Equal(429, endpoint.Response.StatusCode);
        Assert.Equal(["10"], endpoint.Response.Headers["Retry-After"]);
        var rateLimit = endpoint.Response.RateLimit;
        Assert.NotNull(rateLimit);
        Assert.Equal(2, rateLimit.RequestLimit);
        Assert.Equal(10, rateLimit.WindowSeconds);
        var success = rateLimit.SuccessResponse;
        Assert.Equal(200, success.StatusCode);
        Assert.DoesNotContain("Retry-After", success.Headers.Keys);
        using var successBody = JsonDocument.Parse(success.Body);
        Assert.Equal("TRACK-DEMO-001", successBody.RootElement.GetProperty("trackingId").GetString());
        var expectedSuccess = new MockResponseDefinition
        {
            StatusCode = success.StatusCode,
            ReasonPhrase = success.ReasonPhrase,
            Headers = success.Headers,
            ContentType = success.ContentType,
            Body = success.Body
        };
        await using var factory = CreateFactory("trailhead-shipping.json");
        using var client = factory.CreateClient();

        for (var request = 0; request < 3; request++)
        {
            using var response = await client.GetAsync(endpoint.Path);
            var expected = request < 2 ? expectedSuccess : endpoint.Response;
            await AssertResponseAsync(response, expected, HttpMethod.Get);
        }
    }

    [Theory]
    [InlineData("trailhead-shipping.json", "GET", "/shipping/demo-faults/connection-failure")]
    [InlineData("trailhead-aoai.json", "POST", "/aoai/transport-failure/chat/completions")]
    public async Task SampleConnectionFailure_AbortsWithoutAnHttpResponse(string fileName, string method, string path)
    {
        var endpoint = FindEndpoint(ReadSample(fileName), method, path);
        Assert.Equal(MockResponseBehavior.AbortConnection, endpoint.Response.Behavior);
        Assert.Null(endpoint.Response.StatusCode);
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.Path);

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendAsync(request));
    }

    [Fact]
    public void TrailheadAssistant_AnswerAgreesWithTheStaticInventoryAndShippingFixtures()
    {
        var completion = FindEndpoint(ReadSample("trailhead-aoai.json"), "POST", "/aoai/200/chat/completions");
        var item = FindEndpoint(ReadSample("trailhead-inventory.json"), "GET", "/inventory/items/SKU-1001");
        var shipment = FindEndpoint(ReadSample("trailhead-shipping.json"), "GET", "/shipping/shipments/shp-demo-001");
        using var completionBody = JsonDocument.Parse(completion.Response.Body);
        using var itemBody = JsonDocument.Parse(item.Response.Body);
        using var shipmentBody = JsonDocument.Parse(shipment.Response.Body);
        var answer = completionBody.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = answer.GetProperty("content").GetString()!;

        Assert.Equal("chat.completion", completionBody.RootElement.GetProperty("object").GetString());
        Assert.Equal("assistant", answer.GetProperty("role").GetString());
        Assert.Contains("Trailhead Supply", content, StringComparison.Ordinal);
        Assert.Contains(
            $"{itemBody.RootElement.GetProperty("sku").GetString()} ({itemBody.RootElement.GetProperty("name").GetString()})",
            content, StringComparison.Ordinal);
        Assert.Contains(
            $"{itemBody.RootElement.GetProperty("quantityAvailable").GetInt32()} units available at {itemBody.RootElement.GetProperty("warehouseId").GetString()}",
            content, StringComparison.Ordinal);
        Assert.Contains(
            $"Shipment {shipmentBody.RootElement.GetProperty("id").GetString()} is {shipmentBody.RootElement.GetProperty("status").GetString()}",
            content, StringComparison.Ordinal);
        Assert.Contains(shipmentBody.RootElement.GetProperty("trackingId").GetString()!, content, StringComparison.Ordinal);
        Assert.Contains("fixed sample snapshots", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"How many Trail flasks are available?\"}]}")]
    [InlineData("{\"messages\":[{\"role\":\"user\",\"content\":\"Track a different shipment.\"}]}")]
    public async Task TrailheadAssistant_ReturnsTheSameAnswerRegardlessOfPromptOrQuery(string? payload)
    {
        const string fileName = "trailhead-aoai.json";
        var endpoint = FindEndpoint(ReadSample(fileName), "POST", "/aoai/200/chat/completions");
        await using var factory = CreateFactory(fileName);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Path + "?api-version=demo&scenario=error");
        if (payload is not null)
        {
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request);

        await AssertResponseAsync(response, endpoint.Response, HttpMethod.Post);
    }

    private static MockApiConfigurationDocument ReadSample(string fileName)
    {
        var document = JsonSerializer.Deserialize(
            File.ReadAllText(ConfigurationSchemaFixture.GetSamplePath(fileName)),
            MockApiJsonContext.Default.MockApiConfigurationDocument);
        Assert.NotNull(document);
        return document;
    }

    private static WebApplicationFactory<Program> CreateFactory(string fileName) =>
        new UnsecuredApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:ConfigurationPath", ConfigurationSchemaFixture.GetSamplePath(fileName));
            builder.UseSetting("MockApi:AllowEmptyConfiguration", "false");
        });

    private static MockEndpointDefinition FindEndpoint(MockApiConfigurationDocument document, string method, string path) =>
        Assert.Single(document.Endpoints, endpoint => endpoint.Path == path && endpoint.Methods.Contains(method));

    private static void AssertSuccessfulLookup(MockApiConfigurationDocument document, string? path)
    {
        Assert.NotNull(path);
        Assert.Equal(200, FindEndpoint(document, "GET", path).Response.StatusCode);
    }

    private static void AssertSchemaValid(string json)
    {
        using var instance = JsonDocument.Parse(json);
        var result = ConfigurationSchemaFixture.Evaluate(instance.RootElement);
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
    }

    private static async Task AssertResponseAsync(
        HttpResponseMessage response, MockResponseDefinition expected, HttpMethod method)
    {
        Assert.Equal(expected.StatusCode, (int)response.StatusCode);
        Assert.Equal(expected.ReasonPhrase, response.ReasonPhrase);
        Assert.Equal(expected.ContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            Encoding.UTF8.GetBytes(method == HttpMethod.Head ? string.Empty : expected.Body),
            await response.Content.ReadAsByteArrayAsync());
        foreach (var header in expected.Headers)
        {
            Assert.True(
                response.Headers.TryGetValues(header.Key, out var values) ||
                response.Content.Headers.TryGetValues(header.Key, out values),
                $"Missing configured header: {header.Key}");
            Assert.Equal(header.Value, values);
        }
    }
}
