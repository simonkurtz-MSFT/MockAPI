using System.Text.Json;
using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationStateTests
{
    [Fact]
    public void TryReplace_AtomicallyPublishesDocumentRoutesRevisionAndDirtyState()
    {
        var state = new ConfigurationState();
        var endpoint = CreateEndpoint("GET", "/active", "body");

        var result = state.TryReplace(CreateDocument(endpoint), expectedRevision: 0);

        Assert.Equal(ConfigurationUpdateStatus.Applied, result.Status);
        var snapshot = state.Current;
        Assert.Equal(1, snapshot.Revision);
        Assert.Equal("\"1\"", snapshot.ETag);
        Assert.True(snapshot.HasUnsavedChanges);
        Assert.True(snapshot.Endpoints.TryGet("get", "/active", out var runtimeEndpoint));
        Assert.Equal(endpoint.Id, runtimeEndpoint.Id);
        Assert.Equal(endpoint.Id, Assert.Single(snapshot.GetDocument().Endpoints).Id);
    }

    [Fact]
    public void TryReplace_InvalidCandidateLeavesAllStateUnchanged()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/original", "body")), 0).Status);
        var original = state.Current;

        var result = state.TryReplace(
            CreateDocument(CreateEndpoint("GET", "/health", "invalid")),
            original.Revision);

        Assert.Equal(ConfigurationUpdateStatus.ValidationFailed, result.Status);
        Assert.NotEmpty(result.Validation.Errors);
        Assert.Same(original, state.Current);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/original", out _));
    }

    [Fact]
    public void TryReplace_StaleRevisionLeavesAllStateUnchanged()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/current", "body")), 0).Status);
        var current = state.Current;

        var result = state.TryReplace(
            CreateDocument(CreateEndpoint("POST", "/stale", "body")),
            expectedRevision: 0);

        Assert.Equal(ConfigurationUpdateStatus.RevisionConflict, result.Status);
        Assert.Same(current, state.Current);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/current", out _));
        Assert.False(state.Current.Endpoints.TryGet("POST", "/stale", out _));
    }

    [Fact]
    public void MarkPersisted_ClearsDirtyStateOnlyForCurrentRevision()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(CreateEndpoint("GET", "/active", "body")), 0).Status);

        Assert.False(state.TryMarkPersisted(expectedRevision: 0));
        Assert.True(state.Current.HasUnsavedChanges);
        Assert.True(state.TryMarkPersisted(expectedRevision: 1));
        Assert.False(state.Current.HasUnsavedChanges);
        Assert.Equal(1, state.Current.Revision);
        Assert.Equal("\"1\"", state.Current.ETag);
    }

    [Fact]
    public void PublishedState_DoesNotReferenceMutableCandidateCollections()
    {
        var methods = new[] { "GET" };
        var headerValues = new[] { "original" };
        var endpoint = CreateEndpoint("GET", "/immutable", "body") with
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
        var state = new ConfigurationState();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(endpoint), 0).Status);

        methods[0] = "POST";
        headerValues[0] = "changed";
        var exported = state.Current.GetDocument();
        exported.Endpoints[0].Response.Headers["X-Value"][0] = "also changed";

        Assert.True(state.Current.Endpoints.TryGet("GET", "/immutable", out var runtimeEndpoint));
        Assert.False(state.Current.Endpoints.TryGet("POST", "/immutable", out _));
        Assert.Equal("original", Assert.Single(Assert.Single(runtimeEndpoint.Headers).Values));
        Assert.Equal("original", state.Current.GetDocument().Endpoints[0].Response.Headers["X-Value"][0]);
    }

    [Fact]
    public void Export_IsDeterministicAndValidatesAgainstSchema()
    {
        var state = new ConfigurationState();
        var endpoint = CreateEndpoint("GET", "/export", "body") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 200,
                Headers = new()
                {
                    ["Z-Last"] = ["z"],
                    ["A-First"] = ["a"]
                },
                ContentType = "text/plain",
                Body = "body"
            }
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(endpoint), 0).Status);

        var first = state.Current.ExportUtf8();
        var second = state.Current.ExportUtf8();

        Assert.Equal(first, second);
        using var json = JsonDocument.Parse(first);
        var evaluation = ConfigurationSchemaFixture.Evaluate(json.RootElement);
        Assert.True(evaluation.IsValid, JsonSerializer.Serialize(evaluation));
        var text = JsonSerializer.Serialize(json.RootElement);
        Assert.True(text.IndexOf("A-First", StringComparison.Ordinal) < text.IndexOf("Z-Last", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentWritersWithSameRevision_HaveSingleWinner()
    {
        var state = new ConfigurationState();
        var results = new ConfigurationUpdateResult[100];

        Parallel.For(0, results.Length, index =>
        {
            results[index] = state.TryReplace(
                CreateDocument(CreateEndpoint("GET", $"/endpoint-{index}", index.ToString())),
                expectedRevision: 0);
        });

        Assert.Single(results, result => result.Status == ConfigurationUpdateStatus.Applied);
        Assert.Equal(99, results.Count(result => result.Status == ConfigurationUpdateStatus.RevisionConflict));
        Assert.Equal(1, state.Current.Revision);
        var document = state.Current.GetDocument();
        var endpoint = Assert.Single(document.Endpoints);
        Assert.True(state.Current.Endpoints.TryGet("GET", endpoint.Path, out _));
    }

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
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
