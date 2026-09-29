using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace MockAPI.Runtime;

/// <summary>Collects bounded, process-local request statistics using thread-safe counters and rolling minute buckets.</summary>
/// <remarks>Snapshots are safe during concurrent recording and may reflect operations completing across the snapshot interval.</remarks>
public sealed class RequestStatisticsCollector
{
    /// <summary>Gets the maximum number of endpoint-specific counters retained in process memory.</summary>
    public const int MaximumTrackedEndpoints = 256;

    /// <summary>Gets the maximum number of recent request summaries retained in process memory.</summary>
    public const int MaximumRecentRequests = 100;

    private readonly TimeProvider _timeProvider;
    private readonly Action? _beforeEndpointLock;
    private readonly MinuteBucketSeries _recentMinutes = new();
    private readonly ConcurrentDictionary<Guid, EndpointCounter> _endpoints = new();
    private readonly object _endpointGate = new();
    private readonly Queue<RecentRequestRecord> _recentRequests = new(MaximumRecentRequests);
    private readonly HashSet<string> _recentRequestIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentUnmatchedRequestIds = new(MaximumRecentRequests);
    private readonly HashSet<string> _recentUnmatchedRequestIdSet = new(StringComparer.Ordinal);
    private readonly object _recentRequestsGate = new();
    private long _totalRequests;
    private long _matchedRequests;
    private long _unmatchedRequests;
    private long _failedWrites;
    private long _abortedConnections;
    private long _informationalResponses;
    private long _successResponses;
    private long _redirectionResponses;
    private long _clientErrorResponses;
    private long _serverErrorResponses;
    private long _responseBytes;
    private long _version;

    internal long Version => Interlocked.Read(ref _version);

    /// <summary>Initializes a request statistics collector.</summary>
    /// <param name="timeProvider">The UTC time source, or <see langword="null"/> to use <see cref="TimeProvider.System"/>.</param>
    public RequestStatisticsCollector(TimeProvider? timeProvider = null)
        : this(timeProvider, beforeEndpointLock: null)
    {
    }

    internal RequestStatisticsCollector(TimeProvider? timeProvider, Action? beforeEndpointLock)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _beforeEndpointLock = beforeEndpointLock;
    }

    /// <summary>Records one request for which no enabled method/path pair matched.</summary>
    public void RecordUnmatched() => RecordUnmatched(string.Empty, string.Empty);

    /// <summary>Records one request for which no enabled method/path pair matched.</summary>
    /// <param name="method">The normalized HTTP method.</param>
    /// <param name="path">The normalized request path without a query string.</param>
    /// <param name="requestId">An optional logical request ID used to count repeated transport attempts as one request.</param>
    public void RecordUnmatched(string method, string path, string? requestId = null)
    {
        var now = _timeProvider.GetUtcNow();
        if (!TryRecordUnmatchedRequest(requestId))
        {
            return;
        }

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _unmatchedRequests);
        _recentMinutes.Record(now, StatusCodes.Status404NotFound);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Records a matched request whose configured response completed.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    /// <param name="statusCode">The HTTP status code selected for the response.</param>
    /// <param name="responseBytes">The number of body bytes written; this is zero for <c>HEAD</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="responseBytes"/> is negative.</exception>
    public void RecordMatched(Guid endpointId, int statusCode, long responseBytes) =>
        RecordMatched(endpointId, string.Empty, string.Empty, statusCode, responseBytes);

    /// <summary>Records a matched request whose configured response completed.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    /// <param name="method">The normalized HTTP method.</param>
    /// <param name="path">The normalized request path without a query string.</param>
    /// <param name="statusCode">The HTTP status code selected for the response.</param>
    /// <param name="responseBytes">The number of body bytes written; this is zero for <c>HEAD</c>.</param>
    /// <param name="requestId">An optional logical request ID used to count repeated transport attempts as one request.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="responseBytes"/> is negative.</exception>
    public void RecordMatched(
        Guid endpointId,
        string method,
        string path,
        int statusCode,
        long responseBytes,
        string? requestId = null) =>
        RecordMatchedCore(endpointId, method, path, statusCode, responseBytes, failedToWrite: false, requestId);

    /// <summary>Records a matched response that failed while writing and contributed no completed body bytes.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    /// <param name="statusCode">The terminal status code configured for the endpoint.</param>
    public void RecordFailedWrite(Guid endpointId, int statusCode) =>
        RecordFailedWrite(endpointId, string.Empty, string.Empty, statusCode);

    /// <summary>Records a matched response that failed while writing and contributed no completed body bytes.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    /// <param name="method">The normalized HTTP method.</param>
    /// <param name="path">The normalized request path without a query string.</param>
    /// <param name="statusCode">The terminal status code configured for the endpoint.</param>
    /// <param name="requestId">An optional logical request ID used to count repeated transport attempts as one request.</param>
    public void RecordFailedWrite(
        Guid endpointId,
        string method,
        string path,
        int statusCode,
        string? requestId = null) =>
        RecordMatchedCore(endpointId, method, path, statusCode, responseBytes: 0, failedToWrite: true, requestId);

    /// <summary>Records a matched request whose configured behavior intentionally aborted the connection.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    public void RecordAbortedConnection(Guid endpointId)
        => RecordAbortedConnection(endpointId, string.Empty, string.Empty);

    /// <summary>Records a matched request whose configured behavior intentionally aborted the connection.</summary>
    /// <param name="endpointId">The stable ID of the matched endpoint.</param>
    /// <param name="method">The normalized HTTP method.</param>
    /// <param name="path">The normalized request path without a query string.</param>
    /// <param name="requestId">An optional logical request ID used to count repeated transport attempts as one request.</param>
    public void RecordAbortedConnection(Guid endpointId, string method, string path, string? requestId = null)
    {
        var now = _timeProvider.GetUtcNow();
        var request = new RequestLogEntrySnapshot(
            now,
            method,
            path,
            endpointId,
            StatusCode: null,
            RequestLogOutcome.Aborted,
            ResponseBytes: 0);
        if (!TryRecordRecentRequest(request, requestId))
        {
            return;
        }

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _matchedRequests);
        Interlocked.Increment(ref _abortedConnections);
        _recentMinutes.Record(now);
        GetOrAddEndpoint(endpointId)?.RecordAbort(now);
        Interlocked.Increment(ref _version);
    }

    /// <summary>Captures aggregate, endpoint, and trailing 60-minute statistics without blocking recorders globally.</summary>
    /// <returns>An immutable, point-in-time view ordered by endpoint ID.</returns>
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
            Interlocked.Read(ref _abortedConnections),
            Interlocked.Read(ref _informationalResponses),
            Interlocked.Read(ref _successResponses),
            Interlocked.Read(ref _redirectionResponses),
            Interlocked.Read(ref _clientErrorResponses),
            Interlocked.Read(ref _serverErrorResponses),
            Interlocked.Read(ref _responseBytes),
            _recentMinutes.GetSnapshot(now),
            endpoints,
            GetRecentRequestsSnapshot());
    }

    /// <summary>Resets aggregate, endpoint, and rolling-window counters.</summary>
    /// <remarks>Recording may continue concurrently, so requests racing the reset may appear before or after it.</remarks>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _matchedRequests, 0);
        Interlocked.Exchange(ref _unmatchedRequests, 0);
        Interlocked.Exchange(ref _failedWrites, 0);
        Interlocked.Exchange(ref _abortedConnections, 0);
        Interlocked.Exchange(ref _informationalResponses, 0);
        Interlocked.Exchange(ref _successResponses, 0);
        Interlocked.Exchange(ref _redirectionResponses, 0);
        Interlocked.Exchange(ref _clientErrorResponses, 0);
        Interlocked.Exchange(ref _serverErrorResponses, 0);
        Interlocked.Exchange(ref _responseBytes, 0);
        _recentMinutes.Reset();
        _endpoints.Clear();
        lock (_recentRequestsGate)
        {
            _recentRequests.Clear();
            _recentRequestIds.Clear();
            _recentUnmatchedRequestIds.Clear();
            _recentUnmatchedRequestIdSet.Clear();
        }
        Interlocked.Increment(ref _version);
    }

    /// <summary>Removes statistics retained for one stable endpoint ID.</summary>
    /// <param name="endpointId">The endpoint ID whose counters are removed.</param>
    /// <returns><see langword="true"/> when counters existed and were removed; otherwise, <see langword="false"/>.</returns>
    /// <remarks>A concurrent request may recreate the endpoint counter after removal.</remarks>
    public bool Reset(Guid endpointId)
    {
        var removed = _endpoints.TryRemove(endpointId, out _);
        if (!removed)
        {
            return false;
        }

        lock (_recentRequestsGate)
        {
            var retained = new List<RecentRequestRecord>(_recentRequests.Count);
            foreach (var request in _recentRequests)
            {
                if (request.Entry.EndpointId.GetValueOrDefault() != endpointId)
                {
                    retained.Add(request);
                }
            }

            _recentRequests.Clear();
            _recentRequestIds.Clear();
            foreach (var request in retained)
            {
                _recentRequests.Enqueue(request);
                if (request.RequestId is not null)
                {
                    _recentRequestIds.Add(request.RequestId);
                }
            }
        }

        Interlocked.Increment(ref _version);
        return true;
    }

    private void RecordMatchedCore(
        Guid endpointId,
        string method,
        string path,
        int statusCode,
        long responseBytes,
        bool failedToWrite,
        string? requestId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(responseBytes);

        var now = _timeProvider.GetUtcNow();
        var request = new RequestLogEntrySnapshot(
            now,
            method,
            path,
            endpointId,
            statusCode,
            failedToWrite ? RequestLogOutcome.FailedWrite : RequestLogOutcome.Response,
            responseBytes);
        if (!TryRecordRecentRequest(request, requestId))
        {
            return;
        }

        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _matchedRequests);
        if (failedToWrite)
        {
            Interlocked.Increment(ref _failedWrites);
        }

        IncrementStatusClass(statusCode);
        Interlocked.Add(ref _responseBytes, responseBytes);
        _recentMinutes.Record(now, statusCode);
        GetOrAddEndpoint(endpointId)?.Record(now, statusCode, responseBytes);
        Interlocked.Increment(ref _version);
    }

    private bool TryRecordRecentRequest(RequestLogEntrySnapshot request, string? requestId = null)
    {
        var boundedRequestId = string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ? null : requestId;
        lock (_recentRequestsGate)
        {
            if (boundedRequestId is not null && _recentUnmatchedRequestIdSet.Contains(boundedRequestId))
            {
                Interlocked.Increment(ref _version);
                return false;
            }

            if (boundedRequestId is not null && !_recentRequestIds.Add(boundedRequestId))
            {
                var recentRequest = _recentRequests.First(request =>
                    string.Equals(request.RequestId, boundedRequestId, StringComparison.Ordinal));
                recentRequest.RecordTransportAttempt();
                Interlocked.Increment(ref _version);
                return false;
            }

            if (_recentRequests.Count == MaximumRecentRequests)
            {
                var removed = _recentRequests.Dequeue();
                if (removed.RequestId is not null)
                {
                    _recentRequestIds.Remove(removed.RequestId);
                }
            }

            _recentRequests.Enqueue(new RecentRequestRecord(request, boundedRequestId));
            return true;
        }
    }

    private bool TryRecordUnmatchedRequest(string? requestId)
    {
        var boundedRequestId = string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ? null : requestId;
        if (boundedRequestId is null)
        {
            return true;
        }

        lock (_recentRequestsGate)
        {
            if (_recentRequestIds.Contains(boundedRequestId) ||
                !_recentUnmatchedRequestIdSet.Add(boundedRequestId))
            {
                Interlocked.Increment(ref _version);
                return false;
            }

            if (_recentUnmatchedRequestIds.Count == MaximumRecentRequests)
            {
                var removedRequestId = _recentUnmatchedRequestIds.Dequeue();
                _recentUnmatchedRequestIdSet.Remove(removedRequestId);
            }

            _recentUnmatchedRequestIds.Enqueue(boundedRequestId);
            return true;
        }
    }

    private ImmutableArray<RequestLogEntrySnapshot> GetRecentRequestsSnapshot()
    {
        lock (_recentRequestsGate)
        {
            return _recentRequests.Reverse().Select(request => request.Entry).ToImmutableArray();
        }
    }

    private sealed class RecentRequestRecord(RequestLogEntrySnapshot entry, string? requestId)
    {
        public RequestLogEntrySnapshot Entry { get; private set; } = entry;

        public string? RequestId { get; } = requestId;

        public void RecordTransportAttempt() =>
            Entry = Entry with { TransportAttempts = GetNextTransportAttemptCount(Entry.TransportAttempts) };
    }

    internal static int GetNextTransportAttemptCount(int count) =>
        count == int.MaxValue ? count : count + 1;

    private EndpointCounter? GetOrAddEndpoint(Guid endpointId)
    {
        if (_endpoints.TryGetValue(endpointId, out var existing))
        {
            return existing;
        }

        _beforeEndpointLock?.Invoke();
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

    internal static uint GetNextBucketCount(int recordedMinute, int minute, uint count) =>
        recordedMinute == minute
            ? count == uint.MaxValue ? count : count + 1
            : 1;

    private sealed class EndpointCounter
    {
        private readonly MinuteBucketSeries _recentMinutes = new();
        private long _totalRequests;
        private long _lastRequestUtcTicks;
        private int _lastStatusCode;
        private long _responseBytes;

        public void RecordAbort(DateTimeOffset now)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Exchange(ref _lastRequestUtcTicks, now.UtcTicks);
            Interlocked.Exchange(ref _lastStatusCode, 0);
            _recentMinutes.Record(now);
        }

        public void Record(DateTimeOffset now, int statusCode, long responseBytes)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Exchange(ref _lastRequestUtcTicks, now.UtcTicks);
            Interlocked.Exchange(ref _lastStatusCode, statusCode);
            Interlocked.Add(ref _responseBytes, responseBytes);
            _recentMinutes.Record(now, statusCode);
        }

        public EndpointStatisticsSnapshot GetSnapshot(Guid endpointId, DateTimeOffset now)
        {
            var lastRequestTicks = Interlocked.Read(ref _lastRequestUtcTicks);
            return new EndpointStatisticsSnapshot(
                endpointId,
                Interlocked.Read(ref _totalRequests),
                new DateTimeOffset(lastRequestTicks, TimeSpan.Zero),
                Volatile.Read(ref _lastStatusCode) is var statusCode && statusCode != 0 ? statusCode : null,
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

        public void Record(DateTimeOffset timestamp, int? statusCode = null)
        {
            var minute = GetUnixMinute(timestamp);
            _buckets[GetBucketIndex(minute)].Record(minute, statusCode);
        }

        public ImmutableArray<MinuteBucketSnapshot> GetSnapshot(DateTimeOffset timestamp)
        {
            var currentMinute = GetUnixMinute(timestamp);
            var builder = ImmutableArray.CreateBuilder<MinuteBucketSnapshot>(BucketCount);
            for (var offset = BucketCount - 1; offset >= 0; offset--)
            {
                var minute = currentMinute - offset;
                var bucket = _buckets[GetBucketIndex(minute)];
                builder.Add(new MinuteBucketSnapshot(
                    DateTimeOffset.FromUnixTimeSeconds(Math.Max(
                        (long)minute * 60,
                        DateTimeOffset.MinValue.ToUnixTimeSeconds())),
                    bucket.ReadRequests(minute),
                    bucket.ReadInformationalResponses(minute),
                    bucket.ReadSuccessResponses(minute),
                    bucket.ReadRedirectionResponses(minute),
                    bucket.ReadClientErrorResponses(minute),
                    bucket.ReadServerErrorResponses(minute)));
            }

            return builder.MoveToImmutable();
        }

        public void Reset()
        {
            foreach (var bucket in _buckets)
            {
                bucket.Reset();
            }
        }

        private static int GetUnixMinute(DateTimeOffset timestamp) =>
            checked((int)(timestamp.ToUnixTimeSeconds() / 60));

        private static int GetBucketIndex(int minute) => Math.Abs(minute % BucketCount);
    }

    private sealed class MinuteBucket
    {
        private readonly MinuteCounter _requests = new();
        private readonly MinuteCounter _informationalResponses = new();
        private readonly MinuteCounter _successResponses = new();
        private readonly MinuteCounter _redirectionResponses = new();
        private readonly MinuteCounter _clientErrorResponses = new();
        private readonly MinuteCounter _serverErrorResponses = new();

        public void Record(int minute, int? statusCode)
        {
            _requests.Record(minute);
            switch (statusCode)
            {
                case >= 100 and <= 199:
                    _informationalResponses.Record(minute);
                    break;
                case >= 200 and <= 299:
                    _successResponses.Record(minute);
                    break;
                case >= 300 and <= 399:
                    _redirectionResponses.Record(minute);
                    break;
                case >= 400 and <= 499:
                    _clientErrorResponses.Record(minute);
                    break;
                case >= 500 and <= 599:
                    _serverErrorResponses.Record(minute);
                    break;
            }
        }

        public long ReadRequests(int minute) => _requests.Read(minute);

        public long ReadInformationalResponses(int minute) => _informationalResponses.Read(minute);

        public long ReadSuccessResponses(int minute) => _successResponses.Read(minute);

        public long ReadRedirectionResponses(int minute) => _redirectionResponses.Read(minute);

        public long ReadClientErrorResponses(int minute) => _clientErrorResponses.Read(minute);

        public long ReadServerErrorResponses(int minute) => _serverErrorResponses.Read(minute);

        public void Reset()
        {
            _requests.Reset();
            _informationalResponses.Reset();
            _successResponses.Reset();
            _redirectionResponses.Reset();
            _clientErrorResponses.Reset();
            _serverErrorResponses.Reset();
        }
    }

    private sealed class MinuteCounter
    {
        private long _state;

        public void Record(int minute)
        {
            while (true)
            {
                var state = Volatile.Read(ref _state);
                var recordedMinute = (int)(state >> 32);
                var count = (uint)state;
                var nextCount = GetNextBucketCount(recordedMinute, minute, count);
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

        public void Reset() => Interlocked.Exchange(ref _state, 0);
    }
}

/// <summary>Represents immutable aggregate and endpoint statistics captured from one process.</summary>
/// <param name="TotalRequests">All logical requests observed by the mock dispatcher.</param>
/// <param name="MatchedRequests">Logical requests matched to an enabled endpoint, including aborts and failed writes.</param>
/// <param name="UnmatchedRequests">Logical requests for which no enabled method/path pair matched.</param>
/// <param name="FailedWrites">Matched responses that threw while writing.</param>
/// <param name="AbortedConnections">Matched requests intentionally aborted by configuration.</param>
/// <param name="InformationalResponses">Matched responses classified from status codes 100 through 199.</param>
/// <param name="SuccessResponses">Matched responses classified from status codes 200 through 299.</param>
/// <param name="RedirectionResponses">Matched responses classified from status codes 300 through 399.</param>
/// <param name="ClientErrorResponses">Matched responses classified from status codes 400 through 499.</param>
/// <param name="ServerErrorResponses">Matched responses classified from status codes 500 through 599.</param>
/// <param name="ResponseBytes">Completed response body bytes, excluding failed writes, aborts, and <c>HEAD</c> bodies.</param>
/// <param name="RecentMinutes">Exactly 60 UTC minute buckets in ascending time order.</param>
/// <param name="Endpoints">Bounded endpoint statistics ordered by stable endpoint ID.</param>
/// <param name="RecentRequests">At most 100 privacy-safe matched-endpoint request summaries in newest-first order.</param>
public sealed record RequestStatisticsSnapshot(
    long TotalRequests,
    long MatchedRequests,
    long UnmatchedRequests,
    long FailedWrites,
    long AbortedConnections,
    long InformationalResponses,
    long SuccessResponses,
    long RedirectionResponses,
    long ClientErrorResponses,
    long ServerErrorResponses,
    long ResponseBytes,
    ImmutableArray<MinuteBucketSnapshot> RecentMinutes,
    ImmutableArray<EndpointStatisticsSnapshot> Endpoints,
    ImmutableArray<RequestLogEntrySnapshot> RecentRequests);

/// <summary>Identifies the observable outcome of one request summary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RequestLogOutcome>))]
public enum RequestLogOutcome
{
    /// <summary>The configured HTTP response completed.</summary>
    [JsonStringEnumMemberName("response")]
    Response,

    /// <summary>No enabled endpoint matched and the dispatcher returned <c>404</c>.</summary>
    [JsonStringEnumMemberName("unmatched")]
    Unmatched,

    /// <summary>The configured response failed while being written.</summary>
    [JsonStringEnumMemberName("failedWrite")]
    FailedWrite,

    /// <summary>The configured endpoint intentionally aborted the connection.</summary>
    [JsonStringEnumMemberName("aborted")]
    Aborted
}

/// <summary>Represents privacy-safe metadata for one recently completed dispatch.</summary>
/// <param name="TimestampUtc">The UTC completion timestamp.</param>
/// <param name="Method">The normalized HTTP method.</param>
/// <param name="Path">The normalized request path without query values.</param>
/// <param name="EndpointId">The stable matched endpoint ID, or <see langword="null"/> when unmatched.</param>
/// <param name="StatusCode">The HTTP status code, or <see langword="null"/> when the connection was aborted.</param>
/// <param name="Outcome">The dispatch outcome.</param>
/// <param name="ResponseBytes">The number of completed response body bytes.</param>
/// <param name="TransportAttempts">The number of physical dispatches coalesced into this logical request.</param>
public sealed record RequestLogEntrySnapshot(
    DateTimeOffset TimestampUtc,
    string Method,
    string Path,
    Guid? EndpointId,
    int? StatusCode,
    RequestLogOutcome Outcome,
    long ResponseBytes,
    int TransportAttempts = 1);

/// <summary>Represents immutable statistics retained for one stable endpoint ID.</summary>
/// <param name="EndpointId">The stable endpoint ID.</param>
/// <param name="TotalRequests">Matched logical requests attributed to the endpoint, including aborts and failed writes.</param>
/// <param name="LastRequestUtc">The latest recorded logical-request time in UTC, or <see langword="null"/> when none was recorded.</param>
/// <param name="LastStatusCode">The latest recorded status code, or <see langword="null"/> when the latest request aborted.</param>
/// <param name="ResponseBytes">Completed response body bytes attributed to the endpoint.</param>
/// <param name="RecentMinutes">Exactly 60 UTC minute buckets in ascending time order.</param>
public sealed record EndpointStatisticsSnapshot(
    Guid EndpointId,
    long TotalRequests,
    DateTimeOffset? LastRequestUtc,
    int? LastStatusCode,
    long ResponseBytes,
    ImmutableArray<MinuteBucketSnapshot> RecentMinutes);

/// <summary>Represents logical-request and response-status counts for one UTC minute.</summary>
/// <param name="MinuteUtc">The inclusive UTC start of the minute.</param>
/// <param name="Requests">The number of recorded logical requests, saturating at <see cref="uint.MaxValue"/>.</param>
/// <param name="InformationalResponses">Responses with status codes 100 through 199.</param>
/// <param name="SuccessResponses">Responses with status codes 200 through 299.</param>
/// <param name="RedirectionResponses">Responses with status codes 300 through 399.</param>
/// <param name="ClientErrorResponses">Responses with status codes 400 through 499, including unmatched requests.</param>
/// <param name="ServerErrorResponses">Responses with status codes 500 through 599.</param>
public readonly record struct MinuteBucketSnapshot(
    DateTimeOffset MinuteUtc,
    long Requests,
    long InformationalResponses,
    long SuccessResponses,
    long RedirectionResponses,
    long ClientErrorResponses,
    long ServerErrorResponses);
