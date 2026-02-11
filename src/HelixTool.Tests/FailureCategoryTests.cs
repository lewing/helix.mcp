using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class FailureCategoryTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    // --- Static ClassifyFailure tests (1–10) ---

    [Fact]
    public void ClassifyFailure_TimedOutState_ReturnsTimeout()
    {
        var result = HelixService.ClassifyFailure(1, "Timed Out", null, "test.dll");
        Assert.Equal(FailureCategory.Timeout, result);
    }

    [Fact]
    public void ClassifyFailure_StateContainsTimeout_ReturnsTimeout()
    {
        var result = HelixService.ClassifyFailure(1, "timeout occurred", null, "test.dll");
        Assert.Equal(FailureCategory.Timeout, result);
    }

    [Fact]
    public void ClassifyFailure_NegativeExitCode_ReturnsCrash()
    {
        var result = HelixService.ClassifyFailure(-2, "Finished", null, "test.dll");
        Assert.Equal(FailureCategory.Crash, result);
    }

    [Fact]
    public void ClassifyFailure_HighExitCode_ReturnsCrash()
    {
        var result = HelixService.ClassifyFailure(139, "Finished", null, "test.dll");
        Assert.Equal(FailureCategory.Crash, result);
    }

    [Fact]
    public void ClassifyFailure_ExitCode1_BuildWorkItem_ReturnsBuildFailure()
    {
        var result = HelixService.ClassifyFailure(1, "Finished", null, "build.cmd");
        Assert.Equal(FailureCategory.BuildFailure, result);
    }

    [Fact]
    public void ClassifyFailure_ExitCode1_TestWorkItem_ReturnsTestFailure()
    {
        var result = HelixService.ClassifyFailure(1, "Finished", null, "System.Runtime.Tests.dll");
        Assert.Equal(FailureCategory.TestFailure, result);
    }

    [Fact]
    public void ClassifyFailure_ExitCode1_GenericWorkItem_ReturnsTestFailure()
    {
        var result = HelixService.ClassifyFailure(1, "Finished", null, "something");
        Assert.Equal(FailureCategory.TestFailure, result);
    }

    [Fact]
    public void ClassifyFailure_StateContainsError_ReturnsInfrastructureError()
    {
        var result = HelixService.ClassifyFailure(2, "Infrastructure error", null, "test.dll");
        Assert.Equal(FailureCategory.InfrastructureError, result);
    }

    [Fact]
    public void ClassifyFailure_ExitCodeMinus1_NoState_ReturnsUnknown()
    {
        var result = HelixService.ClassifyFailure(-1, null, null, "test.dll");
        Assert.Equal(FailureCategory.Unknown, result);
    }

    [Fact]
    public void ClassifyFailure_ExitCode0_ReturnsUnknown()
    {
        var result = HelixService.ClassifyFailure(0, "Finished", null, "test.dll");
        Assert.Equal(FailureCategory.Unknown, result);
    }

    // --- Integration tests via GetJobStatusAsync (11–12) ---

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public FailureCategoryTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    [Fact]
    public async Task GetJobStatus_FailedItems_HaveFailureCategory()
    {
        // Arrange
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("categorize-job");
        jobDetails.QueueId.Returns("ubuntu.2204.amd64");
        jobDetails.Creator.Returns("test@test.com");
        jobDetails.Source.Returns("pr/999");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("System.Runtime.Tests.dll");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(1);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("helix-linux-01");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 1, 0, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("System.Runtime.Tests.dll", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);

        // Act
        var result = await _svc.GetJobStatusAsync(ValidJobId);

        // Assert
        Assert.Single(result.Failed);
        Assert.NotNull(result.Failed[0].FailureCategory);
    }

    [Fact]
    public async Task GetJobStatus_PassedItems_HaveNullFailureCategory()
    {
        // Arrange
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("pass-job");
        jobDetails.QueueId.Returns("ubuntu.2204.amd64");
        jobDetails.Creator.Returns("test@test.com");
        jobDetails.Source.Returns("pr/999");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("passing-test");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("helix-win-01");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 30, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("passing-test", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);

        // Act
        var result = await _svc.GetJobStatusAsync(ValidJobId);

        // Assert
        Assert.Single(result.Passed);
        Assert.Null(result.Passed[0].FailureCategory);
    }
}
