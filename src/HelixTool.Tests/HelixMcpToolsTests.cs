using HelixTool.Core;
using ModelContextProtocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class HelixMcpToolsTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly HelixMcpTools _tools;

    public HelixMcpToolsTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
        _tools = new HelixMcpTools(_svc);
    }

    // --- Status tests ---

    [Fact]
    public async Task Status_ReturnsValidJsonWithExpectedStructure()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("test-job", result.Job.Name);
        Assert.Equal("windows.10.amd64", result.Job.QueueId);
        Assert.Equal(2, result.TotalWorkItems);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.PassedCount);
    }

    [Fact]
    public async Task Status_FailedItems_IncludeExitCodeStateAndMachine()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId);

        Assert.NotNull(result.Failed);
        Assert.Single(result.Failed);
        var item = result.Failed[0];
        Assert.Equal("workitem-bad", item.Name);
        Assert.Equal(1, item.ExitCode);
        Assert.Equal("Finished", item.State);
        Assert.Equal("helix-linux-03", item.MachineName);
    }

    [Fact]
    public async Task Status_FilterFailed_PassedIsNull()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId, filter: "failed");

        Assert.Null(result.Passed);
    }

    [Fact]
    public async Task Status_FilterAll_PassedIncludesItems()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId, filter: "all");

        Assert.NotNull(result.Passed);
        Assert.Single(result.Passed);
        Assert.Equal("workitem-ok", result.Passed[0].Name);
    }

    [Fact]
    public async Task Status_DefaultFilter_ShowsOnlyFailed()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId);

        Assert.Null(result.Passed);
        Assert.NotNull(result.Failed);
        Assert.True(result.Failed.Count > 0);
    }

    [Fact]
    public async Task Status_FilterPassed_FailedIsNull()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId, filter: "passed");

        Assert.Null(result.Failed);
        Assert.NotNull(result.Passed);
        Assert.True(result.Passed.Count > 0);
    }

    [Fact]
    public async Task Status_FilterPassed_IncludesPassedItems()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId, filter: "passed");

        Assert.NotNull(result.Passed);
        Assert.Single(result.Passed);
        var item = result.Passed[0];
        Assert.Equal("workitem-ok", item.Name);
        Assert.Equal(0, item.ExitCode);
        Assert.Equal("Finished", item.State);
        Assert.Equal("helix-win-01", item.MachineName);
    }

    [Fact]
    public async Task Status_FilterCaseInsensitive()
    {
        ArrangeJobWithWorkItems();

        var result = await _tools.Status(ValidJobId, filter: "ALL");

        Assert.NotNull(result.Failed);
        Assert.True(result.Failed.Count > 0);
        Assert.NotNull(result.Passed);
        Assert.True(result.Passed.Count > 0);
    }

    [Fact]
    public async Task Status_InvalidFilter_ThrowsArgumentException()
    {
        ArrangeJobWithWorkItems();

        await Assert.ThrowsAsync<ArgumentException>(() => _tools.Status(ValidJobId, filter: "invalid"));
    }

    // --- FormatDuration tested through Status output ---

    [Fact]
    public async Task Status_DurationSeconds_FormatsAsSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 0, 45, TimeSpan.Zero));

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("45s", result.Failed![0].Duration);
    }

    [Fact]
    public async Task Status_DurationMinutesAndSeconds_FormatsAsMinutesSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 2, 34, TimeSpan.Zero));

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("2m 34s", result.Failed![0].Duration);
    }

    [Fact]
    public async Task Status_DurationExactMinutes_FormatsWithoutSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 5, 0, TimeSpan.Zero));

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("5m", result.Failed![0].Duration);
    }

    [Fact]
    public async Task Status_DurationHoursAndMinutes_FormatsAsHoursMinutes()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 11, 15, 0, TimeSpan.Zero));

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("1h 15m", result.Failed![0].Duration);
    }

    [Fact]
    public async Task Status_DurationExactHours_FormatsWithoutMinutes()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 12, 0, 0, TimeSpan.Zero));

        var result = await _tools.Status(ValidJobId);

        Assert.Equal("2h", result.Failed![0].Duration);
    }

    [Fact]
    public async Task Status_NoDuration_ReturnsNull()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1, started: null, finished: null);

        var result = await _tools.Status(ValidJobId);

        Assert.Null(result.Failed![0].Duration);
    }

    // --- Files tests ---

    [Fact]
    public async Task Files_ReturnsValidJsonWithFileTags()
    {
        var f1 = Substitute.For<IWorkItemFile>();
        f1.Name.Returns("build.binlog");
        f1.Link.Returns("https://helix.dot.net/files/build.binlog");
        var f2 = Substitute.For<IWorkItemFile>();
        f2.Name.Returns("results.trx");
        f2.Link.Returns("https://helix.dot.net/files/results.trx");
        var f3 = Substitute.For<IWorkItemFile>();
        f3.Name.Returns("output.txt");
        f3.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f1, f2, f3 });

        var result = await _tools.Files(ValidJobId, "wi1");

        Assert.Single(result.Binlogs);
        Assert.Equal("build.binlog", result.Binlogs[0].Name);

        Assert.Single(result.TestResults);
        Assert.Equal("results.trx", result.TestResults[0].Name);

        Assert.Single(result.Other);
        Assert.Equal("output.txt", result.Other[0].Name);
    }

    [Fact]
    public async Task Files_IncludesNameAndUri()
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns("artifact.zip");
        f.Link.Returns("https://helix.dot.net/files/artifact.zip");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f });

        var result = await _tools.Files(ValidJobId, "wi1");
        var item = result.Other[0];

        Assert.Equal("artifact.zip", item.Name);
        Assert.Equal("https://helix.dot.net/files/artifact.zip", item.Uri);
    }

    // --- FindBinlogs tests ---

    [Fact]
    public async Task FindBinlogs_ReturnsValidJsonWithScanResults()
    {
        var wi1 = Substitute.For<IWorkItemSummary>();
        wi1.Name.Returns("wi-with-binlog");
        var wi2 = Substitute.For<IWorkItemSummary>();
        wi2.Name.Returns("wi-no-binlog");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi1, wi2 });

        var binlogFile = Substitute.For<IWorkItemFile>();
        binlogFile.Name.Returns("msbuild.binlog");
        binlogFile.Link.Returns("https://helix.dot.net/files/msbuild.binlog");

        var txtFile = Substitute.For<IWorkItemFile>();
        txtFile.Name.Returns("output.txt");
        txtFile.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi-with-binlog", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { binlogFile, txtFile });
        _mockApi.ListWorkItemFilesAsync("wi-no-binlog", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { txtFile });

        var result = await _tools.FindBinlogs(ValidJobId);

        Assert.Equal(30, result.ScannedItems);
        Assert.Equal(1, result.Found);

        Assert.Single(result.Results);
        Assert.Equal("wi-with-binlog", result.Results[0].WorkItem);

        Assert.Single(result.Results[0].Files);
        Assert.Equal("msbuild.binlog", result.Results[0].Files[0].Name);
    }

    [Fact]
    public async Task FindBinlogs_NoBinlogs_ReturnsEmptyResults()
    {
        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi-plain");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var txtFile = Substitute.For<IWorkItemFile>();
        txtFile.Name.Returns("output.txt");
        txtFile.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi-plain", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { txtFile });

        var result = await _tools.FindBinlogs(ValidJobId);

        Assert.Equal(0, result.Found);
        Assert.Empty(result.Results);
    }

    // --- Download tests ---

    [Fact]
    public async Task Download_NoMatchingFiles_ThrowsMcpException()
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns("output.txt");
        f.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f });

        var ex = await Assert.ThrowsAsync<McpException>(() => _tools.Download(ValidJobId, "wi1", "*.binlog"));
        Assert.Contains("*.binlog", ex.Message);
    }

    [Fact]
    public async Task Download_NoFiles_ThrowsMcpException()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile>());

        await Assert.ThrowsAsync<McpException>(() => _tools.Download(ValidJobId, "wi1", "*"));
    }

    // --- Constructor tests ---

    [Fact]
    public void Constructor_AcceptsHelixService()
    {
        var tools = new HelixMcpTools(_svc);
        Assert.NotNull(tools);
    }

    // --- FindFiles tests ---

    [Fact]
    public async Task FindFiles_ReturnsValidJsonWithScanResults()
    {
        var wi1 = Substitute.For<IWorkItemSummary>();
        wi1.Name.Returns("wi-with-trx");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi1 });

        var trxFile = Substitute.For<IWorkItemFile>();
        trxFile.Name.Returns("results.trx");
        trxFile.Link.Returns("https://helix.dot.net/files/results.trx");

        var txtFile = Substitute.For<IWorkItemFile>();
        txtFile.Name.Returns("output.txt");
        txtFile.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi-with-trx", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { trxFile, txtFile });

        var result = await _tools.FindFiles(ValidJobId, "*.trx");

        Assert.Equal("*.trx", result.Pattern);
        Assert.Equal(30, result.ScannedItems);
        Assert.Equal(1, result.Found);

        Assert.Single(result.Results);
        Assert.Equal("wi-with-trx", result.Results[0].WorkItem);

        Assert.Single(result.Results[0].Files);
        Assert.Equal("results.trx", result.Results[0].Files[0].Name);
        Assert.Equal("https://helix.dot.net/files/results.trx", result.Results[0].Files[0].Uri);
    }

    [Fact]
    public async Task FindFiles_WildcardPattern_ReturnsAllFiles()
    {
        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi-all");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var f1 = Substitute.For<IWorkItemFile>();
        f1.Name.Returns("build.binlog");
        f1.Link.Returns("https://helix.dot.net/files/build.binlog");
        var f2 = Substitute.For<IWorkItemFile>();
        f2.Name.Returns("results.trx");
        f2.Link.Returns("https://helix.dot.net/files/results.trx");
        var f3 = Substitute.For<IWorkItemFile>();
        f3.Name.Returns("output.txt");
        f3.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi-all", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f1, f2, f3 });

        var result = await _tools.FindFiles(ValidJobId, "*");

        Assert.Equal("*", result.Pattern);
        Assert.Equal(1, result.Found);
        Assert.Equal(3, result.Results[0].Files.Count);
    }

    [Fact]
    public async Task FindBinlogs_DelegatesToFindFiles()
    {
        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi-binlog");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var binlogFile = Substitute.For<IWorkItemFile>();
        binlogFile.Name.Returns("msbuild.binlog");
        binlogFile.Link.Returns("https://helix.dot.net/files/msbuild.binlog");

        var txtFile = Substitute.For<IWorkItemFile>();
        txtFile.Name.Returns("output.txt");
        txtFile.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi-binlog", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { binlogFile, txtFile });

        // FindBinlogs delegates to FindFiles with *.binlog pattern
        var binlogResult = await _tools.FindBinlogs(ValidJobId);
        var findFilesResult = await _tools.FindFiles(ValidJobId, "*.binlog");

        // Both should have same structure: pattern, scannedItems, found, results with files
        Assert.Equal("*.binlog", binlogResult.Pattern);
        Assert.Equal("*.binlog", findFilesResult.Pattern);
        Assert.Equal(binlogResult.Found, findFilesResult.Found);
        Assert.Equal(binlogResult.Results.Count, findFilesResult.Results.Count);
    }

    // --- BatchStatus tests ---

    [Fact]
    public async Task BatchStatus_AcceptsStringArray()
    {
        // Arrange two jobs
        ArrangeJobForBatch("aaaaaaaa-1111-2222-3333-444444444444", "job-one");
        ArrangeJobForBatch("bbbbbbbb-5555-6666-7777-888888888888", "job-two");

        // BatchStatus now accepts string[] directly
        var result = await _tools.BatchStatus(new[] { "aaaaaaaa-1111-2222-3333-444444444444", "bbbbbbbb-5555-6666-7777-888888888888" });

        Assert.Equal(2, result.JobCount);
        Assert.Equal(2, result.Jobs.Count);
    }

    private void ArrangeJobForBatch(string jobId, string jobName)
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns(jobName);
        jobDetails.QueueId.Returns("queue1");
        jobDetails.Creator.Returns("user");
        jobDetails.Source.Returns("src");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");

        _mockApi.ListWorkItemsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("machine1");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 1, 0, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("wi1", jobId, Arg.Any<CancellationToken>())
            .Returns(detail);
    }

    // --- Helpers ---

    private void ArrangeJobWithWorkItems()
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("windows.10.amd64");
        jobDetails.Creator.Returns("testuser@microsoft.com");
        jobDetails.Source.Returns("pr/12345");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi1 = Substitute.For<IWorkItemSummary>();
        wi1.Name.Returns("workitem-ok");
        var wi2 = Substitute.For<IWorkItemSummary>();
        wi2.Name.Returns("workitem-bad");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi1, wi2 });

        var detail1 = Substitute.For<IWorkItemDetails>();
        detail1.ExitCode.Returns(0);
        detail1.State.Returns("Finished");
        detail1.MachineName.Returns("helix-win-01");
        detail1.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail1.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 34, TimeSpan.Zero));

        var detail2 = Substitute.For<IWorkItemDetails>();
        detail2.ExitCode.Returns(1);
        detail2.State.Returns("Finished");
        detail2.MachineName.Returns("helix-linux-03");
        detail2.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail2.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 45, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("workitem-ok", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail1);
        _mockApi.GetWorkItemDetailsAsync("workitem-bad", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail2);
    }

    private void ArrangeJobWithSingleWorkItem(int exitCode, DateTimeOffset? started, DateTimeOffset? finished)
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("queue1");
        jobDetails.Creator.Returns("user");
        jobDetails.Source.Returns("src");
        jobDetails.Created.Returns("2025-07-18T10:00:00Z");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");

        _mockApi.GetJobDetailsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");

        _mockApi.ListWorkItemsAsync(ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var detail = Substitute.For<IWorkItemDetails>();
        detail.ExitCode.Returns(exitCode);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("machine1");
        detail.Started.Returns(started);
        detail.Finished.Returns(finished);

        _mockApi.GetWorkItemDetailsAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);
    }
}
