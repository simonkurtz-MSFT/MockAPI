using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class RequestStatisticsCollectorTests
{
    [Fact]
    public void Snapshot_DistinguishesOutcomesAndTracksEndpointByStableId()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var collector = new RequestStatisticsCollector(timeProvider);
        var endpointId = Guid.NewGuid();

        collector.RecordUnmatched();
        collector.RecordMatched(endpointId, 201, 12);
        collector.RecordFailedWrite(endpointId, 503);

        var snapshot = collector.GetSnapshot();

        Assert.Equal(3, snapshot.TotalRequests);
        Assert.Equal(2, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
        Assert.Equal(1, snapshot.FailedWrites);
        Assert.Equal(1, snapshot.SuccessResponses);
        Assert.Equal(1, snapshot.ServerErrorResponses);
        Assert.Equal(12, snapshot.ResponseBytes);
        Assert.Equal(3, snapshot.RecentMinutes[^1].Requests);
        var endpoint = Assert.Single(snapshot.Endpoints);
        Assert.Equal(endpointId, endpoint.EndpointId);
        Assert.Equal(2, endpoint.TotalRequests);
        Assert.Equal(503, endpoint.LastStatusCode);
        Assert.Equal(12, endpoint.ResponseBytes);
        Assert.Equal(timeProvider.GetUtcNow(), endpoint.LastRequestUtc);
    }

    [Fact]
    public void Snapshot_RetainsExactlySixtyOneMinuteBuckets()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var collector = new RequestStatisticsCollector(timeProvider);

        collector.RecordUnmatched();
        timeProvider.Advance(TimeSpan.FromMinutes(59));
        collector.RecordUnmatched();

        var beforeRollover = collector.GetSnapshot();
        Assert.Equal(60, beforeRollover.RecentMinutes.Length);
        Assert.Equal(2, beforeRollover.RecentMinutes.Sum(bucket => bucket.Requests));

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        collector.RecordUnmatched();

        var afterRollover = collector.GetSnapshot();
        Assert.Equal(60, afterRollover.RecentMinutes.Length);
        Assert.Equal(2, afterRollover.RecentMinutes.Sum(bucket => bucket.Requests));
        Assert.Equal(0, afterRollover.RecentMinutes[0].Requests);
        Assert.Equal(1, afterRollover.RecentMinutes[^1].Requests);
    }

    [Fact]
    public void RecordMatched_IsConcurrencySafe()
    {
        var collector = new RequestStatisticsCollector();
        var endpointId = Guid.NewGuid();

        Parallel.For(0, 10_000, _ => collector.RecordMatched(endpointId, 200, 5));

        var snapshot = collector.GetSnapshot();
        Assert.Equal(10_000, snapshot.TotalRequests);
        Assert.Equal(10_000, snapshot.MatchedRequests);
        Assert.Equal(50_000, snapshot.ResponseBytes);
        Assert.Equal(10_000, snapshot.RecentMinutes[^1].Requests);
        Assert.Equal(10_000, Assert.Single(snapshot.Endpoints).TotalRequests);
    }

    [Fact]
    public void EndpointTracking_IsBoundedWithoutDroppingAggregateCounts()
    {
        var collector = new RequestStatisticsCollector();

        for (var index = 0; index < RequestStatisticsCollector.MaximumTrackedEndpoints + 10; index++)
        {
            collector.RecordMatched(Guid.NewGuid(), 200, 1);
        }

        var snapshot = collector.GetSnapshot();
        Assert.Equal(RequestStatisticsCollector.MaximumTrackedEndpoints + 10, snapshot.TotalRequests);
        Assert.Equal(RequestStatisticsCollector.MaximumTrackedEndpoints, snapshot.Endpoints.Length);
    }

    [Fact]
    public void Snapshot_TracksEveryResponseStatusClass()
    {
        var collector = new RequestStatisticsCollector();
        var endpointId = Guid.NewGuid();

        collector.RecordMatched(endpointId, 101, 0);
        collector.RecordMatched(endpointId, 204, 0);
        collector.RecordMatched(endpointId, 302, 0);
        collector.RecordMatched(endpointId, 404, 0);
        collector.RecordMatched(endpointId, 500, 0);
        collector.RecordMatched(endpointId, 99, 0);

        var snapshot = collector.GetSnapshot();
        Assert.Equal(1, snapshot.InformationalResponses);
        Assert.Equal(1, snapshot.SuccessResponses);
        Assert.Equal(1, snapshot.RedirectionResponses);
        Assert.Equal(1, snapshot.ClientErrorResponses);
        Assert.Equal(1, snapshot.ServerErrorResponses);
        Assert.Equal(6, Assert.Single(snapshot.Endpoints).TotalRequests);
    }

    [Theory]
    [InlineData(100, 1, 0, 0, 0, 0)]
    [InlineData(199, 1, 0, 0, 0, 0)]
    [InlineData(200, 0, 1, 0, 0, 0)]
    [InlineData(299, 0, 1, 0, 0, 0)]
    [InlineData(300, 0, 0, 1, 0, 0)]
    [InlineData(399, 0, 0, 1, 0, 0)]
    [InlineData(400, 0, 0, 0, 1, 0)]
    [InlineData(499, 0, 0, 0, 1, 0)]
    [InlineData(500, 0, 0, 0, 0, 1)]
    [InlineData(599, 0, 0, 0, 0, 1)]
    [InlineData(99, 0, 0, 0, 0, 0)]
    [InlineData(600, 0, 0, 0, 0, 0)]
    public void Snapshot_ClassifiesStatusBoundaries(
        int statusCode,
        long informational,
        long success,
        long redirection,
        long clientError,
        long serverError)
    {
        var collector = new RequestStatisticsCollector();
        collector.RecordMatched(Guid.NewGuid(), statusCode, 0);

        var snapshot = collector.GetSnapshot();

        Assert.Equal(informational, snapshot.InformationalResponses);
        Assert.Equal(success, snapshot.SuccessResponses);
        Assert.Equal(redirection, snapshot.RedirectionResponses);
        Assert.Equal(clientError, snapshot.ClientErrorResponses);
        Assert.Equal(serverError, snapshot.ServerErrorResponses);
    }

    [Fact]
    public void Snapshot_MinimumRequestTimeIsPreserved()
    {
        var collector = new RequestStatisticsCollector(new ManualTimeProvider(DateTimeOffset.MinValue));
        collector.RecordMatched(Guid.NewGuid(), 200, 0);

        Assert.Equal(DateTimeOffset.MinValue, Assert.Single(collector.GetSnapshot().Endpoints).LastRequestUtc);
    }

    [Fact]
    public void RecordMatched_RejectsNegativeResponseBytes()
    {
        var collector = new RequestStatisticsCollector();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            collector.RecordMatched(Guid.NewGuid(), 200, -1));
    }

    [Fact]
    public void Reset_ReturnsWhetherEndpointStatisticsExisted()
    {
        var collector = new RequestStatisticsCollector();
        var endpointId = Guid.NewGuid();
        collector.RecordMatched(endpointId, 200, 0);

        Assert.True(collector.Reset(endpointId));
        Assert.False(collector.Reset(endpointId));
    }

    [Fact]
    public async Task RecordMatched_WhenEndpointIsAddedBeforeLockUsesExistingCounter()
    {
        var firstReachedLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var endpointId = Guid.NewGuid();
        var collector = new RequestStatisticsCollector(TimeProvider.System, () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstReachedLock.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        });
        var firstTask = Task.Run(() => collector.RecordMatched(endpointId, 200, 1));
        await firstReachedLock.Task;
        collector.RecordMatched(endpointId, 201, 2);
        releaseFirst.SetResult();
        await firstTask;

        var endpoint = Assert.Single(collector.GetSnapshot().Endpoints);
        Assert.Equal(2, endpoint.TotalRequests);
        Assert.Equal(3, endpoint.ResponseBytes);
    }

    [Theory]
    [InlineData(10, 10, 0U, 1U)]
    [InlineData(10, 10, 1U, 2U)]
    [InlineData(10, 10, uint.MaxValue, uint.MaxValue)]
    [InlineData(9, 10, uint.MaxValue, 1U)]
    public void MinuteBucket_NextCountHandlesIncrementSaturationAndMinuteReset(
        int recordedMinute,
        int minute,
        uint count,
        uint expected)
    {
        Assert.Equal(expected, RequestStatisticsCollector.GetNextBucketCount(recordedMinute, minute, count));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

}
