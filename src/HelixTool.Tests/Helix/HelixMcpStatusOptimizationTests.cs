using System.Text.Json;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class HelixMcpStatusOptimizationTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixMcpTools _tools;

    public HelixMcpStatusOptimizationTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        var service = new HelixService(_mockApi, new HttpClient());
        _tools = new HelixMcpTools(service, Substitute.For<IHelixTokenAccessor>());
    }

    [Fact]
    public async Task Status_FilterAll_ReturnsPassedExitCodeFromSummaryPath()
    {
        ArrangeStatusJob();

        var result = await _tools.Status(ValidJobId, filter: "all");

        var passed = Assert.IsType<List<StatusWorkItem>>(result.Passed);
        var passedItem = Assert.Single(passed);
        Assert.Equal("workitem-ok", passedItem.Name);
        Assert.Equal(0, passedItem.ExitCode);
        Assert.Null(passedItem.State);
        Assert.Null(passedItem.MachineName);
        Assert.Null(passedItem.Duration);
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync("workitem-ok", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Status_FilterFailed_ReturnsOnlyFailedItemsWhenPassedItemsUseSummaryPath()
    {
        ArrangeStatusJob();

        var result = await _tools.Status(ValidJobId, filter: "failed");

        Assert.Null(result.Passed);
        var failed = Assert.IsType<List<StatusWorkItem>>(result.Failed);
        var failedItem = Assert.Single(failed);
        Assert.Equal("workitem-bad", failedItem.Name);
        Assert.Equal(5, failedItem.ExitCode);
        Assert.Equal("Failed", failedItem.State);
        await _mockApi.DidNotReceive().GetWorkItemDetailsAsync("workitem-ok", ValidJobId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Status_StatusWorkItemJsonSchema_RemainsUnchanged()
    {
        ArrangeStatusJob();

        var result = await _tools.Status(ValidJobId, filter: "all");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));

        var expectedProperties = new[]
        {
            "consoleLogUrl",
            "duration",
            "exitCode",
            "failureCategory",
            "machineName",
            "name",
            "state"
        };

        var passedProperties = doc.RootElement.GetProperty("passed")[0]
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();
        var failedProperties = doc.RootElement.GetProperty("failed")[0]
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(expectedProperties, passedProperties);
        Assert.Equal(expectedProperties, failedProperties);
    }

    private void ArrangeStatusJob()
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

        var passedSummary = CreateSummary("workitem-ok", exitCode: 0);
        var failedSummary = CreateSummary("workitem-bad", exitCode: 5);
        var failedDetails = CreateDetails(5, state: "Failed", machineName: "helix-linux-06");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { passedSummary, failedSummary });

        _mockApi.GetWorkItemDetailsAsync("workitem-bad", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(failedDetails);
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
        details.Finished.Returns(new DateTimeOffset(2026, 5, 21, 13, 16, 0, TimeSpan.Zero));
        return details;
    }
}
