using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace HelixTool.Core;

/// <summary>Category of work item failure.</summary>
public enum FailureCategory
{
    Unknown,
    Timeout,
    Crash,
    AssertionFailure,
    InfrastructureError,
    BuildFailure,
    TestFailure
}

/// <summary>
/// Core Helix API operations, shared between CLI and MCP server modes.
/// Requires an <see cref="IHelixApiClient"/> injected via constructor (decision D3).
/// </summary>
public class HelixService
{
    private readonly IHelixApiClient _api;

    /// <summary>
    /// Initializes a new instance of <see cref="HelixService"/>.
    /// </summary>
    /// <param name="api">The Helix API client to use for all SDK calls.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="api"/> is <c>null</c>.</exception>
    public HelixService(IHelixApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Represents a single work item's name and exit code.</summary>
    public record WorkItemResult(string Name, int ExitCode, string? State, string? MachineName, TimeSpan? Duration, string ConsoleLogUrl, FailureCategory? FailureCategory);

    /// <summary>Aggregated pass/fail summary for all work items in a Helix job.</summary>
    public record JobSummary(
        string JobId, string Name, string QueueId, string Creator, string Source,
        string? Created, string? Finished,
        int TotalCount, List<WorkItemResult> Failed, List<WorkItemResult> Passed);

    /// <summary>Get a pass/fail summary of all work items in a Helix job.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A <see cref="JobSummary"/> with job metadata, failed items, and passed items.</returns>
    /// <exception cref="HelixException">Thrown when the job is not found or the API is unreachable.</exception>
    public async Task<JobSummary> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            var job = await _api.GetJobDetailsAsync(id, cancellationToken);
            var workItems = await _api.ListWorkItemsAsync(id, cancellationToken);

            var semaphore = new SemaphoreSlim(10);
            var tasks = workItems.Select(async wi =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var details = await _api.GetWorkItemDetailsAsync(wi.Name, id, cancellationToken);
                    TimeSpan? duration = (details.Started.HasValue && details.Finished.HasValue)
                        ? details.Finished.Value - details.Started.Value
                        : null;
                    var consoleLogUrl = $"https://helix.dot.net/api/2019-06-17/jobs/{id}/workitems/{wi.Name}/console";
                    var exitCode = details.ExitCode ?? -1;
                    FailureCategory? category = exitCode != 0
                        ? ClassifyFailure(exitCode, details.State, duration, wi.Name)
                        : null;
                    return new WorkItemResult(wi.Name, exitCode, details.State, details.MachineName, duration, consoleLogUrl, category);
                }
                finally { semaphore.Release(); }
            }).ToList();

            var results = await Task.WhenAll(tasks);
            var failed = results.Where(r => r.ExitCode != 0).ToList();
            var passed = results.Where(r => r.ExitCode == 0).ToList();

            return new JobSummary(
                id,
                job.Name ?? "", job.QueueId ?? "", job.Creator ?? "", job.Source ?? "",
                job.Created, job.Finished,
                workItems.Count, failed, passed);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>Represents an uploaded file from a Helix work item.</summary>
    public record FileEntry(string Name, string Uri);

    /// <summary>
    /// List uploaded files for a work item using the <c>ListFiles</c> endpoint.
    /// This avoids the broken URIs from the <c>Details</c> endpoint (dnceng#6072).
    /// </summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A list of <see cref="FileEntry"/> with name, URI, and type tags.</returns>
    /// <exception cref="HelixException">Thrown when the work item is not found or the API is unreachable.</exception>
    public async Task<List<FileEntry>> GetWorkItemFilesAsync(string jobId, string workItem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            var files = await _api.ListWorkItemFilesAsync(workItem, id, cancellationToken);
            return files.Select(f => new FileEntry(
                f.Name, f.Link ?? ""
            )).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Work item '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>Download a work item's console log to a temp file on disk.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The absolute path of the downloaded log file.</returns>
    /// <exception cref="HelixException">Thrown when the log is not found or the API is unreachable.</exception>
    public async Task<string> DownloadConsoleLogAsync(string jobId, string workItem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            await using var stream = await _api.GetConsoleLogAsync(workItem, id, cancellationToken);
            var safeName = CacheSecurity.SanitizePathSegment(workItem);
            var idPrefix = id.Length >= 8 ? id[..8] : id;
            var path = Path.Combine(Path.GetTempPath(), $"helix-{idPrefix}-{safeName}.txt");
            CacheSecurity.ValidatePathWithinRoot(path, Path.GetTempPath());
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, cancellationToken);
            return path;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Console log for '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>Get console log content as a string, optionally returning only the last N lines.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="tailLines">If set, return only the last N lines.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The console log text (full or tailed).</returns>
    /// <exception cref="HelixException">Thrown when the log is not found or the API is unreachable.</exception>
    public async Task<string> GetConsoleLogContentAsync(string jobId, string workItem, int? tailLines = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            await using var stream = await _api.GetConsoleLogAsync(workItem, id, cancellationToken);
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            if (tailLines.HasValue)
            {
                var lines = content.Split('\n');
                var start = Math.Max(0, lines.Length - tailLines.Value);
                return string.Join('\n', lines[start..]);
            }
            return content;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Console log for '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>A work item and the matching files it contains.</summary>
    public record FileSearchResult(string WorkItem, List<FileEntry> Files);

    /// <summary>Scan work items in a job to find files matching a pattern.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="pattern">File name or glob pattern (e.g., <c>*.binlog</c>). Default: all files.</param>
    /// <param name="maxItems">Maximum number of work items to scan (default 30).</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>A list of <see cref="FileSearchResult"/> for work items that contain matching files.</returns>
    /// <exception cref="HelixException">Thrown when the job is not found or the API is unreachable.</exception>
    public async Task<List<FileSearchResult>> FindFilesAsync(string jobId, string pattern = "*", int maxItems = 30, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            var workItems = await _api.ListWorkItemsAsync(id, cancellationToken);
            var toScan = workItems.Take(maxItems).ToList();
            var results = new List<FileSearchResult>();

            foreach (var wi in toScan)
            {
                var files = await _api.ListWorkItemFilesAsync(wi.Name, id, cancellationToken);
                var matching = files
                    .Where(f => MatchesPattern(f.Name, pattern))
                    .Select(f => new FileEntry(f.Name, f.Link ?? ""))
                    .ToList();
                if (matching.Count > 0)
                    results.Add(new FileSearchResult(wi.Name, matching));
            }

            return results;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>Scan work items in a job to find which ones contain binlog files.</summary>
    public Task<List<FileSearchResult>> FindBinlogsAsync(string jobId, int maxItems = 30, CancellationToken cancellationToken = default)
        => FindFilesAsync(jobId, "*.binlog", maxItems, cancellationToken);

    /// <summary>Download files matching a pattern from a work item to a temp directory.</summary>
    /// <param name="jobId">Helix job ID (GUID) or full Helix URL.</param>
    /// <param name="workItem">Work item name.</param>
    /// <param name="pattern">File name or glob pattern (e.g., <c>*.binlog</c>). Default: all files.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>List of absolute paths of downloaded files, or empty if no matches.</returns>
    /// <exception cref="HelixException">Thrown when the work item is not found or the API is unreachable.</exception>
    public async Task<List<string>> DownloadFilesAsync(string jobId, string workItem, string pattern = "*", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            var files = await _api.ListWorkItemFilesAsync(workItem, id, cancellationToken);
            var matching = files.Where(f => MatchesPattern(f.Name, pattern)).ToList();

            if (matching.Count == 0)
                return [];

            var idPrefix = id.Length >= 8 ? id[..8] : id;
            var outDir = Path.Combine(Path.GetTempPath(), $"helix-{idPrefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(outDir);
            var paths = new List<string>();

            foreach (var f in matching)
            {
                await using var stream = await _api.GetFileAsync(f.Name, workItem, id, cancellationToken);
                var safeName = CacheSecurity.SanitizePathSegment(Path.GetFileName(f.Name));
                var outPath = Path.Combine(outDir, safeName);
                CacheSecurity.ValidatePathWithinRoot(outPath, outDir);
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                await using var file = File.Create(outPath);
                await stream.CopyToAsync(file, cancellationToken);
                paths.Add(outPath);
            }

            return paths;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Work item '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    private static readonly HttpClient s_httpClient = new();

    /// <summary>Download a file from a direct URL (e.g., blob storage URI) to a temp file.</summary>
    /// <param name="url">Direct file URL to download.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>The absolute path of the downloaded file.</returns>
    /// <exception cref="HelixException">Thrown when the download fails.</exception>
    public async Task<string> DownloadFromUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        try
        {
            var uri = new Uri(url);
            if (uri.Scheme != "http" && uri.Scheme != "https")
                throw new ArgumentException($"Only HTTP and HTTPS URLs are supported. Got scheme '{uri.Scheme}'.", nameof(url));
            var fileName = Uri.UnescapeDataString(uri.Segments[^1]);
            var safeName = CacheSecurity.SanitizePathSegment(Path.GetFileName(fileName));
            var path = Path.Combine(Path.GetTempPath(), $"helix-download-{safeName}");
            CacheSecurity.ValidatePathWithinRoot(path, Path.GetTempPath());

            using var response = await s_httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, cancellationToken);

            return path;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized || ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied downloading file. The URL may require authentication.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new HelixException($"File not found at URL: {url}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Download error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Download request timed out.", ex);
        }
    }

    /// <summary>Detailed information about a single work item.</summary>
    public record WorkItemDetail(
        string Name, int ExitCode, string? State, string? MachineName,
        TimeSpan? Duration, string ConsoleLogUrl, List<FileEntry> Files, FailureCategory? FailureCategory);

    /// <summary>Get detailed info about a single work item including its files.</summary>
    public async Task<WorkItemDetail> GetWorkItemDetailAsync(string jobId, string workItem, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        var id = HelixIdResolver.ResolveJobId(jobId);

        try
        {
            var detailsTask = _api.GetWorkItemDetailsAsync(workItem, id, cancellationToken);
            var filesTask = _api.ListWorkItemFilesAsync(workItem, id, cancellationToken);
            await Task.WhenAll(detailsTask, filesTask);

            var details = detailsTask.Result;
            var files = filesTask.Result;

            TimeSpan? duration = (details.Started.HasValue && details.Finished.HasValue)
                ? details.Finished.Value - details.Started.Value
                : null;
            var consoleLogUrl = $"https://helix.dot.net/api/2019-06-17/jobs/{id}/workitems/{workItem}/console";
            var fileEntries = files.Select(f => new FileEntry(
                f.Name, f.Link ?? ""
            )).ToList();

            var exitCode = details.ExitCode ?? -1;
            FailureCategory? category = exitCode != 0
                ? ClassifyFailure(exitCode, details.State, duration, workItem)
                : null;

            return new WorkItemDetail(workItem, exitCode, details.State, details.MachineName, duration, consoleLogUrl, fileEntries, category);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Set the HELIX_ACCESS_TOKEN environment variable with a token from your helix.dot.net profile page.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Work item '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw new HelixException("Helix API request timed out.", ex);
        }
    }

    /// <summary>Summary for multiple jobs.</summary>
    public record BatchJobSummary(List<JobSummary> Jobs, int TotalFailed, int TotalPassed);

    /// <summary>Maximum number of jobs allowed in a single batch status request.</summary>
    internal const int MaxBatchSize = 50;

    /// <summary>Get status for multiple jobs in parallel.</summary>
    public async Task<BatchJobSummary> GetBatchStatusAsync(IEnumerable<string> jobIds, CancellationToken cancellationToken = default)
    {
        var idList = jobIds.ToList();
        if (idList.Count == 0)
            throw new ArgumentException("At least one job ID is required.", nameof(jobIds));
        if (idList.Count > MaxBatchSize)
            throw new ArgumentException($"Batch size {idList.Count} exceeds the maximum of {MaxBatchSize} jobs.", nameof(jobIds));

        var semaphore = new SemaphoreSlim(5);
        var tasks = idList.Select(async jobId =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                return await GetJobStatusAsync(jobId, cancellationToken);
            }
            finally { semaphore.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var jobs = results.ToList();
        var totalFailed = jobs.Sum(j => j.Failed.Count);
        var totalPassed = jobs.Sum(j => j.Passed.Count);

        return new BatchJobSummary(jobs, totalFailed, totalPassed);
    }

    /// <summary>Classify a work item failure based on available signals.</summary>
    public static FailureCategory ClassifyFailure(int exitCode, string? state, TimeSpan? duration, string? workItemName)
    {
        // 1. Timeout
        if (state != null && (state == "Timed Out" || state.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
            return FailureCategory.Timeout;

        // 2. Crash (exit code < 0 or >= 128 on Unix signals)
        if (exitCode < 0 || exitCode >= 128)
        {
            // Special case: exitCode == -1 and state not set → Unknown
            if (exitCode == -1 && string.IsNullOrEmpty(state))
                return FailureCategory.Unknown;
            return FailureCategory.Crash;
        }

        // 3. exitCode == 1 and workItemName contains "build" → BuildFailure
        if (exitCode == 1 && workItemName != null && workItemName.Contains("build", StringComparison.OrdinalIgnoreCase))
            return FailureCategory.BuildFailure;

        // 4. exitCode == 1 and (workItemName contains "test" or ends with ".dll") → TestFailure
        if (exitCode == 1 && workItemName != null &&
            (workItemName.Contains("test", StringComparison.OrdinalIgnoreCase) ||
             workItemName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            return FailureCategory.TestFailure;

        // 5. state contains "error" or "fail" (case insensitive) → InfrastructureError
        if (state != null &&
            (state.Contains("error", StringComparison.OrdinalIgnoreCase) ||
             state.Contains("fail", StringComparison.OrdinalIgnoreCase)))
            return FailureCategory.InfrastructureError;

        // 6. exitCode == 1 → TestFailure (default for exit code 1)
        if (exitCode == 1)
            return FailureCategory.TestFailure;

        // 7. exitCode != 0 → Unknown
        if (exitCode != 0)
            return FailureCategory.Unknown;

        // 8. Everything else → Unknown
        return FailureCategory.Unknown;
    }

    /// <summary>Whether file content search is disabled by configuration.</summary>
    internal static bool IsFileSearchDisabled =>
        string.Equals(Environment.GetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maximum file size allowed for content search (50 MB).</summary>
    internal const long MaxSearchFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>Result of searching a console log.</summary>
    public record LogSearchResult(string WorkItem, List<LogMatch> Matches, int TotalLines);

    /// <summary>A single match in a console log.</summary>
    public record LogMatch(int LineNumber, string Line, List<string>? Context = null);

    /// <summary>Result of searching an uploaded file's content.</summary>
    public record FileContentSearchResult(string FileName, List<LogMatch> Matches, int TotalLines, bool Truncated, bool IsBinary);

    /// <summary>Parsed test result from a TRX file.</summary>
    public record TrxTestResult(string TestName, string Outcome, string? Duration, string? ComputerName, string? ErrorMessage, string? StackTrace);

    /// <summary>Summary of parsed TRX test results.</summary>
    public record TrxParseResult(string FileName, int TotalTests, int Passed, int Failed, int Skipped, List<TrxTestResult> Results);

    private static readonly XmlReaderSettings s_trxReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 50_000_000,
        Async = true
    };

    /// <summary>Search lines for a pattern with optional context.</summary>
    private static LogSearchResult SearchLines(string identifier, string[] lines, string pattern, int contextLines, int maxMatches)
    {
        var matchIndices = new List<int>();

        for (int i = 0; i < lines.Length && matchIndices.Count < maxMatches; i++)
        {
            if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                matchIndices.Add(i);
        }

        var matches = new List<LogMatch>();
        foreach (var idx in matchIndices)
        {
            List<string>? context = null;
            if (contextLines > 0)
            {
                int start = Math.Max(0, idx - contextLines);
                int end = Math.Min(lines.Length - 1, idx + contextLines);
                context = new List<string>();
                for (int j = start; j <= end; j++)
                    context.Add(lines[j]);
            }
            matches.Add(new LogMatch(idx + 1, lines[idx], context));
        }

        return new LogSearchResult(identifier, matches, lines.Length);
    }

    /// <summary>Search a work item's console log for lines matching a pattern.</summary>
    public async Task<LogSearchResult> SearchConsoleLogAsync(
        string jobId, string workItem, string pattern,
        int contextLines = 0, int maxMatches = 50,
        CancellationToken cancellationToken = default)
    {
        if (IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var path = await DownloadConsoleLogAsync(jobId, workItem, cancellationToken);
        try
        {
            var allLines = await File.ReadAllLinesAsync(path, cancellationToken);
            return SearchLines(workItem, allLines, pattern, contextLines, maxMatches);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>Search a work item's uploaded file for lines matching a pattern.</summary>
    public async Task<FileContentSearchResult> SearchFileAsync(
        string jobId, string workItem, string fileName, string pattern,
        int contextLines = 0, int maxMatches = 50,
        CancellationToken cancellationToken = default)
    {
        if (IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var downloadedPaths = await DownloadFilesAsync(jobId, workItem, fileName, cancellationToken);
        if (downloadedPaths.Count == 0)
            throw new HelixException($"File '{fileName}' not found in work item '{workItem}'.");

        var filePath = downloadedPaths[0];
        try
        {
            // Check file size
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length > MaxSearchFileSizeBytes)
                throw new HelixException($"File '{fileName}' is {fileInfo.Length / (1024 * 1024)}MB which exceeds the {MaxSearchFileSizeBytes / (1024 * 1024)}MB search limit.");

            // Check for binary content (null byte in first 8KB)
            var buffer = new byte[Math.Min(8192, fileInfo.Length)];
            await using (var fs = File.OpenRead(filePath))
            {
                _ = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            if (Array.IndexOf(buffer, (byte)0) >= 0)
                return new FileContentSearchResult(fileName, [], 0, false, true);

            var allLines = await File.ReadAllLinesAsync(filePath, cancellationToken);
            var searchResult = SearchLines(fileName, allLines, pattern, contextLines, maxMatches);
            var truncated = searchResult.Matches.Count >= maxMatches;

            return new FileContentSearchResult(fileName, searchResult.Matches, searchResult.TotalLines, truncated, false);
        }
        finally
        {
            foreach (var p in downloadedPaths)
            {
                try { File.Delete(p); } catch { }
            }
        }
    }

    private static TrxParseResult ParseTrxFile(string filePath, string fileName, bool includePassed, int maxResults)
    {
        using var fileStream = File.OpenRead(filePath);
        using var reader = XmlReader.Create(fileStream, s_trxReaderSettings);

        var doc = XDocument.Load(reader);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        var unitTestResults = doc.Descendants(ns + "UnitTestResult").ToList();

        int passed = 0, failed = 0, skipped = 0;
        var results = new List<TrxTestResult>();

        foreach (var utr in unitTestResults)
        {
            var testName = utr.Attribute("testName")?.Value ?? "Unknown";
            var outcome = utr.Attribute("outcome")?.Value ?? "Unknown";
            var duration = utr.Attribute("duration")?.Value;
            var computerName = utr.Attribute("computerName")?.Value;

            switch (outcome.ToLowerInvariant())
            {
                case "passed": passed++; break;
                case "failed": failed++; break;
                default: skipped++; break;
            }

            // Extract error info for failed tests
            string? errorMessage = null;
            string? stackTrace = null;
            if (outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                var errorInfo = utr.Element(ns + "Output")?.Element(ns + "ErrorInfo");
                errorMessage = errorInfo?.Element(ns + "Message")?.Value;
                stackTrace = errorInfo?.Element(ns + "StackTrace")?.Value;

                // Truncate error message at 500 chars
                if (errorMessage?.Length > 500)
                    errorMessage = errorMessage[..500] + "... (truncated)";
                if (stackTrace?.Length > 1000)
                    stackTrace = stackTrace[..1000] + "... (truncated)";
            }

            // Include test based on filter and limit
            bool shouldInclude = outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                                (!outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)) || // always include non-pass/non-fail
                                includePassed;

            if (shouldInclude && results.Count < maxResults)
                results.Add(new TrxTestResult(testName, outcome, duration, computerName, errorMessage, stackTrace));
        }

        return new TrxParseResult(fileName, unitTestResults.Count, passed, failed, skipped, results);
    }

    /// <summary>Parse TRX test result files from a Helix work item.</summary>
    public async Task<List<TrxParseResult>> ParseTrxResultsAsync(
        string jobId, string workItem, string? fileName = null,
        bool includePassed = false, int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);

        var pattern = fileName ?? "*.trx";
        var downloadedPaths = await DownloadFilesAsync(jobId, workItem, pattern, cancellationToken);

        if (downloadedPaths.Count == 0)
            throw new HelixException($"No TRX files found in work item '{workItem}'.");

        var results = new List<TrxParseResult>();
        try
        {
            foreach (var path in downloadedPaths)
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > MaxSearchFileSizeBytes)
                    throw new HelixException($"TRX file '{Path.GetFileName(path)}' exceeds the {MaxSearchFileSizeBytes / (1024 * 1024)}MB size limit.");

                var trxFileName = fileName ?? Path.GetFileName(path);
                results.Add(ParseTrxFile(path, trxFileName, includePassed, maxResults));
            }
        }
        finally
        {
            foreach (var p in downloadedPaths)
                try { File.Delete(p); } catch { }
        }

        return results;
    }

    public static bool MatchesPattern(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*."))
            return name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
