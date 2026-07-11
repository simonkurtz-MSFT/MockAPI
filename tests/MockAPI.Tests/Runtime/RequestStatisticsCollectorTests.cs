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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
