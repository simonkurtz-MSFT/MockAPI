using System.Text;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class CompiledResponseReuseTests
{
    [Fact]
    public void Replacement_ReusesResponseStorageByStableIdAcrossRouteAndMetadataChanges()
    {
        var definition = CreateEndpoint("original");
        var registry = new EndpointRegistry();
        Assert.True(registry.TryReplace(CreateDocument(definition)).IsValid);
        var originalSnapshot = registry.Current;
        Assert.True(originalSnapshot.TryGet("GET", "/original", out var original));

        var edited = definition with { Name = "renamed", Path = "/renamed", Methods = ["POST"] };
        Assert.True(registry.TryReplace(CreateDocument(edited)).IsValid);
        Assert.True(registry.Current.TryGet("POST", "/renamed", out var replacement));

        Assert.NotSame(original, replacement);
        Assert.True(original.Body.Equals(replacement.Body));
        Assert.True(original.Headers.Equals(replacement.Headers));
        Assert.False(registry.Current.TryGet("GET", "/original", out _));
        Assert.True(originalSnapshot.TryGet("GET", "/original", out _));

        edited.Response.Headers["X-First"][0] = "mutated";
        Assert.Equal("first", original.Headers[0].Values[0]);
        Assert.Equal("first", replacement.Headers[0].Values[0]);
    }

    [Theory]
    [MemberData(nameof(BodyCases))]
    public void Replacement_ReusesOnlyIdenticalEncodedBodyBytes(string originalBody, string replacementBody, bool reuse)
    {
        var definition = CreateEndpoint(originalBody);
        var registry = new EndpointRegistry();
        Assert.True(registry.TryReplace(CreateDocument(definition)).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var original));
        var edited = definition with { Response = definition.Response with { Body = replacementBody } };

        Assert.True(registry.TryReplace(CreateDocument(edited)).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var replacement));

        Assert.Equal(reuse, original.Body.Equals(replacement.Body));
        Assert.Equal(Encoding.UTF8.GetBytes(originalBody), original.Body.ToArray());
        Assert.Equal(Encoding.UTF8.GetBytes(replacementBody), replacement.Body.ToArray());
    }

    public static TheoryData<string, string, bool> BodyCases => new()
    {
        { "", "", true },
        { "same", "same", true },
        { "before", "after", false },
        { "longer", "long", false },
        { "short", "shorter", false },
        { "nonempty", "", false },
        { "", "nonempty", false },
        { new string('x', 511) + "\uD83D\uDE03", new string('x', 511) + "\uD83D\uDE03", true },
        { new string('x', 1023) + "\u00E9\u6C34", new string('x', 1023) + "\u00E9\u6C34", true },
        { new string('x', 1024) + "end", new string('x', 1024) + "edited", false }
    };

    [Fact]
    public void BodyComparison_PreservesEncodingReplacementFallbackForLoneSurrogates()
    {
        foreach (var surrogate in new[] { "\uD800", "\uDC00" })
        {
            var definition = CreateEndpoint(new string('x', 511) + surrogate);
            var original = RuntimeEndpoint.Create(definition);
            var edited = definition with { Response = definition.Response with { Body = new string('x', 511) + "\uFFFD" } };

            var replacement = RuntimeEndpoint.Create(edited, original);

            Assert.True(original.Body.Equals(replacement.Body));
            Assert.Equal(Encoding.UTF8.GetBytes(edited.Response.Body), replacement.Body.ToArray());
        }
    }

    [Fact]
    public void ReusedWireData_DoesNotRetainPreviousStatusContentTypeReasonOrDelay()
    {
        var definition = CreateEndpoint("body");
        var original = RuntimeEndpoint.Create(definition);
        var edited = definition with
        {
            Response = definition.Response with
            {
                StatusCode = 202,
                ReasonPhrase = "Accepted",
                ContentType = "application/custom",
                DelaySeconds = 1
            }
        };

        var replacement = RuntimeEndpoint.Create(edited, original);

        Assert.True(original.Body.Equals(replacement.Body));
        Assert.True(original.Headers.Equals(replacement.Headers));
        Assert.Equal(202, replacement.StatusCode);
        Assert.Equal("Accepted", replacement.ReasonPhrase);
        Assert.Equal("application/custom", replacement.ContentType);
        Assert.Equal(TimeSpan.FromSeconds(1), replacement.Delay);
        Assert.Equal(200, original.StatusCode);
        Assert.Null(original.ReasonPhrase);
        Assert.Equal(TimeSpan.Zero, original.Delay);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    public void Replacement_PreservesExactHeaderNamesOrderingAndValues(int change, bool reuse)
    {
        var definition = CreateEndpoint("body");
        var registry = new EndpointRegistry();
        Assert.True(registry.TryReplace(CreateDocument(definition)).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var original));
        Dictionary<string, string[]> headers = change switch
        {
            0 => new() { ["X-First"] = ["first", "second"], ["X-Last"] = ["last"] },
            1 => new() { ["x-first"] = ["first", "second"], ["X-Last"] = ["last"] },
            2 => new() { ["X-Last"] = ["last"], ["X-First"] = ["first", "second"] },
            3 => new() { ["X-First"] = ["second", "first"], ["X-Last"] = ["last"] },
            4 => new() { ["X-First"] = ["first", "second"] },
            5 => new() { ["X-First"] = ["first", "second"], ["X-Last"] = ["last"], ["X-New"] = ["new"] },
            _ => new() { ["X-First"] = ["first"], ["X-Last"] = ["last"] }
        };
        var edited = definition with { Response = definition.Response with { Headers = headers } };

        Assert.True(registry.TryReplace(CreateDocument(edited)).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var replacement));

        Assert.Equal(reuse, original.Headers.Equals(replacement.Headers));
        Assert.True(original.Body.Equals(replacement.Body));
        Assert.Equal(headers.Keys, replacement.Headers.Select(header => header.Name));
        Assert.Equal(headers.Values, replacement.Headers.Select(header => header.Values.ToArray()));
    }

    [Fact]
    public void ConfigurationReplacement_ReusesBothResponsesButResetsRateLimitPermits()
    {
        var definition = CreateEndpoint("limited") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = new() { ["Retry-After"] = ["3600"] },
                ContentType = "text/plain",
                Body = "limited",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 1,
                    WindowSeconds = 3600,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = 201,
                        Headers = new() { ["X-Success"] = ["first", "second"] },
                        ContentType = "text/plain",
                        Body = "success"
                    }
                }
            }
        };
        var state = new ConfigurationState();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(definition), 0).Status);
        var oldSnapshot = state.Current;
        Assert.True(oldSnapshot.Endpoints.TryGet("GET", "/original", out var original));
        var oldSuccess = original.SelectResponse();
        Assert.Equal(201, oldSuccess.StatusCode);
        Assert.Equal(429, original.SelectResponse().StatusCode);

        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(definition), 1).Status);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/original", out var replacement));
        var newSuccess = replacement.SelectResponse();

        Assert.NotSame(original, replacement);
        Assert.True(original.Body.Equals(replacement.Body));
        Assert.True(original.Headers.Equals(replacement.Headers));
        Assert.True(oldSuccess.Body.Equals(newSuccess.Body));
        Assert.True(oldSuccess.Headers.Equals(newSuccess.Headers));
        Assert.Equal(201, newSuccess.StatusCode);
        Assert.Equal(429, replacement.SelectResponse().StatusCode);
        Assert.Equal(429, original.SelectResponse().StatusCode);
        Assert.Equal(1, oldSnapshot.Revision);
    }

    [Fact]
    public void Replacement_DoesNotReuseStorageForAnUnrelatedEndpointId()
    {
        var definition = CreateEndpoint("body");
        var registry = new EndpointRegistry();
        Assert.True(registry.TryReplace(CreateDocument(definition)).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var original));

        Assert.True(registry.TryReplace(CreateDocument(definition with { Id = Guid.NewGuid() })).IsValid);
        Assert.True(registry.Current.TryGet("GET", "/original", out var replacement));

        Assert.False(original.Body.Equals(replacement.Body));
        Assert.False(original.Headers.Equals(replacement.Headers));
    }

    [Fact]
    public void WarmResponseCompilation_DoesNotAllocateAnotherLargeBodyArray()
    {
        var definition = CreateEndpoint(new string('x', 512 * 1024));
        var previous = RuntimeEndpoint.Create(definition);
        for (var index = 0; index < 5; index++)
        {
            RuntimeEndpoint.Create(definition, previous);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var replacement = RuntimeEndpoint.Create(definition, previous);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(previous.Body.Equals(replacement.Body));
        // Isolate compilation: full validation still serializes the document and may refill serializer pools.
        Assert.InRange(allocated, 0, 1_024);
    }

    internal static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static MockEndpointDefinition CreateEndpoint(string body) => new()
    {
        Id = Guid.NewGuid(),
        Name = "original",
        Enabled = true,
        Methods = ["GET"],
        Path = "/original",
        Response = new MockResponseDefinition
        {
            StatusCode = 200,
            Headers = new() { ["X-First"] = ["first", "second"], ["X-Last"] = ["last"] },
            ContentType = "text/plain",
            Body = body
        }
    };
}
