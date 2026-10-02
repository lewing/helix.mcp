using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.AspNetCore;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public sealed class AcquisitionErrorMcpTests
{
    [Fact]
    public async Task AddAcquisitionErrorFilter_ConvertsExceptionToStructuredToolError()
    {
        var handler = CreateFilteredHandler((_, _) => throw AcquisitionAssertions.Exception(
            AcquisitionErrorKind.RateLimited,
            "azdo",
            "get_build_log",
            new Dictionary<string, object?>
            {
                ["org"] = "dnceng-public",
                ["project"] = "public",
                ["buildId"] = 12345,
                ["logId"] = 7
            },
            httpStatus: 429,
            retryAfterSeconds: 30));

        var result = await handler(CreateRequest("azdo_log"), CancellationToken.None);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
        Assert.True(result.StructuredContent.HasValue);
        AssertStructuredError(result.StructuredContent.Value, "rate_limited", "azdo", "get_build_log", 429, 30);
    }

    [Fact]
    public async Task AzdoLog_AcquisitionError_ReturnsMcpErrorWithStructuredContent()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<string?>(AcquisitionAssertions.Exception(
                AcquisitionErrorKind.NotFound,
                "azdo",
                "get_build_log",
                new Dictionary<string, object?>
                {
                    ["org"] = "dnceng-public",
                    ["project"] = "public",
                    ["buildId"] = 12345,
                    ["logId"] = 7
                },
                httpStatus: 404)));

        using var host = await CreateAzdoMcpHostAsync(api);
        using var httpClient = host.GetTestServer().CreateClient();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/") }, httpClient);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("azdo_log", new Dictionary<string, object?>
        {
            ["buildIdOrUrl"] = "12345",
            ["logId"] = 7
        });

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        var error = result.StructuredContent.Value.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("kind").GetString());
        Assert.Equal("azdo", error.GetProperty("provider").GetString());
        Assert.Equal("get_build_log", error.GetProperty("operation").GetString());
        Assert.Equal(404, error.GetProperty("httpStatus").GetInt32());
        Assert.Equal(12345, error.GetProperty("resource").GetProperty("buildId").GetInt32());
        Assert.Equal(7, error.GetProperty("resource").GetProperty("logId").GetInt32());
    }

    [Fact]
    public async Task AzdoLog_EmptySuccess_RemainsRawTextWithoutStructuredError()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 7, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        api.GetBuildLogsListAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 7, LineCount = 0 } });

        using var host = await CreateAzdoMcpHostAsync(api);
        using var httpClient = host.GetTestServer().CreateClient();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/") }, httpClient);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("azdo_log", new Dictionary<string, object?>
        {
            ["buildIdOrUrl"] = "12345",
            ["logId"] = 7
        });

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal(string.Empty, text.Text);
        Assert.False(result.StructuredContent.HasValue);
    }

    [Fact]
    public async Task AzdoLog_EmptyBodyAbsentFromMetadata_ReturnsMcpErrorWithStructuredContent()
    {
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 999999, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        api.GetBuildLogsListAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 1, LineCount = 10 } });
        api.GetTimelineAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new AzdoTimelineRecord { Id = "task1", Log = new AzdoLogReference { Id = 2 } }
                ]
            });

        using var host = await CreateAzdoMcpHostAsync(api);
        using var httpClient = host.GetTestServer().CreateClient();
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri("http://localhost/") }, httpClient);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("azdo_log", new Dictionary<string, object?>
        {
            ["buildIdOrUrl"] = "12345",
            ["logId"] = 999999
        });

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        var error = result.StructuredContent.Value.GetProperty("error");
        Assert.Equal("not_found", error.GetProperty("kind").GetString());
        Assert.Equal("azdo", error.GetProperty("provider").GetString());
        Assert.Equal("get_build_log", error.GetProperty("operation").GetString());
        Assert.Equal(12345, error.GetProperty("resource").GetProperty("buildId").GetInt32());
        Assert.Equal(999999, error.GetProperty("resource").GetProperty("logId").GetInt32());
    }

    private static void AssertStructuredError(
        JsonElement structuredContent,
        string kind,
        string provider,
        string operation,
        int httpStatus,
        int retryAfterSeconds)
    {
        var error = structuredContent.GetProperty("error");
        Assert.Equal(kind, error.GetProperty("kind").GetString());
        Assert.Equal(provider, error.GetProperty("provider").GetString());
        Assert.Equal(operation, error.GetProperty("operation").GetString());
        Assert.Equal(httpStatus, error.GetProperty("httpStatus").GetInt32());
        Assert.Equal(retryAfterSeconds, error.GetProperty("retryAfterSeconds").GetInt32());
        var resource = error.GetProperty("resource");
        Assert.Equal("dnceng-public", resource.GetProperty("org").GetString());
        Assert.Equal("public", resource.GetProperty("project").GetString());
        Assert.Equal(12345, resource.GetProperty("buildId").GetInt32());
        Assert.Equal(7, resource.GetProperty("logId").GetInt32());
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateFilteredHandler(
        Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> next)
    {
        var options = new McpServerOptions().AddAcquisitionErrorFilter();
        var filter = Assert.Single(options.Filters.Request.CallToolFilters);
        return filter((request, ct) => next(request, ct));
    }

    private static RequestContext<CallToolRequestParams> CreateRequest(string toolName)
        => new(
            server: Substitute.For<McpServer>(),
            jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" },
            parameters: new CallToolRequestParams { Name = toolName });

    private static async Task<IHost> CreateAzdoMcpHostAsync(IAzdoApiClient api) =>
        await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(api);
                    services.AddSingleton(Substitute.For<IAzdoTokenAccessor>());
                    services.AddSingleton(sp => new AzdoService(sp.GetRequiredService<IAzdoApiClient>()));

                    services.AddMcpServer(options =>
                        {
                            options.AddBindingErrorFilter();
                            options.AddAcquisitionErrorFilter();
                        })
                        .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
                        .WithTools<AzdoMcpTools>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp());
                });
            })
            .StartAsync();
}
