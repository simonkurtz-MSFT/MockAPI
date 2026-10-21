using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MockAPI.Configuration;
using MockAPI.Management;

namespace MockAPI.Tests.Management;

public sealed class ConfigurationReadTests
{
    internal static readonly Func<HttpContext, EndpointManagementService, Task> WriteStatus =
        typeof(ManagementApiEndpoints).GetMethod("WriteConfigurationStatusAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<HttpContext, EndpointManagementService, Task>>();

    internal static readonly Func<HttpContext, ConfigurationManagementService, Guid[]?, string?, bool, Task> WriteExport =
        typeof(ManagementApiEndpoints).GetMethod("ExportConfigurationAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<HttpContext, ConfigurationManagementService, Guid[]?, string?, bool, Task>>();

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("text")]
    public async Task Status_PreservesMetadataAndCapturedRevisionAcrossSaveAndReplacement(string metadata)
    {
        var state = CreateState(metadata);
        var dirty = state.Current;
        var captured = ConfigurationStatusResponse.FromSnapshot(dirty);
        var expected = dirty.GetDocument().ApiDescriptions;
        var detached = dirty.GetDocument();
        detached.ApiDescriptions?.Clear();
        var service = new EndpointManagementService(state);

        await AssertStatusAsync(service, dirty, expected);
        Assert.True(state.TryMarkPersisted(dirty.Revision));
        await AssertStatusAsync(service, state.Current, expected);
        Assert.Equal(dirty.Revision, state.Current.Revision);
        Assert.Equal(dirty.ETag, state.Current.ETag);
        Assert.True(captured.HasUnsavedChanges);
        AssertMetadata(expected, captured.ApiDescriptions);
        Assert.Same(dirty.ApiDescriptions, captured.ApiDescriptions);
        Assert.Same(captured.ApiDescriptions, ConfigurationStatusResponse.FromSnapshot(state.Current).ApiDescriptions);

        var replacement = detached with { ApiDescriptions = new() { ["/new"] = "new" } };
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(replacement, dirty.Revision).Status);
        await AssertStatusAsync(service, state.Current, replacement.ApiDescriptions);
        AssertMetadata(expected, ConfigurationStatusResponse.FromSnapshot(dirty).ApiDescriptions);
        AssertMetadata(expected, captured.ApiDescriptions);
    }

    [Theory]
    [InlineData("null", "omitted")]
    [InlineData("empty", "omitted")]
    [InlineData("text", "omitted")]
    [InlineData("text", "empty")]
    [InlineData("text", "selected")]
    [InlineData("text", "unknown")]
    public async Task NativeExport_PreservesCanonicalBytesMetadataAndHeaders(string metadata, string selection)
    {
        var state = CreateState(metadata);
        var snapshot = state.Current;
        var document = snapshot.GetDocument();
        Guid[]? ids = selection switch
        {
            "empty" => [],
            "selected" => [document.Endpoints[1].Id],
            "unknown" => [Guid.NewGuid()],
            _ => null
        };
        var expected = ids is null or { Length: 0 }
            ? snapshot.ExportUtf8()
            : JsonSerializer.SerializeToUtf8Bytes(document with
            {
                Endpoints = document.Endpoints.Where(endpoint => ids.Contains(endpoint.Id)).ToArray()
            }, MockApiJsonContext.Default.MockApiConfigurationDocument);
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await WriteExport(context, new ConfigurationManagementService(state, new UnusedStore()), ids, null, true);

        Assert.Equal(expected, body.ToArray());
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(snapshot.ETag, context.Response.Headers.ETag);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("attachment; filename=mockapi.json", context.Response.Headers.ContentDisposition);
        Assert.Same(snapshot, state.Current);
    }

    [Theory]
    [InlineData("postman")]
    [InlineData("insomnia")]
    [InlineData("openapi")]
    [InlineData("curl")]
    [InlineData("jmeter")]
    [InlineData("k6")]
    [InlineData("http")]
    public async Task GeneratedExports_PreserveFilteredAndUnfilteredArtifacts(string format)
    {
        await using var factory = new UnsecuredApplicationFactory();
        using var client = factory.CreateClient();
        var state = factory.Services.GetRequiredService<ConfigurationState>();
        var document = CreateState("text").Current.GetDocument();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(document, 0).Status);

        foreach (var filtered in new[] { false, true })
        {
            var selected = filtered ? document with { Endpoints = [document.Endpoints[1]] } : document;
            Assert.True(ConfigurationExportService.TryExport(format, selected, out var expected, requireApiKey: false));
            var query = filtered ? $"?endpointId={document.Endpoints[1].Id}" : "";
            using var response = await client.GetAsync($"/__mockapi/api/configuration/export/{format}{query}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(expected!.Content, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(state.Current.ETag, response.Headers.ETag!.Tag);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeExport_DoesNotAllocateADocumentSizedCopy(bool emptySelection)
    {
        var state = CreateState("text", bodyLength: 166000, endpointCount: 25);
        var service = new ConfigurationManagementService(state, new UnusedStore());
        var context = new DefaultHttpContext();
        context.Response.Body = Stream.Null;
        Guid[]? endpointIds = emptySelection ? [] : null;
        await WriteExport(context, service, endpointIds, null, true);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var export = WriteExport(context, service, endpointIds, null, true);
        Assert.True(export.IsCompletedSuccessfully);
        await export;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocatedBytes, 0, 64 * 1024);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("empty")]
    public async Task GeneratedExport_AbsentOrEmptySelectionPreservesAllEndpointsAndMetadata(string selection)
    {
        await using var factory = new UnsecuredApplicationFactory();
        var state = CreateState("text");
        var snapshot = state.Current;
        var document = snapshot.GetDocument();
        Assert.True(ConfigurationExportService.TryExport("openapi", document, out var expected, requireApiKey: false));
        Guid[]? endpointIds = selection == "empty" ? [] : null;
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.RequestServices = factory.Services;
        context.Response.Body = body;
        var service = new ConfigurationManagementService(state, new UnusedStore());

        await WriteExport(context, service, endpointIds, "openapi", true);

        Assert.Equal(expected!.Content, body.ToArray());
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(expected.ContentType, context.Response.ContentType);
        Assert.Equal($"attachment; filename={expected.FileName}", context.Response.Headers.ContentDisposition);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(snapshot.ETag, context.Response.Headers.ETag);
        Assert.Same(snapshot, state.Current);
    }

    internal static ConfigurationState CreateState(string metadata, int bodyLength = 8, int endpointCount = 2)
    {
        Dictionary<string, string>? descriptions = metadata switch
        {
            "null" => null,
            "empty" => [],
            _ => new() { ["/z"] = "  Z\r\n\t ", ["/a"] = "lower", ["/A"] = "" }
        };
        var document = new MockApiConfigurationDocument
        {
            Schema = "../schemas/mockapi.schema.json",
            SchemaVersion = "1.0",
            ApiDescriptions = descriptions,
            Endpoints = Enumerable.Range(0, endpointCount).Select(index => new MockEndpointDefinition
            {
                Id = new Guid(index + 1, 0, 0, new byte[8]),
                Name = $"Read fixture {index}",
                Path = $"/a/read-{index}",
                Methods = ["GET", "HEAD"],
                Enabled = true,
                Response = new MockResponseDefinition
                {
                    StatusCode = 200,
                    Headers = new() { ["X-Fixture"] = ["first", "second"] },
                    ContentType = "text/plain; charset=utf-8",
                    Body = new string('x', bodyLength)
                }
            }).ToArray()
        };
        var state = new ConfigurationState();
        Assert.Equal(ConfigurationUpdateStatus.Applied, state.TryReplace(document, 0).Status);
        return state;
    }

    private static async Task AssertStatusAsync(
        EndpointManagementService service,
        ConfigurationStateSnapshot expectedSnapshot,
        IReadOnlyDictionary<string, string>? expectedMetadata)
    {
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        await WriteStatus(context, service);
        var response = JsonSerializer.Deserialize(body.ToArray(), ManagementJsonContext.Default.ConfigurationStatusResponse)!;
        Assert.Equal(expectedSnapshot.Revision, response.Revision);
        Assert.Equal(expectedSnapshot.ETag, response.ETag);
        Assert.Equal(expectedSnapshot.ETag, context.Response.Headers.ETag);
        Assert.Equal(expectedSnapshot.HasUnsavedChanges, response.HasUnsavedChanges);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        AssertMetadata(expectedMetadata, response.ApiDescriptions);
    }

    private static void AssertMetadata(
        IReadOnlyDictionary<string, string>? expected,
        IReadOnlyDictionary<string, string>? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    internal sealed class UnusedStore : IConfigurationStore
    {
        public Task LoadAsync(ConfigurationState state, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Configuration reads must not load persistence.");

        public Task<ConfigurationSaveResult> SaveAsync(ConfigurationState state, long expectedRevision, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Configuration reads must not save persistence.");
    }
}
