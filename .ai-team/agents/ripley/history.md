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

- **NormalizeAndSplit extraction:** Extracted `\r\n` → `\n` normalization + split + trailing-empty-trim into `NormalizeAndSplit()` private static method in `AzdoService`. Both `SearchBuildLogAsync` and `SearchBuildLogAcrossStepsAsync` share this. Pattern: when two methods share identical preprocessing, extract immediately rather than let copy-paste drift.
- **4-bucket ranking algorithm:** Logs ranked by failure likelihood: Bucket 0 (failed/canceled) → Bucket 1 (has issues) → Bucket 2 (succeededWithIssues) → Bucket 3 (succeeded) → Bucket 4 (orphans). Within each bucket, sorted by lineCount descending. This matches how a human would scan — check failed steps first, bigger logs more likely to contain errors.
- **Orphan log detection:** Logs in the logs list but not referenced by any timeline record go into Bucket 4. These are rare but occur in retried builds. Synthetic `AzdoTimelineRecord` created with `Name = $"log:{entry.Id}"` since there's no real timeline data.
- **Early termination has two triggers:** (1) `remainingMatches <= 0` — found enough matches, and (2) `logsSearched >= maxLogsToSearch` — API call budget exhausted. `StoppedEarly` is true if either triggers before exhausting the eligible log queue.
- **Task.WhenAll for metadata:** Timeline and logs list are independent API calls. `Task.WhenAll` fetches both concurrently. Must `await` individual tasks after `WhenAll` to unwrap results (not just use `.Result` which can deadlock in some contexts).
- **CrossStepSearchResult types live in Core AzdoModels.cs:** Same pattern as `TimelineSearchResult` — domain types in Core, MCP tools return them directly. No wrapper DTO needed in McpToolResults.cs.
- **CachingAzdoApiClient dynamic TTL for logs list:** `GetBuildLogsListAsync` uses completed build → 4h, in-progress → 15s, matching the existing pattern for timeline caching (check build completion status first).

📌 Team update (2026-03-08): AzDO search gap analysis consolidated — CI-analysis skill study validated `azdo_search_log` as P0, confirmed `SearchLines()` extraction approach. New P1 ideas: `azdo_search_timeline`, `azdo_search_log_across_steps`. — analyzed by Ash
📌 Team update (2026-03-09): Incremental log fetching full design spec merged — API range support (startLine/endLine), append-on-expire caching (two-key pattern), tail optimization. P0 CountLines off-by-one fixed. 864/864 tests passing. PR #13 opened. — decided by Dallas
📌 Team update (2026-03-09): P0 CountLines off-by-one fix — Split('\n') overcounts by 1 for trailing newline content. Fix: subtract 1 when content ends with '\n'. Affects delta-fetch startLine computation. — decided by Dallas
📌 Team update (2026-03-09): azdo_search_log_across_steps full design spec merged — 4-bucket ranking, early termination, GetBuildLogsListAsync, NormalizeAndSplit extraction. 19 estimated tests. — decided by Dallas

## Learnings (PR #13 review fixes)

- **Integer overflow in tail optimization:** `tailLines.Value * 2` computed as `int` can overflow for large user-controlled values. Fix: cast to `(long)tailLines.Value * 2` for the comparison, and guard `startLine` arithmetic with bounds check (`> 0 && <= int.MaxValue`) before the `(int)` cast. Falls back to full fetch if values don't fit. Lesson: always consider overflow on user-controlled arithmetic, especially before narrowing casts.
- **ExtractRange clamping vs server semantics:** The original `ExtractRange` clamped out-of-bounds `startLine` to the last line, which differs from AzDO API behavior (returns empty/404 for out-of-range). Fix: return `null` when `start >= lines.Length`, `start < 0`, or `end < start`. Lesson: cache-layer range extraction must match server semantics to avoid behavioral divergence between cached and uncached paths.
