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

## Learnings (PR #14 review fixes, 2025-07-18)

- **CRLF in streamed content:** When bypassing `File.ReadAllLinesAsync` and splitting raw content, always normalize `\r\n`/`\r` to `\n` before splitting — otherwise `\r` leaks into line strings and breaks pattern matching.
- **Trailing empty element on split:** `"content\n".Split('\n')` yields a trailing empty string. Drop it only when `lines.Count > 1` to preserve semantics for single-newline inputs like `"\n"` → `[""]`.
- **Cache sentinel collision:** Plain-text prefixes like `"raw:"` can collide with legitimate log content. Use NUL byte prefixes (`"\0raw\n"`) — NUL can't appear in valid log text, making collision impossible.

📌 Team update (2025-07-18): Fixed 3 PR #14 review comments — CRLF handling in SearchConsoleLogAsync, NormalizeAndSplit edge case for single-newline, cache sentinel collision via NUL prefix — implemented by Ripley
- **Integer overflow in tail optimization:** `tailLines.Value * 2` computed as `int` can overflow for large user-controlled values. Fix: cast to `(long)tailLines.Value * 2` for the comparison, and guard `startLine` arithmetic with bounds check (`> 0 && <= int.MaxValue`) before the `(int)` cast. Falls back to full fetch if values don't fit. Lesson: always consider overflow on user-controlled arithmetic, especially before narrowing casts.
- **ExtractRange clamping vs server semantics:** The original `ExtractRange` clamped out-of-bounds `startLine` to the last line, which differs from AzDO API behavior (returns empty/404 for out-of-range). Fix: return `null` when `start >= lines.Length`, `start < 0`, or `end < start`. Lesson: cache-layer range extraction must match server semantics to avoid behavioral divergence between cached and uncached paths.
- **Allocation-free string line operations:** Replace `string.Split('\n')` with span-based approaches for hot-path methods. `CountLines`: use `content.AsSpan().Count('\n')` (MemoryExtensions.Count) — zero allocation. `ExtractRange`: scan for Nth newline via `IndexOf('\n')` in a loop to find character offsets, then slice with `content[start..end]` — avoids allocating an array of all lines just to extract a small range. Critical for large AzDO logs on delta-refresh paths. Note: must handle content without trailing `\n` (add +1 to count).

## Learnings (performance code review, 2025-07-18)

- **Chained Replace is the #1 perf pattern to watch:** `NormalizeAndSplit()` in AzdoService does `.Replace("\r\n", "\n").Replace("\r", "\n")` creating two intermediate full-size strings before `.Split('\n')` creates a third. In cross-step search this runs up to 30× per request on multi-MB logs. Fix: span-based line enumerator that handles all line ending types in one pass.
- **Split+Join for tail trimming is wasteful:** Both `AzdoService.GetBuildLogAsync` and `HelixService.GetConsoleLogContentAsync` split the entire log into a string[] just to get the last N lines, then Join them back. Fix: reverse-scan for Nth `\n` from end using `ReadOnlySpan<char>.LastIndexOf('\n')`, then slice — zero array allocation.
- **MatchesPattern allocates a substring on every call:** `pattern[1..]` in `MatchesPattern` and `MatchesTestResultPattern` creates a new string. Called per-file in loops (FindFiles scans 30 work items × N files each). Fix: `name.AsSpan().EndsWith(pattern.AsSpan(1), ...)`.
- **Triple-iteration anti-pattern in HelixMcpTools.Files:** Three `.Where().Select().ToList()` chains iterate the file list 3 times with redundant `MatchesPattern`/`IsTestResultFile` calls. Fix: single-pass categorization loop.
- **SearchConsoleLogAsync does disk round-trip unnecessarily:** Downloads log to temp file, then reads it all back with `File.ReadAllLinesAsync`. Could stream directly into memory, avoiding double I/O.
- **CachingAzdoApiClient serializes log content as JSON strings:** `JsonSerializer.Serialize<string>()` on multi-MB log content escapes every special character and wraps in quotes. `Deserialize<string>()` on cache hit re-parses it all. For large logs, store as plain text with a content-type marker instead.
- **Static array allocations on every call:** `knownTrailingSegments` in `HelixIdResolver.TryResolveJobAndWorkItem` allocates `string[]` per invocation. Should be `static readonly`.

📌 Team update (2025-07-18): Perf review identified 17 allocation issues — decided by Ripley

## Learnings (version bump 0.3.0, 2025-07-18)

- Version bump to 0.3.0 for the release containing AzDO integration, perf optimizations, and incremental log support
📌 Team update (2026-03-09): Test quality guidelines established — no layer duplication in tests, passthrough methods get ≤1 smoke test, interface compliance tests are redundant. ~20 tests cleaned up (PR #15). — decided by Dallas, actioned by Lambert

## MCP Error Surfacing & Message Quality (latest session)

**Exception wrapping in MCP tools:** All AzDO and Helix MCP tool handlers now wrap service-layer exceptions (HttpRequestException, InvalidOperationException, ArgumentException, HelixException) in McpException with the actual error message. Pattern: `catch (Exception ex) when (ex is X or Y) { throw new McpException($"Failed to {action}: {ex.Message}", ex); }`. The MCP SDK only surfaces `McpException.Message` to clients — unhandled exceptions become generic "An error occurred" messages.

**helix_test_results error message:** When no TRX files found, error now filters out noise (hash-named .log files) and highlights useful files (crash dumps, binlogs, XML). Crash artifacts (core.*, *.dmp, *crashdump*) get a ⚠️ callout with search suggestions. When only .log files exist, suggests `helix_search_log` with pattern `'  Failed'`.

**helix_search_log description:** Updated to document substring-based (not regex) matching, literal treatment of metacharacters, and common search patterns.
