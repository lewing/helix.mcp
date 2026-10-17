namespace HelixTool.Core.Text;

/// <summary>
/// Shared bounded <c>*</c>/<c>?</c> glob matcher for name-style selectors (timeline record names,
/// artifact member paths, etc). A pattern containing no wildcard characters falls back to the
/// legacy case-insensitive substring match so existing callers keep working unchanged.
/// This is intentionally not a general regex engine: no character classes, anchors, or escapes
/// beyond literal <c>*</c>/<c>?</c>, and traversal-sensitive callers (e.g. ZIP member selection)
/// must apply their own path-safety checks on top of this match.
/// <para>
/// Wildcard matching uses the classic greedy-star-with-backtrack-point algorithm (track the most
/// recent '*' and the candidate position it last matched, backtrack only that one bookmark on a
/// mismatch) rather than a backtracking regex. A regex built from an adjacent run of <c>*</c>s
/// (e.g. <c>*a*a*a*a*b</c>) can force catastrophic exponential backtracking against a long
/// non-matching candidate; this algorithm has no recursion and no per-call compilation, but it is
/// <b>not</b> linear-time: each pending '*' bookmark can be retried once per remaining candidate
/// character, so worst case is O(candidate.Length * pattern.Length), not O(candidate.Length +
/// pattern.Length). That bound is still safe to call once per row across an entire selector scan
/// (no exponential blow-up), just not truly linear.
/// </para>
/// </summary>
public static class HlxNameGlob
{
    /// <summary>
    /// Guideline-only pattern length used for diagnostics/logging; not a validity gate. Overlong
    /// patterns still work, they are just unlikely to be an intentional selector.
    /// </summary>
    public const int GuidelineMaxLength = 256;

    /// <summary>True when the candidate matches the pattern under glob or legacy substring rules.</summary>
    public static bool IsMatch(string candidate, string pattern, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(pattern);

        if (pattern.Length == 0)
            return candidate.Length == 0;

        if (!HasWildcard(pattern))
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            return candidate.Contains(pattern, comparison);
        }

        return MatchGlob(candidate, pattern, caseSensitive);
    }

    /// <summary>True when the pattern contains a <c>*</c> or <c>?</c> wildcard.</summary>
    public static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    /// <summary>
    /// Bounded (no recursion, no backtracking regex) full-string <c>*</c>/<c>?</c> glob match.
    /// Classic two-pointer wildcard algorithm: on a mismatch, retry from just after the most
    /// recently seen <c>*</c>, advancing the candidate position it anchors by one each retry —
    /// each candidate character is revisited at most once per pending <c>*</c>, bounding total work
    /// to O(candidate.Length * pattern.Length) with constant extra space (not linear-time: see the
    /// class remarks). The <c>'*'</c> check is evaluated before the literal/<c>'?'</c> equality
    /// check so a <paramref name="pattern"/> wildcard always establishes its backtrack bookmark —
    /// otherwise a <paramref name="candidate"/> that happens to contain a literal <c>'*'</c>
    /// character could coincidentally satisfy the equality branch first and consume the wildcard
    /// position without ever recording <c>starPi</c>/<c>starCi</c>.
    /// </summary>
    private static bool MatchGlob(string candidate, string pattern, bool caseSensitive)
    {
        var cLen = candidate.Length;
        var pLen = pattern.Length;
        var ci = 0;
        var pi = 0;
        var starPi = -1;
        var starCi = -1;

        bool CharsEqual(char a, char b) =>
            caseSensitive ? a == b : char.ToUpperInvariant(a) == char.ToUpperInvariant(b);

        while (ci < cLen)
        {
            if (pi < pLen && pattern[pi] == '*')
            {
                // Collapse adjacent '*'s: repeatedly recording the latest one is equivalent to a
                // single '*' at that position, since a '*' can already match zero characters. This
                // branch must run before the literal/'?' equality check below: pattern[pi] == '*'
                // always means "wildcard", regardless of what character the candidate has here.
                starPi = pi;
                starCi = ci;
                pi++;
            }
            else if (pi < pLen && (pattern[pi] == '?' || CharsEqual(pattern[pi], candidate[ci])))
            {
                ci++;
                pi++;
            }
            else if (starPi >= 0)
            {
                // Mismatch with a pending '*': let it consume one more candidate character and
                // retry the pattern from just after it.
                pi = starPi + 1;
                starCi++;
                ci = starCi;
            }
            else
            {
                return false;
            }
        }

        while (pi < pLen && pattern[pi] == '*')
            pi++;

        return pi == pLen;
    }
}
