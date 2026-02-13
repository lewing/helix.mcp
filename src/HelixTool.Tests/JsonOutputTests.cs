using System.Text.Json;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Tests for US-11: --json CLI output flag.
/// Verifies JSON serialization structure matches expected shape by replicating
/// the same serialization logic used in the CLI Commands class.
/// </summary>
public class JsonOutputTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public JsonOutputTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    [Fact]
    public async Task StatusJsonOutput_MatchesMcpStructure()
    {
        ArrangeJobWithWorkItems();

        var summary = await _svc.GetJobStatusAsync(ValidJobId);

        // Replicate the CLI status --json serialization from Program.cs
        var result = new
        {
            job = new { jobId = summary.JobId, summary.Name, summary.QueueId, summary.Creator, summary.Source, summary.Created, summary.Finished },
            totalWorkItems = summary.TotalCount,
            failedCount = summary.Failed.Count,
            passedCount = summary.Passed.Count,
            failed = summary.Failed.Select(f => new { f.Name, f.ExitCode, f.State, f.MachineName, duration = f.Duration?.ToString(), f.ConsoleLogUrl }),
            passed = (object?)null
        };
        var json = JsonSerializer.Serialize(result, s_jsonOptions);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify top-level fields exist
        Assert.True(root.TryGetProperty("job", out var job));
        Assert.Equal(ValidJobId, job.GetProperty("jobId").GetString());
        Assert.Equal("test-job", job.GetProperty("Name").GetString());
        Assert.Equal("windows.10.amd64", job.GetProperty("QueueId").GetString());

        Assert.Equal(2, root.GetProperty("totalWorkItems").GetInt32());
        Assert.Equal(1, root.GetProperty("failedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("passedCount").GetInt32());

        // Verify failed array
        var failed = root.GetProperty("failed");
        Assert.Equal(1, failed.GetArrayLength());
        Assert.Equal("workitem-bad", failed[0].GetProperty("Name").GetString());
        Assert.Equal(1, failed[0].GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task StatusJsonOutput_IncludesConsoleLogUrl()
    {
        ArrangeJobWithWorkItems();

        var summary = await _svc.GetJobStatusAsync(ValidJobId);

        var result = new
        {
            job = new { jobId = summary.JobId, summary.Name, summary.QueueId, summary.Creator, summary.Source, summary.Created, summary.Finished },
            totalWorkItems = summary.TotalCount,
            failedCount = summary.Failed.Count,
            passedCount = summary.Passed.Count,
            failed = summary.Failed.Select(f => new { f.Name, f.ExitCode, f.State, f.MachineName, duration = f.Duration?.ToString(), f.ConsoleLogUrl }),
            passed = (object?)null
        };
        var json = JsonSerializer.Serialize(result, s_jsonOptions);

        var doc = JsonDocument.Parse(json);
        var failed = doc.RootElement.GetProperty("failed");

        foreach (var item in failed.EnumerateArray())
        {
            Assert.True(item.TryGetProperty("ConsoleLogUrl", out var urlProp));
            var url = urlProp.GetString();
            Assert.NotNull(url);
            Assert.Contains(ValidJobId, url);
            Assert.StartsWith("https://helix.dot.net/api/", url);
        }
    }

    [Fact]
    public async Task FilesJsonOutput_HasGroupedStructure()
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

        var files = await _svc.GetWorkItemFilesAsync(ValidJobId, "wi1");

        // Replicate the CLI files --json serialization from Program.cs
        var result = new
        {
            binlogs = files.Where(f => HelixService.MatchesPattern(f.Name, "*.binlog")).Select(f => new { f.Name, f.Uri }),
            testResults = files.Where(f => HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri }),
            other = files.Where(f => !HelixService.MatchesPattern(f.Name, "*.binlog") && !HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri })
        };
        var json = JsonSerializer.Serialize(result, s_jsonOptions);

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Verify binlogs array
        var binlogs = root.GetProperty("binlogs");
        Assert.Equal(1, binlogs.GetArrayLength());
        Assert.Equal("build.binlog", binlogs[0].GetProperty("Name").GetString());
        Assert.True(binlogs[0].TryGetProperty("Uri", out _));

        // Verify testResults array
        var testResults = root.GetProperty("testResults");
        Assert.Equal(1, testResults.GetArrayLength());
        Assert.Equal("results.trx", testResults[0].GetProperty("Name").GetString());

        // Verify other array
        var other = root.GetProperty("other");
        Assert.Equal(1, other.GetArrayLength());
        Assert.Equal("output.txt", other[0].GetProperty("Name").GetString());
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
}
