// Ensures GetHelixJobsAsync extracts Helix job IDs and failed work items from timeline issue messages.

using HelixTool.Core.AzDO;
using NSubstitute;
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

    // ── Null timeline throws ────────────────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_NullTimeline_ThrowsInvalidOperation()
    {
        SetupTimeline(null);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.GetHelixJobsAsync("42"));
    }

    // ── Invalid filter throws ───────────────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_InvalidFilter_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetHelixJobsAsync("42", filter: "invalid"));
    }
}
