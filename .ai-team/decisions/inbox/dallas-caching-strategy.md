### 2025-07-14: Caching Strategy for Helix API Responses and Artifacts

**By:** Dallas
**What:** A two-tier caching system in `HelixTool.Core` — an in-memory LRU cache for API metadata and a disk cache for downloaded artifacts, keyed by `{jobId}/{workItem}/{fileName}`, with job-completion-aware invalidation.
**Why:** Helix API calls are slow (especially when fanning out across work items), and downloaded artifacts (binlogs, TRX files) are large and immutable once a job completes. Caching avoids redundant network I/O in both the short-lived CLI (repeated commands during a debugging session) and the long-lived MCP server (same job queried multiple times by an agent). The design below is opinionated, concrete, and implementable.

---

## Design

### 1. What Gets Cached

| Data | Tier | Rationale |
|------|------|-----------|
| Job details (`DetailsAsync`) | Memory | Small JSON. Queried on every command. |
| Work item list (`ListAsync`) | Memory | Small. Needed by `status`, `find-binlogs`, `download`. |
| Work item details (`DetailsAsync`) | Memory | Small. Exit codes, per-item. |
| File listings (`ListFilesAsync`) | Memory | Small. Needed before any download. |
| Console log content | Disk | Can be large (multi-MB). Text, immutable post-completion. |
| Downloaded artifacts (binlogs, trx) | Disk | Large (50-200MB). Binary. Immutable post-completion. |

### 2. Cache Invalidation — Job State Awareness

This is the crux. The rule is simple:

- **Completed job** (`Finished != null`): Cache everything indefinitely. Data is immutable.
- **Running job** (`Finished == null`): Cache with a short TTL (60 seconds for metadata, no caching for downloads).

The first API call for any job ID is always `Job.DetailsAsync`. The cache checks `Finished` on that response. If the job is complete, all subsequent data for that job is cached permanently (within TTL/eviction bounds). If it's running, metadata gets a 60-second sliding window so repeated calls within a debugging session don't hammer the API, but stale data doesn't linger.

No manual invalidation. No `--no-cache` flag (for now). If you need fresh data for a running job, wait 60 seconds. This is simple and correct.

### 3. Cache Location

**Disk cache root:** `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "hlx", "cache")`

On Windows: `%LOCALAPPDATA%\hlx\cache\`
On Linux/macOS: `~/.local/share/hlx/cache/`

Not temp dir — temp gets cleaned by the OS unpredictably. Not configurable (for now) — YAGNI. If someone needs to configure it, we add `HLX_CACHE_DIR` env var later.

Structure on disk:
```
cache/
├── meta/                          # Serialized API responses (JSON)
│   └── {jobId}/
│       ├── job.json               # Job details
│       ├── workitems.json         # Work item list
│       ├── workitems/
│       │   └── {workItemName}.json  # Work item details
│       └── files/
│           └── {workItemName}.json  # File listings
├── logs/                          # Console log text files
│   └── {jobId}/
│       └── {workItemName}.txt
└── artifacts/                     # Downloaded binlogs, trx, etc.
    └── {jobId}/
        └── {workItemName}/
            └── {fileName}
```

Work item names need sanitization (replace `/`, `\`, and other path-unsafe chars with `_`).

### 4. Cache Size Management

- **Memory tier:** Fixed-capacity LRU, 500 entries max. Each entry is a small JSON object. This caps memory at ~50MB worst case (generous). No TTL eviction for completed jobs; 60s TTL for running jobs.
- **Disk tier:** No automatic eviction on write. Instead:
  - A `hlx cache clear` CLI command to nuke the entire cache.
  - A `hlx cache clear --older-than 7d` variant to prune by age.
  - Log a warning if the cache directory exceeds 1GB (check on startup of MCP server only — CLI is too short-lived to bother).
  - Future: LRU eviction based on access time. Not worth building now.

Rationale: Automatic eviction of large files is fraught. Users downloading binlogs are doing it deliberately. Let them manage disk space explicitly. The `cache clear` command is cheap to implement and sufficient.

### 5. Cache Key Design

Keys are hierarchical, not hashes. Readable, debuggable, `ls`-able:

- **Memory:** `CacheKey` record: `(string JobId, CacheCategory Category, string? WorkItem, string? FileName)`
- **Disk:** Path-based as shown above.

`CacheCategory` enum: `JobDetails`, `WorkItemList`, `WorkItemDetails`, `FileList`, `ConsoleLog`, `Artifact`.

No URL hashing. Job IDs are GUIDs. Work item names are short strings. This is human-readable and debuggable — you can `ls` the cache directory and understand what's there.

### 6. Interface & Class Design

```csharp
// In HelixTool.Core

/// <summary>
/// Caches Helix API responses and downloaded artifacts.
/// Thread-safe for use in long-lived MCP server.
/// </summary>
public sealed class HelixCache : IDisposable
{
    private readonly string _diskRoot;
    private readonly ConcurrentDictionary<string, CacheEntry> _memory = new();
    private readonly SemaphoreSlim _diskLock = new(1, 1);

    public HelixCache(string? cacheRoot = null);

    // Memory-tier: API metadata
    public T? GetMetadata<T>(string jobId, CacheCategory category,
        string? workItem = null) where T : class;
    public void SetMetadata<T>(string jobId, CacheCategory category,
        T value, bool isJobCompleted, string? workItem = null) where T : class;

    // Disk-tier: large content
    public string? GetArtifactPath(string jobId, string workItem, string fileName);
    public string StoreArtifact(string jobId, string workItem, string fileName,
        Stream content);
    public string? GetConsoleLog(string jobId, string workItem);
    public string StoreConsoleLog(string jobId, string workItem, string content);

    // Management
    public long GetCacheSizeBytes();
    public void Clear(TimeSpan? olderThan = null);

    public void Dispose();
}

public enum CacheCategory
{
    JobDetails,
    WorkItemList,
    WorkItemDetails,
    FileList
}

internal record CacheEntry(object Value, DateTimeOffset ExpiresAt, bool Immutable);
```

### 7. Integration with HelixService

`HelixService` gets a constructor parameter:

```csharp
public class HelixService
{
    private readonly HelixApi _api = new();
    private readonly HelixCache? _cache;

    public HelixService(HelixCache? cache = null)
    {
        _cache = cache;
    }
}
```

The `cache` parameter is optional and nullable. Every method follows this pattern:

```csharp
public async Task<JobSummary> GetJobStatusAsync(string jobId)
{
    var id = HelixIdResolver.ResolveJobId(jobId);

    // Check cache
    var cached = _cache?.GetMetadata<JobSummary>(id, CacheCategory.JobDetails);
    if (cached != null) return cached;

    // Fetch from API
    var job = await _api.Job.DetailsAsync(id);
    // ... existing logic ...

    var summary = new JobSummary(...);

    // Store in cache
    _cache?.SetMetadata(id, CacheCategory.JobDetails, summary,
        isJobCompleted: job.Finished != null);

    return summary;
}
```

This is minimally invasive. No interface extraction needed. No DI container. The cache is an optional collaborator. CLI and MCP can both pass one in or not.

### 8. CLI vs MCP Differences

- **CLI:** Create a single `HelixCache` instance in `Commands` constructor. The disk cache persists across CLI invocations (that's the whole point — `hlx status <job>` then `hlx files <job> <wi>` reuses the cached job details). Memory cache is per-process, so CLI only benefits from disk cache across invocations.
- **MCP server:** Create a single `HelixCache` instance in DI (singleton). Both memory and disk caches stay warm. The memory cache is the primary win here — the MCP server handles many requests for the same job in a session.

Both share the same `HelixCache` class. No separate implementations.

### 9. Implementation Order

1. `HelixCache` class with memory tier only (metadata caching).
2. Add `HelixCache?` parameter to `HelixService`, wire cache checks into `GetJobStatusAsync` and `GetWorkItemFilesAsync`.
3. Add disk tier for console logs and artifacts.
4. Add `hlx cache clear` CLI command.
5. Wire into MCP server.

Steps 1-2 are the highest-value, lowest-risk change. Console logs and artifacts are fetched less frequently and the disk I/O adds complexity. Start with the metadata cache.

### 10. What I'm NOT Doing

- **No `ICache` interface.** We have one implementation. If we need a second (Redis? Distributed?), we extract then. Not now.
- **No configuration system.** No `appsettings.json`, no env vars for TTL. Hardcode the 60-second TTL for running jobs. Change it when someone complains.
- **No cache warming.** Don't prefetch anything.
- **No ETag/conditional GET.** The Helix API doesn't support it anyway.
- **No compression.** Disk is cheap. Compression adds CPU cost and complexity.
