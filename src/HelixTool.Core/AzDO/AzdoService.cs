using System.Buffers;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Business logic layer for Azure DevOps operations.
/// Sits between MCP tools and <see cref="IAzdoApiClient"/>, orchestrating
/// URL resolution, multi-step API calls, and result shaping.
/// </summary>
public class AzdoService
{
    private readonly IAzdoApiClient _client;

    public AzdoService(IAzdoApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

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
        string buildIdOrUrl, int runId, int top = 200, CancellationToken ct = default)
    {
        var (org, project, _) = AzdoIdResolver.Resolve(buildIdOrUrl);
        return await _client.GetTestResultsAsync(org, project, runId, top, ct);
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
            results = results.Where(a => HelixService.MatchesPattern(a.Name ?? string.Empty, pattern)).ToList();

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

        if (HelixService.IsFileSearchDisabled)
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

        if (resultFilter is not null &&
            !resultFilter.Equals("failed", StringComparison.OrdinalIgnoreCase) &&
            !resultFilter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid resultFilter '{resultFilter}'. Must be 'failed' or 'all'.", nameof(resultFilter));
        }

        var timeline = await GetTimelineAsync(buildIdOrUrl, ct);
        if (timeline is null)
            throw new InvalidOperationException($"No timeline available for build '{buildIdOrUrl}'.");

        var records = timeline.Records;
        var recordById = records
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

        // Default resultFilter to "failed": include non-succeeded records and succeeded records with issues
        resultFilter ??= "failed";

        var matches = new List<TimelineSearchMatch>();

        foreach (var r in records)
        {
            // Apply recordType filter
            if (recordType is not null && !string.Equals(r.Type, recordType, StringComparison.OrdinalIgnoreCase))
                continue;

            // Apply resultFilter
            if (resultFilter.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                var isFailed = r.Result is not null &&
                    !r.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
                var hasIssues = r.Issues is { Count: > 0 };
                if (!isFailed && !hasIssues)
                    continue;
            }
            // "all" = no filtering

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
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentOutOfRangeException.ThrowIfNegative(contextLines);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMatches, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxLogsToSearch, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(minLogLines);

        if (HelixService.IsFileSearchDisabled)
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

        // Remove trailing empty element if content ends with a newline
        // (matches original behavior)
        if (lines.Count > 0 && content.Length > 0 &&
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
}
