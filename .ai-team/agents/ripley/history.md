# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Four projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server), HelixTool.Mcp.Tools (MCP tool definitions)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context (summarized through 2026-03-09)

> Older history archived to history-archive.md on 2026-03-09.

**Architecture & DI:** IHelixApiClient with projection interfaces (IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile). Constructor injection on HelixService. DI: CLI via `ConsoleApp.ServiceProvider`, MCP via `builder.Services.AddSingleton<>()`. Two DI containers in CLI (commands + `hlx mcp`). Program.cs has UTF-8 BOM.

**Helix features (P0-P1 complete):** Positional args, dotnet tool packaging, --json flag, namespace cleanup, rich status, download by URL, ConsoleLogUrl, batch status (SemaphoreSlim(5)), URL parsing for optional workItem, structured JSON, stdio MCP transport, hlx_search_file (SearchLines helper, binary detection, 50MB cap), hlx_test_results (TRX+xUnit XML, XXE-safe, auto-discovery via TestResultFilePatterns). Status filter: `failed|passed|all`. MaxBatchSize=50. URL scheme validation (http/https only).

**Cache (2026-02-12):** SQLite-backed (WAL mode, connection-per-operation with `Cache=Shared`), CachingHelixApiClient decorator with TTL matrix, XDG paths, `HLX_CACHE_MAX_SIZE_MB=0` disables. Auth isolation via per-token-hash DB + artifact dirs. Path traversal defense-in-depth: sanitize + `Path.GetFullPath` prefix check. `ValidatePathWithinRoot` must append `DirectorySeparatorChar` to root.

**HTTP/SSE multi-auth (2026-02-13):** IHelixTokenAccessor + IHelixApiClientFactory + ICacheStoreFactory pattern. HttpContextHelixTokenAccessor in HelixTool.Mcp. Scoped DI for HTTP transport. Multi-auth deferred — single-token-per-process sufficient. CacheStoreFactory uses `ConcurrentDictionary<K, Lazy<T>>` for thread-safe single-invocation.

**MCP API refactoring:** FileEntry simplified to `(Name, Uri)`. `FindFilesAsync` generalized from binlog-only. `hlx_batch_status` uses `string[]` (native MCP arrays). All MCP JSON output camelCase via `JsonNamingPolicy.CamelCase`. `UseStructuredContent=true` for all 12 tools (hlx_logs excepted). HelixException → McpException translation in tool handlers.

**Test result parsing:** TRX strict + xUnit XML best-effort. Auto-discovery via TestResultFilePatterns (`*.trx`, `testResults.xml`, `*.testResults.xml.txt`, `testResults.xml.txt`). Single file list query per work item. `IsTestResultFile()` public for CLI tagging.

**AzDO foundation (2026-03-07):** Files in `src/HelixTool.Core/AzDO/`. AzdoModels.cs (sealed records, `[JsonPropertyName]`), IAzdoTokenAccessor (AZDO_TOKEN → az CLI → null), AzdoIdResolver (dev.azure.com + visualstudio.com, no regex), IAzdoApiClient (7 methods), AzdoApiClient (HTTP + Bearer auth, 404→null, 401/403→auth hint). CachingAzdoApiClient with `azdo:` key prefix, dynamic TTL by build status.

**AzDO MCP tools (2026-03-07):** 6 tools with `azdo_` prefix. Context-limiting defaults: tailLines=500, top=20-200, filter="failed". Timeline filtering is client-side (parent chain walk-up). Model types used directly as MCP returns (no wrapper DTOs needed). Cache keys must include all limit/filter parameters.

**AzDO CLI (2026-03-08):** 9 commands mirroring MCP tools. IHttpClientFactory via DI (named clients "HelixDownload" + "AzDO", 5-min timeout). `HttpCompletionOption.ResponseHeadersRead` for streaming.

**Mcp.Tools extraction (2026-03-08):** Separate `HelixTool.Mcp.Tools` project for MCP tool definitions + DTOs. ModelContextProtocol dependency removed from Core. `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`. Internal→public promotions for cross-assembly access. `git mv` for history preservation. Test project references Core, Mcp, and Mcp.Tools.

**AzDO search & timeline tools (2026-03-08–09):** `TextSearchHelper` extracted to Core (shared Helix/AzDO search). Domain types (TimelineSearchMatch, TimelineSearchResult, CrossStepSearchResult) in Core AzdoModels.cs, returned directly by MCP tools. `[JsonIgnore]` for raw record access. CRLF normalization (`\r\n`→`\n`) before Split('\n'). `IsFileSearchDisabled` dual-check: MCP→McpException, service→InvalidOperationException. CLI-side validation references CLI option names in errors. MCP result fields named for broader type (e.g., `Build` not `BuildId`). McpException wrapping for service exceptions. `result="failed"` filter means non-succeeded OR has issues.

**Key patterns:**
- `File.Move(overwrite: true)` on Windows: catch both `IOException` and `UnauthorizedAccessException`
- Per-invocation `Guid.NewGuid()` temp dirs to prevent cross-process races
- `WithToolsFromAssembly()` needs explicit assembly arg for referenced libs
- MCP descriptions should expose behavioral contracts, not implementation mechanics
- Default parameter values: check test call sites when extracting methods to public APIs
- CLI context line-numbers: use `m.LineNumber - contextLines`, not TakeWhile (breaks with duplicate lines)

## Learnings (azdo_search_log_across_steps implementation)

- **NormalizeAndSplit extraction:** When two methods share identical preprocessing, extract immediately.
- **4-bucket ranking:** Logs ranked by failure likelihood (failed → issues → succeededWithIssues → succeeded → orphans), largest first. Matches human scan order.
- **Early termination:** Two triggers — match budget exhausted OR log search budget exhausted.
- **Task.WhenAll for metadata:** Timeline + logs list fetched concurrently. Always `await` individual tasks after WhenAll.
- **CachingAzdoApiClient dynamic TTL for logs list:** Completed → 4h, in-progress → 15s.

📌 Team updates (2026-03-08 – 2026-03-09 summary): AzDO search gap analysis, incremental log fetching (PR #13), CountLines off-by-one fix, azdo_search_log_across_steps spec, test quality guidelines (~20 tests cleaned, PR #15). — decided by Ash/Dallas/Lambert

## Core Context (performance & review fixes, summarized 2026-03-09)

> Full entries archived to history-archive.md.

- **Perf review (2025-07-18):** 17 allocation issues found, 8 fixed. Key anti-patterns: chained `.Replace()`, Split+Join for tail, substring in loops, triple-iteration categorization, disk round-trip for search, JSON-serialized log strings.
- **PR #13 fixes:** Integer overflow guards (cast to `long`). Cache-layer range returns `null` for out-of-range. Allocation-free `CountLines` via `AsSpan().Count('\n')`. `SearchValues<char>` for line-break scanning. Shared `StringHelpers.TailLines`. Cache format migration via `raw:` prefix.
- **PR #14 fixes:** CRLF normalization before Split. Cache sentinel collision fix via NUL byte prefix.
- **Version 0.3.0:** AzDO integration, perf optimizations, incremental log support.

## MCP Error Surfacing & Message Quality (latest session)

**Exception wrapping in MCP tools:** All AzDO and Helix MCP tool handlers now wrap service-layer exceptions (HttpRequestException, InvalidOperationException, ArgumentException, HelixException) in McpException with the actual error message. Pattern: `catch (Exception ex) when (ex is X or Y) { throw new McpException($"Failed to {action}: {ex.Message}", ex); }`. The MCP SDK only surfaces `McpException.Message` to clients — unhandled exceptions become generic "An error occurred" messages.

**helix_test_results error message:** When no TRX files found, error now filters out noise (hash-named .log files) and highlights useful files (crash dumps, binlogs, XML). Crash artifacts (core.*, *.dmp, *crashdump*) get a ⚠️ callout with search suggestions. When only .log files exist, suggests `helix_search_log` with pattern `'  Failed'`.

**helix_search_log description:** Updated to document substring-based (not regex) matching, literal treatment of metacharacters, and common search patterns.

📌 Team update (2026-03-09): CI profile analysis — 14 recommendations for MCP tool descriptions/error messages. Tool description changes in HelixMcpTools.cs, AzdoMcpTools.cs, error messages in HelixService.cs. — decided by Ash

📌 Team update (2025-07-24): Test quality review — ~17 redundant tests deleted, guidelines: no layer duplication, ≤1 passthrough smoke test, prune proactive tests when real tests land. — decided by Dallas

## Learnings (MCP tool description updates with CI knowledge)

- **Updated 5 tool descriptions** across HelixMcpTools.cs (helix_test_results, helix_search_log) and AzdoMcpTools.cs (azdo_test_runs, azdo_test_results, azdo_timeline) to embed repo-specific CI knowledge.
- **Key pattern: warn-before-fail.** helix_test_results now warns that 4/6 major repos don't upload TRX to Helix, directing agents to azdo_test_runs + azdo_test_results instead. This prevents the most common wasted tool call.
- **Repo-specific search patterns in descriptions:** helix_search_log now lists failure patterns per repo (runtime='[FAIL]', aspnetcore/efcore='  Failed', sdk='error MSB', roslyn='aborted'/'Process exited').
- **Trust-but-verify on run counts:** azdo_test_runs description now warns that failedTests=0 can be a lie — always drill into azdo_test_results.
- **Helix task name mapping in azdo_timeline:** runtime/aspnetcore='Send to Helix', sdk='🟣 Run TestBuild Tests', efcore='Send job to helix', roslyn=embedded, VMR=no Helix.
- **helix_ci_guide cross-references:** Both helix_test_results and helix_search_log now point agents to helix_ci_guide for full repo profiles.

## Learnings (CiKnowledgeService enrichment — 9-repo knowledge base)

- **Expanded CiRepoProfile record** with 9 new properties: PipelineNames, OrgProject, ExitCodeMeanings, WorkItemNamingPattern, KnownGotchas, RecommendedInvestigationOrder, TestFramework, TestRunnerModel, UploadedFiles. All use init-only defaults so existing code is backward-compatible.
- **Added 3 new repos:** maui (3 pipelines!), macios (devdiv org, NUnit, Make-based), android (devdiv org, NUnit+xUnit, emulator targets). Total: 9 repos.
- **Key pattern: devdiv org repos need explicit warnings.** macios and android are on devdiv.visualstudio.com — standard `helix_*` and `ado-dnceng-*` tools do NOT work. KnownGotchas leads with ⚠️ prefix.
- **ExitCodeMeanings as string[]** — cleaner than Dictionary<int, string> since entries include contextual notes (e.g., "0: Passed (but can coexist with [FAIL] results)").
- **MAUI has 3 separate pipelines** with completely different investigation approaches — this is the only repo where pipeline identity matters for tool selection.
- **FormatProfile() enriched** with sections for Org/Project, Pipelines, Gotchas, Exit Codes, Investigation Order, File Inventory. Gotchas rendered first because they're the most critical.
- **GetOverview() enriched** with Org column and helix_test_results status column — agents immediately see which repos work with which tools.
- **Test update needed:** Existing test asserted 6 repos; updated to 9. Lambert should review for coverage of new repos.

📌 Team update (2026-03-10): CiKnowledgeService enrichment merged — 9 full profiles, 9 new properties, 5 tool descriptions updated. 171 new tests by Lambert. All on mcp-error-improvements branch (PR #16). — decided by Ripley

## Learnings (PR #16 review comment fixes)

- **Indentation inside try blocks must match method scope.** When wrapping existing code in a try block, re-indent the body. Four methods in HelixMcpTools.cs (Status, Files, WorkItem, BatchStatus) had inconsistent indentation after try-wrapping.
- **McpException wrapping must include inner exception and "Failed to" prefix.** Three AzDO search tool catch blocks (azdo_search_log, azdo_search_timeline, azdo_search_log_across_steps) were doing `throw new McpException(ex.Message)` — dropping inner exception and context. Pattern: `throw new McpException($"Failed to {action}: {ex.Message}", ex)`.
- **Error messages shouldn't recommend a single repo-specific pattern.** The "no test results" message in HelixService.cs suggested only `'  Failed'`, but patterns vary by repo (`[FAIL]` for runtime, `'  Failed'` for aspnetcore, etc.). Now suggests multiple patterns and points to `helix_ci_guide`.
- **Boolean properties with nuanced semantics should be strings/enums.** `UploadsTestResultsToHelix: bool` couldn't represent "partial" (runtime CoreCLR yes, libraries no) or "varies" (MAUI device tests yes, unit tests no). Replaced with `HelixTestResultAvailability: string` using values `"none"`, `"partial"`, `"varies"`. FormatProfile and GetOverview updated to render nuanced status.
- **When renaming a required record property, check test files too.** Three test assertions referenced `UploadsTestResultsToHelix` and needed updating to `HelixTestResultAvailability` + new values to compile.
