using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MockAPI.Configuration;
using Xunit.Abstractions;

namespace MockAPI.Tests.Configuration;

public sealed class ConfigurationReplacementDiagnosticsTests(ITestOutputHelper output)
{
    private const int MeasurementIterations = 3;
    private const int ResponseBodyBytes = 960 * 1024;

    [Fact]
    public void TryReplace_NearLimitConfiguration_ReportsCurrentMetrics()
    {
        var candidate = CreateNearLimitDocument();
        var serializedLength = JsonSerializer.SerializeToUtf8Bytes(
            candidate,
            MockApiJsonContext.Default.MockApiConfigurationDocument).Length;
        Assert.InRange(
            serializedLength,
            ConfigurationLimits.MaximumDocumentBytes * 9 / 10,
            ConfigurationLimits.MaximumDocumentBytes);

        var warmupState = new ConfigurationState();
        Assert.Equal(
            ConfigurationUpdateStatus.Applied,
            warmupState.TryReplace(candidate, expectedRevision: 0).Status);

        var state = new ConfigurationState();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var iteration = 0; iteration < MeasurementIterations; iteration++)
        {
            var result = state.TryReplace(candidate, state.Current.Revision);
            Assert.Equal(ConfigurationUpdateStatus.Applied, result.Status);
        }
        stopwatch.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        output.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Configuration replacement diagnostic: documentBytes={serializedLength}, iterations={MeasurementIterations}, allocatedBytes={allocatedBytes}, allocatedBytesPerReplacement={allocatedBytes / MeasurementIterations}, elapsedMilliseconds={stopwatch.Elapsed.TotalMilliseconds:F3}, elapsedMillisecondsPerReplacement={stopwatch.Elapsed.TotalMilliseconds / MeasurementIterations:F3}"));
    }

    private static MockApiConfigurationDocument CreateNearLimitDocument()
    {
        var body = new string('x', ResponseBodyBytes);
        return new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            Endpoints = Enumerable.Range(0, 4)
                .Select(index => new MockEndpointDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = $"Near-limit endpoint {index}",
                    Enabled = true,
                    Methods = ["GET"],
                    Path = $"/near-limit-{index}",
                    Response = new MockResponseDefinition
                    {
                        StatusCode = 200,
                        Headers = [],
                        ContentType = "text/plain; charset=utf-8",
                        Body = body
                    }
                })
                .ToArray()
        };
    }
}
