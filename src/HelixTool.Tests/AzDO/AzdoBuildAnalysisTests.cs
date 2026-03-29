using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoBuildAnalysisTests
{
    private readonly IAzdoApiClient _client;
    private readonly AzdoService _svc;

    public AzdoBuildAnalysisTests()
    {
        _client = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_client);
    }

    private void SetupBuild(AzdoBuild build, int buildId = 42,
        string org = "dnceng-public", string project = "public")
    {
        _client.GetBuildAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(build);
    }

    private void SetupTimeline(AzdoTimeline? timeline, int buildId = 42,
        string org = "dnceng-public", string project = "public")
    {
        _client.GetTimelineAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(timeline);
    }

    [Fact]
    public async Task ExtractsKnownIssuesFromBuildTags()
    {
        SetupBuild(new AzdoBuild
        {
            Id = 42,
            Result = "failed",
            Tags = ["Known test failure: https://github.com/dotnet/runtime/issues/12345"]
        });
        SetupTimeline(new AzdoTimeline { Id = "tl1", Records = [] });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Single(result.KnownIssues);
        Assert.Equal(12345, result.KnownIssues[0].IssueNumber);
        Assert.Equal("dotnet/runtime", result.KnownIssues[0].Repository);
        Assert.Equal("https://github.com/dotnet/runtime/issues/12345", result.KnownIssues[0].IssueUrl);
        Assert.Contains("build tags", result.AnalysisSource!);
    }

    [Fact]
    public async Task ExtractsKnownIssuesFromTimelineIssueMessages()
    {
        SetupBuild(new AzdoBuild { Id = 42, Result = "failed" });
        SetupTimeline(new AzdoTimeline
        {
            Id = "tl1",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "r1", Name = "Run tests", Type = "Task", Result = "failed",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "error",
                            Message = "Known issue: https://github.com/dotnet/runtime/issues/99999"
                        }
                    ]
                }
            ]
        });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Single(result.KnownIssues);
        Assert.Equal(99999, result.KnownIssues[0].IssueNumber);
        Assert.Contains("Run tests", result.KnownIssues[0].MatchedFailures);
    }

    [Fact]
    public async Task DeduplicatesIssuesFromTagsAndTimeline()
    {
        SetupBuild(new AzdoBuild
        {
            Id = 42,
            Result = "failed",
            Tags = ["Known test failure: https://github.com/dotnet/runtime/issues/11111"]
        });
        SetupTimeline(new AzdoTimeline
        {
            Id = "tl1",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "r1", Name = "Test step", Type = "Task", Result = "failed",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "error",
                            Message = "Matched https://github.com/dotnet/runtime/issues/11111"
                        }
                    ]
                }
            ]
        });

        var result = await _svc.GetBuildAnalysisAsync("42");

        // Same issue URL from both sources — should appear once
        Assert.Single(result.KnownIssues);
        Assert.Equal(11111, result.KnownIssues[0].IssueNumber);
        Assert.Contains("Test step", result.KnownIssues[0].MatchedFailures);
    }

    [Fact]
    public async Task CollectsUnmatchedErrorsFromTimeline()
    {
        SetupBuild(new AzdoBuild { Id = 42, Result = "failed" });
        SetupTimeline(new AzdoTimeline
        {
            Id = "tl1",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "r1", Name = "Build step", Type = "Task", Result = "failed",
                    Issues =
                    [
                        new AzdoIssue { Type = "error", Message = "error CS1234: Something bad" }
                    ]
                }
            ]
        });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Empty(result.KnownIssues);
        Assert.Single(result.UnmatchedFailures);
        Assert.Contains("CS1234", result.UnmatchedFailures[0]);
    }

    [Fact]
    public async Task NoIssuesOrFailures_ReturnsEmptyResult()
    {
        SetupBuild(new AzdoBuild { Id = 42, Result = "succeeded" });
        SetupTimeline(new AzdoTimeline { Id = "tl1", Records = [] });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Empty(result.KnownIssues);
        Assert.Empty(result.UnmatchedFailures);
        Assert.Null(result.AnalysisSource);
        Assert.Equal("succeeded", result.BuildResult);
    }

    [Fact]
    public async Task NullBuild_ThrowsInvalidOperation()
    {
        _client.GetBuildAsync("dnceng-public", "public", 999, Arg.Any<CancellationToken>())
            .Returns((AzdoBuild?)null);
        SetupTimeline(new AzdoTimeline { Id = "tl1", Records = [] }, buildId: 999);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.GetBuildAnalysisAsync("999"));
    }

    [Fact]
    public async Task ExtractsIssueTitleFromTag()
    {
        SetupBuild(new AzdoBuild
        {
            Id = 42,
            Result = "failed",
            Tags = ["Known test failure: Flaky timeout in HttpClient (https://github.com/dotnet/runtime/issues/55555)"]
        });
        SetupTimeline(new AzdoTimeline { Id = "tl1", Records = [] });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Single(result.KnownIssues);
        Assert.Equal("Flaky timeout in HttpClient", result.KnownIssues[0].IssueTitle);
    }

    [Fact]
    public async Task MultipleDistinctKnownIssues()
    {
        SetupBuild(new AzdoBuild
        {
            Id = 42,
            Result = "failed",
            Tags =
            [
                "Known test failure: https://github.com/dotnet/runtime/issues/11111",
                "Known test failure: https://github.com/dotnet/aspnetcore/issues/22222"
            ]
        });
        SetupTimeline(new AzdoTimeline { Id = "tl1", Records = [] });

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Equal(2, result.KnownIssues.Count);
        Assert.Contains(result.KnownIssues, ki => ki.IssueNumber == 11111 && ki.Repository == "dotnet/runtime");
        Assert.Contains(result.KnownIssues, ki => ki.IssueNumber == 22222 && ki.Repository == "dotnet/aspnetcore");
    }

    [Fact]
    public async Task NullTimeline_StillExtractsFromTags()
    {
        SetupBuild(new AzdoBuild
        {
            Id = 42,
            Result = "failed",
            Tags = ["Known test failure: https://github.com/dotnet/runtime/issues/33333"]
        });
        SetupTimeline(null);

        var result = await _svc.GetBuildAnalysisAsync("42");

        Assert.Single(result.KnownIssues);
        Assert.Equal(33333, result.KnownIssues[0].IssueNumber);
    }
}
