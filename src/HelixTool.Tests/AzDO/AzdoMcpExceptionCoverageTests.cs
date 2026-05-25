using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoMcpExceptionCoverageTests
{
    private const string RequiresCentralization = "Requires Ripley Bug B MCP exception centralization for issue #61 (PR not open as of 2026-05-25).";

    private readonly IAzdoApiClient _api;
    private readonly AzdoMcpTools _tools;

    public AzdoMcpExceptionCoverageTests()
    {
        _api = Substitute.For<IAzdoApiClient>();
        var service = new AzdoService(_api);
        _tools = new AzdoMcpTools(service, Substitute.For<IAzdoTokenAccessor>());
    }

    [Fact(Skip = RequiresCentralization)]
    public async Task BuildAnalysis_WhenTaskWhenAllSurfacesAggregateHttpFailure_ThrowsStructuredMcpException()
    {
        _api.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AzdoBuild?>(new AggregateException(
                new HttpRequestException("primary network failure"))));
        _api.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Id = "timeline", Records = [] });

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.BuildAnalysis("42"));

        AssertStructuredMcpError(ex, "build analysis", "primary network failure");
    }

    [Fact]
    public async Task Builds_WhenHttpRequestExceptionOccurs_ThrowsStructuredMcpException()
    {
        _api.ListBuildsAsync("dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AzdoBuild>>(
                new HttpRequestException("AzDO unavailable")));

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.Builds());

        AssertStructuredMcpError(ex, "list builds", "AzDO unavailable");
    }

    [Fact(Skip = RequiresCentralization)]
    public async Task Timeline_WhenTaskCanceledExceptionOccurs_ThrowsStructuredMcpException()
    {
        _api.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<AzdoTimeline?>(
                new TaskCanceledException("timeline request timed out")));

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.Timeline("42"));

        AssertStructuredMcpError(ex, "build timeline", "timeline request timed out");
    }

    private static void AssertStructuredMcpError(McpException ex, params string[] expectedFragments)
    {
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        foreach (var fragment in expectedFragments)
        {
            Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
