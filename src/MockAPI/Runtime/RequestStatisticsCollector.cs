using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MockAPI.Runtime;

public sealed class RequestStatisticsCollector
{
    public const int MaximumTrackedEndpoints = 256;

    private readonly TimeProvider _timeProvider;
    private readonly MinuteBucketSeries _recentMinutes = new();
    private readonly ConcurrentDictionary<Guid, EndpointCounter> _endpoints = new();
    private readonly object _endpointGate = new();
    private long _totalRequests;
    private long _matchedRequests;
    private long _unmatchedRequests;
    private long _failedWrites;
    private long _informationalResponses;
    private long _successResponses;
    private long _redirectionResponses;
    private long _clientErrorResponses;
    private long _serverErrorResponses;
    private long _responseBytes;

    public RequestStatisticsCollector(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void RecordUnmatched()
    {
        var now = _timeProvider.GetUtcNow();
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _unmatchedRequests);
        _recentMinutes.Record(now);
    }

    public void RecordMatched(Guid endpointId, int statusCode, long responseBytes) =>
        RecordMatchedCore(endpointId, statusCode, responseBytes, failedToWrite: false);

    public void RecordFailedWrite(Guid endpointId, int statusCode) =>
        RecordMatchedCore(endpointId, statusCode, responseBytes: 0, failedToWrite: true);

    public RequestStatisticsSnapshot GetSnapshot()
    {
        var now = _timeProvider.GetUtcNow();
        var endpoints = _endpoints
            .Select(pair => pair.Value.GetSnapshot(pair.Key, now))
            .OrderBy(snapshot => snapshot.EndpointId)
            .ToImmutableArray();

        return new RequestStatisticsSnapshot(
            Interlocked.Read(ref _totalRequests),
            Interlocked.Read(ref _matchedRequests),
            Interlocked.Read(ref _unmatchedRequests),
            Interlocked.Read(ref _failedWrites),
            Interlocked.Read(ref _informationalResponses),
            Interlocked.Read(ref _successResponses),
            Interlocked.Read(ref _redirectionResponses),
            Interlocked.Read(ref _clientErrorResponses),
            Interlocked.Read(ref _serverErrorResponses),
            Interlocked.Read(ref _responseBytes),
            _recentMinutes.GetSnapshot(now),
            endpoints);
    }

    private void RecordMatchedCore(
        Guid endpointId,
        int statusCode,
        long responseBytes,
        bool failedToWrite)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(responseBytes);

        var now = _timeProvider.GetUtcNow();
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _matchedRequests);
        if (failedToWrite)
        {
            Interlocked.Increment(ref _failedWrites);
        }

        IncrementStatusClass(statusCode);
        Interlocked.Add(ref _responseBytes, responseBytes);
        _recentMinutes.Record(now);
        GetOrAddEndpoint(endpointId)?.Record(now, statusCode, responseBytes);
    }

    private EndpointCounter? GetOrAddEndpoint(Guid endpointId)
    {
        if (_endpoints.TryGetValue(endpointId, out var existing))
        {
            return existing;
        }

        lock (_endpointGate)
        {
            if (_endpoints.TryGetValue(endpointId, out existing))
            {
                return existing;
            }

            if (_endpoints.Count >= MaximumTrackedEndpoints)
            {
                return null;
            }

            var added = new EndpointCounter();
            _endpoints[endpointId] = added;
            return added;
        }
    }

    private void IncrementStatusClass(int statusCode)
    {
        switch (statusCode)
        {
            case >= 100 and <= 199:
                Interlocked.Increment(ref _informationalResponses);
                break;
            case >= 200 and <= 299:
                Interlocked.Increment(ref _successResponses);
                break;
            case >= 300 and <= 399:
                Interlocked.Increment(ref _redirectionResponses);
                break;
            case >= 400 and <= 499:
                Interlocked.Increment(ref _clientErrorResponses);
                break;
            case >= 500 and <= 599:
                Interlocked.Increment(ref _serverErrorResponses);
                break;
        }
    }

    private sealed class EndpointCounter
    {
        private readonly MinuteBucketSeries _recentMinutes = new();
        private long _totalRequests;
        private long _lastRequestUtcTicks;
        private int _lastStatusCode;
        private long _responseBytes;

        public void Record(DateTimeOffset now, int statusCode, long responseBytes)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Exchange(ref _lastRequestUtcTicks, now.UtcTicks);
            Interlocked.Exchange(ref _lastStatusCode, statusCode);
            Interlocked.Add(ref _responseBytes, responseBytes);
            _recentMinutes.Record(now);
        }

        public EndpointStatisticsSnapshot GetSnapshot(Guid endpointId, DateTimeOffset now)
        {
            var lastRequestTicks = Interlocked.Read(ref _lastRequestUtcTicks);
            return new EndpointStatisticsSnapshot(
                endpointId,
                Interlocked.Read(ref _totalRequests),
                lastRequestTicks == 0 ? null : new DateTimeOffset(lastRequestTicks, TimeSpan.Zero),
                Volatile.Read(ref _lastStatusCode),
                Interlocked.Read(ref _responseBytes),
                _recentMinutes.GetSnapshot(now));
        }
    }

    private sealed class MinuteBucketSeries
    {
        private const int BucketCount = 60;
        private readonly MinuteBucket[] _buckets = Enumerable.Range(0, BucketCount)
            .Select(_ => new MinuteBucket())
            .ToArray();

        public void Record(DateTimeOffset timestamp)
        {
            var minute = GetUnixMinute(timestamp);
            _buckets[minute % BucketCount].Record(minute);
        }

        public ImmutableArray<MinuteBucketSnapshot> GetSnapshot(DateTimeOffset timestamp)
        {
            var currentMinute = GetUnixMinute(timestamp);
            var builder = ImmutableArray.CreateBuilder<MinuteBucketSnapshot>(BucketCount);
            for (var offset = BucketCount - 1; offset >= 0; offset--)
            {
                var minute = currentMinute - offset;
                var requests = _buckets[minute % BucketCount].Read(minute);
                builder.Add(new MinuteBucketSnapshot(
                    DateTimeOffset.FromUnixTimeSeconds((long)minute * 60),
                    requests));
            }

            return builder.MoveToImmutable();
        }

        private static int GetUnixMinute(DateTimeOffset timestamp) =>
            checked((int)(timestamp.ToUnixTimeSeconds() / 60));
    }

    private sealed class MinuteBucket
    {
        private long _state;

        public void Record(int minute)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                var recordedMinute = (int)(state >> 32);
                var count = (uint)state;
                var nextCount = recordedMinute == minute
                    ? count == uint.MaxValue ? count : count + 1
                    : 1;
                var nextState = ((long)minute << 32) | nextCount;
                if (Interlocked.CompareExchange(ref _state, nextState, state) == state)
                {
                    return;
                }
            }
        }

        public long Read(int minute)
        {
            var state = Volatile.Read(ref _state);
            return (int)(state >> 32) == minute ? (uint)state : 0;
        }
    }
}

public sealed record RequestStatisticsSnapshot(
    long TotalRequests,
    long MatchedRequests,
    long UnmatchedRequests,
    long FailedWrites,
    long InformationalResponses,
    long SuccessResponses,
    long RedirectionResponses,
    long ClientErrorResponses,
    long ServerErrorResponses,
    long ResponseBytes,
    ImmutableArray<MinuteBucketSnapshot> RecentMinutes,
    ImmutableArray<EndpointStatisticsSnapshot> Endpoints);

public sealed record EndpointStatisticsSnapshot(
    Guid EndpointId,
    long TotalRequests,
    DateTimeOffset? LastRequestUtc,
    int LastStatusCode,
    long ResponseBytes,
    ImmutableArray<MinuteBucketSnapshot> RecentMinutes);

public readonly record struct MinuteBucketSnapshot(DateTimeOffset MinuteUtc, long Requests);