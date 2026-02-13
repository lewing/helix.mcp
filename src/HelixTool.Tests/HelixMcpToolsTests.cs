using System.Text.Json;
using HelixTool.Core;
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

        var json = await _tools.Status(ValidJobId);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("test-job", root.GetProperty("job").GetProperty("name").GetString());
        Assert.Equal("windows.10.amd64", root.GetProperty("job").GetProperty("queueId").GetString());
        Assert.Equal(2, root.GetProperty("totalWorkItems").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("passedCount").GetInt32());
    }

    [Fact]
    public async Task Status_FailedItems_IncludeExitCodeStateAndMachine()
    {
        ArrangeJobWithWorkItems();

        var json = await _tools.Status(ValidJobId);
        var doc = JsonDocument.Parse(json);
        var failed = doc.RootElement.GetProperty("failed");

        Assert.Equal(1, failed.GetArrayLength());
        var item = failed[0];
        Assert.Equal("workitem-bad", item.GetProperty("name").GetString());
        Assert.Equal(1, item.GetProperty("exitCode").GetInt32());
        Assert.Equal("Finished", item.GetProperty("state").GetString());
        Assert.Equal("helix-linux-03", item.GetProperty("machineName").GetString());
    }

    [Fact]
    public async Task Status_AllFalse_PassedIsNull()
    {
        ArrangeJobWithWorkItems();

        var json = await _tools.Status(ValidJobId, includePassed: false);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("passed").ValueKind);
    }

    [Fact]
    public async Task Status_AllTrue_PassedIncludesItems()
    {
        ArrangeJobWithWorkItems();

        var json = await _tools.Status(ValidJobId, includePassed: true);
        var doc = JsonDocument.Parse(json);
        var passed = doc.RootElement.GetProperty("passed");

        Assert.Equal(1, passed.GetArrayLength());
        Assert.Equal("workitem-ok", passed[0].GetProperty("name").GetString());
    }

    // --- FormatDuration tested through Status output ---

    [Fact]
    public async Task Status_DurationSeconds_FormatsAsSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 0, 45, TimeSpan.Zero));

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration").GetString();

        Assert.Equal("45s", duration);
    }

    [Fact]
    public async Task Status_DurationMinutesAndSeconds_FormatsAsMinutesSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 2, 34, TimeSpan.Zero));

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration").GetString();

        Assert.Equal("2m 34s", duration);
    }

    [Fact]
    public async Task Status_DurationExactMinutes_FormatsWithoutSeconds()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 10, 5, 0, TimeSpan.Zero));

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration").GetString();

        Assert.Equal("5m", duration);
    }

    [Fact]
    public async Task Status_DurationHoursAndMinutes_FormatsAsHoursMinutes()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 11, 15, 0, TimeSpan.Zero));

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration").GetString();

        Assert.Equal("1h 15m", duration);
    }

    [Fact]
    public async Task Status_DurationExactHours_FormatsWithoutMinutes()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1,
            started: new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero),
            finished: new DateTimeOffset(2025, 7, 18, 12, 0, 0, TimeSpan.Zero));

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration").GetString();

        Assert.Equal("2h", duration);
    }

    [Fact]
    public async Task Status_NoDuration_ReturnsNull()
    {
        ArrangeJobWithSingleWorkItem(exitCode: 1, started: null, finished: null);

        var json = await _tools.Status(ValidJobId);
        var duration = JsonDocument.Parse(json).RootElement
            .GetProperty("failed")[0].GetProperty("duration");

        Assert.Equal(JsonValueKind.Null, duration.ValueKind);
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

        var json = await _tools.Files(ValidJobId, "wi1");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // binlog file in binlogs group
        var binlogs = root.GetProperty("binlogs");
        Assert.Equal(1, binlogs.GetArrayLength());
        Assert.Equal("build.binlog", binlogs[0].GetProperty("name").GetString());

        // trx file in testResults group
        var testResults = root.GetProperty("testResults");
        Assert.Equal(1, testResults.GetArrayLength());
        Assert.Equal("results.trx", testResults[0].GetProperty("name").GetString());

        // plain file in other group
        var other = root.GetProperty("other");
        Assert.Equal(1, other.GetArrayLength());
        Assert.Equal("output.txt", other[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Files_IncludesNameAndUri()
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns("artifact.zip");
        f.Link.Returns("https://helix.dot.net/files/artifact.zip");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f });

        var json = await _tools.Files(ValidJobId, "wi1");
        var item = JsonDocument.Parse(json).RootElement.GetProperty("other")[0];

        Assert.Equal("artifact.zip", item.GetProperty("name").GetString());
        Assert.Equal("https://helix.dot.net/files/artifact.zip", item.GetProperty("uri").GetString());
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

        var json = await _tools.FindBinlogs(ValidJobId);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(30, root.GetProperty("scannedItems").GetInt32());
        Assert.Equal(1, root.GetProperty("found").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("wi-with-binlog", results[0].GetProperty("workItem").GetString());

        var files = results[0].GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("msbuild.binlog", files[0].GetProperty("name").GetString());
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

        var json = await _tools.FindBinlogs(ValidJobId);
        var doc = JsonDocument.Parse(json);

        Assert.Equal(0, doc.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    // --- Download tests ---

    [Fact]
    public async Task Download_NoMatchingFiles_ReturnsErrorJson()
    {
        var f = Substitute.For<IWorkItemFile>();
        f.Name.Returns("output.txt");
        f.Link.Returns("https://helix.dot.net/files/output.txt");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { f });

        var json = await _tools.Download(ValidJobId, "wi1", "*.binlog");
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out var errorProp));
        Assert.Contains("*.binlog", errorProp.GetString());
    }

    [Fact]
    public async Task Download_NoFiles_ReturnsErrorJson()
    {
        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile>());

        var json = await _tools.Download(ValidJobId, "wi1", "*");
        var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("error", out _));
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

        var json = await _tools.FindFiles(ValidJobId, "*.trx");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("*.trx", root.GetProperty("pattern").GetString());
        Assert.Equal(30, root.GetProperty("scannedItems").GetInt32());
        Assert.Equal(1, root.GetProperty("found").GetInt32());

        var results = root.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("wi-with-trx", results[0].GetProperty("workItem").GetString());

        var files = results[0].GetProperty("files");
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("results.trx", files[0].GetProperty("name").GetString());
        Assert.Equal("https://helix.dot.net/files/results.trx", files[0].GetProperty("uri").GetString());
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

        var json = await _tools.FindFiles(ValidJobId, "*");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("*", root.GetProperty("pattern").GetString());
        Assert.Equal(1, root.GetProperty("found").GetInt32());

        var files = root.GetProperty("results")[0].GetProperty("files");
        Assert.Equal(3, files.GetArrayLength());
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
        var binlogJson = await _tools.FindBinlogs(ValidJobId);
        var findFilesJson = await _tools.FindFiles(ValidJobId, "*.binlog");

        var binlogDoc = JsonDocument.Parse(binlogJson);
        var findFilesDoc = JsonDocument.Parse(findFilesJson);

        // Both should have same structure: pattern, scannedItems, found, results with files
        Assert.Equal("*.binlog", binlogDoc.RootElement.GetProperty("pattern").GetString());
        Assert.Equal("*.binlog", findFilesDoc.RootElement.GetProperty("pattern").GetString());
        Assert.Equal(
            binlogDoc.RootElement.GetProperty("found").GetInt32(),
            findFilesDoc.RootElement.GetProperty("found").GetInt32());
        Assert.Equal(
            binlogDoc.RootElement.GetProperty("results").GetArrayLength(),
            findFilesDoc.RootElement.GetProperty("results").GetArrayLength());
    }

    // --- BatchStatus tests ---

    [Fact]
    public async Task BatchStatus_AcceptsStringArray()
    {
        // Arrange two jobs
        ArrangeJobForBatch("aaaaaaaa-1111-2222-3333-444444444444", "job-one");
        ArrangeJobForBatch("bbbbbbbb-5555-6666-7777-888888888888", "job-two");

        // BatchStatus now accepts string[] directly
        var json = await _tools.BatchStatus(new[] { "aaaaaaaa-1111-2222-3333-444444444444", "bbbbbbbb-5555-6666-7777-888888888888" });
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(2, root.GetProperty("jobCount").GetInt32());
        Assert.Equal(2, root.GetProperty("jobs").GetArrayLength());
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
