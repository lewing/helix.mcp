# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)
- **No tests exist yet** — test project needs to be created

## Learnings

- All three projects (Core, CLI, MCP) share `namespace HelixTool` — the MCP project forces this with `<RootNamespace>HelixTool</RootNamespace>`. This causes ambiguity.
- `HelixApi` from `Microsoft.DotNet.Helix.Client` is `new()`'d in three places: HelixService.cs (field initializer), HelixMcpTools.cs (static field), Program.cs/Commands (field initializer). No DI anywhere.
- `HelixMcpTools` uses a `private static readonly HelixService` — static state prevents injection. ModelContextProtocol SDK does support DI for tool classes.
- `HelixIdResolver` is a clean static utility with pure functions — good pattern, easily testable.
- No exception handling anywhere in the codebase. API errors propagate as raw exceptions.
- `HelixService` model records (`JobSummary`, `WorkItemResult`, `FileEntry`, `BinlogResult`) are nested inside the class — should be extracted to top-level types.
- CLI has `Spectre.Console` as a dependency but uses raw `Console.ForegroundColor` instead. Empty `Display/` folder suggests rendering was planned but not implemented.
- `MatchesPattern` in HelixService is documented as "glob" but only handles `*`, `*.ext`, and substring match.
- No `CancellationToken` support on any async method.
- `SemaphoreSlim` in `GetJobStatusAsync` throttles concurrent API calls to 10 — good pattern but semaphore isn't disposed.
- `JsonSerializerOptions { WriteIndented = true }` is allocated 4 times in HelixMcpTools.cs — should be a shared static field.
- ConsoleAppFramework v5.7.13 is used for CLI command routing. Commands class is registered via `app.Add<Commands>()`.
- MCP server uses `ModelContextProtocol.AspNetCore` v0.8.0-preview.1 with `WithHttpTransport().WithToolsFromAssembly()` pattern.

📌 Team update (2026-02-11): MatchesPattern changed to internal static; InternalsVisibleTo added to Core csproj for test access. — decided by Lambert
📌 Team update (2026-02-11): Documentation audit found 15 improvements needed — missing XML docs on public records, no install instructions, no LICENSE file. — decided by Kane

- Console logs for running Helix jobs must never be cached — they're append-only streams and any cached version is immediately stale. No TTL is short enough to be correct; skip the cache entirely.
- Completed jobs should not be cached indefinitely. 4-hour sliding expiration for in-memory metadata, 7-day last-access expiry for disk artifacts. Covers a debugging session and next-day follow-up without unbounded growth.
- Running-job metadata needs per-data-type TTLs, not a blanket value. Job details and work item lists change state (15s). File listings update as artifacts appear (30s). Console logs change continuously (no cache).
- Disk cache needs automatic eviction — manual-only `cache clear` is not a real policy. LRU with a 500MB cap plus 7-day expiry on startup covers it.
- The "log grew since last fetch" problem cannot be solved with TTL-based caching. The only correct approach is to bypass the cache for running-job logs. Range-request optimization is a future concern that depends on Helix API support.

📌 Team update (2026-02-11): Ran P0 Foundation design review. Decided: IHelixApiClient wrapper interface (6 methods), HelixService constructor injection, HelixException single exception type, CancellationToken on all async methods, ArgumentException guards, MCP tools become instance methods. Key risks: MCP SDK may not support instance tool methods (verify first), Helix SDK return types may be concrete (may need DTOs). See log/2026-02-11-design-review-p0-foundation.md for full decisions.
📌 Team update (2026-02-11): Requirements backlog formalized — 30 user stories (US-1 through US-30) including 12 from ci-analysis skill review. P0 confirmed: US-12 + US-13. — decided by Ash

📌 Session 2026-02-11-p0-implementation: D1–D10 design implemented by Ripley, tested by Lambert. All design decisions validated. Key runtime findings: TaskCanceledException needs IsCancellationRequested check (not token equality), Helix SDK types needed adapter pattern, CAF v5 DI works via static ServiceProvider property. 38 tests pass.

- **Stdio MCP transport decision (2026-02-12):** Approved Option (b) — add `hlx mcp` subcommand to the CLI binary for stdio transport, rather than modifying the separate HelixTool.Mcp HTTP project. Key insight: every primary MCP consumer (GitHub Copilot CLI, Claude Desktop, VS Code, ci-analysis skill) uses stdio, not HTTP. The `ModelContextProtocol` base package (not `.AspNetCore`) provides `WithStdioServerTransport()` via `Microsoft.Extensions.Hosting` — no ASP.NET Core / Kestrel / web stack needed. Single binary = single `dotnet tool install`. HTTP MCP project kept for remote/shared scenarios. Risk: ConsoleAppFramework + Host.CreateApplicationBuilder may conflict in the `mcp` subcommand — Ripley must verify.

📌 Session 2026-02-11-p1-features: Ripley implemented US-1 and US-20. Kane completed documentation fixes. IWorkItemDetails expanded (State, MachineName, Started, Finished), WorkItemResult updated. FormatDuration duplicated in CLI/MCP — acceptable for now per Ripley's decision. llmstxt + README are now authoritative docs (Kane decision). 38/38 tests pass.

- **US-4 Auth design (2026-02-12):** Helix SDK already has full auth plumbing — `HelixApiOptions(TokenCredential)`, `HelixApiTokenCredential(string)`, and `ApiFactory.GetAuthenticated()` pattern in arcade. Auth scheme is `Authorization: token {value}` (custom, not Bearer). Token source: `HELIX_ACCESS_TOKEN` env var — matches arcade's `HelixAccessToken` MSBuild property naming and works natively with MCP client config. Design is ~35 lines total: optional token parameter on `HelixApiClient` constructor, env var read at DI registration, 401/403 catch clauses in `HelixService` with actionable error messages. No interface changes, no new dependencies, no test impact. `IHelixApiClient` stays as the mock boundary — auth is a transport concern inside `HelixApiClient` only. Deliberately excluded: `hlx auth login`, Azure CLI integration, token refresh, keyring storage. Env var is the right abstraction for a CLI/MCP tool.

📌 Team update (2026-02-11): Ripley implemented stdio MCP — separate DI container, HelixMcpTools.cs duplicated. 55/55 tests pass. — decided by Ripley
📌 Team update (2026-02-11): Lambert's MCP test strategy — tests reference HelixTool.Mcp via ProjectReference. FormatDuration tested indirectly. Download error returns JSON. If HelixMcpTools refactored, tests need updating but mock pattern stays. — decided by Lambert

📌 Team update (2026-02-11): US-17 namespace cleanup complete — `HelixTool.Core` and `HelixTool.Mcp` now have distinct namespaces. `<RootNamespace>HelixTool</RootNamespace>` removed from Mcp csproj. All consumers need `using HelixTool.Core;`. — decided by Ripley
📌 Team update (2026-02-11): US-24 + US-30 implemented — `hlx_files` returns grouped JSON (breaking change), `hlx_status` includes `jobId`/`helixUrl`, `DownloadFromUrlAsync` uses static HttpClient (not mockable via IHelixApiClient). — decided by Ripley
📌 Team update (2026-02-11): US-29 MCP input flexibility — `TryResolveJobAndWorkItem` added to HelixIdResolver (Try-pattern, not exceptions). `workItem` now optional on `hlx_logs`, `hlx_files`, `hlx_download`. Both HelixMcpTools.cs copies updated. 81/81 tests pass. — decided by Ripley

📌 Team update (2026-02-12): Ripley removed Spectre.Console dependency and empty Commands/Display dirs (US-18). Added `--json` flag to status/files commands (US-11), matching MCP tool JSON structure per D10 convention. Resolves architecture review items 1d (empty Display/) and 4c (raw console output). — decided by Ripley


📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley



📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley


📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry


📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

- **Cache design review (2026-02-12):** Facilitated design review for SQLite-backed cross-process caching. Key decisions: (1) Decorator pattern — `CachingHelixApiClient` wrapping `IHelixApiClient`, not caching inside `HelixService`. (2) `ICacheStore` interface as the test boundary for cache behavior, separate from `IHelixApiClient` mock boundary. (3) `Microsoft.Data.Sqlite` for raw SQL over EF Core or sqlite-net-pcl — two tables don't need an ORM. (4) Three SQLite tables: `cache_metadata` (JSON blobs with TTL), `cache_artifacts` (disk file tracking with LRU), `cache_job_state` (completed flag for TTL selection). (5) WAL mode + busy_timeout=5000 for cross-process safety. (6) `PRAGMA user_version=1` for future schema migration — destructive migration acceptable for cache data. (7) Stream caching via write-to-temp-then-rename pattern. (8) `HLX_CACHE_MAX_SIZE_MB` env var for configuration (default 1024). (9) Two test tiers: unit tests mock `ICacheStore`, integration tests use `:memory:` SQLite. Wrote 11 Ripley action items (R-CACHE-1 through R-CACHE-11) and 10 Lambert action items (L-CACHE-1 through L-CACHE-10). See decisions.md for full design.

📌 Session 2026-02-12-cache-implementation: Cache implementation complete. Ripley implemented R-CACHE-1 through R-CACHE-11 (5 new files in Cache/, DI wiring, CLI commands). Lambert wrote 56 tests (L-CACHE-1 through L-CACHE-10) across 3 new test files. All 182 tests pass, build clean. Committed as d62d0d1. Previous cache strategy decisions (2025-07-14, 2025-07-18) marked superseded in decisions.md — replaced by 2026-02-12 refined requirements (SQLite-backed, 1GB cap, XDG paths).

📌 Session 2026-02-12-cache-security: Two security fixes applied to cache layer. (1) Auth context isolation — separate SQLite DB + artifacts per HELIX_ACCESS_TOKEN hash (SHA256, first 8 hex). No token → `{base}/public/`, token → `{base}/cache-{hash}/`. (2) Path traversal hardening — new `CacheSecurity.cs` with ValidatePathWithinRoot/SanitizePathSegment/SanitizeCacheKeySegment, applied to SqliteCacheStore, CachingHelixApiClient, and HelixService download methods. Lambert added 24 security tests (206 total). Committed as f8b49a3. — decided by Ripley

