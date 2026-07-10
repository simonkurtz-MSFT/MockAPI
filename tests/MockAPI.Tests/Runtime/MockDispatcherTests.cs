using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class MockDispatcherTests
{
    [Fact]
    public async Task Root_ReturnsServiceResponseUnlessExplicitlyConfigured()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", CancellationToken.None);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/");
        using var headResponse = await client.SendAsync(headRequest, CancellationToken.None);
        using var postResponse = await client.PostAsync("/", null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal("MockAPI is running.\n", await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);

        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/", "configured")));

        Assert.Equal("configured", await client.GetStringAsync("/", CancellationToken.None));
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(2, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
    }

    [Fact]
    public async Task ConfiguredEndpoint_WritesExactResponseAndHeadOmitsBody()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        var endpoint = CreateEndpoint(
            methods: ["GET", "HEAD"],
            path: "/api/raw",
            body: "{not-valid-json}\r\n") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                ReasonPhrase = "Too Many Requests",
                Headers = new()
                {
                    ["Retry-After"] = ["30"],
                    ["X-Repeated"] = ["first", "second"]
                },
                ContentType = "application/json; charset=utf-8",
                Body = "{not-valid-json}\r\n"
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/raw?ignored=secret", CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("Too Many Requests", response.ReasonPhrase);
        Assert.Equal("30", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(["first", "second"], response.Headers.GetValues("X-Repeated"));
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(
            Encoding.UTF8.GetBytes("{not-valid-json}\r\n"),
            await response.Content.ReadAsByteArrayAsync(CancellationToken.None));

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/api/raw");
        using var headResponse = await client.SendAsync(headRequest, CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, headResponse.StatusCode);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.Equal("30", Assert.Single(headResponse.Headers.GetValues("Retry-After")));
        Assert.Equal("application/json; charset=utf-8", headResponse.Content.Headers.ContentType!.ToString());
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(2, snapshot.TotalRequests);
        Assert.Equal(2, snapshot.MatchedRequests);
        Assert.Equal(Encoding.UTF8.GetByteCount("{not-valid-json}\r\n"), snapshot.ResponseBytes);
    }

    [Fact]
    public async Task Dispatcher_UsesExactPathAndReturnsNotFoundWhenUnmatched()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/case-sensitive", "matched")));
        using var client = factory.CreateClient();

        using var wrongCase = await client.GetAsync("/Case-Sensitive", CancellationToken.None);
        using var wrongMethod = await client.PostAsync("/case-sensitive", null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, wrongCase.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongMethod.StatusCode);
        Assert.Equal(2, statistics.GetSnapshot().UnmatchedRequests);
    }

    [Fact]
    public async Task ReplacingRegistry_ChangesRunningRouteWithoutRestart()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/dynamic", "before")));
        using var client = factory.CreateClient();

        Assert.Equal("before", await client.GetStringAsync("/dynamic", CancellationToken.None));

        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/dynamic", "after")));

        Assert.Equal("after", await client.GetStringAsync("/dynamic", CancellationToken.None));
    }

    [Fact]
    public async Task Dispatcher_RecordsFailedWriteAndRethrows()
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/fails", "body")));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/fails";
        context.Response.Body = new ThrowingWriteStream();

        await Assert.ThrowsAsync<IOException>(() =>
            MockRequestDispatcher.DispatchAsync(context, configuration, statistics));

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.FailedWrites);
        Assert.Equal(0, snapshot.ResponseBytes);
    }

    [Fact]
    public async Task ConcurrentRequestsAndReplacements_ReturnOnlyCompleteResponses()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var first = CreateDocument(CreateEndpoint(["GET"], "/race", "first-response"));
        var second = CreateDocument(CreateEndpoint(["GET"], "/race", "second-response"));
        Apply(configuration, first);
        using var client = factory.CreateClient();

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 500; index++)
            {
                Apply(configuration, index % 2 == 0 ? first : second);
            }
        });
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 100; index++)
            {
                var body = await client.GetStringAsync("/race", CancellationToken.None);
                Assert.True(body is "first-response" or "second-response", body);
            }
        });

        await Task.WhenAll(readers.Append(writer));

        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>().GetSnapshot();
        Assert.Equal(800, statistics.TotalRequests);
        Assert.Equal(800, statistics.MatchedRequests);
    }

    private static MockApiConfigurationDocument CreateDocument(params MockEndpointDefinition[] endpoints) => new()
    {
        SchemaVersion = "1.0",
        Endpoints = endpoints
    };

    private static void Apply(ConfigurationState state, MockApiConfigurationDocument document)
    {
        var result = state.TryReplace(document, state.Current.Revision);
        Assert.Equal(ConfigurationUpdateStatus.Applied, result.Status);
    }

    private static MockEndpointDefinition CreateEndpoint(
        IReadOnlyList<string> methods,
        string path,
        string body) => new()
        {
            Id = Guid.NewGuid(),
            Name = path,
            Enabled = true,
            Methods = methods,
            Path = path,
            Response = new MockResponseDefinition
            {
                StatusCode = 200,
                Headers = [],
                ContentType = "text/plain; charset=utf-8",
                Body = body
            }
        };

    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Simulated response write failure."));
    }
}