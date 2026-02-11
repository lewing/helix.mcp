using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class ConsoleLogUrlTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public ConsoleLogUrlTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    // --- Test 1: ConsoleLogUrl is correctly constructed ---

    [Fact]
    public async Task GetJobStatusAsync_ConsoleLogUrl_IsCorrectlyConstructed()
    {
        // Arrange: mock job details
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/12345");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        // Arrange: mock work item list
        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("MyWorkItem");
        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        // Arrange: mock work item details
        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("helix-win-01");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 0, TimeSpan.Zero));
        _mockApi.GetWorkItemDetailsAsync("MyWorkItem", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);

        // Act
        var result = await _svc.GetJobStatusAsync(ValidJobId);

        // Assert
        var item = Assert.Single(result.Passed);
        Assert.Equal(
            $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems/MyWorkItem/console",
            item.ConsoleLogUrl);
    }

    // --- Test 2: ConsoleLogUrl uses resolved job ID (not the full URL) ---

    [Fact]
    public async Task GetJobStatusAsync_ConsoleLogUrl_UsesResolvedJobId_NotFullUrl()
    {
        // Arrange: pass a full Helix URL as jobId
        var helixUrl = $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}";

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/12345");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("SomeTest");
        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("helix-win-01");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 0, TimeSpan.Zero));
        _mockApi.GetWorkItemDetailsAsync("SomeTest", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);

        // Act: pass the full URL — HelixIdResolver should extract the GUID
        var result = await _svc.GetJobStatusAsync(helixUrl);

        // Assert: the ConsoleLogUrl should use the extracted GUID, not the full URL
        var item = Assert.Single(result.Passed);
        Assert.Equal(
            $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems/SomeTest/console",
            item.ConsoleLogUrl);
        Assert.DoesNotContain("https://helix.dot.net/api/2019-06-17/jobs/https://", item.ConsoleLogUrl);
    }

    // --- Test 3: ConsoleLogUrl handles work item names with special characters ---

    [Fact]
    public async Task GetJobStatusAsync_ConsoleLogUrl_HandlesWorkItemNamesWithSpecialCharacters()
    {
        // Arrange: work item name with dots and dashes
        const string workItemName = "dotnet-watch.Tests.dll.1";

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/12345");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns(workItemName);
        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("helix-win-01");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 0, TimeSpan.Zero));
        _mockApi.GetWorkItemDetailsAsync(workItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);

        // Act
        var result = await _svc.GetJobStatusAsync(ValidJobId);

        // Assert: the URL includes the exact work item name with dots and dashes
        var item = Assert.Single(result.Passed);
        Assert.Equal(
            $"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems/{workItemName}/console",
            item.ConsoleLogUrl);
        Assert.Contains("dotnet-watch.Tests.dll.1", item.ConsoleLogUrl);
    }
}
