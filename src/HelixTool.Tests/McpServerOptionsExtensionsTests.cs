using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

    // ── Strict UnmappedMemberHandling.Disallow tests (Issue #81 Stage A) ─────────

    [Fact]
    public async Task StrictOptions_CanonicalArgsOnly_DoNotThrow()
    {
        // Smoke regression: canonical params must not be flagged as unknown by Disallow.
        var tools = CreateAzdoTools(out var client);
        client.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());
        var handler = CreateStrictFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("org", "dnceng-public"), ("project", "public")));

        // Should complete without throwing — strict mode must not reject known params.
        await handler(request, CancellationToken.None);
    }

    [Fact]
    public async Task StrictOptions_ResultAliasReachesService_ViaAzdoSearchTimeline()
    {
        // 'result' is the historical alias; the filter renames it to 'resultFilter' before strict
        // mode fires, so the call must succeed end-to-end.
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
        var handler = CreateStrictFilteredToolHandler("azdo_search_timeline", tools);
        var request = CreateRequest("azdo_search_timeline", Arguments(
            ("buildIdOrUrl", "42"),
            ("pattern", "tests"),
            ("result", "failed")));

        await handler(request, CancellationToken.None);

        await client.Received(1).GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StrictOptions_UnknownParam_ThrowsMcpException()
    {
        // Unknown params must surface as McpException rather than being silently dropped.
        var tools = CreateAzdoTools(out _);
        var handler = CreateStrictFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("minFinishTime", "2024-01-01")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("Parameter binding error", ex.Message);
        Assert.IsType<ArgumentException>(ex.InnerException);
    }

    [Fact]
    public async Task StrictOptions_UnknownParam_ErrorMessageNamesTheBadKey()
    {
        // Callers need actionable feedback — the unknown key name must appear in the error.
        var tools = CreateAzdoTools(out _);
        var handler = CreateStrictFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("minFinishTime", "2024-01-01")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        var fullMessage = ex.Message + " " + ex.InnerException?.Message;
        Assert.Contains("minFinishTime", fullMessage);
    }

    [Fact]
    public async Task StrictOptions_MultipleUnknownParams_ThrowsMcpExceptionNamingAtLeastOne()
    {
        // The SDK may report only the first unknown key per throw; at minimum one name must appear.
        var tools = CreateAzdoTools(out _);
        var handler = CreateStrictFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(
            ("minFinishTime", "2024-01-01"),
            ("maxTime", "2024-12-31")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("Parameter binding error", ex.Message);
        var fullMessage = ex.Message + " " + ex.InnerException?.Message;
        Assert.True(
            fullMessage.Contains("minFinishTime") || fullMessage.Contains("maxTime"),
            $"Expected at least one unknown key name in error message, got: {fullMessage}");
    }

    [Fact]
    public async Task StrictOptions_MissingRequiredParam_StillThrowsMcpException()
    {
        // Existing missing-required-param behavior must be unchanged alongside Disallow.
        var tools = CreateAzdoTools(out _);
        var handler = CreateStrictFilteredToolHandler("azdo_build", tools);
        var request = CreateRequest("azdo_build", Arguments());

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("Parameter binding error for 'azdo_build'", ex.Message);
        Assert.Contains("buildIdOrUrl", ex.Message);
    }

    // ── Alias-key removal tests ───────────────────────────────────────────────

    [Fact]
    public async Task NormalizeArgumentAliases_RemovesResultAliasKey_AfterRename()
    {
        // After the filter renames 'result' → 'resultFilter', the original 'result' key must be
        // absent so strict mode does not flag it as an unknown parameter.
        var handler = CreateFilteredHandler((request, _) =>
        {
            var args = request.Params!.Arguments!;
            Assert.False(args.ContainsKey("result"), "'result' key should be removed after alias rename");
            Assert.True(args.ContainsKey("resultFilter"), "'resultFilter' key should be present after rename");
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_search_timeline", Arguments(
            ("buildIdOrUrl", "42"),
            ("pattern", "tests"),
            ("result", "failed")));

        await handler(request, CancellationToken.None);
    }

    [Fact]
    public async Task NormalizeArgumentAliases_RemovesBuildIdAliasKey_AfterRename()
    {
        // After the filter renames 'build_id' → 'buildIdOrUrl', the original 'build_id' key must
        // be absent so strict mode does not flag it as an unknown parameter.
        var handler = CreateFilteredHandler((request, _) =>
        {
            var args = request.Params!.Arguments!;
            Assert.False(args.ContainsKey("build_id"), "'build_id' key should be removed after alias rename");
            Assert.True(args.ContainsKey("buildIdOrUrl"), "'buildIdOrUrl' key should be present after rename");
            return ValueTask.FromResult(new CallToolResult());
        });
        var request = CreateRequest("azdo_builds", Arguments(("build_id", "42")));

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

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateStrictFilteredToolHandler(
        string toolName,
        AzdoMcpTools tools)
    {
        var strictOptions = new McpServerToolCreateOptions
        {
            SerializerOptions = new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            }
        };
        var method = typeof(AzdoMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var tool = McpServerTool.Create(method, tools, strictOptions);
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
