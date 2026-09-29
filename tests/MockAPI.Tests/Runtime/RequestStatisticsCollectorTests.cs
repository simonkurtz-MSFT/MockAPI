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
        collector.RecordMatched(endpointId, 302, 0);
        collector.RecordFailedWrite(endpointId, 503);

        var snapshot = collector.GetSnapshot();

        Assert.Equal(4, snapshot.TotalRequests);
        Assert.Equal(3, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
        Assert.Equal(1, snapshot.FailedWrites);
        Assert.Equal(1, snapshot.SuccessResponses);
        Assert.Equal(1, snapshot.RedirectionResponses);
        Assert.Equal(1, snapshot.ServerErrorResponses);
        Assert.Equal(12, snapshot.ResponseBytes);
        var currentMinute = snapshot.RecentMinutes[^1];
        Assert.Equal(4, currentMinute.Requests);
        Assert.Equal(1, currentMinute.SuccessResponses);
        Assert.Equal(1, currentMinute.RedirectionResponses);
        Assert.Equal(1, currentMinute.ClientErrorResponses);
        Assert.Equal(1, currentMinute.ServerErrorResponses);
        var endpoint = Assert.Single(snapshot.Endpoints);
        Assert.Equal(endpointId, endpoint.EndpointId);
        Assert.Equal(3, endpoint.TotalRequests);
        Assert.Equal(503, endpoint.LastStatusCode);
        Assert.Equal(12, endpoint.ResponseBytes);
        Assert.Equal(timeProvider.GetUtcNow(), endpoint.LastRequestUtc);
        Assert.Equal(3, endpoint.RecentMinutes[^1].Requests);
        Assert.Equal(1, endpoint.RecentMinutes[^1].SuccessResponses);
        Assert.Equal(1, endpoint.RecentMinutes[^1].RedirectionResponses);
        Assert.Equal(1, endpoint.RecentMinutes[^1].ServerErrorResponses);
    }

    [Fact]
    public void RecentRequests_AreBoundedNewestFirstAndClearedByReset()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var collector = new RequestStatisticsCollector(timeProvider);
        var endpointId = Guid.NewGuid();

        collector.RecordUnmatched("GET", "/missing");
        for (var index = 0; index <= RequestStatisticsCollector.MaximumRecentRequests; index++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            collector.RecordMatched(endpointId, "POST", $"/items/{index}", 201, index, $"request-{index}");
        }

        var recentRequests = collector.GetSnapshot().RecentRequests;

        Assert.Equal(RequestStatisticsCollector.MaximumRecentRequests, recentRequests.Length);
        Assert.Equal("/items/100", recentRequests[0].Path);
        Assert.Equal("/items/1", recentRequests[^1].Path);
        Assert.DoesNotContain(recentRequests, request => request.Path == "/missing");
        Assert.All(recentRequests, request =>
        {
            Assert.Equal(endpointId, request.EndpointId);
            Assert.Equal("POST", request.Method);
            Assert.Equal(RequestLogOutcome.Response, request.Outcome);
            Assert.Equal(201, request.StatusCode);
        });

        Assert.True(collector.Reset(endpointId));
        Assert.Empty(collector.GetSnapshot().RecentRequests);

        collector.RecordUnmatched("GET", "/missing");
        collector.Reset();
        Assert.Empty(collector.GetSnapshot().RecentRequests);
    }

    [Fact]
    public void RecentRequests_CoalesceTransportRetriesAsOneLogicalRequest()
    {
        var collector = new RequestStatisticsCollector();
        var endpointId = Guid.NewGuid();

        collector.RecordAbortedConnection(endpointId, "GET", "/abort", "logical-request");
        collector.RecordAbortedConnection(endpointId, "GET", "/abort", "logical-request");

        var snapshot = collector.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.AbortedConnections);
        Assert.Equal(1, snapshot.RecentMinutes[^1].Requests);
        Assert.Equal(1, Assert.Single(snapshot.Endpoints).TotalRequests);
        var request = Assert.Single(snapshot.RecentRequests);
        Assert.Equal(2, request.TransportAttempts);
    }

    [Fact]
    public void RecordUnmatched_CoalescesTransportRetriesWithoutAddingARecentRequest()
    {
        var collector = new RequestStatisticsCollector();

        collector.RecordUnmatched("GET", "/missing", "logical-request");
        collector.RecordUnmatched("GET", "/missing", "logical-request");

        var snapshot = collector.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
        Assert.Equal(1, snapshot.RecentMinutes[^1].Requests);
        Assert.Empty(snapshot.RecentRequests);
    }

    [Fact]
    public void RecordMatched_DoesNotReclassifyAnUnmatchedLogicalRequest()
    {
        var collector = new RequestStatisticsCollector();
        var endpointId = Guid.NewGuid();

        collector.RecordUnmatched("GET", "/missing", "logical-request");
        collector.RecordMatched(endpointId, "GET", "/matched", 200, 1, "logical-request");

        var snapshot = collector.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
        Assert.Equal(0, snapshot.MatchedRequests);
        Assert.Empty(snapshot.Endpoints);
        Assert.Empty(snapshot.RecentRequests);
    }

    [Fact]
    public void RecordUnmatched_EvictsTheOldestLogicalRequestIdAtCapacity()
    {
        var collector = new RequestStatisticsCollector();

        for (var index = 0; index < RequestStatisticsCollector.MaximumRecentRequests; index++)
        {
            collector.RecordUnmatched("GET", "/missing", $"request-{index}");
        }

        collector.RecordUnmatched("GET", "/missing", "request-at-capacity");
        collector.RecordUnmatched("GET", "/missing", "request-0");

        var snapshot = collector.GetSnapshot();
        Assert.Equal(RequestStatisticsCollector.MaximumRecentRequests + 2, snapshot.TotalRequests);
        Assert.Equal(snapshot.TotalRequests, snapshot.UnmatchedRequests);
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
        var timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var collector = new RequestStatisticsCollector(timeProvider);
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

        collector.RecordAbortedConnection(Guid.NewGuid());

        snapshot = collector.GetSnapshot();
        Assert.Equal(RequestStatisticsCollector.MaximumTrackedEndpoints + 11, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.AbortedConnections);
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
        Assert.Equal(1, snapshot.RecentMinutes[^1].InformationalResponses);
        Assert.Equal(1, Assert.Single(snapshot.Endpoints).RecentMinutes[^1].InformationalResponses);
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
        var retainedEndpointId = Guid.NewGuid();
        collector.RecordMatched(endpointId, "GET", "/removed", 200, 0, "removed-request");
        collector.RecordMatched(retainedEndpointId, "GET", "/retained", 200, 0, "retained-request");
        collector.RecordUnmatched("GET", "/unmatched", "unmatched-request");

        Assert.True(collector.Reset(endpointId));
        Assert.False(collector.Reset(endpointId));

        collector.RecordMatched(retainedEndpointId, "GET", "/retained", 200, 0, "retained-request");
        var retainedRequests = collector.GetSnapshot().RecentRequests;
        var retainedRequest = Assert.Single(retainedRequests);
        Assert.Equal(retainedEndpointId, retainedRequest.EndpointId);
        Assert.Equal(2, retainedRequest.TransportAttempts);
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

    [Theory]
    [InlineData(1, 2)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void TransportAttempts_NextCountSaturates(int count, int expected)
    {
        Assert.Equal(expected, RequestStatisticsCollector.GetNextTransportAttemptCount(count));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }

}
