# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Summarized History (through 2026-02-11)

**Architecture & DI (P0):** Implemented IHelixApiClient interface with projection interfaces for Helix SDK types, HelixApiClient wrapper, HelixException, constructor injection on HelixService, CancellationToken on all methods, input validation (D1-D10). DI for CLI via `ConsoleApp.ServiceProvider`, for MCP via `builder.Services.AddSingleton<>()`.

**Key patterns established:**
- Helix SDK types are concrete — mockable via projection interfaces (IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile)
- `TaskCanceledException`: use `cancellationToken.IsCancellationRequested` to distinguish timeout vs cancellation
- Program.cs has UTF-8 BOM — use `UTF8Encoding($true)` when writing
- `FormatDuration` duplicated in CLI/MCP — extract to Core if third consumer appears
- HelixMcpTools.cs duplicated in HelixTool and HelixTool.Mcp — both must be updated together
- Two DI containers in CLI: one for commands, separate `Host.CreateApplicationBuilder()` for `hlx mcp`

**Features implemented:**
- US-1 (positional args), US-5 (dotnet tool packaging v0.1.0), US-11 (--json flag on status/files)
- US-17 (namespace cleanup: HelixTool.Core, HelixTool.Mcp), US-18 (removed unused Spectre.Console)
- US-20 (rich status: State, ExitCode, Duration, MachineName), US-24 (download by URL)
- US-25 (ConsoleLogUrl on WorkItemResult), US-29 (MCP URL parsing for optional workItem)
- US-30 (structured JSON: grouped files, jobId+helixUrl in status)
- US-10 (WorkItemDetail + work-item command + hlx_work_item MCP tool)
- US-23 (BatchJobSummary + batch-status command + hlx_batch_status MCP tool, SemaphoreSlim(5) throttling)
- Stdio MCP transport via `hlx mcp` subcommand

**Team updates received:**
- Architecture review, caching strategy, cache TTL policy, requirements backlog (30 US), docs fixes (Kane), auth design (US-4), MCP test strategy — all in decisions.md

📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley

📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley

📌 Team update (2026-02-12): US-22 console log search implemented — SearchConsoleLogAsync in HelixService with case-insensitive pattern matching, context lines, and maxMatches cap. CLI `search-log` command with colored output. MCP `hlx_search_log` tool in both HelixMcpTools.cs files with URL resolution and JSON output (context array per match). — decided by Ripley

## Learnings
- SearchConsoleLogAsync downloads the log via DownloadConsoleLogAsync to a temp file, reads all lines, searches, then deletes the temp file. This reuses the existing download infrastructure rather than streaming.
- LogMatch record uses optional `Context` property (`List<string>?`) to carry the full context window (before + match + after lines) when contextLines > 0. This lets the CLI and MCP tools render context without re-reading the file.
- HelixMcpTools consolidated into HelixTool.Core — single copy, no more dual-update requirement. Both HelixTool (stdio) and HelixTool.Mcp (HTTP) reference Core for MCP tool discovery via `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`.
- `WithToolsFromAssembly()` (no args) only scans the calling assembly. When tools live in a referenced library, you must pass `typeof(SomeToolClass).Assembly` explicitly.
- ModelContextProtocol package added to HelixTool.Core for `[McpServerToolType]` and `[McpServerTool]` attributes. This is the base package (not AspNetCore variant).


📌 Team update (2026-02-11): Consolidated HelixMcpTools.cs from 2 copies (HelixTool + HelixTool.Mcp) into 1 in HelixTool.Core. Updated tool discovery to typeof(HelixMcpTools).Assembly. Removed Mcp ProjectReference from tests. Build clean, 126/126 tests passed. — decided by Ripley
- Added `<PackageType>McpServer</PackageType>` to HelixTool.csproj and created `.mcp/server.json` for dnx zero-install MCP server support. The server.json describes the package as `hlx` on nuget with `mcp` positional argument. The json file is packed into the nupkg at `/.mcp/` via a `<None>` item with `Pack=true`.
- Created `.github/workflows/ci.yml` — GitHub Actions CI workflow. Triggers on push/PR to main/master. Matrix builds on ubuntu-latest and windows-latest. Uses .NET 10 preview SDK via `actions/setup-dotnet@v4`. Steps: checkout → restore → build → test. The repo-root `nuget.config` (with dotnet-eng feed) is picked up automatically by `dotnet restore`.
- Renamed NuGet PackageId from `hlx` to `lewing.helix.mcp` following baronfel's `baronfel.binlog.mcp` naming convention. Added PackageTags, PackageReadmeFile, PublishRepositoryUrl. Added Content item to pack README.md into nupkg. ToolCommandName stays `hlx` — the CLI command is unchanged.
- Updated `.mcp/server.json` to Chet's 2025-10-17 schema format: new schema URL, `registryType`/`identifier` fields replacing `registry_name`/`name`, added `title`, `version`, and `websiteUrl` top-level fields. Dropped `version_detail` block.

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry

- Created `.github/workflows/publish.yml` — NuGet Trusted Publishing workflow. Triggers on `v*` tag push. Uses OIDC via `NuGet/login@v1` (no API key secret needed — just `NUGET_USER`). Packs with `dotnet pack src/HelixTool -c Release -o src/HelixTool/nupkg`, pushes with glob `*.nupkg`. Creates GitHub Release via `ncipollo/release-action@v1` with nupkg attached. Uses `$GITHUB_OUTPUT` (not deprecated `set-output`). Matched CI's .NET 10 preview SDK config. Pattern adapted from baronfel/mcp-binlog-tool release.yml.

📌 Team update (2025-02-12): Publish workflow created at .github/workflows/publish.yml — NuGet Trusted Publishing via OIDC — decided by Ripley


📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

- Implemented SQLite-backed caching layer (R-CACHE-1 through R-CACHE-11). New files: `Cache/ICacheStore.cs` (interface), `Cache/CacheOptions.cs` (XDG path resolution), `Cache/CacheStatus.cs` (status record), `Cache/SqliteCacheStore.cs` (SQLite + disk implementation with WAL mode, PRAGMA user_version=1, eviction), `Cache/CachingHelixApiClient.cs` (decorator with TTL matrix: 15s/30s running, 1h/4h completed, console log bypass for running jobs, stream caching via disk).
- DI updated in both CLI container and `hlx mcp` container in Program.cs. `HelixApiClient` registered as concrete type, `IHelixApiClient` resolved as `CachingHelixApiClient` wrapping it.
- Added `cache clear` and `cache status` CLI commands using ConsoleAppFramework's `[Command("cache clear")]` pattern for subcommand routing.
- Cache key format: `job:{jobId}:details`, `job:{jobId}:workitems`, `job:{jobId}:wi:{workItem}:details`, etc.
- Artifact files stored at `{cache_root}/artifacts/{jobId[0:8]}/{sanitized_key}` with write-then-rename pattern.
- `CachingHelixApiClient` uses private DTO records (`JobDetailsDto`, `WorkItemSummaryDto`, etc.) that implement the projection interfaces for JSON round-tripping.
- `HLX_CACHE_MAX_SIZE_MB=0` disables caching entirely (pass-through mode).
- llmstxt updated with cache commands and caching section.

📌 Team update (2026-02-12): SQLite-backed caching layer implemented — ICacheStore, SqliteCacheStore, CachingHelixApiClient, cache clear/status commands, DI wiring for CLI and MCP containers — decided by Ripley

📌 Session 2026-02-12-cache-implementation: Lambert wrote 56 tests (L-CACHE-1 through L-CACHE-10) against cache implementation — all pass. 182 total tests, build clean. Committed as d62d0d1, pushed to origin/main.

- Security fix: Cache auth isolation. Separate SQLite DBs and artifact dirs per auth context. No token → `{base}/public/`, token → `{base}/cache-{SHA256[0:8]}/`. Prevents unauthenticated instances from reading cached private job data. `CacheOptions` gets `AuthTokenHash` property and `ComputeTokenHash()` static helper. `GetEffectiveCacheRoot()` subdivides by auth context; `GetBaseCacheRoot()` returns the old unsegmented root. Both DI containers in Program.cs pass the token hash. `cache clear` wipes all auth contexts. `cache status` shows current context. SqliteCacheStore unchanged — it already uses `GetEffectiveCacheRoot()`.

📌 Team update (2026-02-13): Security fix — cache auth isolation implemented. Separate SQLite DB + artifacts per HELIX_ACCESS_TOKEN hash. 182/182 tests pass. Decision doc at decisions/inbox/ripley-cache-auth-isolation.md — decided by Ripley

## Learnings

- Path traversal hardening requires defense-in-depth: sanitize inputs (replace `..`, `/`, `\` with `_`) AND validate resolved paths stay within their root directory via `Path.GetFullPath` prefix check. Either alone is insufficient.
- Cache key segments (jobId, workItemName, fileName) must be sanitized before embedding in `:` delimited cache keys, because those keys later become file path segments in `SqliteCacheStore.SetArtifactAsync`.
- `Path.GetFileName()` is the first line of defense for API-returned file names — it strips any directory components, so `../../etc/passwd` becomes `passwd`.
- `ValidatePathWithinRoot` must append `Path.DirectorySeparatorChar` to the root before `StartsWith` comparison, otherwise a root of `/foo/bar` would incorrectly match `/foo/bar-evil/`.
- `CacheSecurity` is `internal static` — keeps the API surface clean and avoids exposing security helpers as public API.

📌 Team update (2026-02-13): Security fix — path traversal hardening for cache and download paths. New CacheSecurity.cs with ValidatePathWithinRoot/SanitizePathSegment/SanitizeCacheKeySegment. Hardened SqliteCacheStore (Get/Set artifact), CachingHelixApiClient (all 6 cache key sites), HelixService (3 download methods). 182/182 tests pass. Decision doc at decisions/inbox/ripley-path-traversal-hardening.md — decided by Ripley

📌 Team update (2026-02-13): HTTP/SSE multi-client auth architecture (R-HTTP-1 through R-HTTP-3, R-HTTP-6) implemented per Dallas's design (dallas-http-sse-auth.md). New files: IHelixTokenAccessor.cs (interface + EnvironmentHelixTokenAccessor), IHelixApiClientFactory.cs (interface + HelixApiClientFactory), Cache/ICacheStoreFactory.cs (interface + CacheStoreFactory with ConcurrentDictionary). SqliteCacheStore refactored to connection-per-operation with Cache=Shared for thread-safe concurrent access (replaces single SqliteConnection field). File I/O hardened with FileShare.ReadWrite for concurrent read/write safety. Program.cs rewired: both CLI and MCP containers use IHelixTokenAccessor for token resolution. 233/233 tests pass. — decided by Ripley

## Learnings
- SqliteCacheStore now uses connection-per-operation (`OpenConnection()` returns a new `SqliteConnection` each time with `Cache=Shared`). This enables thread-safe concurrent access needed for HTTP/SSE multi-client mode. WAL mode is set once during `InitializeSchema()`, `busy_timeout` is set per-connection in `OpenConnection()`.
- `File.Move(overwrite: true)` on Windows throws `UnauthorizedAccessException` (not just `IOException`) when the target file is locked by a concurrent reader. Must catch both exception types.
- `File.OpenRead()` uses `FileShare.Read` which blocks concurrent writers. For concurrent access scenarios, use `new FileStream(..., FileShare.ReadWrite | FileShare.Delete)` instead.
- Temp files for write-then-rename must use unique names (e.g., `Guid.NewGuid()`) to avoid collisions between concurrent writers to the same cache key.

📌 Team update (2026-02-13): R-HTTP-4 and R-HTTP-5 implemented — HttpContextHelixTokenAccessor created in HelixTool.Mcp, Program.cs fully rewired with scoped DI for multi-client HTTP/SSE auth. IHelixTokenAccessor (Scoped), IHelixApiClientFactory (Singleton), ICacheStoreFactory (Singleton), CacheOptions/ICacheStore/IHelixApiClient/HelixService (Scoped). Token extracted from Authorization header (Bearer/token schemes, case-insensitive), falls back to HELIX_ACCESS_TOKEN env var. ServerInfo version set to 0.1.2. Added missing `using HelixTool.Mcp` to test file. 252/252 tests pass. — decided by Ripley

