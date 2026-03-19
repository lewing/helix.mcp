using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Microsoft.DotNet.Helix.Client;

using HelixTool.Core;
using HelixTool.Core.Helix;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class HelixMcpTools
{
    private readonly HelixService _svc;
    private readonly IHelixTokenAccessor _tokenAccessor;

    public HelixMcpTools(HelixService svc, IHelixTokenAccessor tokenAccessor)
    {
        _svc = svc;
        _tokenAccessor = tokenAccessor;
    }

    [McpServerTool(Name = "helix_status", Title = "Helix Job Status", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Work item pass/fail summary for a Helix job. Returns failed items with exit codes, state, duration, machine. Filter: 'failed' (default), 'passed', or 'all'.")]
    public async Task<StatusResult> Status(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Filter: 'failed' (default), 'passed', or 'all'")] string filter = "failed")
    {
        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("passed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException($"Invalid filter '{filter}'. Must be 'failed', 'passed', or 'all'.");
        }

        try
        {
            var summary = await _svc.GetJobStatusAsync(jobId);

            var showFailed = filter.Equals("failed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);
            var showPassed = filter.Equals("passed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);

            return new StatusResult
            {
                Job = new StatusJobInfo
                {
                    JobId = summary.JobId,
                    Name = summary.Name,
                    QueueId = summary.QueueId,
                    Creator = summary.Creator,
                    Source = summary.Source,
                    Created = summary.Created,
                    Finished = summary.Finished,
                    HelixUrl = $"https://helix.dot.net/api/jobs/{summary.JobId}/details"
                },
                TotalWorkItems = summary.TotalCount,
                FailedCount = summary.Failed.Count,
                PassedCount = summary.Passed.Count,
                Failed = showFailed ? summary.Failed.Select(f => new StatusWorkItem
                {
                    Name = f.Name, ExitCode = f.ExitCode, State = f.State, MachineName = f.MachineName,
                    Duration = FormatDuration(f.Duration), ConsoleLogUrl = f.ConsoleLogUrl,
                    FailureCategory = f.FailureCategory?.ToString()
                }).ToList() : null,
                Passed = showPassed ? summary.Passed.Select(p => new StatusWorkItem
                {
                    Name = p.Name, ExitCode = p.ExitCode, State = p.State, MachineName = p.MachineName,
                    Duration = FormatDuration(p.Duration), ConsoleLogUrl = p.ConsoleLogUrl,
                    FailureCategory = null
                }).ToList() : null
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to get job status: {ex.Message}", ex);
        }
    }

    internal static string? FormatDuration(TimeSpan? duration)
    {
        if (!duration.HasValue) return null;
        var d = duration.Value;
        if (d.TotalHours >= 1)
        {
            var hours = (int)d.TotalHours;
            var minutes = d.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (d.TotalMinutes >= 1)
        {
            var minutes = (int)d.TotalMinutes;
            var seconds = d.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }
        return $"{(int)d.TotalSeconds}s";
    }

    [McpServerTool(Name = "helix_logs", Title = "Helix Work Item Logs", ReadOnly = true, Idempotent = true), Description("Get console log content for a Helix work item. Returns the log text directly (last N lines if tail specified).")]
    public async Task<string> Logs(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Lines from end to return (default: all)")] int? tail = 500)
    {
        // If workItem not provided, try to extract from jobId URL
        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

        try
        {
            return await _svc.GetConsoleLogContentAsync(jobId, workItem, tail);
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to get console log: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_files", Title = "Helix Work Item Files", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("List uploaded files for a Helix work item, grouped by type. Returns binlogs, testResults, and other files with names and URIs.")]
    public async Task<FilesResult> Files(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null)
    {
        // If workItem not provided, try to extract from jobId URL
        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

        try
        {
            var files = await _svc.GetWorkItemFilesAsync(jobId, workItem);

            var binlogs = new List<FileInfo_>();
            var testResults = new List<FileInfo_>();
            var other = new List<FileInfo_>();
            foreach (var f in files)
            {
                var info = new FileInfo_ { Name = f.Name, Uri = f.Uri };
                if (HelixService.MatchesPattern(f.Name, "*.binlog"))
                    binlogs.Add(info);
                else if (HelixService.IsTestResultFile(f.Name))
                    testResults.Add(info);
                else
                    other.Add(info);
            }

            return new FilesResult
            {
                Binlogs = binlogs,
                TestResults = testResults,
                Other = other
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to get work item files: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_download", Title = "Download Helix Files or URL", Destructive = false, Idempotent = true, UseStructuredContent = true), Description("Download Helix files by pattern or direct URL. Returns local file paths.")]
    public async Task<DownloadResult> Download(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string? jobId = null,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("File name or glob pattern (e.g., *.binlog). Default: all files")] string pattern = "*",
        [Description("Direct file URL to download (bypasses jobId/workItem)")] string? url = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                var path = await _svc.DownloadFromUrlAsync(url);
                return new DownloadResult { DownloadedFiles = [path] };
            }

            if (string.IsNullOrWhiteSpace(jobId))
                throw new McpException("Job ID is required unless url is provided.");

            // If workItem not provided, try to extract from jobId URL
            if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
            {
                if (!string.IsNullOrEmpty(resolvedWorkItem))
                {
                    jobId = resolvedJobId;
                    workItem = resolvedWorkItem;
                }
            }

            if (string.IsNullOrEmpty(workItem))
                throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

            var paths = await _svc.DownloadFilesAsync(jobId, workItem, pattern);

            if (paths.Count == 0)
                throw new McpException($"No files matching '{pattern}' found.");

            return new DownloadResult { DownloadedFiles = paths };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to download files: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_find_files", Title = "Find Files in Helix Job", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Search work items in a Helix job for files matching a glob pattern. Returns work item names and matching file URIs.")]
    public async Task<FindFilesResult> FindFiles(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("File name or glob pattern (e.g., *.binlog). Default: all files")] string pattern = "*",
        [Description("Maximum work items to scan. Default: 50")] int maxItems = 50)
    {
        try
        {
            var results = await _svc.FindFilesAsync(jobId, pattern, maxItems);

            return new FindFilesResult
            {
                Pattern = pattern,
                ScannedItems = maxItems,
                Found = results.Count,
                Results = results.Select(r => new FindFilesWorkItem
                {
                    WorkItem = r.WorkItem,
                    Files = r.Files.Select(f => new FileInfo_ { Name = f.Name, Uri = f.Uri }).ToList()
                }).ToList()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to find files: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_work_item", Title = "Helix Work Item Details", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Get detailed info about a specific work item including exit code, state, machine, duration, files, and console log URL.")]
    public async Task<WorkItemToolResult> WorkItem(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null)
    {
        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

        try
        {
            var detail = await _svc.GetWorkItemDetailAsync(jobId, workItem);

            return new WorkItemToolResult
            {
                Name = detail.Name,
                ExitCode = detail.ExitCode,
                State = detail.State,
                MachineName = detail.MachineName,
                Duration = FormatDuration(detail.Duration),
                ConsoleLogUrl = detail.ConsoleLogUrl,
                FailureCategory = detail.FailureCategory?.ToString(),
                Files = detail.Files.Select(f => new FileInfo_ { Name = f.Name, Uri = f.Uri }).ToList()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to get work item details: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_search", Title = "Search Helix Work Item Content", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Search a Helix work item's console log or uploaded file for case-insensitive text matches with context.")]
    public async Task<SearchResult> SearchLog(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("File name to search (from helix_files). Omit for console log.")] string? fileName = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context around each match")] int contextLines = 2,
        [Description("Maximum matches to return. Default: 100")] int maxMatches = 100)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

        try
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                var result = await _svc.SearchFileAsync(jobId, workItem, fileName, pattern, contextLines, maxMatches);

                if (result.IsBinary)
                    throw new McpException($"File '{fileName}' appears to be binary and cannot be searched.");

                return new SearchResult
                {
                    WorkItem = workItem,
                    FileName = result.FileName,
                    Pattern = pattern,
                    TotalLines = result.TotalLines,
                    MatchCount = result.Matches.Count,
                    Truncated = result.Truncated,
                    Matches = result.Matches.Select(m => new SearchMatch
                    {
                        LineNumber = m.LineNumber,
                        Line = m.Line,
                        Context = m.Context
                    }).ToList()
                };
            }

            var logResult = await _svc.SearchConsoleLogAsync(jobId, workItem, pattern, contextLines, maxMatches);

            return new SearchResult
            {
                WorkItem = logResult.WorkItem,
                Pattern = pattern,
                TotalLines = logResult.TotalLines,
                MatchCount = logResult.Matches.Count,
                Truncated = logResult.Truncated,
                Matches = logResult.Matches.Select(m => new SearchMatch
                {
                    LineNumber = m.LineNumber,
                    Line = m.Line,
                    Context = m.Context
                }).ToList()
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to search: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_parse_uploaded_trx", Title = "Parse Uploaded TRX from Helix", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Parse TRX/xUnit XML files from Helix blob storage. Niche — most repos use azdo_test_results instead.")]
    public async Task<TestResultsToolResult> TestResults(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Result file name (optional; auto-discovered)")] string? fileName = null,
        [Description("Include passed tests (default: false)")] bool includePassed = false,
        [Description("Maximum results to return")] int maxResults = 200)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            throw new McpException("Work item name is required. Provide it as a separate parameter or include it in the Helix URL.");

        List<HelixService.TrxParseResult> trxResults;
        try
        {
            trxResults = await _svc.ParseTrxResultsAsync(jobId, workItem, fileName, includePassed, maxResults);
        }
        catch (HelixException ex)
        {
            throw new McpException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to parse test results: {ex.Message}", ex);
        }

        return new TestResultsToolResult
        {
            WorkItem = workItem,
            FileCount = trxResults.Count,
            Files = trxResults.Select(r => new TestResultFile
            {
                FileName = r.FileName,
                TotalTests = r.TotalTests,
                Passed = r.Passed,
                Failed = r.Failed,
                Skipped = r.Skipped,
                Results = r.Results.Select(t => new TestResultEntry
                {
                    TestName = t.TestName,
                    Outcome = t.Outcome,
                    Duration = t.Duration,
                    ComputerName = t.ComputerName,
                    ErrorMessage = t.ErrorMessage,
                    StackTrace = t.StackTrace
                }).ToList()
            }).ToList()
        };
    }

    [McpServerTool(Name = "helix_batch_status", Title = "Batch Helix Job Status", ReadOnly = true, Idempotent = true, UseStructuredContent = true), Description("Get status for multiple Helix jobs at once. Returns per-job summaries and overall totals. Maximum 50 jobs per request.")]
    public async Task<BatchStatusResult> BatchStatus(
        [Description("Helix job IDs (GUIDs) or URLs")] string[] jobIds)
    {
        try
        {
            var batch = await _svc.GetBatchStatusAsync(jobIds);

            var allFailed = batch.Jobs.SelectMany(j => j.Failed).Where(f => f.FailureCategory.HasValue).ToList();
            var failureBreakdown = allFailed.Count > 0
                ? allFailed.GroupBy(f => f.FailureCategory!.Value.ToString())
                    .ToDictionary(g => g.Key, g => g.Count())
                : null;

            return new BatchStatusResult
            {
                Jobs = batch.Jobs.Select(j => new BatchJobEntry
                {
                    JobId = j.JobId,
                    Name = j.Name,
                    FailedCount = j.Failed.Count,
                    PassedCount = j.Passed.Count,
                    TotalCount = j.TotalCount
                }).ToList(),
                TotalFailed = batch.TotalFailed,
                TotalPassed = batch.TotalPassed,
                JobCount = batch.Jobs.Count,
                FailureBreakdown = failureBreakdown
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or HelixException or RestApiException or InvalidOperationException or ArgumentException)
        {
            throw new McpException($"Failed to get batch status: {ex.Message}", ex);
        }
    }

    [McpServerTool(Name = "helix_auth_status", Title = "Helix Auth Status", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Current Helix auth status: authenticated or anonymous, and token source (env var, stored credential).")]
    public HelixAuthStatus HelixAuth()
    {
        var token = _tokenAccessor.GetAccessToken();
        var hasToken = !string.IsNullOrEmpty(token);

        string source;
        if (_tokenAccessor is ChainedHelixTokenAccessor chained)
        {
            source = chained.Source switch
            {
                TokenSource.EnvironmentVariable => "HELIX_ACCESS_TOKEN environment variable",
                TokenSource.StoredCredential => "stored credential (git credential)",
                _ => "none"
            };
        }
        else if (hasToken)
        {
            source = _tokenAccessor.GetType().Name;
        }
        else
        {
            source = "none";
        }

        return new HelixAuthStatus
        {
            IsAuthenticated = hasToken,
            Source = source
        };
    }
}

public sealed record HelixAuthStatus
{
    [JsonPropertyName("isAuthenticated")]
    public bool IsAuthenticated { get; init; }

    [JsonPropertyName("source")]
    public string Source { get; init; } = "none";
}
