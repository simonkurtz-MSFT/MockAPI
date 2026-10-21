using System.Diagnostics;
using MockAPI.Runtime;
using Xunit.Abstractions;

namespace MockAPI.Tests.Runtime;

public sealed class StatisticsRecordingDiagnostics(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void MeasureConcurrentRecording()
    {
        if (Environment.GetEnvironmentVariable("MOCKAPI_RECORDING_DIAGNOSTICS") != "1")
        {
            return;
        }

        output.WriteLine($"Runtime={Environment.Version}; processors={Environment.ProcessorCount}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        foreach (var matched in new[] { false, true })
        {
            foreach (var workers in new[] { 1, 2, 4, 8 })
            {
                var samples = Enumerable.Range(0, 5).Select(_ => Measure(matched, workers)).ToArray();
                output.WriteLine($"{(matched ? "matched with bounded request log" : "unmatched without request ID")}; workers={workers}; requests/s=[{string.Join(", ", samples.Select(sample => sample.Rate))}]; monitorContentions=[{string.Join(", ", samples.Select(sample => sample.Contentions))}]");
            }
        }
    }

    private static (long Rate, long Contentions) Measure(bool matched, int workers)
    {
        const int totalRequests = 120_000;
        var collector = new RequestStatisticsCollector();
        var ids = Enumerable.Range(0, workers).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            collector.RecordMatched(id, "GET", "/diagnostic", 200, 0);
        }
        using var ready = new CountdownEvent(workers);
        using var start = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, workers).Select(worker => new Thread(() =>
        {
            ready.Signal();
            start.Wait();
            for (var request = 0; request < totalRequests / workers; request++)
            {
                if (matched) collector.RecordMatched(ids[worker], "GET", "/diagnostic", 200, 0);
                else collector.RecordUnmatched();
            }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        ready.Wait();
        var contentions = Monitor.LockContentionCount;
        var timestamp = Stopwatch.GetTimestamp();
        start.Set();
        foreach (var thread in threads) thread.Join();
        var elapsed = Stopwatch.GetElapsedTime(timestamp);
        contentions = Monitor.LockContentionCount - contentions;
        Assert.Equal(totalRequests + workers, collector.GetSnapshot().TotalRequests);
        return ((long)(totalRequests / elapsed.TotalSeconds), contentions);
    }
}
