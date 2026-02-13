### 2026-02-12: Cache Implementation Design Review
**By:** Dallas
**What:** SQLite-backed cross-process caching layer for hlx — interface design, integration strategy, schema, risk assessment, and action items for Ripley (implementation) and Lambert (tests).
**Why:** Each MCP stdio invocation (`hlx mcp`) is a fresh process. In-memory caching is useless for the primary use case. SQLite provides safe concurrent access across multiple hlx instances (WAL mode), structured queryable metadata, and reliable cross-process sharing. This design review translates Larry's refined requirements into concrete interfaces, classes, and tasks.

---

## 1. Architectural Decision: Decorator on IHelixApiClient

**Decision: Decorator pattern — `CachingHelixApiClient` wrapping `IHelixApiClient`.**

Rejected alternative: caching inside `HelixService`. Reasons:
- `HelixService` is already 600 lines. Adding cache logic doubles it.
- Cache concerns (TTL, eviction, SQLite) are orthogonal to business logic (failure classification, log search, batch status).
- The decorator is invisible to `HelixService` — it sees the same `IHelixApiClient` interface it already depends on.
- Testing: Lambert can test cache behavior by wrapping a mock `IHelixApiClient` with `CachingHelixApiClient`, without touching `HelixService` tests.

**The decorator intercepts 6 `IHelixApiClient` methods with cache-aware logic:**

| Method | Cache Strategy |
|---|---|
| `GetJobDetailsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `ListWorkItemsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `GetWorkItemDetailsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `ListWorkItemFilesAsync` | Cache. Running: 30s TTL. Completed: 4h TTL. |
| `GetConsoleLogAsync` | **Running: NO CACHE.** Completed: 1h TTL. Returns Stream — cache to disk, serve from disk. |
| `GetFileAsync` | Cache to disk. Eviction-based only (no TTL). LRU with 1GB cap. |

**Job state determination:** To apply the correct TTL, the decorator must know if a job is running or completed. Strategy: before checking the TTL for any sub-resource, the decorator checks its own SQLite cache for that job's `Finished` timestamp. If not cached, it calls the inner client's `GetJobDetailsAsync` (which is itself cached). This is a single extra call at most, amortized across all subsequent lookups for that job.

---

## 2. New Types and File Layout

All new code lives in `HelixTool.Core` (namespace `HelixTool.Core`).

### New Files

| File | Type | Purpose |
|---|---|---|
| `Cache/ICacheStore.cs` | Interface | Abstract cache storage — enables testing without real SQLite |
| `Cache/SqliteCacheStore.cs` | Class | SQLite + disk file implementation of `ICacheStore` |
| `Cache/CachingHelixApiClient.cs` | Class | Decorator implementing `IHelixApiClient`, delegates to inner client + `ICacheStore` |
| `Cache/CacheOptions.cs` | Record | Configuration: max size, cache root, TTLs |
| `Cache/CacheStatus.cs` | Record | Return type for `hlx cache status` |

### Interface: `ICacheStore`

```csharp
namespace HelixTool.Core;

/// <summary>
/// Abstract cache storage for API metadata and artifact files.
/// Implementations handle persistence (SQLite+disk) and eviction.
/// </summary>
public interface ICacheStore : IDisposable
{
    // Metadata (JSON-serialized API responses)
    Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default);
    Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default);

    // Artifact files (console logs, binlogs, downloaded files)
    Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default);
    Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default);

    // Job state cache (needed for TTL decisions)
    Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default);
    Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default);

    // Management
    Task ClearAsync(CancellationToken ct = default);
    Task<CacheStatus> GetStatusAsync(CancellationToken ct = default);
    Task EvictExpiredAsync(CancellationToken ct = default);
}

public record CacheStatus(
    long TotalSizeBytes,
    int MetadataEntryCount,
    int ArtifactFileCount,
    DateTimeOffset? OldestEntry,
    DateTimeOffset? NewestEntry,
    long MaxSizeBytes);
```

### Class: `CacheOptions`

```csharp
namespace HelixTool.Core;

public record CacheOptions
{
    /// <summary>Maximum cache size in bytes. Default: 1 GB.</summary>
    public long MaxSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Cache root directory. Default: platform-appropriate XDG path.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>Artifact expiry (last access). Default: 7 days.</summary>
    public TimeSpan ArtifactMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Resolve the actual cache root, respecting XDG conventions.</summary>
    public string GetEffectiveCacheRoot()
    {
        if (!string.IsNullOrEmpty(CacheRoot)) return CacheRoot;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hlx");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(!string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "hlx");
    }
}
```

### Class: `CachingHelixApiClient`

```csharp
namespace HelixTool.Core;

/// <summary>
/// Decorator that adds SQLite-backed caching to any IHelixApiClient.
/// Injected between DI registration and HelixService.
/// </summary>
public sealed class CachingHelixApiClient : IHelixApiClient
{
    private readonly IHelixApiClient _inner;
    private readonly ICacheStore _cache;

    public CachingHelixApiClient(IHelixApiClient inner, ICacheStore cache) { ... }

    // Each method: check cache → return if hit → call inner → store → return
    // TTL selection based on IsJobCompletedAsync result
}
```

---

## 3. DI Integration — Program.cs Changes

Current registration (CLI):
```csharp
services.AddSingleton<IHelixApiClient>(_ => new HelixApiClient(...));
services.AddSingleton<HelixService>();
```

New registration:
```csharp
services.AddSingleton<CacheOptions>(_ =>
{
    var opts = new CacheOptions();
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
    return opts;
});
services.AddSingleton<ICacheStore>(sp =>
{
    var opts = sp.GetRequiredService<CacheOptions>();
    return new SqliteCacheStore(opts);
});
services.AddSingleton<HelixApiClient>(_ => new HelixApiClient(...));
services.AddSingleton<IHelixApiClient>(sp =>
    new CachingHelixApiClient(
        sp.GetRequiredService<HelixApiClient>(),
        sp.GetRequiredService<ICacheStore>()));
services.AddSingleton<HelixService>();
```

**Same pattern applies to the `mcp` command's DI container.** The `HelixTool.Mcp` HTTP project gets the same registration if desired, but it's lower priority since it's long-lived (in-memory cache matters more there).

**`cache clear` and `cache status` commands** access `ICacheStore` directly from DI:
```csharp
[Command("cache clear")]
public async Task CacheClear()
{
    var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
    await cache.ClearAsync();
    Console.WriteLine("Cache cleared.");
}

[Command("cache status")]
public async Task CacheStatus()
{
    var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
    var status = await cache.GetStatusAsync();
    // Format and print
}
```

**Note on ConsoleAppFramework:** CAF v5 supports nested command groups. `hlx cache clear` and `hlx cache status` may need to be registered as `app.Add("cache", ...)` or as a separate `CacheCommands` class. Ripley should verify the CAF subcommand routing pattern.

---

## 4. SQLite Schema

### Package Choice: `Microsoft.Data.Sqlite`

**Decision: `Microsoft.Data.Sqlite` (Microsoft's ADO.NET provider).**

Rejected:
- `sqlite-net-pcl` — ORM-ish, auto-creates tables from C# classes. Convenient but hides SQL, harder to control schema precisely, no built-in migration story.
- `EF Core SQLite` — massive dependency for what's essentially two tables. EF migrations are overkill.
- `Microsoft.Data.Sqlite` — lightweight (~200KB), raw SQL, explicit schema control, first-party Microsoft package. We need two tables and a few indexes. This is the right tool.

### Tables

```sql
-- API metadata cache (JSON blobs)
CREATE TABLE IF NOT EXISTS cache_metadata (
    cache_key   TEXT PRIMARY KEY,
    json_value  TEXT NOT NULL,
    created_at  TEXT NOT NULL,  -- ISO 8601
    expires_at  TEXT NOT NULL,  -- ISO 8601
    job_id      TEXT NOT NULL   -- for join/cleanup queries
);

CREATE INDEX IF NOT EXISTS idx_metadata_expires ON cache_metadata(expires_at);
CREATE INDEX IF NOT EXISTS idx_metadata_job ON cache_metadata(job_id);

-- Downloaded artifact files (tracked by SQLite, stored on disk)
CREATE TABLE IF NOT EXISTS cache_artifacts (
    cache_key       TEXT PRIMARY KEY,
    file_path       TEXT NOT NULL,    -- relative to artifacts dir
    file_size       INTEGER NOT NULL, -- bytes
    created_at      TEXT NOT NULL,    -- ISO 8601
    last_accessed   TEXT NOT NULL,    -- ISO 8601, updated on read
    job_id          TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_artifacts_accessed ON cache_artifacts(last_accessed);
CREATE INDEX IF NOT EXISTS idx_artifacts_job ON cache_artifacts(job_id);

-- Job completion state (for TTL decisions)
CREATE TABLE IF NOT EXISTS cache_job_state (
    job_id       TEXT PRIMARY KEY,
    is_completed INTEGER NOT NULL,  -- 0 or 1
    finished_at  TEXT,              -- ISO 8601, null if running
    cached_at    TEXT NOT NULL,     -- ISO 8601
    expires_at   TEXT NOT NULL      -- ISO 8601
);
```

### Cache Key Format

| Data Type | Cache Key Pattern |
|---|---|
| Job details | `job:{jobId}:details` |
| Work item list | `job:{jobId}:workitems` |
| Work item details | `job:{jobId}:wi:{workItem}:details` |
| File listing | `job:{jobId}:wi:{workItem}:files` |
| Console log | `job:{jobId}:wi:{workItem}:console` |
| Downloaded file | `job:{jobId}:wi:{workItem}:file:{fileName}` |

### Artifact File Path on Disk

`{cache_root}/hlx/artifacts/{jobId[0:8]}/{workItem}/{fileName}`

Using first 8 chars of jobId as directory prefix keeps the directory tree shallow and avoids filesystem limits.

### WAL Mode and Concurrency

```csharp
// On connection open:
connection.Execute("PRAGMA journal_mode=WAL;");
connection.Execute("PRAGMA busy_timeout=5000;");  // 5s retry on lock
```

WAL mode allows concurrent reads across processes. Writers queue with busy_timeout. This is safe for our use case: multiple `hlx mcp` processes reading simultaneously, occasional writes when cache misses occur.

---

## 5. Stream Caching Strategy

`GetConsoleLogAsync` and `GetFileAsync` return `Stream`. These need special handling because:
1. We can't serialize a `Stream` to SQLite — we need to write to disk.
2. The caller expects a `Stream` back, not a file path.

**Strategy for cached stream responses:**
1. On cache miss: call inner client, tee the stream to a temp file, return a `FileStream` from the temp file, then move the temp file to the artifact cache location.
2. On cache hit: return `File.OpenRead(cachedFilePath)`.
3. Update `last_accessed` in `cache_artifacts` on every cache hit.

**Implementation detail:** Use a write-then-rename pattern to avoid partial files on crash:
```csharp
var tempPath = artifactPath + ".tmp";
using (var fs = File.Create(tempPath))
    await sourceStream.CopyToAsync(fs, ct);
File.Move(tempPath, artifactPath, overwrite: true);
```

---

## 6. Eviction Implementation

Eviction runs at two points:
1. **Startup** — `SqliteCacheStore` constructor calls `EvictExpiredAsync()` synchronously (or fires-and-forgets with a small delay). Removes expired metadata entries and artifact files older than 7 days.
2. **After every artifact write** — Check total artifact size. If over cap, delete LRU entries until under cap.

```sql
-- Remove expired metadata
DELETE FROM cache_metadata WHERE expires_at < @now;

-- Remove expired artifacts (7-day last-access)
SELECT cache_key, file_path FROM cache_artifacts WHERE last_accessed < @cutoff;
-- Then delete files + rows

-- LRU eviction when over cap
SELECT cache_key, file_path, file_size FROM cache_artifacts ORDER BY last_accessed ASC;
-- Iterate, summing sizes, delete until total <= max
```

After deleting artifact rows, also delete the corresponding disk files. Handle `FileNotFoundException` gracefully (file may have been deleted by another process or manually).

---

## 7. Configuration

**Decision:** Environment variable `HLX_CACHE_MAX_SIZE_MB` (integer, megabytes).

Rejected config file approach — adds complexity for one setting. Environment variables work in all contexts (shell, MCP client config, CI).

Default: `1024` (1 GB per Larry's refined requirements — note: original Dallas design had 500 MB, Larry bumped to 1 GB).

To disable caching entirely: `HLX_CACHE_MAX_SIZE_MB=0` → `CachingHelixApiClient` passes through to inner client without caching.

---

## 8. Risk Assessment

### R1: SQLite Locking Under Heavy Concurrent Access
**Severity:** Medium
**Detail:** Multiple `hlx mcp` processes writing simultaneously could hit SQLITE_BUSY despite WAL mode. Mitigated by `busy_timeout=5000` (5s retry), but under extreme load (10+ concurrent MCP instances all cache-missing on the same job), contention is possible.
**Mitigation:** WAL + busy_timeout is the standard solution. If problems appear in practice, consider connection pooling or write-serialization via a named mutex. Monitor for now.

### R2: Schema Migration
**Severity:** Low (but important to plan for)
**Detail:** Once users have `cache.db` files, we can't casually change the schema. First release must get the schema right.
**Mitigation:** Add a `PRAGMA user_version` check on startup. Current version = 1. If the version doesn't match, drop all tables and recreate (destructive migration is acceptable for a cache — it's all regenerable data).

### R3: Stale "Running" Classification
**Severity:** Low
**Detail:** A job's `is_completed` status is cached. If a job completes while the "running" cache entry is still live (15s TTL), we serve shorter-TTL data for a few extra seconds. This is harmless — worst case we re-fetch data that just became cacheable for longer.
**Mitigation:** None needed. 15s staleness on job state is acceptable.

### R4: Disk Space Accounting Accuracy
**Severity:** Low
**Detail:** `file_size` in `cache_artifacts` is set at write time. If a file is modified externally (shouldn't happen, but possible), the accounting drifts.
**Mitigation:** `cache clear` resets everything. `cache status` could optionally do a fresh disk scan for accurate reporting. Periodic re-scan is overkill.

### R5: Testing Without Real SQLite
**Severity:** Medium (affects Lambert)
**Detail:** `ICacheStore` is the mock boundary for Lambert. Tests should NOT require a real SQLite database. However, `SqliteCacheStore` itself needs integration tests with a real (in-memory or temp-file) SQLite database.
**Mitigation:** Two test tiers:
- **Unit tests:** Mock `ICacheStore` with NSubstitute. Test `CachingHelixApiClient` logic (TTL selection, bypass for running logs, cache hit/miss flow).
- **Integration tests:** Real `SqliteCacheStore` with `:memory:` connection string or temp file. Test schema creation, eviction, concurrent access.

### R6: File Descriptor Leaks on Cached Streams
**Severity:** Medium
**Detail:** `GetArtifactAsync` returns a `FileStream`. If the caller doesn't dispose it, file handles leak. But this is the same contract as the inner client — callers already `await using` the streams.
**Mitigation:** Ensure all returned streams are documented as disposable. No code change needed — existing callers already use `await using`.

---

## 9. Action Items

### For Ripley (Implementation)

| ID | Task | Depends On | Notes |
|---|---|---|---|
| R-CACHE-1 | Add `Microsoft.Data.Sqlite` to `HelixTool.Core.csproj` | — | Version: latest stable |
| R-CACHE-2 | Create `Cache/ICacheStore.cs` | — | Interface per §2 |
| R-CACHE-3 | Create `Cache/CacheOptions.cs` | — | Record per §2, XDG path resolution |
| R-CACHE-4 | Create `Cache/CacheStatus.cs` | — | Record per §2 |
| R-CACHE-5 | Create `Cache/SqliteCacheStore.cs` | R-CACHE-2, R-CACHE-3 | Schema per §4, WAL mode, eviction per §6, `PRAGMA user_version=1` |
| R-CACHE-6 | Create `Cache/CachingHelixApiClient.cs` | R-CACHE-2 | Decorator per §2, TTL matrix per §1, stream caching per §5 |
| R-CACHE-7 | Update CLI `Program.cs` DI | R-CACHE-5, R-CACHE-6 | Registration per §3, both CLI container and `mcp` command container |
| R-CACHE-8 | Add `cache clear` command | R-CACHE-2 | Calls `ICacheStore.ClearAsync()` |
| R-CACHE-9 | Add `cache status` command | R-CACHE-2, R-CACHE-4 | Calls `ICacheStore.GetStatusAsync()`, format output |
| R-CACHE-10 | Update `llmstxt` | R-CACHE-8, R-CACHE-9 | Document new cache commands |
| R-CACHE-11 | Verify CAF subcommand routing | — | `hlx cache clear` / `hlx cache status` — test that CAF supports this or find workaround |

### For Lambert (Tests)

| ID | Task | Depends On | Notes |
|---|---|---|---|
| L-CACHE-1 | Unit tests: `CachingHelixApiClient` cache hit | R-CACHE-6 | Mock `ICacheStore`, verify inner client NOT called on cache hit |
| L-CACHE-2 | Unit tests: `CachingHelixApiClient` cache miss | R-CACHE-6 | Mock `ICacheStore` returning null, verify inner client called, result stored |
| L-CACHE-3 | Unit tests: TTL selection (running vs completed) | R-CACHE-6 | Mock `IsJobCompletedAsync` returning true/false, verify correct TTL passed to `SetMetadataAsync` |
| L-CACHE-4 | Unit tests: Console log bypass for running jobs | R-CACHE-6 | When `IsJobCompletedAsync` returns false, `GetConsoleLogAsync` must NOT cache |
| L-CACHE-5 | Unit tests: Console log cached for completed jobs | R-CACHE-6 | When `IsJobCompletedAsync` returns true, `GetConsoleLogAsync` must cache with 1h TTL |
| L-CACHE-6 | Integration tests: `SqliteCacheStore` CRUD | R-CACHE-5 | Use `:memory:` SQLite. Test set/get/evict/clear/status |
| L-CACHE-7 | Integration tests: Eviction (TTL + LRU) | R-CACHE-5 | Set entries with past expiry, verify eviction. Set entries over max size, verify LRU deletion |
| L-CACHE-8 | Integration tests: Schema creation idempotent | R-CACHE-5 | Open store twice on same DB file, verify no errors |
| L-CACHE-9 | Unit tests: `CacheOptions.GetEffectiveCacheRoot()` | R-CACHE-3 | Test Windows path, test XDG override, test default fallback |
| L-CACHE-10 | Unit tests: Cache disabled when max size = 0 | R-CACHE-6, R-CACHE-7 | `HLX_CACHE_MAX_SIZE_MB=0` → `CachingHelixApiClient` passes through |

---

## 10. Sequencing Recommendation

1. **R-CACHE-1 through R-CACHE-4** — Types and interfaces (no behavior yet). Lambert can start writing test skeletons against `ICacheStore`.
2. **R-CACHE-5** — `SqliteCacheStore` implementation. Lambert writes integration tests (L-CACHE-6 through L-CACHE-8).
3. **R-CACHE-6** — `CachingHelixApiClient` decorator. Lambert writes unit tests (L-CACHE-1 through L-CACHE-5).
4. **R-CACHE-7** — DI wiring. Smoke-test manually.
5. **R-CACHE-8, R-CACHE-9** — CLI commands. Lambert adds L-CACHE-9, L-CACHE-10.
6. **R-CACHE-10, R-CACHE-11** — Documentation and CAF verification.

Ripley should target the interfaces first so Lambert can write tests in parallel.

---

## 11. Open Questions (for Larry)

1. **Should HelixTool.Mcp (HTTP project) also get caching?** It's long-lived so in-memory caching matters more there, but SQLite would give persistence across restarts. Recommendation: yes, same DI registration, low incremental effort.
2. **Should `cache status` show per-job breakdown?** Or just totals? Recommendation: totals only for v1, per-job in v2 if useful.
3. **Should cache be opt-out?** Currently always on. `HLX_CACHE_MAX_SIZE_MB=0` disables, but should there be `--no-cache` flag on individual commands? Recommendation: defer to v2 unless MCP consumers need it.
