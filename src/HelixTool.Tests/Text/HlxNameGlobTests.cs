using HelixTool.Core.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace HelixTool.Tests.Text;

/// <summary>
/// Covers the bounded two-pointer glob matcher (PR #159 review item: the prior per-call regex
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
    [InlineData("*ab", "*b", true)]
    [InlineData("zab", "*b", true)]
    [InlineData("**ab", "**b", true)]
    [InlineData("*abab", "*ab", true)]
    [InlineData("ab*abab", "ab*ab", true)]
    [InlineData("*aaab", "*a*b", true)]
    [InlineData("*aaab", "*a*a*c", false)]
    [InlineData("abababa", "*aba*aba", true)]
    [InlineData("aaaaab", "*aa*aa*b", true)]
    [InlineData("aaaaab", "aa*aa*aa*b", false)]
    [InlineData("abcabc", "*abc*abc*", true)]
    [InlineData("*", "?", true)]
    [InlineData("?", "*?", true)]
    [InlineData("", "***", true)]
    [InlineData("", "*?*", false)]
    [InlineData("", "", true)]
    [InlineData("a", "", false)]
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
        // classic catastrophic-backtracking shape for a naive regex-based matcher; the bounded
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsMatch_SeededWildcardInputsAgreeWithIndependentRegex(bool caseSensitive)
    {
        var random = new Random(159953);
        const string alphabet = "abAB*?.[]\n";
        for (var i = 0; i < 10000; i++)
        {
            string Sample(int maxLength) => new(Enumerable.Range(0, random.Next(maxLength + 1))
                .Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var candidate = Sample(16);
            var pattern = Sample(10);
            var expected = ReferenceMatch(candidate, pattern, caseSensitive);
            var actual = HlxNameGlob.IsMatch(candidate, pattern, caseSensitive);
            Assert.True(expected == actual,
                $"seed=159953 iteration={i} caseSensitive={caseSensitive} candidate={System.Text.Json.JsonSerializer.Serialize(candidate)} pattern={System.Text.Json.JsonSerializer.Serialize(pattern)} expected={expected} actual={actual}");
        }
    }

    private static bool ReferenceMatch(string candidate, string pattern, bool caseSensitive)
    {
        if (pattern.Length == 0)
            return candidate.Length == 0;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return candidate.Contains(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        var expression = "\\A" + string.Concat(pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString())
        })) + "\\z";
        var options = RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!caseSensitive)
            options |= RegexOptions.IgnoreCase;
        return Regex.IsMatch(candidate, expression, options, TimeSpan.FromSeconds(1));
    }
}
