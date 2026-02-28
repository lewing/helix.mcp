using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace HelixTool.Core;

[McpServerToolType]
public sealed class HelixMcpTools
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HelixService _svc;

    public HelixMcpTools(HelixService svc)
    {
        _svc = svc;
    }

    [McpServerTool(Name = "hlx_status"), Description("Get work item pass/fail summary for a Helix job. Returns structured JSON with job metadata, failed items (with exit codes, state, duration, machine, failureCategory), and passed count. Use the 'filter' parameter to control which work items are included: 'failed' (default), 'passed', or 'all'.")]
    public async Task<string> Status(
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

        var result = new
        {
            job = new { jobId = summary.JobId, summary.Name, summary.QueueId, summary.Creator, summary.Source, summary.Created, summary.Finished, helixUrl = $"https://helix.dot.net/api/jobs/{summary.JobId}/details" },
            totalWorkItems = summary.TotalCount,
            failedCount = summary.Failed.Count,
            passedCount = summary.Passed.Count,
            failed = showFailed ? summary.Failed.Select(f => new { f.Name, f.ExitCode, f.State, f.MachineName, duration = FormatDuration(f.Duration), consoleLogUrl = f.ConsoleLogUrl, failureCategory = f.FailureCategory?.ToString() }) : null,
            passed = showPassed ? summary.Passed.Select(p => new { p.Name, p.ExitCode, p.State, p.MachineName, duration = FormatDuration(p.Duration), consoleLogUrl = p.ConsoleLogUrl, failureCategory = (string?)null }) : null
        };

        return JsonSerializer.Serialize(result, s_jsonOptions);
    }

    private static string? FormatDuration(TimeSpan? duration)
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

    [McpServerTool(Name = "hlx_logs"), Description("Get console log content for a Helix work item. Returns the log text directly (last N lines if tail specified).")]
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
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        return await _svc.GetConsoleLogContentAsync(jobId, workItem, tail);
    }

    [McpServerTool(Name = "hlx_files"), Description("List uploaded files for a Helix work item, grouped by type. Returns binlogs, testResults, and other files with names and URIs.")]
    public async Task<string> Files(
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
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var files = await _svc.GetWorkItemFilesAsync(jobId, workItem);

        var result = new
        {
            binlogs = files.Where(f => HelixService.MatchesPattern(f.Name, "*.binlog")).Select(f => new { f.Name, f.Uri }),
            testResults = files.Where(f => HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri }),
            other = files.Where(f => !HelixService.MatchesPattern(f.Name, "*.binlog") && !HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri })
        };

        return JsonSerializer.Serialize(result, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_download"), Description("Download files from a Helix work item to temp directory. Returns local file paths. Use pattern to filter (e.g., '*.binlog').")]
    public async Task<string> Download(
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
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var paths = await _svc.DownloadFilesAsync(jobId, workItem, pattern);

        if (paths.Count == 0)
            return JsonSerializer.Serialize(new { error = $"No files matching '{pattern}' found." });

        return JsonSerializer.Serialize(new { downloadedFiles = paths });
    }

    [McpServerTool(Name = "hlx_find_files"), Description("Search work items in a Helix job for files matching a pattern. Returns work item names and matching file URIs. Use pattern like '*.binlog', '*.trx', '*.dmp', or '*' for all files.")]
    public async Task<string> FindFiles(
        [Description("Helix job ID (GUID) or URL")] string jobId,
        [Description("File name or glob pattern (e.g., *.binlog, *.trx, *.dmp). Default: all files")] string pattern = "*",
        [Description("Maximum work items to scan (default: 30)")] int maxItems = 30)
    {
        var results = await _svc.FindFilesAsync(jobId, pattern, maxItems);
        var output = results.Select(r => new
        {
            workItem = r.WorkItem,
            files = r.Files.Select(f => new { f.Name, f.Uri })
        });
        return JsonSerializer.Serialize(new { pattern, scannedItems = maxItems, found = results.Count, results = output }, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_find_binlogs"), Description("Scan work items in a Helix job to find which ones contain binlog files. Returns work item names and binlog URIs.")]
    public async Task<string> FindBinlogs(
        [Description("Helix job ID (GUID) or URL")] string jobId,
        [Description("Maximum work items to scan (default: 30)")] int maxItems = 30)
        => await FindFiles(jobId, "*.binlog", maxItems);

    [McpServerTool(Name = "hlx_download_url"), Description("Download a file by direct URL (e.g., blob storage URI from hlx_files output). Returns the local file path.")]
    public async Task<string> DownloadUrl(
        [Description("Direct file URL to download")] string url)
    {
        var path = await _svc.DownloadFromUrlAsync(url);
        return JsonSerializer.Serialize(new { downloadedFile = path }, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_work_item"), Description("Get detailed info about a specific work item including exit code, state, machine, duration, files, and console log URL.")]
    public async Task<string> WorkItem(
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
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var detail = await _svc.GetWorkItemDetailAsync(jobId, workItem);

        var result = new
        {
            detail.Name,
            detail.ExitCode,
            detail.State,
            detail.MachineName,
            duration = FormatDuration(detail.Duration),
            detail.ConsoleLogUrl,
            failureCategory = detail.FailureCategory?.ToString(),
            files = detail.Files.Select(f => new { f.Name, f.Uri })
        };

        return JsonSerializer.Serialize(result, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_search_log"), Description("Search a work item's console log for lines matching a pattern. Returns matching lines with optional context. Use this to find specific errors, stack traces, or patterns in Helix test output.")]
    public async Task<string> SearchLog(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context before and after each match")] int contextLines = 2,
        [Description("Maximum number of matches to return")] int maxMatches = 50)
    {
        if (HelixService.IsFileSearchDisabled)
            return JsonSerializer.Serialize(new { error = "File content search is disabled by configuration." }, s_jsonOptions);

        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var result = await _svc.SearchConsoleLogAsync(jobId, workItem, pattern, contextLines, maxMatches);

        var output = new
        {
            workItem = result.WorkItem,
            pattern,
            totalLines = result.TotalLines,
            matchCount = result.Matches.Count,
            matches = result.Matches.Select(m => new
            {
                lineNumber = m.LineNumber,
                line = m.Line,
                context = m.Context
            })
        };

        return JsonSerializer.Serialize(output, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_search_file"), Description("Search a work item's uploaded file for lines matching a pattern. Returns matching lines with optional context. Use this to find specific errors, stack traces, or patterns in Helix test output files without downloading them.")]
    public async Task<string> SearchFile(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("File name to search (exact name from hlx_files output)")] string fileName,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Text pattern to search for (case-insensitive)")] string pattern = "error",
        [Description("Lines of context before and after each match")] int contextLines = 2,
        [Description("Maximum number of matches to return")] int maxMatches = 50)
    {
        if (HelixService.IsFileSearchDisabled)
            return JsonSerializer.Serialize(new { error = "File content search is disabled by configuration." }, s_jsonOptions);

        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var result = await _svc.SearchFileAsync(jobId, workItem, fileName, pattern, contextLines, maxMatches);

        if (result.IsBinary)
            return JsonSerializer.Serialize(new { error = $"File '{fileName}' appears to be binary and cannot be searched." }, s_jsonOptions);

        var output = new
        {
            fileName = result.FileName,
            pattern,
            totalLines = result.TotalLines,
            matchCount = result.Matches.Count,
            truncated = result.Truncated,
            matches = result.Matches.Select(m => new
            {
                lineNumber = m.LineNumber,
                line = m.Line,
                context = m.Context
            })
        };

        return JsonSerializer.Serialize(output, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_test_results"), Description("Parse TRX test result files from a Helix work item. Returns structured test results including test names, outcomes, durations, and error messages for failed tests. Auto-discovers all .trx files or filter to a specific one.")]
    public async Task<string> TestResults(
        [Description("Helix job ID (GUID), Helix URL, or full work item URL")] string jobId,
        [Description("Work item name (optional if included in jobId URL)")] string? workItem = null,
        [Description("Specific TRX file name (optional - auto-discovers all .trx files if not set)")] string? fileName = null,
        [Description("Include passed tests in output (default: false)")] bool includePassed = false,
        [Description("Maximum number of test results to return (default: 200)")] int maxResults = 200)
    {
        if (HelixService.IsFileSearchDisabled)
            return JsonSerializer.Serialize(new { error = "File content search is disabled by configuration." }, s_jsonOptions);

        if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
        {
            if (!string.IsNullOrEmpty(resolvedWorkItem))
            {
                jobId = resolvedJobId;
                workItem = resolvedWorkItem;
            }
        }

        if (string.IsNullOrEmpty(workItem))
            return JsonSerializer.Serialize(new { error = "Work item name is required. Provide it as a separate parameter or include it in the Helix URL." }, s_jsonOptions);

        var trxResults = await _svc.ParseTrxResultsAsync(jobId, workItem, fileName, includePassed, maxResults);

        var output = new
        {
            workItem,
            fileCount = trxResults.Count,
            files = trxResults.Select(r => new
            {
                fileName = r.FileName,
                totalTests = r.TotalTests,
                passed = r.Passed,
                failed = r.Failed,
                skipped = r.Skipped,
                results = r.Results.Select(t => new
                {
                    testName = t.TestName,
                    outcome = t.Outcome,
                    duration = t.Duration,
                    computerName = t.ComputerName,
                    errorMessage = t.ErrorMessage,
                    stackTrace = t.StackTrace
                })
            })
        };

        return JsonSerializer.Serialize(output, s_jsonOptions);
    }

    [McpServerTool(Name = "hlx_batch_status"), Description("Get status for multiple Helix jobs at once. Returns per-job summaries and overall totals. Maximum 50 jobs per request.")]
    public async Task<string> BatchStatus(
        [Description("Helix job IDs (GUIDs) or URLs")] string[] jobIds)
    {
        var batch = await _svc.GetBatchStatusAsync(jobIds);

        var allFailed = batch.Jobs.SelectMany(j => j.Failed).Where(f => f.FailureCategory.HasValue).ToList();
        var failureBreakdown = allFailed.Count > 0
            ? allFailed.GroupBy(f => f.FailureCategory!.Value.ToString())
                .ToDictionary(g => g.Key, g => g.Count())
            : null;

        var result = new
        {
            jobs = batch.Jobs.Select(j => new
            {
                jobId = j.JobId,
                j.Name,
                failedCount = j.Failed.Count,
                passedCount = j.Passed.Count,
                totalCount = j.TotalCount
            }),
            batch.TotalFailed,
            batch.TotalPassed,
            jobCount = batch.Jobs.Count,
            failureBreakdown
        };

        return JsonSerializer.Serialize(result, s_jsonOptions);
    }
}
