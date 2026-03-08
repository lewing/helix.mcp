// Tests for streaming behavior in HelixService — edge cases for SEC-3 (streaming large downloads).
// Validates current stream-to-string reading and prepares for streaming refactor.

using System.Net;
using System.Text;
using HelixTool.Core;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace HelixTool.Tests;

public class StreamingBehaviorTests
{
    private const string ValidJobId = "c3d4e5f6-a7b8-9012-cdef-234567890abc";
    private const string WorkItem = "test-workitem";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public StreamingBehaviorTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    // ── Empty response handling ──────────────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_EmptyStream_ReturnsEmptyString()
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Array.Empty<byte>()));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_EmptyStream_WithTailLines_ReturnsEmptyString()
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Array.Empty<byte>()));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem, tailLines: 10);

        Assert.Equal(string.Empty, result);
    }

    // ── Large response handling ──────────────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_LargeContent_ReturnsFullContent()
    {
        // Simulate a large log — 1000 lines of 100 chars each (~100KB)
        var lines = Enumerable.Range(0, 1000)
            .Select(i => $"[2025-07-18 10:00:{i:D3}] Line {i}: {new string('x', 80)}")
            .ToList();
        var content = string.Join('\n', lines);
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Equal(content, result);
        Assert.Equal(1000, result.Split('\n').Length);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_LargeContent_TailLines_ReturnsLastN()
    {
        var lines = Enumerable.Range(0, 1000)
            .Select(i => $"Line {i}")
            .ToList();
        var content = string.Join('\n', lines);
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem, tailLines: 5);

        var resultLines = result.Split('\n');
        Assert.Equal(5, resultLines.Length);
        Assert.Equal("Line 995", resultLines[0]);
        Assert.Equal("Line 999", resultLines[4]);
    }

    // ── TailLines edge cases ─────────────────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_TailLines_ExceedsLineCount_ReturnsAll()
    {
        var content = "Line 1\nLine 2\nLine 3";
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem, tailLines: 100);

        // When tailLines > total lines, should return all content
        Assert.Contains("Line 1", result);
        Assert.Contains("Line 3", result);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_TailLines_One_ReturnsSingleLine()
    {
        var content = "First\nSecond\nThird\nLast";
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem, tailLines: 1);

        Assert.Equal("Last", result);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_SingleLineContent_NoNewlines()
    {
        var content = "Single line with no newline";
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Equal(content, result);
    }

    // ── Connection error handling ────────────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_HttpError_WrapsInHelixException()
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused", null, HttpStatusCode.ServiceUnavailable));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem));

        Assert.Contains("API error", ex.Message);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_NotFound_WrapsInHelixException()
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_Unauthorized_MentionsLogin()
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HelixException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem));

        Assert.Contains("login", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Stream disposal ──────────────────────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_DisposesStream()
    {
        // Verify the stream is properly disposed after reading.
        var stream = new TrackingMemoryStream(Encoding.UTF8.GetBytes("test content"));

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => stream);

        await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.True(stream.WasDisposed, "Stream should be disposed after reading");
    }

    // ── Content with special characters ──────────────────────────────

    [Fact]
    public async Task GetConsoleLogContentAsync_BinaryishContent_ReturnsAsString()
    {
        // Log content may contain ANSI escape codes, null bytes from truncation, etc.
        var content = "Normal line\n\x1b[31mRed text\x1b[0m\nAnother line";
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Contains("\x1b[31m", result);
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task GetConsoleLogContentAsync_Utf8Content_PreservesEncoding()
    {
        var content = "Build summary: 🟢 passed | 🔴 failed | ⚠️ warnings";
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Equal(content, result);
    }

    // ── Input validation ─────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetConsoleLogContentAsync_InvalidJobId_Throws(string? jobId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetConsoleLogContentAsync(jobId!, WorkItem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetConsoleLogContentAsync_InvalidWorkItem_Throws(string? workItem)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.GetConsoleLogContentAsync(ValidJobId, workItem!));
    }

    // ── SEC-3: Streaming readiness ───────────────────────────────────
    // These tests document the current behavior (read-all-to-string).
    // When SEC-3 lands streaming for large downloads, these validate
    // that the non-streaming path still works for normal-sized logs.

    [Fact]
    public async Task GetConsoleLogContentAsync_ModerateSizeLog_ReadsToString()
    {
        // ~50KB log — within acceptable range for non-streaming read
        var lines = Enumerable.Range(0, 500)
            .Select(i => $"[INFO] Build step {i}: {new string('a', 80)}");
        var content = string.Join('\n', lines);
        var bytes = Encoding.UTF8.GetBytes(content);

        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(bytes));

        var result = await _svc.GetConsoleLogContentAsync(ValidJobId, WorkItem);

        Assert.Equal(content.Length, result.Length);
    }

    // ── Test helpers ─────────────────────────────────────────────────

    private class TrackingMemoryStream : MemoryStream
    {
        public bool WasDisposed { get; private set; }

        public TrackingMemoryStream(byte[] buffer) : base(buffer) { }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
