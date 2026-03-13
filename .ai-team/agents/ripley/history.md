# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Four projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server), HelixTool.Mcp.Tools (MCP tool definitions)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context

- **Implementation layout:** service code lives under `src/HelixTool.Core/Helix/` and `src/HelixTool.Core/AzDO/`; MCP tool definitions live under `src/HelixTool.Mcp.Tools/Helix/` and `src/HelixTool.Mcp.Tools/AzDO/`.
- **Cache/search primitives:** `CachingHelixApiClient`, `CachingAzdoApiClient`, `StringHelpers`, and `TextSearchHelper` are the shared implementation seams; `HLX_CACHE_MAX_SIZE_MB=0` disables caching and `HLX_DISABLE_FILE_SEARCH` disables file-content search.
- **Wire-format conventions:** structured MCP tools use `UseStructuredContent=true`, camelCase JSON/property names remain stable, and descriptions stay behavior-first with repo-specific guidance routed to `helix_ci_guide`.
- **Auth/runtime:** Helix auth remains env-var based, while AzDO auth now uses the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous with metadata carried by `AzdoCredential`.

## Learnings (azdo_search_log_across_steps implementation)

**Archive refresh (2026-03-13):** Detailed notes for CI-knowledge description updates, 9-repo profile expansion, PR #16 review fixes, Option A restructuring, review-fix/security follow-ups, and discoverability routing moved to `history-archive.md`. Durable rules: keep repo-specific guidance in `helix_ci_guide`, preserve the `HelixTool.Core.Helix` / `HelixTool.Core.Cache` split with shared `StringHelpers`, use exact Ordinal root-boundary checks, and prefer explicit fallback routing over composite tools.

- **NormalizeAndSplit extraction:** When two methods share identical preprocessing, extract immediately.
- **4-bucket ranking:** Logs ranked by failure likelihood (failed → issues → succeededWithIssues → succeeded → orphans), largest first. Matches human scan order.
- **Early termination:** Two triggers — match budget exhausted OR log search budget exhausted.
- **Task.WhenAll for metadata:** Timeline + logs list fetched concurrently. Always `await` individual tasks after WhenAll.
- **CachingAzdoApiClient dynamic TTL for logs list:** Completed → 4h, in-progress → 15s.

📌 Team updates (2026-03-08–09): AzDO search, incremental log (PR #13), search_across_steps spec, test quality cleanup (PR #15). — Ash/Dallas/Lambert

## Learnings (perf & review fixes)

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

## Learnings (security boundary and DI review fixes)

- **Path boundary checks:** For security-sensitive root containment, normalize both paths, preserve the root boundary with `Path.TrimEndingDirectorySeparator(...) + Path.DirectorySeparatorChar`, and compare with `StringComparison.Ordinal`; ignore-case prefix checks can admit case-variant sibling paths on case-sensitive filesystems.
- **HelixService constructor contract:** `HelixService` should require an injected `HttpClient` and null-guard both constructor dependencies instead of silently allocating a fallback transport.
- **User preference:** Code-review follow-up fixes should stay surgical, behavior-safe, and avoid unrelated refactoring.
- **Key file paths:** `src/HelixTool.Core/Cache/CacheSecurity.cs` contains cache/download path traversal guards. `src/HelixTool.Core/Helix/HelixService.cs` owns direct URL download behavior and now depends on caller-provided `HttpClient`. `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` are the production DI registration points for `HelixService`.

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash

## Learnings (discoverability routing pass)

- **Behavioral routing beats vague warnings:** For tool-selection surfaces, state when a tool works, when to skip it, and the exact fallback path instead of saying it may fail.
- **Guide ordering pattern:** A short `Start Here` section before gotchas and inventory makes repo-specific workflow choice discoverable without reading the entire CI profile.
- **User preference:** Keep discoverability improvements incremental; do not add composite tools or new parameters when wording/order changes can solve the workflow gap.
- **Key file paths:** `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` holds MCP tool descriptions, `src/HelixTool.Core/Helix/HelixService.cs` owns `helix_test_results` fallback messaging, `src/HelixTool.Core/CiKnowledgeService.cs` formats repo-specific CI guides, `src/HelixTool.Mcp.Tools/CiKnowledgeTool.cs` describes `helix_ci_guide`, and `src/HelixTool/Program.cs` mirrors MCP guidance in llms-txt/help output.

📌 Team update (2026-03-10): Discoverability routing decisions merged — keep the current tool surface, route repo-specific workflow selection through `helix_ci_guide(repo)`, treat `helix_test_results` as structured Helix-hosted parsing rather than a universal first step, and keep `helix_search_log`/docs/help guidance synchronized across surfaces. — decided by Dallas, Kane, Ripley

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

📌 Team update (2026-03-13): AzDO auth is now the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous, with scheme-aware `AzdoCredential` metadata and `DisplayToken` kept separate from the wire token. — decided by Dallas, Ripley

📌 Team update (2026-03-13): README/docs should expose MCP resources (`ci://profiles`, `ci://profiles/{repo}`) and treat idempotent annotations as a context-efficiency design point. — decided by Lambert

## Learnings (AzDO auth threat-model quick wins)

- **AzdoCredential safety boundary:** `AzdoCredential` no longer implicitly converts to `string`; any caller that truly needs the human-readable token must opt into `.DisplayToken`, while the legacy `string` → `AzdoCredential` conversion stays obsolete-only compatibility.
- **Explicit token-type override beats heuristics:** `AZDO_TOKEN_TYPE=pat|bearer` now short-circuits the two-dot JWT heuristic in `AzCliAzdoTokenAccessor.TryGetEnvCredential`, so ambiguous tokens do not rely solely on shape.
- **Unexpected AzDO error bodies are sanitized before surfacing:** `AzdoApiClient.ThrowOnUnexpectedError` still truncates to 500 chars, then redacts JWT-like values, long base64-like blobs, and `token=`/`key=`/`password=`/`secret=` assignments before throwing.
- **Key file paths:** `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` owns credential metadata and env-var auth resolution, `src/HelixTool.Core/AzDO/AzdoApiClient.cs` applies auth headers and sanitizes unexpected error snippets, and `src/HelixTool.Tests/AzDO/AzdoTokenAccessorTests.cs` covers env-var auth behavior and the legacy compatibility path.
