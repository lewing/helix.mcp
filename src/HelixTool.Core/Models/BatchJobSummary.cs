namespace HelixTool.Core;

/// <summary>Summary for multiple jobs.</summary>
public record BatchJobSummary(IReadOnlyList<JobSummary> Jobs, int TotalFailed, int TotalPassed);
