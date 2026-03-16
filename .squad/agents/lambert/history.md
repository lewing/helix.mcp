# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
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
- For private or compile-time auth seams, prefer narrow seam tests: drive `AzdoApiClient` redaction through public 500-response behavior, and use reflection only for `AzdoCredential` API-surface assertions or `TryGetEnvCredential` env-only behavior.
- Edge case: `GetAccessTokenAsync()` does not return null when only `AZDO_TOKEN_TYPE` is set, because the accessor still falls through to `AzureCliCredential`/`az` fallback; the null assertion belongs at the env-resolution seam, not the full accessor chain.
- Edge case: redaction regexes are intentionally selective — `token|key|password|secret=` values, JWT-shaped triples, and 41+ char base64-like blobs redact independently, while ordinary `name=value` text such as `reason=timeout` should remain visible in exception snippets.
- Key file paths: `src/HelixTool.Tests/AzDO/AzdoApiClientRedactionTests.cs` covers error-body redaction via `GetBuildAsync`, and `src/HelixTool.Tests/AzDO/AzdoTokenAccessorTests.cs` now locks invalid override fallback, missing-token env behavior, and `AzdoCredential` operator metadata.

📌 Team update (2026-03-13): PR #28 merged the remaining AzDO auth quick wins — fallback Azure CLI/`az` credentials now refresh on deadline/401, cache isolation keys off stable auth-source identity instead of raw token bytes, and `hlx azdo auth-status` exposes safe auth-path metadata. High-value regression coverage should lock refresh invalidation, cache partitioning, and auth-status output. — decided by Ripley
- Test patterns established: for `AzCliAzdoTokenAccessor`, deterministic fallback-cache tests can be done by seeding the private cached resolution via reflection while forcing a PATH with no `az`; that cleanly distinguishes “fresh cached fallback returned” from “expired fallback re-resolved to anonymous” without depending on developer machine credentials.
- Edge cases discovered: JWT helpers should treat non-JWT text, missing `exp`, and malformed base64url payloads as non-fatal nulls; auth-status on env PATs should surface `environment variable` + PAT expiry warning, while anonymous fallback must still report a user-facing "No AzDO credentials resolved" warning.

📌 Team update (2026-03-13): Cache roots now stay stable via `CacheRootHash` while mutable `AuthTokenHash` partitions AzDO entries, and AzDO auth hashes are seeded before cached AzDO reads. — decided by Ripley

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane
- Added TDD-style schema coverage in `src/HelixTool.Tests/CliSchema/SchemaGeneratorTests.cs` for the planned `HelixTool.Core.CliSchema.SchemaGenerator` API: primitives, DateTime/DateTimeOffset, Guid, enums, flat/nested POCOs, collections, nullable unwraps, circular references, max-depth cutoff, `AzdoAuthStatus` smoke coverage, and generic-vs-Type overload parity.
- Key pattern for pre-implementation testability: call `SchemaGenerator.GenerateSchema<T>()` / `GenerateSchema(Type)` via reflection, then parse the returned JSON with `JsonDocument` and assert placeholder values and array/object shape. That keeps `dotnet build src/HelixTool.Tests/HelixTool.Tests.csproj --nologo` green before the production type exists while still giving Ripley runtime-red TDD coverage once the implementation lands.

📌 Team update (2026-03-14): `hlx azdo search-log --schema` must mirror the active JSON payload: `LogSearchResult` with `--log-id`, `CrossStepSearchResult` otherwise. — decided by Ripley
- Test patterns for source-generated code: exercise generated registries through the consuming assembly (`HelixTool.Generated.CommandRegistry`) instead of the generator project, and sync metadata tests by reflecting the runtime MCP assembly for `[McpServerTool]` + `[Description]` attributes before comparing routes, descriptions, categories, and parameter defaults.
- Key file paths for describe coverage: `src/HelixTool.Tests/CliSchema/DescribeTests.cs`, `src/HelixTool/Program.cs`, `src/HelixTool.Generators/DescribeGenerator.cs`, and `src/HelixTool.Mcp.Tools/{Helix/HelixMcpTools.cs,AzDO/AzdoMcpTools.cs,CiKnowledgeTool.cs}`.
- `src/HelixTool.Tests/CliSchema/SchemaGeneratorTests.cs` should call `SchemaGenerator.GenerateSchema<T>()` / `GenerateSchema(Type)` directly now that the API is public; schema assertions must follow JSON contract names, so `[JsonPropertyName]` coverage belongs in both a local DTO test and a real-model smoke test such as `AzdoBuild`.

📌 Team update (2026-03-16): MCP timeline truncation implementation complete — `TimelineResponse` record type introduced for `azdo_timeline` return value when output exceeds 200 records (first 100 returned + truncation metadata). May need test updates if assuming `AzdoTimeline?` return type. See `.squad/decisions/decisions.md` (section "Timeline Truncation Implementation") for details and design trade-offs. — implemented by Ripley
📌 Team update (2026-03-16): MCP timeline truncation implementation complete — `TimelineResponse` record type introduced for `azdo_timeline` return value when output exceeds 200 records (first 100 returned + truncation metadata). May need test updates if assuming `AzdoTimeline?` return type. See `.squad/decisions/decisions.md#timeline-truncation` for details and design trade-offs. — implemented by Ripley
