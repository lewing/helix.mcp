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

📌 Team updates (2026-03-08–09): AzDO search, incremental log (PR #13), search_across_steps spec, test quality cleanup (PR #15). — Ash/Dallas/Lambert

## Learnings (perf & review fixes)

- **Perf review (2025-07-18):** 17 allocation issues, 8 fixed — chained `.Replace()`, Split+Join, substring-in-loops, disk round-trip for search, JSON-serialized log strings.
- **PR #13/14 fixes:** Integer overflow guards, allocation-free `CountLines`, `SearchValues<char>`, cache `raw:` prefix migration, CRLF normalization, sentinel collision fix via NUL byte.
- **Version 0.3.0:** AzDO integration, perf optimizations, incremental log support.

## Learnings (MCP error surfacing)

- **McpException wrapping pattern:** `catch (Exception ex) when (ex is X or Y) { throw new McpException($"Failed to {action}: {ex.Message}", ex); }` — MCP SDK only surfaces McpException.Message; unhandled → generic error.
- **helix_test_results "no TRX" error:** Filters noise, highlights crash dumps with ⚠️, suggests `helix_search_log` as fallback.
- **helix_search_log description:** Documents substring-based (not regex) matching with common search patterns.

📌 Team update (2026-03-09): CI profile analysis — 14 tool description/error message recommendations. — Ash

📌 Team update (2025-07-24): Test quality review — ~17 redundant tests deleted, no layer duplication rule. — Dallas

## Learnings (MCP tool description updates with CI knowledge)

- **5 tool descriptions updated** with repo-specific CI knowledge (helix_test_results, helix_search_log, azdo_test_runs, azdo_test_results, azdo_timeline).
- **warn-before-fail pattern:** helix_test_results warns that 4/6 repos don't upload TRX, directing to azdo_test_runs/results.
- **Repo-specific search patterns:** runtime=`[FAIL]`, aspnetcore/efcore=`  Failed`, sdk=`error MSB`, roslyn=`aborted`/`Process exited`.
- **azdo_test_runs:** failedTests=0 can lie — always drill into results. azdo_timeline includes Helix task name mapping per repo.

## Learnings (CiKnowledgeService enrichment — 9-repo knowledge base)

- **CiRepoProfile expanded** with 9 new properties (PipelineNames, OrgProject, ExitCodeMeanings, etc.). Init-only defaults for backward compat. 3 new repos: maui (3 pipelines), macios (devdiv, NUnit), android (devdiv, NUnit+xUnit). Total: 9.
- **devdiv org repos need ⚠️ warnings** — standard helix_*/ado-dnceng-* tools don't work for macios/android.
- **MAUI is unique:** 3 separate pipelines with different investigation approaches. Pipeline identity matters for tool selection.
- **FormatProfile/GetOverview enriched** with org, pipelines, gotchas, exit codes, investigation order columns.

📌 Team update (2026-03-10): CiKnowledgeService enrichment (9 repos, 9 new properties, 171 tests, PR #16). — Ripley

## Learnings (PR #16 review comment fixes)

- **Try-block indentation:** Re-indent body when wrapping in try. Caught in 4 HelixMcpTools methods.
- **McpException must include inner exception and "Failed to" prefix.** Three AzDO search catch blocks were dropping context.
- **Error messages: don't hardcode one repo's pattern.** Use multiple patterns + point to `helix_ci_guide`.
- **Bool→string for nuanced semantics.** `UploadsTestResultsToHelix: bool` → `HelixTestResultAvailability: string` (`"none"`, `"partial"`, `"varies"`).
- **When renaming record properties, update test assertions too.**

## Learnings (Option A folder restructuring)

- **Moved 9 Helix files** from `Core/` root to `Core/Helix/`: HelixService, HelixApiClient, IHelixApiClient, IHelixApiClientFactory, HelixIdResolver, HelixException, IHelixTokenAccessor, ChainedHelixTokenAccessor, plus CachingHelixApiClient from `Cache/`. Namespace: `HelixTool.Core` → `HelixTool.Core.Helix`.
- **Added `HelixTool.Core.Cache` namespace** to 6 cache infrastructure files in `Cache/`: SqliteCacheStore, ICacheStore, ICacheStoreFactory, CacheOptions, CacheSecurity, CacheStatus.
- **Extracted `MatchesPattern` and `IsFileSearchDisabled`** from HelixService to `StringHelpers.cs`. HelixService methods now delegate to StringHelpers. AzdoService, HelixMcpTools, and AzdoMcpTools updated to call StringHelpers directly — breaking the AzDO→Helix coupling.
- **Moved MCP tools**: HelixMcpTools → `Mcp.Tools/Helix/`, AzdoMcpTools → `Mcp.Tools/AzDO/`.
- **Moved 24 Helix-specific test files** to `Tests/Helix/`. Kept shared tests (cache, security, CI knowledge, text search, API middleware) at root.
- **HelixService.cs needed `using HelixTool.Core.Cache;`** — it references `CacheSecurity` for path validation. Initial build failed until this was added. Key learning: when splitting namespaces within the same project, intra-project cross-namespace references are easy to miss.
- **StringHelpers changed from `internal` to `public`** to support cross-project access (MCP tools project references it).
- **59 files touched**, 0 behavioral changes, all 1038 tests pass.

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)
