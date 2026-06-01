using System.Reflection;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class McpServerOptionsExtensionsTests
{
    [Theory]
    [InlineData("build_id")]
    [InlineData("buildId")]
    [InlineData("buildUrl")]
    public async Task AddBindingErrorFilter_MapsBuildIdOrUrlAliases_WhenCanonicalAbsent(string alias)
    {
        var handler = CreateFilteredHandler((request, _) =>
        {
            var value = AssertBuildIdOrUrl(request);
            Assert.Equal("42", value.GetString());
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_build", Arguments((alias, "42")));

        await handler(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("build_id")]
    [InlineData("buildId")]
    public async Task AddBindingErrorFilter_CoercesNumericBuildIdOrUrlAliasesToString(string alias)
    {
        var handler = CreateFilteredHandler((request, _) =>
        {
            var value = AssertBuildIdOrUrl(request);
            Assert.Equal(JsonValueKind.String, value.ValueKind);
            Assert.Equal("2989057", value.GetString());
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_search_timeline", Arguments((alias, 2989057), ("pattern", "tests")));

        await handler(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("build_id")]
    [InlineData("buildId")]
    public async Task AddBindingErrorFilter_EndToEndSearchTimeline_NumericAliasBindsAsString(string alias)
    {
        var tools = CreateAzdoTools(out var client);
        client.GetTimelineAsync("dnceng-public", "public", 2989057, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Id = "tl1",
                Records =
                [
                    new AzdoTimelineRecord
                    {
                        Id = "r1",
                        Name = "Run tests",
                        Type = "Task",
                        Result = "failed",
                        State = "completed"
                    }
                ]
            });
        var handler = CreateFilteredToolHandler("azdo_search_timeline", tools);
        var request = CreateRequest("azdo_search_timeline", Arguments(
            (alias, 2989057),
            ("pattern", "tests")));

        await handler(request, CancellationToken.None);

        await client.Received(1).GetTimelineAsync("dnceng-public", "public", 2989057, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBindingErrorFilter_PreservesCanonicalValue_WhenAliasAlsoSupplied()
    {
        var handler = CreateFilteredHandler((request, _) =>
        {
            var value = AssertBuildIdOrUrl(request);
            Assert.Equal("canonical", value.GetString());
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_build", Arguments(
            ("buildIdOrUrl", "canonical"),
            ("build_id", "alias")));

        await handler(request, CancellationToken.None);
    }

    [Fact]
    public async Task AddBindingErrorFilter_StillConvertsBinderError_WhenNoCanonicalOrAliasExists()
    {
        var tools = CreateAzdoTools(out _);
        var handler = CreateFilteredToolHandler("azdo_build", tools);
        var request = CreateRequest("azdo_build", Arguments());

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("Parameter binding error for 'azdo_build'", ex.Message);
        Assert.Contains("buildIdOrUrl", ex.Message);
    }

    [Fact]
    public async Task AddBindingErrorFilter_EndToEndBuildAnalysis_AliasReachesService()
    {
        var tools = CreateAzdoTools(out var client);
        client.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Result = "succeeded" });
        client.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Id = "tl1", Records = [] });
        var handler = CreateFilteredToolHandler("azdo_build_analysis", tools);
        var request = CreateRequest("azdo_build_analysis", Arguments(("build_id", "42")));

        await handler(request, CancellationToken.None);

        await client.Received(1).GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
        await client.Received(1).GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBindingErrorFilter_EndToEndSearchTimeline_AliasReachesService()
    {
        var tools = CreateAzdoTools(out var client);
        client.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Id = "tl1",
                Records =
                [
                    new AzdoTimelineRecord
                    {
                        Id = "r1",
                        Name = "Run tests",
                        Type = "Task",
                        Result = "failed",
                        State = "completed"
                    }
                ]
            });
        var handler = CreateFilteredToolHandler("azdo_search_timeline", tools);
        var request = CreateRequest("azdo_search_timeline", Arguments(
            ("buildUrl", "42"),
            ("pattern", "tests")));

        await handler(request, CancellationToken.None);

        await client.Received(1).GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBindingErrorFilter_UsesFirstAliasPrecedence_WhenMultipleAliasesSuppliedWithoutCanonical()
    {
        var handler = CreateFilteredHandler((request, _) =>
        {
            var value = AssertBuildIdOrUrl(request);
            Assert.Equal("from-build-id", value.GetString());
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_build", Arguments(
            ("buildUrl", "from-build-url"),
            ("buildId", "from-buildId"),
            ("build_id", "from-build-id")));

        await handler(request, CancellationToken.None);
    }

    [Theory]
    [InlineData("BUILD_ID")]
    [InlineData("BuildID")]
    [InlineData("BUILDURL")]
    public async Task AddBindingErrorFilter_MatchesAliasKeysCaseInsensitively(string alias)
    {
        var handler = CreateFilteredHandler((request, _) =>
        {
            var value = AssertBuildIdOrUrl(request);
            Assert.Equal("42", value.GetString());
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_build", Arguments((alias, "42")));

        await handler(request, CancellationToken.None);
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateFilteredHandler(
        Func<RequestContext<CallToolRequestParams>, CancellationToken, ValueTask<CallToolResult>> next)
    {
        var options = new McpServerOptions().AddBindingErrorFilter();
        var filter = Assert.Single(options.Filters.Request.CallToolFilters);
        return filter((request, ct) => next(request, ct));
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateFilteredToolHandler(
        string toolName,
        AzdoMcpTools tools)
    {
        var method = typeof(AzdoMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var tool = McpServerTool.Create(method, tools, options: null);
        return CreateFilteredHandler((request, ct) => tool.InvokeAsync(request, ct));
    }

    private static AzdoMcpTools CreateAzdoTools(out IAzdoApiClient client)
    {
        client = Substitute.For<IAzdoApiClient>();
        return new AzdoMcpTools(new AzdoService(client), Substitute.For<IAzdoTokenAccessor>());
    }

    private static JsonElement AssertBuildIdOrUrl(RequestContext<CallToolRequestParams> request)
    {
        Assert.NotNull(request.Params);
        var arguments = request.Params.Arguments;
        Assert.NotNull(arguments);
        Assert.True(arguments.ContainsKey("buildIdOrUrl"));
        return arguments["buildIdOrUrl"];
    }

    private static RequestContext<CallToolRequestParams> CreateRequest(
        string toolName,
        IDictionary<string, JsonElement> arguments)
        => new(
            server: Substitute.For<McpServer>(),
            jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" },
            parameters: new CallToolRequestParams { Name = toolName, Arguments = arguments });

    private static Dictionary<string, JsonElement> Arguments(params (string Key, object? Value)[] values)
    {
        var arguments = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in values)
        {
            arguments[key] = JsonSerializer.SerializeToElement(value);
        }

        return arguments;
    }
}
