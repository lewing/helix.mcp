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
## Summarized History (through 2026-02-11) — archived 2026-03-08

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

## Archived 2026-03-09: Learnings (HelixTool.Mcp.Tools extraction)

- **HelixTool.Mcp.Tools project:** New class library at `src/HelixTool.Mcp.Tools/` containing MCP tool definitions (HelixMcpTools, AzdoMcpTools) and MCP result DTOs (McpToolResults.cs). No NuGet packaging metadata.
- **Namespace: `HelixTool.Mcp.Tools`:** All three moved files use this namespace. AzdoMcpTools was previously `HelixTool.Core.AzDO`.
- **ModelContextProtocol package removed from Core:** Core no longer references `ModelContextProtocol` — that dependency now lives in `HelixTool.Mcp.Tools`.
- **`IsFileSearchDisabled` promoted to public:** Was `internal static` on `HelixService`, had to become `public` for separate assembly.
- **`WithToolsFromAssembly` assembly reference:** Both CLI and HTTP server use `typeof(HelixMcpTools).Assembly`.
- **Test project references all three:** `HelixTool.Tests.csproj` now references Core, Mcp, and Mcp.Tools projects. Six test files needed `using HelixTool.Mcp.Tools;`.
- **git mv preserves history:** Used `git mv` for all three file moves.

## Archived 2026-03-09: Learnings (azdo_search_log implementation)

- **TextSearchHelper extraction:** `SearchLines()` moved from `HelixService` (private static) to `TextSearchHelper` (public static) in `HelixTool.Core`. Records (LogMatch, LogSearchResult, FileContentSearchResult) promoted to top-level in Core namespace.
- **Default parameter values matter:** Added `contextLines = 0, maxMatches = 50` defaults to `TextSearchHelper.SearchLines()` — existing tests relied on calling with fewer args.
- **AzDO log fetching already supports full content:** `AzdoApiClient.GetBuildLogAsync` returns the complete log; for search, pass `tailLines: null` to get full content.
- **IsFileSearchDisabled dual-check pattern:** MCP tool layer (throws McpException) and service layer (throws InvalidOperationException) both check.
- **CLI search output pattern:** Context lines displayed with `>>>` prefix for matching line and `   ` prefix for context. Line numbers right-aligned in 6-char column.

📌 Team update (2026-03-08): AzDO search gap analysis consolidated — CI-analysis skill study validated `azdo_search_log` as P0, confirmed `SearchLines()` extraction approach. New P1 ideas: `azdo_search_timeline`, `azdo_search_log_across_steps`. — analyzed by Ash

## Archived 2026-03-09: Learnings (PR #10 review fixes)

- **CLI line-number calculation:** Derive `startLine` from `m.LineNumber - contextLines` (clamped to 0) instead of TakeWhile. TakeWhile breaks with duplicate line text.
- **CRLF normalization in AzDO logs:** Normalize `\r\n` → `\n`, `\r` → `\n` before `Split('\n')`, trim trailing empty entry.
- **MCP result field naming honesty:** Name property to reflect broader type when field accepts ID or URL.
- **McpException wrapping pattern:** Wrap service calls for expected exceptions (InvalidOperationException, HttpRequestException) and rethrow as McpException.

## Archived 2026-03-09: Learnings (azdo_search_timeline implementation)

- **Domain types in Core, not MCP.Tools:** `TimelineSearchMatch` and `TimelineSearchResult` live in `HelixTool.Core.AzDO` (AzdoModels.cs). MCP tools return Core types directly.
- **`[JsonIgnore]` for raw record access:** `TimelineSearchMatch.Record` exposes underlying `AzdoTimelineRecord` with `[JsonIgnore]`.
- **Duration formatting in service layer:** Service computes formatted duration strings. `FormatDuration` is private to `AzdoService`.
- **Null timeline → InvalidOperationException:** MCP layer catches and wraps as McpException.
- **Pre-existing tests drove API shape:** Aligning service return type to match test expectations.

## Archived 2026-03-09: Learnings (PR #11 review fixes)

- **CLI-side validation before service calls:** Validate at CLI layer so error messages reference CLI option names. Check valid values with `string.Equals(OrdinalIgnoreCase)`, throw `ArgumentException` with `nameof(cliParam)`.
- **Doc accuracy for 'failed' filter semantics:** `result="failed"` means "non-succeeded OR has timeline issues", not just result=failed.
