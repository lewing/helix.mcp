// Tests for azdo_search_log_across_steps (cross-step build log search).
// Validates AzdoService.SearchBuildLogAcrossStepsAsync ranking algorithm,
// early termination, validation, and MCP exception wrapping per Dallas's design spec.

using HelixTool.Core;
using HelixTool.Core.AzDO;
using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("FileSearchConfig")]
public class SearchBuildLogAcrossStepsTests
{
    private readonly IAzdoApiClient _client;
    private readonly AzdoService _svc;
    private readonly AzdoMcpTools _tools;

    private const string Org = "dnceng-public";
    private const string Project = "public";
    private const int BuildId = 42;

    public SearchBuildLogAcrossStepsTests()
    {
        _client = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_client);
        _tools = new AzdoMcpTools(_svc, Substitute.For<IAzdoTokenAccessor>());
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static AzdoTimeline CreateTimeline(params AzdoTimelineRecord[] records) =>
        new() { Id = "timeline-1", Records = records };

    private static AzdoTimelineRecord CreateRecord(
        string id, string name, string type = "Task", string? result = "succeeded",
        int? logId = null, List<AzdoIssue>? issues = null, string? parentId = null) => new()
    {
        Id = id, Name = name, Type = type, Result = result,
        Log = logId.HasValue ? new AzdoLogReference { Id = logId.Value } : null,
        Issues = issues, ParentId = parentId
    };

    private static AzdoBuildLogEntry CreateLogEntry(int id, long lineCount) => new()
    {
        Id = id, LineCount = lineCount
    };

    private void SetupTimeline(AzdoTimeline? timeline) =>
        _client.GetTimelineAsync(Org, Project, BuildId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(timeline));

    private void SetupLogsList(params AzdoBuildLogEntry[] entries) =>
        _client.GetBuildLogsListAsync(Org, Project, BuildId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AzdoBuildLogEntry>>(entries.ToList()));

    private void SetupLogContent(int logId, string content) =>
        _client.GetBuildLogAsync(Org, Project, BuildId, logId, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(content));

    /// <summary>Generate a log with N lines, with an optional error line at a specific position.</summary>
    private static string GenerateLogContent(int lineCount, int? errorAtLine = null, string errorText = "error: something failed")
    {
        var lines = new string[lineCount];
        for (int i = 0; i < lineCount; i++)
            lines[i] = (errorAtLine.HasValue && i == errorAtLine.Value - 1)
                ? errorText
                : $"normal output line {i + 1}";
        return string.Join("\n", lines);
    }

    // ── T-1: Empty build (no logs, no timeline) → 0 matches ────────

    [Fact]
    public async Task T1_EmptyBuild_NoLogsNoTimeline_ReturnsZeroMatches()
    {
        SetupTimeline(CreateTimeline()); // empty records
        SetupLogsList(); // no log entries

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        Assert.Equal(0, result.TotalMatchCount);
        Assert.Equal(0, result.LogsSearched);
        Assert.Empty(result.Steps);
        Assert.False(result.StoppedEarly);
    }

    // ── T-2: All logs below minLogLines → 0 matches, all skipped ───

    [Fact]
    public async Task T2_AllLogsBelowMinLogLines_ReturnsZeroMatchesAllSkipped()
    {
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Small step A", logId: 1),
            CreateRecord("r2", "Small step B", logId: 2)));
        SetupLogsList(
            CreateLogEntry(1, lineCount: 3), // below default minLogLines=5
            CreateLogEntry(2, lineCount: 4));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        Assert.Equal(0, result.TotalMatchCount);
        Assert.Equal(0, result.LogsSearched);
        // Logs below minLogLines are filtered before ranking — they don't count as "skipped"
        Assert.Equal(0, result.LogsSkipped);
        Assert.Empty(result.Steps);
    }

    // ── T-3: Single failed log with matches → correct StepSearchResult

    [Fact]
    public async Task T3_SingleFailedLogWithMatches_ReturnsCorrectStepSearchResult()
    {
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Build solution", result: "failed", logId: 10)));
        SetupLogsList(CreateLogEntry(10, lineCount: 20));
        SetupLogContent(10, GenerateLogContent(20, errorAtLine: 8));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        Assert.Equal(1, result.TotalMatchCount);
        var step = Assert.Single(result.Steps);
        Assert.Equal(10, step.LogId);
        Assert.Equal("Build solution", step.StepName);
        Assert.Equal("failed", step.StepResult);
        Assert.Equal(1, step.MatchCount);
        var match = Assert.Single(step.Matches);
        Assert.Equal(8, match.LineNumber);
        Assert.Contains("error", match.Line);
    }

    // ── T-4: Ranking order: failed → issues → succeededWithIssues → succeeded

    [Fact]
    public async Task T4_RankingOrder_FailedFirstThenIssuesThenSucceededWithIssuesThenSucceeded()
    {
        // Bucket 3: succeeded (logId=1)
        var succeededRec = CreateRecord("r1", "Succeeded step", result: "succeeded", logId: 1);
        // Bucket 2: succeededWithIssues (logId=2)
        var swIssuesRec = CreateRecord("r2", "SWI step", result: "succeededWithIssues", logId: 2);
        // Bucket 1: succeeded but has issues (logId=3)
        var issuesRec = CreateRecord("r3", "Issues step", result: "succeeded", logId: 3,
            issues: new List<AzdoIssue> { new() { Type = "warning", Message = "caution" } });
        // Bucket 0: failed (logId=4)
        var failedRec = CreateRecord("r4", "Failed step", result: "failed", logId: 4);

        SetupTimeline(CreateTimeline(succeededRec, swIssuesRec, issuesRec, failedRec));
        SetupLogsList(
            CreateLogEntry(1, 10), CreateLogEntry(2, 10),
            CreateLogEntry(3, 10), CreateLogEntry(4, 10));

        // Each log has exactly one unique error that identifies which log was searched
        SetupLogContent(1, GenerateLogContent(10, errorAtLine: 5, errorText: "error:bucket3"));
        SetupLogContent(2, GenerateLogContent(10, errorAtLine: 5, errorText: "error:bucket2"));
        SetupLogContent(3, GenerateLogContent(10, errorAtLine: 5, errorText: "error:bucket1"));
        SetupLogContent(4, GenerateLogContent(10, errorAtLine: 5, errorText: "error:bucket0"));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        // Expect download order: failed (bucket0) → issues (bucket1) → SWI (bucket2) → succeeded (bucket3)
        Assert.Equal(4, result.Steps.Count);
        Assert.Equal("Failed step", result.Steps[0].StepName);     // Bucket 0
        Assert.Equal("Issues step", result.Steps[1].StepName);     // Bucket 1
        Assert.Equal("SWI step", result.Steps[2].StepName);        // Bucket 2
        Assert.Equal("Succeeded step", result.Steps[3].StepName);  // Bucket 3
    }

    // ── T-5: Early termination at maxMatches ────────────────────────

    [Fact]
    public async Task T5_EarlyTermination_StoppedEarlyTrueAndExactlyMaxMatches()
    {
        // Two logs, each with 3 "error" lines. maxMatches=3 should stop after first log (or mid-second).
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Step A", result: "failed", logId: 1),
            CreateRecord("r2", "Step B", result: "failed", logId: 2)));
        SetupLogsList(CreateLogEntry(1, 10), CreateLogEntry(2, 10));

        // Log 1: 3 errors
        SetupLogContent(1, "error line1\nerror line2\nerror line3\nok\nok\nok\nok\nok\nok\nok");
        // Log 2: 2 more errors (should not all be reached)
        SetupLogContent(2, "error line4\nerror line5\nok\nok\nok\nok\nok\nok\nok\nok");

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error", maxMatches: 3);

        Assert.Equal(3, result.TotalMatchCount);
        Assert.True(result.StoppedEarly);
    }

    // ── T-6: maxLogsToSearch limit ──────────────────────────────────

    [Fact]
    public async Task T6_MaxLogsToSearch_OnlySearchesLimitedCount()
    {
        // Create 10 eligible timeline records with logs
        var records = Enumerable.Range(1, 10)
            .Select(i => CreateRecord($"r{i}", $"Step {i}", result: "failed", logId: i))
            .ToArray();

        SetupTimeline(CreateTimeline(records));
        SetupLogsList(Enumerable.Range(1, 10)
            .Select(i => CreateLogEntry(i, lineCount: 20)).ToArray());

        // All logs have no matches — we just want to verify the count of logs searched
        foreach (var i in Enumerable.Range(1, 10))
            SetupLogContent(i, GenerateLogContent(20));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error", maxLogsToSearch: 5);

        Assert.Equal(5, result.LogsSearched);
        // Verify only 5 GetBuildLogAsync calls were made
        await _client.ReceivedWithAnyArgs(5).GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── T-7: Orphan logs (no timeline record) → Bucket 4 ───────────

    [Fact]
    public async Task T7_OrphanLogs_NoTimelineRecord_AppendedAtEndBucket4()
    {
        // Timeline has one failed record referencing logId=1
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Known step", result: "failed", logId: 1)));
        // Logs list has logId=1 AND orphan logId=99 (not in timeline)
        SetupLogsList(CreateLogEntry(1, 10), CreateLogEntry(99, 15));

        SetupLogContent(1, GenerateLogContent(10, errorAtLine: 5, errorText: "error:known"));
        SetupLogContent(99, GenerateLogContent(15, errorAtLine: 10, errorText: "error:orphan"));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        Assert.Equal(2, result.Steps.Count);
        // Failed record (Bucket 0) should come first
        Assert.Equal("Known step", result.Steps[0].StepName);
        // Orphan (Bucket 4) should come last — step name will be empty or contain logId
        Assert.Equal(99, result.Steps[1].LogId);
    }

    // ── T-8: Pattern not found in any log → 0 matches ──────────────

    [Fact]
    public async Task T8_PatternNotFoundInAnyLog_ReturnsZeroMatches()
    {
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Step A", result: "failed", logId: 1),
            CreateRecord("r2", "Step B", result: "failed", logId: 2)));
        SetupLogsList(CreateLogEntry(1, 10), CreateLogEntry(2, 10));
        SetupLogContent(1, GenerateLogContent(10));
        SetupLogContent(2, GenerateLogContent(10));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "NONEXISTENT_PATTERN_XYZ");

        Assert.Equal(0, result.TotalMatchCount);
        Assert.False(result.StoppedEarly);
        Assert.Empty(result.Steps);
        Assert.Equal(2, result.LogsSearched);
    }

    // ── T-9: Timeline record with no log reference → skipped ────────

    [Fact]
    public async Task T9_TimelineRecordWithNoLogReference_Skipped()
    {
        // Record without logId (Log == null)
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "No-log record", result: "failed", logId: null),
            CreateRecord("r2", "Has-log record", result: "failed", logId: 1)));
        SetupLogsList(CreateLogEntry(1, 10));
        SetupLogContent(1, GenerateLogContent(10, errorAtLine: 3, errorText: "error: found"));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        Assert.Equal(1, result.TotalMatchCount);
        Assert.Equal(1, result.LogsSearched);
        var step = Assert.Single(result.Steps);
        Assert.Equal("Has-log record", step.StepName);
    }

    // ── T-10: Context lines propagation ─────────────────────────────

    [Fact]
    public async Task T10_ContextLinesPropagation_FlowsToSearchHelper()
    {
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Step A", result: "failed", logId: 1)));
        SetupLogsList(CreateLogEntry(1, 20));

        // Error at line 10 of 20 lines
        SetupLogContent(1, GenerateLogContent(20, errorAtLine: 10));

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error", contextLines: 3);

        var step = Assert.Single(result.Steps);
        var match = Assert.Single(step.Matches);
        Assert.NotNull(match.Context);
        // 3 before + match + 3 after = 7 context lines
        Assert.Equal(7, match.Context!.Count);
    }

    // ── T-11: Line ending normalization ─────────────────────────────

    [Fact]
    public async Task T11_LineEndingNormalization_CrLfAndCrNormalized()
    {
        SetupTimeline(CreateTimeline(
            CreateRecord("r1", "Step A", result: "failed", logId: 1)));
        SetupLogsList(CreateLogEntry(1, 10));

        // Mix of \r\n and \r line endings
        SetupLogContent(1, "line 1\r\nline 2\rerror: test failure\r\nline 4\rline 5\nline 6\nline 7\nline 8\nline 9\nline 10");

        var result = await _svc.SearchBuildLogAcrossStepsAsync("42", "error");

        var step = Assert.Single(result.Steps);
        var match = Assert.Single(step.Matches);
        Assert.Equal(3, match.LineNumber); // "error: test failure" is line 3 after normalization
        Assert.Contains("error: test failure", match.Line);
    }

    // ══════════════════════════════════════════════════════════════════
    // Validation Tests (V-1 through V-6)
    // ══════════════════════════════════════════════════════════════════

    // ── V-1: Null/empty pattern → ArgumentException ────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task V1_NullOrEmptyPattern_ThrowsArgumentException(string? badPattern)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchBuildLogAcrossStepsAsync("42", badPattern!));
    }

    // ── V-2: Negative contextLines → ArgumentOutOfRangeException ───

    [Fact]
    public async Task V2_NegativeContextLines_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.SearchBuildLogAcrossStepsAsync("42", "error", contextLines: -1));
    }

    // ── V-3: Zero maxMatches → ArgumentOutOfRangeException ─────────

    [Fact]
    public async Task V3_ZeroMaxMatches_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.SearchBuildLogAcrossStepsAsync("42", "error", maxMatches: 0));
    }

    // ── V-4: Zero maxLogsToSearch → ArgumentOutOfRangeException ────

    [Fact]
    public async Task V4_ZeroMaxLogsToSearch_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.SearchBuildLogAcrossStepsAsync("42", "error", maxLogsToSearch: 0));
    }

    // ── V-5: Negative minLogLines → ArgumentOutOfRangeException ────

    [Fact]
    public async Task V5_NegativeMinLogLines_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _svc.SearchBuildLogAcrossStepsAsync("42", "error", minLogLines: -1));
    }

    // ── V-6: IsFileSearchDisabled=true → InvalidOperationException ─

    [Fact]
    public async Task V6_FileSearchDisabled_ThrowsInvalidOperationException()
    {
        var original = Environment.GetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH");
        try
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _svc.SearchBuildLogAcrossStepsAsync("42", "error"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", original);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // MCP Tool Tests (M-1, M-2)
    // ══════════════════════════════════════════════════════════════════

    // ── M-1: IsFileSearchDisabled → McpException (not InvalidOp) ───

    [Fact]
    public async Task M1_FileSearchDisabled_McpToolThrowsMcpException()
    {
        var original = Environment.GetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH");
        try
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");

            await Assert.ThrowsAsync<McpException>(
                () => _tools.SearchLog("42", pattern: "error"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", original);
        }
    }

    // ── M-2: Service ArgumentException → McpException ──────────────

    [Fact]
    public async Task M2_ServiceArgumentException_McpToolThrowsMcpException()
    {
        // Null pattern triggers ArgumentException in service; MCP should remap to McpException
        await Assert.ThrowsAsync<McpException>(
            () => _tools.SearchLog("42", pattern: null!));
    }
}
