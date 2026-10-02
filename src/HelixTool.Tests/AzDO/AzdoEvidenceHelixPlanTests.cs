using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoEvidenceHelixPlanTests
{
    private const int RuntimeMonitorOnlyBuildId = 1621192;
    private const int RuntimeMixedBuildId = 1621133;
    private const string MonitorJobId = "078edc60-2a90-5618-e72d-426a045b11f0";
    private const string MonitorTaskId = "0ca3371c-1de8-59d8-daef-35ea5811e911";
    private const string RuntimeHelixJobId = "d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a";
    private const string SvcHelixJobId = "344b1d8c-5e14-49b2-9981-2e2891ac0132";
    private const string CanceledBuildLegId = "2244f57f-fc11-5e95-8791-b52f0f7438a4";

    private readonly IAzdoApiClient _azdo;
    private readonly IHelixApiClient _helix;
    private readonly AzdoService _svc;

    public AzdoEvidenceHelixPlanTests()
    {
        _azdo = Substitute.For<IAzdoApiClient>();
        _helix = Substitute.For<IHelixApiClient>();
        _svc = new AzdoService(_azdo, _helix);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_MonitorFailure_ProducesHelixFailuresAndComplete()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(RuntimeMonitorOnlyBuildId, RuntimeMonitorTimeline());
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        Assert.Empty(plan.Entries);
        var failure = Assert.Single(plan.HelixFailures);
        Assert.Equal(MonitorJobId, failure.MonitorJobId);
        Assert.Equal("Monitor Helix Jobs", failure.MonitorJobName);
        Assert.Equal("failed", failure.MonitorJobResult);
        Assert.Equal(1, failure.MonitorJobOrder);
        Assert.Equal(1, failure.MonitorJobAttempt);
        Assert.Equal(MonitorTaskId, failure.MonitorTaskId);
        Assert.Equal("Monitor Helix Jobs", failure.MonitorTaskName);
        Assert.Equal(RuntimeHelixJobId, failure.HelixJobId);
        Assert.Equal("System.Diagnostics.Process.Tests", failure.WorkItem);
        Assert.Equal("windows-x86 Debug Libraries_CheckedCoreCLR", failure.Leg);
        Assert.Equal("windows.10.amd64.open.rt", failure.Queue);
        Assert.Equal("Finished", failure.State);
        Assert.Equal(-3, failure.ExitCode);
        Assert.Equal("monitor-warning", failure.SourceFormat);
        Assert.Equal(0, plan.HelixFailureOffset);
        Assert.Equal(AzdoEvidencePlan.DefaultHelixFailureLimit, plan.HelixFailureLimit);
        Assert.Equal(1, plan.HelixFailureTotal);
        Assert.False(plan.HelixFailuresTruncated);
        Assert.True(plan.Complete);
        Assert.Empty(plan.IncompleteReasons);
        Assert.Empty(plan.IncompleteDetails);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_MonitorFailure_DoesNotCallHelixApi()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(RuntimeMonitorOnlyBuildId, RuntimeMonitorTimeline());
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        await _svc.GetEvidencePlanAsync(RuntimeMonitorOnlyBuildId.ToString(), DefaultOptions());

        await _helix.DidNotReceiveWithAnyArgs()
            .ListJobsByBuildAsync(default!, default!, default, default);
        await _helix.DidNotReceiveWithAnyArgs()
            .GetJobDetailsAsync(default!, default);
        await _helix.DidNotReceiveWithAnyArgs()
            .ListWorkItemsAsync(default!, default);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_MonitorUnparseable_RemainsIncompleteWithMonitorReason()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(
            RuntimeMonitorOnlyBuildId,
            TimelineWithMonitorIssues([new() { Type = "error", Message = "Failed work item information:" }]));
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(MonitorJobId, entry.JobId);
        Assert.Equal("Monitor Helix Jobs", entry.JobName);
        Assert.Equal("missing", entry.Status);
        Assert.Empty(plan.HelixFailures);
        Assert.False(plan.Complete);
        Assert.Contains(plan.IncompleteReasons, reason =>
            reason.Contains("no parseable Helix work-item failures", StringComparison.Ordinal));
        Assert.Contains(plan.IncompleteDetails, detail =>
            detail.Code == "monitor_unparseable"
            && detail.JobId == MonitorJobId
            && detail.JobName == "Monitor Helix Jobs");
    }

    [Fact]
    public async Task GetEvidencePlanAsync_UnresolvedMonitorJobId_IncompleteEvenWithParsedSibling()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(
            RuntimeMonitorOnlyBuildId,
            TimelineWithMonitorIssues(
            [
                new()
                {
                    Type = "warning",
                    Message =
                        $"""
                        Work item 'MissingGuid.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open' failed (Failed).
                        Work item 'Parsed.Tests' in job 'windows-x64 Release Libraries - windows.10.amd64.open ({SvcHelixJobId})' failed (Finished, exit code 1).
                        """
                }
            ]));
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        Assert.Single(plan.HelixFailures);
        Assert.False(plan.Complete);
        var detail = Assert.Single(plan.IncompleteDetails, detail => detail.Code == "monitor_unresolved_job_id");
        Assert.Equal(MonitorJobId, detail.JobId);
        Assert.Equal(1, detail.Count);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_MixedArtifactFailureAndMonitorFailure()
    {
        SetupBuild(RuntimeMixedBuildId);
        SetupTimeline(
            RuntimeMixedBuildId,
            TimelineWithRecords(
            [
                MonitorJob(),
                MonitorTask(LiveMonitorWarning(SvcHelixJobId, "windows.10.amd64.open.svc")),
                new()
                {
                    Id = CanceledBuildLegId,
                    Type = "Job",
                    Name = "osx-arm64 Debug Libraries_CheckedCoreCLR ",
                    State = "completed",
                    Result = "canceled",
                    Order = 1,
                    Attempt = 1,
                    Issues =
                    [
                        new()
                        {
                            Type = "error",
                            Message = "The job running on agent Azure Pipelines 52 ran longer than the maximum time of 120 minutes. For more information, see https://go.microsoft.com/fwlink/?linkid=2077134"
                        }
                    ]
                }
            ]));
        SetupArtifacts(
            RuntimeMixedBuildId,
            [
                new()
                {
                    Id = 49214240,
                    Name = "Logs_Build_Attempt1_osx__arm64_Debug_Libraries_CheckedCoreCLR",
                    Source = CanceledBuildLegId,
                    Resource = new()
                    {
                        Type = "PipelineArtifact",
                        Properties = new Dictionary<string, string> { ["artifactsize"] = "101049951" }
                    }
                }
            ]);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMixedBuildId.ToString(),
            DefaultOptions());

        var entry = Assert.Single(plan.Entries);
        Assert.Equal(CanceledBuildLegId, entry.JobId);
        Assert.Equal("mapped", entry.Status);
        Assert.Equal(49214240, Assert.Single(entry.Candidates).ArtifactId);
        Assert.Equal(101049951, entry.Candidates[0].SizeBytes);

        var failure = Assert.Single(plan.HelixFailures);
        Assert.Equal(SvcHelixJobId, failure.HelixJobId);
        Assert.Equal("windows.10.amd64.open.svc", failure.Queue);
        Assert.True(plan.Complete);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_MultipleMonitorJobs_AggregatesDeterministically()
    {
        const string secondMonitorJobId = "11111111-2222-3333-4444-555555555555";
        const string secondMonitorTaskId = "66666666-7777-8888-9999-aaaaaaaaaaaa";

        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(
            RuntimeMonitorOnlyBuildId,
            TimelineWithRecords(
            [
                MonitorJob() with { Order = 2, Attempt = 1 },
                MonitorTask(LiveMonitorWarning(RuntimeHelixJobId, "windows.10.amd64.open.rt")),
                MonitorJob(secondMonitorJobId, "Monitor Helix Jobs retry") with { Order = 1, Attempt = 2 },
                MonitorTask(
                    $"Work item 'Second.Tests' in job 'linux-x64 Release Libraries - ubuntu.2204.amd64.open ({SvcHelixJobId})' failed (Finished, exit code 2).",
                    secondMonitorTaskId,
                    secondMonitorJobId,
                    "Monitor Helix Jobs retry")
            ]));
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        Assert.Equal(2, plan.HelixFailures.Count);
        Assert.Equal(["Second.Tests", "System.Diagnostics.Process.Tests"], plan.HelixFailures.Select(f => f.WorkItem));
        Assert.Equal([2, 1], plan.HelixFailures.Select(f => f.MonitorJobAttempt));
        Assert.Empty(plan.Entries);
        Assert.True(plan.Complete);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_HelixFailuresOverCap_TruncatesAndIncomplete()
    {
        var total = AzdoEvidencePlan.DefaultHelixFailureLimit + 2;
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(RuntimeMonitorOnlyBuildId, TimelineWithMonitorIssues(CreateMonitorWarnings(total)));
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        Assert.Equal(0, plan.HelixFailureOffset);
        Assert.Equal(AzdoEvidencePlan.DefaultHelixFailureLimit, plan.HelixFailureLimit);
        Assert.Equal(total, plan.HelixFailureTotal);
        Assert.Equal(AzdoEvidencePlan.DefaultHelixFailureLimit, plan.HelixFailures.Count);
        Assert.True(plan.HelixFailuresTruncated);
        Assert.True(plan.Truncated);
        Assert.False(plan.Complete);
        Assert.Contains(plan.Warnings, warning => warning.Contains("Helix monitor failures truncated", StringComparison.Ordinal));

        var detail = Assert.Single(plan.IncompleteDetails, detail => detail.Code == "helix_failures_truncated");
        Assert.Equal(AzdoEvidencePlan.DefaultHelixFailureLimit, detail.Count);
        Assert.Equal(total, detail.Total);
    }

    [Fact]
    public async Task GetEvidencePlanAsync_HelixFailurePaging_ReturnsStableSecondPage()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(RuntimeMonitorOnlyBuildId, TimelineWithMonitorIssues(CreateMonitorWarnings(3)));
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var full = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions() with { HelixFailureLimit = 3 });
        var first = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions() with { HelixFailureLimit = 2 });
        var second = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions() with { HelixFailureOffset = 2, HelixFailureLimit = 2 });

        Assert.Equal(3, full.HelixFailureTotal);
        Assert.Equal(3, first.HelixFailureTotal);
        Assert.Equal(3, second.HelixFailureTotal);
        Assert.Equal(["WorkItem000", "WorkItem001", "WorkItem002"], full.HelixFailures.Select(f => f.WorkItem));
        Assert.Equal(["WorkItem000", "WorkItem001"], first.HelixFailures.Select(f => f.WorkItem));
        Assert.Equal(["WorkItem002"], second.HelixFailures.Select(f => f.WorkItem));
        Assert.Empty(first.HelixFailures.Select(f => f.WorkItem).Intersect(second.HelixFailures.Select(f => f.WorkItem), StringComparer.Ordinal));
        Assert.False(full.HelixFailuresTruncated);
        Assert.True(first.HelixFailuresTruncated);
        Assert.True(second.HelixFailuresTruncated);
        Assert.True(full.Complete);
        Assert.False(first.Complete);
        Assert.False(second.Complete);

        var secondDetail = Assert.Single(second.IncompleteDetails, detail => detail.Code == "helix_failures_truncated");
        Assert.Equal(1, secondDetail.Count);
        Assert.Equal(3, secondDetail.Total);
        Assert.Contains(second.Warnings, warning => warning.Contains("showing 3-3 of 3", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Warnings, warning => warning.Contains("showing first", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetEvidencePlanAsync_ParsedFailure_IncludesSuggestedFetchCoordinates()
    {
        SetupBuild(RuntimeMonitorOnlyBuildId);
        SetupTimeline(RuntimeMonitorOnlyBuildId, RuntimeMonitorTimeline());
        SetupArtifacts(RuntimeMonitorOnlyBuildId, []);

        var plan = await _svc.GetEvidencePlanAsync(
            RuntimeMonitorOnlyBuildId.ToString(),
            DefaultOptions());

        var failure = Assert.Single(plan.HelixFailures);
        Assert.Collection(
            failure.SuggestedFetches,
            fetch => AssertFetch(fetch, "helix_work_item", RuntimeHelixJobId, "System.Diagnostics.Process.Tests", "work-item metadata"),
            fetch => AssertFetch(fetch, "helix_logs", RuntimeHelixJobId, "System.Diagnostics.Process.Tests", "work-item console/log files"),
            fetch => AssertFetch(fetch, "helix_files", RuntimeHelixJobId, "System.Diagnostics.Process.Tests", "work-item artifact/file listing"));
    }

    [Fact]
    public void EvidencePlan_SerializesHelixFieldsAndLegacyFields()
    {
        var plan = new AzdoEvidencePlan
        {
            BuildId = RuntimeMonitorOnlyBuildId,
            Build = new AzdoBuildProvenance { BuildId = RuntimeMonitorOnlyBuildId, Org = "dnceng-public", Project = "public" },
            MatchStrategy = "auto",
            JobResultsFilter = ["failed", "canceled"],
            Entries =
            [
                new()
                {
                    JobId = CanceledBuildLegId,
                    JobName = "osx-arm64 Debug Libraries_CheckedCoreCLR ",
                    Status = "mapped",
                    Candidates = [new() { Rank = 0, ArtifactId = 49214240, ArtifactName = "Logs_Build_Attempt1_osx__arm64_Debug_Libraries_CheckedCoreCLR" }],
                    CandidateTotal = 1
                }
            ],
            HelixFailures =
            [
                new()
                {
                    MonitorJobId = MonitorJobId,
                    MonitorJobName = "Monitor Helix Jobs",
                    MonitorTaskId = MonitorTaskId,
                    MonitorTaskName = "Monitor Helix Jobs",
                    HelixJobId = RuntimeHelixJobId,
                    WorkItem = "System.Diagnostics.Process.Tests",
                    ExitCode = -1,
                    SourceFormat = "monitor-warning",
                    SuggestedFetches = [new() { Tool = "helix_work_item", HelixJobId = RuntimeHelixJobId, WorkItem = "System.Diagnostics.Process.Tests", Purpose = "work-item metadata" }]
                }
            ],
            HelixFailureOffset = 0,
            HelixFailureLimit = AzdoEvidencePlan.DefaultHelixFailureLimit,
            HelixFailureTotal = 1,
            Complete = false,
            IncompleteDetails = [new() { Code = "monitor_unparseable", Message = "monitor parse failed", JobId = MonitorJobId, JobName = "Monitor Helix Jobs" }],
            GeneratedAt = DateTimeOffset.Parse("2026-10-02T11:29:27-05:00")
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(plan));
        var root = json.RootElement;

        Assert.True(root.TryGetProperty("entries", out _));
        Assert.True(root.TryGetProperty("complete", out _));
        Assert.True(root.TryGetProperty("truncated", out _));
        Assert.True(root.TryGetProperty("warnings", out _));
        Assert.True(root.TryGetProperty("helixFailures", out _));
        Assert.True(root.TryGetProperty("helixFailureOffset", out _));
        Assert.True(root.TryGetProperty("helixFailureLimit", out _));
        Assert.True(root.TryGetProperty("helixFailureTotal", out _));
        Assert.True(root.TryGetProperty("helixFailuresTruncated", out _));
        Assert.True(root.TryGetProperty("incompleteDetails", out _));
        Assert.Equal(-1, root.GetProperty("helixFailures")[0].GetProperty("exitCode").GetInt32());
    }

    private static void AssertFetch(
        AzdoHelixEvidenceFetch fetch,
        string tool,
        string helixJobId,
        string workItem,
        string purpose)
    {
        Assert.Equal(tool, fetch.Tool);
        Assert.Equal(helixJobId, fetch.HelixJobId);
        Assert.Equal(workItem, fetch.WorkItem);
        Assert.Equal(purpose, fetch.Purpose);
    }

    private void SetupBuild(int buildId)
    {
        _azdo.GetBuildAsync("dnceng-public", "public", buildId, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = buildId,
                BuildNumber = $"runtime-{buildId}",
                Status = "completed",
                Result = "failed",
                Definition = new() { Id = 666, Name = "runtime" }
            });
    }

    private void SetupTimeline(int buildId, AzdoTimeline timeline) =>
        _azdo.GetTimelineAsync("dnceng-public", "public", buildId, Arg.Any<CancellationToken>())
            .Returns(timeline);

    private void SetupArtifacts(int buildId, IReadOnlyList<AzdoBuildArtifact> artifacts) =>
        _azdo.GetBuildArtifactsAsync("dnceng-public", "public", buildId, Arg.Any<CancellationToken>())
            .Returns(artifacts);

    private static AzdoEvidencePlanOptions DefaultOptions() => new()
    {
        JobResults = ["failed", "canceled"],
        ArtifactPattern = "Logs_Build_*",
        ArtifactJobPrefix = "Logs_Build_",
        StripAttemptPrefix = true,
        Match = "auto"
    };

    private static AzdoTimeline RuntimeMonitorTimeline() =>
        TimelineWithMonitorIssues(
        [
            new() { Type = "warning", Category = "General", Message = LiveMonitorWarning(RuntimeHelixJobId, "windows.10.amd64.open.rt") },
            new() { Type = "error", Category = "General", Message = "Failed work item information:" },
            new() { Type = "error", Category = "General", Message = "Bash exited with code '1'." }
        ]);

    private static AzdoTimeline TimelineWithMonitorIssues(IReadOnlyList<AzdoIssue> issues) =>
        TimelineWithRecords([MonitorJob(), MonitorTask(issues)]);

    private static AzdoTimeline TimelineWithRecords(IReadOnlyList<AzdoTimelineRecord> records) =>
        new() { Id = "timeline", Records = records };

    private static AzdoTimelineRecord MonitorJob(
        string id = MonitorJobId,
        string name = "Monitor Helix Jobs") => new()
    {
        Id = id,
        ParentId = "64403e3f-dfc4-5e18-208d-36820c6b1984",
        Type = "Job",
        Name = name,
        State = "completed",
        Result = "failed",
        Order = 1,
        Attempt = 1
    };

    private static AzdoTimelineRecord MonitorTask(
        string message,
        string id = MonitorTaskId,
        string parentId = MonitorJobId,
        string name = "Monitor Helix Jobs") =>
        MonitorTask([new() { Type = "warning", Category = "General", Message = message }], id, parentId, name);

    private static AzdoTimelineRecord MonitorTask(
        IReadOnlyList<AzdoIssue> issues,
        string id = MonitorTaskId,
        string parentId = MonitorJobId,
        string name = "Monitor Helix Jobs") => new()
    {
        Id = id,
        ParentId = parentId,
        Type = "Task",
        Name = name,
        State = "completed",
        Result = "failed",
        Order = 4,
        Attempt = 1,
        Issues = issues
    };

    private static string LiveMonitorWarning(string helixJobId, string queue) =>
        $"Work item 'System.Diagnostics.Process.Tests' in job 'windows-x86 Debug Libraries_CheckedCoreCLR - {queue} ({helixJobId})' failed (Finished, exit code -3).";

    private static IReadOnlyList<AzdoIssue> CreateMonitorWarnings(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AzdoIssue
            {
                Type = "warning",
                Category = "General",
                Message = $"Work item 'WorkItem{i:D3}' in job 'Leg {i:D3} - queue-{i:D3} ({GuidForIndex(i)})' failed (Finished, exit code {i})."
            })
            .ToList();

    private static string GuidForIndex(int index) =>
        $"aaaaaaaa-bbbb-cccc-dddd-{index:000000000000}";
}
