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
using HelixTool.Core.Delivery;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class AzdoMcpTools
{
    private readonly AzdoService _svc;
    private readonly IAzdoTokenAccessor _tokenAccessor;
    private readonly IEvidenceDeliveryStore _evidenceStore;

    /// <summary>
    /// Back-compat convenience constructor for existing test call sites that predate the shared
    /// evidence delivery store. Production DI should use the three-argument constructor with an
    /// explicit <see cref="IEvidenceDeliveryStore"/>.
    /// </summary>
    public AzdoMcpTools(AzdoService svc, IAzdoTokenAccessor tokenAccessor)
        : this(svc, tokenAccessor, new HelixTool.Core.Delivery.FileEvidenceDeliveryStore(new HelixTool.Core.Cache.CacheOptions()))
    {
    }

    public AzdoMcpTools(AzdoService svc, IAzdoTokenAccessor tokenAccessor, IEvidenceDeliveryStore evidenceStore)
    {
        _svc = svc;
        _tokenAccessor = tokenAccessor;
        _evidenceStore = evidenceStore;
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

    [McpServerTool(Name = "azdo_builds", Title = "AzDO Build List", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
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

    [McpServerTool(Name = "azdo_timeline", Title = "AzDO Build Timeline", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Triage an AzDO build with error/Helix-first Jobs/Tasks, short deduplicated issue text and log IDs. Full records/issues remain accessible: continue with next or read the returned evidence reference/file. Defaults omit duplicate Phase rows; use compact for count rows and summary for aggregates.")]
    public async Task<TimelineProjectionResult> Timeline(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Effective 'failed' for ordinary triage; 'all' when an explicit record/parent/type/result/state/name selector is supplied and no preset is given. An explicit preset still intersects selectors."),
         AllowedValues("failed", "all", "running", "pending", "incomplete", "issues")] string? filter = null,
        [Description("Exact timeline record GUID. Bypasses only an omitted/implicit failure preset, not a deliberate caller filter.")] string? recordId = null,
        [Description("Select records whose direct parent is this GUID; expansion can add descendants/context.")] string? parentId = null,
        [Description("Exact record type: Stage, Phase, Job, Task, Checkpoint (ordinal-ignore-case)."), AllowedValues("Stage", "Phase", "Job", "Task", "Checkpoint")] string? type = null,
        [Description("Exact result: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none."),
         AllowedValues("failed", "canceled", "abandoned", "skipped", "succeededWithIssues", "succeeded", "none")] string? result = null,
        [Description("Exact provider state: pending, inProgress, completed (running/active/not-started normalize to these)."),
         AllowedValues("pending", "inProgress", "completed")] string? state = null,
        [Description("Case-insensitive name glob using * and ?; a pattern without wildcards is a legacy substring match.")] string? name = null,
        [Description("none, ancestors (default), children, descendants, ancestorsAndChildren, or ancestorsAndDescendants."),
         AllowedValues("none", "ancestors", "children", "descendants", "ancestorsAndChildren", "ancestorsAndDescendants")] string expand = "ancestors",
        [Description("triage (default, diagnostic previews), compact (identity/count rows), full (original record detail), or summary (aggregate counts only)."),
         AllowedValues("triage", "compact", "full", "summary")] string projection = "triage",
        [Description("Effective default false for triage/summary, true for full/compact. An exact Phase target remains retrievable regardless.")] bool? includePhase = null,
        [Description("Efficiency default per row; larger explicit values remain accessible through paging/file delivery.")] int previewIssueLimit = 5,
        [Description("Efficiency default Unicode-scalar preview length per issue; larger requested text remains accessible through full/file delivery.")] int previewChars = 200,
        [Description("Offset into the deduplicated expanded record set.")] int offset = 0,
        [Description("Requested row window. Larger values remain valid; delivery shapes the response, never refuses it.")] int limit = 10,
        [Description("Source/view fingerprint from a prior response; a stale/changed value is rejected so paging never silently shifts.")] string? viewId = null,
        [Description("Issue offset for one recordId with projection='full'; nonzero is invalid otherwise.")] int issueOffset = 0,
        [Description("Explicit issue window for one full record; omitted means all issues (chunk/file accessible regardless of inline target).")] int? issueLimit = null,
        [Description("Inline shaping target, not a data-access ceiling. Default: 12288.")] long maxResponseBytes = 12_288,
        [Description("auto (default) keeps a useful inline page; file/all stream the complete requested selection via hlx_read_evidence.")]
        [AllowedValues("auto", "inline", "file", "chunked")] string delivery = "auto",
        [Description("Retrieve the complete selected scope (ignores limit/maxResponseBytes shaping) without changing filters.")] bool all = false)
    {
        if (filter is not null)
        {
            filter = AzdoService.NormalizeFilter(filter);
            if (!AzdoService.IsValidFilter(filter))
                throw new McpException(AzdoService.GetInvalidFilterMessage(filter));
        }
        if (result is not null && !AzdoTimelineProjector.IsValidResult(result))
            throw new McpException($"Invalid result '{result}'. Must be one of: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none.");
        if (state is not null && !AzdoTimelineProjector.IsValidState(state))
            throw new McpException($"Invalid state '{state}'. Must be one of: pending, inProgress, completed (running/active/not-started are accepted aliases).");
        if (!s_validProjections.Contains(projection))
            throw new McpException($"Invalid projection '{projection}'. Must be one of: triage, compact, full, summary.");
        if (!s_validExpand.Contains(expand))
            throw new McpException($"Invalid expand '{expand}'. Must be one of: none, ancestors, children, descendants, ancestorsAndChildren, ancestorsAndDescendants.");
        if (type is not null && !AzdoTimelineProjector.IsValidType(type))
            throw new McpException($"Invalid type '{type}'. Must be one of: Stage, Phase, Job, Task, Checkpoint.");

        // Ordinarily shape the requested target into 2,048..16,384; explicit all/file delivery
        // below still serves the complete payload regardless of this inline shaping target.
        var effectiveMaxResponseBytes = Math.Clamp(maxResponseBytes, 2_048, 16_384);

        var timeline = await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetTimelineAsync(buildIdOrUrl),
            "get build timeline",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));

        if (timeline is null || timeline.Records.Count == 0)
        {
            return new TimelineProjectionResult
            {
                Id = timeline?.Id,
                Projection = projection.ToLowerInvariant(),
                EffectiveFilter = filter ?? "failed",
                ViewId = "",
                Matched = 0,
                SelectedTotal = 0,
                TimelineRecords = 0,
                SelectionScope = "preset",
                IncludePhase = includePhase ?? false,
                PhaseSuppressed = false,
                PreviewIssueLimit = previewIssueLimit,
                PreviewChars = previewChars,
                MaxResponseBytes = effectiveMaxResponseBytes,
                Counts = new TimelineCounts { Scope = "matched", Complete = true, ByTypeResult = [] },
                Offset = offset,
                Limit = limit,
                Returned = 0,
                Total = 0,
                Complete = true,
                Truncated = false,
                Note = $"No timeline available for build {buildIdOrUrl}. The build may still be initializing, was canceled before any leg reported, or has no timeline data."
            };
        }

        var request = new TimelineProjectionRequest
        {
            Filter = filter,
            RecordId = recordId,
            ParentId = parentId,
            Type = type,
            Result = result,
            State = state,
            Name = name,
            Expand = expand,
            Projection = projection,
            IncludePhase = includePhase,
            PreviewIssueLimit = previewIssueLimit,
            PreviewChars = previewChars,
            Offset = offset,
            Limit = limit,
            ViewId = viewId,
            IssueOffset = issueOffset,
            IssueLimit = issueLimit,
            MaxResponseBytes = effectiveMaxResponseBytes,
            All = all || delivery.Equals("file", StringComparison.OrdinalIgnoreCase) || delivery.Equals("all", StringComparison.OrdinalIgnoreCase),
            BuildIdOrUrl = buildIdOrUrl
        };

        TimelineProjectionResult Project(TimelineProjectionRequest r)
        {
            try
            {
                return AzdoTimelineProjector.Project(timeline, r);
            }
            catch (ArgumentException ex)
            {
                throw new McpException(ex.Message);
            }
        }

        var (cacheKey, cachePartition) = _svc.ResolveTimelineCacheIdentity(buildIdOrUrl);
        var (shaped, deliveryDescriptor) = McpPresentationBudget.ShapeTimeline(request, Project, _evidenceStore, cachePartition, cacheKey);

        if (deliveryDescriptor is not null)
        {
            shaped = shaped with
            {
                Complete = false,
                Truncated = true,
                Delivery = deliveryDescriptor,
                Continuation = new TimelineAction
                {
                    Tool = "hlx_read_evidence",
                    Arguments = new Dictionary<string, object?>
                    {
                        ["evidenceId"] = deliveryDescriptor.EvidenceId,
                        ["offsetBytes"] = 0,
                        ["lengthBytes"] = 65536,
                        ["encoding"] = "auto"
                    }
                },
                Note = $"Requested payload exceeded the {maxResponseBytes}-byte inline shaping target; the complete selection was written to a verified evidence file. " +
                       $"Read it with hlx_read_evidence(evidenceId='{deliveryDescriptor.EvidenceId}', offsetBytes=0, lengthBytes=65536)."
            };
        }

        return shaped;
    }

    private static readonly HashSet<string> s_validProjections = new(StringComparer.OrdinalIgnoreCase) { "triage", "compact", "full", "summary" };
    private static readonly HashSet<string> s_validExpand = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "ancestors", "children", "descendants", "ancestorsandchildren", "ancestorsanddescendants"
    };

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

    [McpServerTool(Name = "azdo_changes", Title = "AzDO Build Changes", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
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

    [McpServerTool(Name = "azdo_test_runs", Title = "AzDO Test Runs", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Get Azure DevOps (AzDO) test run summaries for a build with total/passed/failed counts. failedTests is derived from AzDO unanalyzedTests (failed results not yet analyzed/triaged); analyzed failures may not be counted, so drill into azdo_test_results when exact failures matter.")]
    public async Task<LimitedResults<AzdoTestRun>> TestRuns(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Maximum results to return")] int top = 50)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetTestRunsAsync(buildIdOrUrl, top), top),
            "get test runs",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_test_results", Title = "AzDO Test Results", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
     Description("Get Azure DevOps (AzDO) test results for a specific test run. Defaults to failed tests only.")]
    public async Task<LimitedResults<AzdoTestResult>> TestResults(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Azure DevOps test run ID from azdo_test_runs")] int runId,
        [Description("Maximum results to return. Default: 200")] int top = 200,
        [Description("Comma-separated AzDO test outcomes to include (e.g. 'Failed', 'Passed,Failed', 'NotExecuted'). Default: Failed (matches current behavior).")] string? outcomes = null)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            async () => CreateLimitedResults(await _svc.GetTestResultsAsync(buildIdOrUrl, runId, top, outcomes), top),
            "get test results",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_artifacts", Title = "AzDO Build Artifacts", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
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

    [McpServerTool(Name = "azdo_test_attachments", Title = "AzDO Test Attachments", ReadOnly = true, Idempotent = true, UseStructuredContent = true,
                   OutputSchemaType = typeof(MinimalObjectSchema)),
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
        [Description("Primary Helix filter: 'all' returns every discovered job; 'running' and 'incomplete' return jobs without Finished; 'pending' returns none; 'failed' (default) and 'issues' return jobs with parsed AzDO monitor failure evidence. Timeline fallback retains its task-based filtering."), AllowedValues("failed", "all", "running", "pending", "incomplete", "issues")] string filter = "failed")
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetHelixJobsAsync(buildIdOrUrl, filter),
            "extract Helix jobs",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_build_analysis", Title = "AzDO Build Issue URL Evidence", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Extract GitHub issue URLs from AzDO build tags/timeline, not Build Analysis KBE matches. knownIssues=[] and unmatchedFailures are not BA conclusions; use helix_ci_guide for authoritative check guidance.")]
    public async Task<BuildAnalysisResult> BuildAnalysis(
        [Description("AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl)
    {
        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetBuildAnalysisAsync(buildIdOrUrl),
            "get build analysis",
            ex => GetAzdoNotFoundMessage(ex, buildIdOrUrl));
    }

    [McpServerTool(Name = "azdo_evidence_plan", Title = "AzDO Evidence Plan", ReadOnly = true, Idempotent = true, UseStructuredContent = true),
     Description("Plan CI evidence for an Azure DevOps build: maps failed/canceled jobs to artifact candidates and surfaces Helix monitor work-item failures from timeline issues. No downloads or Helix API calls. Use Helix IDs from helixFailures with azdo_helix_jobs/helix_* for drilldown.")]
    public async Task<AzdoEvidencePlan> EvidencePlan(
        [Description("AzDO build ID as a JSON string (for example, '1570501') or full Azure DevOps build URL; not a Helix job ID")] string buildIdOrUrl,
        [Description("Glob pattern for artifact names to include. Supports '*' (all), '*.ext' (suffix), 'Prefix*' (prefix), or substring. Default: '*'")] string artifactPattern = "*",
        [Description("Prefix stripped from artifact names before matching (e.g. 'Logs_Build_'). Ordinal StartsWith; no regex.")] string? artifactJobPrefix = null,
        [Description("Strip 'AttemptN_' from artifact names after the job prefix, recording the attempt number. Default: true")] bool stripAttemptPrefix = true,
        [Description("Matching strategy: 'auto' (default) = source-id join then normalized-name fallback; 'source-id' = GUID join only; 'normalized-exact' = PR #132609 name parity; 'exact' = ordinal-ignore-case equality after prefix strip, no normalization."),
         AllowedValues("auto", "source-id", "normalized-exact", "exact")] string match = "auto",
        // No [AllowedValues]: this is a comma-separated multi-value string, so a JSON-schema enum
        // would reject every valid combination (including the 'failed,canceled' default). Matches
        // the sibling 'outcomes' parameter on azdo_test_results; per-token validation is below.
        [Description("Comma-separated job results to include (e.g. 'failed', 'failed,canceled', 'succeeded,succeededWithIssues'). Any combination of: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none. Unknown values are rejected. Default: 'failed,canceled'.")] string jobResults = "failed,canceled",
        [Description("Offset into parsed Helix monitor failures for deterministic collectors. Default: 0.")] int helixFailureOffset = 0,
        [Description("Maximum Helix monitor failures to return. Default: 200, max: 1000. Any partial page returns complete=false with helixFailuresTruncated=true; request the full set when a complete plan is required.")] int helixFailureLimit = AzdoEvidencePlan.DefaultHelixFailureLimit)
    {
        // Parse and validate jobResults
        var resultList = jobResults
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .ToList();
        if (resultList.Count == 0)
            resultList = ["failed", "canceled"];

        var validResults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "failed", "canceled", "abandoned", "skipped", "succeededWithIssues", "succeeded", "none" };
        var bad = resultList.FirstOrDefault(r => !validResults.Contains(r));
        if (bad is not null)
            throw new McpException($"Invalid jobResults value '{bad}'. Must be one of: {string.Join(", ", validResults.OrderBy(v => v))}.");

        if (!AzdoEvidenceMatchStrategy.AllValues.Contains(match, StringComparer.OrdinalIgnoreCase))
            throw new McpException($"Invalid match '{match}'. Must be one of: {string.Join(", ", AzdoEvidenceMatchStrategy.AllValues)}.");

        var options = new AzdoEvidencePlanOptions
        {
            ArtifactPattern = artifactPattern,
            ArtifactJobPrefix = artifactJobPrefix,
            StripAttemptPrefix = stripAttemptPrefix,
            Match = match,
            JobResults = resultList,
            HelixFailureOffset = helixFailureOffset,
            HelixFailureLimit = helixFailureLimit
        };

        return await McpExceptionHandler.RunServiceCallAsync(
            () => _svc.GetEvidencePlanAsync(buildIdOrUrl, options),
            "build evidence plan",
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
