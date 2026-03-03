namespace HelixTool.Core;

/// <summary>Aggregated pass/fail summary for all work items in a Helix job.</summary>
public record JobSummary(
    string JobId, string Name, string QueueId, string Creator, string Source,
    string? Created, string? Finished,
    int TotalCount, IReadOnlyList<WorkItemResult> FailedItems, IReadOnlyList<WorkItemResult> PassedItems);
