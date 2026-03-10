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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId)
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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Filter: 'failed' (default) shows only non-succeeded records, 'all' shows everything")] string filter = "failed")
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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Log ID from the timeline record's log reference")] int logId,
        [Description("Number of lines from the end to return (default: 500)")] int? tailLines = 500)
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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Maximum number of changes to return (default: 20)")] int top = 20)
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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Maximum number of test runs to return (default: 50)")] int top = 50)
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
        [Description("AzDO build ID (integer) or full AzDO build URL — used to resolve org/project context")] string buildId,
        [Description("Test run ID from azdo_test_runs output")] int runId,
        [Description("Maximum number of test results to return (default: 200)")] int top = 200)
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
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildId,
        [Description("Filter artifacts by name using glob-style matching (e.g., '*.binlog', '*.trx', or '*' for all). Default: all artifacts")] string pattern = "*",
        [Description("Maximum number of artifacts to return (default: 50)")] int top = 50)
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

    [McpServerTool(Name = "azdo_search_log", Title = "Search AzDO Build Log", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search a build step log for lines matching a pattern. Use log ID from azdo_timeline. For broad search, use azdo_search_log_across_steps.")]
    public async Task<SearchBuildLogResult> SearchLog(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildIdOrUrl,
        [Description("Log ID from the timeline record's log reference")] int logId,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context before and after each match")] int contextLines = 2,
        [Description("Maximum number of matches to return")] int maxMatches = 50)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        LogSearchResult result;
        try
        {
            result = await _svc.SearchBuildLogAsync(buildIdOrUrl, logId, pattern, contextLines, maxMatches);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to search build log: {ex.Message}", ex);
        }

        return new SearchBuildLogResult
        {
            Build = buildIdOrUrl,
            LogId = logId,
            Pattern = pattern,
            TotalLines = result.TotalLines,
            MatchCount = result.Matches.Count,
            Matches = result.Matches.Select(m => new SearchMatch
            {
                LineNumber = m.LineNumber,
                Line = m.Line,
                Context = m.Context
            }).ToList()
        };
    }

    [McpServerTool(Name = "azdo_search_timeline", Title = "AzDO Search Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search build timeline record names and issue messages for a pattern. Returns matching records with log IDs for azdo_log or azdo_search_log.")]
    public async Task<TimelineSearchResult> SearchTimeline(
        [Description("AzDO build ID (integer) or full AzDO build URL (https://dev.azure.com/...)")] string buildIdOrUrl,
        [Description("Text pattern to search for in record names and issue messages (case-insensitive)")] string pattern,
        [Description("Filter by record type: 'Stage', 'Job', or 'Task'. Omit for all types")] string? recordType = null,
        [Description("Result filter: 'failed' (default) shows non-succeeded records and any records with timeline issues; 'all' shows everything")] string resultFilter = "failed")
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
        [Description("Test run ID from azdo_test_runs output")] int runId,
        [Description("Test result ID from azdo_test_results output")] int resultId,
        [Description("Azure DevOps project (default: public)")] string project = "public",
        [Description("Azure DevOps organization (default: dnceng-public)")] string org = "dnceng-public",
        [Description("Maximum number of attachments to return (default: 50)")] int top = 50)
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

    [McpServerTool(Name = "azdo_search_log_across_steps",
                   Title = "Search All AzDO Build Logs",
                   ReadOnly = true,
                   Idempotent = true,
                   UseStructuredContent = true),
     Description("Search all log steps in a build for a pattern, ranked by failure likelihood. Stops early at maxMatches. For a single step, use azdo_search_log.")]
    public async Task<CrossStepSearchResult> SearchLogAcrossSteps(
        [Description("AzDO build ID (integer) or full AzDO build URL")] string buildIdOrUrl,
        [Description("Text pattern to search for (case-insensitive substring match)")] string pattern = "error",
        [Description("Lines of context before and after each match (default: 2)")] int contextLines = 2,
        [Description("Maximum total matches across all logs (default: 50). Search stops early once reached.")] int maxMatches = 50,
        [Description("Maximum number of individual log steps to download and search (default: 30). Limits API calls for very large builds.")] int maxLogsToSearch = 30,
        [Description("Minimum line count to include a log in the search (default: 5). Filters out tiny boilerplate logs.")] int minLogLines = 5)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        try
        {
            return await _svc.SearchBuildLogAcrossStepsAsync(
                buildIdOrUrl, pattern, contextLines, maxMatches, maxLogsToSearch, minLogLines);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or ArgumentException)
        {
            throw new McpException($"Failed to search build logs: {ex.Message}", ex);
        }
    }
}
