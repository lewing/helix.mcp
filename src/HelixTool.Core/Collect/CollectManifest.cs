using System.Text.Json.Serialization;
using HelixTool.Core.Acquisition;

namespace HelixTool.Core.Collect;

public sealed record CollectManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "hlx.collect.azdo-build";

    [JsonPropertyName("manifestId")]
    public string ManifestId { get; init; } = "";

    [JsonPropertyName("hlxVersion")]
    public string HlxVersion { get; init; } = "";

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("completedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("command")]
    public CollectCommandInfo Command { get; init; } = new();

    [JsonPropertyName("source")]
    public CollectSourceInfo Source { get; init; } = new();

    [JsonPropertyName("auth")]
    public CollectAuthInfo Auth { get; init; } = new();

    [JsonPropertyName("policy")]
    public CollectPolicyInfo Policy { get; init; } = new();

    [JsonPropertyName("cache")]
    public CollectCacheInfo Cache { get; init; } = new();

    [JsonPropertyName("snapshot")]
    public CollectSnapshotInfo Snapshot { get; init; } = new();

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("exitCode")]
    public int ExitCode { get; init; }

    [JsonPropertyName("incompleteDetails")]
    public IReadOnlyList<CollectIncompleteDetail> IncompleteDetails { get; init; } = [];

    [JsonPropertyName("summary")]
    public CollectSummary Summary { get; init; } = new();

    [JsonPropertyName("attempts")]
    public IReadOnlyList<CollectFetchAttempt> Attempts { get; init; } = [];
}

public sealed record CollectCommandInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "hlx collect azdo-build";

    [JsonPropertyName("argv")]
    public IReadOnlyList<string> Argv { get; init; } = [];

    [JsonPropertyName("options")]
    public IReadOnlyDictionary<string, object?> Options { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

public sealed record CollectSourceInfo
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "azdo";

    [JsonPropertyName("org")]
    public string? Org { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("buildId")]
    public int? BuildId { get; init; }

    [JsonPropertyName("buildUrl")]
    public string? BuildUrl { get; init; }

    [JsonPropertyName("definitionId")]
    public int? DefinitionId { get; init; }

    [JsonPropertyName("definitionName")]
    public string? DefinitionName { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }
}

public sealed record CollectAuthInfo
{
    [JsonPropertyName("azdo")]
    public CollectAzdoAuthInfo Azdo { get; init; } = new();

    [JsonPropertyName("helix")]
    public CollectHelixAuthInfo Helix { get; init; } = new();
}

public sealed record CollectAzdoAuthInfo
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "anonymous";

    [JsonPropertyName("cachePartition")]
    public string CachePartition { get; init; } = "public";

    [JsonPropertyName("replay")]
    public string Replay { get; init; } = "public";

    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record CollectHelixAuthInfo
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "anonymous";
}

public sealed record CollectPolicyInfo
{
    [JsonPropertyName("requiredOperations")]
    public IReadOnlyList<string> RequiredOperations { get; init; } = [];

    [JsonPropertyName("maxConcurrency")]
    public int MaxConcurrency { get; init; }

    [JsonPropertyName("retry")]
    public CollectRetryPolicyInfo Retry { get; init; } = new();

    [JsonPropertyName("caps")]
    public CollectCapsInfo Caps { get; init; } = new();

    [JsonPropertyName("logScope")]
    public string LogScope { get; init; } = "";

    [JsonPropertyName("testScope")]
    public string TestScope { get; init; } = "";

    [JsonPropertyName("maxTestResultsExplicit")]
    public bool MaxTestResultsExplicit { get; init; }

    [JsonPropertyName("testAttachmentScope")]
    public string TestAttachmentScope { get; init; } = "diagnostic";

    [JsonPropertyName("helixScope")]
    public string HelixScope { get; init; } = "";
}

public sealed record CollectRetryPolicyInfo
{
    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; init; }

    [JsonPropertyName("kinds")]
    public IReadOnlyList<AcquisitionErrorKind> Kinds { get; init; } = [];

    [JsonPropertyName("initialDelay")]
    public string InitialDelay { get; init; } = "";

    [JsonPropertyName("maxDelay")]
    public string MaxDelay { get; init; } = "";
}

public sealed record CollectCapsInfo
{
    [JsonPropertyName("maxTestResults")]
    public long MaxTestResults { get; init; } = CollectPolicy.DefaultMaxTestResults;

    [JsonPropertyName("maxTestAttachments")]
    public long MaxTestAttachments { get; init; } = CollectPolicy.DefaultMaxTestAttachments;

    [JsonPropertyName("maxFileBytes")]
    public long MaxFileBytes { get; init; }

    [JsonPropertyName("maxTotalBytes")]
    public long MaxTotalBytes { get; init; }
}

public sealed record CollectCacheInfo
{
    [JsonPropertyName("root")]
    public string Root { get; init; } = "";

    [JsonPropertyName("evalMode")]
    public bool EvalMode { get; init; }
}

public sealed record CollectSnapshotInfo
{
    [JsonPropertyName("exported")]
    public bool Exported { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }

    [JsonPropertyName("manifestPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManifestPath { get; init; }

    [JsonPropertyName("validated")]
    public bool Validated { get; init; }

    [JsonPropertyName("validationErrors")]
    public IReadOnlyList<string> ValidationErrors { get; init; } = [];

    [JsonPropertyName("validationWarnings")]
    public IReadOnlyList<string> ValidationWarnings { get; init; } = [];
}

public sealed record CollectSummary
{
    [JsonPropertyName("attempted")]
    public int Attempted { get; init; }

    [JsonPropertyName("ok")]
    public int Ok { get; init; }

    [JsonPropertyName("cached")]
    public int Cached { get; init; }

    [JsonPropertyName("recordedFailure")]
    public int RecordedFailure { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("skipped")]
    public int Skipped { get; init; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }
}

public sealed record CollectFetchAttempt
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("parentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }

    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    [JsonPropertyName("required")]
    public bool Required { get; init; }

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "";

    [JsonPropertyName("operation")]
    public string Operation { get; init; } = "";

    [JsonPropertyName("resource")]
    public IReadOnlyDictionary<string, object?> Resource { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);

    [JsonPropertyName("cacheKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CacheKey { get; init; }

    [JsonPropertyName("completeCacheKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompleteCacheKey { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; init; }

    [JsonPropertyName("finishedAt")]
    public DateTimeOffset FinishedAt { get; init; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; init; }

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; init; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "";

    [JsonPropertyName("bytes")]
    public long? Bytes { get; init; }

    [JsonPropertyName("sha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256 { get; init; }

    [JsonPropertyName("paging")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CollectPagingInfo? Paging { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AcquisitionError? Error { get; init; }

    [JsonPropertyName("skip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CollectSkipInfo? Skip { get; init; }
}

public sealed record CollectPagingInfo
{
    [JsonPropertyName("returned")]
    public int Returned { get; init; }

    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("offset")]
    public int Offset { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("complete")]
    public bool Complete { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    [JsonPropertyName("next")]
    public object? Next { get; init; }
}

public sealed record CollectSkipInfo
{
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";
}

public sealed record CollectIncompleteDetail
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    [JsonPropertyName("operation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operation { get; init; }

    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Resource { get; init; }
}
