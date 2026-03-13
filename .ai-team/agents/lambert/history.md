# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context

- **Test stack:** `src/HelixTool.Tests/HelixTool.Tests.csproj` targets net10.0 with xUnit + NSubstitute; Helix tests live under `src/HelixTool.Tests/Helix/`, AzDO tests under `src/HelixTool.Tests/AzDO/`, and shared coverage stays at the test-project root.
- **Assertion conventions:** MCP-surface tests assert camelCase JSON names, env-var mutation tests use `[Collection("FileSearchConfig")]`, and disk-writing tests use unique GUID-based temp roots/job IDs to avoid parallel contention.
- **Mocking seams:** mock `IHelixApiClient` / `IAzdoApiClient` plus their projection interfaces, use fresh-stream lambdas for file/download tests, and prefer focused test runs before the full suite when reviewing changes.
- **High-value file paths:** `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs`, `src/HelixTool.Tests/CacheSecurityTests.cs`, and `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` are the main regression seams for current architecture decisions.

## Learnings

**Archive refresh (2026-03-13):** Detailed PR #15 cleanup, 9-repo `CiKnowledgeServiceTests` expansion, and the linked 2026-03-10 CI-knowledge update moved to `history-archive.md`. Durable takeaways: delete proactive duplicate tests once real coverage lands, and use broad Theory matrices for static CI-profile data.

- Tests for AzdoMcpTools should assert against `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers for AzDO tools.
- **SearchBuildLogAcrossSteps (21 tests):** 3 categories — Unit (T-1–T-11: ranking, early termination, orphans, normalization), Validation (V-1–V-6: argument checks), MCP (M-1–M-2: exception remapping). Key: `SetupTimeline()`/`SetupLogsList()`/`SetupLogContent()` helpers. `LogsSkipped` tracks cap-limited logs, not minLines-filtered. `stoppedEarly` = budget exhausted OR eligible logs remain. Test count after: 812.
- Discoverability copy has two strong regression seams: reflect `DescriptionAttribute` text on MCP tool methods to lock routing promises, and assert rendered CI-guide section ordering with `IndexOf`/section slicing so “use AzDO first” guidance stays visible before pattern inventories.
- `helix_test_results` false-confidence regressions are best caught through MCP-layer exception assertions in `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`; high-value cases are no structured-result files, empty uploads, and crash-artifact uploads, all of which should route callers toward `azdo_test_runs`/`azdo_test_results`, `helix_search_log`, and `helix_ci_guide`.
- Key file paths for discoverability coverage: `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs` now holds MCP description + fallback-routing assertions, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs` locks guide wording/order for aspnetcore/runtime, `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` contains the live tool descriptions, and `src/HelixTool.Core/CiKnowledgeService.cs` renders the repo-specific CI guide text.
- User preference reinforced again: for review-driven test changes, run focused tests first to debug wording/assertion mismatches quickly, then run the full `src/HelixTool.Tests/HelixTool.Tests.csproj` suite before concluding the regression coverage is complete.

### Redundant test cleanup (PR #15)
- **Deleted `AzdoCliCommandTests.cs`** (22 tests → 19 removed, 3 rescued): The file was written proactively for CLI subcommands that were never implemented. 19 of 22 tests were near-identical duplicates of `AzdoServiceTests` — same mock setup, same assertions, just different variable names. Rescued 3 unique tests (artifact default/pattern filtering, changes with top parameter) into `AzdoServiceTests.cs`.
- **Removed 3 "ImplementsInterface" / "Constructor_Accepts" tests**: `HelixApiClientFactoryTests.ImplementsIHelixApiClientFactory`, `HttpContextHelixTokenAccessorTests.ImplementsIHelixTokenAccessor`, `HelixMcpToolsTests.Constructor_AcceptsHelixService`. These are compile-time guarantees — if the class doesn't implement the interface, the project won't build.
- **Merged 2 overlapping filter tests** in `HelixMcpToolsTests`: `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed` tested the same behavior (default filter is "failed"). Combined into one test that verifies both the default and explicit "failed" filter.
- **Pattern observed**: Proactive test files written before production code tends to produce near-duplicates of the actual test file once it lands. Worth catching during PR review.
- **Test count**: 864 → 844 (net -20 tests removed). All 844 pass.

📌 Team updates (2026-03-09 – 2026-03-10 summary): CI profile analysis — 14 tool description/error message recommendations (Ash). Test quality review — net -17 tests, zero coverage loss, prune proactive tests when real tests land (Dallas). CiKnowledgeService expanded to 9 repos, 5 tool descriptions updated (Ripley).

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

- Cache path-boundary hardening is now regression-covered in `src/HelixTool.Tests/CacheSecurityTests.cs`; the important edge case is a sibling path that differs only by casing (`test-root` vs `TEST-ROOT`), which must be rejected to avoid false containment on case-sensitive filesystems.
- `HelixService` no longer supports a null/implicit `HttpClient`; `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` covers both `ArgumentNullException` branches, and focused/full-suite runs confirmed current CLI/MCP construction sites already inject `IHttpClientFactory` clients.
- Key file paths for this review: `src/HelixTool.Core/Cache/CacheSecurity.cs` contains the Ordinal child-boundary check, `src/HelixTool.Core/Helix/HelixService.cs` owns the strict constructor requirement, `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` wire named `HelixDownload` clients, and `src/HelixTool.Tests/HttpClientConfigurationTests.cs` exercises timeout/cancellation behavior with explicit `HttpClient` injection.
- User preference reinforced: validate review fixes with targeted tests first, then run the full `src/HelixTool.Tests/HelixTool.Tests.csproj` suite before concluding coverage is sufficient.

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash

📌 Team update (2026-03-10): Discoverability routing decisions merged — keep the current tool surface, route repo-specific workflow selection through `helix_ci_guide(repo)`, treat `helix_test_results` as structured Helix-hosted parsing rather than a universal first step, and keep `helix_search_log`/docs/help guidance synchronized across surfaces. — decided by Dallas, Kane, Ripley

### Idempotent annotation sweep (2025-07-25)
- **What:** Added `Idempotent = true` to all 22 `[McpServerTool]` attributes that had `ReadOnly = true` across 3 files: `AzdoMcpTools.cs` (12 tools), `HelixMcpTools.cs` (9 tools), `CiKnowledgeTool.cs` (1 tool).
- **Why:** MCP best practices (Anthropic, OpenAI, AWS, arxiv 2602.14878) recommend safety annotations on all tools. `Idempotent = true` signals to clients that these tools are safe to retry and cache, complementing the existing `ReadOnly = true`.
- **Verification:** `helix_download` and `helix_download_url` correctly have `Idempotent = true` WITHOUT `ReadOnly = true` — they write files to disk, so they're idempotent but not read-only. No tools were found missing `ReadOnly = true`.
- **Key files:** `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs`, `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs`, `src/HelixTool.Mcp.Tools/CiKnowledgeTool.cs`
- **Test count:** 1047 (1046 pass, 1 pre-existing flaky: `AzdoTokenAccessorTests.ConcurrentCallsWithoutEnvVar`).

📌 Team update (2026-03-13): Scribe merged decision inbox items covering `dotnet` as the VMR profile key, `helix_search`/`helix_parse_uploaded_trx` naming, tighter MCP descriptions, and explicit truncation metadata (`truncated`, `LimitedResults<T>`). README/docs now also call out `ci://profiles` resources and idempotent annotations.
- AzDO auth now centers on `AzdoCredential` instead of raw strings: `Token` is the wire value, `DisplayToken` preserves the original PAT/JWT for assertions and messages, and implicit string conversion returns `DisplayToken`, which keeps older mock patterns readable while still allowing scheme-aware auth tests.
- `AzCliAzdoTokenAccessor` checks `AZDO_TOKEN` on every call but only caches the fallback chain (`AzureCliCredential`/`az` CLI). High-value regression tests should lock both behaviors: env tokens short-circuit without marking fallback state resolved, while a resolved fallback returns the cached credential on later calls.

📌 Team update (2026-03-13): AzDO auth is now the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous, with scheme-aware `AzdoCredential` metadata and `DisplayToken` kept separate from the wire token. — decided by Dallas, Ripley

📌 Team update (2026-03-13): MCP-facing Helix names/descriptions should stay scope-accurate and low-context: use `helix_parse_uploaded_trx`, `helix_search`, and keep repo-specific routing in `helix_ci_guide`. — decided by Ripley
