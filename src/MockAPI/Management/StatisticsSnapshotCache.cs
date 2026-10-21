using System.Text.Json;
using MockAPI.Runtime;

namespace MockAPI.Management;

/// <summary>Shares one immutable statistics view and its compact SSE serialization across management readers.</summary>
internal sealed class StatisticsSnapshotCache(RequestStatisticsCollector statistics)
{
    private static readonly ManagementJsonContext CompactJsonContext = new(
        new JsonSerializerOptions(ManagementJsonContext.Default.Options) { WriteIndented = false });
    private readonly object _gate = new();
    private (long Version, long Minute) _key;
    private RequestStatisticsSnapshot? _snapshot;
    private string? _compactJson;
    private long _resetVersion;

    internal (RequestStatisticsSnapshot Snapshot, long ResetVersion) GetView()
    {
        lock (_gate)
        {
            return (GetSnapshotCore(), _resetVersion);
        }
    }

    internal string GetCompactJson(RequestStatisticsSnapshot snapshot)
    {
        lock (_gate)
        {
            if (ReferenceEquals(snapshot, _snapshot))
            {
                return _compactJson ??= JsonSerializer.Serialize(snapshot, CompactJsonContext.RequestStatisticsSnapshot);
            }
            return JsonSerializer.Serialize(snapshot, CompactJsonContext.RequestStatisticsSnapshot);
        }
    }

    internal RequestStatisticsSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return GetSnapshotCore();
        }
    }

    internal string GetCompactJson()
    {
        lock (_gate)
        {
            var snapshot = GetSnapshotCore();
            return _compactJson ??= JsonSerializer.Serialize(snapshot, CompactJsonContext.RequestStatisticsSnapshot);
        }
    }

    private RequestStatisticsSnapshot GetSnapshotCore()
    {
        var now = statistics.UtcNow;
        var key = (statistics.Version, now.ToUnixTimeSeconds() / 60);
        if (_snapshot is null || _key != key)
        {
            // Capture the version BEFORE reading counters: a recording/reset that finishes
            // during capture must invalidate the next read, not label a partial view as current.
            // Only readers take this gate; serialization never holds a recorder's lock.
            _resetVersion = statistics.ResetVersion;
            _snapshot = statistics.GetSnapshot(now);
            _key = key;
            _compactJson = null;
        }
        return _snapshot;
    }
}
