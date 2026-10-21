using System.Text;
using System.Text.Json;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

namespace MockAPI.Tests.Management;

public sealed class StatisticsDeliveryTests
{
    internal static readonly ManagementJsonContext CompactJson = new(
        new JsonSerializerOptions(ManagementJsonContext.Default.Options) { WriteIndented = false });

    [Fact]
    public async Task StatisticsGet_PreservesExactIndentedJsonAndHeaders()
    {
        var collector = CreateCollector();
        var expected = JsonSerializer.Serialize(collector.GetSnapshot(), ManagementJsonContext.Default.RequestStatisticsSnapshot);
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        await ManagementApiEndpoints.WriteStatisticsAsync(context, new StatisticsSnapshotCache(collector));

        Assert.Equal(expected, Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventStreams_PreserveExactCompactFramesAndHeaders(bool dashboard)
    {
        var collector = CreateCollector();
        var state = new ConfigurationState();
        var expected = "event: statistics\ndata: " +
            JsonSerializer.Serialize(collector.GetSnapshot(), CompactJson.RequestStatisticsSnapshot) + "\n\n";
        if (dashboard)
        {
            expected = "event: configuration\ndata: " +
                JsonSerializer.Serialize(ConfigurationStatusResponse.FromSnapshot(state.Current), CompactJson.ConfigurationStatusResponse) +
                "\n\n" + expected;
        }
        using var cancellation = new CancellationTokenSource();
        using var body = new CancelOnFlushStream(cancellation);
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = body;

        await (dashboard
            ? new DashboardEventStream(state, collector, new Clock()).WriteAsync(context)
            : ManagementApiEndpoints.StreamStatisticsAsync(context, new StatisticsSnapshotCache(collector)));

        Assert.Equal(expected, Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal("text/event-stream", context.Response.ContentType);
        Assert.Equal(dashboard ? "no-store" : "no-cache", context.Response.Headers.CacheControl);
        Assert.Equal(dashboard ? "" : "keep-alive", context.Response.Headers.Connection.ToString());
        Assert.Equal(dashboard ? "no" : "", context.Response.Headers["X-Accel-Buffering"].ToString());
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void Cache_UsesCollectorMinuteAndInvalidatesForRecordingRetriesAndResets()
    {
        var clock = new Clock { Now = new(2026, 1, 1, 0, 0, 59, TimeSpan.Zero) };
        var collector = new RequestStatisticsCollector(clock);
        var cache = new StatisticsSnapshotCache(collector);
        var endpointId = Guid.NewGuid();
        collector.RecordMatched(endpointId, "GET", "/items", 200, 5, "retry");
        var initial = cache.GetSnapshot();
        var initialJson = cache.GetCompactJson();
        Assert.Same(initial, cache.GetSnapshot());
        Assert.Same(initialJson, cache.GetCompactJson());

        clock.Now += TimeSpan.FromMilliseconds(500);
        Assert.Same(initial, cache.GetSnapshot());
        clock.Now += TimeSpan.FromMilliseconds(500);
        var rolled = cache.GetSnapshot();
        Assert.NotSame(initial, rolled);
        Assert.Equal(60, rolled.RecentMinutes.Length);
        Assert.Equal(0, rolled.RecentMinutes[^1].Requests);
        Assert.Equal(1, rolled.RecentMinutes[^2].Requests);
        Assert.Equal(1, Assert.Single(rolled.Endpoints).RecentMinutes[^2].Requests);
        Assert.NotSame(initialJson, cache.GetCompactJson());

        collector.RecordMatched(endpointId, "GET", "/items", 200, 5, "retry");
        var retry = cache.GetSnapshot();
        Assert.NotSame(rolled, retry);
        Assert.Equal(1, retry.TotalRequests);
        Assert.Equal(2, Assert.Single(retry.RecentRequests).TransportAttempts);
        Assert.Equal(1, Assert.Single(initial.RecentRequests).TransportAttempts);

        Assert.True(collector.Reset(endpointId));
        var endpointReset = cache.GetSnapshot();
        Assert.NotSame(retry, endpointReset);
        Assert.Empty(endpointReset.Endpoints);
        Assert.Empty(endpointReset.RecentRequests);
        Assert.Equal(1, endpointReset.TotalRequests);
        Assert.False(collector.Reset(endpointId));
        Assert.Same(endpointReset, cache.GetSnapshot());
        collector.Reset();
        var reset = cache.GetSnapshot();
        Assert.NotSame(endpointReset, reset);
        Assert.Equal(0, reset.TotalRequests);
        Assert.All(reset.RecentMinutes, minute => Assert.Equal(0, minute.Requests));
        Assert.Equal(JsonSerializer.Serialize(reset, CompactJson.RequestStatisticsSnapshot), cache.GetCompactJson());
        Assert.Single(initial.Endpoints);
        Assert.Equal(1, initial.TotalRequests);
    }

    [Fact]
    public void Cache_IdleRolloverExpiresBothAggregateAndEndpointHistoryAfterSixtyMinutes()
    {
        var clock = new Clock();
        var collector = new RequestStatisticsCollector(clock);
        collector.RecordMatched(Guid.NewGuid(), 503, 3);
        var cache = new StatisticsSnapshotCache(collector);
        var initial = cache.GetSnapshot();
        clock.Now += TimeSpan.FromMinutes(59);
        var retained = cache.GetSnapshot();
        Assert.Equal(1, retained.RecentMinutes[0].Requests);
        Assert.Equal(1, Assert.Single(retained.Endpoints).RecentMinutes[0].ServerErrorResponses);
        clock.Now += TimeSpan.FromMinutes(1);
        var expired = cache.GetSnapshot();
        Assert.Equal(60, expired.RecentMinutes.Length);
        Assert.Equal(0, expired.RecentMinutes.Sum(minute => minute.Requests));
        Assert.Equal(0, Assert.Single(expired.Endpoints).RecentMinutes.Sum(minute => minute.Requests));
        Assert.Equal(1, expired.TotalRequests);
        Assert.Equal(1, initial.RecentMinutes[^1].Requests);
    }

    [Fact]
    public async Task Cache_IsSharedByGetAndBothEventStreamsWithoutMutableSnapshots()
    {
        var collector = CreateCollector();
        var cache = new StatisticsSnapshotCache(collector);
        var snapshot = cache.GetSnapshot();
        var json = cache.GetCompactJson();
        var events = new DashboardEventStream(new ConfigurationState(), cache);
        var context = new DefaultHttpContext();
        context.Response.Body = Stream.Null;
        await ManagementApiEndpoints.WriteStatisticsAsync(context, cache);
        Assert.Same(snapshot, cache.GetSnapshot());
        Assert.Same(json, events.GetStatisticsJson());
        foreach (var dashboard in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            using var body = new CancelOnFlushStream(cancellation);
            var streamContext = new DefaultHttpContext { RequestAborted = cancellation.Token };
            streamContext.Response.Body = body;
            await (dashboard ? events.WriteAsync(streamContext) : ManagementApiEndpoints.StreamStatisticsAsync(streamContext, cache));
            Assert.Contains($"event: statistics\ndata: {json}\n\n", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
            Assert.Same(snapshot, cache.GetSnapshot());
            Assert.Same(json, events.GetStatisticsJson());
        }
        collector.Reset();
        Assert.NotSame(snapshot, cache.GetSnapshot());
        Assert.NotSame(json, events.GetStatisticsJson());
        Assert.Equal(4, snapshot.TotalRequests);
    }

    [Fact]
    public void Cache_ConcurrentIdleReadersShareOneSnapshotAndOneSerialization()
    {
        var cache = new StatisticsSnapshotCache(CreateCollector());
        var snapshots = new RequestStatisticsSnapshot[16];
        var payloads = new string[16];
        Parallel.For(0, snapshots.Length, index =>
        {
            snapshots[index] = cache.GetSnapshot();
            payloads[index] = cache.GetCompactJson();
        });
        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.All(payloads, payload => Assert.Same(payloads[0], payload));
    }

    [Fact]
    public void Cache_UsesOneClockReadForTheKeyAndRollingBuckets()
    {
        var clock = new AdvancingClock();
        var cache = new StatisticsSnapshotCache(new RequestStatisticsCollector(clock));
        using var first = JsonDocument.Parse(cache.GetCompactJson());
        Assert.Equal(1, clock.Reads);
        Assert.Equal(clock.Start, first.RootElement.GetProperty("recentMinutes")[59].GetProperty("minuteUtc").GetDateTimeOffset());
        using var second = JsonDocument.Parse(cache.GetCompactJson());
        Assert.Equal(2, clock.Reads);
        Assert.Equal(clock.Start.AddMinutes(1), second.RootElement.GetProperty("recentMinutes")[59].GetProperty("minuteUtc").GetDateTimeOffset());
    }

    [Fact]
    public async Task StandaloneStream_RetransmitsUnchangedStatisticsEveryRefresh()
    {
        var collector = CreateCollector();
        var cache = new StatisticsSnapshotCache(collector);
        var expected = cache.GetCompactJson();
        var pipe = new Pipe();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var writer = pipe.Writer.AsStream();
        await using var readerStream = pipe.Reader.AsStream();
        using var reader = new StreamReader(readerStream);
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = writer;
        var streaming = ManagementApiEndpoints.StreamStatisticsAsync(context, cache);
        try
        {
            for (var refresh = 0; refresh < 2; refresh++)
            {
                Assert.Equal("event: statistics", await reader.ReadLineAsync(cancellation.Token));
                Assert.Equal($"data: {expected}", await reader.ReadLineAsync(cancellation.Token));
                Assert.Equal("", await reader.ReadLineAsync(cancellation.Token));
                Assert.Same(expected, cache.GetCompactJson());
            }
        }
        finally
        {
            cancellation.Cancel();
            await streaming;
        }
    }

    [Fact]
    public async Task Cache_RecordingCompletionInvalidatesAPartialConcurrentView()
    {
        using var reachedEndpoint = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var endpointId = Guid.NewGuid();
        var collector = new RequestStatisticsCollector(new Clock(), () =>
        {
            reachedEndpoint.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        var cache = new StatisticsSnapshotCache(collector);
        var recording = Task.Run(() => collector.RecordMatched(endpointId, 200, 5));
        try
        {
            Assert.True(reachedEndpoint.Wait(TimeSpan.FromSeconds(10)));
            var partial = cache.GetSnapshot();
            Assert.Equal(1, partial.TotalRequests);
            Assert.Empty(partial.Endpoints);
            _ = cache.GetCompactJson();
        }
        finally
        {
            release.Set();
            await recording;
        }
        var completed = cache.GetSnapshot();
        Assert.Equal(1, Assert.Single(completed.Endpoints).TotalRequests);
        Assert.Equal(JsonSerializer.Serialize(completed, CompactJson.RequestStatisticsSnapshot), cache.GetCompactJson());
    }

    [Fact]
    public async Task Cache_ConcurrentRecordersAndReadersConvergeWithoutLosingCounters()
    {
        var collector = new RequestStatisticsCollector(new Clock());
        var cache = new StatisticsSnapshotCache(collector);
        var endpointId = Guid.NewGuid();
        using var start = new ManualResetEventSlim();
        var recorders = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 1_000; index++)
            {
                collector.RecordMatched(endpointId, 201, 3);
            }
        })).ToArray();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 100; index++)
            {
                var snapshot = cache.GetSnapshot();
                Assert.Equal(60, snapshot.RecentMinutes.Length);
                Assert.InRange(snapshot.TotalRequests, 0, 4_000);
                Assert.InRange(snapshot.RecentRequests.Length, 0, RequestStatisticsCollector.MaximumRecentRequests);
                using var json = JsonDocument.Parse(cache.GetCompactJson());
                Assert.InRange(json.RootElement.GetProperty("totalRequests").GetInt64(), 0, 4_000);
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(recorders.Concat(readers)).WaitAsync(TimeSpan.FromSeconds(20));
        var final = cache.GetSnapshot();
        Assert.Equal(4_000, final.TotalRequests);
        Assert.Equal(4_000, final.MatchedRequests);
        Assert.Equal(12_000, final.ResponseBytes);
        Assert.Equal(4_000, final.RecentMinutes[^1].SuccessResponses);
        Assert.Equal(4_000, Assert.Single(final.Endpoints).TotalRequests);
        Assert.Equal(JsonSerializer.Serialize(final, CompactJson.RequestStatisticsSnapshot), cache.GetCompactJson());
    }

    [Fact]
    public async Task Cache_ConcurrentResetsAndRecordingCannotLeaveAStaleQuiescentView()
    {
        var collector = CreateCollector();
        var cache = new StatisticsSnapshotCache(collector);
        var beforeReset = cache.GetSnapshot();
        var endpointId = beforeReset.Endpoints[0].EndpointId;
        using var start = new ManualResetEventSlim();
        var recording = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 1_000; index++)
            {
                collector.RecordMatched(endpointId, 200, 1);
            }
        });
        var resetting = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 100; index++)
            {
                collector.Reset(endpointId);
                collector.Reset();
            }
        });
        var reading = Task.Run(() =>
        {
            start.Wait();
            for (var index = 0; index < 100; index++)
            {
                Assert.Equal(60, cache.GetSnapshot().RecentMinutes.Length);
                using var json = JsonDocument.Parse(cache.GetCompactJson());
                Assert.InRange(json.RootElement.GetProperty("endpoints").GetArrayLength(), 0, 1);
            }
        });
        start.Set();
        await Task.WhenAll(recording, resetting, reading).WaitAsync(TimeSpan.FromSeconds(20));
        collector.Reset();
        var reset = cache.GetSnapshot();
        Assert.Equal(0, reset.TotalRequests);
        Assert.Empty(reset.Endpoints);
        Assert.Empty(reset.RecentRequests);
        Assert.All(reset.RecentMinutes, minute => Assert.Equal(0, minute.Requests));
        Assert.Equal(JsonSerializer.Serialize(reset, CompactJson.RequestStatisticsSnapshot), cache.GetCompactJson());
        Assert.Equal(4, beforeReset.TotalRequests);
        collector.RecordMatched(endpointId, 200, 7);
        var final = cache.GetSnapshot();
        Assert.Equal(1, final.TotalRequests);
        Assert.Equal(7, final.ResponseBytes);
        Assert.Equal(1, Assert.Single(final.Endpoints).TotalRequests);
    }

    [Fact]
    public async Task IdleGet_DoesNotAllocateAnotherFullSnapshot()
    {
        var cache = new StatisticsSnapshotCache(CreateCollector(RequestStatisticsCollector.MaximumTrackedEndpoints));
        var context = new DefaultHttpContext();
        context.Response.Body = Stream.Null;
        for (var warmup = 0; warmup < 5; warmup++)
        {
            await ManagementApiEndpoints.WriteStatisticsAsync(context, cache);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var writing = ManagementApiEndpoints.WriteStatisticsAsync(context, cache);
        Assert.True(writing.IsCompletedSuccessfully);
        await writing;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Indented async serialization still allocates traversal state. The old path
        // also rebuilt ~1 MiB of minute/endpoint snapshots, exceeding this bound.
        Assert.InRange(allocated, 0, 1536 * 1024);
    }

    [Fact]
    public async Task EventFraming_DoesNotAllocateAnotherPayloadSizedString()
    {
        var json = "{\"value\":\"" + new string('x', 256 * 1024) + "\"}";
        var context = new DefaultHttpContext();
        context.Response.Body = Stream.Null;
        for (var warmup = 0; warmup < 5; warmup++)
        {
            await ManagementEventWriter.WriteAsync(context, "statistics", json);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var writing = ManagementEventWriter.WriteAsync(context, "statistics", json);
        Assert.True(writing.IsCompletedSuccessfully);
        await writing;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public async Task EventStreams_CancellationDuringAnyFrameWriteEndsNormally(bool dashboard, int cancelOnWrite)
    {
        using var cancellation = new CancellationTokenSource();
        using var body = new CancelDuringWriteStream(cancellation, cancelOnWrite);
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = body;
        var cache = new StatisticsSnapshotCache(CreateCollector());
        await (dashboard
            ? new DashboardEventStream(new ConfigurationState(), cache).WriteAsync(context)
            : ManagementApiEndpoints.StreamStatisticsAsync(context, cache));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task StatisticsGet_CancelledResponsePropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = Stream.Null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ManagementApiEndpoints.WriteStatisticsAsync(context, new StatisticsSnapshotCache(CreateCollector())));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EventStreams_DoNotSwallowUncancelledTransportFailures(bool dashboard)
    {
        var context = new DefaultHttpContext();
        using var body = new BrokenStream();
        context.Response.Body = body;
        var cache = new StatisticsSnapshotCache(CreateCollector());
        await Assert.ThrowsAsync<IOException>(() => dashboard
            ? new DashboardEventStream(new ConfigurationState(), cache).WriteAsync(context)
            : ManagementApiEndpoints.StreamStatisticsAsync(context, cache));
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Test disconnect"));
    }

    private sealed class CancelDuringWriteStream(CancellationTokenSource cancellation, int cancelOnWrite) : MemoryStream
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (++_writes == cancelOnWrite)
            {
                cancellation.Cancel();
                return ValueTask.FromCanceled(cancellation.Token);
            }
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    internal static RequestStatisticsCollector CreateCollector(int endpointCount = 1)
    {
        var collector = new RequestStatisticsCollector(new Clock());
        for (var index = 0; index < endpointCount; index++)
        {
            // Stable fixture ordering and JSON size across diagnostic runs.
            var endpointId = new Guid(index + 1, 0, 0, new byte[8]);
            collector.RecordMatched(endpointId, "GET", "/items/\u00e9\n\"test\"", 201, 12);
            collector.RecordFailedWrite(endpointId, null);
            collector.RecordAbortedConnection(endpointId);
        }
        collector.RecordUnmatched();
        return collector;
    }

    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AdvancingClock : TimeProvider
    {
        public DateTimeOffset Start { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() => Start.AddMinutes(Reads++);
    }

    internal sealed class CancelOnFlushStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private long _lastFlushedLength;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            // Response.WriteAsync flushes its PipeWriter too. Cancel at the explicit
            // end-of-event body flush, after all framed bytes have reached the sink.
            if (Length > 0 && Length == _lastFlushedLength)
            {
                cancellation.Cancel();
            }
            _lastFlushedLength = Length;
            return Task.CompletedTask;
        }
    }
}
