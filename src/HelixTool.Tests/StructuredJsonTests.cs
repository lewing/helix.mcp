using System.Text.Json;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class StructuredJsonTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;
    private readonly HelixMcpTools _tools;

    public StructuredJsonTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
        _tools = new HelixMcpTools(_svc);
    }

    // --- US-30: Files grouped JSON ---

    [Fact]
    public async Task Files_ReturnsGroupedJson()
    {
        var binlog = Substitute.For<IWorkItemFile>();
        binlog.Name.Returns("msbuild.binlog");
        binlog.Link.Returns("https://helix.dot.net/files/msbuild.binlog");

        var trx = Substitute.For<IWorkItemFile>();
        trx.Name.Returns("results.trx");
        trx.Link.Returns("https://helix.dot.net/files/results.trx");

        var txt = Substitute.For<IWorkItemFile>();
        txt.Name.Returns("console.txt");
        txt.Link.Returns("https://helix.dot.net/files/console.txt");

        _mockApi.ListWorkItemFilesAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { binlog, trx, txt });

        var json = await _tools.Files(ValidJobId, "wi1");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // binlogs array contains only the .binlog file
        var binlogs = root.GetProperty("binlogs");
        Assert.Equal(1, binlogs.GetArrayLength());
        Assert.Equal("msbuild.binlog", binlogs[0].GetProperty("name").GetString());

        // testResults array contains only the .trx file
        var testResults = root.GetProperty("testResults");
        Assert.Equal(1, testResults.GetArrayLength());
        Assert.Equal("results.trx", testResults[0].GetProperty("name").GetString());

        // other array contains only the .txt file
        var other = root.GetProperty("other");
        Assert.Equal(1, other.GetArrayLength());
        Assert.Equal("console.txt", other[0].GetProperty("name").GetString());
    }

    // --- US-30: Status includes helixUrl ---

    [Fact]
    public async Task Status_IncludesHelixUrl()
    {
        ArrangeJob();

        var json = await _tools.Status(ValidJobId);
        var doc = JsonDocument.Parse(json);
        var helixUrl = doc.RootElement.GetProperty("job").GetProperty("helixUrl").GetString();

        Assert.Equal($"https://helix.dot.net/api/jobs/{ValidJobId}/details", helixUrl);
    }

    // --- US-30: Status includes resolved jobId ---

    [Fact]
    public async Task Status_IncludesJobId()
    {
        ArrangeJob();

        var json = await _tools.Status(ValidJobId);
        var doc = JsonDocument.Parse(json);
        var jobId = doc.RootElement.GetProperty("job").GetProperty("jobId").GetString();

        Assert.Equal(ValidJobId, jobId);
    }

    // --- Helpers ---

    private void ArrangeJob()
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
        detail.ExitCode.Returns(0);
        detail.State.Returns("Finished");
        detail.MachineName.Returns("machine1");
        detail.Started.Returns(new DateTimeOffset(2025, 7, 18, 10, 0, 0, TimeSpan.Zero));
        detail.Finished.Returns(new DateTimeOffset(2025, 7, 18, 10, 2, 0, TimeSpan.Zero));

        _mockApi.GetWorkItemDetailsAsync("wi1", ValidJobId, Arg.Any<CancellationToken>())
            .Returns(detail);
    }
}
