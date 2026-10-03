using System.Text.Json.Serialization;
using HelixTool.Core.Delivery;

namespace HelixTool.Core.AzDO;

/// <summary>Normalized request driving <see cref="AzdoTimelineProjector"/>. Pure value object — no I/O.</summary>
public sealed record TimelineProjectionRequest
{
    /// <summary>Preset filter ('failed', 'all', 'running', 'pending', 'incomplete', 'issues'); null means "resolve from selectors".</summary>
    public string? Filter { get; init; }
    public string? RecordId { get; init; }
    public string? ParentId { get; init; }
    public string? Type { get; init; }

    /// <summary>Exact result value: failed, canceled, abandoned, skipped, succeededWithIssues, succeeded, none.</summary>
    public string? Result { get; init; }

    /// <summary>Exact provider state: pending, inProgress, completed (plus legacy running/active/not-started aliases).</summary>
    public string? State { get; init; }

    /// <summary>Case-insensitive name glob (*, ?); non-wildcard pattern is a legacy substring match.</summary>
    public string? Name { get; init; }

    public string Expand { get; init; } = "ancestors";
    public string Projection { get; init; } = "triage";
    public bool? IncludePhase { get; init; }
    public int PreviewIssueLimit { get; init; } = 5;
    public int PreviewChars { get; init; } = 200;
    public int Offset { get; init; }
    public int Limit { get; init; } = 10;
    public string? ViewId { get; init; }
    public int IssueOffset { get; init; }
    public int? IssueLimit { get; init; }
    public long MaxResponseBytes { get; init; } = 12_288;
    public bool All { get; init; }
    public string Delivery { get; init; } = "auto";

    /// <summary>Original caller-supplied build identifier/URL, carried only so recovery actions can reference it exactly.</summary>
    public string BuildIdOrUrl { get; init; } = "";

    public bool HasExplicitSelector =>
        RecordId is not null || ParentId is not null || Type is not null ||
        Result is not null || State is not null || Name is not null;
}

/// <summary>Timeline log reference projected onto triage/compact rows: id only, per the design's exact field list.</summary>
public sealed record TimelineLogRef([property: JsonPropertyName("id")] int Id);

/// <summary>One deduplicated, truncation-aware issue preview within a triage row.</summary>
public sealed record TimelineIssuePreview
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Number of raw source issues collapsed into this preview (formatting-equivalent dedupe only).</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>Index of the first occurrence within the record's raw (ungrouped) issue list.</summary>
    [JsonPropertyName("issueIndex")]
    public required int IssueIndex { get; init; }

    [JsonPropertyName("messageTruncated")]
    public required bool MessageTruncated { get; init; }

    /// <summary>True when this preview collapsed formatting-only near-duplicate variants (same meaning, not an edit-distance guess).</summary>
    [JsonPropertyName("normalized")]
    public required bool Normalized { get; init; }
}

/// <summary>Exact next hlx invocation to recover a row's complete issue/record detail.</summary>
public sealed record TimelineAction
{
    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "azdo_timeline";

    [JsonPropertyName("arguments")]
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
}

/// <summary>Triage projection row: diagnostic-first identity/result fields plus ranked issue previews.</summary>
public sealed record TimelineTriageRow
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    /// <summary>Nearest displayed non-suppressed ancestor id, found by traversing the full (pre-suppression) graph.</summary>
    [JsonPropertyName("contextParentId")]
    public string? ContextParentId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; init; }

    [JsonPropertyName("log")]
    public TimelineLogRef? Log { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }

    /// <summary>match | ancestor | child | descendant — match takes precedence when a record is both.</summary>
    [JsonPropertyName("matchKind")]
    public required string MatchKind { get; init; }

    /// <summary>parsed | unresolved | nameHint | null.</summary>
    [JsonPropertyName("monitorEvidence")]
    public string? MonitorEvidence { get; init; }

    [JsonPropertyName("issueTotal")]
    public required int IssueTotal { get; init; }

    /// <summary>Count of distinct deduplicated issue groups, independent of how many previews were included.</summary>
    [JsonPropertyName("issuePreviewTotal")]
    public required int IssuePreviewTotal { get; init; }

    [JsonPropertyName("issuePreviews")]
    public required IReadOnlyList<TimelineIssuePreview> IssuePreviews { get; init; }

    [JsonPropertyName("previewsTruncated")]
    public required bool PreviewsTruncated { get; init; }

    [JsonPropertyName("fullAction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineAction? FullAction { get; init; }
}

/// <summary>Compact projection row: identity/count fields without preview text.</summary>
public sealed record TimelineCompactRow
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("attempt")]
    public int? Attempt { get; init; }

    [JsonPropertyName("log")]
    public TimelineLogRef? Log { get; init; }

    [JsonPropertyName("durationSeconds")]
    public double? DurationSeconds { get; init; }

    [JsonPropertyName("issueCount")]
    public required int IssueCount { get; init; }

    [JsonPropertyName("errorCount")]
    public required int ErrorCount { get; init; }

    [JsonPropertyName("warningCount")]
    public required int WarningCount { get; init; }

    [JsonPropertyName("matchKind")]
    public required string MatchKind { get; init; }
}

public sealed record TimelineTypeResultCount(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("result")] string Result,
    [property: JsonPropertyName("count")] int Count);

/// <summary>Exact seed counts for the selected presentation scope, independent of output paging. Context ancestors never inflate these.</summary>
public sealed record TimelineCounts
{
    [JsonPropertyName("scope")]
    public string Scope { get; init; } = "matched";

    [JsonPropertyName("complete")]
    public bool Complete { get; init; } = true;

    [JsonPropertyName("byTypeResult")]
    public IReadOnlyList<TimelineTypeResultCount> ByTypeResult { get; init; } = [];
}

public sealed record TimelineIncompleteDetail(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record TimelineNextPage
{
    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("viewId")]
    public string? ViewId { get; init; }
}

/// <summary>
/// Explicit per-issue window for a single full-projection record (projection=full, one exact
/// recordId). Exposed on <see cref="TimelineProjectionResult.IssueWindow"/> for direct Core
/// consumers, and flattened onto top-level <c>issueTotal</c>/<c>issueReturned</c>/etc JSON fields
/// so MCP/CLI callers never need a second nesting level to read it.
/// </summary>
public sealed record TimelineIssueWindow
{
    public required int IssueReturned { get; init; }
    public required int IssueTotal { get; init; }
    public required int IssueOffset { get; init; }
    public int? IssueLimit { get; init; }
    public required bool IssueComplete { get; init; }
    public required bool IssueTruncated { get; init; }
    public TimelineNextPage? IssueNext { get; init; }
}

/// <summary>
/// Complete projection output. <see cref="Records"/> is a single unified array whose element shape
/// depends on <see cref="Projection"/> (triage/compact rows, or raw <see cref="AzdoTimelineRecord"/>
/// for full/summary) — matching the existing MCP <c>records</c> contract; there is never a second
/// large array for the same page.
/// </summary>
public sealed record TimelineProjectionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("projection")]
    public required string Projection { get; init; }

    [JsonPropertyName("effectiveFilter")]
    public required string EffectiveFilter { get; init; }

    [JsonPropertyName("viewId")]
    public required string ViewId { get; init; }

    /// <summary>Seed count before expansion.</summary>
    [JsonPropertyName("matched")]
    public required int Matched { get; init; }

    /// <summary>Deduplicated record count after expansion (and, for triage/summary, after Phase suppression) — the size of the paged set.</summary>
    [JsonPropertyName("selectedTotal")]
    public required int SelectedTotal { get; init; }

    /// <summary>Raw timeline record count, including suppressed records.</summary>
    [JsonPropertyName("timelineRecords")]
    public required int TimelineRecords { get; init; }

    [JsonPropertyName("selectionScope")]
    public required string SelectionScope { get; init; }

    [JsonPropertyName("includePhase")]
    public required bool IncludePhase { get; init; }

    [JsonPropertyName("phaseSuppressed")]
    public required bool PhaseSuppressed { get; init; }

    [JsonPropertyName("previewIssueLimit")]
    public required int PreviewIssueLimit { get; init; }

    [JsonPropertyName("previewChars")]
    public required int PreviewChars { get; init; }

    [JsonPropertyName("maxResponseBytes")]
    public required long MaxResponseBytes { get; init; }

    [JsonPropertyName("counts")]
    public required TimelineCounts Counts { get; init; }

    [JsonPropertyName("incompleteDetails")]
    public IReadOnlyList<TimelineIncompleteDetail> IncompleteDetails { get; init; } = [];

    // ── Row payloads: exactly one of these three is non-null, matching Projection. ──
    [JsonIgnore]
    public IReadOnlyList<TimelineTriageRow>? TriageRows { get; init; }

    [JsonIgnore]
    public IReadOnlyList<TimelineCompactRow>? CompactRows { get; init; }

    [JsonIgnore]
    public IReadOnlyList<AzdoTimelineRecord>? FullRecords { get; init; }

    /// <summary>Unified records array; shape depends on <see cref="Projection"/>. Never a second large array alongside this one.</summary>
    [JsonPropertyName("records")]
    public IReadOnlyList<object> Records =>
        TriageRows is not null ? [.. TriageRows.Cast<object>()] :
        CompactRows is not null ? [.. CompactRows.Cast<object>()] :
        FullRecords is not null ? [.. FullRecords.Cast<object>()] :
        [];

    [JsonPropertyName("returned")]
    public required int Returned { get; init; }

    /// <summary>Exact number in the selected set (= selectedTotal); offline views always know this, so it is never null here.</summary>
    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("totalRecords")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalRecords { get; init; }

    [JsonPropertyName("offset")]
    public required int Offset { get; init; }

    [JsonPropertyName("limit")]
    public required int Limit { get; init; }

    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }

    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    [JsonPropertyName("next")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TimelineNextPage? Next { get; init; }

    /// <summary>Exact next hlx invocation whenever <see cref="Complete"/> is false — next page or whole-selection recovery.</summary>
    [JsonPropertyName("continuation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimelineAction? Continuation { get; init; }

    // ── Explicit single-record issue window (projection=full, one exact recordId). Omitted as a
    // group when no issue window applies; issueNext is force-emitted (even as null) only when the
    // window itself is present, so a legitimately-null next page is never confused with absence. ──
    [JsonIgnore]
    public TimelineIssueWindow? IssueWindow { get; init; }

    [JsonPropertyName("issueTotal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IssueTotal => IssueWindow?.IssueTotal;

    [JsonPropertyName("issueReturned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IssueReturned => IssueWindow?.IssueReturned;

    [JsonPropertyName("issueOffset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IssueOffset => IssueWindow?.IssueOffset;

    [JsonPropertyName("issueLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? IssueLimit => IssueWindow?.IssueLimit;

    [JsonPropertyName("issueComplete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IssueComplete => IssueWindow?.IssueComplete;

    [JsonPropertyName("issueTruncated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IssueTruncated => IssueWindow?.IssueTruncated;

    /// <summary>Force-emitted as literal null whenever an issue window is active but has no next page; omitted entirely when no issue window applies.</summary>
    [JsonPropertyName("issueNext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TimelineNextPage? IssueNext => IssueWindow?.IssueNext;

    /// <summary>
    /// Present only when the requested payload did not fit <see cref="MaxResponseBytes"/> and was
    /// materialized to a verified cache-backed evidence file instead. Read it with
    /// <c>hlx_read_evidence(evidenceId=delivery.evidenceId)</c>.
    /// </summary>
    [JsonPropertyName("delivery")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceDescriptor? Delivery { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }
}
