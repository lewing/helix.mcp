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

## Learnings (PR #13 review fixes + performance, 2025-07-18)

- **Integer overflow guard:** Cast to `(long)` for user-controlled arithmetic before narrowing to `int`. Falls back to full fetch on overflow.
- **Cache-layer range must match server semantics:** Return `null` for out-of-range, don't clamp.
- **Allocation-free string line ops:** `content.AsSpan().Count('\n')` for CountLines; `IndexOf('\n')` loop + slice for ExtractRange. Critical for large log delta-refresh.
- **SearchValues<char> for line-break scanning:** `SearchValues.Create("\r\n")` + `IndexOfAny` handles all line endings in one pass without pre-normalizing.
- **Shared StringHelpers.TailLines:** Reverse-scan `LastIndexOf('\n')` in Core. Guard empty string (pos+1 exceeds length).
- **SearchConsoleLogAsync refactor safe:** Only download feature and SearchFileAsync use disk path. Search can stream directly.
- **Cache format migration via sentinel prefix:** `raw:` prefix with `JsonSerializer.Deserialize<string>()` fallback for legacy entries. Zero-downtime, natural TTL expiry handles transition.
- **Test assertions must match serialization format:** Tests asserting exact stored values need updating on format changes; `Arg.Any<string>()` is resilient.
- **Key perf anti-patterns:** Chained `.Replace()` (span enumerator instead), Split+Join for tail (reverse-scan slice), `pattern[1..]` substring in loops (span EndsWith), triple-iteration file categorization (single-pass), disk round-trip for search (stream directly), JSON-serialized log strings (plain text + marker).

📌 Team update (2025-07-18): Perf review (17 issues) + 8 fixes implemented (3 P0, 5 P1), all 864 tests passing — decided/implemented by Ripley

📌 Team update (2026-03-09): 3 perf decisions merged — raw: cache prefix, SearchConsoleLog decoupling, shared StringHelpers — decided by Ripley
