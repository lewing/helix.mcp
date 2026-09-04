using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class HelixJobProjectionTests
{
    private const string Source = "ci/public/dotnet/runtime/refs/heads/main";

    private static AzdoBuild Build() => new()
    {
        Id = 42,
        Reason = "individualCI",
        Project = new AzdoTeamProjectRef { Name = "public" },
        Repository = new AzdoBuildRepository { Name = "dotnet/runtime" },
        SourceBranch = "refs/heads/main"
    };

    private static IHelixJobSummary Summary(
        string name,
        string? phaseName = null,
        string? jobDisplayName = null,
        string? jobName = null,
        string? finished = null,
        string? queueId = null,
        int? workItemCount = null,
        string? previousJobName = null)
    {
        var summary = Substitute.For<IHelixJobSummary>();
        summary.Name.Returns(name);
        summary.PhaseName.Returns(phaseName);
        summary.JobDisplayName.Returns(jobDisplayName);
        summary.JobName.Returns(jobName);
        summary.Finished.Returns(finished);
        summary.QueueId.Returns(queueId);
        summary.InitialWorkItemCount.Returns(workItemCount);
        summary.PreviousHelixJobName.Returns(previousJobName);
        return summary;
    }

    private static async Task<HelixJobsFromBuildResult> ProjectAsync(
        params IHelixJobSummary[] summaries)
    {
        var azdo = Substitute.For<IAzdoApiClient>();
        var helix = Substitute.For<IHelixApiClient>();
        azdo.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(Build());
        helix.ListJobsByBuildAsync(Source, "42", 100_000, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IHelixJobSummary>>(summaries));
        azdo.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Records = [] });

        return await new AzdoService(azdo, helix).GetHelixJobsAsync("42", filter: "all");
    }

    [Theory]
    [InlineData("Build_Release", "Windows_NT Build_Release", "Logical", "Build_Release")]
    [InlineData(null, "Windows_NT Build_Release", "Logical", "Windows_NT Build_Release")]
    [InlineData(null, null, "Build_Release", "Build_Release")]
    [InlineData(null, null, "__default", "")]
    [InlineData(null, null, null, "")]
    public async Task ParentJobName_UsesResolutionLadder(
        string? phaseName, string? displayName, string? jobName, string expected)
    {
        var summary = Summary(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            phaseName: phaseName,
            jobDisplayName: displayName,
            jobName: jobName);

        var job = Assert.Single((await ProjectAsync(summary)).Jobs);

        Assert.Equal(expected, job.ParentJobName);
    }

    [Theory]
    [InlineData(null, "running")]
    [InlineData("", "running")]
    [InlineData("2026-09-04T12:00:00Z", "completed")]
    public async Task Finished_DeterminesStateAndResult(string? finished, string expected)
    {
        var job = Assert.Single((await ProjectAsync(
            Summary("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", finished: finished))).Jobs);

        Assert.Equal(expected, job.State);
        Assert.Equal(expected, job.Result);
    }

    [Fact]
    public async Task QueueAndWorkItemCount_RoundTrip()
    {
        var job = Assert.Single((await ProjectAsync(
            Summary(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                queueId: "Windows.10.Amd64.Open",
                workItemCount: 73))).Jobs);

        Assert.Equal("Windows.10.Amd64.Open", job.QueueId);
        Assert.Equal(73, job.WorkItemCount);
    }

    [Fact]
    public async Task PreviousJobLineage_AnnotatesSupersededWithoutFilteringOrChangingCounts()
    {
        const string firstName = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        const string secondName = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
        var result = await ProjectAsync(
            Summary(firstName, finished: "2026-09-04T12:00:00Z"),
            Summary(secondName, previousJobName: firstName.ToUpperInvariant()));

        Assert.Equal(2, result.TotalHelixJobs);
        Assert.Equal(0, result.FailedHelixJobs);
        Assert.Equal(2, result.OutcomeUnknownHelixJobs);
        Assert.True(Assert.Single(result.Jobs, job => job.HelixJobId == firstName).Superseded);
        Assert.False(Assert.Single(result.Jobs, job => job.HelixJobId == secondName).Superseded);
    }

    [Fact]
    public async Task HelixDefaults_AreOmittedFromJsonWhileOriginalMembersRemain()
    {
        var job = Assert.Single((await ProjectAsync(
            Summary(
                "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                finished: "2026-09-04T12:00:00Z"))).Jobs);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(job));

        Assert.False(json.RootElement.TryGetProperty("queueId", out _));
        Assert.False(json.RootElement.TryGetProperty("workItemCount", out _));
        Assert.False(json.RootElement.TryGetProperty("superseded", out _));
        Assert.False(json.RootElement.TryGetProperty("taskErrorCount", out _));
        Assert.False(json.RootElement.TryGetProperty("taskWarningCount", out _));
        Assert.False(json.RootElement.TryGetProperty("messages", out _));
        Assert.True(json.RootElement.TryGetProperty("state", out _));
        Assert.True(json.RootElement.TryGetProperty("HelixJobId", out _));
        Assert.True(json.RootElement.TryGetProperty("ParentJobName", out _));
        Assert.True(json.RootElement.TryGetProperty("Result", out _));
        Assert.True(json.RootElement.TryGetProperty("FailedWorkItems", out _));
    }
}
