// Tests for AzDO search_log functionality.
// Validates the AzdoService.SearchBuildLogAsync method that searches AzDO build logs.
// Ensures build log search returns expected matches, line numbers, and surrounding context.

using HelixTool.Core;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("FileSearchConfig")]
public class AzdoSearchLogTests
{
    private readonly IAzdoApiClient _mockApi;
    private readonly AzdoService _svc;

    public AzdoSearchLogTests()
    {
        _mockApi = Substitute.For<IAzdoApiClient>();
        _svc = new AzdoService(_mockApi);
    }

    /// <summary>Helper: set up the mock to return log content for a given build/logId.</summary>
    private void SetupBuildLog(string content, string org = "dnceng-public", string project = "public",
        int buildId = 42, int logId = 7)
    {
        _mockApi.GetBuildLogAsync(org, project, buildId, logId, Arg.Any<CancellationToken>())
            .Returns(content);
    }

    // ── Happy path ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_FindsErrors_ReturnsMatchesWithContext()
    {
        var logContent = "Starting build\nCompilation succeeded\nerror CS1234: Something bad\nDone";
        SetupBuildLog(logContent);

        var result = await _svc.SearchBuildLogAsync("42", 7, "error");

        Assert.NotNull(result);
        Assert.Equal(4, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber); // 1-based
        Assert.Contains("error CS1234", match.Line);
    }

    [Fact]
    public async Task SearchBuildLog_MultipleMatches_ReturnsAll()
    {
        var lines = new[]
        {
            "error: first problem",
            "info: all clear",
            "error: second problem",
            "debug: details",
            "error: third problem"
        };
        SetupBuildLog(string.Join("\n", lines));

        var result = await _svc.SearchBuildLogAsync("42", 7, "error");

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(3, result.Matches[1].LineNumber);
        Assert.Equal(5, result.Matches[2].LineNumber);
    }

    // ── No matches ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_NoMatches_ReturnsEmpty()
    {
        SetupBuildLog("Everything is fine\nNo issues here\nAll good");

        var result = await _svc.SearchBuildLogAsync("42", 7, "FATAL_CRASH");

        Assert.Empty(result.Matches);
        Assert.Equal(3, result.TotalLines);
    }

    // ── Context lines parameter ─────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_ContextLinesZero_ReturnsNoContext()
    {
        var logContent = "line A\nline B\nERROR: fail\nline D\nline E";
        SetupBuildLog(logContent);

        var result = await _svc.SearchBuildLogAsync("42", 7, "ERROR", contextLines: 0);

        var match = Assert.Single(result.Matches);
        Assert.Null(match.Context);
    }

    [Fact]
    public async Task SearchBuildLog_ContextLinesFive_ReturnsExtendedContext()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"log line {i}").ToList();
        lines[9] = "ERROR: failure at line 10"; // index 9 = line 10

        SetupBuildLog(string.Join("\n", lines));

        var result = await _svc.SearchBuildLogAsync("42", 7, "ERROR", contextLines: 5);

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Context);
        // 5 before + match + 5 after = 11
        Assert.Equal(11, match.Context!.Count);
        Assert.Equal("log line 5", match.Context[0]);
        Assert.Equal("ERROR: failure at line 10", match.Context[5]);
        Assert.Equal("log line 15", match.Context[10]);
    }

    // ── Max matches respected ───────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_MaxMatchesRespected_LimitsOutput()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"error on line {i}");
        SetupBuildLog(string.Join("\n", lines));

        var result = await _svc.SearchBuildLogAsync("42", 7, "error", maxMatches: 3);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(20, result.TotalLines);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(2, result.Matches[1].LineNumber);
        Assert.Equal(3, result.Matches[2].LineNumber);
    }

    // ── Large log handling ──────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_LargeLog_CompletesSuccessfully()
    {
        var lines = new string[10_000];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = $"build output line {i + 1}";
        lines[4999] = "CRITICAL ERROR: out of memory";

        SetupBuildLog(string.Join("\n", lines));

        var result = await _svc.SearchBuildLogAsync("42", 7, "CRITICAL ERROR");

        Assert.Equal(10_000, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(5000, match.LineNumber);
        Assert.Contains("CRITICAL ERROR", match.Line);
    }

    // ── Special characters ──────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_SpecialCharPattern_MatchesLiterally()
    {
        var logContent = "file.cs(42,1): error CS0001\nnormal line\nfile.cs(43,2): warning";
        SetupBuildLog(logContent);

        var result = await _svc.SearchBuildLogAsync("42", 7, "file.cs(42,1)");

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
    }

    [Fact]
    public async Task SearchBuildLog_BracketPattern_NotInterpretedAsRegex()
    {
        SetupBuildLog("[ERROR] something happened\n[INFO] all clear");

        var result = await _svc.SearchBuildLogAsync("42", 7, "[ERROR]");

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
    }

    // ── URL resolution ──────────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_AcceptsUrl_ResolvesOrgProject()
    {
        _mockApi.GetBuildLogAsync("myorg", "myproject", 123, 5, Arg.Any<CancellationToken>())
            .Returns("line 1\nerror: test failure\nline 3");

        var result = await _svc.SearchBuildLogAsync(
            "https://dev.azure.com/myorg/myproject/_build/results?buildId=123", 5, "error");

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
        await _mockApi.Received(1).GetBuildLogAsync("myorg", "myproject", 123, 5, Arg.Any<CancellationToken>());
    }

    // ── Case insensitivity ──────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_CaseInsensitive_MatchesRegardless()
    {
        SetupBuildLog("Line 1\nerror happened here\nLine 3");

        var result = await _svc.SearchBuildLogAsync("42", 7, "ERROR");

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
    }

    // ── Input validation ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task SearchBuildLog_ThrowsOnInvalidPattern(string? badPattern)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => _svc.SearchBuildLogAsync("42", 7, badPattern!));
    }

    // ── Null log content ────────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_NullLogContent_ThrowsInvalidOperation()
    {
        _mockApi.GetBuildLogAsync("dnceng-public", "public", 42, 7, Arg.Any<CancellationToken>())
            .Returns((string?)null);

        // Ripley's implementation throws InvalidOperationException for null log content
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.SearchBuildLogAsync("42", 7, "error"));
    }

    // ── Search disabled ─────────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_WhenSearchDisabled_ThrowsInvalidOperation()
    {
        // IsFileSearchDisabled is controlled by HLX_DISABLE_FILE_SEARCH env var.
        // This test validates the guard clause exists.
        // Setting the env var in-process to test the behavior.
        var original = Environment.GetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH");
        try
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", "true");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => _svc.SearchBuildLogAsync("42", 7, "error"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH", original);
        }
    }

    // ── Identifier in result ────────────────────────────────────────

    [Fact]
    public async Task SearchBuildLog_ResultIdentifier_ContainsLogId()
    {
        SetupBuildLog("error: test");

        var result = await _svc.SearchBuildLogAsync("42", 7, "error");

        // Ripley's implementation uses $"log:{logId}" as identifier
        Assert.Contains("7", result.WorkItem);
    }
}
