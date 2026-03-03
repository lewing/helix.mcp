using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HelixTool.Core;

[McpServerToolType]
public sealed class HelixMcpTools
{
    private readonly HelixService _svc;

    public HelixMcpTools(HelixService svc)
    {
        _svc = svc;
    }

    [McpServerTool(Name = "hlx_status", Title = "Helix Job Status", ReadOnly = true, UseStructuredContent = true), Description("Get work item pass/fail summary for a Helix job. Returns structured JSON with job metadata, failed items (with exit codes, state, duration, machine, failureCategory), and passed count. Use the 'filter' parameter to control which work items are included: 'failed' (default), 'passed', or 'all'.")]
    public async Task<StatusResult> Status(
        [Description("Helix job ID (GUID) or full Helix URL")] string jobId,
        [Description("Filter: 'failed' (default) shows only failures, 'passed' shows only passed, 'all' shows everything")] string filter = "failed")
    {
        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("passed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid filter '{filter}'. Must be 'failed', 'passed', or 'all'.", nameof(filter));
        }

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

    [McpServerTool(Name = "hlx_logs", Title = "Helix Work Item Logs", ReadOnly = true), Description("Get console log content for a Helix work item. Returns the log text directly (last N lines if tail specified).")]
    public async Task<string> Logs(
        [Description("Helix job ID (GUID), Helix job URL, or full work item URL (which includes both job ID and work item name)")] string jobId,
        [Description("Work item name (optional if included in the jobId URL)")] string? workItem = null,
        [Description("Number of lines from the end to return (default: all)")] int? tail = 500)
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

        return await _svc.GetConsoleLogContentAsync(jobId, workItem, tail);
    }

    [McpServerTool(Name = "hlx_files", Title = "Helix Work Item Files", ReadOnly = true, UseStructuredContent = true), Description("List uploaded files for a Helix work item, grouped by type. Returns binlogs, testResults, and other files with names and URIs.")]
    public async Task<FilesResult> Files(
        [Description("Helix job ID (GUID), Helix job URL, or full work item URL (which includes both job ID and work item name)")] string jobId,
        [Description("Work item name (optional if included in the jobId URL)")] string? workItem = null)
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

        var files = await _svc.GetWorkItemFilesAsync(jobId, workItem);

        return new FilesResult
        {
            Binlogs = files.Where(f => HelixService.MatchesPattern(f.Name, "*.binlog")).Select(f => new FileInfo_ { Name = f.Name, Uri = f.Uri }).ToList(),
            TestResults = files.Where(f => HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new FileInfo_ { Name = f.Name, Uri = f.Uri }).ToList(),
            Other = files.Where(f => !HelixService.MatchesPattern(f.Name, "*.binlog") && !HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new FileInfo_ { Name = f.Name, Uri = f.Uri }).ToList()
        };
    }

    [McpServerTool(Name = "hlx_download", Title = "Download Helix Files", Idempotent = true, UseStructuredContent = true), Description("Download files from a Helix work item to temp directory. Returns local file paths. Use pattern to filter (e.g., '*.binlog').")]
    public async Task<DownloadResult> Download(
        [Description("Helix job ID (GUID), Helix job URL, or full work item URL (which includes both job ID and work item name)")] string jobId,
        [Description("Work item name (optional if included in the jobId URL)")] string? workItem = null,
        [Description("File name or glob pattern (e.g., *.binlog). Default: all files")] string pattern = "*")
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

        var paths = await _svc.DownloadFilesAsync(jobId, workItem, pattern);

        if (paths.Count == 0)
            throw new McpException($"No files matching '{pattern}' found.");

        return new DownloadResult { DownloadedFiles = paths };
    }

    [McpServerTool(Name = "hlx_find_files", Title = "Find Files in Helix Job", ReadOnly = true, UseStructuredContent = true), Description("Search work items in a Helix job for files matching a pattern. Returns work item names and matching file URIs. Use pattern like '*.binlog', '*.trx', '*.dmp', or '*' for all files.")]
    public async Task<FindFilesResult> FindFiles(
        [Description("Helix job ID (GUID) or URL")] string jobId,
        [Description("File name or glob pattern (e.g., *.binlog, *.trx, *.dmp). Default: all files")] string pattern = "*",
        [Description("Maximum work items to scan (default: 30)")] int maxItems = 30)
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

    [McpServerTool(Name = "hlx_find_binlogs", Title = "Find Binlogs in Helix Job", ReadOnly = true, UseStructuredContent = true), Description("Scan work items in a Helix job to find which ones contain binlog files. Returns work item names and binlog URIs.")]
    public async Task<FindFilesResult> FindBinlogs(
        [Description("Helix job ID (GUID) or URL")] string jobId,
        [Description("Maximum work items to scan (default: 30)")] int maxItems = 30)
        => await FindFiles(jobId, "*.binlog", maxItems);

    [McpServerTool(Name = "hlx_download_url", Title = "Download File by URL", Idempotent = true, UseStructuredContent = true), Description("Download a file by direct URL (e.g., blob storage URI from hlx_files output). Returns the local file path.")]
    public async Task<DownloadUrlResult> DownloadUrl(
        [Description("Direct file URL to download")] string url)
    {
        var path = await _svc.DownloadFromUrlAsync(url);
        return new DownloadUrlResult { DownloadedFile = path };
    }

    [McpServerTool(Name = "hlx_work_item", Title = "Helix Work Item Details", ReadOnly = true, UseStructuredContent = true), Description("Get detailed info about a specific work item including exit code, state, machine, duration, files, and console log URL.")]
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

    [McpServerTool(Name = "hlx_search_log", Title = "Search Helix Console Log", ReadOnly = true, UseStructuredContent = true), Description("Search a work item's console log for lines matching a pattern. Returns matching lines with optional context. Use this to find specific errors, stack traces, or patterns in Helix test output.")]
    public async Task<SearchLogResult> SearchLog(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context before and after each match")] int contextLines = 2,
        [Description("Maximum number of matches to return")] int maxMatches = 50)
    {
        if (HelixService.IsFileSearchDisabled)
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

        var result = await _svc.SearchConsoleLogAsync(jobId, workItem, pattern, contextLines, maxMatches);

        return new SearchLogResult
        {
            WorkItem = result.WorkItem,
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

    [McpServerTool(Name = "hlx_search_file", Title = "Search Helix File", ReadOnly = true, UseStructuredContent = true), Description("Search a work item's uploaded file for lines matching a pattern. Returns matching lines with optional context. Use this to find specific errors, stack traces, or patterns in Helix test output files without downloading them.")]
    public async Task<SearchFileResult> SearchFile(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("File name to search (exact name from hlx_files output)")] string fileName,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context before and after each match")] int contextLines = 2,
        [Description("Maximum number of matches to return")] int maxMatches = 50)
    {
        if (HelixService.IsFileSearchDisabled)
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

        var result = await _svc.SearchFileAsync(jobId, workItem, fileName, pattern, contextLines, maxMatches);

        if (result.IsBinary)
            throw new McpException($"File '{fileName}' appears to be binary and cannot be searched.");

        return new SearchFileResult
        {
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

    [McpServerTool(Name = "hlx_test_results", Title = "Parse TRX Test Results", ReadOnly = true, UseStructuredContent = true), Description("Parse TRX test result files from a Helix work item. Returns structured test results including test names, outcomes, durations, and error messages for failed tests. Auto-discovers all .trx files or filter to a specific one.")]
    public async Task<TestResultsToolResult> TestResults(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Specific TRX file name (optional - auto-discovers all .trx files if not set)")] string? fileName = null,
        [Description("Include passed tests in output (default: false)")] bool includePassed = false,
        [Description("Maximum number of test results to return (default: 200)")] int maxResults = 200)
    {
        if (HelixService.IsFileSearchDisabled)
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

        var trxResults = await _svc.ParseTrxResultsAsync(jobId, workItem, fileName, includePassed, maxResults);

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

    [McpServerTool(Name = "hlx_batch_status", Title = "Batch Helix Job Status", ReadOnly = true, UseStructuredContent = true), Description("Get status for multiple Helix jobs at once. Returns per-job summaries and overall totals. Maximum 50 jobs per request.")]
    public async Task<BatchStatusResult> BatchStatus(
        [Description("Helix job IDs (GUIDs) or URLs")] string[] jobIds)
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
}
