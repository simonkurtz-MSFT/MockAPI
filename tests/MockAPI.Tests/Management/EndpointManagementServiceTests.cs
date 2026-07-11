using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class EndpointManagementServiceTests
{
    [Fact]
    public void Operations_ReturnExpectedStatusesAndPreserveEndpointOrder()
    {
        var state = new ConfigurationState();
        var service = new EndpointManagementService(state);
        var first = CreateEndpoint("/first");
        var second = CreateEndpoint("/second");

        Assert.Equal(ManagementOperationStatus.Applied, service.Create(first, 0).Status);
        Assert.Equal(ManagementOperationStatus.Applied, service.Create(second, 1).Status);
        Assert.Equal(ManagementOperationStatus.AlreadyExists, service.Create(second, 2).Status);
        Assert.Equal(
            ManagementOperationStatus.IdMismatch,
            service.Replace(second.Id, second with { Id = Guid.NewGuid() }, 2).Status);
        var missingId = Guid.NewGuid();
        Assert.Equal(
            ManagementOperationStatus.NotFound,
            service.Replace(missingId, CreateEndpoint("/missing") with { Id = missingId }, 2).Status);

        var beforeInvalid = state.Current;
        var invalid = CreateEndpoint("/health");
        var invalidResult = service.Create(invalid, 2);
        Assert.Equal(ManagementOperationStatus.ValidationFailed, invalidResult.Status);
        Assert.NotEmpty(invalidResult.Validation!.Errors);
        Assert.Same(beforeInvalid, state.Current);

        var replacement = second with { Path = "/second-replaced", Name = "second-replaced" };
        Assert.Equal(ManagementOperationStatus.Applied, service.Replace(second.Id, replacement, 2).Status);
        Assert.Equal(ManagementOperationStatus.Applied, service.SetEnabled(second.Id, false, 3).Status);
        Assert.Equal(ManagementOperationStatus.Applied, service.Delete(first.Id, 4).Status);

        var remaining = Assert.Single(state.Current.GetDocument().Endpoints);
        Assert.Equal(second.Id, remaining.Id);
        Assert.Equal("/second-replaced", remaining.Path);
        Assert.False(remaining.Enabled);
        Assert.Equal(5, state.Current.Revision);
        Assert.True(state.Current.HasUnsavedChanges);
    }

    [Fact]
    public void ConcurrentCreatesWithSameRevision_HaveExactlyOneWinner()
    {
        var state = new ConfigurationState();
        var service = new EndpointManagementService(state);
        var results = new ManagementOperationResult[100];

        Parallel.For(0, results.Length, index =>
        {
            results[index] = service.Create(CreateEndpoint($"/endpoint-{index}"), expectedRevision: 0);
        });

        Assert.Single(results, result => result.Status == ManagementOperationStatus.Applied);
        Assert.Equal(99, results.Count(result => result.Status == ManagementOperationStatus.RevisionConflict));
        Assert.Equal(1, state.Current.Revision);
        var endpoint = Assert.Single(state.Current.GetDocument().Endpoints);
        Assert.True(state.Current.Endpoints.TryGet("GET", endpoint.Path, out _));
    }

    private static MockEndpointDefinition CreateEndpoint(string path) => new()
    {
        Id = Guid.NewGuid(),
        Name = path.TrimStart('/'),
        Enabled = true,
        Methods = ["GET"],
        Path = path,
        Response = new MockResponseDefinition
        {
            StatusCode = 200,
            Headers = [],
            ContentType = "text/plain; charset=utf-8",
            Body = path
        }
    };
}
