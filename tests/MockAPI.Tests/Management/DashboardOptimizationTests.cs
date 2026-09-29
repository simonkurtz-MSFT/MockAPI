using System.Net;
using System.Net.Http.Headers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

namespace MockAPI.Tests.Management;

public sealed class DashboardOptimizationTests
{
    [Theory]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    [InlineData("/dashboard-sync.js")]
    [InlineData("/dashboard-management.js")]
    [InlineData("/dashboard-dom.js")]
    [InlineData("/dashboard-editor-dialog.js")]
    [InlineData("/dashboard-api-description.js")]
    [InlineData("/dashboard-endpoint-table.js")]
    [InlineData("/dashboard-layout.js")]
    [InlineData("/dashboard-statistics.js")]
    [InlineData("/dashboard-test-blade.js")]
    public async Task UnversionedAssets_RevalidateWithContentETagsAndNeverUseSharedCaching(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var first = await client.GetAsync(path);
        Assert.True(first.Headers.CacheControl!.Private);
        Assert.True(first.Headers.CacheControl.NoCache);
        Assert.NotNull(first.Headers.ETag);
        Assert.False(first.Headers.ETag.IsWeak);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, path);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag);
        using var cached = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
        Assert.Equal(first.Headers.ETag, cached.Headers.ETag);

        using var outdated = new HttpRequestMessage(HttpMethod.Get, path);
        outdated.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"old-content\""));
        using var updated = await client.SendAsync(outdated);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.NotEmpty(await updated.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task VersionedAssets_UseOneContentVersionAndPrivateImmutableCaching()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var dashboard = await client.GetAsync("/");
        var html = await dashboard.Content.ReadAsStringAsync();
        var versionMatch = Regex.Match(html, "/app\\.css\\?v=([A-F0-9]{64})");
        Assert.True(versionMatch.Success);
        var version = versionMatch.Groups[1].Value;
        Assert.Contains($"/app.js?v={version}", html, StringComparison.Ordinal);
        Assert.Contains($"/favicon.svg?v={version}", html, StringComparison.Ordinal);
        Assert.Contains($"/brand-mark.svg?v={version}", html, StringComparison.Ordinal);
        Assert.Contains($"/__mockapi/openapi-logo.svg?v={version}#", html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{ASSET_VERSION}}", html, StringComparison.Ordinal);

        using var stylesheet = await client.GetAsync($"/app.css?v={version}");
        using var script = await client.GetAsync($"/app.js?v={version}");
        AssertVersionedCacheControl(stylesheet.Headers.CacheControl);
        AssertVersionedCacheControl(script.Headers.CacheControl);
        var javascript = await script.Content.ReadAsStringAsync();
        Assert.Contains($"./dashboard-core.js?v={version}", javascript, StringComparison.Ordinal);
        Assert.Contains($"./dashboard-test-request.js?v={version}", javascript, StringComparison.Ordinal);
        Assert.Contains($"./dashboard-management.js?v={version}", javascript, StringComparison.Ordinal);
        Assert.DoesNotContain("{{ASSET_VERSION}}", javascript, StringComparison.Ordinal);

        using var management = await client.GetAsync($"/dashboard-management.js?v={version}");
        Assert.Equal(HttpStatusCode.OK, management.StatusCode);
        Assert.Equal("text/javascript", management.Content.Headers.ContentType!.MediaType);
        AssertVersionedCacheControl(management.Headers.CacheControl);

        foreach (var name in new[] { "dashboard-dom.js", "dashboard-editor-dialog.js", "dashboard-endpoint-table.js",
            "dashboard-layout.js", "dashboard-statistics.js", "dashboard-test-blade.js" })
        {
            Assert.Contains($"./{name}?v={version}", javascript, StringComparison.Ordinal);
            using var asset = await client.GetAsync($"/{name}?v={version}");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.Equal("text/javascript", asset.Content.Headers.ContentType!.MediaType);
            AssertVersionedCacheControl(asset.Headers.CacheControl);
            Assert.DoesNotContain("{{ASSET_VERSION}}", await asset.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var stale = await client.GetAsync("/app.css?v=stale");
        Assert.Equal("no-cache, private", stale.Headers.CacheControl!.ToString());
    }

    private static void AssertVersionedCacheControl(CacheControlHeaderValue? cacheControl)
    {
        Assert.NotNull(cacheControl);
        Assert.True(cacheControl.Private);
        Assert.Equal(TimeSpan.FromDays(365), cacheControl.MaxAge);
        Assert.Contains(cacheControl.Extensions, extension => extension.Name == "immutable");
    }

    [Theory]
    [InlineData("/__mockapi/api/configuration")]
    [InlineData("/__mockapi/api/endpoints")]
    [InlineData("/__mockapi/api/statistics")]
    public async Task AdministrativeJson_IsNotStoredEvenWithARevisionValidator(string path)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"0\""));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task DashboardStream_PublishesInitialStateEditsSaveAndStatistics()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__mockapi/api/dashboard/events");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
        await using var body = await response.Content.ReadAsStreamAsync(cancellation.Token);
        using var reader = new StreamReader(body);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("no", response.Headers.GetValues("X-Accel-Buffering").Single());
        using var initial = await ReadEventAsync(reader, "configuration", cancellation.Token);
        Assert.Equal(0, initial.RootElement.GetProperty("revision").GetInt64());
        using var initialStatistics = await ReadEventAsync(reader, "statistics", cancellation.Token);
        Assert.Equal(0, initialStatistics.RootElement.GetProperty("totalRequests").GetInt64());

        var result = state.TryReplace(new MockApiConfigurationDocument { SchemaVersion = "1.0", Endpoints = [] }, 0);
        Assert.Equal(ConfigurationUpdateStatus.Applied, result.Status);
        using var edit = await ReadEventAsync(reader, "configuration", cancellation.Token);
        Assert.Equal(1, edit.RootElement.GetProperty("revision").GetInt64());
        Assert.True(edit.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());
        Assert.True(state.TryMarkPersisted(1));
        using var save = await ReadEventAsync(reader, "configuration", cancellation.Token);
        Assert.Equal(1, save.RootElement.GetProperty("revision").GetInt64());
        Assert.False(save.RootElement.GetProperty("hasUnsavedChanges").GetBoolean());

        statistics.RecordUnmatched("GET", "/unmatched");
        using var activity = await ReadEventAsync(reader, "statistics", cancellation.Token);
        Assert.Equal(1, activity.RootElement.GetProperty("totalRequests").GetInt64());
        cancellation.Cancel();
    }

    [Fact]
    public void DashboardStatisticsCache_ReusesIdlePayloadAndInvalidatesOnEveryKindOfChange()
    {
        var clock = new Clock();
        var statistics = new RequestStatisticsCollector(clock);
        var events = new DashboardEventStream(new ConfigurationState(), statistics, clock);
        var initial = events.GetStatisticsJson();
        Assert.Same(initial, events.GetStatisticsJson());
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Same(initial, events.GetStatisticsJson());
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.NotSame(initial, events.GetStatisticsJson());

        var previous = events.GetStatisticsJson();
        void AssertInvalidated()
        {
            var current = events.GetStatisticsJson();
            Assert.NotSame(previous, current);
            Assert.Same(current, events.GetStatisticsJson());
            previous = current;
        }

        statistics.RecordUnmatched("GET", "/missing", "repeat");
        AssertInvalidated();
        statistics.RecordUnmatched("GET", "/missing", "repeat");
        AssertInvalidated();
        var endpointId = Guid.NewGuid();
        statistics.RecordMatched(endpointId, 200, 2);
        AssertInvalidated();
        statistics.RecordFailedWrite(endpointId, 500);
        AssertInvalidated();
        statistics.RecordAbortedConnection(endpointId);
        AssertInvalidated();
        Assert.True(statistics.Reset(endpointId));
        AssertInvalidated();
        statistics.Reset();
        AssertInvalidated();
    }

    [Fact]
    public async Task IdleDashboardStream_SendsOnlyKeepaliveUntilStatisticsChange()
    {
        // Advance the reported time on each read while retaining the real two-second timer.
        // The first idle tick stays below the heartbeat threshold; the next crosses it.
        var clock = new SteppingClock();
        var events = new DashboardEventStream(new ConfigurationState(), new RequestStatisticsCollector(), clock);
        var pipe = new Pipe();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        await using var writer = pipe.Writer.AsStream();
        await using var readerStream = pipe.Reader.AsStream();
        using var reader = new StreamReader(readerStream);
        context.Response.Body = writer;
        var writing = events.WriteAsync(context);
        using var configuration = await ReadEventAsync(reader, "configuration", cancellation.Token);
        using var statistics = await ReadEventAsync(reader, "statistics", cancellation.Token);
        Assert.Equal(": keepalive", await reader.ReadLineAsync(cancellation.Token));
        Assert.Equal(string.Empty, await reader.ReadLineAsync(cancellation.Token));
        cancellation.Cancel();
        await writing;
    }

    [Fact]
    public async Task DashboardStream_CancellationDuringAWriteEndsNormally()
    {
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        await using var body = new CancellingStream(cancellation);
        context.Response.Body = body;
        var events = new DashboardEventStream(new ConfigurationState(), new RequestStatisticsCollector());
        await events.WriteAsync(context);
        Assert.True(cancellation.IsCancellationRequested);
    }

    private static async Task<JsonDocument> ReadEventAsync(StreamReader reader, string expected, CancellationToken cancellation)
    {
        Assert.Equal($"event: {expected}", await reader.ReadLineAsync(cancellation));
        var data = await reader.ReadLineAsync(cancellation);
        Assert.NotNull(data);
        Assert.StartsWith("data: ", data, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await reader.ReadLineAsync(cancellation));
        return JsonDocument.Parse(data[6..]);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SteppingClock : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(Interlocked.Increment(ref _reads) * 4);
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellation.Token);
        }
    }
}
