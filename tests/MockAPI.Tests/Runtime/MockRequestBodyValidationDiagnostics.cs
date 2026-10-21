using System.Diagnostics;
using MockAPI.Runtime;
using Xunit.Abstractions;

namespace MockAPI.Tests.Runtime;

public sealed class MockRequestBodyValidationDiagnostics(ITestOutputHelper output)
{
    // Opt in with MOCKAPI_BODY_VALIDATION_DIAGNOSTICS=1 in Release with DOTNET_TieredCompilation=0.
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void MeasureValidationAllocationsAndElapsedTime()
    {
        if (Environment.GetEnvironmentVariable("MOCKAPI_BODY_VALIDATION_DIAGNOSTICS") != "1")
        {
            return;
        }

        output.WriteLine($"Runtime={Environment.Version}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; synchronous memory streams, no network");
        foreach (var scenario in new[] { "empty", "text", "json", "large-text", "large-json" })
        {
            foreach (var knownLength in new[] { true, false })
            {
                using var body = new MemoryStream(MockRequestBodyValidatorTests.CreateBody(scenario));
                var request = MockRequestBodyValidatorTests.CreateRequest(body,
                    scenario.Contains("json", StringComparison.Ordinal) ? "application/json" : "text/plain",
                    knownLength ? body.Length : null);
                Measure($"{scenario}; knownLength={knownLength}", () =>
                {
                    body.Position = 0;
                    var validation = MockRequestBodyValidator.ValidateAsync(request);
                    Assert.True(validation.IsCompleted, "Thread-local allocation measurements require synchronous completion.");
                    Assert.Null(validation.GetAwaiter().GetResult());
                });
            }
        }
    }

    private void Measure(string name, Action operation)
    {
        const int iterations = 30;
        const int samples = 3;
        for (var iteration = 0; iteration < 10; iteration++)
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
            // Collections can trim ArrayPool caches; measure steady-state reuse, not pool refill.
            operation();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                operation();
            }
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        }

        output.WriteLine($"{name}: bytes/op=[{string.Join(", ", allocations)}]; ms/op=[{string.Join(", ", elapsed.Select(value => value.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)))}]; iterations={iterations}; samples={samples}");
    }
}
