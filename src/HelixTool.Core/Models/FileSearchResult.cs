namespace HelixTool.Core;

/// <summary>A work item and the matching files it contains.</summary>
public record FileSearchResult(string WorkItem, List<FileEntry> Files);
