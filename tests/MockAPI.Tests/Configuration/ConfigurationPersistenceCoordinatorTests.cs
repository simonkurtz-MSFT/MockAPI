using MockAPI.Configuration;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationPersistenceCoordinatorTests
{
    private static readonly Func<CancellationToken, ValueTask> NoOp =
        _ => ValueTask.CompletedTask;

    [Fact]
    public void Constructor_RejectsNullHooks()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationPersistenceCoordinator(null!, NoOp));
        Assert.Throws<ArgumentNullException>(() =>
            new ConfigurationPersistenceCoordinator(NoOp, null!));
    }

    [Fact]
    public void CaptureLoadRevision_ReturnsCurrentRevisionAndRejectsNullState()
    {
        var coordinator = new ConfigurationPersistenceCoordinator(NoOp, NoOp);
        var state = CreateDirtyState();

        Assert.Equal(1, coordinator.CaptureLoadRevision(state));
        Assert.Throws<ArgumentNullException>(() => coordinator.CaptureLoadRevision(null!));
    }

    [Fact]
    public async Task ApplyLoadedAsync_PublishesCleanStateAndRejectsNullArguments()
    {
        var loaded = false;
        var coordinator = new ConfigurationPersistenceCoordinator(
            NoOp,
            _ =>
            {
                loaded = true;
                return ValueTask.CompletedTask;
            });
        var state = new ConfigurationState();
        var document = CreateDocument("/loaded");

        await coordinator.ApplyLoadedAsync(
            state,
            document,
            expectedRevision: 0,
            "validation failed",
            "revision conflict",
            CancellationToken.None);

        Assert.True(loaded);
        Assert.Equal(1, state.Current.Revision);
        Assert.False(state.Current.HasUnsavedChanges);
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.ApplyLoadedAsync(
            null!, document, 0, "validation failed", "revision conflict", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.ApplyLoadedAsync(
            state, null!, 1, "validation failed", "revision conflict", CancellationToken.None));
    }

    [Fact]
    public async Task SaveAsync_PersistsCapturedSnapshotAndValidatesArguments()
    {
        var captured = false;
        var coordinator = new ConfigurationPersistenceCoordinator(
            _ =>
            {
                captured = true;
                return ValueTask.CompletedTask;
            },
            NoOp);
        var state = CreateDirtyState();
        ConfigurationStateSnapshot? persisted = null;

        var result = await coordinator.SaveAsync(
            state,
            expectedRevision: 1,
            (snapshot, _) =>
            {
                persisted = snapshot;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(captured);
        Assert.NotNull(persisted);
        Assert.NotSame(persisted, state.Current);
        Assert.Equal(persisted.Revision, state.Current.Revision);
        Assert.Equal(persisted.ExportUtf8(), state.Current.ExportUtf8());
        Assert.Equal(1, result.Revision);
        Assert.True(result.IsCurrentRevision);
        Assert.False(state.Current.HasUnsavedChanges);
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.SaveAsync(
            null!, 1, (_, _) => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => coordinator.SaveAsync(
            state, -1, (_, _) => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => coordinator.SaveAsync(
            state, 1, null!, CancellationToken.None));
    }

    private static ConfigurationState CreateDirtyState()
    {
        var state = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            state.TryReplace(CreateDocument("/saved"), expectedRevision: 0).Status);
        return state;
    }

    private static MockApiConfigurationDocument CreateDocument(string path) => new()
    {
        Schema = "../schemas/mockapi.schema.json",
        SchemaVersion = "1.0",
        Endpoints =
        [
            new MockEndpointDefinition
            {
                Id = Guid.NewGuid(),
                Name = path,
                Enabled = true,
                Methods = ["GET"],
                Path = path,
                Response = new MockResponseDefinition
                {
                    StatusCode = 200,
                    Headers = [],
                    ContentType = "text/plain; charset=utf-8",
                    Body = "body"
                }
            }
        ]
    };
}
