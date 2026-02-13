### 2026-02-12: Refined Cache Requirements — SQLite-backed, Cross-Process Shared Cache
**By:** Larry (via Coordinator)
**What:** Refined caching requirements superseding the original Dallas design. Key changes: SQLite-backed (not in-memory), cross-process shared cache for stdio MCP server instances, XDG-compliant cache location, 1GB default cap (configurable).

**Why:** Each MCP stdio invocation is a fresh process — in-memory cache is useless for the primary use case. SQLite provides safe concurrent access across multiple hlx instances (WAL mode), structured queryable metadata, and reliable cross-process sharing.

---

#### Architecture

- **Storage:** SQLite database for API metadata + disk files for downloaded artifacts (tracked by SQLite)
- **SQLite location:** `{cache_root}/hlx/cache.db`
- **Artifact directory:** `{cache_root}/hlx/artifacts/{jobId-prefix}/{workItem}/{fileName}`
- **Cache root resolution:**
  - Windows: `%LOCALAPPDATA%`
  - Linux/macOS: `$XDG_CACHE_HOME` (fallback: `~/.cache`)
- **Concurrency:** SQLite WAL mode for safe concurrent reads/writes across processes

#### TTL Matrix (per Dallas's revised design, unchanged)

| Data Type | Running Job | Completed Job |
|---|---|---|
| Job details (name, queue, creator, state) | 15s | 4h |
| Work item list (names, counts) | 15s | 4h |
| Work item details (exit code, state per item) | 15s | 4h |
| File listing (per work item) | 30s | 4h |
| Console log content | **NO CACHE** | 1h |
| Downloaded artifact (binlog, trx, files on disk) | Until eviction | Until eviction |
| Binlog scan results (FindBinlogsAsync) | 30s | 4h |

#### Console Logs — Never Cache While Running

Console logs for running work items are append-only streams. Bypass cache entirely when job has no `Finished` timestamp. For completed jobs, cache for 1 hour.

#### Disk Cache Eviction

- **Max size:** Configurable, default 1 GB
- **TTL-based cleanup:** Artifacts older than 7 days (by last access) are evicted
- **LRU eviction:** When over max size, oldest-accessed files deleted first
- **Cleanup triggers:** At startup + after every download operation

#### CLI Commands

- `hlx cache clear` — Wipe all cached data (SQLite + artifact files)
- `hlx cache status` — Show cache size, entry count, oldest/newest entries

#### Configuration

- Max cache size configurable (environment variable or config file — TBD by implementer)
- Default: 1 GB
