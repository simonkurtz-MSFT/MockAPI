using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;
using Xunit.Abstractions;

namespace MockAPI.Tests.Management;

public sealed class StatisticsDeliveryDiagnostics(ITestOutputHelper output)
{
    // Opt in with MOCKAPI_STATISTICS_DIAGNOSTICS=1, Release, DOTNET_TieredCompilation=0,
    // and --logger "console;verbosity=detailed". Fixture creation is not measured.
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void MeasureSnapshotAndDeliveryAllocations()
    {
        if (Environment.GetEnvironmentVariable("MOCKAPI_STATISTICS_DIAGNOSTICS") != "1")
        {
            return;
        }

        var collector = StatisticsDeliveryTests.CreateCollector(RequestStatisticsCollector.MaximumTrackedEndpoints);
        var cache = new StatisticsSnapshotCache(collector);
        var events = new DashboardEventStream(new ConfigurationState(), cache, new StatisticsDeliveryTests.Clock());
        var getContext = new DefaultHttpContext();
        getContext.Response.Body = Stream.Null;
        var compact = JsonSerializer.Serialize(collector.GetSnapshot(), StatisticsDeliveryTests.CompactJson.RequestStatisticsSnapshot);
        output.WriteLine($"Runtime={Environment.Version}; endpoints={RequestStatisticsCollector.MaximumTrackedEndpoints}; compactJsonChars={compact.Length}");
        Measure("uncached snapshot + compact serialization (reference)", () =>
            GC.KeepAlive(JsonSerializer.Serialize(collector.GetSnapshot(), StatisticsDeliveryTests.CompactJson.RequestStatisticsSnapshot)));
        Measure("idle dashboard compact JSON", () => GC.KeepAlive(events.GetStatisticsJson()));
        Measure("idle GET -> Stream.Null", () => Complete(ManagementApiEndpoints.WriteStatisticsAsync(getContext, cache)));
        Measure("idle standalone SSE, one event -> counting sink", () => WriteStream(dashboard: false));
        Measure("idle dashboard SSE, initial events -> counting sink", () => WriteStream(dashboard: true));
        Measure("changed dashboard compact JSON", () =>
        {
            collector.RecordUnmatched();
            GC.KeepAlive(events.GetStatisticsJson());
        });

        void WriteStream(bool dashboard)
        {
            using var cancellation = new CancellationTokenSource();
            using var sink = new CancelOnFlushSink(cancellation);
            var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
            context.Response.Body = sink;
            Complete(dashboard ? events.WriteAsync(context) : ManagementApiEndpoints.StreamStatisticsAsync(context, cache));
        }
    }

    private void Measure(string name, Action operation)
    {
        const int iterations = 10;
        const int samples = 5;
        for (var index = 0; index < 10; index++)
        {
            operation();
        }
        var allocations = new long[samples];
        var elapsed = new double[samples];
        for (var sample = 0; sample < samples; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var index = 0; index < iterations; index++)
            {
                operation();
            }
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }
        output.WriteLine($"{name}: bytes/op=[{string.Join(", ", allocations)}]; ms/op=[{string.Join(", ", elapsed.Select(value => value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)))}]; iterations={iterations}; samples={samples}");
    }

    private static void Complete(Task operation)
    {
        Assert.True(operation.IsCompleted, "Thread-local allocation measurement requires synchronous sink completion.");
        operation.GetAwaiter().GetResult();
    }

    private sealed class CancelOnFlushSink(CancellationTokenSource cancellation) : Stream
    {
        private long _bytes;
        private long _lastFlushedBytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_bytes > 0 && _bytes == _lastFlushedBytes)
            {
                cancellation.Cancel();
            }
            _lastFlushedBytes = _bytes;
            return Task.CompletedTask;
        }
        public override void Write(byte[] buffer, int offset, int count) => _bytes += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _bytes += buffer.Length;
            return ValueTask.CompletedTask;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
