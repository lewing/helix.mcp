// Tests for TextSearchHelper — the shared search utility extracted from HelixService.SearchLines.
// These test the pure-function search logic independent of any I/O or API calls.

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class TextSearchHelperTests
{
    private const int DefaultContext = 0;
    private const int DefaultMaxMatches = 50;

    // ── Basic matching ──────────────────────────────────────────────

    [Fact]
    public void SearchLines_BasicMatch_ReturnsCorrectLineAndNumber()
    {
        var lines = new[] { "Starting build", "Compilation succeeded", "error CS1234: Something bad", "Done" };

        var result = TextSearchHelper.SearchLines("test", lines, "error", DefaultContext, DefaultMaxMatches);

        Assert.Equal("test", result.WorkItem);
        Assert.Equal(4, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber); // 1-based
        Assert.Contains("error CS1234", match.Line);
    }

    [Fact]
    public void SearchLines_MultipleMatches_ReturnsAll()
    {
        var lines = new[]
        {
            "error: first problem",
            "info: all clear",
            "error: second problem",
            "debug: details",
            "error: third problem"
        };

        var result = TextSearchHelper.SearchLines("test", lines, "error", DefaultContext, DefaultMaxMatches);

        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(3, result.Matches[1].LineNumber);
        Assert.Equal(5, result.Matches[2].LineNumber);
    }

    // ── Context lines ───────────────────────────────────────────────

    [Fact]
    public void SearchLines_ContextLinesZero_ReturnsNoContext()
    {
        var lines = new[] { "line A", "line B", "ERROR: fail", "line D", "line E" };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 0, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
        Assert.Null(match.Context); // no context when contextLines=0
    }

    [Fact]
    public void SearchLines_ContextLinesOne_ReturnsSurroundingLines()
    {
        var lines = new[] { "line A", "line B", "ERROR: fail", "line D", "line E" };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 1, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
        Assert.NotNull(match.Context);
        Assert.Equal(3, match.Context!.Count); // 1 before + match + 1 after
        Assert.Equal("line B", match.Context[0]);
        Assert.Equal("ERROR: fail", match.Context[1]);
        Assert.Equal("line D", match.Context[2]);
    }

    [Fact]
    public void SearchLines_ContextLinesThree_ReturnsMoreContext()
    {
        var lines = new[]
        {
            "line 1", "line 2", "line 3", "line 4",
            "ERROR: fail",
            "line 6", "line 7", "line 8", "line 9"
        };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 3, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Context);
        // 3 before + match + 3 after = 7
        Assert.Equal(7, match.Context!.Count);
        Assert.Equal("line 2", match.Context[0]);
        Assert.Equal("ERROR: fail", match.Context[3]);
        Assert.Equal("line 8", match.Context[6]);
    }

    [Fact]
    public void SearchLines_ContextAtStartOfContent_ClampsBefore()
    {
        var lines = new[] { "ERROR: at start", "line 2", "line 3" };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 3, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Context);
        // Only 0 lines before (clamped) + match + 2 after = 3
        Assert.Equal(3, match.Context!.Count);
        Assert.Equal("ERROR: at start", match.Context[0]);
        Assert.Equal("line 3", match.Context[2]);
    }

    [Fact]
    public void SearchLines_ContextAtEndOfContent_ClampsAfter()
    {
        var lines = new[] { "line 1", "line 2", "ERROR: at end" };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 3, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Context);
        // 2 before + match + 0 after (clamped) = 3
        Assert.Equal(3, match.Context!.Count);
        Assert.Equal("line 1", match.Context[0]);
        Assert.Equal("ERROR: at end", match.Context[2]);
    }

    // ── Case insensitivity ──────────────────────────────────────────

    [Theory]
    [InlineData("error")]
    [InlineData("ERROR")]
    [InlineData("Error")]
    [InlineData("eRrOr")]
    public void SearchLines_CaseInsensitive_MatchesRegardlessOfCase(string pattern)
    {
        var lines = new[] { "line 1", "Error: something went wrong", "line 3" };

        var result = TextSearchHelper.SearchLines("test", lines, pattern, DefaultContext, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(2, match.LineNumber);
    }

    // ── No matches ──────────────────────────────────────────────────

    [Fact]
    public void SearchLines_NoMatches_ReturnsEmptyList()
    {
        var lines = new[] { "Everything is fine", "No issues here", "All good" };

        var result = TextSearchHelper.SearchLines("test", lines, "FATAL_CRASH", DefaultContext, DefaultMaxMatches);

        Assert.Empty(result.Matches);
        Assert.Equal(3, result.TotalLines);
    }

    // ── Empty content ───────────────────────────────────────────────

    [Fact]
    public void SearchLines_EmptyContent_ReturnsEmptyList()
    {
        var lines = Array.Empty<string>();

        var result = TextSearchHelper.SearchLines("test", lines, "error", DefaultContext, DefaultMaxMatches);

        Assert.Empty(result.Matches);
        Assert.Equal(0, result.TotalLines);
    }

    // ── Null/empty pattern ──────────────────────────────────────────

    [Fact]
    public void SearchLines_EmptyPattern_Throws()
    {
        var lines = new[] { "line 1", "line 2", "line 3" };

        Assert.Throws<ArgumentException>(() =>
            TextSearchHelper.SearchLines("test", lines, "", DefaultContext, DefaultMaxMatches));
    }

    // ── Max matches limit ───────────────────────────────────────────

    [Fact]
    public void SearchLines_MaxMatchesLimit_ReturnsOnlyFirst()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"error on line {i}").ToArray();

        var result = TextSearchHelper.SearchLines("test", lines, "error", DefaultContext, 2);

        Assert.Equal(2, result.Matches.Count);
        Assert.Equal(10, result.TotalLines);
        Assert.Equal(1, result.Matches[0].LineNumber);
        Assert.Equal(2, result.Matches[1].LineNumber);
    }

    [Fact]
    public void SearchLines_MaxMatchesOne_ReturnsSingleMatch()
    {
        var lines = new[] { "error A", "error B", "error C" };

        var result = TextSearchHelper.SearchLines("test", lines, "error", DefaultContext, 1);

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
    }

    // ── Overlapping context ─────────────────────────────────────────

    [Fact]
    public void SearchLines_CloseMatches_ContextDoesNotDuplicate()
    {
        // Two errors 2 lines apart — with contextLines=1, context windows overlap
        var lines = new[] { "line 1", "ERROR: first", "line 3", "ERROR: second", "line 5" };

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 1, DefaultMaxMatches);

        Assert.Equal(2, result.Matches.Count);
        // First match: context is [line 1, ERROR: first, line 3]
        Assert.Equal(3, result.Matches[0].Context!.Count);
        Assert.Equal("line 1", result.Matches[0].Context![0]);
        // Second match: context is [line 3, ERROR: second, line 5]
        Assert.Equal(3, result.Matches[1].Context!.Count);
        Assert.Equal("line 3", result.Matches[1].Context![0]);
    }

    // ── Line numbers ────────────────────────────────────────────────

    [Fact]
    public void SearchLines_LineNumbers_AreOneBased()
    {
        var lines = new[] { "first line", "second line", "third line" };

        var result = TextSearchHelper.SearchLines("test", lines, "first", DefaultContext, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber); // 1-based, not 0-based
    }

    [Fact]
    public void SearchLines_LastLine_HasCorrectLineNumber()
    {
        var lines = new[] { "line 1", "line 2", "match here" };

        var result = TextSearchHelper.SearchLines("test", lines, "match here", DefaultContext, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(3, match.LineNumber);
    }

    // ── Large content ───────────────────────────────────────────────

    [Fact]
    public void SearchLines_LargeContent_SingleMatchAtMiddle()
    {
        var lines = new string[10_000];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = $"normal log line {i + 1}";
        lines[4999] = "CRITICAL ERROR: disk full";

        var result = TextSearchHelper.SearchLines("test", lines, "CRITICAL ERROR", DefaultContext, DefaultMaxMatches);

        Assert.Equal(10_000, result.TotalLines);
        var match = Assert.Single(result.Matches);
        Assert.Equal(5000, match.LineNumber);
        Assert.Contains("CRITICAL ERROR", match.Line);
    }

    [Fact]
    public void SearchLines_LargeContent_WithContext_ReturnsCorrectContext()
    {
        var lines = new string[10_000];
        for (int i = 0; i < lines.Length; i++)
            lines[i] = $"log line {i + 1}";
        lines[4999] = "ERROR: something failed";

        var result = TextSearchHelper.SearchLines("test", lines, "ERROR", 2, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.NotNull(match.Context);
        Assert.Equal(5, match.Context!.Count); // 2 before + match + 2 after
        Assert.Equal("log line 4998", match.Context[0]);
        Assert.Equal("log line 4999", match.Context[1]);
        Assert.Equal("ERROR: something failed", match.Context[2]);
        Assert.Equal("log line 5001", match.Context[3]);
        Assert.Equal("log line 5002", match.Context[4]);
    }

    // ── Identifier passthrough ──────────────────────────────────────

    [Fact]
    public void SearchLines_IdentifierPassedToResult()
    {
        var lines = new[] { "error: test" };

        var result = TextSearchHelper.SearchLines("my-work-item", lines, "error", DefaultContext, DefaultMaxMatches);

        Assert.Equal("my-work-item", result.WorkItem);
    }

    // ── Special characters in pattern ───────────────────────────────

    [Fact]
    public void SearchLines_PatternWithSpecialChars_MatchesLiterally()
    {
        var lines = new[] { "file.cs(42,1): error CS0001", "normal line", "file.cs(43,2): warning" };

        var result = TextSearchHelper.SearchLines("test", lines, "file.cs(42,1)", DefaultContext, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
    }

    [Fact]
    public void SearchLines_PatternWithBrackets_MatchesLiterally()
    {
        var lines = new[] { "test [ERROR] something", "test (info) other" };

        var result = TextSearchHelper.SearchLines("test", lines, "[ERROR]", DefaultContext, DefaultMaxMatches);

        var match = Assert.Single(result.Matches);
        Assert.Equal(1, match.LineNumber);
    }
}
