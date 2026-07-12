using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationManagementServiceTests
{
    [Fact]
    public void MergeBuiltIn_ReportsRevisionValidationAndNoChangeResults()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var endpoint = CreateEndpoint(Guid.NewGuid(), "/example", "body");
        var document = CreateDocument(endpoint);

        var stale = service.MergeBuiltIn(document, expectedRevision: 1, force: false);
        var invalid = service.MergeBuiltIn(
            CreateDocument(CreateEndpoint(Guid.NewGuid(), "/health", "invalid")),
            expectedRevision: 0,
            force: false);
        var applied = service.MergeBuiltIn(document, expectedRevision: 0, force: false);
        var unchanged = service.MergeBuiltIn(document, expectedRevision: 1, force: false);

        Assert.Equal(BuiltInMergeStatus.RevisionConflict, stale.Status);
        Assert.Equal(BuiltInMergeStatus.ValidationFailed, invalid.Status);
        Assert.NotEmpty(invalid.Validation.Errors);
        Assert.Equal(BuiltInMergeStatus.Applied, applied.Status);
        Assert.Equal(1, applied.Added);
        Assert.Equal(BuiltInMergeStatus.NoChanges, unchanged.Status);
        Assert.Equal(1, unchanged.Skipped);
    }

    [Fact]
    public void MergeBuiltIn_ReportsAndForcesSameIdAndRouteCollisions()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var sharedId = Guid.NewGuid();
        var activeById = CreateEndpoint(sharedId, "/local", "local");
        var activeByRoute = CreateEndpoint(Guid.NewGuid(), "/collision", "collision");
        var unrelated = CreateEndpoint(Guid.NewGuid(), "/unrelated", "unrelated");
        var identical = CreateEndpoint(Guid.NewGuid(), "/identical", "identical");
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument(activeById, activeByRoute, unrelated, identical), 0).Status);
        var builtInById = CreateEndpoint(sharedId, "/built-in", "built-in");
        var builtInByRoute = CreateEndpoint(Guid.NewGuid(), "/collision", "replacement");
        var addition = CreateEndpoint(Guid.NewGuid(), "/addition", "addition");
        var builtIn = CreateDocument(builtInById, builtInByRoute, identical, addition);

        var conflict = service.MergeBuiltIn(builtIn, expectedRevision: 1, force: false);
        var forced = service.MergeBuiltIn(builtIn, expectedRevision: 1, force: true);

        Assert.Equal(BuiltInMergeStatus.Conflict, conflict.Status);
        Assert.Contains(conflict.Conflicts, item => item.Kind == "different");
        Assert.Contains(conflict.Conflicts, item => item.Kind == "routeCollision");
        Assert.Equal(BuiltInMergeStatus.Applied, forced.Status);
        Assert.Equal(2, forced.Updated);
        Assert.Equal(1, forced.Added);
        Assert.Equal(1, forced.Skipped);
        Assert.Equal(5, state.Current.GetDocument().Endpoints.Count);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/built-in", out _));
        Assert.True(state.Current.Endpoints.TryGet("GET", "/collision", out var collision));
        Assert.Equal("replacement", System.Text.Encoding.UTF8.GetString(collision.Body.Span));
        Assert.True(state.Current.Endpoints.TryGet("GET", "/unrelated", out _));
        Assert.True(state.Current.Endpoints.TryGet("GET", "/identical", out _));
        Assert.True(state.Current.Endpoints.TryGet("GET", "/addition", out _));
    }

    [Fact]
    public void MergeBuiltIn_NormalMergeRetainsUnrelatedEndpointsAndAddsMissingOnes()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var unrelated = CreateEndpoint(Guid.NewGuid(), "/unrelated", "unrelated");
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(unrelated), 0).Status);
        var addition = CreateEndpoint(Guid.NewGuid(), "/addition", "addition");

        var result = service.MergeBuiltIn(CreateDocument(addition), 1, force: false);

        Assert.Equal(BuiltInMergeStatus.Applied, result.Status);
        Assert.Equal(1, result.Added);
        Assert.True(state.Current.Endpoints.TryGet("GET", "/unrelated", out _));
        Assert.True(state.Current.Endpoints.TryGet("GET", "/addition", out _));
    }

    [Fact]
    public void ShouldRetain_CoversEveryForceIdAndRouteCombination()
    {
        var active = CreateEndpoint(Guid.NewGuid(), "/active", "active");
        var collision = CreateEndpoint(Guid.NewGuid(), "/active", "collision");
        var unrelated = CreateEndpoint(Guid.NewGuid(), "/unrelated", "unrelated");

        Assert.False(ConfigurationManagementService.ShouldRetain(
            active,
            new HashSet<Guid> { active.Id },
            [unrelated],
            force: false));
        Assert.True(ConfigurationManagementService.ShouldRetain(
            active,
            new HashSet<Guid>(),
            [collision],
            force: false));
        Assert.True(ConfigurationManagementService.ShouldRetain(
            active,
            new HashSet<Guid>(),
            [unrelated],
            force: true));
        Assert.False(ConfigurationManagementService.ShouldRetain(
            active,
            new HashSet<Guid>(),
            [collision],
            force: true));
    }

    [Fact]
    public void BuiltInMergeResult_FactoriesPreserveTheirContracts()
    {
        var state = new ConfigurationState();
        var validation = new ConfigurationValidationResult(
            [new ConfigurationValidationError("path", "code", "message")]);
        var conflict = new BuiltInMergeConflict(Guid.NewGuid(), "built-in", "different", Guid.NewGuid(), "active");

        var applied = BuiltInMergeResult.Applied(state.Current, 1, 2, 3, [conflict]);
        var unchanged = BuiltInMergeResult.NoChanges(state.Current, 4);
        var conflicted = BuiltInMergeResult.Conflict(state.Current, 5, [conflict]);
        var invalid = BuiltInMergeResult.ValidationFailed(validation);
        var stale = BuiltInMergeResult.RevisionConflict();

        Assert.Equal((BuiltInMergeStatus.Applied, 1, 2, 3), (applied.Status, applied.Added, applied.Updated, applied.Skipped));
        Assert.Equal(BuiltInMergeStatus.NoChanges, unchanged.Status);
        Assert.Equal(4, unchanged.Skipped);
        Assert.Equal(BuiltInMergeStatus.Conflict, conflicted.Status);
        Assert.Equal(5, conflicted.Skipped);
        Assert.Same(validation, invalid.Validation);
        Assert.Equal(BuiltInMergeStatus.RevisionConflict, stale.Status);
    }

    [Fact]
    public async Task MergeBuiltIn_WhenStateChangesDuringApplyReturnsRevisionConflict()
    {
        var firstReachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var state = new ConfigurationState(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstReachedLock.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        });
        var service = CreateService(state);
        var mergeTask = Task.Run(() => service.MergeBuiltIn(
            CreateDocument(CreateEndpoint(Guid.NewGuid(), "/merged", "merged")),
            0,
            force: false));
        await firstReachedLock.Task;
        var winner = state.TryReplace(CreateDocument(CreateEndpoint(Guid.NewGuid(), "/winner", "winner")), 0);
        releaseFirst.SetResult();

        var merge = await mergeTask;

        Assert.Equal(ConfigurationUpdateStatus.Applied, winner.Status);
        Assert.Equal(BuiltInMergeStatus.RevisionConflict, merge.Status);
    }

    private static ConfigurationManagementService CreateService(ConfigurationState state)
    {
        var options = new MockApiOptions
        {
            ConfigurationPath = Path.Combine(Path.GetTempPath(), "MockAPI.Tests", Guid.NewGuid().ToString("N"), "mockapi.json")
        };
        return new ConfigurationManagementService(state, new ConfigurationFileStore(options));
    }

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static MockEndpointDefinition CreateEndpoint(Guid id, string path, string body) => new()
    {
        Id = id,
        Name = path.Trim('/'),
        Enabled = true,
        Methods = ["GET"],
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
