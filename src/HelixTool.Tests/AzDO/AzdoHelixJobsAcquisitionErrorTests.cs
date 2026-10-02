using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class AzdoHelixJobsAcquisitionErrorTests
{
    [Fact]
    public async Task GetHelixJobsAsync_PrimaryAcquisitionErrorFallsBackAndIsRecorded()
    {
        var azdo = Substitute.For<IAzdoApiClient>();
        var helix = Substitute.For<IHelixApiClient>();
        azdo.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 42,
                Reason = "individualCI",
                Project = new AzdoTeamProjectRef { Name = "public" },
                Repository = new AzdoBuildRepository { Name = "dotnet/runtime" },
                SourceBranch = "refs/heads/main"
            });
        helix.ListJobsByBuildAsync(
                "ci/public/dotnet/runtime/refs/heads/main",
                "42",
                100_000,
                Arg.Any<CancellationToken>())
            .ThrowsAsync(AcquisitionAssertions.Exception(
                AcquisitionErrorKind.AccessDenied,
                "helix",
                "list_helix_jobs_by_build",
                new Dictionary<string, object?>
                {
                    ["source"] = "ci/public/dotnet/runtime/refs/heads/main",
                    ["buildId"] = "42",
                    ["count"] = 100_000
                },
                httpStatus: 403));
        azdo.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(TimelineWithOneJob("11111111-2222-3333-4444-555555555555"));

        var result = await new AzdoService(azdo, helix).GetHelixJobsAsync("42", filter: "all");

        Assert.Equal("timeline", result.Strategy);
        Assert.Single(result.Jobs);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var error = document.RootElement.GetProperty("primaryAcquisitionError");
        Assert.Equal("access_denied", error.GetProperty("kind").GetString());
        Assert.Equal("helix", error.GetProperty("provider").GetString());
        Assert.Equal("list_helix_jobs_by_build", error.GetProperty("operation").GetString());
        Assert.Equal(403, error.GetProperty("httpStatus").GetInt32());
        Assert.Equal("42", error.GetProperty("resource").GetProperty("buildId").GetString());
    }

    private static AzdoTimeline TimelineWithOneJob(string jobGuid) =>
        new()
        {
            Id = "tl-id",
            Records =
            [
                new AzdoTimelineRecord
                {
                    Id = "job1",
                    Name = "Build Tests",
                    Type = "Job",
                    Result = "failed",
                    State = "completed"
                },
                new AzdoTimelineRecord
                {
                    Id = "task1",
                    Name = "Send to Helix",
                    Type = "Task",
                    Result = "failed",
                    State = "completed",
                    ParentId = "job1",
                    Issues =
                    [
                        new AzdoIssue
                        {
                            Type = "error",
                            Message = $"Helix job started: https://helix.dot.net/api/2019-06-17/jobs/{jobGuid}/details"
                        }
                    ]
                }
            ]
        };
}
