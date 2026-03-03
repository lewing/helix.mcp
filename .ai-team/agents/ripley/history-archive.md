# Ripley — History Archive

> Entries older than 2 weeks, archived on 2026-02-15.

## 2025-07-23: Threat model action items

📌 Team update (2025-07-23): STRIDE threat model approved — Ripley has two action items: (1) E1: add URL scheme validation in DownloadFromUrlAsync (reject non-http/https), (2) D1: add batch size guard in GetBatchStatusAsync (cap at 50) — decided by Dallas

## 2025-07-23: P1 Security Fixes (E1 + D1)

- **E1 — URL scheme validation:** Added scheme check in `DownloadFromUrlAsync` (HelixService.cs line ~388). After parsing the URL to a `Uri`, validates `uri.Scheme` is `"http"` or `"https"`. Throws `ArgumentException` with message including the rejected scheme. This runs before any HTTP request, blocking `file://`, `ftp://`, etc.
- **D1 — Batch size limit:** Added `internal const int MaxBatchSize = 50` to `HelixService` (line ~488). `GetBatchStatusAsync` now checks `idList.Count > MaxBatchSize` and throws `ArgumentException` with the actual count and the limit. Tests can reference the constant via `HelixService.MaxBatchSize`.
- **MCP tool description updated:** `hlx_batch_status` description in `HelixMcpTools.cs` now documents "Maximum 50 jobs per request."
- All three changes use `ArgumentException`, consistent with the existing `ArgumentException.ThrowIfNullOrWhiteSpace` pattern in the codebase.

📌 Team update (2026-02-13): Security validation test strategy for E1+D1 fixes (18 tests, negative assertion pattern) — decided by Lambert


📌 Team update (2026-02-13): Remote search design — US-31 (hlx_search_file) and US-32 (hlx_test_results) designed. Phase 1: refactor SearchConsoleLogAsync + add SearchFileAsync (~100 lines). Phase 2: TRX parsing with XmlReaderSettings (DtdProcessing.Prohibit, XmlResolver=null). 50MB file size cap. — decided by Dallas

## 2025-07-23: US-31 — hlx_search_file Implementation

- **Extracted `SearchLines` private static helper** from `SearchConsoleLogAsync` in HelixService.cs. Takes `identifier`, `lines[]`, `pattern`, `contextLines`, `maxMatches` and returns `LogSearchResult`. Both `SearchConsoleLogAsync` and `SearchFileAsync` call it.
- **Added `SearchFileAsync`** to HelixService: downloads file via `DownloadFilesAsync(exact fileName)`, checks binary (null byte in first 8KB), enforces `MaxSearchFileSizeBytes` (50MB), delegates to `SearchLines`, cleans up temp files in finally. Returns `FileContentSearchResult(FileName, Matches, TotalLines, Truncated, IsBinary)`.
- **Added config toggle**: `IsFileSearchDisabled` static property checks `HLX_DISABLE_FILE_SEARCH=true` env var. Both `SearchConsoleLogAsync` and `SearchFileAsync` throw `InvalidOperationException` when disabled. MCP tools `SearchLog` and `SearchFile` return JSON error instead.
- **Added `hlx_search_file` MCP tool** in HelixMcpTools.cs following the exact `SearchLog` pattern — `TryResolveJobAndWorkItem` URL resolution, config toggle check, binary detection returns JSON error.
- **Added `search-file` CLI command** in Program.cs following the `search-log` pattern — positional args (jobId, workItem, fileName, pattern), context highlighting with ConsoleColor.Yellow.
- **Constants/records added**: `MaxSearchFileSizeBytes`, `IsFileSearchDisabled`, `FileContentSearchResult` record.
- Pattern: `DownloadFilesAsync` with exact fileName filter works as a single-file download since `MatchesPattern` does substring match — passing exact name matches only that file.


📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH config toggle added as security safeguard for disabling file content search operations — decided by Larry Ewing (via Copilot)

## 2025-07-23: US-32 — hlx_test_results TRX Parsing Implementation

- **Added `ParseTrxFile` private static method** to HelixService.cs. Parses TRX XML using secure `XmlReaderSettings` (`DtdProcessing.Prohibit`, `XmlResolver=null`, `MaxCharactersFromEntities=0`, `MaxCharactersInDocument=50M`). Extracts `UnitTestResult` elements from TRX namespace `http://microsoft.com/schemas/VisualStudio/TeamTest/2010`. Truncates error messages at 500 chars, stack traces at 1000 chars.
- **Added `ParseTrxResultsAsync` public method** to HelixService.cs. Downloads TRX files via `DownloadFilesAsync`, checks `IsFileSearchDisabled` and `MaxSearchFileSizeBytes`, parses each file, cleans up temp files in finally block. Auto-discovers all `.trx` files when no specific fileName is provided.
- **Added records**: `TrxTestResult(TestName, Outcome, Duration, ComputerName, ErrorMessage, StackTrace)` and `TrxParseResult(FileName, TotalTests, Passed, Failed, Skipped, Results)`.
- **Added `s_trxReaderSettings`** as `private static readonly XmlReaderSettings` field — follows the `s_jsonOptions` naming pattern.
- **Added `hlx_test_results` MCP tool** in HelixMcpTools.cs — follows SearchFile pattern with URL resolution, config toggle check, structured JSON output with per-file summary + results.
- **Added `test-results` CLI command** in Program.cs — positional args (jobId, workItem), color-coded output (red for FAIL, green for PASS, yellow for skipped).
- **Key patterns**: `using System.Xml` and `using System.Xml.Linq` added to HelixService.cs. XmlReader wraps FileStream for secure parsing. Filter logic: failed tests always included, non-pass/non-fail always included, passed only when `includePassed=true`.

## 2025-07-23: Status filter refactor (boolean → enum string)

- **MCP tool (`hlx_status`):** Replaced `bool includePassed = false` with `string filter = "failed"`. Three values: `"failed"` (default, shows only failures), `"passed"` (shows only passed, failed=null), `"all"` (both populated). Validation throws `ArgumentException` for invalid values. Uses `StringComparison.OrdinalIgnoreCase`.
- **CLI command (`status`):** Replaced `bool all = false` with `[Argument] string filter = "failed"` as second positional arg. Same three-way filter logic. Hint text updated from `(use --all to show)` to `(use 'hlx status <jobId> all' to show)`. Help text updated to `hlx status <jobId> [failed|passed|all]`.
- **Breaking change:** `--all` and `includePassed` no longer exist. Callers must use `filter="all"`.

📌 Team update (2025-07-23): Status filter refactored from boolean (--all/includePassed) to enum-style string (failed|passed|all) in both MCP tool and CLI command — decided by Ripley

## 2025-07-23: CI version validation

- Added "Validate version consistency" step to `.github/workflows/publish.yml` that checks csproj `<Version>`, server.json top-level `version`, and server.json `packages[0].version` all match the git tag. Fails with clear expected-vs-actual messages on mismatch.
- Updated Pack step to pass `/p:Version=${{ steps.tag_name.outputs.current_version }}` so the NuGet package version always matches the tag regardless of csproj content.
- Belt-and-suspenders: validation catches developer mistakes early, `/p:Version=` override ensures correctness even if validation is bypassed.


📌 Team update (2026-02-15): Cache security expectations documented in README (cached data subsection, auth isolation model, hlx cache clear recommendation) — decided by Kane
📌 Team update (2026-02-15): README v0.1.3 comprehensive update — llmstxt in Program.cs needs sync (missing hlx_search_file, hlx_test_results, search-file, test-results) — decided by Kane
📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth, csproj+server.json must match — decided by Ripley
> Entries archived on 2026-03-03 (>2 weeks old).

## Sessions (2026-02-12)

**Cache implementation (R-CACHE-1 through R-CACHE-11):** SQLite-backed caching layer — ICacheStore, SqliteCacheStore (WAL mode, PRAGMA user_version=1), CachingHelixApiClient (decorator with TTL matrix), CacheOptions (XDG paths). Cache key format: `job:{jobId}:details`. Artifacts stored at `{cache_root}/artifacts/{jobId[0:8]}/{sanitized_key}`. HLX_CACHE_MAX_SIZE_MB=0 disables caching. Private DTO records for JSON round-tripping. `cache clear` and `cache status` CLI commands.

**Cache security (2026-02-12):** Auth isolation (separate SQLite DB + artifacts per token SHA256 hash). Path traversal hardening (CacheSecurity.cs: ValidatePathWithinRoot, SanitizePathSegment, SanitizeCacheKeySegment). Applied to SqliteCacheStore, CachingHelixApiClient, HelixService download methods.

**HTTP/SSE multi-auth (R-HTTP-1 through R-HTTP-5, 2026-02-13):** IHelixTokenAccessor + EnvironmentHelixTokenAccessor, IHelixApiClientFactory, ICacheStoreFactory (ConcurrentDictionary). SqliteCacheStore refactored to connection-per-operation with Cache=Shared. HttpContextHelixTokenAccessor in HelixTool.Mcp. Program.cs scoped DI wiring for HTTP transport. 252/252 tests pass.

## Learnings (archived)

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

## Learnings (MCP API batch, archived)

- `MatchesPattern` promoted from `internal static` to `public static` so MCP tools and CLI can use it for file-type classification after removing `IsBinlog`/`IsTestResults` booleans from `FileEntry`.
- `FileEntry` simplified to `(string Name, string Uri)` — file type classification is now done at the presentation layer via `MatchesPattern`, not stored on the record.
- `BinlogResult` renamed to `FileSearchResult(string WorkItem, List<FileEntry> Files)` — generalized to support any file pattern search, not just binlogs.
- `FindBinlogsAsync` is now a convenience wrapper around `FindFilesAsync(jobId, "*.binlog", ...)`.
- `hlx_batch_status` MCP tool parameter changed from comma-separated `string` to `string[]` — MCP protocol handles array serialization natively.
- `s_jsonOptions` in `HelixMcpTools.cs` uses `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` — all MCP JSON output is now consistently camelCase. CLI `s_jsonOptions` in `Program.cs` is NOT changed (different UX).
- `hlx_status` MCP tool parameter renamed from `all` to `includePassed` for clarity. CLI `--all` flag is NOT renamed.
- Tests that assert PascalCase JSON property names (e.g., `"Name"`, `"ExitCode"`) in MCP output will need updating to camelCase — Lambert's responsibility.

📌 Team update (2026-02-13): Generalize hlx_find_binlogs to hlx_find_files with pattern parameter — add generic FindFilesAsync in Core, keep hlx_find_binlogs as convenience alias, rename BinlogResult to FileSearchResult — decided by Dallas

## 2026-02-15: Cross-agent note from Scribe (archived)

- **Decision merged:** "camelCase JSON assertion convention" (Lambert, 2026-02-13) — convention established for camelCase in all MCP test assertions.
- **Decision merged:** "MCP API Batch — Tests Need CamelCase Update" (Ripley, 2026-02-15) — your note to Lambert about test updates has been propagated.

📌 Team update (2026-02-15): Cache security expectations documented in README (cached data subsection, auth isolation model, hlx cache clear recommendation) — decided by Kane
📌 Team update (2026-02-15): README v0.1.3 comprehensive update — llmstxt in Program.cs needs sync (missing hlx_search_file, hlx_test_results, search-file, test-results) — decided by Kane
📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth, csproj+server.json must match — decided by Ripley
📌 Team update (2026-02-27): Enhancement layer documentation consolidated — Dallas cataloged 12 value-adds, Kane audited doc surfaces and wrote README section. Remaining P1: llmstxt missing hlx_search_file/hlx_test_results, MCP descriptions need local-enhancement flags — decided by Dallas, Kane
📌 Team update (2026-02-27): MCP descriptions should expose behavioral contracts, not implementation mechanics. P1: llmstxt still missing hlx_search_file and hlx_test_results. P1: hlx_status description should list failureCategory field — decided by Dallas
