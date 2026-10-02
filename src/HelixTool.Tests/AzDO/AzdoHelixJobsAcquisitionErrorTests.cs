using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Helix;
using Microsoft.DotNet.Helix.Client;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class AzdoHelixJobsAcquisitionErrorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSdkJobDiscovery_CallerCancellationDoesNotFallBack_DallasGate(bool throughService)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new JobDiscoveryCancellationHandler(cancellation);
        using var http = new HttpClient(handler);
        var options = new HelixApiOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var helix = HelixApiClient.CreateForTesting(options);
        var azdo = Substitute.For<IAzdoApiClient>();
        azdo.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Reason = "individualCI" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (throughService)
                await new AzdoService(azdo, helix).GetHelixJobsAsync("42", ct: cancellation.Token);
            else
                await helix.ListJobsByBuildAsync("ci/public/dotnet/runtime/refs/heads/main", "42", ct: cancellation.Token);
        });

        Assert.True(cancellation.IsCancellationRequested);
        await azdo.DidNotReceive().GetTimelineAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.NotFound, true)]
    public async Task RealSdkJobDiscovery_ClassifiesAndRecordsTimelineFallback_DallasGate(
        HttpStatusCode status, bool transportFailure)
    {
        const string source = "ci/public/dotnet/runtime/refs/heads/main";
        const string jobId = "11111111-2222-3333-4444-555555555555";
        using var handler = new JobDiscoveryFailureHandler(status, transportFailure);
        using var http = new HttpClient(handler);
        var options = new HelixApiOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        var helix = HelixApiClient.CreateForTesting(options);
        var kind = status == HttpStatusCode.Forbidden ? AcquisitionErrorKind.AccessDenied : AcquisitionErrorKind.NotFound;

        var direct = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => helix.ListJobsByBuildAsync(source, "42"));

        AcquisitionAssertions.Error(direct, kind, "helix", "list_helix_jobs_by_build", (int)status);
        AcquisitionAssertions.Resource(direct.Error, "source", source);
        AcquisitionAssertions.Resource(direct.Error, "buildId", "42");
        AcquisitionAssertions.Resource(direct.Error, "count", 100_000);
        if (transportFailure)
            Assert.IsType<Azure.RequestFailedException>(direct.InnerException);

        var azdo = Substitute.For<IAzdoApiClient>();
        azdo.GetBuildAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild
            {
                Id = 42,
                Reason = "individualCI",
                Project = new AzdoTeamProjectRef { Name = "public" },
                Repository = new AzdoBuildRepository { Name = "dotnet/runtime" },
                SourceBranch = "refs/heads/main"
            });
        azdo.GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>())
            .Returns(TimelineWithOneJob(jobId));

        var result = await new AzdoService(azdo, helix).GetHelixJobsAsync("42", filter: "all");

        Assert.Equal("timeline", result.Strategy);
        Assert.False(result.Complete);
        Assert.Equal(jobId, Assert.Single(result.Jobs).HelixJobId);
        var primary = Assert.IsType<AcquisitionError>(result.PrimaryAcquisitionError);
        Assert.Equal(kind, primary.Kind);
        Assert.Equal("helix", primary.Provider);
        Assert.Equal("list_helix_jobs_by_build", primary.Operation);
        Assert.Equal((int)status, primary.HttpStatus);
        AcquisitionAssertions.Resource(primary, "source", source);
        AcquisitionAssertions.Resource(primary, "buildId", "42");
        AcquisitionAssertions.Resource(primary, "count", 100_000);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result));
        var recorded = document.RootElement.GetProperty("primaryAcquisitionError");
        Assert.Equal(status == HttpStatusCode.Forbidden ? "access_denied" : "not_found", recorded.GetProperty("kind").GetString());
        Assert.Equal("list_helix_jobs_by_build", recorded.GetProperty("operation").GetString());
        Assert.Equal((int)status, recorded.GetProperty("httpStatus").GetInt32());
        Assert.Equal(2, handler.Calls);
        await azdo.Received(1).GetTimelineAsync("dnceng-public", "public", 42, Arg.Any<CancellationToken>());
    }

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

    private sealed class JobDiscoveryFailureHandler(HttpStatusCode status, bool transportFailure) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Contains("/jobs", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (transportFailure)
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Helix job discovery rejected", null, status));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"message":"Helix job discovery rejected"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class JobDiscoveryCancellationHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }
}
