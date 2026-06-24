using System.Collections;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using HelixTool.Core;
using HelixTool.Core.AzDO;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class AzdoMcpTools
{
    private readonly AzdoService _svc;
    private readonly IAzdoTokenAccessor _tokenAccessor;

    public AzdoMcpTools(AzdoService svc, IAzdoTokenAccessor tokenAccessor)
    {
        _svc = svc;
        _tokenAccessor = tokenAccessor;
    }

    [McpServerTool(Name = "azdo_build", Title = "AzDO Build Details", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get details of an Azure DevOps (AzDO) build: status, result, definition, source branch, timing, and web URL. Use AzDO build IDs/URLs, not Helix job IDs.")]
    public async Task<AzdoBuildSummary> Build(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetBuildSummaryAsync(buildIdOrUrl),
            "get build details",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_builds", Title = "AzDO Build List", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List recent Azure DevOps (AzDO) builds for a project. Filter by PR, branch, definition, or status.")]
    public async Task<LimitedResults<AzdoBuild>> Builds(
        [Description("Azure DevOps organization. Default: dnceng-public"), AllowedValues("dnceng-public", "dnceng", "devdiv")] string org = "dnceng-public",
        [Description("Azure DevOps project. Default: public"), AllowedValues("public", "internal")] string project = "public",
        [Description("Maximum results to return. Default: 20")] int top = 20,
        [Description("Filter by branch name (e.g., 'refs/heads/main')")] string? branch = null,
        [Description("Filter by pull request number")] string? prNumber = null,
        [Description("Filter by pipeline definition ID")] int? definitionId = null,
        [Description("Filter by build status"), AllowedValues("all", "cancelling", "completed", "inProgress", "none", "notStarted", "postponed")] string? status = null,
        [Description("Lower bound on queue/start/finish time (ISO 8601). Pair with queryOrder to choose which time field is filtered.")] DateTimeOffset? minTime = null,
        [Description("Upper bound on queue/start/finish time (ISO 8601). Pair with queryOrder to choose which time field is filtered.")] DateTimeOffset? maxTime = null,
        [Description("Order results by time field. AzDO interprets minTime/maxTime against the field matching this order (e.g. finishTimeDescending → filter by finishTime). Default: queueTimeDescending"),
         AllowedValues("queueTimeAscending", "queueTimeDescending", "startTimeAscending", "startTimeDescending", "finishTimeAscending", "finishTimeDescending")] string? queryOrder = null)
    {
        // If org looks like a URL, extract org/project from it
        (org, project) = TryExtractOrgProjectFromUrl(org, project);

        queryOrder = AzdoService.NormalizeQueryOrder(queryOrder);
        if (!AzdoService.IsValidQueryOrder(queryOrder))
            throw new McpException(AzdoService.GetInvalidQueryOrderMessage(queryOrder!));

        var filter = new AzdoBuildFilter
        {
            PrNumber = prNumber,
            Branch = branch,
            DefinitionId = definitionId,
            Top = top,
            StatusFilter = status,
            MinTime = minTime,
            MaxTime = maxTime,
            QueryOrder = queryOrder
        };

        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.ListBuildsAsync(org, project, filter), top),
            "list builds");
    }

    private const int MaxTimelineRecords = 200;
    private const int TruncatedTimelineBudget = 100;

    [McpServerTool(Name = "azdo_timeline", Title = "AzDO Build Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get the Azure DevOps (AzDO) build timeline with stages, jobs, and tasks. Find failed steps and AzDO log IDs for azdo_log. Consider azdo_search_timeline for large builds.")]
    public async Task<TimelineResponse?> Timeline(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Filter: 'failed' (default), 'all', 'running' (in-progress tasks), 'pending' (not started), 'incomplete' (running+pending), or 'issues' (errors/warnings only)."), AllowedValues("failed", "all", "running", "pending", "incomplete", "issues")] string filter = "failed")
    {
        filter = AzdoService.NormalizeFilter(filter);
        if (!AzdoService.IsValidFilter(filter))
            throw new McpException(AzdoService.GetInvalidFilterMessage(filter));

        AzdoTimeline? timeline;
        timeline = await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetTimelineAsync(buildIdOrUrl),
            "get build timeline",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));

        if (timeline is null)
            return null;

        List<AzdoTimelineRecord> records;

        if (filter.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            records = [.. timeline.Records];
        }
        else
        {
            var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in timeline.Records)
            {
                if (r.Id is not null && AzdoService.MatchesFilter(r, filter))
                    matchedIds.Add(r.Id);
            }

            // Include parent records for context (walk up parentId chain)
            var allIds = new HashSet<string>(matchedIds, StringComparer.OrdinalIgnoreCase);
            var recordById = timeline.Records
                .Where(r => r.Id is not null)
                .ToDictionary(r => r.Id!, StringComparer.OrdinalIgnoreCase);

            foreach (var id in matchedIds)
            {
                var current = recordById.GetValueOrDefault(id);
                while (current?.ParentId is not null && allIds.Add(current.ParentId))
                {
                    current = recordById.GetValueOrDefault(current.ParentId);
                }
            }

            records = timeline.Records.Where(r => r.Id is not null && allIds.Contains(r.Id)).ToList();
        }

        // Partial response pattern: truncate if too many records
        var totalRecords = records.Count;
        if (totalRecords > MaxTimelineRecords)
        {
            records = records.Take(TruncatedTimelineBudget).ToList();
            return new TimelineResponse
            {
                Id = timeline.Id,
                Records = records,
                Truncated = true,
                TotalRecords = totalRecords,
                Note = $"⚠️ Timeline truncated: showing {TruncatedTimelineBudget} of {totalRecords} records. " +
                       (filter.Equals("all", StringComparison.OrdinalIgnoreCase)
                           ? $"Use azdo_search_timeline(buildIdOrUrl, 'pattern') for targeted search, or azdo_timeline with filter='failed' to reduce results."
                           : $"Use azdo_search_timeline(buildIdOrUrl, 'pattern') for targeted search.")
            };
        }

        return new TimelineResponse
        {
            Id = timeline.Id,
            Records = records
        };
    }

    [McpServerTool(Name = "azdo_log", Title = "AzDO Build Log", ReadOnly = true, Idempotent = true),
     Description("Get Azure DevOps (AzDO) log content for a build step. Use the AzDO log ID from azdo_timeline. Returns last N lines by default.")]
    public async Task<string> Log(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("AzDO log ID from azdo_timeline")] int logId,
        [Description("Lines from end to return")] int? tailLines = 500)
    {
        string? content;
        content = await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetBuildLogAsync(buildIdOrUrl, logId, tailLines),
            "get build log",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
        return content ?? string.Empty;
    }

    [McpServerTool(Name = "azdo_changes", Title = "AzDO Build Changes", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get commits/changes for an Azure DevOps (AzDO) build. Returns commit IDs, messages, authors, and timestamps.")]
    public async Task<LimitedResults<AzdoBuildChange>> Changes(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Maximum results to return")] int top = 20)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetBuildChangesAsync(buildIdOrUrl, top), top),
            "get build changes",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_test_runs", Title = "AzDO Test Runs", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get Azure DevOps (AzDO) test run summaries for a build with total/passed/failed counts. ⚠️ Run-level failedTests can be inaccurate — always drill into azdo_test_results to verify.")]
    public async Task<LimitedResults<AzdoTestRun>> TestRuns(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Maximum results to return")] int top = 50)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetTestRunsAsync(buildIdOrUrl, top), top),
            "get test runs",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_test_results", Title = "AzDO Test Results", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Get Azure DevOps (AzDO) test results for a specific test run. Defaults to failed tests only.")]
    public async Task<LimitedResults<AzdoTestResult>> TestResults(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Azure DevOps test run ID from azdo_test_runs")] int runId,
        [Description("Maximum results to return. Default: 200")] int top = 200)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetTestResultsAsync(buildIdOrUrl, runId, top), top),
            "get test results",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_artifacts", Title = "AzDO Build Artifacts", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List Azure DevOps (AzDO) build artifacts such as logs, test results, and binlogs. Supports glob-style artifact-name filtering.")]
    public async Task<LimitedResults<AzdoBuildArtifact>> Artifacts(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Artifact name glob. Default: all")] string pattern = "*",
        [Description("Maximum results to return. Default: 100")] int top = 100)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetBuildArtifactsAsync(buildIdOrUrl, pattern, top), top),
            "get build artifacts",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_search_log", Title = "Search AzDO Build Logs", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search Azure DevOps (AzDO) build step logs for a case-insensitive text pattern. Provide logId for one step, or omit it to search all ranked steps.")]
    public async Task<CrossStepSearchResult> SearchLog(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("AzDO log ID from azdo_timeline")] int? logId = null,
        [Description("Case-insensitive text substring to search for; not a regex")] string pattern = "error",
        [Description("Lines of context around each match")] int contextLines = 2,
        [Description("Maximum matches to return. Default: 100")] int maxMatches = 100,
        [Description("Maximum log steps to search. Default: 50")] int maxLogsToSearch = 50,
        [Description("Minimum lines per log to search")] int minLogLines = 5,
        IProgress<ProgressNotificationValue>? progress = null)
    {
        if (StringHelpers.IsFileSearchDisabled)
            throw new McpException("File content search is disabled by configuration.");

        return await McpExceptionHandler.RunServiceCallAsync(async () =>
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
                    StoppedEarly = result.Truncated,
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
                buildIdOrUrl, pattern, contextLines, maxMatches, maxLogsToSearch, minLogLines,
                McpProgressAdapter.Wrap(progress));
        }, "search build logs", ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_search_timeline", Title = "AzDO Search Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Search Azure DevOps (AzDO) build timeline record names and issue messages for a case-insensitive substring. Returns matching records with AzDO log IDs for azdo_log or azdo_search_log.")]
    public async Task<TimelineSearchResult> SearchTimeline(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Case-insensitive text substring to search for; not a regex")] string pattern,
        [Description("Optional Azure DevOps timeline record type filter: 'Stage', 'Job', or 'Task'"), AllowedValues("Stage", "Job", "Task")] string? recordType = null,
        [Description("Filter: 'failed' (default), 'all', 'running' (in-progress records), 'pending' (not started), 'incomplete' (not completed), or 'issues' (errors/warnings only)."), AllowedValues("failed", "all", "running", "pending", "incomplete", "issues")] string resultFilter = "failed")
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.SearchTimelineAsync(buildIdOrUrl, pattern, recordType, resultFilter),
            "search timeline",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_test_attachments", Title = "AzDO Test Attachments", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("List Azure DevOps (AzDO) attachments for a test result, such as screenshots, logs, and dumps. Requires run ID and result ID from azdo_test_results.")]
    public async Task<LimitedResults<AzdoTestAttachment>> TestAttachments(
        [Description("Azure DevOps test run ID from azdo_test_runs")] int runId,
        [Description("Azure DevOps test result ID from azdo_test_results")] int resultId,
        [Description("Azure DevOps project"), AllowedValues("public", "internal")] string project = "public",
        [Description("Azure DevOps organization"), AllowedValues("dnceng-public", "dnceng", "devdiv")] string org = "dnceng-public",
        [Description("Maximum results to return. Default: 100")] int top = 100)
    {
        // If org looks like a URL, extract org/project from it
        (org, project) = TryExtractOrgProjectFromUrl(org, project);

        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetTestAttachmentsAsync(org, project, runId, resultId, top), top),
            "get test attachments");
    }

    [McpServerTool(Name = "azdo_helix_jobs", Title = "Helix Jobs from Build", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Extract Helix job IDs from an Azure DevOps (AzDO) build. Start with an AzDO build ID/URL, then pass returned Helix job GUIDs to helix_* tools.")]
    public async Task<HelixJobsFromBuildResult> HelixJobs(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Filter: 'failed' (default), 'all', 'running', 'pending', 'incomplete', or 'issues'."), AllowedValues("failed", "all", "running", "pending", "incomplete", "issues")] string filter = "failed")
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetHelixJobsAsync(buildIdOrUrl, filter),
            "extract Helix jobs",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_build_analysis", Title = "Build Analysis Known Issues", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Extract Build Analysis known issue matches from an Azure DevOps (AzDO) build ID or URL.")]
    public async Task<BuildAnalysisResult> BuildAnalysis(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetBuildAnalysisAsync(buildIdOrUrl),
            "get build analysis",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_auth_status", Title = "AzDO Auth Status", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Current AzDO auth method (anonymous, PAT, Entra, az CLI), expiry, and warnings. No API call made.")]
    public async Task<CallToolResult> AuthStatus()
    {
        return McpToolResultFactory.CreateStructuredJson(await _tokenAccessor.AuthStatusAsync());
    }

    private static LimitedResults<T> CreateLimitedResults<T>(IReadOnlyList<T> results, int top)
    {
        var truncated = top > 0 && results.Count >= top;
        return new LimitedResults<T>(
            results,
            truncated,
            note: truncated ? $"Results may have been limited to {top}. Use a higher 'top' value if you need more." : null);
    }

    /// <summary>
    /// If <paramref name="org"/> looks like an AzDO URL, extract org and project from it.
    /// This handles agents that pass full URLs into the org parameter instead of just the org name.
    /// </summary>
    private static (string org, string project) TryExtractOrgProjectFromUrl(string org, string project)
    {
        if (org.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            org.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase) ||
            org.Contains("://", StringComparison.Ordinal))
        {
            if (AzdoIdResolver.TryResolve(org, out var resolvedOrg, out var resolvedProject, out _))
            {
                return (resolvedOrg, resolvedProject);
            }
        }
        return (org, project);
    }

    private static string? GetAzdoNotFoundMessage(Exception ex, string buildIdOrUrl)
    {
        if (ex is InvalidOperationException invalidOperation &&
            invalidOperation.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return AppendNotFoundHint(invalidOperation.Message, buildIdOrUrl);
        }

        return null;
    }

    /// <summary>
    /// When a build is not found and the query used default org/project, append an auth hint.
    /// Internal AzDO projects return 404 (not 401) to unauthenticated callers.
    /// </summary>
    private static string AppendNotFoundHint(string message, string buildIdOrUrl)
    {
        // If the input is a URL, try to extract the org/project the agent intended.
        if (AzdoIdResolver.TryResolve(buildIdOrUrl, out var resolvedOrg, out var resolvedProject, out _) &&
            !resolvedOrg.Equals(AzdoIdResolver.DefaultOrg, StringComparison.OrdinalIgnoreCase))
        {
            // The URL pointed to a non-default org — 404 likely means auth is needed.
            return message +
                $" Build not found in {resolvedOrg}/{resolvedProject} — this org may require authentication. " +
                "Run 'az login' or set AZDO_TOKEN to a PAT with Build(read) scope.";
        }

        // Bare integer resolved to default org — suggest the agent may have the wrong org.
        if (message.Contains(AzdoIdResolver.DefaultOrg, StringComparison.OrdinalIgnoreCase) &&
            message.Contains(AzdoIdResolver.DefaultProject, StringComparison.OrdinalIgnoreCase))
        {
            return message +
                " If this is an internal build, pass the full AzDO URL so org/project can be extracted, " +
                "or use azdo_builds with org='dnceng' and project='internal' (requires auth via 'az login' or AZDO_TOKEN).";
        }
        return message;
    }
}

/// <summary>Timeline response with optional truncation metadata for the partial response pattern.</summary>
public sealed record TimelineResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<AzdoTimelineRecord> Records { get; init; } = [];

    [JsonPropertyName("truncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Truncated { get; init; }

    [JsonPropertyName("totalRecords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalRecords { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}

[JsonConverter(typeof(LimitedResultsJsonConverterFactory))]
public sealed class LimitedResults<T> : IReadOnlyList<T>
{
    public LimitedResults(IReadOnlyList<T> results, bool truncated, int? total = null, string? note = null)
    {
        Results = results ?? [];
        Truncated = truncated;
        Total = total;
        Note = note;
    }

    public IReadOnlyList<T> Results { get; }
    public bool Truncated { get; }
    public int? Total { get; }
    public string? Note { get; }

    [JsonIgnore]
    public int Count => Results.Count;

    [JsonIgnore]
    public T this[int index] => Results[index];

    public IEnumerator<T> GetEnumerator() => Results.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class LimitedResultsJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(LimitedResults<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(LimitedResultsJsonConverter<>).MakeGenericType(itemType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

public sealed class LimitedResultsJsonConverter<T> : JsonConverter<LimitedResults<T>>
{
    public override LimitedResults<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var results = root.TryGetProperty("results", out var resultsElement)
            ? JsonSerializer.Deserialize<List<T>>(resultsElement.GetRawText(), options) ?? []
            : [];
        var truncated = root.TryGetProperty("truncated", out var truncatedElement) && truncatedElement.GetBoolean();
        int? total = root.TryGetProperty("total", out var totalElement) && totalElement.ValueKind is JsonValueKind.Number
            ? totalElement.GetInt32()
            : null;
        var note = root.TryGetProperty("note", out var noteElement) ? noteElement.GetString() : null;

        return new LimitedResults<T>(results, truncated, total, note);
    }

    public override void Write(Utf8JsonWriter writer, LimitedResults<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        JsonSerializer.Serialize(writer, value.Results, options);
        writer.WriteBoolean("truncated", value.Truncated);
        if (value.Total.HasValue)
            writer.WriteNumber("total", value.Total.Value);
        if (!string.IsNullOrWhiteSpace(value.Note))
            writer.WriteString("note", value.Note);
        writer.WriteEndObject();
    }
}
