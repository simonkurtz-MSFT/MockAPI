using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.Http;
using MockAPI.Runtime;

namespace MockAPI.Tests.Runtime;

public sealed class MockRequestBodyValidatorTests
{
    [Theory]
    [InlineData("empty", true)]
    [InlineData("text", true)]
    [InlineData("json", true)]
    [InlineData("large-text", false)]
    [InlineData("large-json", true)]
    [InlineData("large-json", false)]
    public async Task RepeatedValidation_DoesNotAllocateRequestSizedBuffers(string scenario, bool knownLength)
    {
        using var body = new MemoryStream(CreateBody(scenario));
        var request = CreateRequest(body, scenario.Contains("json", StringComparison.Ordinal) ? "application/json" : "text/plain",
            knownLength ? body.Length : null);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            body.Position = 0;
            Assert.Null(await MockRequestBodyValidator.ValidateAsync(request));
        }

        body.Position = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var validation = MockRequestBodyValidator.ValidateAsync(request);
        Assert.True(validation.IsCompletedSuccessfully);
        var problem = await validation;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(problem);
        Assert.InRange(allocatedBytes, 0, 4096);
    }

    [Theory]
    [InlineData("{}", true)]
    [InlineData("[]", true)]
    [InlineData("  null\r\n", true)]
    [InlineData("true", true)]
    [InlineData("false", true)]
    [InlineData("-1.25e+2", true)]
    [InlineData("\"\"", true)]
    [InlineData("\"\\uD800\"", true)]
    [InlineData("{\"field\":1,\"field\":2}", true)]
    [InlineData(" ", false)]
    [InlineData("{} {}", false)]
    [InlineData("{} garbage", false)]
    [InlineData("[1,]", false)]
    [InlineData("{\"x\":}", false)]
    [InlineData("/*comment*/{}", false)]
    [InlineData("NaN", false)]
    [InlineData("01", false)]
    [InlineData("\"\\uXXXX\"", false)]
    public async Task JsonSyntaxValidation_PreservesAcceptedValuesAndRejectedSyntax(string json, bool valid)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var request = CreateRequest(body, "application/json", body.Length);

        var problem = await MockRequestBodyValidator.ValidateAsync(request);

        if (valid)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Equal(400, problem?.Status);
            Assert.EndsWith("/invalid-json", problem?.Type, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public async Task JsonNestingLimit_RemainsInclusive(int depth, bool valid)
    {
        var json = new string('[', depth) + "0" + new string(']', depth);
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var problem = await MockRequestBodyValidator.ValidateAsync(CreateRequest(body, "application/json", null));

        Assert.Equal(valid ? null : 400, problem?.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65535)]
    public async Task FragmentedJson_PreservesUtf8TokensAndBufferGrowth(int chunkSize)
    {
        var json = "[\"" + new string('x', 65535) + "\u00e9\U0001f600\",true,null,-1.25e+2]";
        using var body = new FragmentedReadStream(Encoding.UTF8.GetBytes(json), chunkSize);
        var pool = new TrackingArrayPool();

        var problem = await MockRequestBodyValidator.ValidateAsync(
            CreateRequest(body, "application/problem+json", null), pool);

        Assert.Null(problem);
        AssertBuffersReturnedAndCleared(pool);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("invalid-json")]
    [InlineData("unsupported-charset")]
    [InlineData("oversized")]
    [InlineData("protocol-error")]
    [InlineData("protocol-too-large")]
    [InlineData("io-error")]
    [InlineData("cancelled")]
    public async Task EveryExit_ReturnsAndClearsAllRentedBuffers(string outcome)
    {
        var payload = outcome == "oversized"
            ? new byte[MockRequestBodyValidator.MaximumBodyBytes + 1]
            : Encoding.UTF8.GetBytes("\"" + new string('s', 70000) + (outcome == "invalid-json" ? "" : "\""));
        Exception? failure = outcome switch
        {
            "protocol-error" => new BadHttpRequestException("Private protocol details"),
            "protocol-too-large" => new BadHttpRequestException("Private protocol details", 413),
            "io-error" => new IOException("Read failed"),
            "cancelled" => new OperationCanceledException(),
            _ => null
        };
        using var body = new FragmentedReadStream(payload, 8192, failure, asynchronous: true);
        var contentType = outcome == "unsupported-charset" ? "application/json; charset=utf-16" : "application/json";
        var request = CreateRequest(body, contentType, null);
        var pool = new TrackingArrayPool();

        if (outcome is "io-error" or "cancelled" or "protocol-too-large")
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(() => MockRequestBodyValidator.ValidateAsync(request, pool));
            Assert.Same(failure, thrown);
        }
        else
        {
            var problem = await MockRequestBodyValidator.ValidateAsync(request, pool);
            int? expectedStatus = outcome switch
            {
                "valid" => null,
                "unsupported-charset" => 415,
                "oversized" => 413,
                _ => 400
            };
            Assert.Equal(expectedStatus, problem?.Status);
            Assert.DoesNotContain("Private protocol details", problem?.Detail ?? "");
        }

        AssertBuffersReturnedAndCleared(pool);
    }

    [Theory]
    [InlineData("invalid media type", 0, 400)]
    [InlineData("invalid media type", 1048577, 413)]
    [InlineData("application/json; charset=utf-16", 0, null)]
    [InlineData("application/json; charset=utf-16", 1, 415)]
    [InlineData("application/json; charset=utf-16", 1048577, 413)]
    public async Task ErrorPrecedence_PreservesSizeMediaTypeAndEmptyBodyRules(
        string contentType, int length, int? expectedStatus)
    {
        using var body = new MemoryStream(new byte[length]);

        var problem = await MockRequestBodyValidator.ValidateAsync(CreateRequest(body, contentType, length));

        Assert.Equal(expectedStatus, problem?.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(1L)]
    public async Task DeclaredLength_DoesNotReplaceReadingOrActualSizeEnforcement(long? declaredLength)
    {
        using var body = new MemoryStream(new byte[MockRequestBodyValidator.MaximumBodyBytes + 1]);

        var problem = await MockRequestBodyValidator.ValidateAsync(
            CreateRequest(body, "application/json; charset=utf-16", declaredLength));

        Assert.Equal(413, problem?.Status);
    }

    [Theory]
    [InlineData("POST", null)]
    [InlineData("PUT", "text/plain")]
    [InlineData("PATCH", "application/octet-stream")]
    public async Task NonJsonBodies_OnlyRentOneScratchBuffer(string method, string? contentType)
    {
        using var body = new MemoryStream("not valid JSON"u8.ToArray());
        var request = CreateRequest(body, contentType, body.Length);
        request.Method = method;
        var pool = new TrackingArrayPool();

        Assert.Null(await MockRequestBodyValidator.ValidateAsync(request, pool));

        Assert.Single(pool.Rented);
        AssertBuffersReturnedAndCleared(pool);
    }

    [Theory]
    [InlineData("GET", "application/json", 0, null)]
    [InlineData("POST", "invalid media type", 0, 400)]
    [InlineData("POST", "application/json", 1048577, 413)]
    public async Task RequestsRejectedOrSkippedBeforeReading_DoNotRentBuffers(
        string method, string contentType, long declaredLength, int? expectedStatus)
    {
        using var body = new MemoryStream();
        var request = CreateRequest(body, contentType, declaredLength);
        request.Method = method;
        var pool = new TrackingArrayPool();

        var problem = await MockRequestBodyValidator.ValidateAsync(request, pool);

        Assert.Equal(expectedStatus, problem?.Status);
        Assert.Empty(pool.Rented);
    }

    [Fact]
    public async Task FragmentedInvalidUtf8_RemainsAnInvalidJsonProblem()
    {
        using var body = new FragmentedReadStream([0x22, 0xc3, 0x28, 0x22], 1);

        var problem = await MockRequestBodyValidator.ValidateAsync(CreateRequest(body, "application/json", null));

        Assert.Equal(400, problem?.Status);
        Assert.EndsWith("/invalid-json", problem?.Type, StringComparison.Ordinal);
    }

    internal static HttpRequest CreateRequest(Stream body, string? contentType, long? contentLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Body = body;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = contentLength;
        return context.Request;
    }

    internal static byte[] CreateBody(string scenario) => scenario switch
    {
        "empty" => [],
        "text" => "example"u8.ToArray(),
        "json" => "{\"ok\":true}"u8.ToArray(),
        "large-text" => new byte[MockRequestBodyValidator.MaximumBodyBytes],
        "large-json" => Encoding.UTF8.GetBytes("\"" + new string('x', MockRequestBodyValidator.MaximumBodyBytes - 2) + "\""),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown request-body fixture.")
    };

    private static void AssertBuffersReturnedAndCleared(TrackingArrayPool pool)
    {
        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Rented, buffer =>
        {
            Assert.Contains(pool.Returned, returned => ReferenceEquals(buffer, returned));
            Assert.InRange(buffer.Length, 1, MockRequestBodyValidator.MaximumBodyBytes);
            Assert.True(buffer.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        });
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        internal List<byte[]> Rented { get; } = [];
        internal List<byte[]> Returned { get; } = [];

        public override byte[] Rent(int minimumLength)
        {
            var buffer = new byte[minimumLength];
            Array.Fill(buffer, (byte)0xa5);
            Rented.Add(buffer);
            return buffer;
        }

        public override void Return(byte[] array, bool clearArray = false)
        {
            Assert.True(clearArray);
            Assert.DoesNotContain(Returned, returned => ReferenceEquals(array, returned));
            Array.Clear(array);
            Returned.Add(array);
        }
    }

    private sealed class FragmentedReadStream(
        byte[] payload, int chunkSize, Exception? failure = null, bool asynchronous = false) : MemoryStream(payload)
    {
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (asynchronous)
            {
                await Task.Yield();
            }
            if (Position == Length && failure is not null)
            {
                throw failure;
            }

            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
        }
    }
}
