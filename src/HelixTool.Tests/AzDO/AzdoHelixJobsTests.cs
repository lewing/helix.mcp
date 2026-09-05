// Ensures GetHelixJobsAsync extracts Helix job IDs and failed work items from timeline issue messages.

using HelixTool.Core.AzDO;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoHelixJobsTests
{
    private readonly IAzdoApiClient _client;
    private readonly AzdoService _svc;

    public AzdoHelixJobsTests()
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
        List<AzdoIssue>? issues = null) => new()
    {
        Id = id, Name = name, Type = type, Result = result,
        ParentId = parentId, State = state, Issues = issues
    };

    private void SetupTimeline(AzdoTimeline? timeline,
        string org = "dnceng-public", string project = "public", int buildId = 42)
    {
        _client.GetTimelineAsync(org, project, buildId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(timeline));
    }

    // ── Extraction from issue messages ──────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_ExtractsJobIdsAndFailedWorkItems()
    {
        var jobGuid1 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var jobGuid2 = "11111111-2222-3333-4444-555555555555";
        var timeline = CreateTestTimeline(
            CreateRecord("job1", "Build Tests", "Job", result: "failed"),
            CreateRecord("task1", "Send to Helix", "Task", result: "failed", parentId: "job1",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "error", Message = $"Helix job started: https://helix.dot.net/api/2019-06-17/jobs/{jobGuid1}/details" },
                    new() { Type = "error", Message = $"Work item MyTest.dll in job {jobGuid1} has failed" },
                    new() { Type = "error", Message = $"Work item AnotherTest.dll in job {jobGuid1} has failed" },
                    new() { Type = "error", Message = $"Helix job started: https://helix.dot.net/api/2019-06-17/jobs/{jobGuid2}/details" },
                }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "all");

        Assert.Equal("42", result.BuildId);
        Assert.Equal(2, result.TotalHelixJobs);

        var job1 = Assert.Single(result.Jobs, j => j.HelixJobId == jobGuid1);
        Assert.Equal("Build Tests", job1.ParentJobName);
        Assert.Equal("failed", job1.Result);
        Assert.Equal(2, job1.FailedWorkItems.Count);
        Assert.Contains("MyTest.dll", job1.FailedWorkItems);
        Assert.Contains("AnotherTest.dll", job1.FailedWorkItems);

        var job2 = Assert.Single(result.Jobs, j => j.HelixJobId == jobGuid2);
        Assert.Empty(job2.FailedWorkItems);
    }

    // ── Filter: failed (default) excludes succeeded tasks ───────────

    [Fact]
    public async Task GetHelixJobsAsync_FilterFailed_ExcludesSucceededTasks()
    {
        var failedGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var succeededGuid = "11111111-2222-3333-4444-555555555555";
        var timeline = CreateTestTimeline(
            CreateRecord("job1", "Tests A", "Job"),
            CreateRecord("task1", "Send to Helix", "Task", result: "failed", parentId: "job1",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "error", Message = $"https://helix.dot.net/api/2019-06-17/jobs/{failedGuid}/details" },
                }),
            CreateRecord("job2", "Tests B", "Job"),
            CreateRecord("task2", "Send job to helix", "Task", result: "succeeded", parentId: "job2",
                issues: new List<AzdoIssue>
                {
                    new() { Type = "warning", Message = $"https://helix.dot.net/api/2019-06-17/jobs/{succeededGuid}/details" },
                }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42"); // default filter = "failed"

        Assert.Single(result.Jobs);
        Assert.Equal(failedGuid, result.Jobs[0].HelixJobId);
    }

    // ── No Helix tasks ──────────────────────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_NoHelixTasks_ReturnsEmpty()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("r1", "Build solution", "Task", result: "failed"));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "all");

        Assert.Empty(result.Jobs);
        Assert.Equal(0, result.TotalHelixJobs);
    }

    // ── Null timeline returns friendly note ─────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_NullTimeline_ReturnsFriendlyNote()
    {
        SetupTimeline(null);

        var result = await _svc.GetHelixJobsAsync("42");

        Assert.NotNull(result);
        Assert.Contains("No timeline available", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.TotalHelixJobs);
        Assert.Empty(result.Jobs);
    }

    // ── Invalid filter throws ───────────────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_InvalidFilter_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetHelixJobsAsync("42", filter: "invalid"));
    }

    // ── Filter presets: running / pending / incomplete / issues (§5) ─

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Running_ReturnsInProgressHelixTask()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "completed",  result: "succeeded"));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "running");

        // task1 matches 'running' with no issues → returned with empty HelixJobId (§8 caveat)
        Assert.Single(result.Jobs);
        Assert.Equal(string.Empty, result.Jobs[0].HelixJobId);
    }

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Running_WithIssues_ReturnsExtractedJobId()
    {
        // §8 caveat: running task that DOES have issues → HelixJobId extracted normally
        var jobGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null,
                issues: new List<AzdoIssue>
                {
                    new() { Message = $"https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/details" }
                }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "running");

        Assert.Single(result.Jobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
    }

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Pending_ReturnsPendingTask()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "pending",   result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "completed", result: "failed",
                issues: new List<AzdoIssue> { new() { Message = "some error" } }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "pending");

        // Only the pending task matches; it has no issues → empty HelixJobId
        Assert.Single(result.Jobs);
        Assert.Equal(string.Empty, result.Jobs[0].HelixJobId);
    }

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Incomplete_ReturnsRunningAndPendingTasks()
    {
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "pending",    result: null),
            CreateRecord("task3", "Send to Helix", "Task", state: "completed",  result: "failed",
                issues: new List<AzdoIssue> { new() { Message = "error" } }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "incomplete");

        Assert.Equal(2, result.Jobs.Count);
        Assert.All(result.Jobs, j => Assert.Equal(string.Empty, j.HelixJobId));
    }

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Issues_ReturnsOnlyTasksWithIssues()
    {
        var jobGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "completed",  result: "failed",
                issues: new List<AzdoIssue>
                {
                    new() { Message = $"https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/details" }
                }));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "issues");

        Assert.Single(result.Jobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
    }

    // ── §8 caveat: state-based presets include tasks with 0 issues ───

    [Fact]
    public async Task GetHelixJobsAsync_Filter_Running_NoIssues_ReturnsRowWithEmptyHelixJobId()
    {
        // §8: state-based presets must include tasks matching state even with no issues.
        // HelixJobId is empty because there are no issue messages to extract job IDs from.
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: "running");

        Assert.Single(result.Jobs);
        Assert.Equal(string.Empty, result.Jobs[0].HelixJobId);
        Assert.Equal("unknown", result.Jobs[0].Result);  // result is null → "unknown"
    }

    // ── Filter presets: aliases ───────────────────────────────────────

    [Theory]
    [InlineData("inProgress")]
    [InlineData("in-progress")]
    [InlineData("active")]
    public async Task GetHelixJobsAsync_Filter_AliasRunning_SameAsRunning(string alias)
    {
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "inProgress", result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "completed",  result: "succeeded"));
        SetupTimeline(timeline);

        var aliasResult    = await _svc.GetHelixJobsAsync("42", filter: alias);
        var canonicalResult = await _svc.GetHelixJobsAsync("42", filter: "running");

        Assert.Equal(canonicalResult.TotalHelixJobs, aliasResult.TotalHelixJobs);
    }

    [Theory]
    [InlineData("notStarted")]
    [InlineData("not-started")]
    public async Task GetHelixJobsAsync_Filter_AliasPending_SameAsPending(string alias)
    {
        var timeline = CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", state: "pending",   result: null),
            CreateRecord("task2", "Send to Helix", "Task", state: "completed", result: "succeeded"));
        SetupTimeline(timeline);

        var aliasResult    = await _svc.GetHelixJobsAsync("42", filter: alias);
        var canonicalResult = await _svc.GetHelixJobsAsync("42", filter: "pending");

        Assert.Equal(canonicalResult.TotalHelixJobs, aliasResult.TotalHelixJobs);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("issues")]
    [InlineData("all")]
    public async Task GetHelixJobsAsync_IssueWithoutJobId_IsPreservedAsBoundedMessage(string filter)
    {
        var timeline = CreateTestTimeline(
            CreateRecord("job1", "Tests", "Job", result: "failed"),
            CreateRecord("task1", "Monitor Helix Jobs", "Task", result: "failed", parentId: "job1",
                issues: [new AzdoIssue { Type = "error", Message = "Queue monitor failed before publishing a Helix job." }]));
        SetupTimeline(timeline);

        var result = await _svc.GetHelixJobsAsync("42", filter: filter);

        var job = Assert.Single(result.Jobs);
        Assert.Equal(string.Empty, job.HelixJobId);
        Assert.Equal(["Queue monitor failed before publishing a Helix job."], job.Messages);
        Assert.Empty(job.FailedWorkItems);
    }

    [Fact]
    public async Task GetHelixJobsAsync_IssueMessages_AreTrimmedAndCapped()
    {
        var issues = Enumerable.Range(1, 25)
            .Select(index => new AzdoIssue
            {
                Type = "error",
                Message = $"  issue-{index:D2}-" + new string('x', 600)
            })
            .ToList();
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Monitor Helix Jobs", "Task", result: "failed", issues: issues)));

        var result = await _svc.GetHelixJobsAsync("42", filter: "issues");

        var messages = Assert.Single(result.Jobs).Messages;
        Assert.NotNull(messages);
        Assert.Equal(21, messages.Count);
        Assert.All(messages.Take(20), message =>
        {
            Assert.Equal(500, message.Length);
            Assert.False(char.IsWhiteSpace(message[0]));
        });
        Assert.Equal("… 5 more issue(s) omitted", messages[20]);

        var timelineMessages = Assert.Single(result.TimelineIssues!).Messages;
        Assert.Equal(messages, timelineMessages);
    }

    [Fact]
    public async Task GetHelixJobsAsync_IssueMessages_DoNotSplitSurrogatePairs()
    {
        var splitEmoji = new string('a', 499) + "😀tail";
        var containedEmoji = new string('b', 498) + "😀tail";
        var ascii = new string('c', 600);
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Monitor Helix Jobs", "Task", result: "failed", issues:
            [
                new AzdoIssue { Type = "error", Message = splitEmoji },
                new AzdoIssue { Type = "error", Message = containedEmoji },
                new AzdoIssue { Type = "error", Message = ascii }
            ])));

        var result = await _svc.GetHelixJobsAsync("42", filter: "issues");

        var expected = new[]
        {
            new string('a', 499),
            new string('b', 498) + "😀",
            new string('c', 500)
        };
        Assert.Equal(expected, Assert.Single(result.Jobs).Messages);
        Assert.Equal(expected, Assert.Single(result.TimelineIssues!).Messages);

        var roundTrip = JsonSerializer.Deserialize<HelixJobsFromBuildResult>(
            JsonSerializer.Serialize(result));
        Assert.Equal(expected, Assert.Single(roundTrip!.Jobs).Messages);
        Assert.Equal(expected, Assert.Single(roundTrip.TimelineIssues!).Messages);
    }

    [Fact]
    public async Task GetHelixJobsAsync_MonitorWarnings_ParseMultipleJobsWithoutConsoleUrls()
    {
        const string firstGuid = "47edfeae-1111-2222-3333-444444444444";
        const string secondGuid = "57edfeae-1111-2222-3333-444444444444";
        var message = $"""
            Work item 'A.dll' in job 'Windows_NT Build_Release - Windows.10.Amd64.Open ({firstGuid})' failed (Failed).
            Console: no console link available
            Work item 'B.dll' in job '{secondGuid}' failed (BadExit).
            Console: no console link available
            """;
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Any Helix Task", "Task", result: "failed",
                issues: [new AzdoIssue { Type = "warning", Message = message }])));

        var result = await _svc.GetHelixJobsAsync("42", filter: "failed");

        Assert.Equal(2, result.TotalHelixJobs);
        Assert.Equal(["A.dll"], Assert.Single(result.Jobs, job => job.HelixJobId == firstGuid).FailedWorkItems);
        Assert.Equal(["B.dll"], Assert.Single(result.Jobs, job => job.HelixJobId == secondGuid).FailedWorkItems);
    }

    [Fact]
    public async Task GetHelixJobsAsync_MonitorWarning_UsesConsoleUrlAsGuidFallback()
    {
        const string jobGuid = "67edfeae-1111-2222-3333-444444444444";
        var message = $"""
            Work item 'Fallback.dll' in job 'Label without a guid' failed (Failed).
            Console: https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/workitems/Fallback.dll/console
            """;
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", result: "failed",
                issues: [new AzdoIssue { Type = "warning", Message = message }])));

        var job = Assert.Single((await _svc.GetHelixJobsAsync("42", filter: "failed")).Jobs);

        Assert.Equal(jobGuid, job.HelixJobId);
        Assert.Equal(["Fallback.dll"], job.FailedWorkItems);
    }

    [Fact]
    public async Task GetHelixJobsAsync_MonitorFailureTree_ParsesMultipleJobsAndIgnoresConsoleLines()
    {
        const string firstGuid = "77edfeae-1111-2222-3333-444444444444";
        const string secondGuid = "87edfeae-1111-2222-3333-444444444444";
        var message = $"""
            Failed work item information:
            Test results: two failures
            ├─ A.dll (Job: Label - Queue ({firstGuid})) (Failed)
            │  └─ Console: no console link available
            └─ B.dll (Job: Other Label - Queue ({secondGuid})) (BadExit)
               └─ Console: no console link available
            """;
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", result: "failed",
                issues: [new AzdoIssue { Type = "error", Message = message }])));

        var result = await _svc.GetHelixJobsAsync("42", filter: "failed");

        Assert.Equal(2, result.TotalHelixJobs);
        Assert.Equal(["A.dll"], Assert.Single(result.Jobs, job => job.HelixJobId == firstGuid).FailedWorkItems);
        Assert.Equal(["B.dll"], Assert.Single(result.Jobs, job => job.HelixJobId == secondGuid).FailedWorkItems);
        Assert.DoesNotContain(result.Jobs.SelectMany(job => job.FailedWorkItems),
            item => item.Contains("Console", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetHelixJobsAsync_MonitorWarningAndTree_DeduplicateWorkItemCaseInsensitively()
    {
        const string jobGuid = "97edfeae-1111-2222-3333-444444444444";
        var message = $"""
            Work item 'Duplicate.dll' in job 'Label ({jobGuid})' failed (Failed).
            Console: no console link available
            Failed work item information:
            └─ duplicate.DLL (Job: Label ({jobGuid})) (Failed)
               └─ Console: no console link available
            """;
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", result: "failed",
                issues: [new AzdoIssue { Type = "error", Message = message }])));

        var job = Assert.Single((await _svc.GetHelixJobsAsync("42", filter: "failed")).Jobs);

        Assert.Single(job.FailedWorkItems);
        Assert.Equal("Duplicate.dll", job.FailedWorkItems[0], ignoreCase: true);
    }

    [Fact]
    public async Task GetHelixJobsAsync_LegacyFailures_DeduplicateWorkItemCaseInsensitively()
    {
        const string jobGuid = "a7edfeae-1111-2222-3333-444444444444";
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", result: "failed",
                issues:
                [
                    new AzdoIssue { Type = "error", Message = $"Work item Legacy.dll in job {jobGuid} has failed" },
                    new AzdoIssue { Type = "error", Message = $"Work item legacy.DLL in job {jobGuid} has failed" }
                ])));

        var job = Assert.Single((await _svc.GetHelixJobsAsync("42", filter: "failed")).Jobs);

        Assert.Single(job.FailedWorkItems);
        Assert.Equal("Legacy.dll", job.FailedWorkItems[0], ignoreCase: true);
    }

    [Theory]
    [InlineData("failed", true)]
    [InlineData("running", true)]
    [InlineData("failed", false)]
    [InlineData("running", false)]
    public async Task GetHelixJobsAsync_RunningWithErrors_SeparatesStateFromOutcome(
        string filter, bool includeGuid)
    {
        const string jobGuid = "b7edfeae-1111-2222-3333-444444444444";
        var guidEvidence = includeGuid
            ? $"https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/details"
            : "queue monitor has not published the job id";
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Monitor Helix Jobs", "Task", state: "inProgress", result: null,
                issues:
                [
                    new AzdoIssue { Type = "error", Message = guidEvidence },
                    new AzdoIssue { Type = "ERROR", Message = "second error" },
                    new AzdoIssue { Type = "Warning", Message = "warning" }
                ])));

        var result = await _svc.GetHelixJobsAsync("42", filter: filter);

        var job = Assert.Single(result.Jobs);
        Assert.Equal(includeGuid ? jobGuid : string.Empty, job.HelixJobId);
        Assert.Equal("running", job.State);
        Assert.Equal("unknown", job.Result);
        Assert.Equal(2, job.TaskErrorCount);
        Assert.Equal(1, job.TaskWarningCount);
        Assert.Contains("still in progress", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(includeGuid ? null : 3, job.Messages?.Count);
    }

    [Theory]
    [InlineData("running", 3, 2)]
    [InlineData("pending", 1, 1)]
    [InlineData("all", 5, 3)]
    public async Task GetHelixJobsAsync_OutcomeUnknownCount_UsesReturnedTimelineRows(
        string filter, int expectedTotal, int expectedUnknown)
    {
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Monitor Helix One", "Task", state: "inProgress", result: null,
                issues: [new AzdoIssue { Type = "error", Message = "still running" }]),
            CreateRecord("task2", "Monitor Helix Two", "Task", state: "inProgress", result: "UnKnOwN",
                issues: [new AzdoIssue { Type = "warning", Message = "mixed case unknown" }]),
            CreateRecord("task3", "Monitor Helix Three", "Task", state: "inProgress", result: "failed",
                issues: [new AzdoIssue { Type = "error", Message = "known failure" }]),
            CreateRecord("task4", "Monitor Helix Four", "Task", state: "pending", result: null,
                issues: [new AzdoIssue { Type = "warning", Message = "not started" }]),
            CreateRecord("task5", "Monitor Helix Five", "Task", state: "completed", result: "succeeded",
                issues: [new AzdoIssue { Type = "warning", Message = "known success" }])));

        var result = await _svc.GetHelixJobsAsync("42", filter: filter);

        Assert.Equal(expectedTotal, result.TotalHelixJobs);
        Assert.Equal(expectedUnknown, result.OutcomeUnknownHelixJobs);
    }

    [Fact]
    public async Task GetHelixJobsAsync_TimelineDefaults_AreOmittedFromJson()
    {
        const string jobGuid = "c7edfeae-1111-2222-3333-444444444444";
        SetupTimeline(CreateTestTimeline(
            CreateRecord("task1", "Send to Helix", "Task", result: "succeeded",
                issues: [new AzdoIssue { Message = $"https://helix.dot.net/jobs/{jobGuid}/details" }])));

        var result = await _svc.GetHelixJobsAsync("42", filter: "all");
        var job = Assert.Single(result.Jobs);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(job));
        using var resultJson = JsonDocument.Parse(JsonSerializer.Serialize(result));

        Assert.False(json.RootElement.TryGetProperty("superseded", out _));
        Assert.False(json.RootElement.TryGetProperty("taskErrorCount", out _));
        Assert.False(json.RootElement.TryGetProperty("taskWarningCount", out _));
        Assert.False(json.RootElement.TryGetProperty("messages", out _));
        Assert.True(json.RootElement.TryGetProperty("HelixJobId", out _));
        Assert.True(json.RootElement.TryGetProperty("ParentJobName", out _));
        Assert.True(json.RootElement.TryGetProperty("Result", out _));
        Assert.True(json.RootElement.TryGetProperty("FailedWorkItems", out _));
        Assert.Equal(0, result.OutcomeUnknownHelixJobs);
        Assert.False(resultJson.RootElement.TryGetProperty("outcomeUnknownHelixJobs", out _));
    }
}
