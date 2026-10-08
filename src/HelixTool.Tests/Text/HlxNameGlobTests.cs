using HelixTool.Core.Text;
using Xunit;

namespace HelixTool.Tests.Text;

/// <summary>
/// Covers the linear-time two-pointer glob matcher (PR #159 review item: the prior per-call regex
/// construction was backtracking-prone against adjacent '*' runs). These are behavior/contract
/// tests, not catastrophic-backtracking timing tests — the fix is validated structurally (no
/// regex, no per-call compilation) plus here via standard glob semantics equivalence.
/// </summary>
public sealed class HlxNameGlobTests
{
    [Theory]
    [InlineData("Build", "Build", true)]
    [InlineData("Build", "build", true)] // case-insensitive by default
    [InlineData("Build Solution", "Build*", true)]
    [InlineData("Build Solution", "*Solution", true)]
    [InlineData("Build Solution", "Build*Solution", true)]
    [InlineData("Build Solution", "Build?Solution", true)]
    [InlineData("BuildXSolution", "Build?Solution", true)]
    [InlineData("BuildXYSolution", "Build?Solution", false)] // '?' matches exactly one char
    [InlineData("Build Solution", "*", true)]
    [InlineData("", "*", true)]
    [InlineData("", "?", false)]
    [InlineData("anything", "**", true)] // collapses to a single '*'
    [InlineData("Build", "Nope", false)]
    [InlineData("Build Solution", "Sol", true)] // no wildcard -> legacy substring match
    [InlineData("Build Solution", "sol", true)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab", "*a*a*a*a*a*a*a*a*a*a*c", false)]
    public void IsMatch_MatchesClassicGlobSemantics(string candidate, string pattern, bool expected)
    {
        Assert.Equal(expected, HlxNameGlob.IsMatch(candidate, pattern));
    }

    [Fact]
    public void IsMatch_CaseSensitiveOverride_DistinguishesCase()
    {
        Assert.False(HlxNameGlob.IsMatch("Build", "build*", caseSensitive: true));
        Assert.True(HlxNameGlob.IsMatch("build1", "build*", caseSensitive: true));
    }

    [Fact]
    public void IsMatch_AdjacentStarRunDoesNotMatchANonMatchingCandidate()
    {
        // A pattern built from many adjacent '*'s against a long near-miss candidate is the
        // classic catastrophic-backtracking shape for a naive regex-based matcher; the linear
        // two-pointer algorithm must still terminate promptly and correctly report no match.
        var candidate = new string('a', 2000) + "x";
        var pattern = string.Concat(Enumerable.Repeat("a*", 30)) + "b";
        Assert.False(HlxNameGlob.IsMatch(candidate, pattern));
    }

    [Fact]
    public void HasWildcard_DetectsStarAndQuestionMarkOnly()
    {
        Assert.True(HlxNameGlob.HasWildcard("a*b"));
        Assert.True(HlxNameGlob.HasWildcard("a?b"));
        Assert.False(HlxNameGlob.HasWildcard("plain"));
    }
}
