// Proactive tests for AzDO CLI subcommand registration.
// Ripley is adding AzDO CLI commands (azdo_build, azdo_timeline, etc.) as subcommands.
// These tests validate the AzdoService layer that CLI commands will call.
// Once CLI command classes exist, additional registration/parsing tests should be added.

using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoCliCommandTests
{
    private readonly IAzdoApiClient _mockClient;
    private readonly AzdoService _svc;

    public AzdoCliCommandTests()
    {
        _mockClient = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockClient);
    }

    // ── Build summary (azdo_build equivalent) ────────────────────────

    [Fact]
    public async Task GetBuildSummary_PlainBuildId_DefaultsToPublic()
    {
        // CLI: `hlx azdo build 123` should resolve to dnceng-public/public
        var build = CreateBuild(123, "completed", "succeeded");
        _mockClient.GetBuildAsync("dnceng-public", "public", 123, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("123");

        Assert.Equal(123, result.Id);
        Assert.Equal("completed", result.Status);
        Assert.Equal("succeeded", result.Result);
    }

    [Fact]
    public async Task GetBuildSummary_AzdoUrl_ResolvesOrgProject()
    {
        // CLI: `hlx azdo build https://dev.azure.com/dnceng/internal/_build/results?buildId=456`
        var build = CreateBuild(456, "completed", "failed");
        _mockClient.GetBuildAsync("dnceng", "internal", 456, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync(
            "https://dev.azure.com/dnceng/internal/_build/results?buildId=456");

        Assert.Equal(456, result.Id);
        Assert.Equal("dnceng", result.WebUrl!.Split('/')[3]); // org in URL
    }

    [Fact]
    public async Task GetBuildSummary_NotFound_ThrowsInvalidOperation()
    {
        _mockClient.GetBuildAsync("dnceng-public", "public", 999, Arg.Any<CancellationToken>())
            .Returns((AzdoBuild?)null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.GetBuildSummaryAsync("999"));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task GetBuildSummary_InvalidBuildId_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetBuildSummaryAsync("not-a-number"));
    }

    // ── Timeline (azdo_timeline equivalent) ──────────────────────────

    [Fact]
    public async Task GetTimeline_ValidBuild_ReturnsTimeline()
    {
        var timeline = new AzdoTimeline { Records = [] };
        _mockClient.GetTimelineAsync("dnceng-public", "public", 789, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _svc.GetTimelineAsync("789");

        Assert.NotNull(result);
        Assert.Empty(result.Records);
    }

    [Fact]
    public async Task GetTimeline_NoBuild_ReturnsNull()
    {
        _mockClient.GetTimelineAsync("dnceng-public", "public", 789, Arg.Any<CancellationToken>())
            .Returns((AzdoTimeline?)null);

        var result = await _svc.GetTimelineAsync("789");

        Assert.Null(result);
    }

    // ── Build log (azdo_log equivalent) ──────────────────────────────

    [Fact]
    public async Task GetBuildLog_ReturnsContent()
    {
        _mockClient.GetBuildLogAsync("dnceng-public", "public", 100, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("Line 1\nLine 2\nLine 3");

        var result = await _svc.GetBuildLogAsync("100", 5);

        Assert.Equal("Line 1\nLine 2\nLine 3", result);
    }

    [Fact]
    public async Task GetBuildLog_WithTailLines_ReturnsLastN()
    {
        var fullLog = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"Line {i}"));
        _mockClient.GetBuildLogAsync("dnceng-public", "public", 100, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(fullLog);

        var result = await _svc.GetBuildLogAsync("100", 5, tailLines: 3);

        Assert.NotNull(result);
        var lines = result.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("Line 98", lines[0]);
    }

    [Fact]
    public async Task GetBuildLog_NotFound_ReturnsNull()
    {
        _mockClient.GetBuildLogAsync("dnceng-public", "public", 100, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _svc.GetBuildLogAsync("100", 5);

        Assert.Null(result);
    }

    // ── Build changes (azdo_changes equivalent) ──────────────────────

    [Fact]
    public async Task GetBuildChanges_ReturnsChangeList()
    {
        var changes = new List<AzdoBuildChange>
        {
            new() { Id = "abc123", Message = "Fix test", Author = new AzdoChangeAuthor { DisplayName = "dev" } },
            new() { Id = "def456", Message = "Update docs", Author = new AzdoChangeAuthor { DisplayName = "dev2" } }
        };
        _mockClient.GetBuildChangesAsync("dnceng-public", "public", 100, null, Arg.Any<CancellationToken>())
            .Returns(changes);

        var result = await _svc.GetBuildChangesAsync("100");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetBuildChanges_WithTopParameter_PassesToClient()
    {
        _mockClient.GetBuildChangesAsync("dnceng-public", "public", 100, 5, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildChange>());

        await _svc.GetBuildChangesAsync("100", top: 5);

        await _mockClient.Received(1)
            .GetBuildChangesAsync("dnceng-public", "public", 100, 5, Arg.Any<CancellationToken>());
    }

    // ── Test runs (azdo_test_runs equivalent) ────────────────────────

    [Fact]
    public async Task GetTestRuns_ReturnsRunsList()
    {
        var runs = new List<AzdoTestRun>
        {
            new() { Id = 1, Name = "Test Run 1", TotalTests = 100, PassedTests = 95, FailedTests = 5 }
        };
        _mockClient.GetTestRunsAsync("dnceng-public", "public", 200, null, Arg.Any<CancellationToken>())
            .Returns(runs);

        var result = await _svc.GetTestRunsAsync("200");

        Assert.Single(result);
        Assert.Equal("Test Run 1", result[0].Name);
    }

    // ── Test results (azdo_test_results equivalent) ──────────────────

    [Fact]
    public async Task GetTestResults_ReturnsResults()
    {
        var results = new List<AzdoTestResult>
        {
            new() { TestCaseTitle = "MyTest", Outcome = "Failed", ErrorMessage = "Assert failed" }
        };
        _mockClient.GetTestResultsAsync("dnceng-public", "public", 1, 200, Arg.Any<CancellationToken>())
            .Returns(results);

        var result = await _svc.GetTestResultsAsync("200", runId: 1);

        Assert.Single(result);
        Assert.Equal("Failed", result[0].Outcome);
    }

    // ── List builds (azdo_builds equivalent) ─────────────────────────

    [Fact]
    public async Task ListBuilds_PassesFilterToClient()
    {
        var builds = new List<AzdoBuild>
        {
            CreateBuild(1, "completed", "succeeded"),
            CreateBuild(2, "completed", "failed")
        };
        var filter = new AzdoBuildFilter { Top = 10 };
        _mockClient.ListBuildsAsync("dnceng-public", "public", filter, Arg.Any<CancellationToken>())
            .Returns(builds);

        var result = await _svc.ListBuildsAsync("dnceng-public", "public", filter);

        Assert.Equal(2, result.Count);
    }

    // ── Artifacts (azdo_artifacts equivalent) ────────────────────────

    [Fact]
    public async Task GetBuildArtifacts_DefaultPattern_ReturnsAll()
    {
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Name = "drop", Id = 1, Resource = new AzdoArtifactResource { Type = "Container" } },
            new() { Name = "logs", Id = 2, Resource = new AzdoArtifactResource { Type = "Container" } }
        };
        _mockClient.GetBuildArtifactsAsync("dnceng-public", "public", 300, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _svc.GetBuildArtifactsAsync("300");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetBuildArtifacts_PatternFilter_FiltersResults()
    {
        var artifacts = new List<AzdoBuildArtifact>
        {
            new() { Name = "build.binlog", Id = 1, Resource = new AzdoArtifactResource { Type = "Container" } },
            new() { Name = "test-results.trx", Id = 2, Resource = new AzdoArtifactResource { Type = "Container" } },
            new() { Name = "restore.binlog", Id = 3, Resource = new AzdoArtifactResource { Type = "Container" } }
        };
        _mockClient.GetBuildArtifactsAsync("dnceng-public", "public", 300, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _svc.GetBuildArtifactsAsync("300", pattern: "*.binlog");

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.EndsWith(".binlog", a.Name!));
    }

    [Fact]
    public async Task GetBuildArtifacts_TopLimit_LimitsResults()
    {
        var artifacts = Enumerable.Range(1, 100)
            .Select(i => new AzdoBuildArtifact
            {
                Name = $"artifact-{i}",
                Id = i,
                Resource = new AzdoArtifactResource { Type = "Container" }
            })
            .ToList();
        _mockClient.GetBuildArtifactsAsync("dnceng-public", "public", 300, Arg.Any<CancellationToken>())
            .Returns(artifacts);

        var result = await _svc.GetBuildArtifactsAsync("300", top: 10);

        Assert.Equal(10, result.Count);
    }

    // ── Duration calculation ─────────────────────────────────────────

    [Fact]
    public async Task GetBuildSummary_CalculatesDuration()
    {
        var start = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var finish = new DateTimeOffset(2025, 7, 18, 10, 15, 30, TimeSpan.Zero);
        var build = CreateBuild(500, "completed", "succeeded", start, finish);
        _mockClient.GetBuildAsync("dnceng-public", "public", 500, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("500");

        Assert.NotNull(result.Duration);
        Assert.Equal(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(30), result.Duration);
    }

    [Fact]
    public async Task GetBuildSummary_InProgressBuild_NullDuration()
    {
        var start = new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero);
        var build = CreateBuild(501, "inProgress", null, start, null);
        _mockClient.GetBuildAsync("dnceng-public", "public", 501, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _svc.GetBuildSummaryAsync("501");

        Assert.Null(result.Duration);
    }

    // ── Test helpers ─────────────────────────────────────────────────

    private static AzdoBuild CreateBuild(int id, string status, string? result,
        DateTimeOffset? startTime = null, DateTimeOffset? finishTime = null)
    {
        return new AzdoBuild
        {
            Id = id,
            BuildNumber = $"20250718.{id}",
            Status = status,
            Result = result,
            SourceBranch = "refs/heads/main",
            SourceVersion = "abc123def456",
            Definition = new AzdoBuildDefinition { Id = 1, Name = "CI" },
            RequestedFor = new AzdoIdentityRef { DisplayName = "Test User" },
            QueueTime = DateTimeOffset.UtcNow.AddMinutes(-30),
            StartTime = startTime ?? DateTimeOffset.UtcNow.AddMinutes(-25),
            FinishTime = finishTime ?? (result != null ? DateTimeOffset.UtcNow : null)
        };
    }
}
