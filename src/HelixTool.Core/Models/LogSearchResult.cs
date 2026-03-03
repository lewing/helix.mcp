namespace HelixTool.Core;

/// <summary>Result of searching a console log.</summary>
public record LogSearchResult(string WorkItem, IReadOnlyList<LogMatch> Matches, int TotalLines);
