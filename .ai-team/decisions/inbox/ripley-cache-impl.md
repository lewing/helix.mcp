### 2026-02-12: Cache Implementation Details
**By:** Ripley
**What:** SQLite-backed caching layer implemented per Dallas's design review (R-CACHE-1 through R-CACHE-11).

**Implementation decisions:**

1. **Microsoft.Data.Sqlite v9.0.7** — latest stable at time of implementation.

2. **CachingHelixApiClient DTO round-tripping** — Private record types (`JobDetailsDto`, `WorkItemSummaryDto`, `WorkItemDetailsDto`, `WorkItemFileDto`) implement the projection interfaces (`IJobDetails`, etc.) directly. This avoids needing a separate deserialization step — the DTOs are both the serialization format and the return type.

3. **Artifact path strategy** — Instead of nested `{jobId[0:8]}/{workItem}/{fileName}`, I used `{jobId[0:8]}/{sanitized_cache_key}` where colons/slashes in the cache key are replaced with underscores. This is simpler and avoids ambiguity when work item names contain path-unsafe characters.

4. **ConsoleAppFramework subcommand routing** — CAF v5 supports `[Command("cache clear")]` and `[Command("cache status")]` directly. No special `app.Add("cache", ...)` registration needed — verified by successful build (R-CACHE-11).

5. **Schema migration strategy** — `PRAGMA user_version=1`. On mismatch, all tables are dropped and recreated (destructive migration is acceptable for cache data). This handles future schema changes cleanly.

6. **Startup eviction** — `EvictExpiredAsync()` is fire-and-forget via `Task.Run()` in the constructor. This avoids blocking startup while still cleaning up stale data.

**For Lambert:** All new types are in `HelixTool.Core` namespace. `ICacheStore` is the mock boundary for unit tests. `SqliteCacheStore` can be tested with `:memory:` connection string or temp file for integration tests. The `CachingHelixApiClient` constructor takes `(IHelixApiClient inner, ICacheStore cache, CacheOptions options)`.

**Files created:**
- `src/HelixTool.Core/Cache/ICacheStore.cs`
- `src/HelixTool.Core/Cache/CacheOptions.cs`
- `src/HelixTool.Core/Cache/CacheStatus.cs`
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs`
- `src/HelixTool.Core/Cache/CachingHelixApiClient.cs`

**Files modified:**
- `src/HelixTool.Core/HelixTool.Core.csproj` (added Microsoft.Data.Sqlite)
- `src/HelixTool/Program.cs` (DI, cache commands, llmstxt update)
