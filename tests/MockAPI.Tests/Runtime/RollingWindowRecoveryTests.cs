using MockAPI.Configuration;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class RollingWindowRecoveryTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(3600)]
    public void ExhaustedEndpoint_RecoversOnFirstRequestAfterIdleWindow(int idleSeconds)
    {
        var clock = new MonotonicClock();
        var endpoint = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);
        Exhaust(endpoint);

        clock.Advance(TimeSpan.FromSeconds(idleSeconds));

        for (var request = 0; request < 4; request++)
        {
            Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        }

        Assert.Equal(429, endpoint.SelectResponse().StatusCode);
    }

    [Fact]
    public void RejectedRequests_DoNotExtendAcceptedRequestWindow()
    {
        var clock = new MonotonicClock();
        var endpoint = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);
        Exhaust(endpoint);

        for (var second = 1; second < 10; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(429, endpoint.SelectResponse().StatusCode);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
    }

    [Fact]
    public void StaggeredRequests_ExpireIndividuallyAtTheirRollingWindowBoundary()
    {
        var clock = new MonotonicClock();
        var endpoint = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(429, endpoint.SelectResponse().StatusCode);

        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        Assert.Equal(429, endpoint.SelectResponse().StatusCode);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(429, endpoint.SelectResponse().StatusCode);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        Assert.Equal(429, endpoint.SelectResponse().StatusCode);
    }

    [Fact]
    public void ConcurrentRequests_CannotExceedPermitsBeforeOrAfterIdleRecovery()
    {
        var clock = new MonotonicClock();
        var endpoint = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);

        for (var window = 0; window < 3; window++)
        {
            var successes = 0;
            Parallel.For(0, 1_000, _ =>
            {
                if (endpoint.SelectResponse().StatusCode == 200)
                {
                    Interlocked.Increment(ref successes);
                }
            });

            Assert.Equal(4, successes);
            clock.Advance(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void EndpointBudgets_RemainIndependent()
    {
        var clock = new MonotonicClock();
        var first = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);
        var second = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: clock);
        Exhaust(first);

        Assert.Equal(200, second.SelectResponse().StatusCode);
        Assert.Equal(429, first.SelectResponse().StatusCode);
    }

    [Fact]
    public void Replacement_StartsFreshWithoutResettingThePriorSnapshot()
    {
        var clock = new MonotonicClock();
        var definition = CreateDefinition();
        var original = RuntimeEndpoint.Create(definition, timeProvider: clock);
        Exhaust(original);

        var replacement = RuntimeEndpoint.Create(definition, original, clock);

        Assert.Equal(200, replacement.SelectResponse().StatusCode);
        Assert.Equal(429, original.SelectResponse().StatusCode);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(200, original.SelectResponse().StatusCode);
    }

    [Fact]
    public void RateLimitedResponses_PreserveConfiguredStatusHeadersAndBodies()
    {
        var endpoint = RuntimeEndpoint.Create(CreateDefinition(), timeProvider: new MonotonicClock());
        var success = endpoint.SelectResponse();
        Assert.Equal(200, success.StatusCode);
        Assert.Equal("accepted", System.Text.Encoding.UTF8.GetString(success.Body.Span));
        Assert.Empty(success.Headers);
        for (var request = 1; request < 4; request++)
        {
            endpoint.SelectResponse();
        }

        var exhausted = endpoint.SelectResponse();

        Assert.Equal(429, exhausted.StatusCode);
        Assert.Equal("limited", System.Text.Encoding.UTF8.GetString(exhausted.Body.Span));
        Assert.Equal("Retry-After", Assert.Single(exhausted.Headers).Name);
        Assert.Equal("10", Assert.Single(Assert.Single(exhausted.Headers).Values));
    }

    private static void Exhaust(RuntimeEndpoint endpoint)
    {
        for (var request = 0; request < 4; request++)
        {
            Assert.Equal(200, endpoint.SelectResponse().StatusCode);
        }

        Assert.Equal(429, endpoint.SelectResponse().StatusCode);
    }

    private static MockEndpointDefinition CreateDefinition() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Rolling-window recovery",
        Enabled = true,
        Methods = ["GET"],
        Path = "/recovery",
        Response = new MockResponseDefinition
        {
            StatusCode = 429,
            Headers = new() { ["Retry-After"] = ["10"] },
            ContentType = "text/plain",
            Body = "limited",
            RateLimit = new MockRateLimitDefinition
            {
                RequestLimit = 4,
                WindowSeconds = 10,
                SuccessResponse = new MockSuccessResponseDefinition
                {
                    StatusCode = 200,
                    Headers = [],
                    ContentType = "text/plain",
                    Body = "accepted"
                }
            }
        }
    };

    private sealed class MonotonicClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
