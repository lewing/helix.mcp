using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelixTool.Core.Text;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Pure Core projector shared by CLI and MCP: turns a raw <see cref="AzdoTimeline"/> plus a
/// <see cref="TimelineProjectionRequest"/> into a bounded, ranked <see cref="TimelineProjectionResult"/>.
/// No I/O, no MCP SDK dependency — delivery/budget shaping on top of this output is the caller's job.
/// </summary>
public static class AzdoTimelineProjector
{
    private static readonly HashSet<string> s_resultValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "failed", "canceled", "abandoned", "skipped", "succeededWithIssues", "succeeded", "none"
    };

    private static readonly HashSet<string> s_stateValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "pending", "inProgress", "completed"
    };

    private static readonly HashSet<string> s_typeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stage", "Phase", "Job", "Task", "Checkpoint"
    };

    private static readonly Dictionary<string, string> s_stateAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["running"] = "inProgress",
        ["active"] = "inProgress",
        ["notStarted"] = "pending",
        ["not-started"] = "pending"
    };

    public static bool IsValidResult(string result) => s_resultValues.Contains(result);

    public static string NormalizeState(string state) =>
        s_stateAliases.TryGetValue(state, out var canonical) ? canonical : state;

    public static bool IsValidState(string state) => s_stateValues.Contains(NormalizeState(state));

    public static bool IsValidType(string type) => s_typeValues.Contains(type);

    /// <summary>
    /// Validates a request's own parameter shape (offsets/limits/enum values/issue-window rules)
    /// independent of any timeline content. Callers that short-circuit before loading/using a
    /// timeline (e.g. an empty-records fast path) must still invoke this, so a malformed request
    /// (zero issueLimit, negative issueOffset, invalid enum value, etc.) is a real error rather than
    /// silently succeeding just because there happened to be nothing to select from.
    /// </summary>
    public static void Validate(TimelineProjectionRequest request) => ValidateRequest(request);


    public static TimelineProjectionResult Project(AzdoTimeline timeline, TimelineProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequest(request);

        var records = timeline.Records;

        // Build the id index without throwing on duplicate GUIDs — a malformed graph fails closed
        // (incomplete, with an explicit code) rather than crashing the whole acquisition.
        var byId = new Dictionary<string, AzdoTimelineRecord>(StringComparer.OrdinalIgnoreCase);
        var sourceIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicateDetected = false;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (r.Id is null) continue;
            if (!byId.TryAdd(r.Id, r))
                duplicateDetected = true;
            sourceIndexById.TryAdd(r.Id, i);
        }

        var hasSelector = request.HasExplicitSelector;
        var effectiveFilter = request.Filter is not null
            ? AzdoService.NormalizeFilter(request.Filter)
            : (hasSelector ? "all" : "failed");

        if (request.Filter is not null && !AzdoService.IsValidFilter(effectiveFilter))
            throw new ArgumentException(AzdoService.GetInvalidFilterMessage(effectiveFilter), nameof(request));

        // Diagnostic-only implicit triage focus: only applies to the unmodified "failed" scope,
        // never when a caller supplied a deliberate selector.
        var diagnosticFocusOnly = request.Projection.Equals("triage", StringComparison.OrdinalIgnoreCase)
            && !hasSelector
            && effectiveFilter.Equals("failed", StringComparison.OrdinalIgnoreCase);

        // includePhase effective resolution — computed before seed matching so suppressed Phase
        // rows never inflate "matched"/counts; expansion can still surface one as context only.
        var defaultIncludePhase = request.Projection.Equals("full", StringComparison.OrdinalIgnoreCase)
            || request.Projection.Equals("compact", StringComparison.OrdinalIgnoreCase);
        var explicitPhaseTarget = (request.Type?.Equals("Phase", StringComparison.OrdinalIgnoreCase) == true)
            || (request.RecordId is not null && byId.TryGetValue(request.RecordId, out var targetRec)
                && targetRec.Type?.Equals("Phase", StringComparison.OrdinalIgnoreCase) == true);

        if (explicitPhaseTarget && request.IncludePhase == false)
            throw new ArgumentException("includePhase=false conflicts with an exact Phase target (type='Phase' or a Phase recordId). Omit includePhase or set it to true.");

        var effectiveIncludePhase = request.IncludePhase ?? (defaultIncludePhase || explicitPhaseTarget);
        var phaseSuppressed = !effectiveIncludePhase;

        bool PassesSelectors(AzdoTimelineRecord r)
        {
            if (!AzdoService.MatchesFilter(r, effectiveFilter))
                return false;
            if (request.RecordId is not null && !string.Equals(r.Id, request.RecordId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (request.ParentId is not null && !string.Equals(r.ParentId, request.ParentId, StringComparison.OrdinalIgnoreCase))
                return false;
            if (request.Type is not null && !string.Equals(r.Type, request.Type, StringComparison.OrdinalIgnoreCase))
                return false;
            if (request.Result is not null)
            {
                var wantsNone = request.Result.Equals("none", StringComparison.OrdinalIgnoreCase);
                var matchesResult = wantsNone
                    ? string.IsNullOrEmpty(r.Result) || r.Result.Equals("none", StringComparison.OrdinalIgnoreCase)
                    : string.Equals(r.Result, request.Result, StringComparison.OrdinalIgnoreCase);
                if (!matchesResult)
                    return false;
            }
            if (request.State is not null)
            {
                var wantState = NormalizeState(request.State);
                if (!string.Equals(NormalizeStateValue(r.State), wantState, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (request.Name is not null && !HlxNameGlob.IsMatch(r.Name ?? "", request.Name))
                return false;
            if (diagnosticFocusOnly && IsBareSkipped(r))
                return false;
            // A suppressed Phase row never counts as a seed/match unless it is itself the explicit
            // target (type='Phase' or an exact Phase recordId); expansion can still surface it as
            // display-only ancestor context, filtered separately at display time.
            if (phaseSuppressed && r.Type?.Equals("Phase", StringComparison.OrdinalIgnoreCase) == true
                && !(request.Type?.Equals("Phase", StringComparison.OrdinalIgnoreCase) == true)
                && !string.Equals(r.Id, request.RecordId, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        var seedIds = new List<string>();
        var seedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records)
        {
            if (r.Id is null) continue;
            if (PassesSelectors(r) && seedSet.Add(r.Id))
                seedIds.Add(r.Id);
        }

        var matched = seedIds.Count;

        // Expand per the full graph, before Phase suppression/paging.
        var matchKindById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in seedIds)
            matchKindById[id] = "match";

        var graphIssues = new GraphIssueTracker { DuplicateDetected = duplicateDetected };
        ExpandSelection(request.Expand, seedIds, byId, matchKindById, graphIssues);

        var expandedIds = matchKindById.Keys.ToList();

        // contextParentId: nearest displayed (non-suppressed) ancestor via full graph.
        string? ContextParentOf(string id)
        {
            var current = byId.GetValueOrDefault(id)?.ParentId;
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (current is not null)
            {
                if (!visited.Add(current))
                {
                    graphIssues.CycleDetected = true;
                    return current;
                }
                if (byId.TryGetValue(current, out var ancestor))
                {
                    var ancestorSuppressed = phaseSuppressed && ancestor.Type?.Equals("Phase", StringComparison.OrdinalIgnoreCase) == true;
                    if (!ancestorSuppressed)
                        return current;
                    current = ancestor.ParentId;
                }
                else
                {
                    graphIssues.MissingParentDetected = true;
                    return current;
                }
            }
            return current;
        }

        // Displayed set: expanded ids minus suppressed Phase rows (unless they are the explicit target).
        bool IsDisplayable(AzdoTimelineRecord r) =>
            !phaseSuppressed
            || !r.Type!.Equals("Phase", StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.Id, request.RecordId, StringComparison.OrdinalIgnoreCase);

        var displayed = expandedIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(r => r is { Id: not null } && (r.Type is null || IsDisplayable(r)))
            .Select(r => r!)
            .ToList();

        var selectedTotal = displayed.Count;

        // Counts describe the *seed* set (matched scope), independent of expansion/paging.
        // Unknown future type/result values are bucketed into "other" so aggregate metadata stays
        // bounded without discarding the records themselves.
        var countsByTypeResult = seedIds
            .Select(id => byId.GetValueOrDefault(id))
            .Where(r => r is not null)
            .GroupBy(r => (BucketType(r!.Type), BucketResult(r!.Result)), (key, group) => new TimelineTypeResultCount(key.Item1, key.Item2, group.Count()))
            .OrderBy(c => c.Type, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Result, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var counts = new TimelineCounts { Scope = "matched", Complete = true, ByTypeResult = countsByTypeResult };

        var incompleteDetails = new List<TimelineIncompleteDetail>();
        if (graphIssues.HasAnyIssue)
        {
            incompleteDetails.Add(new TimelineIncompleteDetail(
                "timeline_graph_invalid",
                "The timeline graph contains a parent-chain cycle, a reference to a missing record, or a duplicate record id. " +
                "The view below is a best-effort reconstruction and is not guaranteed complete."));
        }

        // Ordering: compact/full/summary preserve stored/provider order (original array position);
        // triage uses a deterministic errors-first ranking with the same source order as its final tie-break.
        IReadOnlyList<AzdoTimelineRecord> ordered;
        var projection = request.Projection.ToLowerInvariant();
        if (projection == "triage")
        {
            ordered = RankTriage(displayed, sourceIndexById);
        }
        else
        {
            ordered = [.. displayed.OrderBy(r => sourceIndexById.GetValueOrDefault(r.Id!, int.MaxValue))];
        }

        var viewId = ComputeViewId(timeline, request, effectiveFilter, effectiveIncludePhase);
        if (request.ViewId is not null && !string.Equals(request.ViewId, viewId, StringComparison.Ordinal))
            throw new ArgumentException($"Stale viewId '{request.ViewId}'. The source/view has changed; restart at offset 0 with the new viewId '{viewId}'.");

        var offset = Math.Max(0, request.Offset);
        // All retrieves the complete selection regardless of the caller's (possibly small-default)
        // limit — Project() is the single place that decides row counts, so MCP/CLI only need to set
        // this flag rather than separately recomputing "how many rows is everything."
        var limit = request.All ? Math.Max(selectedTotal, 1) : Math.Max(1, request.Limit);

        var baseResult = new TimelineProjectionResult
        {
            Ok = true,
            Id = timeline.Id,
            Projection = projection,
            EffectiveFilter = effectiveFilter,
            ViewId = viewId,
            Matched = matched,
            SelectedTotal = selectedTotal,
            TimelineRecords = records.Count,
            SelectionScope = hasSelector ? "selector" : "preset",
            IncludePhase = effectiveIncludePhase,
            PhaseSuppressed = phaseSuppressed,
            PreviewIssueLimit = request.PreviewIssueLimit,
            PreviewChars = request.PreviewChars,
            MaxResponseBytes = request.MaxResponseBytes,
            RequestedMaxResponseBytes = request.RequestedMaxResponseBytes ?? request.MaxResponseBytes,
            Counts = counts,
            IncompleteDetails = incompleteDetails,
            Offset = offset,
            Limit = limit,
            Returned = 0,
            Complete = true,
            Truncated = false
        };

        TimelineProjectionResult result = projection switch
        {
            "summary" => baseResult with
            {
                Returned = 0,
                Total = 0,
                TotalRecords = 0,
                Complete = true,
                Truncated = false,
                TriageRows = null,
                CompactRows = null,
                FullRecords = null
            },
            "triage" => BuildTriageProjection(baseResult, ordered, offset, limit, selectedTotal, matchKindById, byId, ContextParentOf, request, viewId),
            "compact" => BuildCompactProjection(baseResult, ordered, offset, limit, selectedTotal, matchKindById, viewId, request),
            _ => BuildFullProjection(baseResult, ordered, offset, limit, selectedTotal, request, viewId),
        };

        if (graphIssues.HasAnyIssue)
        {
            result = result with
            {
                Complete = false,
                Truncated = true,
                Continuation = result.Continuation ?? BuildWholeSelectionContinuation(request, selectedTotal, viewId)
            };
        }

        return result;
    }

    private static readonly HashSet<string> s_projectionValues = new(StringComparer.OrdinalIgnoreCase) { "triage", "compact", "full", "summary" };
    private static readonly HashSet<string> s_expandValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "ancestors", "children", "descendants", "ancestorsAndChildren", "ancestorsAndDescendants"
    };
    private static readonly HashSet<string> s_deliveryValues = new(StringComparer.OrdinalIgnoreCase) { "auto", "inline", "file", "chunked" };

    private static void ValidateRequest(TimelineProjectionRequest request)
    {
        if (request.Offset < 0)
            throw new ArgumentException("offset must be nonnegative.");
        if (request.Limit <= 0)
            throw new ArgumentException("limit must be positive.");
        if (request.PreviewChars <= 0)
            throw new ArgumentException("previewChars must be positive.");
        if (request.PreviewIssueLimit <= 0)
            throw new ArgumentException("previewIssueLimit must be positive.");
        if (request.RecordId is not null && !Guid.TryParse(request.RecordId, out _))
            throw new ArgumentException($"Invalid recordId '{request.RecordId}'; must be a GUID.");
        if (request.ParentId is not null && !Guid.TryParse(request.ParentId, out _))
            throw new ArgumentException($"Invalid parentId '{request.ParentId}'; must be a GUID.");
        if (request.Type is not null && !IsValidType(request.Type))
            throw new ArgumentException($"Invalid type '{request.Type}'. Must be one of: Stage, Phase, Job, Task, Checkpoint.");
        if (request.Result is not null && !IsValidResult(request.Result))
            throw new ArgumentException($"Invalid result '{request.Result}'. Must be one of: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none.");
        if (request.State is not null && !IsValidState(request.State))
            throw new ArgumentException($"Invalid state '{request.State}'. Must be one of: pending, inProgress, completed (running/active/not-started are accepted aliases).");
        if (!s_projectionValues.Contains(request.Projection))
            throw new ArgumentException($"Invalid projection '{request.Projection}'. Must be one of: triage, compact, full, summary.");
        if (!s_expandValues.Contains(request.Expand))
            throw new ArgumentException($"Invalid expand '{request.Expand}'. Must be one of: none, ancestors, children, descendants, ancestorsAndChildren, ancestorsAndDescendants.");
        if (!s_deliveryValues.Contains(request.Delivery))
            throw new ArgumentException($"Invalid delivery '{request.Delivery}'. Must be one of: auto, inline, file, chunked.");
        // A negative issueOffset silently behaving as 0 would hide a caller bug (e.g. a miscomputed
        // continuation) behind an apparently-successful response; reject it explicitly instead.
        if (request.IssueOffset < 0)
            throw new ArgumentException("issueOffset must be nonnegative.");
        // issueLimit=0 would produce a permanently empty, non-advancing issue page (offset + 0 never
        // passes the total), which looks like forward progress to a caller looping on issueNext but
        // never terminates — reject it the same way limit<=0 is rejected for row pagination.
        if (request.IssueLimit is <= 0)
            throw new ArgumentException("issueLimit must be positive when specified.");
        var singleFullRecord = request.RecordId is not null && request.Projection.Equals("full", StringComparison.OrdinalIgnoreCase);
        if (request.IssueOffset != 0 && !singleFullRecord)
            throw new ArgumentException("issueOffset is only valid together with a single exact recordId and projection='full'.");
    }


    private static string BucketType(string? type) =>
        type is not null && s_typeValues.Contains(type) ? type : "other";

    private static string BucketResult(string? result) =>
        result is null ? "none" : s_resultValues.Contains(result) ? result : "other";

    private static string? NormalizeStateValue(string? state) => state is null ? null : NormalizeState(state);

    private static bool IsBareSkipped(AzdoTimelineRecord r) =>
        string.Equals(r.Result, "skipped", StringComparison.OrdinalIgnoreCase) && r.Issues is not { Count: > 0 };

    private sealed class GraphIssueTracker
    {
        public bool DuplicateDetected { get; set; }
        public bool CycleDetected { get; set; }
        public bool MissingParentDetected { get; set; }
        public bool HasAnyIssue => DuplicateDetected || CycleDetected || MissingParentDetected;
    }

    private static void ExpandSelection(
        string expand,
        List<string> seedIds,
        Dictionary<string, AzdoTimelineRecord> byId,
        Dictionary<string, string> matchKindById,
        GraphIssueTracker graphIssues)
    {
        var mode = expand.ToLowerInvariant();
        if (mode == "none")
            return;

        var includeAncestors = mode is "ancestors" or "ancestorsandchildren" or "ancestorsanddescendants";
        var includeChildren = mode is "children" or "ancestorsandchildren";
        var includeDescendants = mode is "descendants" or "ancestorsanddescendants";

        if (includeAncestors)
        {
            foreach (var id in seedIds)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id };
                var current = byId.GetValueOrDefault(id)?.ParentId;
                while (current is not null)
                {
                    if (!visited.Add(current))
                    {
                        graphIssues.CycleDetected = true;
                        break;
                    }
                    if (!matchKindById.ContainsKey(current))
                        matchKindById[current] = "ancestor";
                    if (!byId.TryGetValue(current, out var ancestor))
                    {
                        graphIssues.MissingParentDetected = true;
                        break;
                    }
                    current = ancestor.ParentId;
                }
            }
        }

        if (includeChildren || includeDescendants)
        {
            var childrenByParent = byId.Values
                .Where(r => r.ParentId is not null)
                .GroupBy(r => r.ParentId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            void AddChildren(string parentId, bool recurse, HashSet<string> cycleGuard)
            {
                if (!childrenByParent.TryGetValue(parentId, out var children))
                    return;
                foreach (var child in children)
                {
                    if (child.Id is null || !cycleGuard.Add(child.Id))
                        continue;
                    if (!matchKindById.ContainsKey(child.Id))
                        matchKindById[child.Id] = recurse ? "descendant" : "child";
                    if (recurse)
                        AddChildren(child.Id, recurse: true, cycleGuard);
                }
            }

            foreach (var id in seedIds)
                AddChildren(id, includeDescendants, cycleGuard: [id]);
        }
    }

    private static IReadOnlyList<AzdoTimelineRecord> RankTriage(
        List<AzdoTimelineRecord> displayed,
        Dictionary<string, int> sourceIndexById)
    {
        int Band(AzdoTimelineRecord r)
        {
            var issues = r.Issues ?? [];
            var hasParsedMonitor = issues.Any(i => i.Message is not null && AzdoMonitorFailureParser.ParseMessage(i.Message).Count > 0);
            if (hasParsedMonitor) return 0;

            // "failed-like" per the shared predicate also matches any succeeded-but-warning-bearing
            // record (MatchesFilter's hasIssues branch), so it must never itself gate a ranking band —
            // otherwise a succeeded warning-only Task and a genuinely non-succeeded Job/Stage tie in
            // the same band and the warning Task (ranked Task-first by TypeRank) displaces the real
            // failure. Use the narrower "actually non-succeeded result" test for the monitor/failure
            // bands below and for the generic-failure band.
            var isActuallyFailedResult = r.Result is { Length: > 0 } && !r.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase);
            var hasUnresolvedMonitor = isActuallyFailedResult && issues.Any(i => i.Message is not null && AzdoMonitorFailureParser.IsMonitorLikeMessage(i.Message));
            if (hasUnresolvedMonitor) return 1;

            // Design order: parsed/unresolved monitor evidence, then failed monitor *name* hints
            // (no parseable/monitor-like issue text at all, but the record's own name looks like a
            // known monitor job — e.g. "Monitor Helix Jobs"), ahead of generic failed Task errors.
            // This mirrors ClassifyMonitorEvidence's own "nameHint" classification, which previously
            // had no corresponding ranking band at all.
            var hasMonitorNameHint = isActuallyFailedResult && AzdoMonitorFailureParser.IsMonitorLikeName(r.Name);
            if (hasMonitorNameHint) return 2;

            var isTaskLike = r.Type?.Equals("Task", StringComparison.OrdinalIgnoreCase) == true;
            var hasErrorIssue = issues.Any(i => i.Type?.Equals("error", StringComparison.OrdinalIgnoreCase) == true);
            if (isTaskLike && hasErrorIssue) return 3;
            if (hasErrorIssue) return 4;
            if (isActuallyFailedResult) return 5;
            if (issues.Count > 0) return 6; // warning-only (succeeded, no error-type issues)
            return 7; // context-only
        }

        int TypeRank(AzdoTimelineRecord r) => r.Type?.ToLowerInvariant() switch
        {
            "task" => 0,
            "job" => 1,
            "phase" => 2,
            "stage" => 3,
            "checkpoint" => 4,
            _ => 5
        };

        return
        [
            .. displayed
                .OrderBy(Band)
                .ThenBy(TypeRank)
                .ThenBy(r => sourceIndexById.GetValueOrDefault(r.Id!, int.MaxValue))
        ];
    }

    // ── Pagination/continuation shared by triage/compact/full (non-single-record) branches. ──

    private static TimelineAction BuildWholeSelectionContinuation(TimelineProjectionRequest request, int selectedTotal, string viewId) =>
        new()
        {
            Tool = "azdo_timeline",
            Arguments = BaseArguments(request, offset: 0, limit: Math.Max(selectedTotal, 1), viewId: viewId, delivery: "file")
        };

    private static TimelineAction BuildNextPageContinuation(TimelineProjectionRequest request, TimelineNextPage next, string viewId) =>
        new()
        {
            Tool = "azdo_timeline",
            Arguments = BaseArguments(request, offset: next.Offset, limit: next.Limit, viewId: viewId, delivery: null)
        };

    private static IReadOnlyDictionary<string, object?> BaseArguments(
        TimelineProjectionRequest request, int offset, int limit, string viewId, string? delivery)
    {
        var args = new Dictionary<string, object?>
        {
            ["buildIdOrUrl"] = request.BuildIdOrUrl,
            ["offset"] = offset,
            ["limit"] = limit,
            ["viewId"] = viewId,
            ["projection"] = request.Projection,
        };
        if (request.Filter is not null) args["filter"] = request.Filter;
        if (request.RecordId is not null) args["recordId"] = request.RecordId;
        if (request.ParentId is not null) args["parentId"] = request.ParentId;
        if (request.Type is not null) args["type"] = request.Type;
        if (request.Result is not null) args["result"] = request.Result;
        if (request.State is not null) args["state"] = request.State;
        if (request.Name is not null) args["name"] = request.Name;
        if (!request.Expand.Equals("ancestors", StringComparison.OrdinalIgnoreCase)) args["expand"] = request.Expand;
        if (request.IncludePhase is not null) args["includePhase"] = request.IncludePhase;
        // A byte-shortened candidate's own previewChars/previewIssueLimit must travel with its
        // continuation: replaying without them re-defaults to the caller's original request values,
        // which recomputes a *different* viewId (both values feed the fingerprint) and immediately
        // fails that replay's own stale-viewId check against the very page that produced it.
        if (request.PreviewIssueLimit != 5) args["previewIssueLimit"] = request.PreviewIssueLimit;
        if (request.PreviewChars != 200) args["previewChars"] = request.PreviewChars;
        args["maxResponseBytes"] = request.MaxResponseBytes;
        if (delivery is not null) args["delivery"] = delivery;
        return args;
    }

    private static TimelineProjectionResult BuildTriageProjection(
        TimelineProjectionResult baseResult,
        IReadOnlyList<AzdoTimelineRecord> ordered,
        int offset,
        int limit,
        int selectedTotal,
        Dictionary<string, string> matchKindById,
        Dictionary<string, AzdoTimelineRecord> byId,
        Func<string, string?> contextParentOf,
        TimelineProjectionRequest request,
        string viewId)
    {
        var page = ordered.Skip(offset).Take(limit).ToList();
        var rows = page.Select(r => BuildTriageRow(r, matchKindById, contextParentOf, request)).ToList();
        var hasMore = offset + page.Count < selectedTotal;
        var complete = offset == 0 && !hasMore;
        var next = hasMore ? new TimelineNextPage { Offset = offset + page.Count, Limit = limit, ViewId = viewId } : null;
        return baseResult with
        {
            TriageRows = rows,
            Returned = rows.Count,
            Total = selectedTotal,
            TotalRecords = selectedTotal,
            Complete = complete,
            Truncated = !complete,
            Next = next,
            Continuation = complete ? null : (next is not null
                ? BuildNextPageContinuation(request, next, viewId)
                : BuildWholeSelectionContinuation(request, selectedTotal, viewId))
        };
    }

    private static TimelineProjectionResult BuildCompactProjection(
        TimelineProjectionResult baseResult,
        IReadOnlyList<AzdoTimelineRecord> ordered,
        int offset,
        int limit,
        int selectedTotal,
        Dictionary<string, string> matchKindById,
        string viewId,
        TimelineProjectionRequest request)
    {
        var page = ordered.Skip(offset).Take(limit).ToList();
        var rows = page.Select(r => BuildCompactRow(r, matchKindById)).ToList();
        var hasMore = offset + page.Count < selectedTotal;
        var complete = offset == 0 && !hasMore;
        var next = hasMore ? new TimelineNextPage { Offset = offset + page.Count, Limit = limit, ViewId = viewId } : null;
        return baseResult with
        {
            CompactRows = rows,
            Returned = rows.Count,
            Total = selectedTotal,
            TotalRecords = selectedTotal,
            Complete = complete,
            Truncated = !complete,
            Next = next,
            Continuation = complete ? null : (next is not null
                ? BuildNextPageContinuation(request, next, viewId)
                : BuildWholeSelectionContinuation(request, selectedTotal, viewId))
        };
    }

    private static TimelineProjectionResult BuildFullProjection(
        TimelineProjectionResult baseResult,
        IReadOnlyList<AzdoTimelineRecord> ordered,
        int offset,
        int limit,
        int selectedTotal,
        TimelineProjectionRequest request,
        string viewId)
    {
        // Single-record explicit issue window.
        if (request.RecordId is not null && ordered.Count == 1)
        {
            var rec = ordered[0];
            var allIssues = rec.Issues ?? [];
            var issueOffset = Math.Max(0, request.IssueOffset);

            if (request.IssueLimit is null)
            {
                var windowed = issueOffset >= allIssues.Count ? [] : allIssues.Skip(issueOffset).ToList();
                // Even with no issueLimit (i.e. "take everything from issueOffset"), a nonzero
                // issueOffset means the caller has not seen issues [0, issueOffset) — that is not a
                // complete view of the record's issues, matching the same offset==0-only rule used
                // for the limited-window branch below and for row pagination.
                var windowComplete = issueOffset == 0;
                return baseResult with
                {
                    FullRecords = [rec with { Issues = windowed }],
                    Returned = 1,
                    Total = 1,
                    TotalRecords = 1,
                    Complete = windowComplete,
                    Truncated = !windowComplete,
                    IssueWindow = new TimelineIssueWindow
                    {
                        IssueTotal = allIssues.Count,
                        IssueReturned = windowed.Count,
                        IssueOffset = issueOffset,
                        IssueLimit = null,
                        IssueComplete = windowComplete,
                        IssueTruncated = !windowComplete
                    },
                    Continuation = windowComplete ? null : new TimelineAction
                    {
                        Tool = "azdo_timeline",
                        Arguments = ExtendWithIssueWindow(
                            BaseArguments(request, offset: 0, limit: 1, viewId: viewId, delivery: null),
                            new TimelineNextPage { Offset = 0, Limit = allIssues.Count, ViewId = viewId })
                    }
                };
            }

            var issuePage = allIssues.Skip(issueOffset).Take(request.IssueLimit.Value).ToList();
            var issueHasMore = issueOffset + issuePage.Count < allIssues.Count;
            // A nonzero final page is not complete, matching list-pagination semantics (§2.2): only an
            // offset-0 single-shot response that returns everything is "complete".
            var issueComplete = issueOffset == 0 && !issueHasMore;
            var issueNext = issueHasMore ? new TimelineNextPage { Offset = issueOffset + issuePage.Count, Limit = request.IssueLimit.Value, ViewId = viewId } : null;
            return baseResult with
            {
                FullRecords = [rec with { Issues = issuePage }],
                Returned = 1,
                Total = 1,
                TotalRecords = 1,
                Complete = issueComplete,
                Truncated = !issueComplete,
                IssueWindow = new TimelineIssueWindow
                {
                    IssueTotal = allIssues.Count,
                    IssueReturned = issuePage.Count,
                    IssueOffset = issueOffset,
                    IssueLimit = request.IssueLimit,
                    IssueComplete = issueComplete,
                    IssueTruncated = !issueComplete,
                    IssueNext = issueNext
                },
                Continuation = issueComplete ? null : new TimelineAction
                {
                    Tool = "azdo_timeline",
                    Arguments = ExtendWithIssueWindow(
                        BaseArguments(request, offset: 0, limit: 1, viewId: viewId, delivery: null),
                        issueNext ?? new TimelineNextPage { Offset = 0, Limit = allIssues.Count, ViewId = viewId })
                }
            };
        }

        var page = ordered.Skip(offset).Take(limit).ToList();
        var hasMore = offset + page.Count < selectedTotal;
        var complete = offset == 0 && !hasMore;
        var next = hasMore ? new TimelineNextPage { Offset = offset + page.Count, Limit = limit, ViewId = viewId } : null;
        return baseResult with
        {
            FullRecords = page,
            Returned = page.Count,
            Total = selectedTotal,
            TotalRecords = selectedTotal,
            Complete = complete,
            Truncated = !complete,
            Next = next,
            Continuation = complete ? null : (next is not null
                ? BuildNextPageContinuation(request, next, viewId)
                : BuildWholeSelectionContinuation(request, selectedTotal, viewId))
        };
    }

    private static Dictionary<string, object?> ExtendWithIssueWindow(IReadOnlyDictionary<string, object?> args, TimelineNextPage issueNext)
    {
        var copy = new Dictionary<string, object?>(args)
        {
            ["issueOffset"] = issueNext.Offset,
            ["issueLimit"] = issueNext.Limit
        };
        return copy;
    }

    private static TimelineTriageRow BuildTriageRow(
        AzdoTimelineRecord r,
        Dictionary<string, string> matchKindById,
        Func<string, string?> contextParentOf,
        TimelineProjectionRequest request)
    {
        var issues = r.Issues ?? [];
        var (previews, groupTotal, truncated) = BuildPreviews(issues, request.PreviewIssueLimit, request.PreviewChars);

        TimelineAction? fullAction = null;
        if (truncated || groupTotal < issues.Count)
        {
            fullAction = new TimelineAction
            {
                Arguments = new Dictionary<string, object?>
                {
                    ["buildIdOrUrl"] = request.BuildIdOrUrl,
                    ["recordId"] = r.Id,
                    ["filter"] = "all",
                    ["projection"] = "full",
                    ["expand"] = "none",
                    ["delivery"] = "auto"
                }
            };
        }

        return new TimelineTriageRow
        {
            Id = r.Id!,
            ParentId = r.ParentId,
            ContextParentId = contextParentOf(r.Id!),
            Type = r.Type,
            Name = r.Name,
            State = r.State,
            Result = r.Result,
            Attempt = r.Attempt,
            Log = r.Log is null ? null : new TimelineLogRef(r.Log.Id),
            DurationSeconds = ComputeDuration(r),
            MatchKind = matchKindById.GetValueOrDefault(r.Id!, "match"),
            MonitorEvidence = ClassifyMonitorEvidence(r),
            IssueTotal = issues.Count,
            IssuePreviewTotal = groupTotal,
            IssuePreviews = previews,
            PreviewsTruncated = truncated,
            FullAction = fullAction
        };
    }

    private static TimelineCompactRow BuildCompactRow(AzdoTimelineRecord r, Dictionary<string, string> matchKindById)
    {
        var issues = r.Issues ?? [];
        return new TimelineCompactRow
        {
            Id = r.Id!,
            ParentId = r.ParentId,
            Type = r.Type,
            Name = r.Name,
            State = r.State,
            Result = r.Result,
            Attempt = r.Attempt,
            Log = r.Log is null ? null : new TimelineLogRef(r.Log.Id),
            DurationSeconds = ComputeDuration(r),
            IssueCount = issues.Count,
            ErrorCount = issues.Count(i => i.Type?.Equals("error", StringComparison.OrdinalIgnoreCase) == true),
            WarningCount = issues.Count(i => i.Type?.Equals("warning", StringComparison.OrdinalIgnoreCase) == true),
            MatchKind = matchKindById.GetValueOrDefault(r.Id!, "match")
        };
    }

    private static double? ComputeDuration(AzdoTimelineRecord r) =>
        r.StartTime.HasValue && r.FinishTime.HasValue
            ? (r.FinishTime.Value - r.StartTime.Value).TotalSeconds
            : null;

    private static string? ClassifyMonitorEvidence(AzdoTimelineRecord r)
    {
        var issues = r.Issues ?? [];
        if (issues.Any(i => i.Message is not null && AzdoMonitorFailureParser.ParseMessage(i.Message).Count > 0))
            return "parsed";

        var isFailedLike = AzdoService.MatchesFilter(r, "failed");
        if (isFailedLike && issues.Any(i => i.Message is not null && AzdoMonitorFailureParser.IsMonitorLikeMessage(i.Message)))
            return "unresolved";

        if (isFailedLike && AzdoMonitorFailureParser.IsMonitorLikeName(r.Name))
            return "nameHint";

        return null;
    }

    // ── Preview dedupe/truncation ──────────────────────────────────────────

    private static readonly Regex AnsiEscapeRegex = new(@"\x1b\[[0-9;]*[a-zA-Z]", RegexOptions.Compiled);
    private static readonly Regex LeadingTimestampRegex = new(
        @"^\s*\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?\s*",
        RegexOptions.Compiled);

    private static (List<TimelineIssuePreview> Previews, int GroupTotal, bool Truncated) BuildPreviews(
        IReadOnlyList<AzdoIssue> issues, int previewIssueLimit, int previewChars)
    {
        if (issues.Count == 0)
            return ([], 0, false);

        // Group by (type, normalized full text) before truncation, preserving first-occurrence index/order.
        var groups = new List<(string Type, string RawMessage, string Normalized, int FirstIndex, int Count, bool AnyNormalizedCollapse)>();
        var groupIndexByKey = new Dictionary<(string, string), int>();

        for (var i = 0; i < issues.Count; i++)
        {
            var issue = issues[i];
            var type = issue.Type ?? "error";
            var raw = issue.Message ?? "";
            var normalized = Normalize(raw);
            var key = (type, normalized);
            if (groupIndexByKey.TryGetValue(key, out var idx))
            {
                var g = groups[idx];
                var collapsed = g.AnyNormalizedCollapse || !string.Equals(g.RawMessage, raw, StringComparison.Ordinal);
                groups[idx] = (g.Type, g.RawMessage, g.Normalized, g.FirstIndex, g.Count + 1, collapsed);
            }
            else
            {
                groupIndexByKey[key] = groups.Count;
                groups.Add((type, raw, normalized, i, 1, false));
            }
        }

        // Priority: concrete parsed monitor failures first; substantive errors next; other
        // warnings last; original issue index breaks ties within a band.
        int Band((string Type, string RawMessage, string Normalized, int FirstIndex, int Count, bool AnyNormalizedCollapse) g)
        {
            if (AzdoMonitorFailureParser.ParseMessage(g.RawMessage).Count > 0) return 0;
            if (g.Type.Equals("error", StringComparison.OrdinalIgnoreCase)) return 1;
            return 2;
        }

        var ranked = groups
            .OrderBy(Band)
            .ThenBy(g => g.FirstIndex)
            .ToList();

        var groupTotal = ranked.Count;
        var selected = ranked.Take(previewIssueLimit).ToList();

        var previews = selected.Select(g =>
        {
            var (clipped, wasClipped) = ClipToScalars(g.RawMessage, previewChars);
            return new TimelineIssuePreview
            {
                Type = g.Type,
                Message = clipped,
                Count = g.Count,
                IssueIndex = g.FirstIndex,
                MessageTruncated = wasClipped,
                Normalized = g.AnyNormalizedCollapse
            };
        }).ToList();

        // "Truncated" must cover both ways a caller sees less than the full issue text: whole groups
        // omitted past previewIssueLimit, AND any surviving preview itself clipped at previewChars
        // (e.g. one long undeduplicated message) — either case needs recovery via fullAction.
        var truncated = groupTotal > selected.Count || previews.Any(p => p.MessageTruncated);

        return (previews, groupTotal, truncated);
    }

    private static string Normalize(string message)
    {
        var withoutAnsi = AnsiEscapeRegex.Replace(message, "");
        var unified = withoutAnsi.Replace("\r\n", "\n").Replace('\r', '\n');
        var withoutLeadingTimestamp = LeadingTimestampRegex.Replace(unified, "");
        return withoutLeadingTimestamp.TrimEnd();
    }

    private static (string Clipped, bool WasClipped) ClipToScalars(string message, int previewChars)
    {
        if (previewChars <= 0)
            return ("", message.Length > 0);

        var count = 0;
        var byteIndex = 0;
        foreach (var rune in message.EnumerateRunes())
        {
            if (count >= previewChars)
                return (message[..byteIndex], true);
            byteIndex += rune.Utf16SequenceLength;
            count++;
        }
        return (message, false);
    }

    private static string ComputeViewId(AzdoTimeline timeline, TimelineProjectionRequest request, string effectiveFilter, bool effectiveIncludePhase)
    {
        // Excludes page offset/limit/byte budget; includes selectors, projection, dedupe/ranking version,
        // and a source content fingerprint so a changing live timeline invalidates stale pages. Full
        // SHA-256 hex (64 chars) — not truncated — so it can double as a collision-resistant fingerprint.
        var fingerprint = new
        {
            timeline.Id,
            ContentHash = ComputeTimelineContentHash(timeline),
            request.Expand,
            Projection = request.Projection.ToLowerInvariant(),
            EffectiveFilter = effectiveFilter,
            request.RecordId,
            request.ParentId,
            request.Type,
            request.Result,
            request.State,
            request.Name,
            IncludePhase = effectiveIncludePhase,
            request.PreviewIssueLimit,
            request.PreviewChars,
            RankingVersion = 1
        };
        var json = JsonSerializer.Serialize(fingerprint);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Cheap stable content fingerprint of the actual timeline data (not just its id/count), so a
    /// changed live build (retried task, new attempt, updated issues) invalidates a cached viewId
    /// instead of silently reusing stale pages.
    /// </summary>
    private static string ComputeTimelineContentHash(AzdoTimeline timeline)
    {
        var sb = new StringBuilder();
        foreach (var r in timeline.Records)
        {
            sb.Append(r.Id).Append('\0').Append(r.ParentId).Append('\0').Append(r.Type).Append('\0')
              .Append(r.Name).Append('\0').Append(r.State).Append('\0').Append(r.Result).Append('\0')
              .Append(r.Attempt).Append('\0').Append(r.Log?.Id).Append('\0').Append(r.Issues?.Count ?? 0);
            foreach (var issue in r.Issues ?? [])
                sb.Append('\0').Append(issue.Type).Append('\0').Append(issue.Message);
            sb.Append('\u0001');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
