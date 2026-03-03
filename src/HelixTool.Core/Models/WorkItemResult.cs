namespace HelixTool.Core;

/// <summary>Represents a single work item's name and exit code.</summary>
public record WorkItemResult(string Name, int ExitCode, string? State, string? MachineName, TimeSpan? Duration, string ConsoleLogUrl, FailureCategory? FailureCategory);
