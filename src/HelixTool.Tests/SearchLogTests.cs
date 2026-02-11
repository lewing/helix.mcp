using System.Text;
using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class SearchLogTests
{
    private const string ValidJobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "MyWorkItem";

    private readonly IHelixApiClient _mockApi;
    private readonly HelixService _svc;

    public SearchLogTests()
    {
        _mockApi = Substitute.For<IHelixApiClient>();
        _svc = new HelixService(_mockApi);
    }

    /// <summary>Helper: configure mock to return a stream with the given log content.</summary>
    private void SetupLogContent(string content)
    {
        _mockApi.GetConsoleLogAsync(WorkItem, ValidJobId, Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(content)));
    }

    // --- Argument validation ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchLog_ThrowsOnNullJobId(string? badJobId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchConsoleLogAsync(badJobId!, WorkItem, "pattern"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchLog_ThrowsOnNullWorkItem(string? badWorkItem)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchConsoleLogAsync(ValidJobId, badWorkItem!, "pattern"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchLog_ThrowsOnNullPattern(string? badPattern)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, badPattern!));
    }

    // --- Matching behavior ---

    [Fact]
    public async Task SearchLog_FindsMatchingLines()
    {
        var logContent = "Starting build\nCompilation succeeded\nerror CS1234: Something bad\nDone";
        SetupLogContent(logContent);

        var result = await _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "error");

        Assert.Equal(WorkItem, result.WorkItem);
        Assert.Equal(4, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber); // 1-based
        Assert.Contains("error CS1234", match.Line);
    }

    [Fact]
    public async Task SearchLog_CaseInsensitive()
    {
        var logContent = "Line 1\nerror happened here\nLine 3";
        SetupLogContent(logContent);

        // Search with uppercase "ERROR", log has lowercase "error"
        var result = await _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "ERROR");

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
        Assert.Contains("error", match.Line);
    }

    [Fact]
    public async Task SearchLog_RespectsMaxMatches()
    {
        // Create log with 10 matching lines
        var lines = Enumerable.Range(1, 10).Select(i => $"error on line {i}");
        var logContent = string.Join("\n", lines);
        SetupLogContent(logContent);

        var result = await _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "error", maxMatches: 3);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(10, result.TotalLines);
        // Should be the first 3 matches
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(2, result.Matches[1].LineNumber);
        Assert.Equal(3, result.Matches[2].LineNumber);
    }

    [Fact]
    public async Task SearchLog_IncludesContextLines()
    {
        var logContent = "line A\nline B\nERROR: fail\nline D\nline E";
        SetupLogContent(logContent);

        var result = await _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "ERROR", contextLines: 1);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
        Assert.NotNull(match.Context);
        // contextLines=1 → 1 before + match + 1 after = 3 context lines
        Assert.Equal(3, match.Context.Count);
        Assert.Equal("line B", match.Context[0]);
        Assert.Equal("ERROR: fail", match.Context[1]);
        Assert.Equal("line D", match.Context[2]);
    }

    [Fact]
    public async Task SearchLog_ReturnsEmptyForNoMatches()
    {
        var logContent = "Everything is fine\nNo issues here\nAll good";
        SetupLogContent(logContent);

        var result = await _svc.SearchConsoleLogAsync(ValidJobId, WorkItem, "FATAL_CRASH");

        Assert.Empty(result.Matches);
        Assert.Equal(3, result.TotalLines);
        Assert.Equal(WorkItem, result.WorkItem);
    }
}
