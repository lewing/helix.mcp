namespace HelixTool.Core;

/// <summary>Detailed information about a single work item.</summary>
public record WorkItemDetail(
    string Name, int ExitCode, string? State, string? MachineName,
    TimeSpan? Duration, string ConsoleLogUrl, IReadOnlyList<FileEntry> Files, FailureCategory? FailureCategory);
