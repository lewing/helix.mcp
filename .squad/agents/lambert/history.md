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

📌 Team update (2026-05-20): Pagination standardization audit complete — Dallas audit found 2 🔴 tools (`azdo_changes`, `azdo_test_runs`) returning raw lists with no truncation metadata, 10 🟡 tools with bespoke response shapes. Phase 1 wraps reds in `CreateLimitedResults()` (~30 min). Phase 2 adds `truncated` field to yellows (~2-3 hours). Testing target: verify truncated flag set correctly when results exceed `top` parameter. See `.squad/decisions.md#dallas--pagination-architecture` for inventory table and upstream API reality.

### Pagination contract tests (2026-05-20)
- **What:** Added 13 contract tests (333 LOC) in `src/HelixTool.Tests/AzDO/PaginationContractTests.cs` verifying pagination standardization per Dallas's spec.
- **Coverage areas:**
  1. **CreateLimitedResults helper** (5 tests): truncation logic when count == top, count < top, top=0, empty results, count > top edge case
  2. **Phase 1 tools** (4 tests): `azdo_changes` and `azdo_test_runs` now return `LimitedResults<T>` with correct truncation flag
  3. **Default parameters** (4 tests): `azdo_builds`, `azdo_test_results`, `azdo_changes`, `azdo_test_runs` use reasonable defaults (20/50/20/50)
- **Branch coordination:** Ripley is implementing Phase 1 changes in parallel (modified `AzdoMcpTools.cs`, `HelixMcpTools.cs`, `AzdoModels.cs`). Ripley's work is WIP (incomplete Helix changes causing build errors), but Phase 1 changes to `azdo_changes` and `azdo_test_runs` match the test expectations exactly.
- **Test status:** Tests compile against `HelixTool.Core`. Full solution build blocked by Ripley's incomplete Helix work — expected and normal for parallel development. Tests will verify Ripley's implementation once complete.
- **Key file:** `src/HelixTool.Tests/AzDO/PaginationContractTests.cs`
- **Test count:** 1167 existing + 13 new = 1180 tests (when Ripley completes implementation).


📌 Team update (2026-05-08): MCP SDK v1.0.0 → v1.3.0 upgrade pending — parallel research (Ash) and inventory (Dallas) complete; recommendation to upgrade to v1.3.0 (low risk, no code changes required). Drift items flagged by Dallas: hardcoded ServerInfo.Version, stdio host missing WithResourcesFromAssembly, no Directory.Packages.props. May need test updates if SDK changes affect existing test coverage. See `.squad/decisions/inbox/*` for details.

📌 Team update (2026-05-08): MCP SDK 1.3.0 upgrade — Ripley shipped branch `squad/mcp-sdk-1.3.0-upgrade` with MCP SDK 1.0.0 → 1.3.0, Central Package Management adoption (Directory.Packages.props), stdio host resource visibility fix, and ServerInfo.Version de-hardcoding. Validation: dotnet restore ✅, dotnet build ✅ (0 errors, 6 NU1507 warnings pre-existing). **Test verification pending** — Lambert owns full suite validation and sign-off before merge.


### MCP SDK 1.0.0 → 1.3.0 upgrade verification (2026-05-08)
- **Branch:** `squad/mcp-sdk-1.3.0-upgrade`, commit 80bf9f2 by Ripley.
- **Test command:** `dotnet test` from repo root → **1167 passed, 0 failed, 0 skipped, ~3 s** on net10.0. No regressions; no test changes needed for the SDK bump itself.
- **Stdio smoke:** `dotnet run --project src/HelixTool -- mcp --help` exits cleanly; `dotnet run --project src/HelixTool -- --version` returns **0.5.4**, proving `AssemblyInformationalVersionAttribute` lookup replaces the old hardcoded "0.1.2".
- **HTTP smoke:** `dotnet src/HelixTool.Mcp/bin/Debug/net10.0/HelixTool.Mcp.dll --urls http://127.0.0.1:18765` boots, binds, logs `Application started.` with zero DI resolve errors. Killed cleanly.
- **Resource scan parity:** Both `src/HelixTool/Program.cs:944` and `src/HelixTool.Mcp/Program.cs:89` now call `.WithResourcesFromAssembly(typeof(HelixMcpTools).Assembly)` — stdio fix matches HTTP host so `ci://profiles` is reachable from CLI/stdio clients.
- **Version source:** both Program.cs files use `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"` (stdio L939, http L84).
- **Gotcha:** Pre-existing `NU1507` warning (nuget.org + dotnet-eng without source mapping) is now surfaced under CPM. Out of scope but worth a follow-up. Verdict file: `.squad/decisions/inbox/lambert-mcp-sdk-upgrade-verdict.md`.
- **Verdict:** ✅ APPROVE.

📌 Team update (2026-05-08): MCP SDK 1.3.0 follow-ups — verification pending

**Context:** Ripley completed two PRs:
1. **PR #47** — `[AllowedValues]` sweep, `OpenWorld` annotations, `<packageSourceMapping>` (1167 tests pass ✅)
2. **PR #48** — Progress notifications on long-running tools (1167 tests pass ✅)

**Verification status:**
- Both PRs passed local test suite (1167/1167)
- Smoke tests confirmed (build clean, MCP help, version resolution)
- Awaiting sequential verification per squad orchestration

**Follow-ups from Ripley:**
- **PR #48:** Add unit tests for `ProgressReporter.CopyToWithProgressAsync` (event count band, monotonic progress, null sink no-op) + one end-to-end MCP-stdio test using SDK client's `WithProgress`
- **General:** Verify `[AllowedValues]` options match actual enum values in MCP schema generation

**Test count:** 1167 baseline maintained across both PRs.

### PR #47 (annotations + NU1507) and PR #48 (MCP progress notifications) verification (2026-05-08)
- **PR #47 verdict:** ✅ APPROVE. Build clean (0 warnings — NU1507 gone), 1167/1167 tests pass. Reflection probe over `HelixTool.Mcp.Tools.dll` confirms 25 `[McpServerTool]` methods with `OpenWorld` set explicitly on every one (22 true / 3 false: `azdo_auth_status`, `helix_auth_status`, `helix_ci_guide`). 10 params carry `[AllowedValues]`. Generated descriptors via `McpServerTool.Create()` show `enum` arrays in JSON schema (e.g. `azdo_builds.status.enum = ["all","cancelling","completed","inProgress","none","notStarted","postponed"]`) and `annotations.openWorldHint` in tool annotations.
- **PR #48 verdict:** ✅ APPROVE. Build clean, 1167/1167 baseline tests pass. Wrote a temporary `src/HelixTool.Tests/ProgressNotificationsSmokeTests.cs` (7 tests) to verify (1) `Download`, `FindFiles`, `SearchLog` declare a default-null `IProgress<ProgressNotificationValue>?` parameter with no `[Description]`, (2) `McpProgressAdapter.Wrap(null) == null` (fast path), (3) `Wrap(sink)` forwards `ProgressUpdate → ProgressNotificationValue` with float coercion, (4) `ProgressReporter.CopyToWithProgressAsync` emits initial+final updates over a 1 MiB stream. With smokes: 1174/1174 pass. Smokes were removed before returning to main (Ripley owns the branch); body preserved in the verdict file for re-use.
- **Pattern:** `McpProgressAdapter` is `internal` and `HelixTool.Mcp.Tools` has no `InternalsVisibleTo("HelixTool.Tests")`, so any future test of internal seams there must use reflection or land an IVT first. Recommended follow-up: ask Dallas/Ripley to add `[assembly: InternalsVisibleTo("HelixTool.Tests")]` to `HelixTool.Mcp.Tools`.
- **Probe pattern reused:** building a tiny `Probe` console csproj that `ProjectReference`s `HelixTool.Mcp.Tools` is the cleanest way to inspect MCP tool descriptors out-of-band — `McpServerTool.Create(method, target)` requires a non-null `target` for instance methods (use `RuntimeHelpers.GetUninitializedObject(method.DeclaringType)` when you don't want to construct the real DI graph).
- **Gotcha:** `HelixMcpTools` and `AzdoMcpTools` live in the flat `HelixTool.Mcp.Tools` namespace despite being under `Helix/` and `AzDO/` folders — don't add `using HelixTool.Mcp.Tools.Helix;`-style usings, they will not compile.
- **Branch sequencing observation:** during this verification, an external process (likely Scribe) checked out main and committed in the same working tree while Lambert was mid-verify. Sequential single-tree verification is workable but fragile — re-confirm the active branch with `git rev-parse --abbrev-ref HEAD` between stages, or move to git worktrees per the standing recommendation in the SDK 1.3.0 verdict.

📌 Team update (2026-05-21): Pagination contract tests — wrote 13 tests (333 LOC) for Phase 1+2 pagination spec in src/HelixTool.Tests/AzDO/PaginationContractTests.cs. All 13/13 passing; full suite 1180/1180 passing. Commits 181ff5b + d5fde34. ⚠️ BRANCH-HYGIENE: committed to local main instead of squad/pagination-standardize per manifest instruction; Larry will handle branch/push decision.
