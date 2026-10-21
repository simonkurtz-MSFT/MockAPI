using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;
using Xunit.Abstractions;

namespace MockAPI.Tests.Runtime;

public sealed class BackendRefactoringDiagnostics(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Diagnostic")]
    public async Task MeasureBackendAllocationBundle()
    {
        if (Environment.GetEnvironmentVariable("MOCKAPI_BACKEND_DIAGNOSTICS") != "1")
        {
            return;
        }

        output.WriteLine($"Runtime={Environment.Version}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        var body = new string('x', 960 * 1024);
        var document = CompiledResponseReuseTests.CreateDocument(Enumerable.Range(0, 4)
            .Select(index => new MockEndpointDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"Endpoint {index}",
                Path = $"/endpoint-{index}",
                Methods = ["GET"],
                Enabled = true,
                Response = new() { StatusCode = 200, Headers = [], ContentType = "text/plain", Body = body }
            }).ToArray());
        var registry = new EndpointRegistry();
        var state = new ConfigurationState();
        Measure("unchanged near-limit registry replacement", () => Assert.True(registry.TryReplace(document).IsValid));
        Measure("unchanged near-limit full configuration replacement", () =>
            Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(document, state.Current.Revision).Status));
        var selected = document.Endpoints[0] with
        {
            Id = Guid.NewGuid(),
            Path = "/selected",
            Response = new() { StatusCode = 200, Headers = [], ContentType = "text/plain", Body = "small" }
        };
        var selectionDocument = CompiledResponseReuseTests.CreateDocument([.. document.Endpoints, selected]);
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(selectionDocument, state.Current.Revision).Status);
        var snapshot = state.Current;
        var selectedId = selected.Id;
        Measure("single endpoint via full document", () =>
            GC.KeepAlive(snapshot.GetDocument().Endpoints.Single(endpoint => endpoint.Id == selectedId)));
        Measure("single endpoint via targeted canonical read", () => GC.KeepAlive(snapshot.GetEndpoint(selectedId)));

        var ids = Enumerable.Range(0, RequestStatisticsCollector.MaximumTrackedEndpoints)
            .Select(_ => Guid.NewGuid()).ToArray();
        Measure("populate 256 endpoint counters", () =>
        {
            var collector = new RequestStatisticsCollector();
            foreach (var id in ids)
            {
                collector.RecordMatched(id, 200, 0);
            }
            GC.KeepAlive(collector);
        });

        var directory = Path.Combine(Path.GetTempPath(), "mockapi-backend-diagnostics", Guid.NewGuid().ToString("N"));
        try
        {
            var key = new string('a', 43);
            using var security = new ApiKeySecurity(new MockApiOptions
            {
                ConfigurationPath = Path.Combine(directory, "mockapi.json"),
                ApiKey = key
            });
            await security.LoadAsync(CancellationToken.None);
            var context = new DefaultHttpContext();
            context.Request.Headers[ApiKeySecurity.HeaderName] = key;
            Measure("valid API-key verification", () => Assert.True(security.Authorizes(context)), iterations: 1_000);
            context.Request.Headers[ApiKeySecurity.HeaderName] = new string('b', 43);
            Measure("invalid API-key verification", () => Assert.False(security.Authorizes(context)), iterations: 1_000);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private void Measure(string name, Action operation, int iterations = 10)
    {
        for (var index = 0; index < 10; index++)
        {
            operation();
        }
        var allocations = new long[5];
        var elapsed = new double[5];
        for (var sample = 0; sample < allocations.Length; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                operation();
            }
            allocations[sample] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            elapsed[sample] = Stopwatch.GetElapsedTime(start).TotalMilliseconds / iterations;
        }
        output.WriteLine($"{name}: bytes/op=[{string.Join(", ", allocations)}]; ms/op=[{string.Join(", ", elapsed.Select(value => value.ToString("F6", CultureInfo.InvariantCulture)))}]; iterations={iterations}");
    }
}
