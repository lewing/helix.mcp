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

    // ── Stage B: Unknown-param filter with Levenshtein hints (Issue #81) ──────

    [Fact]
    public async Task UnknownParamFilter_CanonicalArgsOnly_DoNotThrow()
    {
        // Smoke: canonical params must not be flagged by the unknown-param filter.
        var tools = CreateAzdoTools(out var client);
        client.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("org", "dnceng-public"), ("project", "public")));

        await handler(request, CancellationToken.None);
    }

    [Fact]
    public async Task UnknownParamFilter_AliasedArgPassesAfterNormalization()
    {
        // Alias 'result' is renamed to 'resultFilter' by AddBindingErrorFilter before
        // AddUnknownParameterFilter runs; the renamed key is canonical and must not be flagged.
        var tools = CreateAzdoTools(out var client);
        client.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Id = "tl1", Records = [] });
        var handler = CreateUnknownParamFilteredToolHandler("azdo_search_timeline", tools);
        var request = CreateRequest("azdo_search_timeline", Arguments(
            ("result", "failed"),
            ("buildIdOrUrl", "42"),
            ("pattern", "x")));

        await handler(request, CancellationToken.None);
    }

    [Fact]
    public async Task UnknownParamFilter_SingleUnknownCloseMatch_ThrowsMcpExceptionWithHint()
    {
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("minFinishTime", "2024-01-01")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("minFinishTime", ex.Message);
        Assert.Contains("Did you mean: minTime?", ex.Message);
        Assert.Contains("Allowed parameters:", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_SingleUnknownNoCloseMatch_ThrowsMcpExceptionWithoutHint()
    {
        // 'zzzzzzzzzz' has Levenshtein distance ≥ 10 from every azdo_builds param (all > threshold-6).
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("zzzzzzzzzz", "bar")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("zzzzzzzzzz", ex.Message);
        Assert.DoesNotContain("Did you mean:", ex.Message);
        Assert.Contains("Allowed parameters:", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_MultipleUnknowns_AllSurfacedInMessage()
    {
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(
            ("minFinishTime", "x"),
            ("fooBar", "y")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("minFinishTime", ex.Message);
        Assert.Contains("fooBar", ex.Message);
        Assert.Contains("Allowed parameters:", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_Threshold6Regression_MinFinishTimeGetsMinTimeHint()
    {
        // minFinishTime → minTime has Levenshtein distance = 6 (removes "finish" infix).
        // Threshold-6 (not spec's original 3) is required for this regression case.
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(("minFinishTime", "2024-01-01")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("Did you mean: minTime?", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_MissingRequiredParam_StillWrapsMcpException()
    {
        // Stage B skips when argument count is zero; AddBindingErrorFilter still wraps the
        // SDK ArgumentException for a missing required parameter.
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_build", tools);
        var request = CreateRequest("azdo_build", Arguments());

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.IsType<ArgumentException>(ex.InnerException);
        Assert.Contains("Parameter binding error for 'azdo_build'", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_ParameterlessTool_AnyArgFlaggedUnknown()
    {
        // azdo_auth_status has no parameters; its canonical set is empty, so any
        // argument is treated as unknown and the allowed list is "(none)".
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_auth_status", tools);
        var request = CreateRequest("azdo_auth_status", Arguments(("unknownArg", "value")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Contains("unknownArg", ex.Message);
        Assert.Contains("Allowed parameters: (none).", ex.Message);
    }

    [Fact]
    public async Task UnknownParamFilter_CaseInsensitiveCanonicalMatch_KnownPassesUnknownFlagged()
    {
        // Canonical set is OrdinalIgnoreCase: 'ORG' matches 'org' → not unknown.
        // 'MINFINISHTIME' has no case-insensitive canonical match → flagged with hint.
        var tools = CreateAzdoTools(out _);
        var handler = CreateUnknownParamFilteredToolHandler("azdo_builds", tools);
        var request = CreateRequest("azdo_builds", Arguments(
            ("ORG", "dnceng-public"),
            ("MINFINISHTIME", "2024-01-01")));

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.DoesNotContain("'ORG'", ex.Message);
        Assert.Contains("MINFINISHTIME", ex.Message);
    }

    // ── Levenshtein / FindClosestMatch direct tests (threshold boundary) ─────

    [Fact]
    public void Levenshtein_MinFinishTimeToMinTime_IsExactlyThreshold6()
    {
        // This is the key regression distance that drove the threshold choice.
        var dist = McpServerOptionsExtensions.Levenshtein("minfinishtime", "mintime");
        Assert.Equal(6, dist);
    }

    [Fact]
    public void FindClosestMatch_Distance6_ReturnsSuggestion()
    {
        // minFinishTime → minTime is exactly at threshold-6; the hint must fire.
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "org", "project", "top", "branch", "prNumber", "definitionId",
              "status", "minTime", "maxTime", "queryOrder" };

        var result = McpServerOptionsExtensions.FindClosestMatch("minFinishTime", candidates);

        Assert.Equal("minTime", result);
    }

    [Fact]
    public void FindClosestMatch_FarDistance_ReturnsNull()
    {
        // 'zzzzzzzzzz' is ≥ 10 Levenshtein from every azdo_builds param (all exceed threshold-6).
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "org", "project", "top", "branch", "prNumber", "definitionId",
              "status", "minTime", "maxTime", "queryOrder" };

        var result = McpServerOptionsExtensions.FindClosestMatch("zzzzzzzzzz", candidates);

        Assert.Null(result);
    }

    // ── ExtractToolParamInfo schema edge-case tests ──────────────────────────

    [Fact]
    public void ExtractToolParamInfo_AdditionalPropertiesTrue_ReturnsNull()
    {
        // Schema with additionalProperties: true means any extra key is allowed;
        // the filter must be skipped for such tools (returns null).
        var schema = JsonSerializer.Deserialize<JsonElement>(
            """{"properties": {"foo": {"type": "string"}}, "additionalProperties": true}""");

        var result = McpServerOptionsExtensions.ExtractToolParamInfo(schema, "test_tool", logger: null);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractToolParamInfo_MissingSchema_ReturnsNull()
    {
        // Undefined schema must not produce false positives — filter is skipped.
        var result = McpServerOptionsExtensions.ExtractToolParamInfo(
            default, "test_tool", logger: null);

        Assert.Null(result);
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult>
        CreateUnknownParamFilteredToolHandler(string toolName, AzdoMcpTools tools)
    {
        var options = new McpServerOptions()
            .AddBindingErrorFilter()
            .AddUnknownParameterFilter(typeof(AzdoMcpTools).Assembly);
        Assert.Equal(2, options.Filters.Request.CallToolFilters.Count);

        var toolCreateOptions = new McpServerToolCreateOptions
        {
            SerializerOptions = new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver()
            }
        };
        var method = typeof(AzdoMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
        var tool = McpServerTool.Create(method, tools, toolCreateOptions);

        // Compose: binding-error-filter (aliases + exception wrap) → unknown-param-filter → tool
        var bindingErrorFilter = options.Filters.Request.CallToolFilters[0];
        var unknownParamFilter = options.Filters.Request.CallToolFilters[1];
        McpRequestHandler<CallToolRequestParams, CallToolResult> baseHandler =
            (req, ct) => tool.InvokeAsync(req, ct);
        return bindingErrorFilter(unknownParamFilter(baseHandler));
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
