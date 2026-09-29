using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class EndpointRegistryTests
{
    [Fact]
    public void TryReplace_PublishesCompleteSnapshotWithExactMatchingRules()
    {
        var registry = new EndpointRegistry();
        var enabled = CreateEndpoint("GET", "/orders", "enabled");
        var disabled = CreateEndpoint("POST", "/orders", "disabled") with { Enabled = false };

        var result = registry.TryReplace(CreateDocument(enabled, disabled));

        Assert.True(result.IsValid);
        Assert.True(registry.Current.TryGet("get", "/orders", out var endpoint));
        Assert.Equal(enabled.Id, endpoint.Id);
        Assert.False(registry.Current.TryGet("GET", "/Orders", out _));
        Assert.False(registry.Current.TryGet("POST", "/orders", out _));
    }

    [Fact]
    public void TryReplace_IsAtomicAndRejectedCandidateLeavesSnapshotUnchanged()
    {
        var registry = new EndpointRegistry();
        var original = CreateEndpoint("GET", "/original", "original");
        Assert.True(registry.TryReplace(CreateDocument(original)).IsValid);
        var originalSnapshot = registry.Current;

        var invalid = CreateEndpoint("GET", "/health", "invalid");
        var invalidResult = registry.TryReplace(CreateDocument(invalid));

        Assert.False(invalidResult.IsValid);
        Assert.Same(originalSnapshot, registry.Current);
        Assert.True(registry.Current.TryGet("GET", "/original", out _));

        var replacement = CreateEndpoint("POST", "/replacement", "replacement");
        var replacementResult = registry.TryReplace(CreateDocument(replacement));

        Assert.True(replacementResult.IsValid);
        Assert.NotSame(originalSnapshot, registry.Current);
        Assert.True(registry.Current.TryGet("POST", "/replacement", out _));
        Assert.False(registry.Current.TryGet("GET", "/original", out _));
        Assert.True(originalSnapshot.TryGet("GET", "/original", out _));
        Assert.False(originalSnapshot.TryGet("POST", "/replacement", out _));
    }

    [Fact]
    public void PublishedSnapshot_DoesNotReferenceMutableConfigurationCollections()
    {
        var methods = new[] { "GET" };
        var headerValues = new[] { "original" };
        var endpointDefinition = CreateEndpoint("GET", "/immutable", "body") with
        {
            Methods = methods,
            Response = new MockResponseDefinition
            {
                StatusCode = 200,
                Headers = new() { ["X-Value"] = headerValues },
                ContentType = "text/plain",
                Body = "body"
            }
        };
        var registry = new EndpointRegistry();
        Assert.True(registry.TryReplace(CreateDocument(endpointDefinition)).IsValid);

        methods[0] = "POST";
        headerValues[0] = "changed";

        Assert.True(registry.Current.TryGet("GET", "/immutable", out var runtimeEndpoint));
        Assert.False(registry.Current.TryGet("POST", "/immutable", out _));
        Assert.Equal("original", Assert.Single(Assert.Single(runtimeEndpoint.Headers).Values));
    }

    [Fact]
    public void ConcurrentReplacement_NeverExposesPartialSnapshot()
    {
        var registry = new EndpointRegistry();
        var first = CreateDocument(CreateEndpoint("GET", "/first", "first"));
        var second = CreateDocument(CreateEndpoint("POST", "/second", "second"));
        Assert.True(registry.TryReplace(first).IsValid);

        Parallel.For(0, 5_000, index =>
        {
            Assert.True(registry.TryReplace(index % 2 == 0 ? first : second).IsValid);
            var snapshot = registry.Current;
            var hasFirst = snapshot.TryGet("GET", "/first", out _);
            var hasSecond = snapshot.TryGet("POST", "/second", out _);
            Assert.NotEqual(hasFirst, hasSecond);
        });
    }

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static MockEndpointDefinition CreateEndpoint(string method, string path, string body) => new()
    {
        Id = Guid.NewGuid(),
        Name = path,
        Enabled = true,
        Methods = [method],
        Path = path,
        Response = new MockResponseDefinition
        {
            StatusCode = 200,
            Headers = [],
            ContentType = "text/plain; charset=utf-8",
            Body = body
        }
    };
}
