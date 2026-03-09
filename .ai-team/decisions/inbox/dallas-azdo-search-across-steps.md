### 2026-03-09: azdo_search_log_across_steps design spec
**By:** Dallas
**What:** Incremental search across ALL log steps in an AzDO build, ranked by failure likelihood, with early termination.
**Why:** The existing `azdo_search_log` requires the caller to already know *which* log ID to search. In a build with 160–380 timeline records, an AI agent must make dozens of sequential tool calls to find the needle. This tool automates the scan-and-rank pattern that a human would follow: check failed steps first, skip boilerplate, stop when enough matches are found.

---

## 1. Tool Identity

| Surface | Name | Description |
|---------|------|-------------|
| MCP | `azdo_search_log_across_steps` | Search ALL log steps in an Azure DevOps build for lines matching a pattern. Automatically ranks logs by failure likelihood (failed tasks first, then tasks with issues, then large succeeded logs) and returns matches incrementally. Stops early when maxMatches is reached. Use instead of manually iterating azdo_search_log across many log IDs. |
| CLI | `hlx azdo search-log-all` | Search all build log steps for a pattern, ranked by failure priority. |

The name uses `across_steps` rather than `across_logs` because MCP consumers think in pipeline terms (stages/jobs/steps), not log IDs.

## 2. Parameters

### MCP Tool Signature

```csharp
[McpServerTool(Name = "azdo_search_log_across_steps",
               Title = "Search All AzDO Build Logs",
               ReadOnly = true,
               UseStructuredContent = true)]
public async Task<CrossStepSearchResult> SearchLogAcrossSteps(
    [Description("AzDO build ID (integer) or full AzDO build URL")]
    string buildIdOrUrl,

    [Description("Text pattern to search for (case-insensitive substring match)")]
    string pattern = "error",

    [Description("Lines of context before and after each match (default: 2)")]
    int contextLines = 2,

    [Description("Maximum total matches across all logs (default: 50). Search stops early once reached.")]
    int maxMatches = 50,

    [Description("Maximum number of individual log steps to download and search (default: 30). Limits API calls for very large builds.")]
    int maxLogsToSearch = 30,

    [Description("Minimum line count to include a log in the search (default: 5). Filters out tiny boilerplate logs.")]
    int minLogLines = 5)
```

### CLI Signature

```
hlx azdo search-log-all <buildId> [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N] [--json]
```

### Validation Rules

| Parameter | Rule | Exception |
|-----------|------|-----------|
| `pattern` | `ArgumentException.ThrowIfNullOrWhiteSpace` | `ArgumentException` |
| `contextLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| `maxMatches` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `maxLogsToSearch` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `minLogLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| env check | `HelixService.IsFileSearchDisabled` | `InvalidOperationException` / `McpException` |

No regex. Substring match only (`string.Contains(pattern, OrdinalIgnoreCase)`).

## 3. Return Types

### New types in `AzdoModels.cs` (Core layer)

```csharp
/// <summary>A log entry from the AzDO Build Logs List API (GET _apis/build/builds/{id}/logs).</summary>
public sealed record AzdoBuildLogEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("lineCount")]
    public long LineCount { get; init; }

    [JsonPropertyName("createdOn")]
    public DateTimeOffset? CreatedOn { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Matches found in a single log step during a cross-step search.</summary>
public sealed class StepSearchResult
{
    [JsonPropertyName("logId")] public int LogId { get; init; }
    [JsonPropertyName("stepName")] public string StepName { get; init; } = "";
    [JsonPropertyName("stepType")] public string? StepType { get; init; }
    [JsonPropertyName("stepResult")] public string? StepResult { get; init; }
    [JsonPropertyName("parentName")] public string? ParentName { get; init; }
    [JsonPropertyName("lineCount")] public long LineCount { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("matches")] public List<LogMatch> Matches { get; init; } = [];
}

/// <summary>Result of searching across all log steps in a build.</summary>
public sealed class CrossStepSearchResult
{
    [JsonPropertyName("build")] public string Build { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalLogsInBuild")] public int TotalLogsInBuild { get; init; }
    [JsonPropertyName("logsSearched")] public int LogsSearched { get; init; }
    [JsonPropertyName("logsSkipped")] public int LogsSkipped { get; init; }
    [JsonPropertyName("totalMatchCount")] public int TotalMatchCount { get; init; }
    [JsonPropertyName("stoppedEarly")] public bool StoppedEarly { get; init; }
    [JsonPropertyName("steps")] public List<StepSearchResult> Steps { get; init; } = [];
}
```

### New MCP result type in `McpToolResults.cs`

Not needed. `CrossStepSearchResult` already uses `[JsonPropertyName]` on all properties and `LogMatch` is already in `TextSearchHelper.cs`. The result type can live in Core because it's not reshaping — it IS the domain result. Same pattern as `TimelineSearchResult`.

## 4. Algorithm

### Phase 1: Metadata Collection (2 cheap API calls, parallelizable)

```
1. Resolve buildIdOrUrl → (org, project, buildId)
2. Parallel:
   a. GET _apis/build/builds/{buildId}/logs → List<AzdoBuildLogEntry>  (line counts, no content)
   b. GET _apis/build/builds/{buildId}/timeline → AzdoTimeline          (record states, log refs)
```

### Phase 2: Build Ranked Log Queue

Join timeline records to log entries by `record.Log.Id == logEntry.Id`:

```
For each timeline record with a log reference:
  - Lookup logEntry to get lineCount
  - Skip if lineCount < minLogLines (tiny boilerplate)
  - Assign priority bucket:
    Bucket 0: record.Result is "failed" or "canceled"
    Bucket 1: record.Issues is non-empty (warnings/errors attached)
    Bucket 2: record.Result is "succeededWithIssues"
    Bucket 3: record.Result is "succeeded" or null, lineCount >= minLogLines
  - Within each bucket: sort by lineCount descending (larger logs more likely to contain errors)
```

Orphan logs (logEntry.Id not referenced by any timeline record): appended at end (Bucket 4), sorted by lineCount desc. These are rare but possible in retried builds.

### Phase 3: Incremental Search (sequential downloads, early exit)

```
remainingMatches = maxMatches
logsSearched = 0

for each log in ranked queue (up to maxLogsToSearch):
  if remainingMatches <= 0: break (early exit)

  content = await GetBuildLogAsync(org, project, buildId, log.Id, ct)
  // Normalize line endings, split, search (reuse TextSearchHelper.SearchLines)
  searchResult = TextSearchHelper.SearchLines(
      identifier: $"log:{log.Id}",
      lines: normalizedLines,
      pattern: pattern,
      contextLines: contextLines,
      maxMatches: remainingMatches   // ← pass REMAINING, not total
  )

  if searchResult.Matches.Count > 0:
    add StepSearchResult with step metadata + matches
    remainingMatches -= searchResult.Matches.Count

  logsSearched++
```

### Normalization

Same `\r\n` → `\n`, `\r` → `\n` normalization as `SearchBuildLogAsync`. Extract into a private helper `NormalizeAndSplit(string content)` to DRY up.

## 5. Safety Guards & Limits

| Guard | Default | Rationale |
|-------|---------|-----------|
| `maxMatches` | 50 | Caps total matches across all logs. Primary context-overflow protection. |
| `maxLogsToSearch` | 30 | Caps API calls. A 380-log SDK build would need 380 HTTP requests without this. 30 covers all failures + the largest succeeded logs in typical builds. |
| `minLogLines` | 5 | Filters out ~60% of logs in a typical build (setup/teardown boilerplate with 1–4 lines). |
| No parallel downloads | — | Sequential downloads avoid hammering AzDO API. If we later add parallelism, limit to 3–5 concurrent. |
| `IsFileSearchDisabled` | env check | Same kill switch as all search tools. |
| No download size limit needed | — | `GetBuildLogAsync` already streams the response. Individual logs in AzDO builds rarely exceed 2MB. The `maxLogsToSearch` cap provides aggregate protection. |

### What about in-progress builds?

In-progress builds will have a partial timeline. The tool should work correctly — it just searches whatever logs exist. The timeline fetch is not cached for in-progress builds (established caching rule), so re-running the tool shows fresh results.

## 6. Relationship to `azdo_search_log`

**Complement, not replace.**

| Tool | Use case |
|------|----------|
| `azdo_search_log` | "Search log 47 for 'OutOfMemory'" — caller already knows the log ID (from timeline, from a previous search, from a URL). Fast, single API call. |
| `azdo_search_log_across_steps` | "Find 'error CS' anywhere in this build" — caller doesn't know which log(s) contain the pattern. Automated ranking + early exit. |

The `azdo_search_log_across_steps` description should mention `azdo_search_log` for targeted follow-up: "For targeted search of a specific log step, use azdo_search_log instead."

`azdo_search_log` remains the right tool when the caller has a specific log ID (common after `azdo_timeline`). The new tool is for the "I don't know where to look" workflow.

## 7. Interface & Client Changes

### `IAzdoApiClient` — new method

```csharp
/// <summary>List all build logs with metadata (line counts) without downloading content.</summary>
Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default);
```

### `AzdoApiClient` — implementation

```csharp
public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default)
{
    var url = BuildUrl(org, project, $"build/builds/{buildId}/logs");
    return await GetListAsync<AzdoBuildLogEntry>(url, ct);
}
```

### `CachingAzdoApiClient` — caching wrapper

Cache with same dynamic TTL rules (completed build → 4h, in-progress → 15s). The logs list is immutable once a build completes.

### `AzdoService` — new method

```csharp
public async Task<CrossStepSearchResult> SearchBuildLogAcrossStepsAsync(
    string buildIdOrUrl, string pattern,
    int contextLines = 2, int maxMatches = 50,
    int maxLogsToSearch = 30, int minLogLines = 5,
    CancellationToken ct = default)
```

This is where the ranking algorithm, incremental search, and early termination live. Follows existing pattern: MCP tool is thin wrapper, business logic in `AzdoService`.

## 8. Estimated Test Surface for Lambert

### Unit Tests (AzdoService layer)

| ID | Test | Notes |
|----|------|-------|
| T-1 | Empty build (no logs, no timeline) | Returns 0 matches, logsSearched=0 |
| T-2 | All logs below minLogLines | Returns 0 matches, all skipped |
| T-3 | Single failed log with matches | Bucket 0 prioritization, correct StepSearchResult |
| T-4 | Ranking order: failed → issues → succeededWithIssues → succeeded | Verify download order via mock call sequence |
| T-5 | Early termination at maxMatches | Set maxMatches=3, provide 5 matches across 2 logs, verify stoppedEarly=true and exactly 3 matches |
| T-6 | maxLogsToSearch limit | 50 eligible logs, maxLogsToSearch=5, verify only 5 downloaded |
| T-7 | Orphan logs (no timeline record) | Log in logs list but not in timeline → Bucket 4 |
| T-8 | Pattern not found in any log | Returns 0 matches, stoppedEarly=false |
| T-9 | Timeline record with no log reference | Skipped (no log to download) |
| T-10 | Context lines propagation | Verify contextLines flows to TextSearchHelper |
| T-11 | Line ending normalization | `\r\n` and `\r` normalized before search |

### Validation Tests

| ID | Test | Notes |
|----|------|-------|
| V-1 | Null/empty pattern | ArgumentException |
| V-2 | Negative contextLines | ArgumentOutOfRangeException |
| V-3 | Zero maxMatches | ArgumentOutOfRangeException |
| V-4 | Zero maxLogsToSearch | ArgumentOutOfRangeException |
| V-5 | Negative minLogLines | ArgumentOutOfRangeException |
| V-6 | IsFileSearchDisabled=true | InvalidOperationException |

### MCP Tool Tests

| ID | Test | Notes |
|----|------|-------|
| M-1 | Successful search returns CrossStepSearchResult | UseStructuredContent=true, verify JSON shape |
| M-2 | IsFileSearchDisabled → McpException | Not InvalidOperationException |
| M-3 | Service throws InvalidOperationException → McpException | Exception remapping |
| M-4 | Service throws HttpRequestException → McpException | Exception remapping |
| M-5 | Service throws ArgumentException → McpException | Exception remapping |

### Integration-level Tests (IAzdoApiClient mock)

| ID | Test | Notes |
|----|------|-------|
| I-1 | GetBuildLogsListAsync returns correct AzdoBuildLogEntry list | Deserialization from AzDO format |
| I-2 | Caching: completed build logs list cached at 4h TTL | CachingAzdoApiClient test |
| I-3 | Caching: in-progress build logs list cached at 15s TTL | Dynamic TTL |

**Estimated total: ~19 tests.** Aligns with the ~700 existing test count.

## 9. Implementation Notes

### Extract `NormalizeAndSplit`

Both `SearchBuildLogAsync` and `SearchBuildLogAcrossStepsAsync` need the same line normalization. Extract to a private static method:

```csharp
private static string[] NormalizeAndSplit(string content)
{
    var normalized = content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
    var lines = normalized.Split('\n');
    if (normalized.EndsWith("\n", StringComparison.Ordinal) && lines.Length > 0)
        Array.Resize(ref lines, lines.Length - 1);
    return lines;
}
```

`SearchBuildLogAsync` should be updated to use this helper (minor refactor, not breaking).

### Timeline + Logs List Join

The join is by `timelineRecord.Log.Id == logEntry.Id`. Timeline records of type "Stage" and "Phase" rarely have logs, but if they do, include them. Records with `Log == null` are skipped (no downloadable log).

### Parallel Metadata Fetch

The timeline and logs list are independent API calls. Use `Task.WhenAll` to fetch both concurrently:

```csharp
var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
var logsListTask = _client.GetBuildLogsListAsync(org, project, buildId, ct);
await Task.WhenAll(timelineTask, logsListTask);
```

### Future Optimization: Parallel Log Downloads

Deferred. Sequential downloads are simpler and sufficient for Phase 1. If performance is a problem (unlikely with `maxLogsToSearch=30`), add a `SemaphoreSlim(3)` bounded parallel download later.
