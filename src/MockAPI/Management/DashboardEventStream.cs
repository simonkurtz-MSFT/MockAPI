using System.Text.Json;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Management;

/// <summary>Streams configuration invalidations and coalesced statistics without per-client event queues.</summary>
public sealed class DashboardEventStream
{
    private static readonly ManagementJsonContext JsonContext = new(
        new JsonSerializerOptions(ManagementJsonContext.Default.Options) { WriteIndented = false });
    private readonly ConfigurationState _configuration;
    private readonly StatisticsSnapshotCache _statistics;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a dashboard stream with a bounded statistics cache shared by its connections.</summary>
    /// <param name="configuration">The configuration source for invalidation events.</param>
    /// <param name="statistics">The collector whose UTC clock determines statistics minute rollover.</param>
    /// <param name="timeProvider">The stream timer and keepalive clock, or <see langword="null"/> for the system clock.</param>
    public DashboardEventStream(
        ConfigurationState configuration,
        RequestStatisticsCollector statistics,
        TimeProvider? timeProvider = null)
        : this(configuration, new StatisticsSnapshotCache(statistics), timeProvider)
    {
    }

    internal DashboardEventStream(
        ConfigurationState configuration,
        StatisticsSnapshotCache statistics,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _statistics = statistics;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Writes initial state, immediate configuration changes, and at most one statistics update every two seconds.</summary>
    /// <param name="context">The authenticated management request; disconnecting cancels all stream work.</param>
    /// <returns>A task that completes when the client disconnects.</returns>
    /// <remarks>Reconnects receive current state, not replayed history. Idle streams send keepalive comments.</remarks>
    public async Task WriteAsync(HttpContext context)
    {
        var cancellation = context.RequestAborted;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _timeProvider);
        ConfigurationStateSnapshot? observed = null;
        string? sentStatistics = null;
        var lastWrite = _timeProvider.GetUtcNow();
        Task tick = Task.CompletedTask;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var current = _configuration.Current;
                var wrote = false;
                if (!ReferenceEquals(observed, current))
                {
                    var status = ConfigurationStatusResponse.FromSnapshot(current);
                    await ManagementEventWriter.WriteAsync(context, "configuration", JsonSerializer.Serialize(status, JsonContext.ConfigurationStatusResponse));
                    observed = current;
                    wrote = true;
                }

                if (tick.IsCompleted)
                {
                    await tick;
                    var json = GetStatisticsJson();
                    if (!ReferenceEquals(sentStatistics, json))
                    {
                        await ManagementEventWriter.WriteAsync(context, "statistics", json);
                        sentStatistics = json;
                        wrote = true;
                    }
                    tick = timer.WaitForNextTickAsync(cancellation).AsTask();
                }

                if (!wrote && _timeProvider.GetUtcNow() - lastWrite >= TimeSpan.FromSeconds(15))
                {
                    await context.Response.WriteAsync(": keepalive\n\n", cancellation);
                    wrote = true;
                }
                if (wrote)
                {
                    await context.Response.Body.FlushAsync(cancellation);
                    lastWrite = _timeProvider.GetUtcNow();
                }

                await Task.WhenAny(_configuration.WaitForChangeAsync(observed), tick);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A disconnected client is the normal end of an event stream.
        }
    }

    internal string GetStatisticsJson() => _statistics.GetCompactJson();
}
