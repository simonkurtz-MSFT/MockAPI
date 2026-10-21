using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;
using Xunit.Abstractions;

namespace MockAPI.Tests.Management;

public sealed class StatisticsDeltaTests(ITestOutputHelper output)
{
    [Fact]
    public void Frames_CoalesceCapturesAndShareImmutablePayloadsAcrossReaders()
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        var snapshots = new StatisticsSnapshotCache(collector);
        var cache = new StatisticsDeltaCache(snapshots, clock);
        var first = cache.GetFrame();
        Assert.Equal(1, first.Sequence);
        Assert.Null(first.Delta);
        Assert.Throws<InvalidOperationException>(() => first.GetDeltaJson());
        Assert.Same(snapshots.GetCompactJson(), first.GetSnapshotJson());
        collector.RecordMatched(Guid.NewGuid(), 201, 4);
        Assert.Same(first, cache.GetFrame());
        clock.Now += TimeSpan.FromSeconds(2);
        var frames = new StatisticsDeltaFrame[16];
        var payloads = new string[16];
        Parallel.For(0, frames.Length, index =>
        {
            frames[index] = cache.GetFrame();
            payloads[index] = frames[index].GetDeltaJson();
        });
        Assert.All(frames, frame => Assert.Same(frames[0], frame));
        Assert.All(payloads, payload => Assert.Same(payloads[0], payload));
        Assert.Equal(2, frames[0].Sequence);
        Assert.Equal(1, frames[0].Delta!.BaseSequence);
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Same(frames[0], cache.GetFrame());
        Assert.Equal(0, first.Snapshot.TotalRequests);

        // A later GET must not cause an older, still-streaming frame to serialize newer state.
        collector.RecordUnmatched();
        var latest = snapshots.GetSnapshot();
        Assert.NotSame(latest, frames[0].Snapshot);
        using var captured = JsonDocument.Parse(frames[0].GetSnapshotJson());
        Assert.Equal(1, captured.RootElement.GetProperty("totalRequests").GetInt64());
        Assert.Same(latest, snapshots.GetSnapshot());
        Assert.Same(frames[0].GetSnapshotJson(), frames[0].GetSnapshotJson());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulResets_RequireBaselinesButMissingEndpointResetsDoNot(bool global)
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        var id = Guid.NewGuid();
        collector.RecordMatched(id, 200, 4);
        var cache = new StatisticsDeltaCache(new StatisticsSnapshotCache(collector), clock);
        var before = cache.GetFrame();
        Assert.False(collector.Reset(Guid.NewGuid()));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Same(before, cache.GetFrame());
        if (global) collector.Reset();
        else Assert.True(collector.Reset(id));
        clock.Now += TimeSpan.FromSeconds(2);
        var reset = cache.GetFrame();
        Assert.Equal(before.Sequence + 1, reset.Sequence);
        Assert.Null(reset.Delta);
        Assert.Empty(reset.Snapshot.Endpoints);
        Assert.Empty(reset.Snapshot.RecentRequests);
    }

    [Fact]
    public void Deltas_ContainAbsoluteChangedCountersAndReplaceRetrySummaries()
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        var id = Guid.NewGuid();
        collector.RecordMatched(id, "GET", "/items", 200, 5, "retry");
        var first = collector.GetSnapshot();
        collector.RecordMatched(id, "GET", "/items", 200, 5, "retry");
        var retried = collector.GetSnapshot();
        var retry = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(first, retried, 1, 2));
        Assert.Equal(1, retry.Totals.TotalRequests);
        Assert.Empty(retry.RecentMinutes);
        Assert.Empty(retry.Endpoints);
        Assert.Equal(2, Assert.Single(retry.RecentRequests!.Value).TransportAttempts);
        collector.RecordMatched(id, 503, 7);
        var current = collector.GetSnapshot();
        var changed = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(retried, current, 2, 3));
        var endpoint = Assert.Single(changed.Endpoints);
        Assert.Equal(2, endpoint.TotalRequests);
        Assert.Equal(12, endpoint.ResponseBytes);
        Assert.Equal(503, endpoint.LastStatusCode);
        Assert.Equal(2, Assert.Single(endpoint.RecentMinutes).Requests);
        Assert.Equal(1, Assert.Single(changed.RecentMinutes).ServerErrorResponses);
        Assert.Equal(1, first.TotalRequests);
        Assert.Equal(1, Assert.Single(first.RecentRequests).TransportAttempts);
        var idle = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(current, current, 3, 4));
        Assert.Null(idle.RecentRequests);
        Assert.Empty(idle.Endpoints);
        Assert.Empty(idle.RecentMinutes);
        var removed = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(current, first with
        {
            Endpoints = [],
            RecentRequests = []
        }, 3, 4));
        Assert.Equal(id, Assert.Single(removed.RemovedEndpointIds));
        Assert.Empty(removed.RecentRequests!.Value);
    }

    [Fact]
    public void NewEndpoints_SendOnlyNonzeroMinutesAndRetainAllScalarChanges()
    {
        var collector = new RequestStatisticsCollector(new StatisticsDeliveryTests.Clock());
        var empty = collector.GetSnapshot();
        var id = Guid.NewGuid();
        collector.RecordMatched(id, 201, 3);
        var populated = collector.GetSnapshot();
        var added = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(empty, populated, 1, 2));
        Assert.Single(Assert.Single(added.Endpoints).RecentMinutes);
        var original = Assert.Single(populated.Endpoints);
        foreach (var endpoint in new[]
        {
            original with { TotalRequests = 9 },
            original with { LastRequestUtc = original.LastRequestUtc!.Value.AddSeconds(1) },
            original with { LastStatusCode = 404 },
            original with { ResponseBytes = 9 }
        })
        {
            var delta = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(populated, populated with
            {
                Endpoints = [endpoint]
            }, 2, 3));
            Assert.Empty(Assert.Single(delta.Endpoints).RecentMinutes);
        }
        var zero = original with { RecentMinutes = empty.RecentMinutes };
        var zeroAdded = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(empty, empty with
        {
            Endpoints = [zero]
        }, 1, 2));
        Assert.Empty(Assert.Single(zeroAdded.Endpoints).RecentMinutes);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(300)]
    [InlineData(400)]
    [InlineData(500)]
    public void NewEndpointDeltas_PreserveStatusCountsCapturedBeforeRequestCount(int statusCode)
    {
        var collector = new RequestStatisticsCollector(new StatisticsDeliveryTests.Clock());
        var empty = collector.GetSnapshot();
        collector.RecordMatched(Guid.NewGuid(), statusCode, 0);
        var populated = collector.GetSnapshot();
        var endpoint = Assert.Single(populated.Endpoints);
        // Snapshot counters are non-transactional: the status increment can be visible before the request increment.
        var partialMinutes = endpoint.RecentMinutes.SetItem(
            endpoint.RecentMinutes.Length - 1, endpoint.RecentMinutes[^1] with { Requests = 0 });
        var partial = populated with { Endpoints = [endpoint with { RecentMinutes = partialMinutes }] };

        var delta = Assert.IsType<StatisticsDelta>(StatisticsDelta.Create(empty, partial, 1, 2));

        Assert.Equal(partialMinutes[^1], Assert.Single(Assert.Single(delta.Endpoints).RecentMinutes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(-1)]
    public void Windows_CompareByUtcIdentityIncludingRolloverAndBackwardClockChanges(int minutes)
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        collector.RecordMatched(Guid.NewGuid(), 200, 3);
        var cache = new StatisticsDeltaCache(new StatisticsSnapshotCache(collector), clock);
        var first = cache.GetFrame();
        clock.Now += TimeSpan.FromMinutes(minutes);
        var current = cache.GetFrame();
        Assert.NotSame(first, current);
        var delta = Assert.IsType<StatisticsDelta>(current.Delta);
        Assert.Equal(current.Snapshot.RecentMinutes[0].MinuteUtc, delta.WindowStartUtc);
        Assert.Equal(Math.Min(Math.Abs(minutes), 60), delta.RecentMinutes.Length);
        Assert.Equal(1, first.Snapshot.RecentMinutes[^1].Requests);
    }

    [Fact]
    public void ClampedDuplicateMinuteIdentities_UseBaselinesInBothDirections()
    {
        var clock = new StatisticsDeliveryTests.Clock { Now = DateTimeOffset.MinValue };
        var collector = new RequestStatisticsCollector(clock);
        var clamped = collector.GetSnapshot();
        clock.Now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var normal = collector.GetSnapshot();
        Assert.Null(StatisticsDelta.Create(clamped, normal, 1, 2));
        Assert.Null(StatisticsDelta.Create(normal, clamped, 1, 2));
    }

    [Fact]
    public void SparseInterval_At256EndpointsUsesLessThanTwoPercentOfTheFullPayload()
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = StatisticsDeliveryTests.CreateCollector(RequestStatisticsCollector.MaximumTrackedEndpoints);
        var cache = new StatisticsDeltaCache(new StatisticsSnapshotCache(collector), clock);
        var first = cache.GetFrame();
        collector.RecordMatched(first.Snapshot.Endpoints[0].EndpointId, 201, 1);
        clock.Now += TimeSpan.FromSeconds(2);
        var changed = cache.GetFrame();
        var fullBytes = Encoding.UTF8.GetByteCount(changed.GetSnapshotJson());
        var deltaBytes = Encoding.UTF8.GetByteCount(changed.GetDeltaJson());
        output.WriteLine($"256 endpoints: full={fullBytes:N0} bytes; delta={deltaBytes:N0} bytes; reduction={1d - (double)deltaBytes / fullBytes:P2}");
        Assert.InRange(deltaBytes, 1, fullBytes / 50);
        Assert.Single(changed.Delta!.Endpoints);
        using var json = JsonDocument.Parse(changed.GetDeltaJson());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("recentRequests").ValueKind);
        Assert.Equal(2, json.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal(1, json.RootElement.GetProperty("baseSequence").GetInt64());
        Assert.Equal("response", json.RootElement.GetProperty("recentRequests")[0].GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task SlowStream_MissingAnIntermediateFrameReceivesAFreshBaseline()
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        var events = new DashboardEventStream(new ConfigurationState(), collector, clock);
        using var fastCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var slowCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pipe = new Pipe();
        await using var writer = pipe.Writer.AsStream();
        await using var input = pipe.Reader.AsStream();
        using var reader = new StreamReader(input);
        var fastContext = new DefaultHttpContext { RequestAborted = fastCancellation.Token };
        fastContext.Response.Body = writer;
        var fast = events.WriteAsync(fastContext, true);
        using var slowBody = new PausedBaselineStream(slowCancellation);
        var slowContext = new DefaultHttpContext { RequestAborted = slowCancellation.Token };
        slowContext.Response.Body = slowBody;
        Task? slow = null;
        try
        {
            await ReadUntilId(1);
            slow = events.WriteAsync(slowContext, true);
            await slowBody.Paused.Task.WaitAsync(slowCancellation.Token);
            for (var sequence = 2; sequence <= 3; sequence++)
            {
                collector.RecordUnmatched();
                clock.Now += TimeSpan.FromSeconds(2);
                await ReadUntilId(sequence);
            }
            slowBody.Resume.TrySetResult();
            await slow.WaitAsync(TimeSpan.FromSeconds(5));
            var received = Encoding.UTF8.GetString(slowBody.ToArray());
            Assert.Contains("id: 1\nevent: statistics\n", received, StringComparison.Ordinal);
            Assert.Contains("id: 3\nevent: statistics\n", received, StringComparison.Ordinal);
            Assert.DoesNotContain("event: statistics-delta", received, StringComparison.Ordinal);
        }
        finally
        {
            slowBody.Resume.TrySetResult();
            fastCancellation.Cancel();
            slowCancellation.Cancel();
            await fast;
            if (slow is not null) await slow;
        }

        async Task ReadUntilId(int sequence)
        {
            while (await reader.ReadLineAsync(fastCancellation.Token) is { } line)
            {
                if (line == $"id: {sequence}") return;
            }
            Assert.Fail("The stream ended before its expected statistics frame.");
        }
    }

    private sealed class PausedBaselineStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _lastFlushedLength;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.SequenceEqual("id: 1\n"u8))
            {
                Paused.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (Length == _lastFlushedLength &&
                Encoding.UTF8.GetString(GetBuffer(), 0, (int)Length).Contains("id: 3\n", StringComparison.Ordinal))
            {
                cancellation.Cancel();
            }
            _lastFlushedLength = Length;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task OptInStream_SendsASequencedBaselineThenAnAbsoluteDelta()
    {
        var clock = new StatisticsDeliveryTests.Clock();
        var collector = new RequestStatisticsCollector(clock);
        var pipe = new Pipe();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var writer = pipe.Writer.AsStream();
        await using var input = pipe.Reader.AsStream();
        using var reader = new StreamReader(input);
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        context.Response.Body = writer;
        var streaming = new DashboardEventStream(new ConfigurationState(), collector, clock).WriteAsync(context, true);
        try
        {
            Assert.Equal("configuration", (await ReadEvent()).Name);
            var baseline = await ReadEvent();
            Assert.Equal("statistics", baseline.Name);
            Assert.Equal("id: 1", baseline.Id);
            collector.RecordMatched(Guid.NewGuid(), 200, 5);
            clock.Now += TimeSpan.FromSeconds(2);
            var delta = await ReadEvent();
            Assert.Equal("statistics-delta", delta.Name);
            Assert.Equal("id: 2", delta.Id);
            using var json = JsonDocument.Parse(delta.Data);
            Assert.Equal(1, json.RootElement.GetProperty("totals").GetProperty("totalRequests").GetInt64());
            collector.Reset();
            clock.Now += TimeSpan.FromSeconds(2);
            var reset = await ReadEvent();
            Assert.Equal("statistics", reset.Name);
            Assert.Equal("id: 3", reset.Id);
        }
        finally
        {
            cancellation.Cancel();
            await streaming;
        }

        async Task<(string Name, string? Id, string Data)> ReadEvent()
        {
            var name = await reader.ReadLineAsync(cancellation.Token);
            string? id = null;
            if (name!.StartsWith("id:", StringComparison.Ordinal))
            {
                id = name;
                name = await reader.ReadLineAsync(cancellation.Token);
            }
            var data = await reader.ReadLineAsync(cancellation.Token);
            Assert.Equal("", await reader.ReadLineAsync(cancellation.Token));
            return (name!["event: ".Length..], id, data!["data: ".Length..]);
        }
    }
}
