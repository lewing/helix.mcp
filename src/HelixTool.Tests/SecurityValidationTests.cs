// Security validation tests for P1 threat-model fixes.
// Tests target expected behavior from Ripley's concurrent implementation:
//   E1 — URL scheme validation in DownloadFromUrlAsync
//   D1 — Batch size limit in GetBatchStatusAsync
//   MCP — hlx_batch_status array size enforcement
//
// If Ripley's code hasn't landed yet, some tests will fail to compile — expected.

using System.Text.Json;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// Tests for P1 security hardening: URL scheme validation, batch size limits.
/// Covers threat-model findings E1 (SSRF via DownloadFromUrlAsync) and D1 (unbounded batch size).
/// </summary>
public class SecurityValidationTests
{
    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public SecurityValidationTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    // ─────────────────────────────────────────────────────────────────
    // 1. URL Scheme Validation (DownloadFromUrlAsync) — Threat E1
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DownloadFromUrl_HttpsScheme_DoesNotThrowArgumentException()
    {
        // HTTPS should be accepted — may still fail with HttpRequestException (no real server)
        // but should NOT throw ArgumentException for scheme validation.
        var ex = await Record.ExceptionAsync(() =>
            _svc.DownloadFromUrlAsync("https://helix.dot.net/api/test/file.binlog"));

        // Acceptable: null (unlikely without network), HelixException, HttpRequestException
        // Not acceptable: ArgumentException (would mean scheme validation rejected HTTPS)
        Assert.IsNotType<ArgumentException>(ex);
    }

    [Fact]
    public async Task DownloadFromUrl_HttpScheme_DoesNotThrowArgumentException()
    {
        var ex = await Record.ExceptionAsync(() =>
            _svc.DownloadFromUrlAsync("http://example.com/file.txt"));

        Assert.IsNotType<ArgumentException>(ex);
    }

    [Fact]
    public async Task DownloadFromUrl_FileScheme_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.DownloadFromUrlAsync("file:///etc/passwd"));
    }

    [Fact]
    public async Task DownloadFromUrl_FtpScheme_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.DownloadFromUrlAsync("ftp://evil.com/file"));
    }

    [Fact]
    public async Task DownloadFromUrl_NoScheme_Throws()
    {
        // A relative path or schemeless string should be rejected.
        // May throw ArgumentException (if scheme validation catches it) or
        // UriFormatException (if Uri constructor rejects it first). Either is acceptable.
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => _svc.DownloadFromUrlAsync("just-a-path/file.txt"));

        Assert.True(
            ex is ArgumentException || ex is UriFormatException,
            $"Expected ArgumentException or UriFormatException, got {ex.GetType().Name}");
    }

    [Fact]
    public async Task DownloadFromUrl_Null_ThrowsArgumentException()
    {
        // Existing behavior — confirm null still throws
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFromUrlAsync(null!));
    }

    [Fact]
    public async Task DownloadFromUrl_Empty_ThrowsArgumentException()
    {
        // Existing behavior — confirm empty still throws
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFromUrlAsync(""));
    }

    [Theory]
    [InlineData("data:text/html,<h1>hello</h1>")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ssh://git@github.com/repo")]
    public async Task DownloadFromUrl_ExoticSchemes_ThrowArgumentException(string url)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.DownloadFromUrlAsync(url));
    }

    // ─────────────────────────────────────────────────────────────────
    // 2. Batch Size Limit (GetBatchStatusAsync) — Threat D1
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MaxBatchSize_IsExposedAsConst()
    {
        // Ripley is adding: internal const int MaxBatchSize = 50;
        Assert.Equal(50, HelixService.MaxBatchSize);
    }

    [Fact]
    public async Task GetBatchStatus_SingleJob_Accepted()
    {
        SetupMinimalJobMock("aaaaaaaa-1111-2222-3333-444444444444");

        // Should not throw — 1 is within limit
        var result = await _svc.GetBatchStatusAsync(["aaaaaaaa-1111-2222-3333-444444444444"]);
        Assert.Single(result.Jobs);
    }

    [Fact]
    public async Task GetBatchStatus_ExactlyMaxBatchSize_Accepted()
    {
        // 50 job IDs — boundary: should be accepted
        var jobIds = Enumerable.Range(0, 50)
            .Select(i => $"{i:x8}-0000-0000-0000-000000000000")
            .ToList();

        foreach (var id in jobIds)
            SetupMinimalJobMock(id);

        var result = await _svc.GetBatchStatusAsync(jobIds);
        Assert.Equal(50, result.Jobs.Count);
    }

    [Fact]
    public async Task GetBatchStatus_OneOverMaxBatchSize_ThrowsArgumentException()
    {
        // 51 job IDs — should throw
        var jobIds = Enumerable.Range(0, 51)
            .Select(i => $"{i:x8}-0000-0000-0000-000000000000")
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetBatchStatusAsync(jobIds));
    }

    [Fact]
    public async Task GetBatchStatus_WayOverMaxBatchSize_ThrowsArgumentException()
    {
        // 200 job IDs — clearly over limit
        var jobIds = Enumerable.Range(0, 200)
            .Select(i => $"{i:x8}-0000-0000-0000-000000000000")
            .ToList();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetBatchStatusAsync(jobIds));
    }

    [Fact]
    public async Task GetBatchStatus_EmptyArray_ThrowsArgumentException()
    {
        // Existing behavior — confirm empty still throws
        await Assert.ThrowsAsync<ArgumentException>(
            () => _svc.GetBatchStatusAsync([]));
    }

    // ─────────────────────────────────────────────────────────────────
    // 3. MCP Tool — hlx_batch_status array size enforcement
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task McpBatchStatus_OverLimit_RejectsLargeArray()
    {
        var tools = new HelixMcpTools(_svc);

        var jobIds = Enumerable.Range(0, 51)
            .Select(i => $"{i:x8}-0000-0000-0000-000000000000")
            .ToArray();

        // The MCP tool should propagate the ArgumentException from GetBatchStatusAsync
        // or enforce its own limit. Either way, 51 IDs must not be processed.
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => tools.BatchStatus(jobIds));
    }

    [Fact]
    public async Task McpBatchStatus_AtLimit_Accepted()
    {
        var tools = new HelixMcpTools(_svc);

        var jobIds = Enumerable.Range(0, 50)
            .Select(i => $"{i:x8}-0000-0000-0000-000000000000")
            .ToArray();

        foreach (var id in jobIds)
            SetupMinimalJobMock(id);

        // Should not throw
        var json = await tools.BatchStatus(jobIds);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(50, doc.RootElement.GetProperty("jobCount").GetInt32());
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private void SetupMinimalJobMock(string jobId)
    {
        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("test-job");
        jobDetails.QueueId.Returns("queue");
        jobDetails.Creator.Returns("user");
        jobDetails.Source.Returns("src");
        jobDetails.Created.Returns("2025-01-01T00:00:00Z");
        jobDetails.Finished.Returns("2025-01-01T00:01:00Z");

        _mockApi.GetJobDetailsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);
        _mockApi.ListWorkItemsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary>());
    }
}
