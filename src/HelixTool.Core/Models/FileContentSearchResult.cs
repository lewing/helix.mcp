namespace HelixTool.Core;

/// <summary>Result of searching an uploaded file's content.</summary>
public record FileContentSearchResult(string FileName, List<LogMatch> Matches, int TotalLines, bool Truncated, bool IsBinary);
