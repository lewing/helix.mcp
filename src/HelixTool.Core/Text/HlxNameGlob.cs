using System.Text;
using System.Text.RegularExpressions;

namespace HelixTool.Core.Text;

/// <summary>
/// Shared bounded <c>*</c>/<c>?</c> glob matcher for name-style selectors (timeline record names,
/// artifact member paths, etc). A pattern containing no wildcard characters falls back to the
/// legacy case-insensitive substring match so existing callers keep working unchanged.
/// This is intentionally not a general regex engine: no character classes, anchors, or escapes
/// beyond literal <c>*</c>/<c>?</c>, and traversal-sensitive callers (e.g. ZIP member selection)
/// must apply their own path-safety checks on top of this match.
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

        var regex = BuildRegex(pattern, caseSensitive);
        return regex.IsMatch(candidate);
    }

    /// <summary>True when the pattern contains a <c>*</c> or <c>?</c> wildcard.</summary>
    public static bool HasWildcard(string pattern) => pattern.Contains('*') || pattern.Contains('?');

    private static Regex BuildRegex(string pattern, bool caseSensitive)
    {
        // Collapse redundant repeated '*' before translating, so '**' behaves like '*'.
        var sb = new StringBuilder("^");
        var prevStar = false;
        foreach (var ch in pattern)
        {
            if (ch == '*')
            {
                if (prevStar) continue;
                sb.Append(".*");
                prevStar = true;
            }
            else if (ch == '?')
            {
                sb.Append('.');
                prevStar = false;
            }
            else
            {
                sb.Append(Regex.Escape(ch.ToString()));
                prevStar = false;
            }
        }
        sb.Append('$');

        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (!caseSensitive)
            options |= RegexOptions.IgnoreCase;

        return new Regex(sb.ToString(), options);
    }
}
