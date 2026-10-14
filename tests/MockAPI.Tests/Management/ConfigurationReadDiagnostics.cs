using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using MockAPI.Configuration;
using MockAPI.Management;
using Xunit.Abstractions;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationReadDiagnostics(ITestOutputHelper output)
{
    // Opt in with MOCKAPI_CONFIGURATION_READ_DIAGNOSTICS=1 and run this class in Release
    // with DOTNET_TieredCompilation=0 and --logger "console;verbosity=detailed".
    // No timing/allocation thresholds: this measures controlling paths, not network throughput.
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void MeasureStatusProjectionStatusHandlerAndNativeExport()
    {
        if (Environment.GetEnvironmentVariable("MOCKAPI_CONFIGURATION_READ_DIAGNOSTICS") != "1")
        {
            return;
        }

        var state = ConfigurationReadTests.CreateState("text", bodyLength: 166000, endpointCount: ConfigurationLimits.MaximumEndpoints);
        var snapshot = state.Current;
        var size = snapshot.ExportUtf8().Length;
        output.WriteLine($"Runtime={Environment.Version}; endpoints={ConfigurationLimits.MaximumEndpoints}; documentBytes={size}; limitBytes={ConfigurationLimits.MaximumDocumentBytes}");
        var statusService = new EndpointManagementService(state);
        var exportService = new ConfigurationManagementService(state, new ConfigurationReadTests.UnusedStore());
        var statusContext = new DefaultHttpContext();
        statusContext.Response.Body = Stream.Null;
        var exportContext = new DefaultHttpContext();
        exportContext.Response.Body = Stream.Null;

        Measure("status-projection (also used by SSE)", () => GC.KeepAlive(ConfigurationStatusResponse.FromSnapshot(snapshot)));
        Measure("status-handler -> Stream.Null", () => CompleteSynchronously(ConfigurationReadTests.WriteStatus(statusContext, statusService)));
        Measure("native-export omitted IDs -> Stream.Null", () => CompleteSynchronously(ConfigurationReadTests.WriteExport(exportContext, exportService, null, null, true)));
        Measure("native-export empty IDs -> Stream.Null", () => CompleteSynchronously(ConfigurationReadTests.WriteExport(exportContext, exportService, [], null, true)));
    }

    private void Measure(string name, Action operation)
    {
        const int iterations = 30;
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

    private static void CompleteSynchronously(Task operation)
    {
        // Thread-local allocation measurement is valid only while all work stays on this thread.
        Assert.True(operation.IsCompleted, "The diagnostic sink must complete synchronously.");
        operation.GetAwaiter().GetResult();
    }
}
