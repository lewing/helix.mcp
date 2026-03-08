# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context (summarized through 2026-03-08)

> Older history archived to history-archive.md on 2026-03-08.

**Architecture & DI:** IHelixApiClient with projection interfaces (IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile). Constructor injection on HelixService. DI: CLI via `ConsoleApp.ServiceProvider`, MCP via `builder.Services.AddSingleton<>()`. Two DI containers in CLI (commands + `hlx mcp`). Program.cs has UTF-8 BOM.

**Helix features (P0-P1 complete):** Positional args, dotnet tool packaging, --json flag, namespace cleanup, rich status, download by URL, ConsoleLogUrl, batch status (SemaphoreSlim(5)), URL parsing for optional workItem, structured JSON, stdio MCP transport, hlx_search_file (SearchLines helper, binary detection, 50MB cap), hlx_test_results (TRX+xUnit XML, XXE-safe, auto-discovery via TestResultFilePatterns). Status filter: `failed|passed|all`. MaxBatchSize=50. URL scheme validation (http/https only).

**Cache (2026-02-12):** SQLite-backed (WAL mode, connection-per-operation with `Cache=Shared`), CachingHelixApiClient decorator with TTL matrix, XDG paths, `HLX_CACHE_MAX_SIZE_MB=0` disables. Auth isolation via per-token-hash DB + artifact dirs. Path traversal defense-in-depth: sanitize + `Path.GetFullPath` prefix check. `ValidatePathWithinRoot` must append `DirectorySeparatorChar` to root.

**HTTP/SSE multi-auth (2026-02-13):** IHelixTokenAccessor + IHelixApiClientFactory + ICacheStoreFactory pattern. HttpContextHelixTokenAccessor in HelixTool.Mcp. Scoped DI for HTTP transport. Multi-auth deferred — single-token-per-process sufficient.

**MCP API refactoring:** FileEntry simplified to `(Name, Uri)`. `FindFilesAsync` generalized from binlog-only. `hlx_batch_status` uses `string[]` (native MCP arrays). All MCP JSON output camelCase via `JsonNamingPolicy.CamelCase`. `UseStructuredContent=true` for all 12 tools (hlx_logs excepted).

**Key patterns:**
- `File.Move(overwrite: true)` on Windows: catch both `IOException` and `UnauthorizedAccessException`
- Use `FileStream(..., FileShare.ReadWrite | FileShare.Delete)` for concurrent access
- Per-invocation `Guid.NewGuid()` temp dirs to prevent cross-process races
- `WithToolsFromAssembly()` needs explicit assembly arg for referenced libs
- MCP descriptions should expose behavioral contracts, not implementation mechanics

## Learnings (2026-03-01 onwards)

- **HelixException → McpException translation:** MCP tool handlers must catch `HelixException` and rethrow as `McpException`. Root cause of issue #4.
- **xUnit XML parsing:** `ParseXunitFile` handles `<assemblies>/<assembly>/<collection>/<test>`. `DetectTestFileFormat` inspects root element (`<TestRun>` = TRX, `<assemblies>`/`<assembly>` = xUnit). Both reuse `TrxParseResult`/`TrxTestResult`.
- **Two-tier XML parsing strategy:** TRX strict (XmlException propagates), XML fallback best-effort (XmlException caught per-file). xUnit outcome normalization: `Pass/Fail/Skip` → `Passed/Failed/Skipped`.

## Learnings (2026-03-07: Real Helix test result file patterns)

- **Real-world file patterns discovered:** Runtime CoreCLR tests upload `{workitem}.testResults.xml.txt` to the regular files list (NOT testResults category). iOS/XHarness uploads `testResults.xml`. SDK and ASP.NET Core tests do NOT upload results to Helix blob storage (consumed locally by Azure Pipelines reporter).
- **TestResultFilePatterns array:** `*.trx`, `testResults.xml` (exact), `*.testResults.xml.txt`, `testResults.xml.txt` (exact). Defined as `internal static readonly string[]` on HelixService. Order is priority for documentation but all matching files are returned.
- **Single file list query:** `ParseTrxResultsAsync` now queries `ListWorkItemFilesAsync` once and matches all patterns, instead of issuing separate `DownloadFilesAsync` calls per pattern (which each queried the file list again). Reduces API calls from 2-3 to 1.
- **All matching files returned:** Changed from "first matching pattern wins" to "return all discovered test result files." TRX files parsed strictly, everything else parsed best-effort. This is more useful when a work item has both formats.
- **IsTestResultFile() is public:** Needed by CLI (Program.cs) for file tagging. Uses exact-name and suffix matching (not substring like `MatchesPattern`).
- **DownloadMatchingFilesAsync helper:** Downloads specific `IWorkItemFile` instances directly, bypassing the pattern-matching in `DownloadFilesAsync`. Uses same security (SanitizePathSegment + ValidatePathWithinRoot) and per-invocation temp dir isolation.
- **Error message improvement (P0 #4):** When no test results found, error now lists searched patterns AND available files (up to 10 names), guiding users to understand why nothing was found.

## Learnings (CacheStoreFactory Lazy<T> fix)

- **ConcurrentDictionary.GetOrAdd race condition:** `GetOrAdd(key, factory)` does NOT guarantee single-invocation of the factory for a given key. Under contention, the factory can be called multiple times concurrently for the same key (only one result is stored, others are discarded). This is a documented .NET behavior. When the factory has side effects (like opening a SQLite DB and running `InitializeSchema()`), the concurrent invocations race on the same file, causing `ArgumentOutOfRangeException` from SQLitePCL on Windows.
- **Fix pattern — Lazy<T> wrapping:** Change `ConcurrentDictionary<string, T>` to `ConcurrentDictionary<string, Lazy<T>>` and access `.Value` on return. `Lazy<T>` with default `LazyThreadSafetyMode.ExecutionAndPublication` guarantees the factory runs exactly once, even under contention. This is the standard .NET pattern for single-invocation semantics with `ConcurrentDictionary`.
- **Dispose with Lazy<T>:** When disposing a `ConcurrentDictionary<K, Lazy<T>>`, check `lazy.IsValueCreated` before accessing `.Value` — avoids needlessly triggering lazy initialization during cleanup.

## Learnings (2026-03-07: AzDO Foundation)

- **AzDO files live in `src/HelixTool.Core/AzDO/`** — separate folder under Core, not a new project. Namespace: `HelixTool.Core.AzDO`.
- **AzdoModels.cs:** All AzDO REST API v7.0 DTOs as `sealed record` types with `[JsonPropertyName]` for camelCase mapping. `AzdoListResponse<T>` wraps list endpoints. `AzdoBuildFilter` is client-side only (not deserialized from API). `AzdoTriggerInfo` uses `[JsonPropertyName("ci.message")]` and `[JsonPropertyName("pr.number")]` for dotted key names.
- **IAzdoTokenAccessor.cs:** Async interface (`Task<string?>`) unlike Helix's sync `IHelixTokenAccessor`. Auth chain: `AZDO_TOKEN` env → `az account get-access-token` → null. Token cached after first `az` call. Does NOT use `Azure.Identity` — MSAL breaks on WSL (libsecret/D-Bus, arcade-services#6060).
- **AzdoIdResolver.cs:** Static helper like `HelixIdResolver`. Supports dev.azure.com and *.visualstudio.com URL formats. Uses `Uri.TryCreate` + `HttpUtility.ParseQueryString` — no regex (ReDoS-safe, project security invariant). Defaults: org=`dnceng-public`, project=`public`. Provides both throwing `Resolve()` and non-throwing `TryResolve()`.
- **`AzCliAzdoTokenAccessor` uses `Process.Start`** with redirected stdout, `CreateNoWindow=true`, catches all exceptions to return null on failure (enables anonymous access for public repos).
- **IAzdoApiClient.cs:** 7-method interface mirroring IHelixApiClient pattern — GetBuild, ListBuilds, GetTimeline, GetBuildLog, GetBuildChanges, GetTestRuns, GetTestResults. All take `(org, project, ...)` + `CancellationToken`.
- **AzdoApiClient.cs:** HTTP implementation using `HttpClient` + `IAzdoTokenAccessor`. Base URL: `https://dev.azure.com/{org}/{project}/_apis/`. All requests use `api-version=7.0`. Bearer auth when token is non-null. `System.Text.Json` with `PropertyNameCaseInsensitive=true`. Error handling: 401/403 → auth-failed message, 404 → null/empty list, other errors → `HttpRequestException` with status code + body snippet. List endpoints unwrap `AzdoListResponse<T>.Value`. Log endpoint reads plain text (not JSON). Test runs queried via `buildUri=vstfs:///Build/Build/{id}`. Test results default to `outcomes=Failed` with `$top=1000`.

## Learnings (2026-03-07: AzDO Caching)

- **CachingAzdoApiClient:** Decorator over `IAzdoApiClient`, follows `CachingHelixApiClient` pattern. Uses `ICacheStore.GetMetadataAsync`/`SetMetadataAsync` for JSON round-tripping (string-based). No DTO layer needed — AzDO model types are `sealed record` with `[JsonPropertyName]`, directly serializable unlike Helix's interface-based types.
- **Cache key format:** `azdo:{org}:{project}:{type}:{id}` with `azdo:` prefix to namespace from Helix entries. Org and project segments sanitized via `CacheSecurity.SanitizeCacheKeySegment`. Build state keys use `azdo-build:` prefix for `IsJobCompletedAsync`/`SetJobCompletedAsync`.
- **Dynamic TTL matrix:** Completed builds 4h, in-progress 15s. Timelines NEVER cached while running (skips cache entirely). Logs/changes 4h (immutable). Build lists 30s. Test runs/results 1h.
- **Build completion tracking:** Reuses `ICacheStore.IsJobCompletedAsync`/`SetJobCompletedAsync` with composite key `azdo-build:{org}:{project}:{buildId}`. `GetBuildAsync` populates this state on cache miss. `GetTimelineAsync` and future status-dependent methods query it before deciding whether to cache.
- **Filter hashing for ListBuildsAsync:** SHA256 hash (12-char hex) of `PrNumber|Branch|DefinitionId|Top|StatusFilter` concatenation. Deterministic, collision-resistant.
- **ICacheStore interface:** `GetMetadataAsync(key)` → `string?`, `SetMetadataAsync(key, jsonValue, ttl)`, `GetArtifactAsync(key)` → `Stream?`, `SetArtifactAsync(key, stream)`, `IsJobCompletedAsync(jobId)` → `bool?`, `SetJobCompletedAsync(jobId, completed, ttl)`. The "jobId" parameter is just a string key — works for any entity, not just Helix jobs.

📌 Team update (2026-03-07): Auth UX Phase 1 approved — hlx login + git credential storage + ChainedHelixTokenAccessor. 7 work items assigned for implementation. — decided by Dallas
📌 Team update (2026-03-07): XXE test regression — ParseTrx_RejectsXxeDtdDeclaration fails after xUnit XML refactor. Review DetectTestFileFormat for DtdProcessing.Prohibit. — flagged by Lambert
📌 Team update (2026-03-07): AzDO test patterns — Lambert wrote 55 tests, identified edge cases: negative buildIds accepted, TryResolve out-params default to DefaultOrg/DefaultProject, _resolved flag not thread-safe. — documented by Lambert

## Learnings (2026-03-07: AzdoService)

- **AzdoService pattern:** Thin business-logic layer between MCP tools and `IAzdoApiClient`. Constructor takes `IAzdoApiClient` (will be `CachingAzdoApiClient` at runtime via DI). Every method accepting `buildIdOrUrl` resolves via `AzdoIdResolver.Resolve()` to extract `(org, project, buildId)`.
- **AzdoBuildSummary record:** Added to `AzdoModels.cs`. Flattens nested `AzdoBuild` fields (definition name/id, requestedFor display name) and computes `Duration` and `WebUrl`. MCP tools consume this directly.
- **GetTestResultsAsync uses buildIdOrUrl for org/project:** Since `runId` is scoped to org/project (not globally unique), we resolve org/project from the build reference. The `buildId` itself is discarded (`_`).
- **Tail lines in GetBuildLogAsync:** Splits on `\n` and uses range operator `lines[^tailLines.Value..]`. Handles null/zero/negative tailLines by returning full content.
- **Service doesn't format JSON:** That's the MCP tool layer's job. Service returns typed records and collections.
- **No exception wrapping yet:** Unlike HelixService which wraps `HttpRequestException` → `HelixException`, AzdoService currently lets exceptions propagate. We'll add `AzdoException` when the MCP tool layer needs it.

## Learnings (2026-03-07: AzdoMcpTools)

- **MCP tool registration pattern:** Annotate the class with `[McpServerToolType]`, each method with `[McpServerTool(Name = "...", Title = "...", ReadOnly = true, UseStructuredContent = true)]` and `[Description("...")]`. Constructor takes the service (DI-injected). The MCP SDK auto-discovers tools via `WithToolsFromAssembly()`.
- **UseStructuredContent with model types:** When `UseStructuredContent = true`, return types are serialized to JSON by the MCP SDK. AzDO model types (`AzdoBuild`, `AzdoTimeline`, etc.) already have `[JsonPropertyName]` attributes from API deserialization, so they serialize correctly for MCP output too. No need for separate McpToolResults wrapper types when the API models already have proper JSON attributes.
- **Plain text tool (azdo_log):** For text content, omit `UseStructuredContent = true` and return `string` directly — matches the `hlx_logs` pattern.
- **Tool naming convention:** AzDO tools use `azdo_` prefix (vs `hlx_` for Helix). Names are snake_case, titles are human-readable with "AzDO" prefix.
- **Tool descriptions for agents:** Descriptions should explain what data is returned, when to use the tool, how it relates to other tools (e.g., "Use after azdo_timeline to read logs"), and parameter format notes (e.g., "accepts build URL or plain integer ID").

📌 Team update (2026-03-08): AzDO security review — SEC-1 (Medium) prNumber query injection must be fixed with int.TryParse before merge. SEC-2/3/4 (Low) non-blocking follow-ups. — decided by Dallas

## Learnings (AzDO Context-Limiting Defaults)

- **Context-limiting patterns for AzDO tools:** All 6 AzDO MCP tools now have safe output-size defaults matching the Helix tool patterns. Defaults live in MCP tool method signatures (not buried in service/client code).
- **Parameter defaults chosen:** `azdo_log` tailLines=500 (matches hlx_logs), `azdo_changes` top=20, `azdo_test_runs` top=50, `azdo_test_results` top=200 (matches hlx_test_results, was hardcoded 1000), `azdo_timeline` filter="failed". All remain overridable by callers.
- **Timeline filtering is client-side:** AzDO API doesn't support filtering timeline records, so the "failed" filter works client-side: identify non-succeeded records + records with issues, then walk up parentId chain to include ancestors for hierarchical context.
- **Cache key must include limit parameters:** When adding `top` parameters, cache keys in CachingAzdoApiClient must incorporate the limit value to avoid serving stale partial results from a different limit request.

## Learnings (AzDO Build Artifacts & Test Attachments)

- **Build artifacts endpoint:** `GET _apis/build/builds/{buildId}/artifacts` returns `AzdoListResponse<AzdoBuildArtifact>`. Each artifact has `name` and a nested `resource` with `type`, `data`, `downloadUrl`, `url`. Artifacts are immutable once published — cached with `ImmutableTtl` (4h).
- **Test attachments endpoint:** `GET _apis/test/runs/{runId}/results/{resultId}/attachments` returns `AzdoListResponse<AzdoTestAttachment>`. Each attachment has `fileName`, `size`, `comment`, `url`, `createdDate`. Cached with `TestTtl` (1h).
- **Model records created:** `AzdoBuildArtifact`, `AzdoArtifactResource`, `AzdoTestAttachment` — all `sealed record` with `[JsonPropertyName]` attributes, added to `AzdoModels.cs`.
- **Service-level `top` limiting for attachments:** The AzDO attachments API doesn't support `$top`, so the service applies `Take(top)` client-side. Artifacts typically have few entries so no `top` needed.
- **MCP tool `azdo_test_attachments` takes explicit org/project:** Unlike `azdo_test_results` which resolves org/project from a buildIdOrUrl, attachments use explicit `org`/`project` params since there's no build context — only `runId` + `resultId`.

📌 Team update (2026-03-08): AzDO artifacts must follow Helix patterns — caching and search patterns (hlx_files, hlx_find_files, hlx_search_file, hlx_download) must be mirrored for AzDO tools. — decided by Larry Ewing

## Learnings (AzDO Artifact Pattern Filtering & Cache Key Fix)

- **Pattern matching reuses `HelixService.MatchesPattern`:** AzDO artifact filtering uses the same static method as Helix — `*` matches all, `*.ext` matches suffix, else case-insensitive substring. No regex (ReDoS-safe, project security invariant). The method lives on `HelixService` and is referenced cross-namespace by `AzdoService`.
- **Cache key parameter patterns:** Every limit/filter parameter that changes the cached result set MUST be included in the cache key. The `GetBuildChangesAsync` pattern `changes:{buildId}:{top}` is the reference. `GetTestAttachmentsAsync` was fixed from `testattachments:{runId}:{resultId}` to `testattachments:{runId}:{resultId}:{top}`. When adding new parameters to any cached method, always check the corresponding `CachingAzdoApiClient` cache key.
- **Top parameter threading for test attachments:** The `top` parameter was threaded through `IAzdoApiClient` → `AzdoApiClient` → `CachingAzdoApiClient` → `AzdoService`. The AzDO API itself doesn't support `$top` for attachments, but the parameter is needed at the caching layer for correct cache key generation. The service layer applies `Take(top)` client-side.

📌 Team update (2026-03-08): AzDO artifact/attachment test patterns — 33 tests added, caching decisions: artifacts 4h (immutable), attachments 1h (test-scoped). 700 total tests. — documented by Lambert

📌 Team update (2026-03-08): AzDO docs use subsections within existing README structure — new API domains should follow this pattern (### under ## MCP Tools, ## Authentication, ## Caching). llmstxt updated with AzDO tools subsection. — decided by Kane

## Learnings (2026-03-08: SEC-2/3/4 + AzDO CLI)

- **IHttpClientFactory pattern (SEC-2):** Replaced static `HttpClient s_httpClient` in HelixService with constructor-injected `HttpClient`. Constructor signature: `HelixService(IHelixApiClient api, HttpClient? httpClient = null)` — optional parameter preserves test compatibility (17 test files construct HelixService with one arg). Production code uses `IHttpClientFactory.CreateClient("HelixDownload")` via DI. AzdoApiClient already accepted HttpClient via constructor — DI registrations updated from `new HttpClient()` to factory-sourced. Named clients: `"HelixDownload"` and `"AzDO"`.
- **Three DI containers updated:** Top-level CLI (ServiceCollection), `Mcp()` command (Host.CreateApplicationBuilder), and HelixTool.Mcp (WebApplication.CreateBuilder). Each registers `AddHttpClient("HelixDownload", ...)` and `AddHttpClient("AzDO", ...)` with 5-minute timeout.
- **Microsoft.Extensions.Http package:** Added to HelixTool.csproj (v10.0.0). ASP.NET Core MCP project has it transitively.
- **Streaming (SEC-3):** AzdoApiClient.GetBuildLogAsync, GetAsync<T>, and GetListAsync<T> all now use `HttpCompletionOption.ResponseHeadersRead` for streaming. Log download streams via `StreamReader.ReadToEndAsync` instead of `ReadAsStringAsync`. Error paths (`ThrowOnUnexpectedError`) only read body on failure, so streaming is not defeated on success path.
- **Timeout (SEC-4):** 5-minute timeout configured on named HttpClients in DI registration. Applies to all HelixService URL downloads and all AzDO API calls.
- **AzDO CLI commands:** 9 commands in `AzdoCommands` class: `azdo-build`, `azdo-builds`, `azdo-timeline`, `azdo-log`, `azdo-changes`, `azdo-test-runs`, `azdo-test-results`, `azdo-artifacts`, `azdo-test-attachments`. Registered via `app.Add<AzdoCommands>()`. Each mirrors its MCP tool counterpart. Human-readable output with color coding; `--json` flag for structured JSON. Timeline filtering logic duplicated from MCP tools (client-side failed filter with parent chain walk-up).
- **FormatDuration and FormatBytes are internal static on Commands:** AzdoCommands references them as `Commands.FormatDuration()` and `Commands.FormatBytes()` — avoids duplication.
- **llmstxt updated:** Added AzDO CLI Commands section listing all 9 commands with parameter documentation.
