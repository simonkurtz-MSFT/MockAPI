using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using MockAPI.Runtime;

namespace MockAPI.Management;

/// <summary>Shares interval state patches instead of resending every endpoint's sixty-minute history.</summary>
/// <remarks>
/// This is not request-event replay: each patch compares two bounded snapshots. The cache owns only the current
/// frame, so slow clients recover with a full baseline instead of requiring an ever-growing delivery queue.
/// </remarks>
internal sealed class StatisticsDeltaCache(StatisticsSnapshotCache statistics, TimeProvider clock)
{
    private readonly object _gate = new();
    private StatisticsDeltaFrame? _current;
    private DateTimeOffset _lastCapture;
    private DateTimeOffset _nextCapture;

    internal StatisticsDeltaFrame GetFrame()
    {
        // Coalesce comparison work across clients once per interval. Request recording never takes this gate.
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_current is not null && now >= _lastCapture && now < _nextCapture)
            {
                return _current;
            }

            var view = statistics.GetView();
            _lastCapture = now;
            _nextCapture = now.AddSeconds(2);
            if (_current is not null && ReferenceEquals(view.Snapshot, _current.Snapshot))
            {
                return _current;
            }

            var sequence = (_current?.Sequence ?? 0) + 1;
            // Resets invalidate the client's accumulated state even when the resulting totals happen to match.
            var delta = _current is not null && _current.ResetVersion == view.ResetVersion
                ? StatisticsDelta.Create(_current.Snapshot, view.Snapshot, _current.Sequence, sequence)
                : null;
            // Retain one frame, not a replay queue or a chain of prior snapshots.
            _current = new StatisticsDeltaFrame(statistics, view.Snapshot, view.ResetVersion, sequence, delta);
            return _current;
        }
    }
}

// Each frame owns its captured snapshot and lazily shares its JSON across clients. Keeping serialization here
// avoids per-client payload allocations and lets an in-flight write finish safely after the cache advances.
internal sealed class StatisticsDeltaFrame(
    StatisticsSnapshotCache cache,
    RequestStatisticsSnapshot snapshot,
    long resetVersion,
    long sequence,
    StatisticsDelta? delta)
{
    private readonly object _gate = new();
    private string? _snapshotJson;
    private string? _deltaJson;

    internal RequestStatisticsSnapshot Snapshot { get; } = snapshot;
    internal long ResetVersion { get; } = resetVersion;
    internal long Sequence { get; } = sequence;
    internal StatisticsDelta? Delta { get; } = delta;

    internal string GetSnapshotJson()
    {
        lock (_gate)
        {
            return _snapshotJson ??= cache.GetCompactJson(Snapshot);
        }
    }

    internal string GetDeltaJson()
    {
        lock (_gate)
        {
            return _deltaJson ??= JsonSerializer.Serialize(
                Delta ?? throw new InvalidOperationException("A baseline frame has no statistics delta."),
                StatisticsDeltaJsonContext.Default.StatisticsDelta);
        }
    }
}

/// <summary>Absolute state replacements that require the client's exact <see cref="BaseSequence"/>.</summary>
/// <remarks>
/// Aggregate totals are always included; endpoint summaries and minute buckets are sent only when changed.
/// Omission means unchanged, not zero. The client advances every history to WindowStartUtc and fills new
/// minutes with zeros. RecentRequests is a complete bounded replacement when present, including an empty array.
/// </remarks>
internal sealed record StatisticsDelta(
    long BaseSequence,
    long Sequence,
    DateTimeOffset WindowStartUtc,
    StatisticsTotals Totals,
    ImmutableArray<MinuteBucketSnapshot> RecentMinutes,
    ImmutableArray<EndpointStatisticsDelta> Endpoints,
    ImmutableArray<Guid> RemovedEndpointIds,
    ImmutableArray<RequestLogEntrySnapshot>? RecentRequests)
{
    internal static StatisticsDelta? Create(
        RequestStatisticsSnapshot previous, RequestStatisticsSnapshot current, long baseSequence, long sequence)
    {
        // UTC-minimum clamping can produce duplicate minute identities. Send a baseline rather than an ambiguous patch.
        if (current.RecentMinutes[0].MinuteUtc == current.RecentMinutes[1].MinuteUtc ||
            previous.RecentMinutes[0].MinuteUtc == previous.RecentMinutes[1].MinuteUtc)
        {
            return null;
        }

        var oldEndpoints = previous.Endpoints.ToDictionary(endpoint => endpoint.EndpointId);
        var changed = ImmutableArray.CreateBuilder<EndpointStatisticsDelta>();
        foreach (var endpoint in current.Endpoints)
        {
            oldEndpoints.Remove(endpoint.EndpointId, out var old);
            var minutes = ChangedMinutes(old?.RecentMinutes ?? [], endpoint.RecentMinutes);
            if (old is null || endpoint.TotalRequests != old.TotalRequests || endpoint.LastRequestUtc != old.LastRequestUtc ||
                endpoint.LastStatusCode != old.LastStatusCode || endpoint.ResponseBytes != old.ResponseBytes || !minutes.IsEmpty)
            {
                changed.Add(new EndpointStatisticsDelta(endpoint.EndpointId, endpoint.TotalRequests, endpoint.LastRequestUtc,
                    endpoint.LastStatusCode, endpoint.ResponseBytes, minutes));
            }
        }

        // Retry coalescing can update an existing request summary without appending a request. Replace the bounded
        // feed on any change rather than treating it as append-only or retaining raw request events for replay.
        return new StatisticsDelta(baseSequence, sequence, current.RecentMinutes[0].MinuteUtc,
            StatisticsTotals.FromSnapshot(current), ChangedMinutes(previous.RecentMinutes, current.RecentMinutes),
            changed.ToImmutable(), [.. oldEndpoints.Keys],
            previous.RecentRequests.SequenceEqual(current.RecentRequests) ? null : current.RecentRequests);
    }

    private static ImmutableArray<MinuteBucketSnapshot> ChangedMinutes(
        ImmutableArray<MinuteBucketSnapshot> previous, ImmutableArray<MinuteBucketSnapshot> current)
    {
        var changed = ImmutableArray.CreateBuilder<MinuteBucketSnapshot>();
        // Match UTC minutes across sliding windows, not array positions; backward clock changes can make this negative.
        var offset = previous.IsEmpty ? 0 : (current[0].MinuteUtc - previous[0].MinuteUtc).Ticks / TimeSpan.TicksPerMinute;
        for (var index = 0; index < current.Length; index++)
        {
            var oldIndex = index + offset;
            var bucket = current[index];
            if (previous.IsEmpty)
            {
                // New endpoint histories start at zero on the client; only nonzero buckets need transmission.
                if (bucket.Requests != 0 || bucket.InformationalResponses != 0 || bucket.SuccessResponses != 0 ||
                    bucket.RedirectionResponses != 0 || bucket.ClientErrorResponses != 0 || bucket.ServerErrorResponses != 0)
                {
                    changed.Add(bucket);
                }
            }
            else if (oldIndex < 0 || oldIndex >= previous.Length || previous[(int)oldIndex] != bucket)
            {
                changed.Add(bucket);
            }
        }
        return changed.ToImmutable();
    }
}

internal sealed record EndpointStatisticsDelta(
    Guid EndpointId, long TotalRequests, DateTimeOffset? LastRequestUtc, int? LastStatusCode,
    long ResponseBytes, ImmutableArray<MinuteBucketSnapshot> RecentMinutes);

internal sealed record StatisticsTotals(
    long TotalRequests, long MatchedRequests, long UnmatchedRequests, long FailedWrites, long AbortedConnections,
    long InformationalResponses, long SuccessResponses, long RedirectionResponses, long ClientErrorResponses,
    long ServerErrorResponses, long ResponseBytes)
{
    internal static StatisticsTotals FromSnapshot(RequestStatisticsSnapshot snapshot) => new(
        snapshot.TotalRequests, snapshot.MatchedRequests, snapshot.UnmatchedRequests, snapshot.FailedWrites,
        snapshot.AbortedConnections, snapshot.InformationalResponses, snapshot.SuccessResponses,
        snapshot.RedirectionResponses, snapshot.ClientErrorResponses, snapshot.ServerErrorResponses, snapshot.ResponseBytes);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatisticsDelta))]
internal sealed partial class StatisticsDeltaJsonContext : JsonSerializerContext;
