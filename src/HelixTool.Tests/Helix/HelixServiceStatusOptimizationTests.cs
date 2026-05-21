using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class HelixServiceStatusOptimizationTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _sut;

    public HelixServiceStatusOptimizationTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _sut = new HelixService(_mockApi, new HttpClient());
    }

    [Fact]
    public async Task GetJobStatusAsync_AllPassingItems_DoesNotFetchDetails()
    {
        ArrangeJobDetails();
        var pass1 = CreateSummary("workitem-pass-1", exitCode: 0);
        var pass2 = CreateSummary("workitem-pass-2", exitCode: 0);
        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { pass1, pass2 });

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        Assert.Equal(2, result.TotalCount);
        Assert.Empty(result.Failed);
        Assert.Equal(["workitem-pass-1", "workitem-pass-2"], result.Passed.Select(item => item.Name).OrderBy(name => name));
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync(Arg.Any<string>(), ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobStatusAsync_MixedSummaryExitCodes_FetchesDetailsOnlyForFailedAndUnknown()
    {
        ArrangeJobDetails();
        var pass = CreateSummary("workitem-pass", exitCode: 0);
        var fail = CreateSummary("workitem-fail", exitCode: 17);
        var unknown = CreateSummary("workitem-unknown", exitCode: null);
        var failDetails = CreateDetails(17, state: "Failed", machineName: "helix-linux-01");
        var unknownDetails = CreateDetails(0, state: "Finished", machineName: "helix-win-02");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { pass, fail, unknown });

        _mockApi.GetWorkItemDetailsAsync("workitem-fail", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(failDetails);
        _mockApi.GetWorkItemDetailsAsync("workitem-unknown", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(unknownDetails);

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        Assert.Equal(2, result.Passed.Count);
        Assert.Single(result.Failed);
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync("workitem-pass", ValidJobId, Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetWorkItemDetailsAsync("workitem-fail", ValidJobId, Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetWorkItemDetailsAsync("workitem-unknown", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobStatusAsync_AllNullSummaryExitCodes_FetchesDetailsForEveryItem()
    {
        ArrangeJobDetails();
        var item1 = CreateSummary("workitem-1", exitCode: null);
        var item2 = CreateSummary("workitem-2", exitCode: null);
        var item3 = CreateSummary("workitem-3", exitCode: null);
        var item1Details = CreateDetails(0, state: "Finished", machineName: "helix-win-01");
        var item2Details = CreateDetails(1, state: "Failed", machineName: "helix-linux-02");
        var item3Details = CreateDetails(2, state: "Failed", machineName: "helix-linux-03");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { item1, item2, item3 });

        _mockApi.GetWorkItemDetailsAsync("workitem-1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(item1Details);
        _mockApi.GetWorkItemDetailsAsync("workitem-2", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(item2Details);
        _mockApi.GetWorkItemDetailsAsync("workitem-3", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(item3Details);

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        Assert.Single(result.Passed);
        Assert.Equal(2, result.Failed.Count);
        await _mockApi.Received(1).GetWorkItemDetailsAsync("workitem-1", ValidJobId, Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetWorkItemDetailsAsync("workitem-2", ValidJobId, Arg.Any<CancellationToken>());
        await _mockApi.Received(1).GetWorkItemDetailsAsync("workitem-3", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobStatusAsync_PassedSummaryItems_HaveNullStateMachineNameAndDuration()
    {
        ArrangeJobDetails();
        var pass = CreateSummary("workitem-pass", exitCode: 0);
        var fail = CreateSummary("workitem-fail", exitCode: 9);
        var failDetails = CreateDetails(9, state: "Failed", machineName: "helix-linux-04");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { pass, fail });

        _mockApi.GetWorkItemDetailsAsync("workitem-fail", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(failDetails);

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        var passedItem = Assert.Single(result.Passed);
        Assert.Equal("workitem-pass", passedItem.Name);
        Assert.Equal(0, passedItem.ExitCode);
        Assert.Null(passedItem.State);
        Assert.Null(passedItem.MachineName);
        Assert.Null(passedItem.Duration);
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync("workitem-pass", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobStatusAsync_SummaryExitCodeZero_RemainsPassedWithoutConsultingErrorStateDetails()
    {
        ArrangeJobDetails();
        var pass = CreateSummary("workitem-pass", exitCode: 0);
        var fail = CreateSummary("workitem-fail", exitCode: 1);
        var passDetails = CreateDetails(0, state: "Error", machineName: "helix-error");
        var failDetails = CreateDetails(1, state: "Error", machineName: "helix-linux-05");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { pass, fail });

        _mockApi.GetWorkItemDetailsAsync("workitem-pass", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(passDetails);
        _mockApi.GetWorkItemDetailsAsync("workitem-fail", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(failDetails);

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        Assert.Contains(result.Passed, item => item.Name == "workitem-pass");
        Assert.DoesNotContain(result.Failed, item => item.Name == "workitem-pass");
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync("workitem-pass", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobStatusAsync_EmptyWorkItemList_DoesNotFetchDetailsAndReturnsEmptySummary()
    {
        ArrangeJobDetails();
        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary>());

        var result = await _sut.GetJobStatusAsync(ValidJobId);

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(result.Failed);
        Assert.Empty(result.Passed);
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync(Arg.Any<string>(), ValidJobId, Arg.Any<CancellationToken>());
    }

    private void ArrangeJobDetails()
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/55");
        jobDetails.Created.Returns("2026-05-21T13:11:00Z");
        jobDetails.Finished.Returns("2026-05-21T13:41:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);
    }

    private static IWorkItemSummary CreateSummary(string name, int? exitCode)
    {
        var summary = Substitute.For<IWorkItemSummary>();
        summary.Name.Returns(name);
        summary.ExitCode.Returns(exitCode);
        summary.ConsoleOutputUri.Returns($"https://helix.dot.net/api/2019-06-17/jobs/{ValidJobId}/workitems/{name}/console");
        return summary;
    }

    private static IWorkItemDetails CreateDetails(int? exitCode, string state, string machineName)
    {
        var details = Substitute.For<IWorkItemDetails>();
        details.ExitCode.Returns(exitCode);
        details.State.Returns(state);
        details.MachineName.Returns(machineName);
        details.Started.Returns(new DateTimeOffset(2026, 5, 21, 13, 11, 0, TimeSpan.Zero));
        details.Finished.Returns(new DateTimeOffset(2026, 5, 21, 13, 14, 0, TimeSpan.Zero));
        return details;
    }
}
