namespace MockAPI.Runtime;

/// <summary>Tracks accepted requests within one endpoint's process-local rolling window.</summary>
internal sealed class RollingWindowPermitBudget(int requestLimit, TimeSpan window, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Queue<long> _acceptedTimestamps = new();

    internal bool TryAcquire()
    {
        lock (_gate)
        {
            // Monotonic timestamps expire all elapsed permits, even after a completely idle window.
            // No timer roots retired configuration snapshots; retained entries never exceed the limit.
            var now = clock.GetTimestamp();
            while (_acceptedTimestamps.TryPeek(out var oldest) &&
                   clock.GetElapsedTime(oldest, now) >= window)
            {
                _acceptedTimestamps.Dequeue();
            }

            if (_acceptedTimestamps.Count >= requestLimit)
            {
                return false;
            }

            _acceptedTimestamps.Enqueue(now);
            return true;
        }
    }
}
