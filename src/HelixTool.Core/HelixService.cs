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
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of <see cref="HelixService"/>.
    /// </summary>
    /// <param name="api">The Helix API client to use for all SDK calls.</param>
    /// <param name="httpClient">HttpClient for direct URL downloads. When null, a default instance is created (test convenience).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="api"/> is <c>null</c>.</exception>
    public HelixService(IHelixApiClient api, HttpClient? httpClient = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _httpClient = httpClient ?? new HttpClient();
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
                return StringHelpers.TailLines(content, tailLines.Value);

            return content;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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

            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
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
    public static bool IsFileSearchDisabled =>
        string.Equals(Environment.GetEnvironmentVariable("HLX_DISABLE_FILE_SEARCH"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Maximum file size allowed for content search (50 MB).</summary>
    internal const long MaxSearchFileSizeBytes = 50 * 1024 * 1024;

    /// <summary>Parsed test result from a TRX or xUnit XML file.</summary>
    public record TrxTestResult(string TestName, string Outcome, string? Duration, string? ComputerName, string? ErrorMessage, string? StackTrace);

    /// <summary>Summary of parsed test results from a TRX or xUnit XML file.</summary>
    public record TrxParseResult(string FileName, int TotalTests, int Passed, int Failed, int Skipped, List<TrxTestResult> Results);

    private enum TestFileFormat { Trx, Xunit, Unknown }

    private static readonly XmlReaderSettings s_trxReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = 50_000_000,
        Async = true
    };

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

        // Stream directly to memory instead of writing to disk then reading back
        var content = await GetConsoleLogContentAsync(jobId, workItem, tailLines: null, cancellationToken);
        // Split handling CRLF/CR/LF, strip \r, and omit trailing empty element from final newline
        var allLines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (allLines.Length > 1 && allLines[^1].Length == 0)
            allLines = allLines[..^1];
        return TextSearchHelper.SearchLines(workItem, allLines, pattern, contextLines, maxMatches);
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
            var searchResult = TextSearchHelper.SearchLines(fileName, allLines, pattern, contextLines, maxMatches);
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

    /// <summary>Detect whether an XML file is TRX or xUnit format by inspecting the root element.</summary>
    private static TestFileFormat DetectTestFileFormat(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var reader = XmlReader.Create(stream, s_trxReaderSettings);
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                // TRX files have root element "TestRun" in the VS TeamTest namespace
                if (reader.LocalName == "TestRun")
                    return TestFileFormat.Trx;
                // xUnit XML files have root element "assemblies" or "assembly"
                if (reader.LocalName is "assemblies" or "assembly")
                    return TestFileFormat.Xunit;
                return TestFileFormat.Unknown;
            }
        }
        return TestFileFormat.Unknown;
    }

    /// <summary>Parse an xUnit XML result file.</summary>
    private static TrxParseResult ParseXunitFile(string filePath, string fileName, bool includePassed, int maxResults)
    {
        using var fileStream = File.OpenRead(filePath);
        using var reader = XmlReader.Create(fileStream, s_trxReaderSettings);

        var doc = XDocument.Load(reader);

        // xUnit XML format: <assemblies><assembly><collection><test> ...
        // Or just <assembly><collection><test> ... (single assembly)
        var tests = doc.Descendants("test").ToList();

        int passed = 0, failed = 0, skipped = 0;
        var results = new List<TrxTestResult>();

        foreach (var test in tests)
        {
            var testName = test.Attribute("name")?.Value ?? "Unknown";
            var result = test.Attribute("result")?.Value ?? "Unknown";
            var time = test.Attribute("time")?.Value;

            // Normalize xUnit result values to TRX-style outcomes
            var outcome = result switch
            {
                "Pass" => "Passed",
                "Fail" => "Failed",
                "Skip" => "Skipped",
                _ => result
            };

            switch (outcome.ToLowerInvariant())
            {
                case "passed": passed++; break;
                case "failed": failed++; break;
                default: skipped++; break;
            }

            string? errorMessage = null;
            string? stackTrace = null;
            if (outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
            {
                var failure = test.Element("failure");
                errorMessage = failure?.Element("message")?.Value;
                stackTrace = failure?.Element("stack-trace")?.Value;

                if (errorMessage?.Length > 500)
                    errorMessage = errorMessage[..500] + "... (truncated)";
                if (stackTrace?.Length > 1000)
                    stackTrace = stackTrace[..1000] + "... (truncated)";
            }

            // For skipped tests, capture the reason
            if (outcome.Equals("Skipped", StringComparison.OrdinalIgnoreCase) && errorMessage == null)
            {
                errorMessage = test.Element("reason")?.Value;
                if (errorMessage?.Length > 500)
                    errorMessage = errorMessage[..500] + "... (truncated)";
            }

            bool shouldInclude = outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                                 (!outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)) ||
                                 includePassed;

            if (shouldInclude && results.Count < maxResults)
            {
                // xUnit uses seconds as a decimal; format as duration string
                var duration = time != null ? $"{time}s" : null;
                results.Add(new TrxTestResult(testName, outcome, duration, null, errorMessage, stackTrace));
            }
        }

        return new TrxParseResult(fileName, tests.Count, passed, failed, skipped, results);
    }

    /// <summary>Parse a test result file, auto-detecting format (TRX or xUnit XML).</summary>
    private static TrxParseResult? TryParseTestFile(string filePath, string fileName, bool includePassed, int maxResults)
    {
        var format = DetectTestFileFormat(filePath);
        return format switch
        {
            TestFileFormat.Trx => ParseTrxFile(filePath, fileName, includePassed, maxResults),
            TestFileFormat.Xunit => ParseXunitFile(filePath, fileName, includePassed, maxResults),
            _ => null
        };
    }

    /// <summary>Patterns used to discover test result files in work item file lists, in priority order.</summary>
    internal static readonly string[] TestResultFilePatterns =
    [
        "*.trx",                     // VSTest / dotnet test TRX files
        "testResults.xml",           // XHarness / xUnit XML (exact name)
        "*.testResults.xml.txt",     // CoreCLR XUnitWrapperGenerator pattern
        "testResults.xml.txt",       // CoreCLR variant (exact name)
    ];

    /// <summary>Check whether a file name matches any known test result pattern.</summary>
    public static bool IsTestResultFile(string fileName)
    {
        foreach (var pattern in TestResultFilePatterns)
        {
            if (MatchesTestResultPattern(fileName, pattern))
                return true;
        }
        return false;
    }

    /// <summary>Match a file name against a test result pattern (supports *.ext and exact name).</summary>
    private static bool MatchesTestResultPattern(string fileName, string pattern)
    {
        if (pattern.StartsWith("*."))
            return fileName.AsSpan().EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);
        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parse test result files (TRX or xUnit XML) from a Helix work item.</summary>
    public async Task<List<TrxParseResult>> ParseTrxResultsAsync(
        string jobId, string workItem, string? fileName = null,
        bool includePassed = false, int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        if (IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workItem);

        if (fileName != null)
        {
            // Specific file requested — download and auto-detect format
            var downloadedPaths = await DownloadFilesAsync(jobId, workItem, fileName, cancellationToken);
            if (downloadedPaths.Count == 0)
                throw new HelixException($"File '{fileName}' not found in work item '{workItem}'.");

            return ParseDownloadedFiles(downloadedPaths, fileName, includePassed, maxResults);
        }

        // Auto-discovery: get file list once and find all test result files
        var id = HelixIdResolver.ResolveJobId(jobId);
        List<IWorkItemFile> allFiles;
        try
        {
            allFiles = (await _api.ListWorkItemFilesAsync(workItem, id, cancellationToken)).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized || ex.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new HelixException("Access denied. Run 'hlx login' to authenticate, or set the HELIX_ACCESS_TOKEN environment variable.", ex);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HelixException($"Work item '{workItem}' in job '{id}' not found.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HelixException($"Helix API error: {ex.Message}", ex);
        }

        // Find test result files matching known patterns
        var testResultFiles = allFiles.Where(f => IsTestResultFile(f.Name)).ToList();

        if (testResultFiles.Count > 0)
        {
            // Download and parse discovered test result files
            var downloadedPaths = await DownloadMatchingFilesAsync(testResultFiles, workItem, id, cancellationToken);

            // TRX files are parsed strictly; other formats use best-effort
            var trxPaths = downloadedPaths.Where(p => p.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)).ToList();
            var otherPaths = downloadedPaths.Where(p => !p.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)).ToList();

            var results = new List<TrxParseResult>();

            if (trxPaths.Count > 0)
                results.AddRange(ParseDownloadedFiles(trxPaths, null, includePassed, maxResults));

            if (otherPaths.Count > 0)
                results.AddRange(ParseDownloadedFilesBestEffort(otherPaths, includePassed, maxResults));

            if (results.Count > 0)
                return results;
        }

        // Build a helpful error message — filter out noise (hash-named .log files)
        var fileNames = allFiles.Select(f => f.Name).ToList();
        var patternsSearched = string.Join(", ", TestResultFilePatterns);

        // Identify useful files vs noise
        var crashArtifacts = fileNames.Where(f =>
            f.StartsWith("core.", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("crashdump", StringComparison.OrdinalIgnoreCase)).ToList();
        var usefulFiles = fileNames.Where(f =>
            IsTestResultFile(f) ||
            (f.StartsWith("vstest.", StringComparison.OrdinalIgnoreCase) && f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) ||
            f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
            f.StartsWith("core.", StringComparison.OrdinalIgnoreCase) ||
            f.Contains("crashdump", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".binlog", StringComparison.OrdinalIgnoreCase)).ToList();

        var message = $"No test result files found in work item '{workItem}'. Searched for: {patternsSearched}.";
        message += " Most .NET repos do NOT upload test results to Helix — use azdo_test_runs + azdo_test_results for structured results, or helix_search_log with repo-specific patterns.";

        if (crashArtifacts.Count > 0)
        {
            message += $" ⚠️ Crash artifacts detected: {string.Join(", ", crashArtifacts)}. The test host may have crashed. Try helix_search_log with pattern 'exit code' or 'SIGABRT'.";
        }
        else if (usefulFiles.Count > 0)
        {
            message += $" Available files: {string.Join(", ", usefulFiles)}.";
        }
        else if (fileNames.Count > 0)
        {
            message += $" {fileNames.Count} files found (mostly .log files). Try helix_search_log with common patterns: '[FAIL]', '  Failed' (2 leading spaces), 'Error Message:', or 'exit code'. Call helix_ci_guide with the repo name for repo-specific patterns.";
        }
        else
        {
            message += " The work item has no uploaded files.";
        }

        message += " Call helix_ci_guide with the repo name for recommended search patterns.";

        throw new HelixException(message);
    }

    /// <summary>Download specific files from a work item's file list.</summary>
    private async Task<List<string>> DownloadMatchingFilesAsync(
        List<IWorkItemFile> files, string workItem, string jobId,
        CancellationToken cancellationToken)
    {
        var idPrefix = jobId.Length >= 8 ? jobId[..8] : jobId;
        var outDir = Path.Combine(Path.GetTempPath(), $"helix-{idPrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var paths = new List<string>();

        foreach (var f in files)
        {
            await using var stream = await _api.GetFileAsync(f.Name, workItem, jobId, cancellationToken);
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

    /// <summary>Parse downloaded files strictly — XmlException propagates (preserves XXE detection on .trx).</summary>
    private static List<TrxParseResult> ParseDownloadedFiles(
        List<string> downloadedPaths, string? requestedFileName,
        bool includePassed, int maxResults)
    {
        var results = new List<TrxParseResult>();
        try
        {
            foreach (var path in downloadedPaths)
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > MaxSearchFileSizeBytes)
                    throw new HelixException($"Test result file '{Path.GetFileName(path)}' exceeds the {MaxSearchFileSizeBytes / (1024 * 1024)}MB size limit.");

                var parsedFileName = requestedFileName ?? Path.GetFileName(path);
                var parsed = TryParseTestFile(path, parsedFileName, includePassed, maxResults);
                if (parsed != null)
                    results.Add(parsed);
            }
        }
        finally
        {
            foreach (var p in downloadedPaths)
                try { File.Delete(p); } catch { }
        }

        return results;
    }

    /// <summary>Parse downloaded XML files best-effort — skip unrecognized/malformed files gracefully.</summary>
    private static List<TrxParseResult> ParseDownloadedFilesBestEffort(
        List<string> downloadedPaths, bool includePassed, int maxResults)
    {
        var results = new List<TrxParseResult>();
        try
        {
            foreach (var path in downloadedPaths)
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.Length > MaxSearchFileSizeBytes)
                    continue; // Skip oversized files in best-effort mode

                try
                {
                    var parsedFileName = Path.GetFileName(path);
                    var parsed = TryParseTestFile(path, parsedFileName, includePassed, maxResults);
                    if (parsed != null)
                        results.Add(parsed);
                }
                catch (XmlException)
                {
                    // Skip files that aren't valid XML or have security-blocked content
                    continue;
                }
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
            return name.AsSpan().EndsWith(pattern.AsSpan(1), StringComparison.OrdinalIgnoreCase);
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
