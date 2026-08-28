using System;
using System.Text.Json.Serialization;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Options that control how <see cref="AzdoService.GetEvidencePlanAsync"/> selects jobs and
/// matches them to artifacts. All defaults mirror PR #132609 conventions.
/// </summary>
public sealed record AzdoEvidencePlanOptions
{
    /// <summary>
    /// Glob pattern applied to artifact names before matching.
    /// Supports <c>*</c> (all), <c>*.ext</c> (suffix), <c>Prefix*</c> (prefix), or substring.
    /// Default: <c>*</c> (all artifacts).
    /// </summary>
    public string ArtifactPattern { get; init; } = "*";

    /// <summary>
    /// Prefix stripped from artifact names before computing the match key.
    /// e.g. <c>Logs_Build_</c>. Stripped via ordinal <c>StartsWith</c>; no regex.
    /// </summary>
    public string? ArtifactJobPrefix { get; init; }

    /// <summary>
    /// When <c>true</c>, additionally strip <c>AttemptN_</c> from the artifact name
    /// (after <see cref="ArtifactJobPrefix"/> is removed) and record the attempt number.
    /// </summary>
    public bool StripAttemptPrefix { get; init; } = true;

    /// <summary>
    /// Matching strategy.  One of <c>"auto"</c>, <c>"source-id"</c>,
    /// <c>"normalized-exact"</c>, or <c>"exact"</c>.
    /// Default: <c>"auto"</c> (source-id primary, normalized-exact fallback).
    /// </summary>
    public string Match { get; init; } = AzdoEvidenceMatchStrategy.Auto;

    /// <summary>
    /// Job result values that qualify a timeline record as a target for evidence collection.
    /// Default: <c>["failed", "canceled"]</c>.
    /// </summary>
    public IReadOnlyList<string> JobResults { get; init; } = ["failed", "canceled"];
}

/// <summary>Named constants for <see cref="AzdoEvidencePlanOptions.Match"/>.</summary>
public static class AzdoEvidenceMatchStrategy
{
    /// <summary>source-id join first; normalized-exact fallback for unmapped jobs.</summary>
    public const string Auto = "auto";

    /// <summary>Identity join (<c>artifact.source == record.id</c>) only; unmapped → missing.</summary>
    public const string SourceId = "source-id";

    /// <summary>PR #132609 parity: normalize key, then exact equality. May be ambiguous on retried builds.</summary>
    public const string NormalizedExact = "normalized-exact";

    /// <summary>Ordinal-ignore-case equality after prefix strip, no normalization.</summary>
    public const string Exact = "exact";

    /// <summary>All valid strategy names, for validation.</summary>
    public static readonly IReadOnlyList<string> AllValues =
        [Auto, SourceId, NormalizedExact, Exact];

    /// <summary>Returns the documented lowercase form of a valid strategy name.</summary>
    public static string Canonicalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var candidate in AllValues)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        throw new ArgumentException(
            $"Invalid match strategy '{value}'. Must be one of: {string.Join(", ", AllValues)}.",
            nameof(value));
    }
}

/// <summary>
/// Build metadata emitted as <c>plan.build</c>.
/// All <c>pr*</c> fields are <c>null</c> for non-PR builds.
/// </summary>
public sealed record AzdoBuildProvenance
{
    [JsonPropertyName("buildId")]
    public int BuildId { get; init; }

    [JsonPropertyName("buildNumber")]
    public string? BuildNumber { get; init; }

    [JsonPropertyName("definitionName")]
    public string? DefinitionName { get; init; }

    [JsonPropertyName("definitionId")]
    public int? DefinitionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTimeOffset? FinishTime { get; init; }

    [JsonPropertyName("webUrl")]
    public string WebUrl { get; init; } = "";

    [JsonPropertyName("org")]
    public string Org { get; init; } = "";

    [JsonPropertyName("project")]
    public string Project { get; init; } = "";

    // --- triggerInfo PR fields ---

    [JsonPropertyName("prNumber")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrNumber { get; init; }

    [JsonPropertyName("prSourceSha")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrSourceSha { get; init; }

    [JsonPropertyName("prSourceBranch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrSourceBranch { get; init; }

    [JsonPropertyName("prIsFork")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrIsFork { get; init; }

    [JsonPropertyName("prDraft")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PrDraft { get; init; }

    [JsonPropertyName("prProviderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrProviderId { get; init; }

    /// <summary>
    /// Constructs an <see cref="AzdoBuildProvenance"/> from an <see cref="AzdoBuild"/> plus
    /// the organization and project strings required to build the web URL.
    /// </summary>
    public static AzdoBuildProvenance FromBuild(AzdoBuild build, string org, string project)
    {
        var ti = build.TriggerInfo;
        var webUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_build/results?buildId={build.Id}";
        return new AzdoBuildProvenance
        {
            BuildId    = build.Id,
            BuildNumber    = build.BuildNumber,
            DefinitionName = build.Definition?.Name,
            DefinitionId   = build.Definition?.Id,
            Status         = build.Status,
            Result         = build.Result,
            SourceBranch   = build.SourceBranch,
            SourceVersion  = build.SourceVersion,
            FinishTime     = build.FinishTime,
            WebUrl         = webUrl,
            Org            = org,
            Project        = project,
            PrNumber       = ti?.PrNumber,
            PrSourceSha    = ti?.PrSourceSha,
            PrSourceBranch = ti?.PrSourceBranch,
            PrIsFork       = AzdoTriggerInfo.TryParseAzdoBool(ti?.PrIsFork),
            PrDraft        = AzdoTriggerInfo.TryParseAzdoBool(ti?.PrDraft),
            PrProviderId   = ti?.PrProviderId,
        };
    }
}

/// <summary>
/// A single artifact that is a candidate for one evidence-plan entry.
/// Candidates are ranked for presentation only; rank 0 is NOT a selection.
/// </summary>
public sealed record AzdoEvidenceCandidate
{
    /// <summary>Presentation rank within the entry (0-based). Not a selection.</summary>
    [JsonPropertyName("rank")]
    public int Rank { get; init; }

    [JsonPropertyName("artifactId")]
    public int ArtifactId { get; init; }

    [JsonPropertyName("artifactName")]
    public string ArtifactName { get; init; } = "";

    /// <summary>GUID of the Job timeline record that published this artifact.</summary>
    [JsonPropertyName("source")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Source { get; init; }

    /// <summary>Attempt number parsed from <c>AttemptN_</c> prefix, when stripping is enabled.</summary>
    [JsonPropertyName("attempt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Attempt { get; init; }

    [JsonPropertyName("resourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResourceType { get; init; }

    /// <summary>Credential-free download URL (query is <c>format=zip</c> only).</summary>
    [JsonPropertyName("downloadUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadUrl { get; init; }

    /// <summary>Artifact size in bytes from <c>resource.properties.artifactsize</c>, when present.</summary>
    [JsonPropertyName("sizeBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SizeBytes { get; init; }
}

/// <summary>
/// One entry in the evidence plan, corresponding to a single failed/canceled Job record.
/// </summary>
public sealed record AzdoEvidencePlanEntry
{
    /// <summary>The AzDO timeline record ID (GUID) for this job.</summary>
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("jobName")]
    public string JobName { get; init; } = "";

    [JsonPropertyName("jobResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JobResult { get; init; }

    [JsonPropertyName("jobOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JobOrder { get; init; }

    /// <summary>Job attempt number from the timeline record, when present.</summary>
    [JsonPropertyName("jobAttempt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JobAttempt { get; init; }

    /// <summary>
    /// Which matching strategy produced this entry's candidates:
    /// <c>"source-id"</c>, <c>"normalized-name"</c>, <c>"exact"</c>, or <c>null</c> (missing).
    /// </summary>
    [JsonPropertyName("matchedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MatchedBy { get; init; }

    /// <summary>
    /// <c>"mapped"</c> (exactly one candidate), <c>"ambiguous"</c> (>1), or <c>"missing"</c> (0).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("candidates")]
    public IReadOnlyList<AzdoEvidenceCandidate> Candidates { get; init; } = [];

    /// <summary>Total matching candidates before the bounded <see cref="Candidates"/> list was truncated.</summary>
    [JsonPropertyName("candidateTotal")]
    public int CandidateTotal { get; init; }

    /// <summary><c>true</c> when <see cref="CandidateTotal"/> exceeds the number of returned candidates.</summary>
    [JsonPropertyName("candidatesTruncated")]
    public bool CandidatesTruncated { get; init; }

    /// <summary>Human-readable candidate truncation summary.</summary>
    [JsonPropertyName("candidateNote")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CandidateNote { get; init; }
}

/// <summary>
/// Result returned by <see cref="AzdoEvidenceMatcher.BuildPlan"/>.
/// Contains the matched entries plus diagnostic completeness information.
/// </summary>
public sealed record AzdoBuiltPlanResult
{
    /// <summary>Matched entries (capped at <see cref="AzdoEvidenceMatcher.MaxPlanEntries"/>).</summary>
    public IReadOnlyList<AzdoEvidencePlanEntry> Entries { get; init; } = [];

    /// <summary><c>true</c> when every entry has exactly one mapped candidate and no truncation occurred.</summary>
    public bool Complete { get; init; }

    /// <summary>Human-readable reason for each ambiguous/missing/truncated entry.</summary>
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];

    /// <summary>
    /// Non-fatal planning diagnostics produced by the matcher. This internal list is not capped;
    /// <see cref="AzdoService.GetEvidencePlanAsync"/> applies the public result bound.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary><c>true</c> when the entry list or any entry's candidate list was truncated.</summary>
    public bool Truncated { get; init; }

    /// <summary>Total selected jobs before entry truncation.</summary>
    public int Total { get; init; }

    /// <summary>Canonical documented match strategy used to build the entries.</summary>
    public string MatchStrategy { get; init; } = "";

    /// <summary>Human-readable summary note (set when truncated).</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Output of <see cref="AzdoService.GetEvidencePlanAsync"/>: a deterministic,
/// read-only plan mapping failed/canceled jobs to their artifact candidates.
/// Nothing is downloaded.
/// </summary>
public sealed record AzdoEvidencePlan
{
    /// <summary>Maximum number of original warning diagnostics returned in <see cref="Warnings"/>.</summary>
    public const int MaxWarnings = 10;

    [JsonPropertyName("buildId")]
    public int BuildId { get; init; }

    [JsonPropertyName("build")]
    public AzdoBuildProvenance Build { get; init; } = new();

    /// <summary><c>true</c> when the build's status is not <c>"completed"</c>.</summary>
    [JsonPropertyName("buildIncomplete")]
    public bool BuildIncomplete { get; init; }

    [JsonPropertyName("matchStrategy")]
    public string MatchStrategy { get; init; } = "";

    [JsonPropertyName("jobResultsFilter")]
    public IReadOnlyList<string> JobResultsFilter { get; init; } = [];

    [JsonPropertyName("artifactPattern")]
    public string ArtifactPattern { get; init; } = "*";

    [JsonPropertyName("artifactJobPrefix")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtifactJobPrefix { get; init; }

    [JsonPropertyName("stripAttemptPrefix")]
    public bool StripAttemptPrefix { get; init; }

    [JsonPropertyName("entries")]
    public IReadOnlyList<AzdoEvidencePlanEntry> Entries { get; init; } = [];

    /// <summary><c>true</c> when every entry has exactly one mapped candidate and no output was truncated.</summary>
    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    /// <summary>Human-readable reason for each ambiguous/missing/truncated entry or plan.</summary>
    [JsonPropertyName("incompleteReasons")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string> IncompleteReasons { get; init; } = [];

    /// <summary>
    /// The first <see cref="MaxWarnings"/> original non-fatal planning diagnostics in deterministic order.
    /// </summary>
    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Total warnings before the bounded <see cref="Warnings"/> list was truncated.</summary>
    [JsonPropertyName("warningTotal")]
    public int WarningTotal { get; init; }

    /// <summary><c>true</c> when <see cref="WarningTotal"/> exceeds the number of returned warnings.</summary>
    [JsonPropertyName("warningsTruncated")]
    public bool WarningsTruncated { get; init; }

    /// <summary><c>true</c> when the entry list or any entry's candidate list was truncated.</summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("totalEntries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalEntries { get; init; }

    [JsonPropertyName("note")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }
}
