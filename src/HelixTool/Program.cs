using System.Text.Json;
using ConsoleAppFramework;
using HelixTool;
using HelixTool.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var services = new ServiceCollection();
services.AddSingleton<IHelixTokenAccessor>(_ =>
    new EnvironmentHelixTokenAccessor(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();
services.AddSingleton<CacheOptions>(sp =>
{
    var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
    var opts = new CacheOptions
    {
        AuthTokenHash = CacheOptions.ComputeTokenHash(token)
    };
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
    return opts;
});
services.AddSingleton<ICacheStore>(sp => new SqliteCacheStore(sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<HelixApiClient>(sp =>
{
    var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
    return new HelixApiClient(token);
});
services.AddSingleton<IHelixApiClient>(sp =>
    new CachingHelixApiClient(
        sp.GetRequiredService<HelixApiClient>(),
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<HelixService>();
ConsoleApp.ServiceProvider = services.BuildServiceProvider();

var app = ConsoleApp.Create();
app.Add<Commands>();
// Default to MCP server mode when no command is specified.
// This ensures `dnx lewing.helix.mcp --yes` works without an explicit "mcp" arg.
app.Run(args.Length == 0 ? ["mcp"] : args);

/// <summary>
/// CLI commands for interacting with .NET Helix test infrastructure.
/// </summary>
public class Commands
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly HelixService _svc;

    public Commands(HelixService svc)
    {
        _svc = svc;
    }

    /// <summary>Show work item summary for a Helix job.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="filter">Filter: 'failed' (default), 'passed', or 'all'.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [Command("status")]
    public async Task Status([Argument] string jobId, [Argument] string filter = "failed", bool json = false)
    {
        if (!filter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("passed", StringComparison.OrdinalIgnoreCase) &&
            !filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid filter '{filter}'. Must be 'failed', 'passed', or 'all'.", nameof(filter));
        }

        var showFailed = filter.Equals("failed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);
        var showPassed = filter.Equals("passed", StringComparison.OrdinalIgnoreCase) || filter.Equals("all", StringComparison.OrdinalIgnoreCase);

        Console.Error.Write("Fetching job details...");
        var summary = await _svc.GetJobStatusAsync(jobId);
        Console.Error.WriteLine(" done.");

        if (json)
        {
            var result = new
            {
                job = new { jobId = summary.JobId, summary.Name, summary.QueueId, summary.Creator, summary.Source, summary.Created, summary.Finished },
                totalWorkItems = summary.TotalCount,
                failedCount = summary.Failed.Count,
                passedCount = summary.Passed.Count,
                failed = showFailed ? summary.Failed.Select(f => new { f.Name, f.ExitCode, f.State, f.MachineName, duration = f.Duration?.ToString(), f.ConsoleLogUrl, failureCategory = f.FailureCategory?.ToString() }) : null,
                passed = showPassed ? summary.Passed.Select(p => new { p.Name, p.ExitCode, p.State, p.MachineName, duration = p.Duration?.ToString(), p.ConsoleLogUrl, failureCategory = (string?)null }) : null
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Job: {summary.Name}");
        Console.WriteLine($"Queue: {summary.QueueId}  Creator: {summary.Creator}  Source: {summary.Source}");
        Console.WriteLine($"Created: {summary.Created}  Finished: {summary.Finished}");
        Console.WriteLine($"Work items: {summary.TotalCount}");
        Console.WriteLine();

        if (showFailed && summary.Failed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Failed: {summary.Failed.Count}");
            Console.ResetColor();
            foreach (var item in summary.Failed)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.Write(item.Name);
                if (item.FailureCategory.HasValue)
                    Console.Write($" [{item.FailureCategory.Value}]");
                Console.Write($" (exit code {item.ExitCode}");
                if (item.Duration.HasValue)
                    Console.Write($", {FormatDuration(item.Duration.Value)}");
                if (!string.IsNullOrEmpty(item.MachineName))
                    Console.Write($", machine: {item.MachineName}");
                Console.WriteLine(")");
                Console.WriteLine($"         {item.ConsoleLogUrl}");
            }
        }
        else if (showFailed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All work items passed.");
            Console.ResetColor();
        }

        if (showPassed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\nPassed: {summary.Passed.Count}");
            Console.ResetColor();
            foreach (var item in summary.Passed)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [OK]   ");
                Console.ResetColor();
                Console.Write(item.Name);
                if (item.Duration.HasValue)
                    Console.Write($" ({FormatDuration(item.Duration.Value)})");
                Console.WriteLine();
            }
        }
        else if (summary.Passed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Passed: {summary.Passed.Count} (use 'hlx status <jobId> all' to show)");
            Console.ResetColor();
        }
    }

    /// <summary>Download console log for a work item to a temp file.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    [Command("logs")]
    public async Task Logs([Argument] string jobId, [Argument] string workItem)
    {
        var path = await _svc.DownloadConsoleLogAsync(jobId, workItem);
        Console.WriteLine(path);
    }

    /// <summary>List uploaded files for a work item.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [Command("files")]
    public async Task Files([Argument] string jobId, [Argument] string workItem, bool json = false)
    {
        var files = await _svc.GetWorkItemFilesAsync(jobId, workItem);

        if (json)
        {
            var result = new
            {
                binlogs = files.Where(f => HelixService.MatchesPattern(f.Name, "*.binlog")).Select(f => new { f.Name, f.Uri }),
                testResults = files.Where(f => HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri }),
                other = files.Where(f => !HelixService.MatchesPattern(f.Name, "*.binlog") && !HelixService.MatchesPattern(f.Name, "*.trx")).Select(f => new { f.Name, f.Uri })
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        foreach (var f in files)
        {
            var marker = HelixService.MatchesPattern(f.Name, "*.binlog") ? " [binlog]"
                       : HelixService.MatchesPattern(f.Name, "*.trx") ? " [test-results]"
                       : "";
            Console.WriteLine($"  {f.Name}{marker}");
        }
    }

    /// <summary>Download a specific file from a work item.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="pattern">File name or glob pattern (e.g., *.binlog).</param>
    [Command("download")]
    public async Task Download([Argument] string jobId, [Argument] string workItem, string pattern = "*")
    {
        var paths = await _svc.DownloadFilesAsync(jobId, workItem, pattern);
        if (paths.Count == 0)
        {
            Console.Error.WriteLine($"No files matching '{pattern}' found.");
            return;
        }
        foreach (var p in paths)
            Console.WriteLine(p);
    }

    /// <summary>Download a file by direct URL (e.g., blob storage URI).</summary>
    /// <param name="url">Direct file URL to download.</param>
    [Command("download-url")]
    public async Task DownloadUrl([Argument] string url)
    {
        var path = await _svc.DownloadFromUrlAsync(url);
        Console.WriteLine(path);
    }

    /// <summary>Scan work items in a job to find files matching a pattern.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="pattern">File name or glob pattern (e.g., *.binlog, *.trx, *.dmp).</param>
    /// <param name="maxItems">Max work items to scan (default 30).</param>
    [Command("find-files")]
    public async Task FindFiles([Argument] string jobId, string pattern = "*", int maxItems = 30)
    {
        Console.WriteLine($"Scanning up to {maxItems} work items for '{pattern}'...");
        var results = await _svc.FindFilesAsync(jobId, pattern, maxItems);
        foreach (var r in results)
        {
            Console.Write($"  {r.WorkItem}: ");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(string.Join(", ", r.Files.Select(f => f.Name)));
            Console.ResetColor();
        }
        if (results.Count == 0)
            Console.WriteLine($"  No files matching '{pattern}' found.");
    }

    /// <summary>Scan work items in a job to find which ones contain binlog files.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="maxItems">Max work items to scan (default 30).</param>
    [Command("find-binlogs")]
    public async Task FindBinlogs([Argument] string jobId, int maxItems = 30)
        => await FindFiles(jobId, "*.binlog", maxItems);

    /// <summary>Show detailed info about a specific work item.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="json">Output as structured JSON instead of human-readable text.</param>
    [Command("work-item")]
    public async Task WorkItem([Argument] string jobId, [Argument] string workItem, bool json = false)
    {
        var detail = await _svc.GetWorkItemDetailAsync(jobId, workItem);

        if (json)
        {
            var result = new
            {
                detail.Name,
                detail.ExitCode,
                detail.State,
                detail.MachineName,
                duration = detail.Duration?.ToString(),
                detail.ConsoleLogUrl,
                failureCategory = detail.FailureCategory?.ToString(),
                files = detail.Files.Select(f => new { f.Name, f.Uri })
            };
            Console.WriteLine(JsonSerializer.Serialize(result, s_jsonOptions));
            return;
        }

        Console.WriteLine($"Work Item: {detail.Name}");
        Console.WriteLine($"Exit Code: {detail.ExitCode}");
        if (detail.FailureCategory.HasValue)
            Console.WriteLine($"Category:  {detail.FailureCategory.Value}");
        if (!string.IsNullOrEmpty(detail.State))
            Console.WriteLine($"State:     {detail.State}");
        if (!string.IsNullOrEmpty(detail.MachineName))
            Console.WriteLine($"Machine:   {detail.MachineName}");
        if (detail.Duration.HasValue)
            Console.WriteLine($"Duration:  {FormatDuration(detail.Duration.Value)}");
        Console.WriteLine($"Log:       {detail.ConsoleLogUrl}");

        if (detail.Files.Count > 0)
        {
            Console.WriteLine($"\nFiles ({detail.Files.Count}):");
            foreach (var f in detail.Files)
            {
                var tag = HelixService.MatchesPattern(f.Name, "*.binlog") ? " [binlog]"
                    : HelixService.MatchesPattern(f.Name, "*.trx") ? " [test-results]"
                    : "";
                Console.WriteLine($"  {f.Name}{tag}");
            }
        }
    }

    /// <summary>Get status for multiple Helix jobs at once.</summary>
    /// <param name="jobIds">One or more Helix job IDs or URLs.</param>
    [Command("batch-status")]
    public async Task BatchStatus([Argument] params string[] jobIds)
    {
        Console.Error.Write("Fetching batch status...");
        var batch = await _svc.GetBatchStatusAsync(jobIds);
        Console.Error.WriteLine(" done.");

        foreach (var job in batch.Jobs)
        {
            var idPrefix = job.JobId.Length >= 8 ? job.JobId[..8] : job.JobId;
            Console.WriteLine($"Job {idPrefix}...: {job.Name} — {job.Failed.Count} failed, {job.Passed.Count} passed");
        }

        Console.WriteLine($"Overall: {batch.TotalFailed} failed, {batch.TotalPassed} passed across {batch.Jobs.Count} jobs");

        var allFailed = batch.Jobs.SelectMany(j => j.Failed).Where(f => f.FailureCategory.HasValue).ToList();
        if (allFailed.Count > 0)
        {
            var breakdown = allFailed
                .GroupBy(f => f.FailureCategory!.Value)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {g.Key}");
            Console.WriteLine($"Failure breakdown: {string.Join(", ", breakdown)}");
        }
    }

    /// <summary>Search a work item's console log for a pattern.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="pattern">Text pattern to search for (case-insensitive substring match).</param>
    /// <param name="context">Number of context lines before and after each match.</param>
    /// <param name="maxMatches">Maximum number of matches to return (default 50).</param>
    [Command("search-log")]
    public async Task SearchLog([Argument] string jobId, [Argument] string workItem,
        [Argument] string pattern, int context = 2, int maxMatches = 50)
    {
        var result = await _svc.SearchConsoleLogAsync(jobId, workItem, pattern, context, maxMatches);

        Console.WriteLine($"Searching for \"{pattern}\" in console log ({result.TotalLines} lines)...");
        Console.WriteLine($"Found {result.Matches.Count} matches:");
        Console.WriteLine();

        foreach (var match in result.Matches)
        {
            Console.WriteLine($"--- Line {match.LineNumber} ---");
            if (match.Context != null && context > 0)
            {
                int startLine = Math.Max(1, match.LineNumber - context);
                for (int i = 0; i < match.Context.Count; i++)
                {
                    int lineNum = startLine + i;
                    if (lineNum == match.LineNumber)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                    }
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  {match.LineNumber}: {match.Line}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Console.WriteLine($"{result.Matches.Count} matches found (showing up to {maxMatches}).");
    }

    /// <summary>Search a work item's uploaded file for matching lines.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="fileName">File name to search (exact name from files output).</param>
    /// <param name="pattern">Text pattern to search for (case-insensitive substring match).</param>
    /// <param name="context">Number of context lines before and after each match.</param>
    /// <param name="maxMatches">Maximum number of matches to return (default 50).</param>
    [Command("search-file")]
    public async Task SearchFile([Argument] string jobId, [Argument] string workItem,
        [Argument] string fileName, [Argument] string pattern, int context = 2, int maxMatches = 50)
    {
        var result = await _svc.SearchFileAsync(jobId, workItem, fileName, pattern, context, maxMatches);

        if (result.IsBinary)
        {
            Console.Error.WriteLine($"File '{fileName}' appears to be binary and cannot be searched.");
            return;
        }

        Console.WriteLine($"Searching for \"{pattern}\" in {result.FileName} ({result.TotalLines} lines)...");
        Console.WriteLine($"Found {result.Matches.Count} matches{(result.Truncated ? " (truncated)" : "")}:");
        Console.WriteLine();

        foreach (var match in result.Matches)
        {
            Console.WriteLine($"--- Line {match.LineNumber} ---");
            if (match.Context != null && context > 0)
            {
                int startLine = Math.Max(1, match.LineNumber - context);
                for (int i = 0; i < match.Context.Count; i++)
                {
                    int lineNum = startLine + i;
                    if (lineNum == match.LineNumber)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"  {lineNum}: {match.Context[i]}");
                    }
                }
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  {match.LineNumber}: {match.Line}");
                Console.ResetColor();
            }
            Console.WriteLine();
        }

        Console.WriteLine($"{result.Matches.Count} matches found (showing up to {maxMatches}).");
    }

    /// <summary>Parse TRX test results from a work item.</summary>
    /// <param name="jobId">Helix job ID or URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="fileName">Specific TRX file name (optional - auto-discovers all .trx files if not set).</param>
    /// <param name="includePassed">Include passed tests in output (default: false).</param>
    /// <param name="maxResults">Maximum number of test results to return (default: 200).</param>
    [Command("test-results")]
    public async Task TestResults([Argument] string jobId, [Argument] string workItem,
        string? fileName = null, bool includePassed = false, int maxResults = 200)
    {
        var trxResults = await _svc.ParseTrxResultsAsync(jobId, workItem, fileName, includePassed, maxResults);

        foreach (var file in trxResults)
        {
            Console.WriteLine($"File: {file.FileName}");
            Console.WriteLine($"  Total: {file.TotalTests}  Passed: {file.Passed}  Failed: {file.Failed}  Skipped: {file.Skipped}");
            Console.WriteLine();

            foreach (var test in file.Results)
            {
                if (test.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("  [FAIL] ");
                }
                else if (test.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase))
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  [PASS] ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write($"  [{test.Outcome.ToUpperInvariant()}] ");
                }
                Console.ResetColor();

                Console.Write(test.TestName);
                if (test.Duration != null)
                    Console.Write($" ({test.Duration})");
                Console.WriteLine();

                if (test.ErrorMessage != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"         {test.ErrorMessage}");
                    Console.ResetColor();
                }
                if (test.StackTrace != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"         {test.StackTrace}");
                    Console.ResetColor();
                }
            }
            Console.WriteLine();
        }
    }

    /// <summary>Print comprehensive tool documentation for LLM agents.</summary>
    [Command("llmstxt")]
    public void LlmsTxt()
    {
        var text = """
# hlx - Helix Test Infrastructure CLI

## CLI Commands
- `hlx status <jobId> [failed|passed|all]` — Work item summary (failed items with exit code, duration, machine)
- `hlx logs <jobId> <workItem>` — Download console log to temp file
- `hlx files <jobId> <workItem>` — List uploaded files for a work item
- `hlx download <jobId> <workItem> [--pattern PATTERN]` — Download artifacts (e.g., *.binlog)
- `hlx download-url <url>` — Download a file by direct blob storage URL
- `hlx find-files <jobId> [--pattern PATTERN] [--max-items N]` — Search work items for files matching a pattern
- `hlx find-binlogs <jobId> [--max-items N]` — Scan work items for binlog files
- `hlx work-item <jobId> <workItem> [--json]` — Detailed work item info (exit code, state, machine, duration, files)
- `hlx batch-status <jobId1> <jobId2> ...` — Status for multiple jobs in parallel
- `hlx search-log <jobId> <workItem> <pattern> [--context N] [--max-matches N]` — Search console log for patterns
- `hlx cache status` — Show cache size, entry count, oldest/newest entries
- `hlx cache clear` — Wipe all cached data (SQLite + artifact files)

## MCP Server
- `hlx mcp` — Start MCP server over stdio (for VS Code, Claude Desktop, etc.)
- `hlx-mcp` or `dotnet run --project src/HelixTool.Mcp` — HTTP MCP server

### MCP Tools
- `hlx_status` — Job pass/fail summary with consoleLogUrl and failureCategory per work item
- `hlx_logs` — Console log content (last N lines)
- `hlx_files` — List uploaded files, grouped by type (binlogs, testResults, other)
- `hlx_download` — Download files by glob pattern to temp dir
- `hlx_download_url` — Download a file by direct blob storage URL
- `hlx_find_files` — Search work items for files matching a glob pattern (*.binlog, *.trx, *.dmp, etc.)
- `hlx_find_binlogs` — Scan work items for binlog files
- `hlx_work_item` — Detailed work item info with exit code, state, machine, duration, files
- `hlx_batch_status` — Status for multiple jobs in parallel (accepts an array of job IDs/URLs)
- `hlx_search_log` — Search a work item's console log for error patterns
- `hlx_search_file` — Search a work item's uploaded file for lines matching a pattern
- `hlx_test_results` — Parse TRX test result files from a work item

## Authentication
Set HELIX_ACCESS_TOKEN env var for internal jobs. Public jobs need no auth.

## Input
- Accepts bare GUIDs: `hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009`
- Accepts Helix URLs: `hlx status https://helix.dot.net/api/jobs/02d8bd09.../details`
- jobId and workItem are positional arguments (no --job-id flag needed)

## Failure Categorization
Failed work items are auto-classified: Timeout, Crash, BuildFailure, TestFailure, InfrastructureError, AssertionFailure, Unknown.
Available as `failureCategory` in JSON and MCP output.

## Output
- `status` prints summary with per-work-item state, exit code, duration, machine, consoleLogUrl, failureCategory
- `work-item` shows detailed info including all uploaded files
- `batch-status` shows per-job summary and overall totals with failure breakdown
- `logs` and `download` save files to %TEMP% and print paths

## Caching
- SQLite-backed cross-process cache (shared across `hlx mcp` stdio instances)
- Cache isolated per auth context: unauthenticated uses `{base}/public/`, token uses `{base}/cache-{hash}/` (first 8 chars of SHA256)
- Cache location base: `%LOCALAPPDATA%/hlx/` (Windows) or `$XDG_CACHE_HOME/hlx/` (Linux/macOS)
- Default max size: 1 GB. Configure via `HLX_CACHE_MAX_SIZE_MB` env var. Set to 0 to disable.
- Running jobs: 15-30s TTL. Completed jobs: 1-4h TTL. Console logs never cached while running.
- Eviction: TTL-based + LRU when over max size. Artifacts expire after 7 days without access.
- `cache clear` wipes ALL auth contexts. `cache status` shows the current auth context.
""";
        Console.Write(text);
    }

    /// <summary>Start the MCP server over stdio for use with MCP clients (VS Code, Claude Desktop, etc.).</summary>
    [Command("mcp")]
    public async Task Mcp()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddSingleton<IHelixTokenAccessor>(_ =>
            new EnvironmentHelixTokenAccessor(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
        builder.Services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();
        builder.Services.AddSingleton<CacheOptions>(sp =>
        {
            var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
            var opts = new CacheOptions
            {
                AuthTokenHash = CacheOptions.ComputeTokenHash(token)
            };
            var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
            if (int.TryParse(maxStr, out var mb))
                opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
            return opts;
        });
        builder.Services.AddSingleton<ICacheStore>(sp => new SqliteCacheStore(sp.GetRequiredService<CacheOptions>()));
        builder.Services.AddSingleton<HelixApiClient>(sp =>
        {
            var token = sp.GetRequiredService<IHelixTokenAccessor>().GetAccessToken();
            return new HelixApiClient(token);
        });
        builder.Services.AddSingleton<IHelixApiClient>(sp =>
            new CachingHelixApiClient(
                sp.GetRequiredService<HelixApiClient>(),
                sp.GetRequiredService<ICacheStore>(),
                sp.GetRequiredService<CacheOptions>()));
        builder.Services.AddSingleton<HelixService>();
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new() { Name = "hlx", Version = "1.0.0" };
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly);
        await builder.Build().RunAsync();
    }

    /// <summary>Clear all cached data across ALL auth contexts.</summary>
    [Command("cache clear")]
    public async Task CacheClear()
    {
        // Clear current auth context's cache store
        var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
        await cache.ClearAsync();

        // Also wipe all sibling auth context directories under the base root
        var options = ConsoleApp.ServiceProvider!.GetRequiredService<CacheOptions>();
        var baseRoot = options.GetBaseCacheRoot();
        if (Directory.Exists(baseRoot))
        {
            foreach (var dir in Directory.GetDirectories(baseRoot))
            {
                var name = Path.GetFileName(dir);
                if (name == "public" || name.StartsWith("cache-", StringComparison.Ordinal))
                {
                    // Skip current context (already cleared via ICacheStore)
                    var currentRoot = options.GetEffectiveCacheRoot();
                    if (string.Equals(dir, currentRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch (IOException) { /* best effort */ }
                }
            }
        }

        Console.WriteLine("Cache cleared (all auth contexts).");
    }

    /// <summary>Show cache size, entry count, oldest/newest entries for the current auth context.</summary>
    [Command("cache status")]
    public async Task CacheStatusCmd()
    {
        var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
        var options = ConsoleApp.ServiceProvider!.GetRequiredService<CacheOptions>();
        var status = await cache.GetStatusAsync();

        var context = options.AuthTokenHash != null ? $"authenticated (cache-{options.AuthTokenHash})" : "public (unauthenticated)";
        Console.WriteLine($"Cache status ({context}):");
        Console.WriteLine($"  Cache dir:       {options.GetEffectiveCacheRoot()}");
        Console.WriteLine($"  Max size:        {FormatBytes(status.MaxSizeBytes)}");
        Console.WriteLine($"  Current size:    {FormatBytes(status.TotalSizeBytes)}");
        Console.WriteLine($"  Metadata entries: {status.MetadataEntryCount}");
        Console.WriteLine($"  Artifact files:   {status.ArtifactFileCount}");
        if (status.OldestEntry.HasValue)
            Console.WriteLine($"  Oldest entry:    {status.OldestEntry.Value:u}");
        if (status.NewestEntry.HasValue)
            Console.WriteLine($"  Newest entry:    {status.NewestEntry.Value:u}");
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes >= 1L * 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        if (bytes >= 1024)
            return $"{bytes / 1024.0:F1} KB";
        return $"{bytes} B";
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            var minutes = duration.Minutes;
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        }
        if (duration.TotalMinutes >= 1)
        {
            var minutes = (int)duration.TotalMinutes;
            var seconds = duration.Seconds;
            return seconds > 0 ? $"{minutes}m {seconds}s" : $"{minutes}m";
        }
        return $"{(int)duration.TotalSeconds}s";
    }
}