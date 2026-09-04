using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelixTool.Core.Helix;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Business logic layer for Azure DevOps operations.
/// Sits between MCP tools and <see cref="IAzdoApiClient"/>, orchestrating
/// URL resolution, multi-step API calls, and result shaping.
/// </summary>
public class AzdoService
{
    private readonly IAzdoApiClient _client;
    private readonly IHelixApiClient? _helixApi;
    private const string ValidFilterValues = "'failed', 'all', 'running', 'pending', 'incomplete', or 'issues'";
    private static readonly HashSet<string> s_validFilters = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed",
        "all",
        "running",
        "pending",
        "incomplete",
        "issues"
    };
    private static readonly Dictionary<string, string> s_filterAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inProgress"] = "running",
        ["in-progress"] = "running",
        ["active"] = "running",
        ["notStarted"] = "pending",
        ["not-started"] = "pending"
    };

    /// <summary>Valid queryOrder values accepted by the AzDO builds REST API.</summary>
    public static readonly IReadOnlyList<string> AzdoQueryOrders =
    [
        "queueTimeAscending",
        "queueTimeDescending",
        "startTimeAscending",
        "startTimeDescending",
        "finishTimeAscending",
        "finishTimeDescending"
    ];
    private static readonly HashSet<string> s_validQueryOrders = new(AzdoQueryOrders, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of <see cref="AzdoService"/> with timeline-only job detection.
    /// Unit tests use this constructor. Production prefers the two-argument overload.
    /// </summary>
    public AzdoService(IAzdoApiClient client)
        : this(client, null) { }

    /// <summary>
    /// Initializes a new instance of <see cref="AzdoService"/> with Helix-side job detection
    /// as the primary path (falling back to timeline scraping when the Helix query returns 0 results).
    /// </summary>
    /// <param name="client">AzDO API client for build/timeline queries.</param>
    /// <param name="helixApi">Helix API client for canonical <c>Job.ListAsync(source)</c> queries.
    ///   Pass <c>null</c> to use timeline scraping only.</param>
    public AzdoService(IAzdoApiClient client, IHelixApiClient? helixApi)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _helixApi = helixApi;
    }

    public static string NormalizeFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return s_filterAliases.TryGetValue(filter, out var canonical) ? canonical : filter;
    }

    public static bool IsValidFilter(string filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return s_validFilters.Contains(filter);
    }

    public static string GetInvalidFilterMessage(string filter, string parameterName = "filter") =>
        $"Invalid {parameterName} '{filter}'. Must be one of: {ValidFilterValues}.";

    public static void ValidateFilter(string filter, string parameterName = "filter")
    {
        if (!IsValidFilter(filter))
            throw new ArgumentException(GetInvalidFilterMessage(filter, parameterName), parameterName);
    }

    public static string? NormalizeQueryOrder(string? queryOrder)
    {
        if (queryOrder is null) return null;
        var trimmed = queryOrder.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsValidQueryOrder(string? queryOrder) =>
        queryOrder is null || s_validQueryOrders.Contains(queryOrder);

    public static string GetInvalidQueryOrderMessage(string queryOrder) =>
        $"Invalid queryOrder '{queryOrder}'. Must be one of: {string.Join(", ", AzdoQueryOrders)}.";

    public static bool MatchesFilter(AzdoTimelineRecord r, string filter) => filter.ToLowerInvariant() switch
    {
        "all" => true,
        "failed" => (r.Result is not null && !r.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            || r.Issues is { Count: > 0 },
        "running" => r.State?.Equals("inProgress", StringComparison.OrdinalIgnoreCase) == true,
        "pending" => r.State?.Equals("pending", StringComparison.OrdinalIgnoreCase) == true,
        "incomplete" => !string.Equals(r.State, "completed", StringComparison.OrdinalIgnoreCase),
        "issues" => r.Issues is { Count: > 0 },
        _ => throw new ArgumentException(GetInvalidFilterMessage(filter), nameof(filter))
    };

    /// <summary>
    /// Get a formatted build summary by build ID or AzDO URL.
    /// </summary>
    public async Task<AzdoBuildSummary> GetBuildSummaryAsync(string buildIdOrUrl, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        var build = await _client.GetBuildAsync(org, project, buildId, ct);
        if (build is null)
            throw new InvalidOperationException($"Build {buildId} not found in {org}/{project}.");

        TimeSpan? duration = (build.StartTime.HasValue && build.FinishTime.HasValue)
            ? build.FinishTime.Value - build.StartTime.Value
            : null;

        var webUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_build/results?buildId={buildId}";

        return new AzdoBuildSummary(
            Id: build.Id,
            BuildNumber: build.BuildNumber,
            Status: build.Status,
            Result: build.Result,
            DefinitionName: build.Definition?.Name,
            DefinitionId: build.Definition?.Id,
            SourceBranch: build.SourceBranch,
            SourceVersion: build.SourceVersion,
            QueueTime: build.QueueTime,
            StartTime: build.StartTime,
            FinishTime: build.FinishTime,
            Duration: duration,
            RequestedFor: build.RequestedFor?.DisplayName,
            WebUrl: webUrl);
    }

    /// <summary>
    /// List builds for an org/project with optional filters.
    /// </summary>
    public async Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(
        string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
    {
        return await _client.ListBuildsAsync(org, project, filter, ct);
    }

    /// <summary>
    /// Get the build timeline by build ID or AzDO URL.
    /// </summary>
    public async Task<AzdoTimeline?> GetTimelineAsync(string buildIdOrUrl, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        return await _client.GetTimelineAsync(org, project, buildId, ct);
    }

    /// <summary>
    /// Get build log content by build ID or AzDO URL and log ID.
    /// Optionally returns only the last N lines.
    /// </summary>
    public async Task<string?> GetBuildLogAsync(
        string buildIdOrUrl, int logId, int? tailLines = null, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);

        // Optimization: use lineCount metadata to fetch only the tail
        if (tailLines is > 0)
        {
            var logsList = await _client.GetBuildLogsListAsync(org, project, buildId, ct);
            var logEntry = logsList.FirstOrDefault(e => e.Id == logId);

            if (logEntry is not null && logEntry.LineCount > (long)tailLines.Value * 2)
            {
                var startLine = logEntry.LineCount - tailLines.Value;
                if (startLine > 0 && startLine <= int.MaxValue)
                {
                    return await _client.GetBuildLogAsync(org, project, buildId, logId,
                        startLine: (int)startLine, ct: ct);
                }
            }
        }

        // Fallback: fetch full log, trim client-side
        var content = await _client.GetBuildLogAsync(org, project, buildId, logId, ct: ct);

        if (content is null || tailLines is null or <= 0)
            return content;

        return StringHelpers.TailLines(content, tailLines.Value);
    }

    /// <summary>
    /// Get changes (commits) associated with a build.
    /// </summary>
    public async Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(
        string buildIdOrUrl, int? top = null, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        return await _client.GetBuildChangesAsync(org, project, buildId, top, ct);
    }

    /// <summary>
    /// Get test runs for a build.
    /// </summary>
    public async Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(
        string buildIdOrUrl, int? top = null, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        return await _client.GetTestRunsAsync(org, project, buildId, top, ct);
    }

    /// <summary>
    /// Get test results for a specific test run.
    /// Org/project are resolved from the buildIdOrUrl since runId is scoped to org/project.
    /// </summary>
    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(
        string buildIdOrUrl, int runId, int top = 200, string? outcomes = null, CancellationToken ct = default)
    {
        var (org, project, _) = AzdoIdResolver.Resolve(buildIdOrUrl);
        return await _client.GetTestResultsAsync(org, project, runId, top, outcomes, ct);
    }

    /// <summary>
    /// Get build artifacts by build ID or AzDO URL.
    /// Optionally filters by name pattern and limits result count.
    /// </summary>
    public async Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(
        string buildIdOrUrl, string pattern = "*", int top = 50, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
        var results = await _client.GetBuildArtifactsAsync(org, project, buildId, ct);

        if (pattern != "*")
            results = results.Where(a => StringHelpers.MatchesPattern(a.Name ?? string.Empty, pattern)).ToList();

        if (results.Count > top)
            results = results.Take(top).ToList();

        return results;
    }

    /// <summary>
    /// Search a build log for lines matching a pattern.
    /// Fetches the full log content, then applies <see cref="TextSearchHelper.SearchLines"/>.
    /// </summary>
    public async Task<LogSearchResult> SearchBuildLogAsync(
        string buildIdOrUrl, int logId, string pattern,
        int contextLines = 2, int maxMatches = 50,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMatches, 0);

        if (StringHelpers.IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        var content = await GetBuildLogAsync(buildIdOrUrl, logId, tailLines: null, ct);
        if (content is null)
            throw new InvalidOperationException($"Log {logId} not found for build '{buildIdOrUrl}'.");

        var lines = NormalizeAndSplit(content);
        return TextSearchHelper.SearchLines($"log:{logId}", lines, pattern, contextLines, maxMatches);
    }

    /// <summary>
    /// Search timeline records by pattern (case-insensitive substring match on record names and issue messages).
    /// </summary>
    public async Task<TimelineSearchResult> SearchTimelineAsync(
        string buildIdOrUrl, string pattern,
        string? recordType = null,
        string? resultFilter = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        if (recordType is not null &&
            !recordType.Equals("Stage", StringComparison.OrdinalIgnoreCase) &&
            !recordType.Equals("Job", StringComparison.OrdinalIgnoreCase) &&
            !recordType.Equals("Task", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid recordType '{recordType}'. Must be 'Stage', 'Job', or 'Task'.", nameof(recordType));
        }

        resultFilter ??= "failed";
        resultFilter = NormalizeFilter(resultFilter);
        ValidateFilter(resultFilter, nameof(resultFilter));

        var timeline = await GetTimelineAsync(buildIdOrUrl, ct);
        if (timeline is null)
            return new TimelineSearchResult
            {
                Build = buildIdOrUrl,
                Pattern = pattern,
                Note = $"No timeline available for build {buildIdOrUrl}. The build may still be initializing, was canceled before any leg reported, or has no timeline data."
            };

        var records = timeline.Records;
        var recordById = records
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

        var matches = new List<TimelineSearchMatch>();

        foreach (var r in records)
        {
            // Apply recordType filter
            if (recordType is not null && !string.Equals(r.Type, recordType, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!MatchesFilter(r, resultFilter))
                continue;

            // Search record name
            bool nameMatches = r.Name is not null &&
                r.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase);

            // Search issue messages
            var matchedIssues = new List<string>();
            if (r.Issues is { Count: > 0 })
            {
                foreach (var issue in r.Issues)
                {
                    if (issue.Message is not null &&
                        issue.Message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedIssues.Add(issue.Message);
                    }
                }
            }

            if (!nameMatches && matchedIssues.Count == 0)
                continue;

            // Resolve parent name for context
            string? parentName = null;
            if (r.ParentId is not null && recordById.TryGetValue(r.ParentId, out var parent))
                parentName = parent.Name;

            // Compute duration
            TimeSpan? duration = (r.StartTime.HasValue && r.FinishTime.HasValue)
                ? r.FinishTime.Value - r.StartTime.Value
                : null;

            matches.Add(new TimelineSearchMatch
            {
                RecordId = r.Id ?? "",
                Name = r.Name ?? "",
                Type = r.Type ?? "",
                State = r.State,
                Result = r.Result,
                Duration = duration.HasValue ? FormatDuration(duration.Value) : null,
                LogId = r.Log?.Id,
                MatchedIssues = matchedIssues,
                ParentName = parentName,
                Record = r
            });
        }

        return new TimelineSearchResult
        {
            Build = buildIdOrUrl,
            Pattern = pattern,
            TotalRecords = records.Count,
            MatchCount = matches.Count,
            Matches = matches
        };
    }

    /// <summary>
    /// Search all log steps in a build for a pattern, ranked by failure likelihood with early termination.
    /// Fetches timeline and logs list metadata in parallel, builds a ranked queue (failed → issues →
    /// succeededWithIssues → succeeded → orphans), then searches sequentially until maxMatches is reached.
    /// </summary>
    public async Task<CrossStepSearchResult> SearchBuildLogAcrossStepsAsync(
        string buildIdOrUrl, string pattern,
        int contextLines = 2, int maxMatches = 50,
        int maxLogsToSearch = 30, int minLogLines = 5,
        IProgress<ProgressUpdate>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMatches, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLogsToSearch, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(minLogLines);

        if (StringHelpers.IsFileSearchDisabled)
            throw new InvalidOperationException("File content search is disabled by configuration.");

        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);

        // Phase 1: Parallel metadata fetch
        var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
        var logsListTask = _client.GetBuildLogsListAsync(org, project, buildId, ct);
        await Task.WhenAll(timelineTask, logsListTask);

        var timeline = await timelineTask;
        var logsList = await logsListTask;

        // Build lookup: logEntry.Id → logEntry
        var logEntryById = new Dictionary<int, AzdoBuildLogEntry>();
        foreach (var entry in logsList)
            logEntryById[entry.Id] = entry;

        // Build lookup: recordById for parent resolution
        var records = timeline?.Records ?? [];
        var recordById = records
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

        // Phase 2: Build ranked log queue
        var referencedLogIds = new HashSet<int>();
        var buckets = new List<(int bucket, long lineCount, int logId, AzdoTimelineRecord record)>();

        foreach (var r in records)
        {
            if (r.Log is null) continue;
            var logId = r.Log.Id;
            referencedLogIds.Add(logId);

            if (!logEntryById.TryGetValue(logId, out var logEntry))
                continue;

            if (logEntry.LineCount < minLogLines)
                continue;

            int bucket;
            if (r.Result is not null &&
                (r.Result.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                 r.Result.Equals("canceled", StringComparison.OrdinalIgnoreCase)))
            {
                bucket = 0;
            }
            else if (r.Issues is { Count: > 0 })
            {
                bucket = 1;
            }
            else if (r.Result is not null &&
                     r.Result.Equals("succeededWithIssues", StringComparison.OrdinalIgnoreCase))
            {
                bucket = 2;
            }
            else
            {
                bucket = 3;
            }

            buckets.Add((bucket, logEntry.LineCount, logId, r));
        }

        // Orphan logs (Bucket 4): in logs list but not referenced by any timeline record
        foreach (var entry in logsList)
        {
            if (referencedLogIds.Contains(entry.Id))
                continue;
            if (entry.LineCount < minLogLines)
                continue;
            // Create a synthetic entry — no timeline record available
            buckets.Add((4, entry.LineCount, entry.Id, new AzdoTimelineRecord { Name = $"log:{entry.Id}" }));
        }

        // Sort: by bucket ascending, then by lineCount descending within bucket
        buckets.Sort((a, b) =>
        {
            int cmp = a.bucket.CompareTo(b.bucket);
            if (cmp != 0) return cmp;
            return b.lineCount.CompareTo(a.lineCount);
        });

        // Phase 3: Incremental search with early termination
        var remainingMatches = maxMatches;
        var logsSearched = 0;
        var steps = new List<StepSearchResult>();

        // We'll scan up to min(buckets.Count, maxLogsToSearch) entries.
        var plannedScan = Math.Min(buckets.Count, maxLogsToSearch);
        var step = ProgressReporter.ItemStep(plannedScan);
        progress?.Report(new ProgressUpdate(0, plannedScan,
            plannedScan == 0 ? "No logs to search" : $"Searching up to {plannedScan} log step(s)"));

        foreach (var (_, lineCount, logId, record) in buckets)
        {
            if (remainingMatches <= 0 || logsSearched >= maxLogsToSearch)
                break;

            var content = await _client.GetBuildLogAsync(org, project, buildId, logId, ct: ct);

            if (content is null)
                continue;

            logsSearched++;

            var lines = NormalizeAndSplit(content);
            var searchResult = TextSearchHelper.SearchLines(
                identifier: $"log:{logId}",
                lines: lines,
                pattern: pattern,
                contextLines: contextLines,
                maxMatches: remainingMatches);

            if (searchResult.Matches.Count > 0)
            {
                // Resolve parent name
                string? parentName = null;
                if (record.ParentId is not null && recordById.TryGetValue(record.ParentId, out var parent))
                    parentName = parent.Name;

                steps.Add(new StepSearchResult
                {
                    LogId = logId,
                    StepName = record.Name ?? $"log:{logId}",
                    StepType = record.Type,
                    StepResult = record.Result,
                    ParentName = parentName,
                    LineCount = lineCount,
                    MatchCount = searchResult.Matches.Count,
                    Matches = searchResult.Matches
                });

                remainingMatches -= searchResult.Matches.Count;
            }

            if (progress is not null && (logsSearched % step == 0 || logsSearched == plannedScan))
            {
                var totalMatchesSoFar = steps.Sum(s => s.MatchCount);
                progress.Report(new ProgressUpdate(logsSearched, plannedScan,
                    $"Searched {logsSearched} of {plannedScan} log step(s) ({totalMatchesSoFar} match(es))"));
            }
        }

        var totalEligible = buckets.Count;
        var totalMatchCount = steps.Sum(s => s.MatchCount);
        var stoppedEarly = remainingMatches <= 0 || (logsSearched >= maxLogsToSearch && logsSearched < totalEligible);

        return new CrossStepSearchResult
        {
            Build = buildIdOrUrl,
            Pattern = pattern,
            TotalLogsInBuild = logsList.Count,
            LogsSearched = logsSearched,
            LogsSkipped = totalEligible - logsSearched,
            TotalMatchCount = totalMatchCount,
            StoppedEarly = stoppedEarly,
            Steps = steps
        };
    }

    private static readonly SearchValues<char> s_lineBreakChars = SearchValues.Create("\r\n");

    /// <summary>
    /// Split content into lines handling \r\n, \r, and \n in a single pass with no
    /// intermediate string copies. Uses SearchValues for fast scanning.
    /// </summary>
    private static string[] NormalizeAndSplit(string content)
    {
        var span = content.AsSpan();
        var lines = new List<string>();

        while (span.Length > 0)
        {
            var idx = span.IndexOfAny(s_lineBreakChars);
            if (idx < 0)
            {
                lines.Add(span.ToString());
                break;
            }

            lines.Add(span[..idx].ToString());

            if (idx < span.Length - 1 && span[idx] == '\r' && span[idx + 1] == '\n')
                span = span[(idx + 2)..]; // skip \r\n
            else
                span = span[(idx + 1)..]; // skip \r or \n
        }

        // Remove trailing empty element if content ends with a newline,
        // but only when there is more than one line (preserves [""] for "\n" input)
        if (lines.Count > 1 && content.Length > 0 &&
            (content[^1] == '\n' || content[^1] == '\r'))
        {
            if (lines[^1].Length == 0)
                lines.RemoveAt(lines.Count - 1);
        }

        return [.. lines];
    }

    private static string FormatDuration(TimeSpan duration)
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

    /// <summary>
    /// Get test result attachments for a specific test result.
    /// Org/project are provided explicitly since runId/resultId are scoped to org/project.
    /// </summary>
    public async Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(
        string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
    {
        var results = await _client.GetTestAttachmentsAsync(org, project, runId, resultId, top, ct);
        if (results.Count <= top)
            return results;
        return results.Take(top).ToList();
    }

    private static readonly Regex s_gitHubIssueUrlRegex = new(
        @"https://github\.com/(?<repo>[^/]+/[^/]+)/issues/(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extract Build Analysis known issue data from build tags and timeline issue messages.
    /// Build Analysis (used in dotnet/runtime and other repos) adds tags and timeline annotations
    /// when test failures match known GitHub issues.
    /// </summary>
    public async Task<BuildAnalysisResult> GetBuildAnalysisAsync(string buildIdOrUrl, CancellationToken ct = default)
    {
        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);

        var buildTask = _client.GetBuildAsync(org, project, buildId, ct);
        var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
        await Task.WhenAll(buildTask, timelineTask);

        var build = await buildTask;
        if (build is null)
            throw new InvalidOperationException($"Build {buildId} not found in {org}/{project}.");

        var timeline = await timelineTask;

        // Collect known issues from build tags (e.g., "Known test failure: <url>")
        var knownByUrl = new Dictionary<string, KnownIssueMatch>(StringComparer.OrdinalIgnoreCase);
        var analysisSources = new List<string>();

        if (build.Tags is { Count: > 0 })
        {
            foreach (var tag in build.Tags)
            {
                var match = s_gitHubIssueUrlRegex.Match(tag);
                if (match.Success)
                {
                    var issueUrl = match.Value;
                    if (!knownByUrl.ContainsKey(issueUrl))
                    {
                        knownByUrl[issueUrl] = new KnownIssueMatch
                        {
                            IssueNumber = int.Parse(match.Groups["num"].Value),
                            Repository = match.Groups["repo"].Value,
                            IssueUrl = issueUrl,
                            IssueTitle = ExtractIssueTitleFromTag(tag, issueUrl),
                            MatchedFailures = []
                        };
                    }
                }
            }
            if (knownByUrl.Count > 0)
                analysisSources.Add("build tags");
        }

        // Scan timeline issue messages for Build Analysis annotations with GitHub issue URLs
        var unmatchedFailures = new List<string>();

        if (timeline?.Records is { Count: > 0 })
        {
            foreach (var record in timeline.Records)
            {
                if (record.Issues is not { Count: > 0 })
                    continue;

                foreach (var issue in record.Issues)
                {
                    if (issue.Message is null)
                        continue;

                    var urlMatch = s_gitHubIssueUrlRegex.Match(issue.Message);
                    if (urlMatch.Success)
                    {
                        var issueUrl = urlMatch.Value;
                        if (!knownByUrl.TryGetValue(issueUrl, out var existing))
                        {
                            existing = new KnownIssueMatch
                            {
                                IssueNumber = int.Parse(urlMatch.Groups["num"].Value),
                                Repository = urlMatch.Groups["repo"].Value,
                                IssueUrl = issueUrl,
                                MatchedFailures = []
                            };
                            knownByUrl[issueUrl] = existing;
                        }
                        var failureContext = record.Name ?? issue.Message;
                        if (!existing.MatchedFailures.Contains(failureContext, StringComparer.OrdinalIgnoreCase))
                            existing.MatchedFailures.Add(failureContext);
                    }
                    else if (issue.Type is "error" &&
                             record.Result is not null &&
                             !record.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                    {
                        unmatchedFailures.Add($"[{record.Name}] {issue.Message}");
                    }
                }
            }

            if (knownByUrl.Count > 0 && !analysisSources.Contains("build tags"))
                analysisSources.Add("timeline issues");
            else if (knownByUrl.Count > 0 && analysisSources.Contains("build tags"))
                analysisSources.Add("timeline issues");
        }

        return new BuildAnalysisResult
        {
            BuildId = buildId.ToString(),
            BuildResult = build.Result,
            KnownIssues = knownByUrl.Values.ToList(),
            UnmatchedFailures = unmatchedFailures,
            AnalysisSource = analysisSources.Count > 0 ? string.Join(", ", analysisSources) : null
        };
    }

    /// <summary>
    /// Try to extract a human-readable issue title from a build tag.
    /// Tags often look like "Known test failure: Title (url)" or "Known test failure: url".
    /// </summary>
    private static string? ExtractIssueTitleFromTag(string tag, string issueUrl)
    {
        // Strip the URL from the tag to see if there's a title
        var withoutUrl = tag.Replace(issueUrl, "", StringComparison.OrdinalIgnoreCase).Trim();

        // Remove common prefixes
        foreach (var prefix in new[] { "Known test failure:", "Known Build Error:", "Known issue:" })
        {
            if (withoutUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                withoutUrl = withoutUrl[prefix.Length..].Trim();
                break;
            }
        }

        // Clean up surrounding punctuation/parentheses
        withoutUrl = withoutUrl.Trim('(', ')', '-', ' ');
        return string.IsNullOrWhiteSpace(withoutUrl) ? null : withoutUrl;
    }

    // Matches Helix job GUIDs in URLs like helix.dot.net/api/.../jobs/{guid}/...
    private static readonly Regex HelixJobIdRegex = new(
        @"jobs/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Matches "Work item {name} in job {guid} has failed"
    private static readonly Regex FailedWorkItemRegex = new(
        @"Work item (.+?) in job ([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}) has failed",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonitorFailedWorkItemRegex = new(
        @"Work item '(?<wi>[^']+)' in job '(?<job>[^']*)' failed \((?<state>[^)]*)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MonitorFailureTreeLineRegex = new(
        @"^\s*(?:├─|└─)\s*(?<wi>.*?) \(Job: (?<rest>.*)\) \((?<state>[^)]*)\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JobGuidInTextRegex = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Compute the Helix source string for a build, mirroring arcade's
    /// <c>HelixJobSource.Compute</c> (JobMonitor/HelixJobSource.cs).
    ///
    /// Format: {prefix}/{teamProject}/{repository}/{sourceBranch}
    ///
    /// Prefix rules (case-insensitive, matching arcade exactly):
    ///   Build.Reason == "PullRequest"       → "pr"
    ///   System.TeamProject == "internal"    → "official"
    ///   anything else                       → "ci"
    ///
    /// Note: "official" is keyed off the team project name, not Build.Reason.
    /// A manually-queued internal build produces "official/internal/...".
    /// A public manual/scheduled/individualCI/batchedCI build produces "ci/public/...".
    /// </summary>
    internal static string ComputeHelixSource(AzdoBuild build)
    {
        var teamProject = build.Project?.Name ?? "";
        var repository  = build.Repository?.Name ?? "";
        var sourceBranch = build.SourceBranch ?? "";

        string prefix;
        if (string.Equals(build.Reason, "pullRequest", StringComparison.OrdinalIgnoreCase))
            prefix = "pr";
        else if (string.Equals(teamProject, "internal", StringComparison.OrdinalIgnoreCase))
            prefix = "official";
        else
            prefix = "ci";

        return $"{prefix}/{teamProject}/{repository}/{sourceBranch}";
    }

    /// <summary>
    /// Extract Helix job IDs for a build, using Helix-side <c>Job.ListAsync(source)</c>
    /// as the primary path when a <see cref="IHelixApiClient"/> is available.
    /// Falls back to AzDO timeline task-name scraping when the Helix query returns 0 results
    /// or when no Helix client was injected.
    /// </summary>
    public async Task<HelixJobsFromBuildResult> GetHelixJobsAsync(
        string buildIdOrUrl, string filter = "failed", CancellationToken ct = default)
    {
        filter ??= "failed";
        filter = NormalizeFilter(filter);
        ValidateFilter(filter, nameof(filter));

        // ── Primary path: Helix-side Job.ListAsync(source) + BuildId filter ─────────────
        // Available when IHelixApiClient is injected (production). Unit tests use the
        // timeline-only constructor and skip this block.
        string? source = null;
        if (_helixApi != null)
        {
            var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);
            var build = await _client.GetBuildAsync(org, project, buildId, ct);
            if (build != null)
            {
                source = ComputeHelixSource(build);
                IReadOnlyList<IHelixJobSummary>? jobSummaries = null;
                try
                {
                    jobSummaries = await _helixApi.ListJobsByBuildAsync(
                        source, buildId.ToString(), count: 100_000, ct: ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Helix API unreachable or auth failure — fall through to timeline.
                }

                if (jobSummaries is { Count: > 0 })
                {
                    var result = BuildHelixResultFromJobSummaries(
                        buildIdOrUrl, source, jobSummaries, filter);
                    AzdoTimeline? timeline;
                    try
                    {
                        timeline = await _client.GetTimelineAsync(
                            org, project, buildId, ct);
                    }
                    catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
                    {
                        return result with
                        {
                            Note = AppendNote(result.Note,
                                $"AzDO timeline issue evidence is unavailable: {ex.Message}")
                        };
                    }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                    {
                        return result with
                        {
                            Note = AppendNote(result.Note,
                                $"AzDO timeline issue evidence is unavailable: {ex.Message}")
                        };
                    }
                    catch (JsonException ex) when (!ct.IsCancellationRequested)
                    {
                        return result with
                        {
                            Note = AppendNote(result.Note,
                                $"AzDO timeline issue evidence is unavailable: {ex.Message}")
                        };
                    }
                    catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
                    {
                        return result with
                        {
                            Note = AppendNote(result.Note,
                                $"AzDO timeline issue evidence is unavailable: {ex.Message}")
                        };
                    }

                    if (timeline is null)
                    {
                        return result with
                        {
                            Note = AppendNote(result.Note,
                                "AzDO timeline issue evidence is unavailable.")
                        };
                    }

                    return result with
                    {
                        TimelineIssues = BuildTimelineIssues(timeline)
                    };
                }

                // 0 results: fall through to timeline scraping.
                // Typical causes: in-progress build (jobs not yet submitted), very old
                // jobs aged out of the Helix query window, or BuildId property missing.
            }
        }

        // ── Fallback path: AzDO timeline task-name scraping ─────────────────────────────
        // Used when Helix query is unavailable, returns 0 results, or throws.
        // Fragile for repos that don't name their Helix dispatch task with "helix"
        // (e.g. dotnet/sdk uses "🟣 Run TestBuild Tests").
        // `source` carries over from the primary attempt above when one was made, so the
        // wire result still reports the computed Helix source even on the fallback path.
        return await GetHelixJobsViaTimelineAsync(buildIdOrUrl, filter, ct, source);
    }

    /// <summary>Project Helix job summaries into a build-level result.</summary>
    private static HelixJobsFromBuildResult BuildHelixResultFromJobSummaries(
        string buildIdOrUrl,
        string source,
        IReadOnlyList<IHelixJobSummary> jobSummaries,
        string filter)
    {
        var supersededNames = jobSummaries
            .Select(job => job.PreviousHelixJobName)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var jobs = jobSummaries
            .Select(job =>
            {
                var isCompleted = !string.IsNullOrEmpty(job.Finished);
                return new HelixJobFromBuild(
                    HelixJobId: job.Name,
                    ParentJobName: ResolveParentJobName(job),
                    Result: isCompleted ? "completed" : "running",
                    FailedWorkItems: [])
                {
                    State = isCompleted ? "completed" : "running",
                    QueueId = job.QueueId,
                    WorkItemCount = job.InitialWorkItemCount,
                    Superseded = supersededNames.Contains(job.Name)
                };
            })
            .ToList();

        var notes = new List<string>
        {
            "Helix-side Result reports completion state, not pass/fail outcome."
        };
        if (!string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase))
            notes.Add($"filter='{filter}' is not applied on the Helix-side path; all {jobs.Count} job(s) for this build are returned. Use helix_status on individual job IDs to determine pass/fail.");
        if (jobs.Any(job => job.State == "running"))
            notes.Add("The build is still in progress; Helix job outcomes remain unknown.");

        return new HelixJobsFromBuildResult(
            BuildId: buildIdOrUrl,
            TotalHelixJobs: jobs.Count,
            FailedHelixJobs: 0,
            Jobs: jobs)
        {
            Note = string.Join(" ", notes),
            Source = source,
            Strategy = "helix",
            OutcomeUnknownHelixJobs = jobs.Count
        };
    }

    private static string ResolveParentJobName(IHelixJobSummary job)
    {
        if (!string.IsNullOrEmpty(job.PhaseName))
            return job.PhaseName;
        if (!string.IsNullOrEmpty(job.JobDisplayName))
            return job.JobDisplayName;
        if (!string.IsNullOrEmpty(job.JobName)
            && !string.Equals(job.JobName, "__default", StringComparison.Ordinal))
        {
            return job.JobName;
        }

        return "";
    }

    /// <summary>
    /// Legacy timeline-scraping implementation. Finds Helix dispatch tasks by scanning for
    /// timeline records whose name contains "helix". Fragile for repos that use non-standard
    /// task names (e.g. dotnet/sdk).
    /// </summary>
    /// <param name="source">
    /// Helix source string already computed by the caller (<see cref="ComputeHelixSource"/>),
    /// when the primary Helix-side path was attempted first and returned 0 results. Null when
    /// no <see cref="IHelixApiClient"/> was available or the build could not be resolved —
    /// in that case the source is genuinely unknown and the field stays absent from the wire
    /// result (<c>JsonIgnore(WhenWritingNull)</c>).
    /// </param>
    private async Task<HelixJobsFromBuildResult> GetHelixJobsViaTimelineAsync(
        string buildIdOrUrl, string filter, CancellationToken ct, string? source = null)
    {
        var timeline = await GetTimelineAsync(buildIdOrUrl, ct);
        if (timeline is null)
            return new HelixJobsFromBuildResult(buildIdOrUrl, 0, 0, [])
            {
                Note = $"No timeline available for build {buildIdOrUrl} — Helix jobs cannot be discovered via the timeline. The build may still be initializing, was canceled before any leg reported, or has no timeline data.",
                Source = source,
                Strategy = "timeline"
            };

        var recordById = timeline.Records
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

        // Find Task records whose name contains "helix" (covers "Send to Helix", "Send job to helix", etc.)
        var helixTasks = timeline.Records
            .Where(r => string.Equals(r.Type, "Task", StringComparison.OrdinalIgnoreCase)
                        && r.Name is not null
                        && r.Name.Contains("helix", StringComparison.OrdinalIgnoreCase));

        var jobs = new List<HelixJobFromBuild>();
        var preserveIssuesGate = filter.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("issues", StringComparison.OrdinalIgnoreCase)
            || filter.Equals("all", StringComparison.OrdinalIgnoreCase);

        foreach (var task in helixTasks)
        {
            if (!MatchesFilter(task, filter))
                continue;

            var hasIssues = task.Issues is { Count: > 0 };
            if (!hasIssues && preserveIssuesGate)
                continue;

            // Collect all job IDs and failed work items from this task's issues
            var jobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failedWorkItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            if (hasIssues)
            {
                foreach (var issue in task.Issues!)
                {
                    if (issue.Message is null)
                        continue;

                    // Extract job IDs
                    foreach (Match m in HelixJobIdRegex.Matches(issue.Message))
                    {
                        var jobId = m.Groups[1].Value;
                        jobIds.Add(jobId);
                    }

                    // Extract failed work item names and associate with their own job.
                    foreach (Match wiMatch in FailedWorkItemRegex.Matches(issue.Message))
                    {
                        var workItemName = wiMatch.Groups[1].Value;
                        var jobId = wiMatch.Groups[2].Value;
                        jobIds.Add(jobId);
                        AddFailedWorkItem(failedWorkItems, jobId, workItemName);
                    }

                    foreach (Match wiMatch in MonitorFailedWorkItemRegex.Matches(issue.Message))
                    {
                        var jobId = RecoverMonitorJobId(
                            wiMatch.Groups["job"].Value, issue.Message);
                        if (jobId is not null)
                        {
                            jobIds.Add(jobId);
                            AddFailedWorkItem(
                                failedWorkItems, jobId, wiMatch.Groups["wi"].Value);
                        }
                    }

                    foreach (var line in issue.Message.Split('\n'))
                    {
                        var treeMatch = MonitorFailureTreeLineRegex.Match(line);
                        if (!treeMatch.Success)
                            continue;

                        var jobId = RecoverMonitorJobId(
                            treeMatch.Groups["rest"].Value, issue.Message);
                        if (jobId is not null)
                        {
                            jobIds.Add(jobId);
                            AddFailedWorkItem(
                                failedWorkItems, jobId, treeMatch.Groups["wi"].Value);
                        }
                    }
                }
            }

            // Resolve parent job name for context
            string parentName = "";
            if (task.ParentId is not null && recordById.TryGetValue(task.ParentId, out var parent))
                parentName = parent.Name ?? "";

            string taskResult = task.Result ?? "unknown";
            string? taskState = NormalizeTaskState(task.State);
            int taskErrorCount = task.Issues?.Count(issue =>
                string.Equals(issue.Type, "error", StringComparison.OrdinalIgnoreCase)) ?? 0;
            int taskWarningCount = task.Issues?.Count(issue =>
                string.Equals(issue.Type, "warning", StringComparison.OrdinalIgnoreCase)) ?? 0;

            if (jobIds.Count == 0)
            {
                jobs.Add(new HelixJobFromBuild(
                    HelixJobId: string.Empty,
                    ParentJobName: parentName,
                    Result: taskResult,
                    FailedWorkItems: [])
                {
                    State = taskState,
                    TaskErrorCount = taskErrorCount,
                    TaskWarningCount = taskWarningCount,
                    Messages = BuildIssueMessages(task.Issues)
                });
                continue;
            }

            foreach (var jobId in jobIds)
            {
                failedWorkItems.TryGetValue(jobId, out var items);
                jobs.Add(new HelixJobFromBuild(
                    HelixJobId: jobId,
                    ParentJobName: parentName,
                    Result: taskResult,
                    FailedWorkItems: items ?? [])
                {
                    State = taskState,
                    TaskErrorCount = taskErrorCount,
                    TaskWarningCount = taskWarningCount
                });
            }
        }

        // Apply filter
        if (filter.Equals("failed", StringComparison.OrdinalIgnoreCase))
        {
            jobs = jobs
                .Where(j => !j.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        int failedCount = jobs.Count(j => !j.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase));

        string? note = null;
        if (jobs.Any(job => job.State == "running"))
            note = "The build is still in progress; FailedHelixJobs includes not-yet-complete rows.";

        return new HelixJobsFromBuildResult(
            BuildId: buildIdOrUrl,
            TotalHelixJobs: jobs.Count,
            FailedHelixJobs: failedCount,
            Jobs: jobs)
        {
            Note = note,
            Source = source,
            Strategy = "timeline",
            TimelineIssues = BuildTimelineIssues(timeline)
        };
    }

    private static List<HelixTimelineIssue> BuildTimelineIssues(AzdoTimeline timeline)
    {
        var recordById = timeline.Records
            .Where(record => record.Id is not null)
            .ToDictionary(record => record.Id!, StringComparer.OrdinalIgnoreCase);

        return timeline.Records
            .Where(record =>
                string.Equals(record.Type, "Task", StringComparison.OrdinalIgnoreCase)
                && record.Issues is { Count: > 0 })
            .Select(record =>
            {
                var parentJobName = "";
                if (record.ParentId is not null
                    && recordById.TryGetValue(record.ParentId, out var parent)
                    && string.Equals(parent.Type, "Job", StringComparison.OrdinalIgnoreCase))
                {
                    parentJobName = parent.Name ?? "";
                }

                return new HelixTimelineIssue(
                    RecordId: record.Id ?? "",
                    TaskName: record.Name ?? "",
                    ParentJobName: parentJobName,
                    State: NormalizeTaskState(record.State),
                    Result: record.Result ?? "unknown",
                    ErrorCount: record.Issues!.Count(issue =>
                        string.Equals(issue.Type, "error", StringComparison.OrdinalIgnoreCase)),
                    WarningCount: record.Issues!.Count(issue =>
                        string.Equals(issue.Type, "warning", StringComparison.OrdinalIgnoreCase)),
                    Messages: BuildIssueMessages(record.Issues) ?? []);
            })
            .ToList();
    }

    private static string AppendNote(string? note, string addition) =>
        string.IsNullOrWhiteSpace(note) ? addition : $"{note} {addition}";

    private static string? RecoverMonitorJobId(string jobText, string message)
    {
        var directMatch = JobGuidInTextRegex.Match(jobText);
        if (directMatch.Success)
            return directMatch.Value;

        var messageJobIds = HelixJobIdRegex.Matches(message)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return messageJobIds.Count == 1 ? messageJobIds[0] : null;
    }

    private static void AddFailedWorkItem(
        Dictionary<string, List<string>> failedWorkItems,
        string jobId,
        string workItemName)
    {
        if (!failedWorkItems.TryGetValue(jobId, out var items))
        {
            items = [];
            failedWorkItems[jobId] = items;
        }

        if (!items.Contains(workItemName, StringComparer.OrdinalIgnoreCase))
            items.Add(workItemName);
    }

    private static List<string>? BuildIssueMessages(IReadOnlyList<AzdoIssue>? issues)
    {
        var messages = issues?
            .Select(issue => issue.Message)
            .Where(message => message is not null)
            .Select(message =>
            {
                var trimmed = message!.Trim();
                return trimmed.Length <= 500 ? trimmed : trimmed[..500];
            })
            .ToList() ?? [];

        if (messages.Count == 0)
            return null;

        const int limit = 20;
        if (messages.Count <= limit)
            return messages;

        var omitted = messages.Count - limit;
        var bounded = messages.Take(limit).ToList();
        bounded.Add($"… {omitted} more issue(s) omitted");
        return bounded;
    }

    private static string? NormalizeTaskState(string? state)
    {
        if (state is null)
            return null;
        if (state.Equals("inProgress", StringComparison.OrdinalIgnoreCase))
            return "running";
        if (state.Equals("completed", StringComparison.OrdinalIgnoreCase))
            return "completed";
        if (state.Equals("pending", StringComparison.OrdinalIgnoreCase))
            return "pending";
        return state.ToLowerInvariant();
    }

    // Valid filters for AzDO timeline Job records; "none" selects an unset (null) result.
    private static readonly HashSet<string> s_validJobResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "canceled", "abandoned", "skipped", "succeededWithIssues", "succeeded", "none"
    };

    /// <summary>
    /// Produce a deterministic, read-only evidence plan for a build:
    /// maps every failed/canceled Job record to its matching artifact candidate(s).
    /// Three cached GETs (build, timeline, artifacts); no downloads.
    /// </summary>
    public async Task<AzdoEvidencePlan> GetEvidencePlanAsync(
        string buildIdOrUrl,
        AzdoEvidencePlanOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Validate match strategy
        if (!AzdoEvidenceMatchStrategy.AllValues.Contains(options.Match, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Invalid match strategy '{options.Match}'. " +
                $"Must be one of: {string.Join(", ", AzdoEvidenceMatchStrategy.AllValues)}.",
                nameof(options));
        }

        // Validate job-result values
        foreach (var result in options.JobResults)
        {
            if (!s_validJobResults.Contains(result))
                throw new ArgumentException(
                    $"Invalid job result '{result}'. " +
                    $"Must be one of: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none.",
                    nameof(options));
        }

        var (org, project, buildId) = AzdoIdResolver.Resolve(buildIdOrUrl);

        // Three cached GETs in parallel
        var buildTask = _client.GetBuildAsync(org, project, buildId, ct);
        var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
        var artifactsTask = _client.GetBuildArtifactsAsync(org, project, buildId, ct);
        await Task.WhenAll(buildTask, timelineTask, artifactsTask);

        var build = await buildTask;
        if (build is null)
            throw new InvalidOperationException($"Build {buildId} not found in {org}/{project}.");

        var timeline = await timelineTask;
        if (timeline is null || timeline.Records.Count == 0)
            throw new InvalidOperationException($"No timeline available for build {buildId} in {org}/{project}.");

        var allArtifacts = await artifactsTask;

        // Filter artifacts by pattern
        var filteredArtifacts = options.ArtifactPattern == "*"
            ? allArtifacts
            : allArtifacts
                .Where(a => StringHelpers.MatchesPattern(a.Name ?? string.Empty, options.ArtifactPattern))
                .ToList();

        // Run the matching algorithm (filtering by type/result is done inside BuildPlan)
        var planResult = AzdoEvidenceMatcher.BuildPlan(
            timeline.Records, filteredArtifacts, options);

        var provenance = AzdoBuildProvenance.FromBuild(build, org, project);

        var buildIncomplete = !string.Equals(build.Status, "completed", StringComparison.OrdinalIgnoreCase);
        var allWarnings = new List<string>(planResult.Warnings.Count + 1);
        var warningSet = new HashSet<string>(StringComparer.Ordinal);

        void AddWarning(string warning)
        {
            if (warningSet.Add(warning))
                allWarnings.Add(warning);
        }

        if (buildIncomplete)
        {
            AddWarning(
                "Build is not completed; this evidence plan is a point-in-time snapshot and may change.");
        }
        foreach (var warning in planResult.Warnings)
            AddWarning(warning);

        var warningTotal = allWarnings.Count;
        var warnings = allWarnings
            .Take(AzdoEvidencePlan.MaxWarnings)
            .ToList();

        return new AzdoEvidencePlan
        {
            BuildId = build.Id,
            Build = provenance,
            BuildIncomplete = buildIncomplete,
            MatchStrategy = planResult.MatchStrategy,
            JobResultsFilter = options.JobResults,
            ArtifactPattern = options.ArtifactPattern,
            ArtifactJobPrefix = options.ArtifactJobPrefix,
            StripAttemptPrefix = options.StripAttemptPrefix,
            Entries = planResult.Entries,
            Complete = planResult.Complete,
            IncompleteReasons = planResult.IncompleteReasons,
            Warnings = warnings,
            WarningTotal = warningTotal,
            WarningsTruncated = warningTotal > warnings.Count,
            Truncated = planResult.Truncated,
            TotalEntries = planResult.Truncated ? planResult.Total : null,
            Note = planResult.Note,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }
}
