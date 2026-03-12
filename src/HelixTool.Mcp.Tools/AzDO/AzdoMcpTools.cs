using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

using HelixTool.Core;
using HelixTool.Core.AzDO;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class AzdoMcpTools
{
    private readonly AzdoService _svc;

    public AzdoMcpTools(AzdoService svc)
    {
        _svc = svc;
    }

    [McpServerTool(Name = "azdo_build", Title = "AzDO Build Details", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get details of an AzDO build: status, result, definition, source branch, timing, and web URL. Accepts build URL or integer ID.")]
    public async Task<AzdoBuildSummary> Build(
        [Description("AzDO build ID or full build URL")] string buildId)
    {
        try
        {
            return await _svc.GetBuildSummaryAsync(buildId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get build details: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_builds", Title = "AzDO Build List", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List recent builds for an AzDO project. Filter by PR, branch, or definition. Defaults to dnceng-public/public.")]
    public async Task<IReadOnlyList<AzdoBuild>> Builds(
        [Description("Azure DevOps organization")] string org = "dnceng-public",
        [Description("Azure DevOps project")] string project = "public",
        [Description("Maximum results to return")] int top = 10,
        [Description("Filter by branch name (e.g., 'refs/heads/main')")] string? branch = null,
        [Description("Filter by pull request number")] string? prNumber = null,
        [Description("Filter by pipeline definition ID")] int? definitionId = null,
        [Description("Filter by build status")] string? status = null)
    {
        var filter = new AzdoBuildFilter
        {
            PrNumber = prNumber,
            Branch = branch,
            DefinitionId = definitionId,
            Top = top,
            StatusFilter = status
        };

        try
        {
            return await _svc.ListBuildsAsync(org, project, filter);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to list builds: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_timeline", Title = "AzDO Build Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Build timeline with stages, jobs, and tasks — state, result, timing, log refs, issues. Find failed steps and log IDs for azdo_log. Filter: 'failed' (default) or 'all'.")]
    public async Task<AzdoTimeline?> Timeline(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Filter: 'failed' (default) or 'all'")] string filter = "failed")
    {
        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"Invalid filter '{filter}'. Must be 'failed' or 'all'.");
        }

        AzdoTimeline? timeline;
        try
        {
            timeline = await _svc.GetTimelineAsync(buildId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get build timeline: {ex.Message}", ex);
        }

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

    [McpServerTool(Name = "azdo_log", Title = "AzDO Build Log", ReadOnly = true, Idempotent = true),
     Description("Get log content for a build step. Use log ID from azdo_timeline. Returns last N lines by default.")]
    public async Task<string> Log(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Log ID from azdo_timeline")] int logId,
        [Description("Lines from end to return")] int? tailLines = 500)
    {
        string? content;
        try
        {
            content = await _svc.GetBuildLogAsync(buildId, logId, tailLines);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get build log: {ex.Message}", ex);
        }
        return content ?? string.Empty;
    }

    [McpServerTool(Name = "azdo_changes", Title = "AzDO Build Changes", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get commits/changes for an AzDO build. Returns commit IDs, messages, authors, and timestamps.")]
    public async Task<IReadOnlyList<AzdoBuildChange>> Changes(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Maximum results to return")] int top = 20)
    {
        try
        {
            return await _svc.GetBuildChangesAsync(buildId, top);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get build changes: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_test_runs", Title = "AzDO Test Runs", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Test run summaries for an AzDO build with total/passed/failed counts. ⚠️ Run-level failedTests can be inaccurate — always drill into azdo_test_results to verify.")]
    public async Task<IReadOnlyList<AzdoTestRun>> TestRuns(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Maximum results to return")] int top = 50)
    {
        try
        {
            return await _svc.GetTestRunsAsync(buildId, top);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get test runs: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_test_results", Title = "AzDO Test Results", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Test results for a specific run. Defaults to failed tests only. Primary tool for test failures in most dotnet repos (aspnetcore, sdk, roslyn, efcore).")]
    public async Task<IReadOnlyList<AzdoTestResult>> TestResults(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Test run ID from azdo_test_runs")] int runId,
        [Description("Maximum results to return")] int top = 200)
    {
        try
        {
            return await _svc.GetTestResultsAsync(buildId, runId, top);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get test results: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_artifacts", Title = "AzDO Build Artifacts", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List artifacts from an AzDO build (logs, test results, binlogs). Supports glob-style pattern filtering.")]
    public async Task<IReadOnlyList<AzdoBuildArtifact>> Artifacts(
        [Description("AzDO build ID or full build URL")] string buildId,
        [Description("Artifact name glob. Default: all")] string pattern = "*",
        [Description("Maximum results to return")] int top = 50)
    {
        try
        {
            return await _svc.GetBuildArtifactsAsync(buildId, pattern, top);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get build artifacts: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_search_log", Title = "Search AzDO Build Logs", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search build step logs for a pattern. Provide logId for one step, or omit it to search all ranked steps.")]
    public async Task<CrossStepSearchResult> SearchLog(
        [Description("AzDO build ID or full build URL")] string buildIdOrUrl,
        [Description("Log ID from azdo_timeline")] int? logId = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context around each match")] int contextLines = 2,
        [Description("Maximum matches to return")] int maxMatches = 50,
        [Description("Maximum log steps to search")] int maxLogsToSearch = 30,
        [Description("Minimum lines per log to search")] int minLogLines = 5)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        try
        {
            if (logId.HasValue)
            {
                var result = await _svc.SearchBuildLogAsync(buildIdOrUrl, logId.Value, pattern, contextLines, maxMatches);

                return new CrossStepSearchResult
                {
                    Build = buildIdOrUrl,
                    Pattern = pattern,
                    TotalLogsInBuild = 1,
                    LogsSearched = 1,
                    LogsSkipped = 0,
                    TotalMatchCount = result.Matches.Count,
                    StoppedEarly = false,
                    Steps =
                    [
                        new StepSearchResult
                        {
                            LogId = logId.Value,
                            StepName = $"Log {logId.Value}",
                            LineCount = result.TotalLines,
                            MatchCount = result.Matches.Count,
                            Matches = result.Matches
                        }
                    ]
                };
            }

            return await _svc.SearchBuildLogAcrossStepsAsync(
                buildIdOrUrl, pattern, contextLines, maxMatches, maxLogsToSearch, minLogLines);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to search build logs: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_search_timeline", Title = "AzDO Search Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search build timeline record names and issue messages for a pattern. Returns matching records with log IDs for azdo_log or azdo_search_log.")]
    public async Task<TimelineSearchResult> SearchTimeline(
        [Description("AzDO build ID or full build URL")] string buildIdOrUrl,
        [Description("Text pattern to search for (case-insensitive)")] string pattern,
        [Description("Filter by record type: 'Stage', 'Job', or 'Task'")] string? recordType = null,
        [Description("Filter: 'failed' (default) or 'all'")] string resultFilter = "failed")
    {
        try
        {
            return await _svc.SearchTimelineAsync(buildIdOrUrl, pattern, recordType, resultFilter);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to search timeline: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "azdo_test_attachments", Title = "AzDO Test Attachments", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List attachments for a test result (screenshots, logs, dumps). Requires run ID and result ID from azdo_test_results.")]
    public async Task<IReadOnlyList<AzdoTestAttachment>> TestAttachments(
        [Description("Test run ID from azdo_test_runs")] int runId,
        [Description("Test result ID from azdo_test_results")] int resultId,
        [Description("Azure DevOps project")] string project = "public",
        [Description("Azure DevOps organization")] string org = "dnceng-public",
        [Description("Maximum results to return")] int top = 50)
    {
        try
        {
            return await _svc.GetTestAttachmentsAsync(org, project, runId, resultId, top);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to get test attachments: {ex.Message}", ex);
        }
    }

}
