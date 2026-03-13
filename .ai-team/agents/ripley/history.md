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

**Archive refresh (2026-03-13):** Detailed notes for CI-knowledge description updates, 9-repo profile expansion, PR #16 review fixes, Option A restructuring, review-fix/security follow-ups, and discoverability routing moved to `history-archive.md`. Durable rules: keep repo-specific guidance in `helix_ci_guide`, preserve the `HelixTool.Core.Helix` / `HelixTool.Core.Cache` split with shared `StringHelpers`, use exact Ordinal root-boundary checks, and prefer explicit fallback routing over composite tools.

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

## Learnings (helix_test_results → helix_parse_uploaded_trx rename)

## Learnings (MCP tool description tightening)

- **Tightened 17 tool descriptions** across HelixMcpTools.cs (4), AzdoMcpTools.cs (12), and CiKnowledgeTool.cs (1). Total word reduction: ~550 words removed from tool-level Description() attributes.
- **Stripped all repo-specific patterns** from tool descriptions (e.g., `runtime uses '[FAIL]'`, Helix task name mappings per repo). That guidance now lives exclusively in `helix_ci_guide` responses.
- **Preserved critical steering hints:** azdo_test_runs inaccurate counts warning, azdo_test_results "primary tool" routing, helix_search_log "not regex" + ci_guide pointer, macios/android devdiv warning.
- **Rule of thumb:** Tool descriptions are loaded into every agent context — every word costs tokens on every session. Repo-specific knowledge belongs in helix_ci_guide, not tool descriptions.

- **Renamed MCP tool** from `helix_test_results` to `helix_parse_uploaded_trx` and CLI command from `test-results` to `parse-uploaded-trx`.
- **Reason:** The generic name `helix_test_results` was a context trap — agents reached for it first on every CI investigation even though 95%+ of dotnet repos publish results to AzDO, not as TRX files in Helix. Wasted tool calls on every investigation.
- **Updated description** to steer agents toward `azdo_test_results` first, making clear this tool only works for repos that upload raw result files to Helix (runtime CoreCLR, XHarness device tests).
- **Files with old name references that were updated:** HelixMcpTools.cs (MCP tool registration), CiKnowledgeTool.cs (helix_ci_guide description), Program.cs (CLI command + help text), CiKnowledgeService.cs (25+ references in repo profiles and formatting), README.md (tool table + tips), CiKnowledgeServiceTests.cs (3 assertions), HelixMcpToolsTests.cs (2 assertions + test name).
- **Internal method names unchanged:** `ParseTrxResultsAsync`, `TestResults` method name in HelixMcpTools, `IsTestResultFile` — these are implementation details, not agent-facing.

## Learnings (helix_search_log → helix_search rename)

- **Renamed MCP-visible tool name** from `helix_search_log` to `helix_search` because the tool now searches both console logs and uploaded files, so the broader name better matches its scope and improves agent discoverability.
- **Kept implementation and CLI names stable:** the `SearchLog` C# method and `search-log` CLI command remain unchanged; only MCP-visible and human-facing strings moved to `helix_search`.
- **Key file paths changed:** `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs`, `src/HelixTool/Program.cs`, `src/HelixTool.Core/CiKnowledgeService.cs`, `src/HelixTool.Core/Helix/HelixService.cs`, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs`, `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`, and `README.md`.

## Learnings (default-limit bump and truncation metadata)

- **Shared log-search truncation belongs in `LogSearchResult`.** Adding `Truncated` at the shared `TextSearchHelper.SearchLines` layer fixed Helix console-log MCP responses and also made single-log AzDO CLI/MCP responses surface early-stop state without duplicating search logic.
- **Raw-list MCP outputs can gain metadata without losing list semantics in tests.** A `LimitedResults<T>` wrapper that implements `IReadOnlyList<T>` but uses a custom JSON converter lets MCP emit `{ results, truncated, note }` while existing direct C# callers still use `Count`, indexers, and `Assert.Single/Empty` naturally.
- **When defaults change, sync all agent-facing surfaces together.** For these tools that meant MCP parameter defaults, CLI command defaults, XML/help text in `Program.cs`, and human-facing stop-early hints so agents and humans get the same expectations.
## Learnings (AzDO Azure.Identity credential chain)

- **AzDO auth chain order is now** `AZDO_TOKEN` → `AzureCliCredential` → `az account get-access-token` subprocess → anonymous. `DefaultAzureCredential` stays out of the path to avoid slow probing/timeouts.
- **`IAzdoTokenAccessor` now returns `AzdoCredential` metadata** so callers can distinguish `Bearer` vs `Basic` and surface a safe auth source string in errors.
- **PAT handling is pre-encoded at the accessor boundary:** non-JWT `AZDO_TOKEN` values are treated as PATs and converted to the Basic header payload for `:{pat}`. `AzdoApiClient` just applies the declared scheme.
- **`AzdoCredential.DisplayToken` exists for compatibility** so older string-based tests/mocks still compile and compare human-readable token values while `Token` remains the on-wire header payload.
- **AzDO auth failures now include request context** (`org/project` plus credential source) so 401/403 errors explain which auth path was attempted and how to recover.
- **Validation:** `dotnet build HelixTool.slnx --nologo` passes, and the AzDO-focused test slice passes with serial xUnit runsettings because the env-var auth tests mutate global process state.
