using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using MockAPI.Configuration;
using MockAPI.Management;
using MockAPI.Runtime;

namespace MockAPI.Tests.Management;

public sealed class ApiKeySecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mockapi-security-tests", Guid.NewGuid().ToString("N"));
    private string ConfigurationPath => Path.Combine(_directory, "mockapi.json");

    [Theory]
    [InlineData(32)]
    [InlineData(43)]
    [InlineData(256)]
    public async Task WarmAuthorization_DoesNotAllocateForEncodingHashingOrStoredHashDecoding(int length)
    {
        var key = new string('a', length);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = key });
        await security.LoadAsync(CancellationToken.None);
        var context = new DefaultHttpContext();
        context.Request.Headers[ApiKeySecurity.HeaderName] = key;
        for (var index = 0; index < 100; index++)
        {
            Assert.True(security.Authorizes(context));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var authorized = true;
        for (var index = 0; index < 1_000; index++)
        {
            authorized &= security.Authorizes(context);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(authorized);
        Assert.InRange(allocated, 0, 1_024);
    }

    [Fact]
    public async Task ParallelAuthorization_UsesIndependentScratchStorage()
    {
        var key = new string('a', 256);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = key });
        await security.LoadAsync(CancellationToken.None);
        var valid = new DefaultHttpContext();
        valid.Request.Headers[ApiKeySecurity.HeaderName] = key;
        var invalid = new DefaultHttpContext();
        invalid.Request.Headers[ApiKeySecurity.HeaderName] = new string('b', 256);
        Assert.True(security.Authorizes(valid));
        Assert.False(security.Authorizes(invalid));

        Parallel.For(0, 5_000, index =>
            Assert.Equal(index % 2 == 0, security.Authorizes(index % 2 == 0 ? valid : invalid)));
    }

    [Fact]
    public async Task PersistedLowercaseHash_IsDecodedWithoutChangingStoredContract()
    {
        var key = new string('a', 32);
        Directory.CreateDirectory(_directory);
        var document = new ApiSecurityDocument(true,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant(), 1);
        var content = JsonSerializer.Serialize(document, ApiSecurityJsonContext.Default.ApiSecurityDocument);
        await File.WriteAllTextAsync(ConfigurationPath + ".security.json", content);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });

        await security.LoadAsync(CancellationToken.None);

        Assert.True(Authorizes(security, key));
        Assert.Equal(content, await File.ReadAllTextAsync(ConfigurationPath + ".security.json"));
    }

    [Fact]
    public async Task FailedRotation_PreservesPreviouslyPublishedDigestAndStatus()
    {
        var key = new string('a', 32);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = key });
        await security.LoadAsync(CancellationToken.None);
        var previous = security.Status;
        File.Delete(ConfigurationPath + ".security.json");
        Directory.CreateDirectory(ConfigurationPath + ".security.json");

        var failure = await Record.ExceptionAsync(() => security.UpdateAsync(previous.ETag, true, true, CancellationToken.None));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(previous, security.Status);
        Assert.True(Authorizes(security, key));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(43)]
    [InlineData(256)]
    public async Task DeploymentKey_AuthorizesFirstMockCallAndPersistsOnlyHash(int length)
    {
        var key = "deployment_key-" + new string('a', length - 15);
        Directory.CreateDirectory(_directory);
        var document = new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints = [new MockEndpointDefinition
            {
                Id = Guid.NewGuid(), Name = "Deployment", Enabled = true, Methods = ["GET"], Path = "/deployment",
                Response = new() { StatusCode = 202, Headers = [], ContentType = "text/plain", Body = "ready for APIM" }
            }]
        };
        await File.WriteAllTextAsync(ConfigurationPath, JsonSerializer.Serialize(document, MockApiJsonContext.Default.MockApiConfigurationDocument));
        await using var factory = CreateFactory(apiKey: key);
        using var client = factory.CreateClient();
        using var missing = await client.GetAsync("/deployment");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/deployment");
        request.Headers.Add("x-mockapi-key", key);
        using var authorized = await client.SendAsync(request);
        using var health = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, authorized.StatusCode);
        Assert.Equal("ready for APIM", await authorized.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var security = factory.Services.GetRequiredService<ApiKeySecurity>();
        Assert.True(security.Status.Enabled);
        Assert.True(security.Status.Configured);
        Assert.Equal("\"1\"", security.Status.ETag);
        var persisted = await File.ReadAllTextAsync(ConfigurationPath + ".security.json");
        Assert.DoesNotContain(key, persisted, StringComparison.Ordinal);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))),
            JsonNode.Parse(persisted)!["keyHash"]!.GetValue<string>());

        await using var restarted = CreateFactory();
        using var restartedClient = restarted.CreateClient();
        Assert.True(Authorizes(restarted.Services.GetRequiredService<ApiKeySecurity>(), key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeploymentKey_NeverReplacesPersistedRotationOrEnforcement(bool enabled)
    {
        var key = new string('a', 32);
        var replacement = new string('b', 64);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = key });
        await security.LoadAsync(CancellationToken.None);
        var rotated = (await security.UpdateAsync("\"1\"", true, true, CancellationToken.None))!;
        await security.UpdateAsync(rotated.Status.ETag, enabled, false, CancellationToken.None);
        var persisted = await File.ReadAllTextAsync(ConfigurationPath + ".security.json");

        using var restarted = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = replacement });
        await restarted.LoadAsync(CancellationToken.None);

        Assert.Equal(enabled, restarted.Status.Enabled);
        Assert.Equal("\"3\"", restarted.Status.ETag);
        Assert.Equal(persisted, await File.ReadAllTextAsync(ConfigurationPath + ".security.json"));
        if (enabled)
        {
            Assert.True(Authorizes(restarted, rotated.Key));
            Assert.False(Authorizes(restarted, key));
            Assert.False(Authorizes(restarted, replacement));
        }
        else
        {
            Assert.True(restarted.Authorizes(new DefaultHttpContext()));
        }
    }

    [Fact]
    public async Task DeploymentKey_EnablesProtectionEvenWithBootstrapOptOut()
    {
        var key = new string('a', 32);
        using var security = new ApiKeySecurity(new MockApiOptions
        {
            ConfigurationPath = ConfigurationPath,
            ApiKey = key,
            RequireApiKey = false
        });

        await security.LoadAsync(CancellationToken.None);

        Assert.True(security.Status.Enabled);
        Assert.False(security.Authorizes(new DefaultHttpContext()));
        Assert.True(Authorizes(security, key));
    }

    [Theory]
    [InlineData(31, 'a')]
    [InlineData(257, 'a')]
    [InlineData(32, ' ')]
    [InlineData(32, '\r')]
    [InlineData(32, ',')]
    [InlineData(32, '\u00e9')]
    public void InvalidDeploymentKey_FailsStartupWithoutExposingSecret(int length, char character)
    {
        var key = new string(character, length);
        using var factory = CreateFactory(apiKey: key);

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("MockApi:ApiKey", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(ConfigurationPath + ".security.json"));
    }

    [Fact]
    public async Task EmptyDeploymentKey_LeavesExistingFailClosedDefault()
    {
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath, ApiKey = "" });

        await security.LoadAsync(CancellationToken.None);

        Assert.True(security.Status.Enabled);
        Assert.False(security.Status.Configured);
        Assert.False(File.Exists(ConfigurationPath + ".security.json"));
    }

    [Fact]
    public async Task DeploymentKey_FailedPersistenceNeverActivatesKey()
    {
        Directory.CreateDirectory(ConfigurationPath + ".security.json");
        using var security = new ApiKeySecurity(new MockApiOptions
        {
            ConfigurationPath = ConfigurationPath,
            ApiKey = new string('a', 32)
        });

        var failure = await Record.ExceptionAsync(() => security.LoadAsync(CancellationToken.None));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.False(security.Status.Configured);
        Assert.Equal("\"0\"", security.Status.ETag);
        Assert.False(Authorizes(security, new string('a', 32)));
    }

    [Fact]
    public async Task DeploymentKey_BlobBootstrapPersistsHashAndNeverOverwritesSavedSettings()
    {
        var key = new string('a', 64);
        var options = new MockApiOptions
        {
            ConfigurationPath = ConfigurationPath,
            ConfigurationBlobUri = new Uri("https://example.test/config/mockapi.json"),
            ApiKey = key
        };
        var blob = new SecurityBlobClient();
        using var security = new ApiKeySecurity(options, blob);

        await security.LoadAsync(CancellationToken.None);

        Assert.True(Authorizes(security, key));
        Assert.DoesNotContain(key, Encoding.UTF8.GetString(blob.Content!), StringComparison.Ordinal);
        Assert.False(Directory.Exists(_directory));
        var rotated = (await security.UpdateAsync("\"1\"", true, true, CancellationToken.None))!;
        using var restarted = new ApiKeySecurity(options, blob);
        await restarted.LoadAsync(CancellationToken.None);
        Assert.True(Authorizes(restarted, rotated.Key));
        Assert.False(Authorizes(restarted, key));

        blob.Content = null;
        blob.Failure = new RequestFailedException(500, "storage failed");
        using var unavailable = new ApiKeySecurity(options, blob);
        await Assert.ThrowsAsync<RequestFailedException>(() => unavailable.LoadAsync(CancellationToken.None));
        Assert.False(unavailable.Status.Configured);

        blob.Failure = null;
        blob.UploadFailure = new RequestFailedException(403, "write denied");
        using var unwritable = new ApiKeySecurity(options, blob);
        await Assert.ThrowsAsync<RequestFailedException>(() => unwritable.LoadAsync(CancellationToken.None));
        Assert.False(unwritable.Status.Configured);
        Assert.False(Authorizes(unwritable, key));
    }

    [Fact]
    public async Task DefaultProtection_BlocksMockCallsButNotHealthOrDashboard()
    {
        await using var factory = CreateFactory(authenticated: false);
        using var client = factory.CreateClient();
        using var mock = await client.GetAsync("/unknown");
        using var health = await client.GetAsync("/health/ready");
        using var dashboard = await client.GetAsync("/");
        using var settings = await client.GetAsync("/__mockapi/api/security/");
        using var rotate = await client.PostAsync("/__mockapi/api/security/key", null);

        Assert.Equal(HttpStatusCode.Unauthorized, mock.StatusCode);
        Assert.Equal("ApiKey", mock.Headers.WwwAuthenticate.Single().Scheme);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        Assert.Equal((HttpStatusCode)428, rotate.StatusCode);
        Assert.Equal(0, factory.Services.GetRequiredService<RequestStatisticsCollector>().GetSnapshot().TotalRequests);
    }

    [Fact]
    public async Task MissingAdministratorCredentials_AllowsInitialSecuritySetup()
    {
        await using var factory = CreateFactory(authenticated: false);
        using var client = factory.CreateClient();

        var created = await GenerateKeyAsync(client, "\"0\"");

        Assert.True(created.Status.Enabled);
        Assert.True(created.Status.Configured);
        Assert.True(Authorizes(factory.Services.GetRequiredService<ApiKeySecurity>(), created.Key));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task GeneratedKey_ProtectsEveryMethodAndPreservesResponse(string method)
    {
        await using var factory = CreateFactory();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(new MockApiConfigurationDocument
        {
            SchemaVersion = "1.0",
            Endpoints = [new MockEndpointDefinition
            {
                Id = Guid.NewGuid(), Name = "Protected", Enabled = true, Methods = [method], Path = "/protected",
                Response = new() { StatusCode = 202, Headers = new Dictionary<string, string[]> { ["X-Test"] = ["one", "two"] }, ContentType = "text/plain", Body = "protected body" }
            }]
        }, 0).Status);
        using var administrator = factory.CreateClient();
        Authenticate(administrator);
        var created = await GenerateKeyAsync(administrator, "\"0\"");
        using var publicClient = factory.CreateClient();
        using var missing = await publicClient.SendAsync(new HttpRequestMessage(new HttpMethod(method), "/protected"));
        using var wrongRequest = new HttpRequestMessage(new HttpMethod(method), "/protected");
        wrongRequest.Headers.Add("X-MockAPI-Key", new string('a', 43));
        using var wrong = await publicClient.SendAsync(wrongRequest);
        using var validRequest = new HttpRequestMessage(new HttpMethod(method), "/protected");
        validRequest.Headers.Add("X-MockAPI-Key", created.Key);
        if (method is "POST" or "PUT" or "PATCH")
        {
            validRequest.Content = new StringContent("{}");
        }
        using var valid = await publicClient.SendAsync(validRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
        Assert.Equal(["one", "two"], valid.Headers.GetValues("X-Test"));
        Assert.Equal(method == "HEAD" ? "" : "protected body", await valid.Content.ReadAsStringAsync());
        Assert.Equal(1, factory.Services.GetRequiredService<RequestStatisticsCollector>().GetSnapshot().TotalRequests);
    }

    [Fact]
    public async Task Rotation_RevokesOldKeyRejectsStaleWritesAndPersistsOnlyHash()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Authenticate(client);
        var first = await GenerateKeyAsync(client, "\"0\"");
        var second = await GenerateKeyAsync(client, first.Status.ETag);
        using var staleRequest = new HttpRequestMessage(HttpMethod.Post, "/__mockapi/api/security/key");
        staleRequest.Headers.TryAddWithoutValidation("If-Match", first.Status.ETag);
        using var stale = await client.SendAsync(staleRequest);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

        var security = factory.Services.GetRequiredService<ApiKeySecurity>();
        Assert.False(Authorizes(security, first.Key));
        Assert.True(Authorizes(security, second.Key));
        Assert.False(Authorizes(security, second.Key, second.Key));
        Assert.False(Authorizes(security, "short"));
        var nullHeader = new DefaultHttpContext();
        nullHeader.Request.Headers["X-MockAPI-Key"] = new StringValues(new string?[] { null });
        Assert.False(security.Authorizes(nullHeader));
        var persisted = await File.ReadAllTextAsync(ConfigurationPath + ".security.json");
        Assert.DoesNotContain(first.Key, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(second.Key, persisted, StringComparison.Ordinal);

        await using var restarted = CreateFactory();
        using var restartedClient = restarted.CreateClient();
        Assert.True(Authorizes(restarted.Services.GetRequiredService<ApiKeySecurity>(), second.Key));
        Assert.Equal(second.Status.ETag, restarted.Services.GetRequiredService<ApiKeySecurity>().Status.ETag);
    }

    [Fact]
    public async Task ConfiguredAdministratorCredentialsAndCurrentIndependentETag_AreRequiredForSecurityWrites()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var anonymous = await client.PostAsync("/__mockapi/api/security/key", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Authenticate(client);
        using var initialStatus = await client.GetAsync("/__mockapi/api/security");
        Assert.Equal(HttpStatusCode.OK, initialStatus.StatusCode);
        var statusJson = await initialStatus.Content.ReadAsStringAsync();
        Assert.DoesNotContain("keyHash", statusJson, StringComparison.Ordinal);
        Assert.Equal("\"0\"", JsonNode.Parse(statusJson)!["etag"]!.GetValue<string>());
        using var missingRevision = await client.PostAsync("/__mockapi/api/security/key", null);
        Assert.Equal((HttpStatusCode)428, missingRevision.StatusCode);
        var created = await GenerateKeyAsync(client, "\"0\"");
        using var disableRequest = new HttpRequestMessage(HttpMethod.Put, "/__mockapi/api/security/")
        {
            Content = JsonContent.Create(new ApiSecurityRequest(false), ManagementJsonContext.Default.ApiSecurityRequest)
        };
        disableRequest.Headers.TryAddWithoutValidation("If-Match", created.Status.ETag);
        using var disabled = await client.SendAsync(disableRequest);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.True(factory.Services.GetRequiredService<ApiKeySecurity>().Authorizes(new DefaultHttpContext()));
        Assert.Equal(0, factory.Services.GetRequiredService<ConfigurationState>().Current.Revision);
    }

    [Fact]
    public async Task MissingEnabledProperty_CannotSilentlyDisableProtection()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Authenticate(client);
        using var request = new HttpRequestMessage(HttpMethod.Put, "/__mockapi/api/security")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(factory.Services.GetRequiredService<ApiKeySecurity>().Status.Enabled);
    }

    [Fact]
    public async Task PersistenceFailure_ReturnsExplicitProblemAndDoesNotActivateKey()
    {
        Directory.CreateDirectory(ConfigurationPath + ".security.json");
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Authenticate(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__mockapi/api/security/key");
        request.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(_directory, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("\"0\"", factory.Services.GetRequiredService<ApiKeySecurity>().Status.ETag);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"enabled":false,"revision":1}""")]
    [InlineData("""{"enabled":true,"keyHash":null,"revision":0}""")]
    [InlineData("""{"enabled":true,"keyHash":null,"revision":9223372036854775807}""")]
    [InlineData("""{"enabled":true,"keyHash":"gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg","revision":1}""")]
    public async Task InvalidSecurityDocuments_NeverDisableTheFailClosedDefault(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ConfigurationPath + ".security.json", json);
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });
        var failure = await Record.ExceptionAsync(() => security.LoadAsync(CancellationToken.None));
        Assert.NotNull(failure);
        Assert.True(security.Status.Enabled);
        Assert.False(security.Authorizes(new DefaultHttpContext()));
    }

    [Fact]
    public async Task OversizedSecurityFile_IsRejectedBeforeDeserialization()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ConfigurationPath + ".security.json", new string(' ', 4097));
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });
        await Assert.ThrowsAsync<InvalidOperationException>(() => security.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitBootstrapOptOut_IsOverriddenByPersistedProtection()
    {
        var options = new MockApiOptions { ConfigurationPath = ConfigurationPath, RequireApiKey = false };
        using var security = new ApiKeySecurity(options);
        await security.LoadAsync(CancellationToken.None);
        Assert.True(security.Authorizes(new DefaultHttpContext()));
        await security.UpdateAsync("\"0\"", false, false, CancellationToken.None);
        using var disabled = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });
        await disabled.LoadAsync(CancellationToken.None);
        Assert.False(disabled.Status.Enabled);
        var created = (await security.UpdateAsync("\"1\"", true, true, CancellationToken.None))!;
        using var restarted = new ApiKeySecurity(options);
        await restarted.LoadAsync(CancellationToken.None);
        Assert.False(restarted.Authorizes(new DefaultHttpContext()));
        Assert.True(Authorizes(restarted, created.Key));
    }

    [Fact]
    public async Task BlobSecurity_SavesAndReloadsWithoutFilesystemState()
    {
        var options = new MockApiOptions
        {
            ConfigurationPath = ConfigurationPath,
            ConfigurationBlobUri = new Uri("https://example.test/config/mockapi.json"),
            ManagedIdentityClientId = "00000000-0000-0000-0000-000000000001"
        };
        using var clientConstruction = new ApiKeySecurity(options);
        var blob = new SecurityBlobClient();
        using var security = new ApiKeySecurity(options, blob);
        await security.LoadAsync(CancellationToken.None);
        Assert.True(security.Status.Enabled);
        var created = (await security.UpdateAsync("\"0\"", true, true, CancellationToken.None))!;
        Assert.DoesNotContain(created.Key, Encoding.UTF8.GetString(blob.Content!), StringComparison.Ordinal);
        using var reloaded = new ApiKeySecurity(options, blob);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.True(Authorizes(reloaded, created.Key));
        Assert.False(Directory.Exists(_directory));
        blob.Content = new byte[4097];
        await Assert.ThrowsAsync<InvalidOperationException>(() => reloaded.LoadAsync(CancellationToken.None));
        blob.Failure = new RequestFailedException(500, "storage failed");
        await Assert.ThrowsAsync<RequestFailedException>(() => reloaded.LoadAsync(CancellationToken.None));
        Assert.True(Authorizes(reloaded, created.Key));
    }

    [Fact]
    public async Task ConcurrentRotations_CommitOnlyOneWinningKey()
    {
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });
        var results = await Task.WhenAll(
            security.UpdateAsync("\"0\"", true, true, CancellationToken.None),
            security.UpdateAsync("\"0\"", true, true, CancellationToken.None));
        var winner = Assert.Single(results, result => result is not null)!;
        Assert.True(Authorizes(security, winner.Key));
        Assert.Equal("\"1\"", security.Status.ETag);
    }

    [Fact]
    public async Task FailedPersistence_LeavesAuthorizationAndRevisionUnchanged()
    {
        Directory.CreateDirectory(ConfigurationPath + ".security.json");
        using var security = new ApiKeySecurity(new MockApiOptions { ConfigurationPath = ConfigurationPath });
        var failure = await Record.ExceptionAsync(() => security.UpdateAsync("\"0\"", true, true, CancellationToken.None));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal("\"0\"", security.Status.ETag);
        Assert.False(security.Status.Configured);
        Assert.False(security.Authorizes(new DefaultHttpContext()));
    }

    [Fact]
    public async Task InvalidPersistedSecurity_FailsStartupRatherThanAllowingCalls()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(ConfigurationPath + ".security.json", """{"enabled":false,"keyHash":"invalid","revision":1}""");
        using var factory = CreateFactory(apiKey: new string('a', 32));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task ManagementOpenApi_DescribesSecurityOperationsWithoutExposingHash()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Authenticate(client);
        var json = await client.GetStringAsync("/__mockapi/openapi/v1.json");
        var document = JsonNode.Parse(json)!;
        Assert.Equal("GetApiSecurity", document["paths"]!["/__mockapi/api/security"]!["get"]!["operationId"]!.GetValue<string>());
        Assert.Equal("RotateMockApiKey", document["paths"]!["/__mockapi/api/security/key"]!["post"]!["operationId"]!.GetValue<string>());
        Assert.NotNull(document["paths"]!["/__mockapi/api/security"]!["put"]!["requestBody"]);
        Assert.DoesNotContain("keyHash", json, StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> CreateFactory(bool authenticated = true, string? apiKey = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MockApi:ConfigurationPath", ConfigurationPath);
            if (apiKey is not null)
            {
                builder.UseSetting("MockApi:ApiKey", apiKey);
            }
            if (authenticated)
            {
                var salt = new byte[16];
                var hash = Rfc2898DeriveBytes.Pbkdf2("test-password", salt, 100_000, HashAlgorithmName.SHA256, 32);
                builder.UseSetting("MockApi:DashboardUsername", "operator");
                builder.UseSetting("MockApi:DashboardPasswordHash", $"v1.100000.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
            }
        });

    private static void Authenticate(HttpClient client) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("operator:test-password")));

    private static async Task<ApiKeyCreated> GenerateKeyAsync(HttpClient client, string etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/__mockapi/api/security/key");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var created = (await response.Content.ReadFromJsonAsync(ManagementJsonContext.Default.ApiKeyCreated))!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", created.Key);
        return created;
    }

    private static bool Authorizes(ApiKeySecurity security, params string[] keys)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MockAPI-Key"] = keys;
        return security.Authorizes(context);
    }

    private sealed class SecurityBlobClient : BlobClient
    {
        internal byte[]? Content { get; set; }
        internal RequestFailedException? Failure { get; set; }
        internal RequestFailedException? UploadFailure { get; set; }

        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(
            BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Failure is not null) throw Failure;
            if (Content is null) throw new RequestFailedException(404, "missing");
            var details = BlobsModelFactory.BlobDownloadDetails(contentLength: Content.Length);
            var result = BlobsModelFactory.BlobDownloadStreamingResult(new MemoryStream(Content), details);
            return Task.FromResult(Response.FromValue(result, new SecurityResponse()));
        }

        public override Task<Response<BlobContentInfo>> UploadAsync(
            BinaryData content, bool overwrite = false, CancellationToken cancellationToken = default)
        {
            if (UploadFailure is not null) throw UploadFailure;
            Content = content.ToArray();
            return Task.FromResult(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, null, null, null, null, 0),
                new SecurityResponse()));
        }
    }

    private sealed class SecurityResponse : Response
    {
        public override int Status => 200;
        public override string ReasonPhrase => "OK";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => false;
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
        protected override bool TryGetHeader(string name, out string value) { value = ""; return false; }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = []; return false; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
