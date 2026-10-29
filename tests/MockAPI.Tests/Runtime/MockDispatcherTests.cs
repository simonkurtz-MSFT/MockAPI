using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class MockDispatcherTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task MalformedPayloads_ReturnRealBadRequestInsteadOfConfiguredResponse(string method)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        var endpoint = CreateEndpoint([method], "/payload", "configured") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 503,
                DelaySeconds = 300,
                Headers = new() { ["X-Configured"] = ["not-on-validation-errors"] },
                ContentType = "text/plain",
                Body = "configured"
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();
        foreach (var body in new[] { "{broken", " ", "{} {}", "{\"x\":}" })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.Path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var response = await client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            Assert.False(response.Headers.Contains("X-Configured"));
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        }

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(4, snapshot.MatchedRequests);
        Assert.Equal(4, snapshot.ClientErrorResponses);
        Assert.Equal(0, snapshot.FailedWrites);
        Assert.Equal(4, snapshot.RecentRequests.Length);
        Assert.All(snapshot.RecentRequests, entry => Assert.Equal(400, entry.StatusCode));
        Assert.All(snapshot.RecentRequests, entry => Assert.Equal(RequestLogOutcome.Rejected, entry.Outcome));
        Assert.True(snapshot.ResponseBytes > 0);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task EmptyPayloads_PreserveTheConfiguredResponse(string method)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint([method], "/payload", "configured") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 202,
                Headers = new() { ["X-Configured"] = ["preserved"] },
                ContentType = "text/plain",
                Body = "accepted without a request body"
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();
        foreach (var content in new HttpContent?[]
                 {
                     null,
                     new ByteArrayContent([]),
                     new StringContent(string.Empty, Encoding.Unicode, "application/json")
                 })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.Path) { Content = content };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal("preserved", Assert.Single(response.Headers.GetValues("X-Configured")));
            Assert.Equal("accepted without a request body", await response.Content.ReadAsStringAsync());
        }
    }

    [Theory]
    [InlineData("POST", "application/json", "{\"unexpected\":\"business fields are ignored\"}")]
    [InlineData("PUT", "application/problem+json", "null")]
    [InlineData("PATCH", "Application/JSON", "[1,true,\"text\"]")]
    [InlineData("POST", "application/json", "\"\"")]
    [InlineData("POST", "text/plain", "{not-json")]
    [InlineData("PUT", null, "raw bytes")]
    public async Task ValidPayloads_PreserveExactConfiguredStatusHeadersAndBody(
        string method, string? contentType, string body)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint([method], "/payload", "unchanged") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 422,
                Headers = new() { ["X-Configured"] = ["one", "two"] },
                ContentType = "text/plain",
                Body = "unchanged"
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.Path)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        if (contentType is not null)
        {
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(["one", "two"], response.Headers.GetValues("X-Configured"));
        Assert.Equal("unchanged", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(false, 0, 200)]
    [InlineData(true, 0, 200)]
    [InlineData(false, 1048576, 200)]
    [InlineData(true, 1048576, 200)]
    [InlineData(false, 1048577, 413)]
    [InlineData(true, 1048577, 413)]
    public async Task PayloadSizeLimit_IsInclusiveAndEnforcedWithoutContentLength(
        bool unknownLength, int length, int expectedStatus)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "accepted")));
        using var client = factory.CreateClient();
        using var content = unknownLength
            ? (HttpContent)new StreamContent(new NonSeekableReadStream(new byte[length]))
            : new ByteArrayContent(new byte[length]);

        using var response = await client.PostAsync("/payload", content);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task UnknownLengthPayloadOverLimit_ReturnsPayloadTooLargeAndRecordsActualStatus()
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "configured")));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/payload";
        using var requestBody = new NonSeekableReadStream(new byte[1048577]);
        using var responseBody = new MemoryStream();
        context.Request.Body = requestBody;
        context.Response.Body = responseBody;
        Assert.Null(context.Request.ContentLength);

        await MockRequestDispatcher.DispatchAsync(context, configuration, statistics);

        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.ClientErrorResponses);
        Assert.Equal(0, snapshot.FailedWrites);
        Assert.Equal(413, Assert.Single(snapshot.RecentRequests).StatusCode);
        Assert.Equal(responseBody.Length, snapshot.ResponseBytes);
    }

    [Fact]
    public async Task MalformedPayload_DoesNotAbortOrConsumeRateLimit()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint(["POST"], "/payload", "") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = [],
                ContentType = "text/plain",
                Body = "limited",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 1,
                    WindowSeconds = 60,
                    SuccessResponse = new MockSuccessResponseDefinition { StatusCode = 200, Headers = [], ContentType = "text/plain", Body = "accepted" }
                }
            }
        };
        var aborted = CreateEndpoint(["PATCH"], "/abort-payload", "") with
        {
            Response = new MockResponseDefinition { Behavior = MockResponseBehavior.AbortConnection, Headers = [], Body = "" }
        };
        Apply(configuration, CreateDocument(endpoint, aborted));
        using var client = factory.CreateClient();

        using var malformed = await client.PostAsync(endpoint.Path,
            new StringContent("{", Encoding.UTF8, "application/json"));
        using var malformedAbort = await client.PatchAsync(aborted.Path,
            new StringContent("{", Encoding.UTF8, "application/json"));
        using var valid = await client.PostAsync(endpoint.Path, new StringContent("{}"));
        using var limited = await client.PostAsync(endpoint.Path, new StringContent("{}"));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, malformedAbort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<RequestStatisticsCollector>().GetSnapshot().AbortedConnections);
    }

    [Theory]
    [InlineData("application/json; charset=utf-16", "{}", 415)]
    [InlineData("application/json, text/plain", "{}", 400)]
    [InlineData("invalid media type", "{}", 400)]
    [InlineData("application/json", "{\"value\":1,}", 400)]
    [InlineData("application/json", "/*comment*/{}", 400)]
    [InlineData("application/json", "NaN", 400)]
    [InlineData("application/json; charset=\"UTF-8\"", "false", 200)]
    [InlineData("application/xml", "<not-validated", 200)]
    public async Task PayloadValidation_UsesDeclaredFormatWithoutBusinessValidation(
        string contentType, string body, int expectedStatus)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "configured")));
        using var client = factory.CreateClient();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        using var response = await client.PostAsync("/payload", content);

        Assert.Equal(expectedStatus, (int)response.StatusCode);
    }

    [Fact]
    public async Task PayloadValidation_RejectsInvalidUtf8AndExcessiveJsonNesting()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "configured")));
        using var client = factory.CreateClient();
        foreach (var body in new[] { new byte[] { 0x22, 0xff, 0x22 }, Encoding.UTF8.GetBytes(new string('[', 65) + "0" + new string(']', 65)) })
        {
            using var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
            using var response = await client.PostAsync("/payload", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task OtherMethods_DoNotRequirePayloads(string method)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint([method], "/payload", "configured")));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/payload"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CancelledPayloadRead_IsRecordedOnceWithoutRetainingBodyContent()
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "configured")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/payload";
        context.RequestAborted = cancellation.Token;
        context.Request.Body = new ThrowingReadStream(new OperationCanceledException(cancellation.Token));
        context.Response.Body = new MemoryStream();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MockRequestDispatcher.DispatchAsync(context, configuration, statistics));

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.FailedWrites);
        Assert.Null(Assert.Single(snapshot.RecentRequests).StatusCode);
        Assert.Equal(0, snapshot.ResponseBytes);
    }

    [Fact]
    public async Task IncompleteHttpPayload_ReturnsSafeBadRequestProblem()
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        Apply(configuration, CreateDocument(CreateEndpoint(["POST"], "/payload", "configured")));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/payload";
        context.Request.Body = new ThrowingReadStream(new BadHttpRequestException("Do not expose protocol internals."));
        context.Response.Body = new MemoryStream();

        await MockRequestDispatcher.DispatchAsync(context, configuration, statistics);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.DoesNotContain("protocol internals", Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
        Assert.Equal(1, statistics.GetSnapshot().ClientErrorResponses);
    }

    [Fact]
    public async Task Root_ReturnsDashboardInsteadOfDispatcherResponse()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/", CancellationToken.None);
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/");
        using var headResponse = await client.SendAsync(headRequest, CancellationToken.None);
        using var postResponse = await client.PostAsync("/", null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("MockAPI", await response.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, postResponse.StatusCode);
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(0, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
    }

    [Fact]
    public async Task FaviconRequest_IsServedWithoutAddingARequestLogEntry()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/favicon.ico", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(0, snapshot.TotalRequests);
        Assert.Empty(snapshot.RecentRequests);
    }

    [Fact]
    public async Task UnconfiguredBrowserResource_IsLoggedAsUnmatched()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/.well-known/appspecific/com.chrome.devtools.json",
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.UnmatchedRequests);
        var entry = Assert.Single(snapshot.RecentRequests);
        Assert.Equal(RequestLogOutcome.Unmatched, entry.Outcome);
        Assert.Null(entry.EndpointId);
        Assert.Equal(404, entry.StatusCode);
    }

    [Fact]
    public async Task ConfiguredEndpoint_WritesExactResponseAndHeadOmitsBody()
    {
        await using var factory = new UnsecuredApplicationFactory();
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
    public async Task RateLimitedEndpoint_SharesRequestLimitAcrossMethods()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint(["GET", "POST"], "/api/rate-limited", "{\"error\":\"try later\"}") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = new() { ["Retry-After"] = ["10"] },
                ContentType = "application/json; charset=utf-8",
                Body = "{\"error\":\"try later\"}",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 2,
                    WindowSeconds = 10,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = 202,
                        Headers = [],
                        ContentType = "application/json; charset=utf-8",
                        Body = "{\"status\":\"accepted\"}"
                    }
                }
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/rate-limited", CancellationToken.None);
        using var second = await client.PostAsync("/api/rate-limited", null, CancellationToken.None);
        using var third = await client.GetAsync("/api/rate-limited", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal("{\"status\":\"accepted\"}", await first.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal("{\"status\":\"accepted\"}", await second.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("10", Assert.Single(third.Headers.GetValues("Retry-After")));
        Assert.Equal("{\"error\":\"try later\"}", await third.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RateLimitedEndpoint_ReturnsSuccessAfterWindowExpires(int windowSeconds)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint(["GET"], "/api/rate-window", "limited") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = [],
                ContentType = "text/plain",
                Body = "limited",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 1,
                    WindowSeconds = windowSeconds,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = 200,
                        Headers = [],
                        ContentType = "text/plain",
                        Body = "success"
                    }
                }
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();

        using var first = await client.GetAsync("/api/rate-window", CancellationToken.None);
        using var limited = await client.GetAsync("/api/rate-window", CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(windowSeconds) + TimeSpan.FromMilliseconds(100), CancellationToken.None);
        using var replenished = await client.GetAsync("/api/rate-window", CancellationToken.None);
        using var limitedAgain = await client.GetAsync("/api/rate-window", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replenished.StatusCode);
        Assert.Equal("success", await replenished.Content.ReadAsStringAsync(CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedAgain.StatusCode);
        Assert.Equal("limited", await limitedAgain.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AbortConnectionEndpoint_DropsConnectionWithoutAResponse()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var statistics = factory.Services.GetRequiredService<RequestStatisticsCollector>();
        var endpoint = CreateEndpoint(["GET"], "/api/abort", string.Empty) with
        {
            Response = new MockResponseDefinition
            {
                Behavior = MockResponseBehavior.AbortConnection,
                Headers = [],
                Body = string.Empty
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            client.GetAsync("/api/abort", HttpCompletionOption.ResponseHeadersRead, CancellationToken.None));
        Assert.Equal("The application aborted the request.", exception.Message);

        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.AbortedConnections);
        Assert.Equal(0, snapshot.FailedWrites);
        Assert.Equal(0, snapshot.ResponseBytes);
        Assert.Equal(1, Assert.Single(snapshot.Endpoints).TotalRequests);
        var request = Assert.Single(snapshot.RecentRequests);
        Assert.Equal(endpoint.Id, request.EndpointId);
        Assert.Equal(RequestLogOutcome.Aborted, request.Outcome);
        Assert.Null(request.StatusCode);
        Assert.Equal(0, request.ResponseBytes);
    }

    [Fact]
    public async Task DelayedEndpoint_WaitsBeforeWritingConfiguredResponse()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        var endpoint = CreateEndpoint(["GET"], "/api/delayed", "ready") with
        {
            Response = new MockResponseDefinition
            {
                DelaySeconds = 1,
                StatusCode = 200,
                Headers = [],
                ContentType = "text/plain",
                Body = "ready"
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var client = factory.CreateClient();
        var stopwatch = Stopwatch.StartNew();

        using var response = await client.GetAsync("/api/delayed", CancellationToken.None);

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(900), $"Response completed after {stopwatch.Elapsed}.");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dispatcher_UsesExactPathAndReturnsNotFoundWhenUnmatched()
    {
        await using var factory = new UnsecuredApplicationFactory();
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
    public async Task Dispatcher_NullPathIsUnmatched()
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = default;

        await MockRequestDispatcher.DispatchAsync(context, configuration, statistics);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal(1, statistics.GetSnapshot().UnmatchedRequests);
        Assert.Equal(string.Empty, MockRequestDispatcher.GetPathValue(default));
        Assert.Equal("/value", MockRequestDispatcher.GetPathValue(new PathString("/value")));
    }

    [Fact]
    public async Task Dispatcher_FallbackMatchesPathsWithFileExtensions()
    {
        await using var factory = new UnsecuredApplicationFactory();
        var configuration = factory.Services.GetRequiredService<ConfigurationState>();
        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], "/response.json", "json path")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/response.json", CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("json path", await response.Content.ReadAsStringAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReplacingRegistry_ChangesRunningRouteWithoutRestart()
    {
        await using var factory = new UnsecuredApplicationFactory();
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispatcher_FailedRateLimitedResponseRecordsSelectedStatusAndRethrows(bool cancelWrite)
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        var endpoint = CreateEndpoint(["GET"], "/fails-rate-limited", "limited") with
        {
            Response = new MockResponseDefinition
            {
                StatusCode = 429,
                Headers = [],
                ContentType = "text/plain",
                Body = "limited",
                RateLimit = new MockRateLimitDefinition
                {
                    RequestLimit = 1,
                    WindowSeconds = 300,
                    SuccessResponse = new MockSuccessResponseDefinition
                    {
                        StatusCode = 202,
                        Headers = [],
                        ContentType = "text/plain",
                        Body = "accepted"
                    }
                }
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = endpoint.Path;
        Exception failure = cancelWrite
            ? new OperationCanceledException("Simulated response write cancellation.")
            : new IOException("Simulated response write failure.");
        context.Response.Body = new ThrowingWriteStream(failure);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
                MockRequestDispatcher.DispatchAsync(context, configuration, statistics));

            Assert.Same(failure, exception);
            var snapshot = statistics.GetSnapshot();
            var expectedStatus = attempt == 0 ? 202 : 429;
            Assert.Equal(attempt + 1, snapshot.TotalRequests);
            Assert.Equal(attempt + 1, snapshot.MatchedRequests);
            Assert.Equal(attempt + 1, snapshot.FailedWrites);
            Assert.Equal(0, snapshot.AbortedConnections);
            Assert.Equal(0, snapshot.ResponseBytes);
            Assert.Equal(1, snapshot.SuccessResponses);
            Assert.Equal(attempt, snapshot.ClientErrorResponses);
            Assert.Equal(1, snapshot.RecentMinutes[^1].SuccessResponses);
            Assert.Equal(attempt, snapshot.RecentMinutes[^1].ClientErrorResponses);
            var endpointStatistics = Assert.Single(snapshot.Endpoints);
            Assert.Equal(endpoint.Id, endpointStatistics.EndpointId);
            Assert.Equal(expectedStatus, endpointStatistics.LastStatusCode);
            Assert.Equal(1, endpointStatistics.RecentMinutes[^1].SuccessResponses);
            Assert.Equal(attempt, endpointStatistics.RecentMinutes[^1].ClientErrorResponses);
            var request = snapshot.RecentRequests[0];
            Assert.Equal(RequestLogOutcome.FailedWrite, request.Outcome);
            Assert.Equal(expectedStatus, request.StatusCode);
            Assert.Equal(0, request.ResponseBytes);
        }
    }

    [Theory]
    [InlineData(MockResponseBehavior.Response)]
    [InlineData(MockResponseBehavior.AbortConnection)]
    public async Task Dispatcher_CancelledDelayRecordsFailureWithoutStatusAgainstCapturedEndpoint(
        MockResponseBehavior behavior)
    {
        var configuration = new ConfigurationState();
        var statistics = new RequestStatisticsCollector();
        var endpoint = CreateEndpoint(["GET"], "/cancelled-delay", string.Empty) with
        {
            Response = new MockResponseDefinition
            {
                Behavior = behavior,
                DelaySeconds = 300,
                StatusCode = behavior == MockResponseBehavior.Response ? 200 : null,
                Headers = [],
                Body = string.Empty
            }
        };
        Apply(configuration, CreateDocument(endpoint));
        using var cancellation = new CancellationTokenSource();
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = endpoint.Path;
        context.RequestAborted = cancellation.Token;
        context.Response.Body = new MemoryStream();

        var dispatch = MockRequestDispatcher.DispatchAsync(context, configuration, statistics);
        Assert.False(dispatch.IsCompleted);
        Apply(configuration, CreateDocument(CreateEndpoint(["GET"], endpoint.Path, "replacement")));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Empty(context.Response.Headers);
        Assert.Equal(0, context.Response.Body.Length);
        var snapshot = statistics.GetSnapshot();
        Assert.Equal(1, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.MatchedRequests);
        Assert.Equal(1, snapshot.FailedWrites);
        Assert.Equal(0, snapshot.AbortedConnections);
        Assert.Equal(0, snapshot.ResponseBytes);
        Assert.Equal(0, snapshot.InformationalResponses);
        Assert.Equal(0, snapshot.SuccessResponses);
        Assert.Equal(0, snapshot.RedirectionResponses);
        Assert.Equal(0, snapshot.ClientErrorResponses);
        Assert.Equal(0, snapshot.ServerErrorResponses);
        var minute = snapshot.RecentMinutes[^1];
        Assert.Equal(1, minute.Requests);
        Assert.Equal(0, minute.SuccessResponses);
        var endpointStatistics = Assert.Single(snapshot.Endpoints);
        Assert.Equal(endpoint.Id, endpointStatistics.EndpointId);
        Assert.Equal(1, endpointStatistics.TotalRequests);
        Assert.Null(endpointStatistics.LastStatusCode);
        Assert.Equal(1, endpointStatistics.RecentMinutes[^1].Requests);
        Assert.Equal(0, endpointStatistics.RecentMinutes[^1].SuccessResponses);
        var request = Assert.Single(snapshot.RecentRequests);
        Assert.Equal(endpoint.Id, request.EndpointId);
        Assert.Equal("GET", request.Method);
        Assert.Equal(endpoint.Path, request.Path);
        Assert.Equal(RequestLogOutcome.FailedWrite, request.Outcome);
        Assert.Null(request.StatusCode);
        Assert.Equal(0, request.ResponseBytes);
    }

    [Fact]
    public async Task ConcurrentRequestsAndReplacements_ReturnOnlyCompleteResponses()
    {
        await using var factory = new UnsecuredApplicationFactory();
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

    private sealed class ThrowingWriteStream(Exception? exception = null) : MemoryStream
    {
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException(exception ?? new IOException("Simulated response write failure."));
    }

    private sealed class NonSeekableReadStream(byte[] body) : MemoryStream(body)
    {
        public override bool CanSeek => false;
    }

    private sealed class ThrowingReadStream(Exception exception) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);
    }
}
