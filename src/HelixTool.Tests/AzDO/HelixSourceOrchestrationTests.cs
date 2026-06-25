// Tests for ComputeHelixSource prefix derivation and GetHelixJobsAsync primary/fallback
// orchestration (CCA coverage gap identified in PR #96).

using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
        string? sourceBranch = null) => new()
    {
        Reason = reason,
        Project = project is null ? null : new AzdoTeamProjectRef { Name = project },
        Repository = repository is null ? null : new AzdoBuildRepository { Name = repository },
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

    // ── Helix-success: timeline is NOT invoked ───────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_HelixReturnsJobs_ReturnsHelixResultAndSkipsTimeline()
    {
        var jobGuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        _helix.ListJobNamesByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<string>>([jobGuid]));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        Assert.Equal(1, result.TotalHelixJobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
        Assert.Equal("unknown", result.Jobs[0].Result);
        Assert.Empty(result.Jobs[0].FailedWorkItems);

        // Timeline must NOT have been consulted.
        await _azdo.DidNotReceive()
                   .GetTimelineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── Helix 0-result: falls back to timeline scraping ─────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_HelixReturnsEmpty_FallsBackToTimeline()
    {
        var jobGuid = "11111111-2222-3333-4444-555555555555";
        _helix.ListJobNamesByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .Returns(Task.FromResult<IReadOnlyList<string>>([]));

        _azdo.GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>())
             .Returns(TimelineWithOneJob(jobGuid));

        var result = await _svc.GetHelixJobsAsync(BuildIdStr, filter: "all");

        // Timeline path result is returned (ParentJobName set from timeline).
        Assert.Equal(1, result.TotalHelixJobs);
        Assert.Equal(jobGuid, result.Jobs[0].HelixJobId);
        Assert.Equal("Build Tests", result.Jobs[0].ParentJobName);

        await _azdo.Received(1)
                   .GetTimelineAsync("dnceng-public", "public", BuildId, Arg.Any<CancellationToken>());
    }

    // ── Helix throws non-cancellation → fallback ─────────────────────────────

    [Fact]
    public async Task GetHelixJobsAsync_HelixThrowsHttpException_FallsBackToTimeline()
    {
        var jobGuid = "22222222-3333-4444-5555-666666666666";
        _helix.ListJobNamesByBuildAsync(
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
        _helix.ListJobNamesByBuildAsync(
                Arg.Any<string>(), BuildIdStr, Arg.Any<int>(), Arg.Any<CancellationToken>())
              .ThrowsAsync(new OperationCanceledException("request cancelled"));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _svc.GetHelixJobsAsync(BuildIdStr, filter: "all"));

        // Timeline must NOT have been invoked.
        await _azdo.DidNotReceive()
                   .GetTimelineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
