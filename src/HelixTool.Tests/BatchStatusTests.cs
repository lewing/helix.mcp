using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class BatchStatusTests
{
    private const string JobId1 = "aaaaaaaa-1111-2222-3333-444444444444";
    private const string JobId2 = "bbbbbbbb-5555-6666-7777-888888888888";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public BatchStatusTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    private void SetupJobMock(string jobId, string jobName, List<(string name, int exitCode)> workItems)
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns(jobName);
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/test");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wiSummaries = new List<IWorkItemSummary>();
        foreach (var (name, exitCode) in workItems)
        {
            var wi = Substitute.For<IWorkItemSummary>();
            wi.Name.Returns(name);
            wiSummaries.Add(wi);

            var detail = Substitute.For<IWorkItemDetails>();
            detail.ExitCode.Returns(exitCode);
            detail.State.Returns("Finished");
            detail.MachineName.Returns("helix-machine");
            detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
            detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 1, 0, TimeSpan.Zero));

            _mockApi.GetWorkItemDetailsAsync(name, jobId, Arg.Any<CancellationToken>())
                .Returns(detail);
        }

        _mockApi.ListWorkItemsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(wiSummaries);
    }

    [Fact]
    public async Task GetBatchStatus_AggregatesMultipleJobs()
    {
        // Job1: 2 passed, 1 failed
        SetupJobMock(JobId1, "job-one", [
            ("wi-1a", 0),
            ("wi-1b", 0),
            ("wi-1c", 1)
        ]);

        // Job2: 1 passed, 2 failed
        SetupJobMock(JobId2, "job-two", [
            ("wi-2a", 0),
            ("wi-2b", 1),
            ("wi-2c", 1)
        ]);

        // Act
        var result = await _svc.GetBatchStatusAsync([JobId1, JobId2]);

        // Assert
        Assert.Equal(2, result.Jobs.Count);
        Assert.Equal(3, result.TotalFailed);  // 1 + 2
        Assert.Equal(3, result.TotalPassed);  // 2 + 1
    }

    [Fact]
    public async Task GetBatchStatus_ThrowsOnEmptyList()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetBatchStatusAsync([]));
    }

    [Fact]
    public async Task GetBatchStatus_SingleJob_MatchesDirectCall()
    {
        // Arrange: single job with 1 passed, 1 failed
        SetupJobMock(JobId1, "single-job", [
            ("wi-pass", 0),
            ("wi-fail", 1)
        ]);

        // Act: get batch status with single job
        var batchResult = await _svc.GetBatchStatusAsync([JobId1]);

        // Also get direct job status
        var directResult = await _svc.GetJobStatusAsync(JobId1);

        // Assert: batch with one job should match direct call
        Assert.Single(batchResult.Jobs);
        var batchJob = batchResult.Jobs[0];

        Assert.Equal(directResult.JobId, batchJob.JobId);
        Assert.Equal(directResult.Name, batchJob.Name);
        Assert.Equal(directResult.Failed.Count, batchJob.Failed.Count);
        Assert.Equal(directResult.Passed.Count, batchJob.Passed.Count);
        Assert.Equal(batchResult.TotalFailed, batchJob.Failed.Count);
        Assert.Equal(batchResult.TotalPassed, batchJob.Passed.Count);
    }
}
