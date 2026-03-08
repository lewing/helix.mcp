# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Four projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server), HelixTool.Mcp.Tools (MCP tool definitions)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context (summarized through 2026-03-08)

> Older history archived to history-archive.md on 2026-03-08.

**Architecture & DI:** IHelixApiClient with projection interfaces (IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile). Constructor injection on HelixService. DI: CLI via `ConsoleApp.ServiceProvider`, MCP via `builder.Services.AddSingleton<>()`. Two DI containers in CLI (commands + `hlx mcp`). Program.cs has UTF-8 BOM.

**Helix features (P0-P1 complete):** Positional args, dotnet tool packaging, --json flag, namespace cleanup, rich status, download by URL, ConsoleLogUrl, batch status (SemaphoreSlim(5)), URL parsing for optional workItem, structured JSON, stdio MCP transport, hlx_search_file (SearchLines helper, binary detection, 50MB cap), hlx_test_results (TRX+xUnit XML, XXE-safe, auto-discovery via TestResultFilePatterns). Status filter: `failed|passed|all`. MaxBatchSize=50. URL scheme validation (http/https only).

**Cache (2026-02-12):** SQLite-backed (WAL mode, connection-per-operation with `Cache=Shared`), CachingHelixApiClient decorator with TTL matrix, XDG paths, `HLX_CACHE_MAX_SIZE_MB=0` disables. Auth isolation via per-token-hash DB + artifact dirs. Path traversal defense-in-depth: sanitize + `Path.GetFullPath` prefix check. `ValidatePathWithinRoot` must append `DirectorySeparatorChar` to root.

**HTTP/SSE multi-auth (2026-02-13):** IHelixTokenAccessor + IHelixApiClientFactory + ICacheStoreFactory pattern. HttpContextHelixTokenAccessor in HelixTool.Mcp. Scoped DI for HTTP transport. Multi-auth deferred — single-token-per-process sufficient. CacheStoreFactory uses `ConcurrentDictionary<K, Lazy<T>>` for thread-safe single-invocation.

**MCP API refactoring:** FileEntry simplified to `(Name, Uri)`. `FindFilesAsync` generalized from binlog-only. `hlx_batch_status` uses `string[]` (native MCP arrays). All MCP JSON output camelCase via `JsonNamingPolicy.CamelCase`. `UseStructuredContent=true` for all 12 tools (hlx_logs excepted). HelixException → McpException translation in tool handlers.

**Test result parsing:** TRX strict + xUnit XML best-effort. Auto-discovery via TestResultFilePatterns (`*.trx`, `testResults.xml`, `*.testResults.xml.txt`, `testResults.xml.txt`). Single file list query per work item. `IsTestResultFile()` public for CLI tagging.

**AzDO foundation (2026-03-07):** Files in `src/HelixTool.Core/AzDO/`. AzdoModels.cs (sealed records, `[JsonPropertyName]`), IAzdoTokenAccessor (AZDO_TOKEN → az CLI → null), AzdoIdResolver (dev.azure.com + visualstudio.com, no regex), IAzdoApiClient (7 methods), AzdoApiClient (HTTP + Bearer auth, 404→null, 401/403→auth hint). CachingAzdoApiClient with `azdo:` key prefix, dynamic TTL by build status.

**AzDO MCP tools (2026-03-07):** 6 tools with `azdo_` prefix. Context-limiting defaults: tailLines=500, top=20-200, filter="failed". Timeline filtering is client-side (parent chain walk-up). Model types used directly as MCP returns (no wrapper DTOs needed). Cache keys must include all limit/filter parameters.

**AzDO CLI (2026-03-08):** 9 commands mirroring MCP tools. IHttpClientFactory via DI (named clients "HelixDownload" + "AzDO", 5-min timeout). `HttpCompletionOption.ResponseHeadersRead` for streaming.

**Key patterns:**
- `File.Move(overwrite: true)` on Windows: catch both `IOException` and `UnauthorizedAccessException`
- Per-invocation `Guid.NewGuid()` temp dirs to prevent cross-process races
- `WithToolsFromAssembly()` needs explicit assembly arg for referenced libs
- MCP descriptions should expose behavioral contracts, not implementation mechanics

## Learnings (HelixTool.Mcp.Tools extraction)

- **HelixTool.Mcp.Tools project:** New class library at `src/HelixTool.Mcp.Tools/` containing MCP tool definitions (HelixMcpTools, AzdoMcpTools) and MCP result DTOs (McpToolResults.cs). No NuGet packaging metadata — Larry doesn't want library packages published yet.
- **Namespace: `HelixTool.Mcp.Tools`:** All three moved files use this namespace. AzdoMcpTools was previously `HelixTool.Core.AzDO`, now unified under `HelixTool.Mcp.Tools`. AzDO model/service types (`AzdoBuildSummary`, `AzdoService`, etc.) remain in `HelixTool.Core.AzDO`.
- **ModelContextProtocol package removed from Core:** Core no longer references `ModelContextProtocol` — that dependency now lives in `HelixTool.Mcp.Tools`. Core is purely business logic + API client + caching.
- **`IsFileSearchDisabled` promoted to public:** Was `internal static` on `HelixService`, had to become `public` because `HelixMcpTools` moved to a separate assembly. Same visibility pattern as `MatchesPattern` and `IsTestResultFile`.
- **`WithToolsFromAssembly` assembly reference:** Both CLI and HTTP server use `typeof(HelixMcpTools).Assembly` — since HelixMcpTools moved to `HelixTool.Mcp.Tools` assembly, this automatically picks up the right assembly. No code change needed.
- **Test project references all three:** `HelixTool.Tests.csproj` now references Core, Mcp, and Mcp.Tools projects. Six test files needed `using HelixTool.Mcp.Tools;` added.
- **git mv preserves history:** Used `git mv` for all three file moves to maintain blame/log continuity.

## Learnings (azdo_search_log implementation)

- **TextSearchHelper extraction:** `SearchLines()` moved from `HelixService` (private static) to `TextSearchHelper` (public static) in `HelixTool.Core`. The three record types (`LogMatch`, `LogSearchResult`, `FileContentSearchResult`) also promoted to top-level in the `HelixTool.Core` namespace since they're now shared between Helix and AzDO code paths.
- **Default parameter values matter:** Added `contextLines = 0, maxMatches = 50` defaults to `TextSearchHelper.SearchLines()` — existing tests relied on calling with fewer args. When extracting methods to public APIs, always check test call sites for implicit parameter expectations.
- **AzDO log fetching already supports full content:** `AzdoApiClient.GetBuildLogAsync` returns the complete log; `AzdoService.GetBuildLogAsync` adds optional `tailLines` truncation. For search, pass `tailLines: null` to get full content.
- **IsFileSearchDisabled dual-check pattern:** Both the MCP tool layer (throws `McpException`) and the service layer (throws `InvalidOperationException`) check `IsFileSearchDisabled`. The MCP check provides user-friendly errors; the service check is defense-in-depth for CLI consumers.
- **CLI search output pattern:** Context lines displayed with `>>>` prefix for the matching line and `   ` prefix for context. Line numbers right-aligned in a 6-char column.
