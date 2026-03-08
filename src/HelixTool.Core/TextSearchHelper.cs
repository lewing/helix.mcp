namespace HelixTool.Core;

/// <summary>A single match in a log or file search.</summary>
public record LogMatch(int LineNumber, string Line, List<string>? Context = null);

/// <summary>Result of searching a log for pattern matches.</summary>
public record LogSearchResult(string WorkItem, List<LogMatch> Matches, int TotalLines);

/// <summary>Result of searching an uploaded file's content.</summary>
public record FileContentSearchResult(string FileName, List<LogMatch> Matches, int TotalLines, bool Truncated, bool IsBinary);

/// <summary>
/// Shared text search logic used by both Helix and AzDO log/file search operations.
/// Uses case-insensitive substring matching (no regex — prevents ReDoS).
/// </summary>
public static class TextSearchHelper
{
    /// <summary>Search lines for a pattern with optional context.</summary>
    public static LogSearchResult SearchLines(string identifier, string[] lines, string pattern, int contextLines = 0, int maxMatches = 50)
    {
        var matchIndices = new List<int>();

        for (int i = 0; i < lines.Length && matchIndices.Count < maxMatches; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                matchIndices.Add(i);
        }

        var matches = new List<LogMatch>();
        foreach (var idx in matchIndices)
        {
            List<string>? context = null;
            if (contextLines > 0)
            {
                int start = Math.Max(0, idx - contextLines);
                int end = Math.Min(lines.Length - 1, idx + contextLines);
                context = new List<string>();
                for (int j = start; j <= end; j++)
                    context.Add(lines[j]);
            }
            matches.Add(new LogMatch(idx + 1, lines[idx], context));
        }

        return new LogSearchResult(identifier, matches, lines.Length);
    }
}
