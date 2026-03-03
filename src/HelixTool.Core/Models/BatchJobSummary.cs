namespace HelixTool.Core;

/// <summary>Summary for multiple jobs.</summary>
public record BatchJobSummary(List<JobSummary> Jobs, int TotalFailed, int TotalPassed);
