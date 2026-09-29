using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationManagementServiceTests
{
    [Fact]
    public void ApiDescriptions_MergeMissingDefaultsButPreserveCustomAndEmptyValuesEvenWhenForced()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var endpoint = CreateEndpoint(Guid.NewGuid(), "/ex/hello", "body");
        var active = CreateDocument(endpoint) with
        {
            ApiDescriptions = new() { ["/custom"] = "Custom API", ["/empty"] = "" }
        };
        state.TryReplace(active, 0);
        var builtIn = CreateDocument(endpoint) with
        {
            ApiDescriptions = new() { ["/ex"] = "Example", ["/custom"] = "Default", ["/empty"] = "Default" }
        };

        var merged = service.MergeBuiltIn(builtIn, 1, force: false);
        Assert.Equal(BuiltInMergeStatus.Applied, merged.Status);
        Assert.Equal(0, merged.Added);
        Assert.Equal("Example", state.Current.GetDocument().ApiDescriptions!["/ex"]);
        Assert.Equal("Custom API", state.Current.GetDocument().ApiDescriptions!["/custom"]);
        Assert.Equal("", state.Current.GetDocument().ApiDescriptions!["/empty"]);
        Assert.Equal(BuiltInMergeStatus.NoChanges, service.MergeBuiltIn(builtIn, 2, force: true).Status);

        service.SetApiDescription("/ex", "Customized", 2);
        var divergent = builtIn with { Endpoints = [endpoint with { Name = "Changed example" }] };
        Assert.Equal(BuiltInMergeStatus.Conflict, service.MergeBuiltIn(divergent, 3, force: false).Status);
        Assert.Equal(BuiltInMergeStatus.Applied, service.MergeBuiltIn(divergent, 3, force: true).Status);
        Assert.Equal("Customized", state.Current.GetDocument().ApiDescriptions!["/ex"]);
    }

    [Fact]
    public void ApiDescriptions_ValidationAndStaleWritesLeaveWinningSnapshotIntact()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        Assert.Equal(ConfigurationUpdateStatus.Applied, service.SetApiDescription("/ex", "Winner", 0).Status);
        var winner = state.Current;

        Assert.Equal(ConfigurationUpdateStatus.RevisionConflict, service.SetApiDescription("/ex", "Stale", 0).Status);
        foreach (var path in new[] { "/health", "/HEALTH", "/__mockapi", "/a/b", "", "/a\n", "/" + new string('a', 2048) })
        {
            Assert.Equal(ConfigurationUpdateStatus.ValidationFailed, service.SetApiDescription(path, "Invalid", 1).Status);
        }
        Assert.Equal(ConfigurationUpdateStatus.ValidationFailed,
            service.SetApiDescription("/ex", new string('x', 4001), 1).Status);
        Assert.Same(winner, state.Current);
    }

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
    public void MergeBuiltIn_TreatsDescriptionChangesAsConflicts()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var active = CreateEndpoint(Guid.NewGuid(), "/description", "body") with
        {
            Description = "Active description"
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(active), 0).Status);
        var builtIn = active with { Description = "Built-in description" };

        var result = service.MergeBuiltIn(CreateDocument(builtIn), expectedRevision: 1, force: false);

        Assert.Equal(BuiltInMergeStatus.Conflict, result.Status);
        Assert.Contains(result.Conflicts, conflict => conflict.Kind == "different");
    }

    [Fact]
    public void MergeBuiltIn_TreatsHeaderDictionaryOrderAsEquivalent()
    {
        var state = new ConfigurationState();
        var service = CreateService(state);
        var endpointId = Guid.NewGuid();
        var active = CreateEndpoint(endpointId, "/ordered-headers", "body") with
        {
            Response = CreateEndpoint(endpointId, "/ordered-headers", "body").Response with
            {
                Headers = new Dictionary<string, string[]>
                {
                    ["X-First"] = ["one"],
                    ["X-Second"] = ["two"]
                }
            }
        };
        var builtIn = active with
        {
            Response = active.Response with
            {
                Headers = new Dictionary<string, string[]>
                {
                    ["X-Second"] = ["two"],
                    ["X-First"] = ["one"]
                }
            }
        };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(active), 0).Status);

        var result = service.MergeBuiltIn(CreateDocument(builtIn), expectedRevision: 1, force: false);

        Assert.Equal(BuiltInMergeStatus.NoChanges, result.Status);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, state.Current.Revision);
    }

    [Fact]
    public void MergeBuiltIn_DetectsEveryHeaderDifference()
    {
        var cases = new[]
        {
            (Active: new Dictionary<string, string[]>(), BuiltIn: new Dictionary<string, string[]> { ["X-Added"] = ["value"] }),
            (Active: new Dictionary<string, string[]> { ["X-Active"] = ["value"] }, BuiltIn: new Dictionary<string, string[]> { ["X-Built-In"] = ["value"] }),
            (Active: new Dictionary<string, string[]> { ["X-Value"] = ["active"] }, BuiltIn: new Dictionary<string, string[]> { ["X-Value"] = ["built-in"] })
        };

        foreach (var testCase in cases)
        {
            var state = new ConfigurationState();
            var service = CreateService(state);
            var endpointId = Guid.NewGuid();
            var active = CreateEndpoint(endpointId, "/headers", "body") with
            {
                Response = CreateEndpoint(endpointId, "/headers", "body").Response with { Headers = testCase.Active }
            };
            var builtIn = active with { Response = active.Response with { Headers = testCase.BuiltIn } };
            Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(CreateDocument(active), 0).Status);

            var result = service.MergeBuiltIn(CreateDocument(builtIn), expectedRevision: 1, force: false);

            Assert.Equal(BuiltInMergeStatus.Conflict, result.Status);
            Assert.Contains(result.Conflicts, conflict => conflict.Kind == "different");
        }
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
