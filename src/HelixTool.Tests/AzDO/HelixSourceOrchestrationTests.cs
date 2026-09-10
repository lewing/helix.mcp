// Tests for ComputeHelixSource prefix derivation and GetHelixJobsAsync primary/fallback
// orchestration (CCA coverage gap identified in PR #96).

using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Text.Json;
using Xunit;

namespace HelixTool.Tests.AzDO;

// ═══════════════════════════════════════════════════════════════════════════════
// ComputeHelixSource — internal static, exposed via InternalsVisibleTo
// Formula: {prefix}/{teamProject}/{repository}/{sourceBranch}
// prefix: "pr" if Reason=="pullRequest", "official" if project=="internal", else "ci"
// ═══════════════════════════════════════════════════════════════════════════════

public class ComputeHelixSourceTests
{
    private static AzdoBuild MakeBuild(
        string? reason = null,
        string? project = null,
        string? repository = null,
        string? repositoryId = null,
        string? repositoryType = null,
        string? sourceBranch = null) => new()
    {
        Reason = reason,
        Project = project is null ? null : new AzdoTeamProjectRef { Name = project },
        Repository = repository is null && repositoryId is null && repositoryType is null
            ? null
            : new AzdoBuildRepository
            {
                Name = repository,
                Id = repositoryId,
                Type = repositoryType,
            },
        SourceBranch = sourceBranch,
    };

    // ── PR prefix ────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeHelixSource_PullRequestReason_ReturnsPrPrefix()
    {
        var build = MakeBuild(
            reason: "pullRequest",
            project: "public",
            repository: "dotnet/runtime",
            sourceBranch: "refs/pull/123/merge");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("pr/public/dotnet/runtime/refs/pull/123/merge", source);
    }

    [Fact]
    public void ComputeHelixSource_PullRequestReason_CaseInsensitive()
    {
        var build = MakeBuild(reason: "PULLREQUEST", project: "public", repository: "repo", sourceBranch: "branch");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.StartsWith("pr/", source);
    }

    [Theory]
    [InlineData("GitHub", "pullRequest", "pr")]
    [InlineData("github", "individualCI", "ci")]
    [InlineData("GITHUB", "batchedCI", "ci")]
    public void ComputeHelixSource_BlankGitHubName_UsesRepositoryId(
        string repositoryType, string reason, string prefix)
    {
        var build = MakeBuild(
            reason: reason,
            project: "public",
            repository: " ",
            repositoryId: "dotnet/runtime",
            repositoryType: repositoryType,
            sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal($"{prefix}/public/dotnet/runtime/refs/heads/main", source);
    }

    [Fact]
    public void ComputeHelixSource_NonblankName_WinsOverGitHubRepositoryId()
    {
        var build = MakeBuild(
            reason: "pullRequest",
            project: "public",
            repository: "dotnet/runtime",
            repositoryId: "wrong/repository",
            repositoryType: "GitHub",
            sourceBranch: "refs/pull/123/merge");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("pr/public/dotnet/runtime/refs/pull/123/merge", source);
    }

    [Theory]
    [InlineData("TfsGit")]
    [InlineData(null)]
    public void ComputeHelixSource_BlankNonGitHubName_DoesNotUseRepositoryId(string? repositoryType)
    {
        var build = MakeBuild(
            reason: "individualCI",
            project: "public",
            repository: "",
            repositoryId: "dotnet/runtime",
            repositoryType: repositoryType,
            sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("ci/public//refs/heads/main", source);
    }

    // ── official prefix (internal project) ──────────────────────────────────

    [Fact]
    public void ComputeHelixSource_InternalProject_ReturnsOfficialPrefix()
    {
        var build = MakeBuild(
            reason: "manual",
            project: "internal",
            repository: "dotnet/runtime",
            sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("official/internal/dotnet/runtime/refs/heads/main", source);
    }

    [Fact]
    public void ComputeHelixSource_InternalProject_CaseInsensitive()
    {
        var build = MakeBuild(reason: "individualCI", project: "INTERNAL", repository: "repo", sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.StartsWith("official/", source);
    }

    [Fact]
    public void ComputeHelixSource_InternalProject_ScheduledBuild_ReturnsOfficialPrefix()
    {
        // Internal project always → "official", regardless of Reason
        var build = MakeBuild(reason: "schedule", project: "internal", repository: "repo", sourceBranch: "refs/heads/release/8.0");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("official/internal/repo/refs/heads/release/8.0", source);
    }

    // ── ci prefix (public project — any non-PR reason) ──────────────────────

    [Theory]
    [InlineData("manual")]
    [InlineData("schedule")]
    [InlineData("individualCI")]
    [InlineData("batchedCI")]
    [InlineData(null)]
    public void ComputeHelixSource_PublicProject_NonPrReason_ReturnsCiPrefix(string? reason)
    {
        var build = MakeBuild(reason: reason, project: "public", repository: "dotnet/runtime", sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal($"ci/public/dotnet/runtime/refs/heads/main", source);
    }

    // ── Branch normalization: raw SourceBranch passed through as-is ─────────
    // The formula does NOT strip "refs/heads/" — callers pass the raw AzDO value.

    [Fact]
    public void ComputeHelixSource_BranchRetainedVerbatim_RefsHeadsNotStripped()
    {
        var build = MakeBuild(
            reason: "individualCI",
            project: "public",
            repository: "dotnet/sdk",
            sourceBranch: "refs/heads/release/8.0.1xx");

        var source = AzdoService.ComputeHelixSource(build);

        // The refs/heads/ prefix is NOT stripped — it is part of the source string.
        Assert.Equal("ci/public/dotnet/sdk/refs/heads/release/8.0.1xx", source);
        Assert.DoesNotContain("ci/public/dotnet/sdk/release/", source);
    }

    // ── Edge: null/missing fields produce empty segments (no throw) ──────────

    [Fact]
    public void ComputeHelixSource_NullProject_EmptyProjectSegment()
    {
        var build = MakeBuild(reason: "individualCI", project: null, repository: "repo", sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("ci//repo/refs/heads/main", source);
    }

    [Fact]
    public void ComputeHelixSource_NullRepository_EmptyRepoSegment()
    {
        var build = MakeBuild(reason: "individualCI", project: "public", repository: null, sourceBranch: "refs/heads/main");

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("ci/public//refs/heads/main", source);
    }

    [Fact]
    public void ComputeHelixSource_NullSourceBranch_EmptyBranchSegment()
    {
        var build = MakeBuild(reason: "individualCI", project: "public", repository: "repo", sourceBranch: null);

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("ci/public/repo/", source);
    }

    [Fact]
    public void ComputeHelixSource_AllNullFields_EmptySegmentsNullReason()
    {
        // Reason null → falls to the else branch → "ci"
        var build = MakeBuild(reason: null, project: null, repository: null, sourceBranch: null);

        var source = AzdoService.ComputeHelixSource(build);

        Assert.Equal("ci///", source);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// GetHelixJobsAsync orchestration — primary path + fallback
// Uses the two-arg constructor (IAzdoApiClient + IHelixApiClient).
// ═══════════════════════════════════════════════════════════════════════════════

public class GetHelixJobsOrchestrationTests
{
    private const int BuildId = 42;
    private const string BuildIdStr = "42";

    private readonly IAzdoApiClient _azdo;
    private readonly IHelixApiClient _helix;
    private readonly AzdoService _svc;

    // A minimal valid build for GetBuildAsync to return.
    private static readonly AzdoBuild TestBuild = new()
    {
        Id = BuildId,
        Reason = "individualCI",
        Project = new AzdoTeamProjectRef { Name = "public" },
        Repository = new AzdoBuildRepository { Name = "dotnet/runtime" },
        SourceBranch = "refs/heads/main",
    };

    private static IHelixJobSummary Summary(
        string name,
        string? finished = null,
        string? previousJobName = null)
    {
        var summary = Substitute.For<IHelixJobSummary>();
        summary.Name.Returns(name);
        summary.Finished.Returns(finished);
        summary.PreviousHelixJobName.Returns(previousJobName);
        return summary;
    }

    private static IReadOnlyList<IHelixJobSummary> Build1590167Summaries()
    {
        const string failedJobId = "d170b0c9-c2f8-4b79-878b-f371c7f155f9";
        var summaries = new List<IHelixJobSummary>
        {
            Summary(failedJobId, "2026-09-09T12:00:00Z")
        };
        summaries.AddRange(Enumerable.Range(1, 47).Select(index =>
            Summary(
                $"00000000-0000-0000-0000-{index:D12}",
                "2026-09-09T12:00:00Z",
                index == 1 ? failedJobId : null)));
        return summaries;
    }

    private static AzdoTimeline Build1590167Timeline() =>
        new()
        {
            Id = "1590167-timeline",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "monitor", Name = "Monitor Helix Jobs", Type = "Task",
                    State = "completed", Result = "failed",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "error",
                            Message = """
                                Work item 'GC-scenarios1' in job 'GC scenarios (d170b0c9-c2f8-4b79-878b-f371c7f155f9)' failed (Failed (AzDO tests)).
                                Console: no console link available
                                """
                        },
                        new AzdoIssue
                        {
                            Type = "warning",
                            Message = """
                                Failed work item information:
                                └─ warning-tree-item (Job: GC scenarios (d170b0c9-c2f8-4b79-878b-f371c7f155f9)) (Failed)
                                   └─ Console: no console link available
                                """
                        }
                    ]
                },
                new AzdoTimelineRecord
                {
                    Id = "send", Name = "Send to Helix", Type = "Task",
                    State = "completed", Result = "succeeded",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "warning",
                            Message = "Helix job started: https://helix.dot.net/api/2019-06-17/jobs/00000000-0000-0000-0000-000000000002/details"
                        }
                    ]
                },
                new AzdoTimelineRecord
                {
                    Id = "unrelated", Name = "Publish diagnostics", Type = "Task",
                    State = "completed", Result = "succeeded",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "warning",
                            Message = "Unrelated timeline warning."
                        }
                    ]
                }
            ]
        };

    // Timeline that yields one Helix job (for the fallback assertions).
    private static AzdoTimeline TimelineWithOneJob(string jobGuid) =>
        new()
        {
            Id = "tl-id",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "job1", Name = "Build Tests", Type = "Job",
                    Result = "failed", State = "completed",
                },
                new AzdoTimelineRecord
                {
                    Id = "task1", Name = "Send to Helix", Type = "Task",
                    Result = "failed", State = "completed", ParentId = "job1",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "error",
                            Message = $"Helix job started: https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/details",
                        },
                    ],
                },
            ],
        };

    public GetHelixJobsOrchestrationTests()
    {
        _azdo  = Substitute.For<IAzdoApiClient>();
        _helix = Substitute.For<IHelixApiClient>();
        _svc   = new AzdoService(_azdo, _helix);

        // Default: GetBuildAsync returns a valid build.
        _azdo.GetBuildAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(TestBuild);
    }

    // ── Helix-success: timeline is fetched once for build-level evidence ─────

    [Fact]
    public async Task GetHelixJobsAsync_HelixReturnsJobs_ReturnsHelixResultAndFetchesTimelineOnce()
    {
        var jobGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var summary = Summary(jobGuid, "2026-09-04T12:00:00Z");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(new AzdoTimeline { Records = [] });

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Equal(1, result.TotalHelixJobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
        Assert.Equal("completed", result.Jobs[0].Result);
        Assert.Equal("completed", result.Jobs[0].State);
        Assert.Empty(result.Jobs[0].FailedWorkItems);
        Assert.Equal("helix", result.Strategy);
        Assert.Equal("ci/public/dotnet/runtime/refs/heads/main", result.Source);
        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(1, result.OutcomeUnknownHelixJobs);
        Assert.NotNull(result.TimelineIssues);
        Assert.Empty(result.TimelineIssues);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal(JsonValueKind.Array,
            json.RootElement.GetProperty("timelineIssues").ValueKind);

        await _azdo.Received(1)
            .GetBuildAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        await _helix.Received(1)
            .ListJobsByBuildAsync(
                "ci/public/dotnet/runtime/refs/heads/main",
                BuildIdStr,
                100_000,
                Arg.Any<CancellationToken>());

        await _azdo.Received(1)
            .GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        Assert.Equal(2, _azdo.ReceivedCalls().Count());
        Assert.Single(_helix.ReceivedCalls());
    }

    [Fact]
    public async Task GetHelixJobsAsync_BlankGitHubName_UsesIdForPrimaryLookupWithoutTimelineFallback()
    {
        var primaryJobGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var fallbackJobGuid = "11111111-2222-3333-4444-555555555555";
        var primarySummary = Summary(primaryJobGuid);
        var build = TestBuild with
        {
            Reason = "pullRequest",
            Repository = new AzdoBuildRepository
            {
                Name = " ",
                Id = "dotnet/runtime",
                Type = "gItHuB",
            },
            SourceBranch = "refs/pull/123/merge",
        };
        _azdo.GetBuildAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(build);
        _helix.ListJobsByBuildAsync(
                "pr/public/dotnet/runtime/refs/pull/123/merge",
                BuildIdStr,
                100_000,
                Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([primarySummary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(TimelineWithOneJob(fallbackJobGuid));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Equal("helix", result.Strategy);
        Assert.Equal("pr/public/dotnet/runtime/refs/pull/123/merge", result.Source);
        Assert.Single(result.Jobs);
        Assert.Equal(primaryJobGuid, result.Jobs[0].HelixJobId);
        Assert.DoesNotContain(result.Jobs, job => job.HelixJobId == fallbackJobGuid);
        await _helix.Received(1).ListJobsByBuildAsync(
            "pr/public/dotnet/runtime/refs/pull/123/merge",
            BuildIdStr,
            100_000,
            Arg.Any<CancellationToken>());
    }

    // ── Helix 0-result: falls back to timeline scraping ─────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_HelixReturnsEmpty_FallsBackToTimeline()
    {
        var jobGuid = "11111111-2222-3333-4444-555555555555";
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([]));

        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(TimelineWithOneJob(jobGuid));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        // Timeline path result is returned (ParentJobName set from timeline).
        Assert.Equal(1, result.TotalHelixJobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
        Assert.Equal("Build Tests", result.Jobs[0].ParentJobName);
        Assert.Equal("timeline", result.Strategy);

        await _azdo.Received(1)
                   .GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
    }

    // ── Helix throws non-cancellation → fallback ─────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_HelixThrowsHttpException_FallsBackToTimeline()
    {
        var jobGuid = "22222222-3333-4444-5555-666666666666";
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new HttpRequestException("Helix auth failure (403)"));

        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(TimelineWithOneJob(jobGuid));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        // Timeline result is returned despite Helix throwing.
        Assert.Equal(1, result.TotalHelixJobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);

        await _azdo.Received(1)
                   .GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
    }

    // ── Helix throws OperationCanceledException → propagates (354d736 fix) ──

    [Fact]
    public async Task GetHelixJobsAsync_HelixThrowsOperationCanceled_PropagatesAndSkipsTimeline()
    {
        // Ripley's commit 354d736: `catch (Exception ex) when (ex is not OperationCanceledException)`
        // means cancellation must bubble out, NOT trigger the timeline fallback.
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new OperationCanceledException("request cancelled"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _svc.GetHelixJobsAsync(BuildIdStr, filter: "all"));

        // Timeline must NOT have been invoked.
        await _azdo.DidNotReceive()
                   .GetTimelineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Note population: Helix Result is completion state, not pass/fail ──────

    [Fact]
    public async Task GetHelixJobsAsync_HelixSuccess_FilterFailed_NotePopulatedAndProjectionCorrect()
    {
        var jobGuid = "cccccccc-dddd-eeee-ffff-000000000000";
        var summary = Summary(jobGuid);
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(new AzdoTimeline { Records = [] });

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "failed");

        Assert.NotNull(result.Note);
        Assert.Contains("filter='failed'", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pass/fail", result.Note, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(0, result.OutcomeUnknownHelixJobs);
        Assert.Empty(result.Jobs);

        await _azdo.Received(1)
            .GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("all", 48, 1, 47)]
    [InlineData("failed", 1, 1, 0)]
    [InlineData("issues", 1, 1, 0)]
    [InlineData("running", 0, 0, 0)]
    [InlineData("incomplete", 0, 0, 0)]
    [InlineData("pending", 0, 0, 0)]
    public async Task GetHelixJobsAsync_PrimaryFilters_ModelBuild1590167(
        string filter,
        int expectedTotal,
        int expectedFailed,
        int expectedUnknown)
    {
        const string failedJobId = "d170b0c9-c2f8-4b79-878b-f371c7f155f9";
        var summaries = Build1590167Summaries();
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(summaries));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
            .Returns(Build1590167Timeline());

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter);

        Assert.Equal("helix", result.Strategy);
        Assert.Equal(expectedTotal, result.TotalHelixJobs);
        Assert.Equal(expectedFailed, result.FailedHelixJobs);
        Assert.Equal(expectedUnknown, result.OutcomeUnknownHelixJobs);
        Assert.Contains(result.TimelineIssues!,
            issue => issue.Messages.Contains("Unrelated timeline warning."));

        if (filter is "all" or "failed" or "issues")
        {
            var failedJob = Assert.Single(result.Jobs,
                job => job.HelixJobId == failedJobId);
            Assert.Equal(["GC-scenarios1", "warning-tree-item"], failedJob.FailedWorkItems);
            Assert.Equal("completed", failedJob.State);
            Assert.Equal("completed", failedJob.Result);
            Assert.True(failedJob.Superseded);
            Assert.Equal(failedJobId,
                filter == "all" ? result.Jobs[0].HelixJobId : Assert.Single(result.Jobs).HelixJobId);
        }

        if (filter != "all")
        {
            Assert.Contains($"filter='{filter}'", result.Note,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("discovered 48", result.Note,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"returned {expectedTotal}", result.Note,
                StringComparison.OrdinalIgnoreCase);
        }

        await _azdo.Received(1).GetBuildAsync(
            "dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        await _helix.Received(1).ListJobsByBuildAsync(
            "ci/public/dotnet/runtime/refs/heads/main",
            BuildIdStr,
            100_000,
            Arg.Any<CancellationToken>());
        await _azdo.Received(1).GetTimelineAsync(
            "dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        Assert.Equal(2, _azdo.ReceivedCalls().Count());
        Assert.Single(_helix.ReceivedCalls());
    }

    [Fact]
    public async Task GetHelixJobsAsync_PrimaryUnknownMonitorJob_IsIgnoredWithoutSyntheticRow()
    {
        const string knownJobId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string unknownJobId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        IReadOnlyList<IHelixJobSummary> summaries =
            [Summary(knownJobId, "2026-09-09T12:00:00Z")];
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(summaries));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new AzdoTimelineRecord
                    {
                        Id = "monitor", Name = "Monitor Helix Jobs", Type = "Task",
                        Issues =
                        [
                            new AzdoIssue
                            {
                                Type = "error",
                                Message = $"Work item 'Unknown.dll' in job 'Unknown ({unknownJobId})' failed (Failed)."
                            }
                        ]
                    }
                ]
            });

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        var job = Assert.Single(result.Jobs);
        Assert.Equal(knownJobId, job.HelixJobId);
        Assert.Empty(job.FailedWorkItems);
        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(1, result.OutcomeUnknownHelixJobs);
        Assert.Contains("Ignored failure evidence for 1 monitor job ID", result.Note,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetHelixJobsAsync_PrimaryMonitorEntries_DoNotBorrowSiblingConsoleUrl()
    {
        const string knownJobId = "abababab-bbbb-cccc-dddd-eeeeeeeeeeee";
        IReadOnlyList<IHelixJobSummary> summaries =
            [Summary(knownJobId, "2026-09-09T12:00:00Z")];
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(summaries));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline
            {
                Records =
                [
                    new AzdoTimelineRecord
                    {
                        Id = "monitor", Name = "Monitor Helix Jobs", Type = "Task",
                        Issues =
                        [
                            new AzdoIssue
                            {
                                Type = "warning",
                                Message = $"""
                                    Work item 'Unknown.dll' in job 'Label without a guid' failed (Failed).
                                    Work item 'Known.dll' in job 'Known ({knownJobId})' failed (Failed).
                                    Console: https://helix.dot.net/api/2019-06-17/jobs/{knownJobId}/workitems/Known.dll/console
                                    """
                            }
                        ]
                    }
                ]
            });

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "failed");

        var job = Assert.Single(result.Jobs);
        Assert.Equal(knownJobId, job.HelixJobId);
        Assert.Equal(["Known.dll"], job.FailedWorkItems);
        Assert.Contains("Unknown.dll", Assert.Single(result.TimelineIssues!).Messages[0]);
    }

    [Fact]
    public async Task GetHelixJobsAsync_HelixSuccess_FilterAll_ExplainsResultVocabulary()
    {
        var jobGuid = "dddddddd-eeee-ffff-0000-111111111111";
        var summary = Summary(jobGuid, "2026-09-04T12:00:00Z");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(new AzdoTimeline { Records = [] });

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Contains("completion", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pass/fail", result.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(1, result.OutcomeUnknownHelixJobs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("running")]
    public async Task GetHelixJobsAsync_PrimaryTimelineIssues_AreIndependentOfJobFilter(string? filter)
    {
        var summary = Summary("eeeeeeee-ffff-0000-1111-222222222222");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(new AzdoTimeline
             {
                 Records =
                 [
                     new AzdoTimelineRecord
                     {
                         Id = "parent-record", Name = "Linux tests", Type = "jOb"
                     },
                     new AzdoTimelineRecord
                     {
                         Id = "task-record", ParentId = "PARENT-RECORD",
                         Name = "Publish diagnostics", Type = "tAsK",
                         State = "inProgress", Result = null,
                         Issues =
                         [
                             new AzdoIssue { Type = "ERROR", Message = "first error" },
                             new AzdoIssue { Type = "warning", Message = "warning" }
                         ]
                     }
                 ]
             });

        var result = filter is null
            ? await _svc.GetHelixJobsAsync(BuildIdStr)
            : await _svc.GetHelixJobsAsync(BuildIdStr, filter);

        var issue = Assert.Single(result.TimelineIssues!);
        Assert.Equal("task-record", issue.RecordId);
        Assert.Equal("Publish diagnostics", issue.TaskName);
        Assert.Equal("Linux tests", issue.ParentJobName);
        Assert.Equal("running", issue.State);
        Assert.Equal("unknown", issue.Result);
        Assert.Equal(1, issue.ErrorCount);
        Assert.Equal(1, issue.WarningCount);
        Assert.Equal(["first error", "warning"], issue.Messages);
        Assert.All(result.Jobs, job =>
        {
            Assert.Equal(0, job.TaskErrorCount);
            Assert.Equal(0, job.TaskWarningCount);
        });
    }

    [Fact]
    public async Task GetHelixJobsAsync_PrimaryTimelineFailure_PreservesDiscoveryAndWarns()
    {
        var summary = Summary("ffffffff-0000-1111-2222-333333333333");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .ThrowsAsync(new HttpRequestException("timeline offline"));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Equal("helix", result.Strategy);
        Assert.Single(result.Jobs);
        Assert.Null(result.TimelineIssues);
        Assert.Contains("timeline issue evidence is unavailable", result.Note,
            StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.TryGetProperty("timelineIssues", out _));
    }

    [Theory]
    [MemberData(nameof(ExpectedTimelineEnrichmentFailures))]
    public async Task GetHelixJobsAsync_PrimaryExpectedTimelineFailure_DoesNotRediscover(
        Exception timelineFailure)
    {
        var summary = Summary("12345678-0000-1111-2222-333333333333");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .ThrowsAsync(timelineFailure);

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Equal("helix", result.Strategy);
        Assert.Single(result.Jobs);
        Assert.Null(result.TimelineIssues);
        Assert.Contains("timeline issue evidence is unavailable", result.Note,
            StringComparison.OrdinalIgnoreCase);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.False(json.RootElement.TryGetProperty("timelineIssues", out _));
        await _helix.Received(1).ListJobsByBuildAsync(
            Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _azdo.Received(1).GetTimelineAsync(
            "dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        Assert.Equal(2, _azdo.ReceivedCalls().Count());
    }

    public static TheoryData<Exception> ExpectedTimelineEnrichmentFailures => new()
    {
        new JsonException("invalid timeline JSON"),
        new InvalidOperationException("Network blocked: eval mode. Cache key not found in snapshot.")
    };

    [Theory]
    [InlineData("all", 1)]
    [InlineData("running", 1)]
    [InlineData("incomplete", 1)]
    [InlineData("pending", 0)]
    [InlineData("failed", 0)]
    [InlineData("issues", 0)]
    public async Task GetHelixJobsAsync_PrimaryTimelineUnavailable_AppliesConclusiveFilters(
        string filter,
        int expectedTotal)
    {
        var summary = Summary("12345678-1234-1234-1234-123456789abc");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("timeline offline"));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter);

        Assert.Equal(expectedTotal, result.TotalHelixJobs);
        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(expectedTotal, result.OutcomeUnknownHelixJobs);
        Assert.Null(result.TimelineIssues);
        Assert.Contains("timeline issue evidence is unavailable", result.Note,
            StringComparison.OrdinalIgnoreCase);
        if (filter is "failed" or "issues")
        {
            Assert.Empty(result.Jobs);
            Assert.Contains("inconclusive", result.Note,
                StringComparison.OrdinalIgnoreCase);
        }
        else if (expectedTotal == 1)
        {
            var job = Assert.Single(result.Jobs);
            Assert.Equal("running", job.State);
            Assert.Equal("running", job.Result);
        }
    }

    [Fact]
    public async Task GetHelixJobsAsync_PrimaryTimelineCallerCancellation_TakesPrecedenceWithoutRediscovery()
    {
        using var cts = new CancellationTokenSource();
        var summary = Summary("87654321-1111-2222-3333-444444444444");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 cts.Cancel();
                 return Task.FromCanceled<AzdoTimeline?>(cts.Token);
             });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.GetHelixJobsAsync(BuildIdStr, ct: cts.Token));

        await _helix.Received(1).ListJobsByBuildAsync(
            Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _azdo.Received(1).GetTimelineAsync(
            "dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
        Assert.Equal(2, _azdo.ReceivedCalls().Count());
    }

    [Fact]
    public async Task GetHelixJobsAsync_PrimaryTimelineCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var summary = Summary("00000000-1111-2222-3333-444444444444");
        _helix.ListJobsByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>([summary]));
        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 cts.Cancel();
                 return Task.FromCanceled<AzdoTimeline?>(cts.Token);
             });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _svc.GetHelixJobsAsync(BuildIdStr, ct: cts.Token));
    }
}
