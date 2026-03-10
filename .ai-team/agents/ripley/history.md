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

📌 Team updates (2026-03-08 – 2026-03-09 summary): AzDO search gap analysis, incremental log fetching (PR #13), CountLines off-by-one fix, azdo_search_log_across_steps spec. — decided by Ash/Dallas

## Core Context (performance & review fixes, summarized 2026-03-09)

> Full entries archived to history-archive.md.

- **Perf review (2025-07-18):** 17 allocation issues found, 8 fixed (3 P0, 5 P1). Key anti-patterns: chained `.Replace()` (use span enumerator), Split+Join for tail (reverse-scan slice), substring in loops (use span EndsWith), triple-iteration file categorization (single-pass), disk round-trip for search (stream directly), JSON-serialized log strings (plain text + marker). `knownTrailingSegments` should be `static readonly`.
- **PR #13 fixes:** Integer overflow guards on user-controlled arithmetic (cast to `long` before narrowing). Cache-layer range returns `null` for out-of-range (match server semantics). Allocation-free `CountLines` via `AsSpan().Count('\n')`. `SearchValues<char>` for line-break scanning. Shared `StringHelpers.TailLines` in Core. Cache format migration via `raw:` prefix with JSON fallback.
- **PR #14 fixes:** CRLF normalization before Split (prevent `\r` leaking). Trailing empty element handling. Cache sentinel collision fix via NUL byte prefix (`"\0raw\n"`).
- **Version 0.3.0:** AzDO integration, perf optimizations, incremental log support.

📌 Team update (2026-03-09): Test quality guidelines established — no layer duplication in tests, passthrough methods get ≤1 smoke test, interface compliance tests are redundant. ~20 tests cleaned up (PR #15). — decided by Dallas, actioned by Lambert

## MCP Error Surfacing & Message Quality (latest session)

**Exception wrapping in MCP tools:** All AzDO and Helix MCP tool handlers now wrap service-layer exceptions (HttpRequestException, InvalidOperationException, ArgumentException, HelixException) in McpException with the actual error message. Pattern: `catch (Exception ex) when (ex is X or Y) { throw new McpException($"Failed to {action}: {ex.Message}", ex); }`. The MCP SDK only surfaces `McpException.Message` to clients — unhandled exceptions become generic "An error occurred" messages.

**helix_test_results error message:** When no TRX files found, error now filters out noise (hash-named .log files) and highlights useful files (crash dumps, binlogs, XML). Crash artifacts (core.*, *.dmp, *crashdump*) get a ⚠️ callout with search suggestions. When only .log files exist, suggests `helix_search_log` with pattern `'  Failed'`.

**helix_search_log description:** Updated to document substring-based (not regex) matching, literal treatment of metacharacters, and common search patterns.

📌 Team update (2026-03-09): CI profile analysis — 14 recommendations for MCP tool descriptions/error messages. P0: helix_test_results fails for 4/6 repos (no TRX uploads), helix_search_log needs repo-specific patterns, error messages need actionable next steps. Tool description changes in HelixMcpTools.cs, AzdoMcpTools.cs, error messages in HelixService.cs. — decided by Ash

📌 Team update (2025-07-24): Test quality review — ~17 redundant tests identified (AzdoCliCommandTests near-duplicates, interface compliance tests, overlapping filters). Lambert actioned deletions. Test guidelines: no layer duplication, ≤1 passthrough smoke test, prune proactive tests when real tests land. — decided by Dallas

## Learnings (MCP tool description updates with CI knowledge)

- **Updated 5 tool descriptions** across HelixMcpTools.cs (helix_test_results, helix_search_log) and AzdoMcpTools.cs (azdo_test_runs, azdo_test_results, azdo_timeline) to embed repo-specific CI knowledge.
- **Key pattern: warn-before-fail.** helix_test_results now warns that 4/6 major repos don't upload TRX to Helix, directing agents to azdo_test_runs + azdo_test_results instead. This prevents the most common wasted tool call.
- **Repo-specific search patterns in descriptions:** helix_search_log now lists failure patterns per repo (runtime='[FAIL]', aspnetcore/efcore='  Failed', sdk='error MSB', roslyn='aborted'/'Process exited').
- **Trust-but-verify on run counts:** azdo_test_runs description now warns that failedTests=0 can be a lie — always drill into azdo_test_results.
- **Helix task name mapping in azdo_timeline:** runtime/aspnetcore='Send to Helix', sdk='🟣 Run TestBuild Tests', efcore='Send job to helix', roslyn=embedded, VMR=no Helix.
- **helix_ci_guide cross-references:** Both helix_test_results and helix_search_log now point agents to helix_ci_guide for full repo profiles.
