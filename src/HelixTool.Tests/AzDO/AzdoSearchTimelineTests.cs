// Ensures timeline search returns expected matches by name and issue message patterns.

using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoSearchTimelineTests
{
    private readonly IAzdoApiClient _client;
    private readonly AzdoService _svc;

    public AzdoSearchTimelineTests()
    {
        _client = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_client);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AzdoTimeline CreateTestTimeline(params AzdoTimelineRecord[] records) =>
        new() { Id = "test-timeline-id", Records = records };

    private static AzdoTimelineRecord CreateRecord(
        string id, string name, string type, string? result = "succeeded",
        string? parentId = null, string? state = "completed",
        DateTimeOffset? startTime = null, DateTimeOffset? finishTime = null,
        int? logId = null, List<AzdoIssue>? issues = null) => new()
    {
        Id = id, Name = name, Type = type, Result = result,
        ParentId = parentId, State = state,
        StartTime = startTime, FinishTime = finishTime,
        Log = logId.HasValue ? new AzdoLogReference { Id = logId.Value } : null,
        Issues = issues
    };

    private void SetupTimeline(AzdoTimeline? timeline,
        string org = "dnceng-public", string project = "public", int buildId = 42)
    {
        _client.GetTimelineAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(timeline));
    }

    // ── Match by record name ────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_MatchesByRecordName()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Build solution", "Task", result: "failed"),
            CreateRecord("r2", "Run tests", "Task", result: "succeeded"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "Build");

        Assert.Single(result.Matches);
        Assert.Equal("Build solution", result.Matches[0].Name);
    }

    // ── Match by issue message ──────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_MatchesByIssueMessage()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Compile step", "Task", result: "failed",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "error", Message = "error CS1234: Something bad" }
                }));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "CS1234");

        Assert.Single(result.Matches);
        Assert.Contains("CS1234", result.Matches[0].MatchedIssues[0]);
    }

    // ── Case insensitive match ──────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_CaseInsensitiveMatch()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Build Solution", "Task", result: "failed"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "build solution");

        Assert.Single(result.Matches);
        Assert.Equal("Build Solution", result.Matches[0].Name);
    }

    // ── Filter by record type ───────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_FilterByRecordType()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error step", "Task", result: "failed"),
            CreateRecord("r2", "error stage", "Stage", result: "failed"),
            CreateRecord("r3", "error job", "Job", result: "failed"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error", recordType: "Task");

        Assert.Single(result.Matches);
        Assert.Equal("Task", result.Matches[0].Type);
        Assert.Equal("error step", result.Matches[0].Name);
    }

    [Fact]
    public async Task SearchTimelineAsync_FilterByRecordType_Stage()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error step", "Task", result: "failed"),
            CreateRecord("r2", "error stage", "Stage", result: "failed"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error", recordType: "Stage");

        Assert.Single(result.Matches);
        Assert.Equal("Stage", result.Matches[0].Type);
        Assert.Equal("error stage", result.Matches[0].Name);
    }

    // ── Result filter ───────────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_ResultFilterAll_ReturnsAllMatches()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error task A", "Task", result: "succeeded"),
            CreateRecord("r2", "error task B", "Task", result: "failed"),
            CreateRecord("r3", "error task C", "Task", result: "canceled"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error", resultFilter: "all");

        Assert.Equal(3, result.Matches.Count);
    }

    [Fact]
    public async Task SearchTimelineAsync_ResultFilterFailed_ReturnsOnlyNonSucceeded()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error task A", "Task", result: "succeeded"),
            CreateRecord("r2", "error task B", "Task", result: "failed"),
            CreateRecord("r3", "error task C", "Task", result: "canceled"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error", resultFilter: "failed");

        Assert.Equal(2, result.Matches.Count);
        Assert.All(result.Matches, m =>
            Assert.NotEqual("succeeded", m.Result, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchTimelineAsync_DefaultResultFilter_ExcludesSucceeded()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error task A", "Task", result: "succeeded"),
            CreateRecord("r2", "error task B", "Task", result: "failed"));
        SetupTimeline(timeline);

        // Default resultFilter (null) defaults to "failed", excluding succeeded records
        var result = await _svc.SearchTimelineAsync("42", "error");

        Assert.Single(result.Matches);
        Assert.Equal("error task B", result.Matches[0].Name);
    }

    // ── No matches ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_NoMatches_ReturnsEmptyResult()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Build solution", "Task", result: "failed"),
            CreateRecord("r2", "Run tests", "Task", result: "failed"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "NONEXISTENT_PATTERN");

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.MatchCount);
        Assert.Equal(2, result.TotalRecords);
    }

    // ── Input validation ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchTimelineAsync_NullPattern_ThrowsArgumentException(string? badPattern)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchTimelineAsync("42", badPattern!));
    }

    // ── Empty / null timeline ───────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_EmptyTimeline_ReturnsEmptyResult()
    {
        var timeline = CreateTestTimeline(); // no records
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error");

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.TotalRecords);
    }

    [Fact]
    public async Task SearchTimelineAsync_NullTimeline_ThrowsInvalidOperation()
    {
        SetupTimeline(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.SearchTimelineAsync("42", "error"));
    }

    // ── Parent context ──────────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_IncludesParentContext()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("parent1", "Build Stage", "Stage", result: "failed"),
            CreateRecord("child1", "error task", "Task", result: "failed", parentId: "parent1"));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error task");

        var match = Assert.Single(result.Matches);
        Assert.Equal("Build Stage", match.ParentName);
    }

    // ── Duration calculation ────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_DurationCalculation()
    {
        var start = new DateTimeOffset(2025, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var finish = start.AddMinutes(5).AddSeconds(30);
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error task", "Task", result: "failed",
                startTime: start, finishTime: finish));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error");

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Duration);
        // FormatDuration: 5m 30s
        Assert.Equal("5m 30s", match.Duration);
    }

    // ── Log ID included ─────────────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_LogIdIncluded()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error task", "Task", result: "failed", logId: 99));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error");

        var match = Assert.Single(result.Matches);
        Assert.Equal(99, match.LogId);
    }

    // ── Multiple matched issues ─────────────────────────────────────

    [Fact]
    public async Task SearchTimelineAsync_MultipleMatchedIssues()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Compile step", "Task", result: "failed",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "error", Message = "error CS0001: first problem" },
                    new() { Type = "warning", Message = "no match here" },
                    new() { Type = "error", Message = "error CS0002: second problem" }
                }));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error");

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.MatchedIssues.Count);
        Assert.Contains(match.MatchedIssues, m => m.Contains("CS0001"));
        Assert.Contains(match.MatchedIssues, m => m.Contains("CS0002"));
    }

    // ── Match in both name and issues (no duplication) ───────────────

    [Fact]
    public async Task SearchTimelineAsync_MatchesInBothNameAndIssues()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "error handler task", "Task", result: "failed",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "error", Message = "error in compilation" }
                }));
        SetupTimeline(timeline);

        var result = await _svc.SearchTimelineAsync("42", "error");

        // Should appear only once even though "error" matches both name and issue
        Assert.Single(result.Matches);
        Assert.Equal("error handler task", result.Matches[0].Name);
        Assert.Single(result.Matches[0].MatchedIssues);
    }
}
