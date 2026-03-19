using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoMcpToolsTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;
    private readonly AzdoMcpTools _tools;

    public AzdoMcpToolsTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
        _tools = new AzdoMcpTools(_svc, Substitute.For<IAzdoTokenAccessor>());
    }

    // ── azdo_build ──────────────────────────────────────────────────

    [Fact]
    public async Task Build_ReturnsBuildSummary()
    {
        var build = new AzdoBuild
        {
            Id = 42,
            BuildNumber = "20250718.1",
            Status = "completed",
            Result = "succeeded",
            Definition = new AzdoBuildDefinition { Id = 100, Name = "runtime" },
            SourceBranch = "refs/heads/main",
            SourceVersion = "abc123",
            StartTime = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            FinishTime = new DateTimeOffset(2025, 7, 18, 10, 5, 0, TimeSpan.Zero),
            RequestedFor = new AzdoIdentityRef { DisplayName = "Larry" }
        };

        _mockApi.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _tools.Build("42");

        Assert.Equal(42, result.Id);
        Assert.Equal("20250718.1", result.BuildNumber);
        Assert.Equal("completed", result.Status);
        Assert.Equal("succeeded", result.Result);
        Assert.Equal("runtime", result.DefinitionName);
        Assert.Equal("refs/heads/main", result.SourceBranch);
        Assert.Equal("Larry", result.RequestedFor);
    }

    [Fact]
    public async Task Build_AcceptsUrl()
    {
        var build = new AzdoBuild { Id = 99 };
        _mockApi.GetBuildAsync("myorg", "myproj", 99, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _tools.Build(
            "https://dev.azure.com/myorg/myproj/_build/results?buildId=99");

        Assert.Equal(99, result.Id);
    }

    // ── azdo_builds (parameter defaults) ────────────────────────────

    [Fact]
    public async Task Builds_DefaultOrgAndProject()
    {
        _mockApi.ListBuildsAsync("dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds();

        await _mockApi.Received(1).ListBuildsAsync(
            "dnceng-public", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_CustomOrgAndProject()
    {
        _mockApi.ListBuildsAsync("custom-org", "custom-proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(org: "custom-org", project: "custom-proj");

        await _mockApi.Received(1).ListBuildsAsync(
            "custom-org", "custom-proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_PassesFilterParameters()
    {
        _mockApi.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(top: 5, branch: "refs/heads/main", definitionId: 42, status: "completed");

        await _mockApi.Received(1).ListBuildsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<AzdoBuildFilter>(f =>
                f.Top == 5 &&
                f.Branch == "refs/heads/main" &&
                f.DefinitionId == 42 &&
                f.StatusFilter == "completed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_ReturnsBuildList()
    {
        var builds = new List<AzdoBuild>
        {
            new() { Id = 1, BuildNumber = "b1" },
            new() { Id = 2, BuildNumber = "b2" }
        };
        _mockApi.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(builds);

        var result = await _tools.Builds();

        Assert.Equal(2, result.Count);
        Assert.Equal(1, result[0].Id);
        Assert.Equal(2, result[1].Id);
    }

    // ── azdo_timeline ───────────────────────────────────────────────

    [Fact]
    public async Task Timeline_ReturnsTimeline()
    {
        var timeline = new AzdoTimeline
        {
            Id = "tl-1",
            Records = [new AzdoTimelineRecord { Id = "r1", Name = "Build", Type = "Stage", Result = "failed" }]
        };
        _mockApi.GetTimelineAsync("dnceng-public", "public", 10, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _tools.Timeline("10");

        Assert.NotNull(result);
        Assert.Equal("tl-1", result!.Id);
        Assert.Single(result.Records);
        Assert.Equal("Build", result.Records[0].Name);
    }

    [Fact]
    public async Task Timeline_NullResult_ReturnsNull()
    {
        _mockApi.GetTimelineAsync("dnceng-public", "public", 10, Arg.Any<CancellationToken>())
            .Returns((AzdoTimeline?)null);

        var result = await _tools.Timeline("10");

        Assert.Null(result);
    }

    [Fact]
    public async Task Timeline_Truncated_WhenRecordsExceedMax()
    {
        // 250 records exceeds MaxTimelineRecords (200)
        const int totalCount = 250;
        var records = Enumerable.Range(0, totalCount)
            .Select(i => new AzdoTimelineRecord { Id = $"r{i}", Name = $"Task {i}", Type = "Task", Result = "succeeded" })
            .ToList();
        var timeline = new AzdoTimeline { Id = "tl-big", Records = records };

        _mockApi.GetTimelineAsync("dnceng-public", "public", 10, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _tools.Timeline("10", filter: "all");

        Assert.NotNull(result);
        Assert.True(result!.Truncated);
        Assert.Equal(totalCount, result.TotalRecords);
        Assert.Equal(100, result.Records.Count); // TruncatedTimelineBudget
        Assert.Contains("truncated", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("250", result.Note!);
    }

    [Fact]
    public async Task Timeline_NotTruncated_WhenRecordsBelowMax()
    {
        // 150 records is under MaxTimelineRecords (200)
        const int totalCount = 150;
        var records = Enumerable.Range(0, totalCount)
            .Select(i => new AzdoTimelineRecord { Id = $"r{i}", Name = $"Task {i}", Type = "Task", Result = "succeeded" })
            .ToList();
        var timeline = new AzdoTimeline { Id = "tl-small", Records = records };

        _mockApi.GetTimelineAsync("dnceng-public", "public", 10, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _tools.Timeline("10", filter: "all");

        Assert.NotNull(result);
        Assert.False(result!.Truncated);
        Assert.Null(result.TotalRecords);
        Assert.Null(result.Note);
        Assert.Equal(totalCount, result.Records.Count);
    }

    // ── azdo_log (returns plain text, not JSON) ─────────────────────

    [Fact]
    public async Task Log_ReturnsPlainTextString()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("Build started\nCompiling...\nBuild succeeded");

        var result = await _tools.Log("1", 5);

        Assert.IsType<string>(result);
        Assert.Contains("Build started", result);
        Assert.Contains("Build succeeded", result);
    }

    [Fact]
    public async Task Log_NullContent_ReturnsEmptyString()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _tools.Log("1", 5);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task Log_TailLines_ReturnsLastNLines()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}"));
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(lines);

        var result = await _tools.Log("1", 5, tailLines: 2);

        var resultLines = result.Split('\n');
        Assert.Equal(2, resultLines.Length);
        Assert.Equal("line 9", resultLines[0]);
        Assert.Equal("line 10", resultLines[1]);
    }

    // ── azdo_changes ────────────────────────────────────────────────

    [Fact]
    public async Task Changes_ReturnsChangeList()
    {
        var changes = new List<AzdoBuildChange>
        {
            new() { Id = "sha1", Message = "first commit", Author = new AzdoChangeAuthor { DisplayName = "Dev" } },
            new() { Id = "sha2", Message = "second commit" }
        };
        _mockApi.GetBuildChangesAsync("dnceng-public", "public", 1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(changes);

        var result = await _tools.Changes("1");

        Assert.Equal(2, result.Count);
        Assert.Equal("sha1", result[0].Id);
        Assert.Equal("first commit", result[0].Message);
        Assert.Equal("Dev", result[0].Author?.DisplayName);
    }

    // ── azdo_test_runs ──────────────────────────────────────────────

    [Fact]
    public async Task TestRuns_ReturnsRunList()
    {
        var runs = new List<AzdoTestRun>
        {
            new() { Id = 1, Name = "Windows Tests", TotalTests = 100, PassedTests = 95, FailedTests = 5 }
        };
        _mockApi.GetTestRunsAsync("dnceng-public", "public", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(runs);

        var result = await _tools.TestRuns("42");

        Assert.Single(result);
        Assert.Equal("Windows Tests", result[0].Name);
        Assert.Equal(100, result[0].TotalTests);
        Assert.Equal(5, result[0].FailedTests);
    }

    // ── azdo_test_results ───────────────────────────────────────────

    [Fact]
    public async Task TestResults_ReturnsResultList()
    {
        var results = new List<AzdoTestResult>
        {
            new()
            {
                Id = 1,
                TestCaseTitle = "MyTest",
                Outcome = "Failed",
                DurationInMs = 1234.5,
                ErrorMessage = "Assert.Equal failed"
            }
        };
        _mockApi.GetTestResultsAsync("dnceng-public", "public", 77, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(results);

        var result = await _tools.TestResults("42", 77);

        Assert.Single(result);
        Assert.Equal("MyTest", result[0].TestCaseTitle);
        Assert.Equal("Failed", result[0].Outcome);
        Assert.Equal("Assert.Equal failed", result[0].ErrorMessage);
    }

    [Fact]
    public async Task TestResults_UrlBuildId_ResolvesOrgProject()
    {
        _mockApi.GetTestResultsAsync("myorg", "proj", 10, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestResult>());

        await _tools.TestResults(
            "https://dev.azure.com/myorg/proj/_build/results?buildId=999", 10);

        await _mockApi.Received(1).GetTestResultsAsync("myorg", "proj", 10, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── azdo_builds PR number filter ────────────────────────────────

    [Fact]
    public async Task Builds_PrNumber_PassedInFilter()
    {
        _mockApi.ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(prNumber: "12345");

        await _mockApi.Received(1).ListBuildsAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<AzdoBuildFilter>(f => f.PrNumber == "12345"),
            Arg.Any<CancellationToken>());
    }

    // ── azdo_builds — URL-shaped org parameter detection ────────────

    [Fact]
    public async Task Builds_OrgIsUrl_ExtractsOrgAndProject()
    {
        _mockApi.ListBuildsAsync("dnceng", "internal", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(org: "https://dev.azure.com/dnceng/internal/_build/results?buildId=123");

        await _mockApi.Received(1).ListBuildsAsync(
            "dnceng", "internal", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_OrgIsPlainString_PassesThroughUnchanged()
    {
        _mockApi.ListBuildsAsync("custom-org", "custom-proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(org: "custom-org", project: "custom-proj");

        await _mockApi.Received(1).ListBuildsAsync(
            "custom-org", "custom-proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_OrgIsUrl_OverridesExplicitProject()
    {
        // When org is a URL, the URL's project wins over the explicit project param
        _mockApi.ListBuildsAsync("devdiv", "DevDiv", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(
            org: "https://dev.azure.com/devdiv/DevDiv/_build/results?buildId=999",
            project: "should-be-overridden");

        await _mockApi.Received(1).ListBuildsAsync(
            "devdiv", "DevDiv", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_OrgIsVisualStudioComUrl_ExtractsOrgFromSubdomain()
    {
        _mockApi.ListBuildsAsync("dnceng", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(org: "https://dnceng.visualstudio.com/public/_build/results?buildId=789");

        await _mockApi.Received(1).ListBuildsAsync(
            "dnceng", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Builds_OrgIsInvalidUrl_FallsBackToOriginal()
    {
        // A URL that doesn't parse as AzDO should fall through to original org/project
        _mockApi.ListBuildsAsync("https://github.com/foo", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _tools.Builds(org: "https://github.com/foo");

        await _mockApi.Received(1).ListBuildsAsync(
            "https://github.com/foo", "public", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ── azdo_test_attachments — URL-shaped org parameter detection ──

    [Fact]
    public async Task TestAttachments_OrgIsUrl_ExtractsOrgAndProject()
    {
        _mockApi.GetTestAttachmentsAsync("dnceng", "internal", 1, 2, 100, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        await _tools.TestAttachments(
            runId: 1, resultId: 2,
            org: "https://dev.azure.com/dnceng/internal/_build/results?buildId=555");

        await _mockApi.Received(1).GetTestAttachmentsAsync(
            "dnceng", "internal", 1, 2, 100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestAttachments_OrgIsPlainString_PassesThroughUnchanged()
    {
        _mockApi.GetTestAttachmentsAsync("myorg", "myproj", 1, 2, 100, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestAttachment>());

        await _tools.TestAttachments(runId: 1, resultId: 2, org: "myorg", project: "myproj");

        await _mockApi.Received(1).GetTestAttachmentsAsync(
            "myorg", "myproj", 1, 2, 100, Arg.Any<CancellationToken>());
    }
}
