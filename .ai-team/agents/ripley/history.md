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

**2025-07-23 session (archived):**
- STRIDE threat model action items: E1 URL scheme validation in DownloadFromUrlAsync, D1 batch size guard in GetBatchStatusAsync
- P1 security fixes implemented: URL scheme check (http/https only, throws ArgumentException), MaxBatchSize=50 const with guard in GetBatchStatusAsync, MCP tool description updated
- US-31 hlx_search_file: extracted SearchLines helper, added SearchFileAsync (binary detection, 50MB cap, config toggle), MCP tool + CLI command
- US-32 hlx_test_results: TRX parsing with secure XmlReaderSettings (DtdProcessing.Prohibit), ParseTrxResultsAsync, auto-discovery of .trx files, MCP tool + CLI command
- Status filter refactor: bool includePassed → string filter (failed|passed|all), case-insensitive, breaking change
- CI version validation: publish workflow validates csproj+server.json match git tag, /p:Version= override
## Sessions (2026-02-12)

**Cache implementation (R-CACHE-1 through R-CACHE-11):** SQLite-backed caching layer — ICacheStore, SqliteCacheStore (WAL mode, PRAGMA user_version=1), CachingHelixApiClient (decorator with TTL matrix), CacheOptions (XDG paths). Cache key format: `job:{jobId}:details`. Artifacts stored at `{cache_root}/artifacts/{jobId[0:8]}/{sanitized_key}`. HLX_CACHE_MAX_SIZE_MB=0 disables caching. Private DTO records for JSON round-tripping. `cache clear` and `cache status` CLI commands.

**Cache security (2026-02-12):** Auth isolation (separate SQLite DB + artifacts per token SHA256 hash). Path traversal hardening (CacheSecurity.cs: ValidatePathWithinRoot, SanitizePathSegment, SanitizeCacheKeySegment). Applied to SqliteCacheStore, CachingHelixApiClient, HelixService download methods.

**HTTP/SSE multi-auth (R-HTTP-1 through R-HTTP-5, 2026-02-13):** IHelixTokenAccessor + EnvironmentHelixTokenAccessor, IHelixApiClientFactory, ICacheStoreFactory (ConcurrentDictionary). SqliteCacheStore refactored to connection-per-operation with Cache=Shared. HttpContextHelixTokenAccessor in HelixTool.Mcp. Program.cs scoped DI wiring for HTTP transport. 252/252 tests pass.

## Learnings

- SqliteCacheStore uses connection-per-operation (`OpenConnection()` returns new `SqliteConnection` with `Cache=Shared`). WAL mode set once in `InitializeSchema()`, `busy_timeout` set per-connection.
- `File.Move(overwrite: true)` on Windows throws `UnauthorizedAccessException` (not just `IOException`) when target is locked by concurrent reader. Catch both.
- `File.OpenRead()` uses `FileShare.Read` which blocks concurrent writers. Use `new FileStream(..., FileShare.ReadWrite | FileShare.Delete)` instead.
- Temp files for write-then-rename must use unique names (`Guid.NewGuid()`) to avoid concurrent writer collisions.
- Path traversal defense-in-depth: sanitize inputs (replace `..`, `/`, `\` with `_`) AND validate resolved paths via `Path.GetFullPath` prefix check.
- `ValidatePathWithinRoot` must append `Path.DirectorySeparatorChar` to root before `StartsWith` — prevents `/foo/bar` matching `/foo/bar-evil/`.
- `WithToolsFromAssembly()` (no args) only scans calling assembly. Pass `typeof(SomeToolClass).Assembly` for referenced libraries.
- ModelContextProtocol base package added to Core for `[McpServerToolType]` and `[McpServerTool]` attributes.
- `DownloadFilesAsync` shared temp dir `helix-{idPrefix}` causes cross-process race: concurrent stdio MCP server processes writing the same job's files via `File.Create` (truncates immediately) + `CopyToAsync` corrupt each other. Fix: append per-invocation `Guid.NewGuid():N` to temp dir name so each call is isolated.

📌 Team update (2026-02-13): R-HTTP-4 and R-HTTP-5 implemented — HttpContextHelixTokenAccessor + scoped DI for multi-client HTTP/SSE auth. 252/252 tests pass. — decided by Ripley


📌 Team update (2026-02-13): HTTP/SSE multi-client auth architecture decided — IHttpContextAccessor + scoped IHelixApiClient factory pattern. ICacheStoreFactory for concurrent cache store management. SqliteCacheStore connection-per-operation refactor for HTTP concurrency safety. — decided by Dallas
📌 Team update (2026-02-13): Multi-auth support deferred — current single-token-per-process model is sufficient for both stdio and HTTP transports. — decided by Dallas

📌 Team update (2026-02-13): US-9 script removability analysis complete — 100% core API coverage, Phase 1 migration can proceed with zero blockers — decided by Ash
📌 Team update (2026-02-13): US-6 download E2E verification complete — 46 tests covering DownloadFilesAsync/DownloadFromUrlAsync, all 298 tests pass — decided by Lambert

📌 Team update (2026-02-15): README now documents caching (settings table, TTL policy, auth isolation), HTTP per-request multi-auth, and expanded project structure (Cache/, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor) — decided by Kane

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash
📌 Team update (2026-02-13): MCP API design review — 6 actionable improvements identified (P0: batch_status array fix, P1: add hlx_list_work_items, P2: naming, P3: response envelope) — reviewed by Dallas

## Learnings (MCP API batch)

- `MatchesPattern` promoted from `internal static` to `public static` so MCP tools and CLI can use it for file-type classification after removing `IsBinlog`/`IsTestResults` booleans from `FileEntry`.
- `FileEntry` simplified to `(string Name, string Uri)` — file type classification is now done at the presentation layer via `MatchesPattern`, not stored on the record.
- `BinlogResult` renamed to `FileSearchResult(string WorkItem, List<FileEntry> Files)` — generalized to support any file pattern search, not just binlogs.
- `FindBinlogsAsync` is now a convenience wrapper around `FindFilesAsync(jobId, "*.binlog", ...)`.
- `hlx_batch_status` MCP tool parameter changed from comma-separated `string` to `string[]` — MCP protocol handles array serialization natively.
- `s_jsonOptions` in `HelixMcpTools.cs` uses `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` — all MCP JSON output is now consistently camelCase. CLI `s_jsonOptions` in `Program.cs` is NOT changed (different UX).
- `hlx_status` MCP tool parameter renamed from `all` to `includePassed` for clarity. CLI `--all` flag is NOT renamed.
- Tests that assert PascalCase JSON property names (e.g., `"Name"`, `"ExitCode"`) in MCP output will need updating to camelCase — Lambert's responsibility.
📌 Team update (2026-02-13): Generalize hlx_find_binlogs to hlx_find_files with pattern parameter — add generic FindFilesAsync in Core, keep hlx_find_binlogs as convenience alias, rename BinlogResult to FileSearchResult — decided by Dallas

## 2026-02-15: Cross-agent note from Scribe

- **Decision merged:** "camelCase JSON assertion convention" (Lambert, 2026-02-13) — convention established for camelCase in all MCP test assertions.
- **Decision merged:** "MCP API Batch — Tests Need CamelCase Update" (Ripley, 2026-02-15) — your note to Lambert about test updates has been propagated.


📌 Team update (2026-02-15): Cache security expectations documented in README (cached data subsection, auth isolation model, hlx cache clear recommendation) — decided by Kane
📌 Team update (2026-02-15): README v0.1.3 comprehensive update — llmstxt in Program.cs needs sync (missing hlx_search_file, hlx_test_results, search-file, test-results) — decided by Kane
📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth, csproj+server.json must match — decided by Ripley

📌 Team update (2026-02-27): Enhancement layer documentation consolidated — Dallas cataloged 12 value-adds, Kane audited doc surfaces and wrote README section. Remaining P1: llmstxt missing hlx_search_file/hlx_test_results, MCP descriptions need local-enhancement flags — decided by Dallas, Kane


📌 Team update (2026-02-27): MCP descriptions should expose behavioral contracts, not implementation mechanics. P1: llmstxt still missing hlx_search_file and hlx_test_results. P1: hlx_status description should list failureCategory field — decided by Dallas

## Learnings

- Added `failureCategory` to the `hlx_status` MCP tool `[Description]` parenthetical field list (line 23 of HelixMcpTools.cs). The field was already returned in JSON output (line 46) but omitted from the description. This is a documentation-completeness fix per Dallas's decision that MCP descriptions should accurately list returned fields.

📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas
