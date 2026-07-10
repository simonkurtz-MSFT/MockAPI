using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationValidatorTests
{
    [Theory]
    [InlineData(ConfigurationLimits.MaximumEndpoints, true)]
    [InlineData(ConfigurationLimits.MaximumEndpoints + 1, false)]
    public void Validate_EnforcesEndpointCountLimit(int endpointCount, bool expectedIsValid)
    {
        var document = CreateDocument(Enumerable.Range(1, endpointCount).Select(CreateEndpoint).ToArray());

        var result = ConfigurationValidator.Validate(document);

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "endpoints", "maximumItems"));
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("", false)]
    [InlineData("2.0", false)]
    public void Validate_RequiresSupportedSchemaVersion(string schemaVersion, bool expectedIsValid)
    {
        var result = ConfigurationValidator.Validate(CreateDocument() with { SchemaVersion = schemaVersion });

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "schemaVersion", "const"));
    }

    [Fact]
    public void Validate_RejectsEmptyAndDuplicateEndpointIds()
    {
        var duplicateId = Guid.NewGuid();
        var document = CreateDocument(
            CreateEndpoint(1) with { Id = Guid.Empty },
            CreateEndpoint(2) with { Id = duplicateId },
            CreateEndpoint(3) with { Id = duplicateId });

        var result = ConfigurationValidator.Validate(document);

        Assert.True(HasError(result, "endpoints[0].id", "format"));
        Assert.True(HasError(result, "endpoints[2].id", "unique"));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(ConfigurationLimits.MaximumNameLength, true)]
    [InlineData(ConfigurationLimits.MaximumNameLength + 1, false)]
    public void Validate_EnforcesEndpointNameLength(int length, bool expectedIsValid)
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Name = new string('n', length) }));

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "endpoints[0].name", "maxLength"));
    }

    [Fact]
    public void Validate_RejectsBlankEndpointName()
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Name = "  " }));

        Assert.True(HasError(result, "endpoints[0].name", "minLength"));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneMethod()
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Methods = [] }));

        Assert.True(HasError(result, "endpoints[0].methods", "minItems"));
    }

    [Fact]
    public void Validate_RejectsTooManyMethods()
    {
        var methods = Enumerable.Range(1, ConfigurationLimits.MaximumMethodsPerEndpoint + 1)
            .Select(index => $"METHOD{index}")
            .ToArray();

        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Methods = methods }));

        Assert.True(HasError(result, "endpoints[0].methods", "maximumItems"));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("CUSTOM-METHOD", true)]
    [InlineData("BAD METHOD", false)]
    [InlineData("GET\r\nInjected", false)]
    public void Validate_EnforcesHttpMethodTokenSyntax(string method, bool expectedIsValid)
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Methods = [method] }));

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "endpoints[0].methods[0]", "format"));
    }

    [Fact]
    public void Validate_RejectsDuplicateMethodsCaseInsensitively()
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Methods = ["GET", "get"] }));

        Assert.True(HasError(result, "endpoints[0].methods[1]", "unique"));
    }

    [Theory]
    [InlineData(ConfigurationLimits.MaximumPathLength, true)]
    [InlineData(ConfigurationLimits.MaximumPathLength + 1, false)]
    public void Validate_EnforcesPathLength(int length, bool expectedIsValid)
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Path = "/" + new string('p', length - 1) }));

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "endpoints[0].path", "maxLength"));
    }

    [Theory]
    [InlineData("api/test", "format")]
    [InlineData("/__mockapi", "reserved")]
    [InlineData("/__mockapi/api/endpoints", "reserved")]
    [InlineData("/health", "reserved")]
    [InlineData("/HEALTH/ready", "reserved")]
    public void Validate_RejectsInvalidOrReservedPaths(string path, string code)
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Path = path }));

        Assert.True(HasError(result, "endpoints[0].path", code));
    }

    [Fact]
    public void Validate_AllowsPathThatOnlySharesReservedPrefix()
    {
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Path = "/healthcheck" }));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_RejectsDuplicateActiveMethodAndPathPair()
    {
        var document = CreateDocument(
            CreateEndpoint(1) with { Methods = ["GET"], Path = "/same" },
            CreateEndpoint(2) with { Methods = ["get"], Path = "/same" });

        var result = ConfigurationValidator.Validate(document);

        Assert.True(HasError(result, "endpoints[1].methods[0]", "conflict"));
    }

    [Fact]
    public void Validate_AllowsDisabledEndpointToDuplicateActiveRoute()
    {
        var document = CreateDocument(
            CreateEndpoint(1) with { Methods = ["GET"], Path = "/same" },
            CreateEndpoint(2) with { Enabled = false, Methods = ["get"], Path = "/same" });

        Assert.True(ConfigurationValidator.Validate(document).IsValid);
    }

    [Theory]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(599, true)]
    [InlineData(600, false)]
    public void Validate_EnforcesStatusCodeRange(int statusCode, bool expectedIsValid)
    {
        var response = CreateResponse() with { StatusCode = statusCode };
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Response = response }));

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(!expectedIsValid, HasError(result, "endpoints[0].response.statusCode", "range"));
    }

    [Fact]
    public void Validate_RejectsControlCharactersInReasonPhrase()
    {
        var response = CreateResponse() with { ReasonPhrase = "Created\r\nInjected" };
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Response = response }));

        Assert.True(HasError(result, "endpoints[0].response.reasonPhrase", "format"));
    }

    [Fact]
    public void Validate_RejectsTooManyHeaders()
    {
        var headers = Enumerable.Range(1, ConfigurationLimits.MaximumHeadersPerEndpoint + 1)
            .ToDictionary(index => $"X-Header-{index}", _ => new[] { "value" });
        var result = ValidateHeaders(headers);

        Assert.True(HasError(result, "endpoints[0].response.headers", "maximumProperties"));
    }

    [Theory]
    [InlineData("X-Valid-Header", true)]
    [InlineData("Bad Header", false)]
    [InlineData("Bad:Header", false)]
    public void Validate_EnforcesHeaderNameTokenSyntax(string headerName, bool expectedIsValid)
    {
        var result = ValidateHeaders(new() { [headerName] = ["value"] });

        Assert.Equal(expectedIsValid, result.IsValid);
        Assert.Equal(
            !expectedIsValid,
            HasError(result, $"endpoints[0].response.headers[{headerName}]", "format"));
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("content-length")]
    [InlineData("Date")]
    [InlineData("Host")]
    [InlineData("Server")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    public void Validate_RejectsControlledHeadersCaseInsensitively(string headerName)
    {
        var result = ValidateHeaders(new() { [headerName] = ["value"] });

        Assert.True(HasError(result, $"endpoints[0].response.headers[{headerName}]", "controlled"));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneValuePerHeader()
    {
        var result = ValidateHeaders(new() { ["X-Empty"] = [] });

        Assert.True(HasError(result, "endpoints[0].response.headers[X-Empty]", "minItems"));
    }

    [Fact]
    public void Validate_EnforcesHeaderValueUtf8ByteLimit()
    {
        var oversizedValue = new string('é', (ConfigurationLimits.MaximumHeaderValueBytes / 2) + 1);
        var result = ValidateHeaders(new() { ["X-Large"] = [oversizedValue] });

        Assert.True(HasError(result, "endpoints[0].response.headers[X-Large][0]", "maximumBytes"));
    }

    [Fact]
    public void Validate_RejectsCrLfInHeaderValue()
    {
        var result = ValidateHeaders(new() { ["X-Test"] = ["value\r\ninjected"] });

        Assert.True(HasError(result, "endpoints[0].response.headers[X-Test][0]", "format"));
    }

    [Fact]
    public void Validate_EnforcesCombinedHeaderUtf8ByteLimit()
    {
        var value = new string('a', ConfigurationLimits.MaximumHeaderValueBytes);
        var result = ValidateHeaders(new() { ["X-Combined"] = [value, value, value, value] });

        Assert.True(HasError(result, "endpoints[0].response.headers", "maximumBytes"));
    }

    [Fact]
    public void Validate_EnforcesBodyUtf8ByteLimit()
    {
        var oversizedBody = new string('é', (ConfigurationLimits.MaximumBodyBytes / 2) + 1);
        var response = CreateResponse() with
        {
            Body = oversizedBody,
            ContentType = "text/plain; charset=utf-8"
        };
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Response = response }));

        Assert.True(HasError(result, "endpoints[0].response.body", "maximumBytes"));
    }

    [Fact]
    public void Validate_RequiresContentTypeForNonEmptyBody()
    {
        var response = CreateResponse() with { Body = "content", ContentType = null };
        var result = ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Response = response }));

        Assert.True(HasError(result, "endpoints[0].response.contentType", "required"));
    }

    [Fact]
    public void Validate_EnforcesDocumentUtf8ByteLimit()
    {
        var body = new string('a', ConfigurationLimits.MaximumBodyBytes);
        var endpoints = Enumerable.Range(1, 4)
            .Select(index => CreateEndpoint(index) with
            {
                Response = CreateResponse() with { Body = body, ContentType = "text/plain" }
            })
            .ToArray();

        var result = ConfigurationValidator.Validate(CreateDocument(endpoints));

        Assert.True(HasError(result, "$", "maximumBytes"));
    }

    [Fact]
    public void Validate_ReturnsAllActionableErrors()
    {
        var invalidEndpoint = CreateEndpoint(1) with
        {
            Id = Guid.Empty,
            Name = " ",
            Methods = ["BAD METHOD"],
            Path = "/health",
            Response = CreateResponse() with { StatusCode = 700 }
        };
        var result = ConfigurationValidator.Validate(
            CreateDocument(invalidEndpoint) with { SchemaVersion = "2.0" });

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 6);
    }

    private static ConfigurationValidationResult ValidateHeaders(Dictionary<string, string[]> headers)
    {
        var response = CreateResponse() with { Headers = headers };
        return ConfigurationValidator.Validate(
            CreateDocument(CreateEndpoint(1) with { Response = response }));
    }

    private static bool HasError(ConfigurationValidationResult result, string path, string code) =>
        result.Errors.Any(error => error.Path == path && error.Code == code);

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static MockEndpointDefinition CreateEndpoint(int index) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"Endpoint {index}",
        Enabled = true,
        Methods = ["GET"],
        Path = $"/endpoint-{index}",
        Response = CreateResponse()
    };

    private static MockResponseDefinition CreateResponse() => new()
    {
        StatusCode = 200,
        Headers = [],
        Body = string.Empty
    };
}