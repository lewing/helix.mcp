using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HelixTool.Core.AzDO;

[McpServerToolType]
public sealed class AzdoMcpTools
{
    private readonly AzdoService _svc;

    public AzdoMcpTools(AzdoService svc)
    {
        _svc = svc;
    }

    [McpServerTool(Name = "azdo_build", Title = "AzDO Build Details", ReadOnly = true, UseStructuredContent = true),
     Description("Get details of a specific Azure DevOps build. Returns build metadata including status, result, definition, source branch, timing, and a direct web URL. Use to investigate build failures, check build status, or get build context. Accepts a build URL or plain integer ID.")]
    public async Task<AzdoBuildSummary> Build(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId)
    {
        return await _svc.GetBuildSummaryAsync(buildId);
    }

    [McpServerTool(Name = "azdo_builds", Title = "AzDO Build List", ReadOnly = true, UseStructuredContent = true),
     Description("List recent builds for an Azure DevOps project. Returns build summaries with status, result, branch, and timing. Use to find builds for a PR, branch, or pipeline definition. Defaults to the dnceng-public/public project.")]
    public async Task<IReadOnlyList<AzdoBuild>> Builds(
        [Description("Azure DevOps organization (default: dnceng-public)")] string org = "dnceng-public",
        [Description("Azure DevOps project (default: public)")] string project = "public",
        [Description("Maximum number of builds to return (default: 10)")] int top = 10,
        [Description("Filter by branch name (e.g., 'refs/heads/main')")] string? branch = null,
        [Description("Filter by pull request number")] string? prNumber = null,
        [Description("Filter by pipeline definition ID")] int? definitionId = null,
        [Description("Filter by build status (e.g., 'completed', 'inProgress', 'cancelling')")] string? status = null)
    {
        var filter = new AzdoBuildFilter
        {
            PrNumber = prNumber,
            Branch = branch,
            DefinitionId = definitionId,
            Top = top,
            StatusFilter = status
        };

        return await _svc.ListBuildsAsync(org, project, filter);
    }

    [McpServerTool(Name = "azdo_timeline", Title = "AzDO Build Timeline", ReadOnly = true, UseStructuredContent = true),
     Description("Get the build timeline showing stages, jobs, and tasks for an Azure DevOps build. Returns hierarchical timeline records with state, result, timing, log references, and issues. Use the 'filter' parameter to control which records are returned. Use to drill into which stage/job/task failed and find log IDs for azdo_log.")]
    public async Task<AzdoTimeline?> Timeline(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Filter: 'failed' (default) shows only non-succeeded records, 'all' shows everything")] string filter = "failed")
    {
        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid filter '{filter}'. Must be 'failed' or 'all'.", nameof(filter));
        }

        var timeline = await _svc.GetTimelineAsync(buildId);

        if (timeline is null || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return timeline;

        // Filter to non-succeeded records: failed, canceled, partially succeeded, or with issues
        var failedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in timeline.Records)
        {
            if (r.Id is null) continue;
            var isFailed = r.Result is not null &&
                !r.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
            var hasIssues = r.Issues is { Count: > 0 };
            if (isFailed || hasIssues)
                failedIds.Add(r.Id);
        }

        // Include parent records for context (walk up parentId chain)
        var allIds = new HashSet<string>(failedIds, StringComparer.OrdinalIgnoreCase);
        var recordById = timeline.Records
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

        foreach (var id in failedIds)
        {
            var current = recordById.GetValueOrDefault(id);
            while (current?.ParentId is not null && allIds.Add(current.ParentId))
            {
                current = recordById.GetValueOrDefault(current.ParentId);
            }
        }

        var filtered = timeline.Records.Where(r => r.Id is not null && allIds.Contains(r.Id)).ToList();
        return new AzdoTimeline { Id = timeline.Id, Records = filtered };
    }

    [McpServerTool(Name = "azdo_log", Title = "AzDO Build Log", ReadOnly = true),
     Description("Get log content for a specific build log. Returns plain text log output. Use after azdo_timeline to read the log of a failed task (use the log ID from the timeline record). Returns last N lines by default to prevent context overflow.")]
    public async Task<string> Log(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Log ID from the timeline record's log reference")] int logId,
        [Description("Number of lines from the end to return (default: 500)")] int? tailLines = 500)
    {
        var content = await _svc.GetBuildLogAsync(buildId, logId, tailLines);
        return content ?? string.Empty;
    }

    [McpServerTool(Name = "azdo_changes", Title = "AzDO Build Changes", ReadOnly = true, UseStructuredContent = true),
     Description("Get the commits/changes associated with an Azure DevOps build. Returns commit IDs, messages, authors, and timestamps. Use to see what code changes triggered or are included in a build.")]
    public async Task<IReadOnlyList<AzdoBuildChange>> Changes(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Maximum number of changes to return (default: 20)")] int top = 20)
    {
        return await _svc.GetBuildChangesAsync(buildId, top);
    }

    [McpServerTool(Name = "azdo_test_runs", Title = "AzDO Test Runs", ReadOnly = true, UseStructuredContent = true),
     Description("Get test runs for an Azure DevOps build. Returns test run summaries with total, passed, and failed counts. Use to get an overview of test execution for a build before drilling into individual test results with azdo_test_results.")]
    public async Task<IReadOnlyList<AzdoTestRun>> TestRuns(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Maximum number of test runs to return (default: 50)")] int top = 50)
    {
        return await _svc.GetTestRunsAsync(buildId, top);
    }

    [McpServerTool(Name = "azdo_test_results", Title = "AzDO Test Results", ReadOnly = true, UseStructuredContent = true),
     Description("Get test results for a specific test run. Returns individual test case results including outcome, duration, and error details for failures. Defaults to showing only failed tests. Use after azdo_test_runs to investigate specific test failures.")]
    public async Task<IReadOnlyList<AzdoTestResult>> TestResults(
        [Description("AzDO build ID (integer) or full AzDO build URL — used to resolve org/project context")] string buildId,
        [Description("Test run ID from azdo_test_runs output")] int runId,
        [Description("Maximum number of test results to return (default: 200)")] int top = 200)
    {
        return await _svc.GetTestResultsAsync(buildId, runId, top);
    }

    [McpServerTool(Name = "azdo_artifacts", Title = "AzDO Build Artifacts", ReadOnly = true, UseStructuredContent = true),
     Description("List artifacts produced by an Azure DevOps build (logs, test results, binlogs, etc.). Returns artifact names, resource types, and download URLs. Use to discover what files a build published.")]
    public async Task<IReadOnlyList<AzdoBuildArtifact>> Artifacts(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId)
    {
        return await _svc.GetBuildArtifactsAsync(buildId);
    }

    [McpServerTool(Name = "azdo_test_attachments", Title = "AzDO Test Attachments", ReadOnly = true, UseStructuredContent = true),
     Description("List attachments for a specific test result (screenshots, logs, dumps). Use after azdo_test_results to get files attached to a failed test. Requires run ID and result ID from previous tool output.")]
    public async Task<IReadOnlyList<AzdoTestAttachment>> TestAttachments(
        [Description("Test run ID from azdo_test_runs output")] int runId,
        [Description("Test result ID from azdo_test_results output")] int resultId,
        [Description("Azure DevOps project (default: public)")] string project = "public",
        [Description("Azure DevOps organization (default: dnceng-public)")] string org = "dnceng-public",
        [Description("Maximum number of attachments to return (default: 50)")] int top = 50)
    {
        return await _svc.GetTestAttachmentsAsync(org, project, runId, resultId, top);
    }
}
