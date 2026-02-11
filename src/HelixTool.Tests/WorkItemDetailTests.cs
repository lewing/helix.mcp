using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class WorkItemDetailTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItemName = "System.Runtime.Tests";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public WorkItemDetailTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    [Fact]
    public async Task GetWorkItemDetail_ReturnsCombinedInfo()
    {
        // Arrange: work item details
        var details = Substitute.For<IWorkItemDetails>();
        details.ExitCode.Returns(1);
        details.State.Returns("Finished");
        details.MachineName.Returns("helix-win-01");
        details.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        details.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 5, 30, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(details);

        // Arrange: work item files
        var file1 = Substitute.For<IWorkItemFile>();
        file1.Name.Returns("console.log");
        file1.Link.Returns("https://storage.blob.core.windows.net/console.log");
        var file2 = Substitute.For<IWorkItemFile>();
        file2.Name.Returns("results.trx");
        file2.Link.Returns("https://storage.blob.core.windows.net/results.trx");

        _mockApi.ListWorkItemFilesAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file1, file2 });

        // Act
        var result = await _svc.GetWorkItemDetailAsync(ValidJobId, WorkItemName);

        // Assert
        Assert.Equal(WorkItemName, result.Name);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("Finished", result.State);
        Assert.Equal("helix-win-01", result.MachineName);
        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30), result.Duration);
        Assert.Contains(ValidJobId, result.ConsoleLogUrl);
        Assert.Contains(WorkItemName, result.ConsoleLogUrl);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public async Task GetWorkItemDetail_ClassifiesFiles()
    {
        // Arrange: details
        var details = Substitute.For<IWorkItemDetails>();
        details.ExitCode.Returns(0);
        details.State.Returns("Finished");
        details.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        details.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 1, 0, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(details);

        // Arrange: mixed file types
        var binlog = Substitute.For<IWorkItemFile>();
        binlog.Name.Returns("build.binlog");
        binlog.Link.Returns("https://example.com/build.binlog");

        var trx = Substitute.For<IWorkItemFile>();
        trx.Name.Returns("TestResults.trx");
        trx.Link.Returns("https://example.com/TestResults.trx");

        var txt = Substitute.For<IWorkItemFile>();
        txt.Name.Returns("output.txt");
        txt.Link.Returns("https://example.com/output.txt");

        _mockApi.ListWorkItemFilesAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { binlog, trx, txt });

        // Act
        var result = await _svc.GetWorkItemDetailAsync(ValidJobId, WorkItemName);

        // Assert
        var binlogEntry = result.Files.Single(f => f.Name == "build.binlog");
        Assert.True(binlogEntry.IsBinlog);
        Assert.False(binlogEntry.IsTestResults);

        var trxEntry = result.Files.Single(f => f.Name == "TestResults.trx");
        Assert.False(trxEntry.IsBinlog);
        Assert.True(trxEntry.IsTestResults);

        var txtEntry = result.Files.Single(f => f.Name == "output.txt");
        Assert.False(txtEntry.IsBinlog);
        Assert.False(txtEntry.IsTestResults);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWorkItemDetail_ThrowsOnNullJobId(string? jobId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetWorkItemDetailAsync(jobId!, WorkItemName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetWorkItemDetail_ThrowsOnNullWorkItem(string? workItem)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetWorkItemDetailAsync(ValidJobId, workItem!));
    }

    [Fact]
    public async Task GetWorkItemDetail_CalculatesDuration()
    {
        // Arrange: specific start and finish times
        var started = new DateTimeOffset(2025, 7, 18, 14, 0, 0, TimeSpan.Zero);
        var finished = new DateTimeOffset(2025, 7, 18, 14, 12, 45, TimeSpan.Zero);
        var expectedDuration = finished - started; // 12 min 45 sec

        var details = Substitute.For<IWorkItemDetails>();
        details.ExitCode.Returns(0);
        details.State.Returns("Finished");
        details.MachineName.Returns("helix-linux-02");
        details.Started.Returns(started);
        details.Finished.Returns(finished);

        _mockApi.GetWorkItemDetailsAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(details);

        _mockApi.ListWorkItemFilesAsync(WorkItemName, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile>());

        // Act
        var result = await _svc.GetWorkItemDetailAsync(ValidJobId, WorkItemName);

        // Assert
        Assert.NotNull(result.Duration);
        Assert.Equal(expectedDuration, result.Duration);
        Assert.Equal(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(45), result.Duration);
    }
}
