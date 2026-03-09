using System.Text.Json.Serialization;

namespace HelixTool.Core.AzDO;

/// <summary>Generic wrapper for AzDO REST API list responses.</summary>
public sealed record AzdoListResponse<T>
{
    [JsonPropertyName("value")]
    public IReadOnlyList<T> Value { get; init; } = [];

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary>AzDO build (GET _apis/build/builds/{id}).</summary>
public sealed record AzdoBuild
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("buildNumber")]
    public string? BuildNumber { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("definition")]
    public AzdoBuildDefinition? Definition { get; init; }

    [JsonPropertyName("sourceBranch")]
    public string? SourceBranch { get; init; }

    [JsonPropertyName("sourceVersion")]
    public string? SourceVersion { get; init; }

    [JsonPropertyName("queueTime")]
    public DateTimeOffset? QueueTime { get; init; }

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTimeOffset? FinishTime { get; init; }

    [JsonPropertyName("requestedFor")]
    public AzdoIdentityRef? RequestedFor { get; init; }

    [JsonPropertyName("triggerInfo")]
    public AzdoTriggerInfo? TriggerInfo { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Nested build definition reference.</summary>
public sealed record AzdoBuildDefinition
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>Identity reference (requestedFor, etc.).</summary>
public sealed record AzdoIdentityRef
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
}

/// <summary>
/// Trigger information for PR-triggered builds.
/// AzDO stores PR number in <c>ci.message</c> as "Merge pull request {n} ..."
/// and in <c>pr.number</c> when available.
/// </summary>
public sealed record AzdoTriggerInfo
{
    [JsonPropertyName("ci.message")]
    public string? CiMessage { get; init; }

    [JsonPropertyName("pr.number")]
    public string? PrNumber { get; init; }
}

/// <summary>Query parameters for filtering builds (client-side, not serialized from API).</summary>
public sealed record AzdoBuildFilter
{
    public string? PrNumber { get; init; }
    public string? Branch { get; init; }
    public int? DefinitionId { get; init; }
    public int? Top { get; init; }
    public string? StatusFilter { get; init; }
}

/// <summary>Build timeline (GET _apis/build/builds/{id}/timeline).</summary>
public sealed record AzdoTimeline
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("records")]
    public IReadOnlyList<AzdoTimelineRecord> Records { get; init; } = [];
}

/// <summary>Single timeline record (Stage, Phase, Job, or Task).</summary>
public sealed record AzdoTimelineRecord
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

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

    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; init; }

    [JsonPropertyName("finishTime")]
    public DateTimeOffset? FinishTime { get; init; }

    [JsonPropertyName("log")]
    public AzdoLogReference? Log { get; init; }

    [JsonPropertyName("order")]
    public int? Order { get; init; }

    [JsonPropertyName("issues")]
    public IReadOnlyList<AzdoIssue>? Issues { get; init; }

    [JsonPropertyName("workerName")]
    public string? WorkerName { get; init; }

    [JsonPropertyName("previousAttempts")]
    public IReadOnlyList<AzdoTimelineAttempt>? PreviousAttempts { get; init; }
}

/// <summary>Log reference within a timeline record.</summary>
public sealed record AzdoLogReference
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Previous attempt reference for retried timeline records.</summary>
public sealed record AzdoTimelineAttempt
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("timelineId")]
    public string? TimelineId { get; init; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; init; }
}

/// <summary>Issue (error/warning) attached to a timeline record.</summary>
public sealed record AzdoIssue
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }
}

/// <summary>Commit/change associated with a build.</summary>
public sealed record AzdoBuildChange
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("author")]
    public AzdoChangeAuthor? Author { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>Author of a build change.</summary>
public sealed record AzdoChangeAuthor
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }
}

/// <summary>AzDO test run (GET _apis/test/runs).</summary>
public sealed record AzdoTestRun
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("totalTests")]
    public int TotalTests { get; init; }

    [JsonPropertyName("passedTests")]
    public int PassedTests { get; init; }

    [JsonPropertyName("failedTests")]
    public int FailedTests { get; init; }

    [JsonPropertyName("startedDate")]
    public DateTimeOffset? StartedDate { get; init; }

    [JsonPropertyName("completedDate")]
    public DateTimeOffset? CompletedDate { get; init; }

    [JsonPropertyName("buildConfiguration")]
    public AzdoBuildConfiguration? BuildConfiguration { get; init; }
}

/// <summary>Build configuration reference on a test run.</summary>
public sealed record AzdoBuildConfiguration
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("buildDefinitionId")]
    public int? BuildDefinitionId { get; init; }
}

/// <summary>Formatted build summary returned by <see cref="AzdoService.GetBuildSummaryAsync"/>.</summary>
public sealed record AzdoBuildSummary(
    int Id,
    string? BuildNumber,
    string? Status,
    string? Result,
    string? DefinitionName,
    int? DefinitionId,
    string? SourceBranch,
    string? SourceVersion,
    DateTimeOffset? QueueTime,
    DateTimeOffset? StartTime,
    DateTimeOffset? FinishTime,
    TimeSpan? Duration,
    string? RequestedFor,
    string WebUrl);

/// <summary>Build artifact (GET _apis/build/builds/{id}/artifacts).</summary>
public sealed record AzdoBuildArtifact
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("resource")]
    public AzdoArtifactResource? Resource { get; init; }
}

/// <summary>Resource details for a build artifact.</summary>
public sealed record AzdoArtifactResource
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("downloadUrl")]
    public string? DownloadUrl { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Attachment on a test result (GET _apis/test/Runs/{runId}/Results/{resultId}/attachments).</summary>
public sealed record AzdoTestAttachment
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("createdDate")]
    public DateTimeOffset? CreatedDate { get; init; }
}

/// <summary>Individual test result within a test run.</summary>
public sealed record AzdoTestResult
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("testCaseTitle")]
    public string? TestCaseTitle { get; init; }

    [JsonPropertyName("outcome")]
    public string? Outcome { get; init; }

    [JsonPropertyName("durationInMs")]
    public double? DurationInMs { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("automatedTestName")]
    public string? AutomatedTestName { get; init; }
}

/// <summary>A single timeline record matching a search pattern.</summary>
public sealed class TimelineSearchMatch
{
    [JsonPropertyName("recordId")] public string RecordId { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("result")] public string? Result { get; init; }
    [JsonPropertyName("duration")] public string? Duration { get; init; }
    [JsonPropertyName("logId")] public int? LogId { get; init; }
    [JsonPropertyName("matchedIssues")] public List<string> MatchedIssues { get; init; } = [];
    [JsonPropertyName("parentName")] public string? ParentName { get; init; }

    /// <summary>Raw timeline record for programmatic access (excluded from JSON serialization).</summary>
    [JsonIgnore] public AzdoTimelineRecord? Record { get; init; }
}

/// <summary>Result of searching timeline records by pattern.</summary>
public sealed class TimelineSearchResult
{
    [JsonPropertyName("build")] public string Build { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalRecords")] public int TotalRecords { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("matches")] public List<TimelineSearchMatch> Matches { get; init; } = [];
}
