# Decisions

> Shared team decisions — the single source of truth for architectural and process choices.

### 2025-07-18: Architecture Review — hlx project improvement proposal
### 2026-02-13: User directive

- Operators must be able to disable remote file-content analysis features as a defense-in-depth control, even while leaving metadata/file-list tooling available.
- That directive became the basis for the `HLX_DISABLE_FILE_SEARCH` toggle used by search and structured-result parsing.

# US-31: hlx_search_file Phase 1 Implementation

**By:** Ripley
**Date:** 2025-07-23

## What was implemented

1. **Extracted `SearchLines` helper** — private static method in HelixService that encapsulates the line-matching + context-gathering logic. Both `SearchConsoleLogAsync` and the new `SearchFileAsync` use it, eliminating duplication.

2. **`SearchFileAsync`** — downloads a single file via `DownloadFilesAsync(exact fileName)`, checks for binary content (null byte in first 8KB), enforces 50MB size limit (`MaxSearchFileSizeBytes`), then delegates to `SearchLines`. Returns `FileContentSearchResult` record.

3. **Config toggle** — `HLX_DISABLE_FILE_SEARCH=true` env var disables both `SearchConsoleLogAsync` and `SearchFileAsync` (throws `InvalidOperationException`). MCP tools check via `HelixService.IsFileSearchDisabled` and return JSON error instead of throwing.

4. **MCP tool `hlx_search_file`** — follows exact `SearchLog` pattern with URL resolution and config toggle.

5. **CLI command `search-file`** — follows `search-log` pattern with positional args.

## Decisions made

- **Binary detection strategy**: Check for null byte in first 8KB. Simple and effective — avoids searching compiled binaries, zip files, etc.
- **File download reuse**: Used existing `DownloadFilesAsync` with exact fileName as pattern rather than adding a new download method. Works because `MatchesPattern` does substring match.
- **Config toggle scope**: Applied to both search-log and search-file per Larry's directive. The env var name `HLX_DISABLE_FILE_SEARCH` covers all file content search operations.

## For Lambert

Tests needed:
- `SearchLines` helper (extracted logic, same behavior as before)
- `SearchFileAsync` — binary detection, size limit, normal search, file-not-found
- `IsFileSearchDisabled` toggle — both service methods and MCP tools
- `SearchFile` MCP tool — URL resolution, config toggle, binary file JSON error
- `SearchFile` CLI command smoke test

## Files changed

- `src/HelixTool.Core/HelixService.cs` — SearchLines helper, SearchFileAsync, FileContentSearchResult, IsFileSearchDisabled, MaxSearchFileSizeBytes
- `src/HelixTool.Core/HelixMcpTools.cs` — hlx_search_file tool, config toggle on hlx_search_log
- `src/HelixTool/Program.cs` — search-file CLI command

---

# Decision: Status filter refactored from boolean to enum-style string

**By:** Ripley
**Date:** 2025-07-23
**Requested by:** Larry Ewing

## Context

The `status` command/tool previously used a boolean `--all`/`includePassed` flag to toggle showing passed work items. This was limited — you could only see "failed only" or "everything". There was no way to see "passed only".

## Decision

Replaced the boolean with a three-value string `filter` parameter:
- `"failed"` (default) — shows only failed items (backward-compatible default)
- `"passed"` — shows only passed items
- `"all"` — shows both failed and passed items

Validation throws `ArgumentException` for invalid values. Comparison uses `StringComparison.OrdinalIgnoreCase`.

## Impact

- **MCP tool (`hlx_status`):** `bool includePassed` → `string filter = "failed"`. When filter is `"passed"`, `failed` is null in output. When `"failed"`, `passed` is null. When `"all"`, both populated.
- **CLI command (`status`):** `bool all` → `[Argument] string filter = "failed"`. Second positional arg. Usage: `hlx status <jobId> [failed|passed|all]`.
- **Breaking change:** Existing callers using `--all` or `includePassed=true` must update to `filter="all"`.
- **Tests:** Lambert needs to update tests for the new parameter signature and filter logic.

---

# US-32: TRX Parsing Implementation Notes

**By:** Ripley  
**Date:** 2025-07-23

## What was implemented

Phase 2 of US-32 — structured TRX test result parsing via `ParseTrxResultsAsync` in HelixService, `hlx_test_results` MCP tool, and `test-results` CLI command.

## Key decisions made during implementation

1. **XmlReaderSettings as static readonly field** — Named `s_trxReaderSettings` following the `s_jsonOptions` naming convention. Security settings per Ash's review: `DtdProcessing.Prohibit`, `XmlResolver=null`, `MaxCharactersFromEntities=0`, `MaxCharactersInDocument=50_000_000`.

2. **Error truncation limits** — 500 chars for error messages, 1000 chars for stack traces. These are hard-coded in `ParseTrxFile`. If consumers need full error text, they can use `hlx_search_file` on the TRX file directly.

3. **Reuses `IsFileSearchDisabled` and `MaxSearchFileSizeBytes`** — Same config toggle and size guard as `SearchFileAsync`. TRX parsing is a form of file content analysis, so the same security controls apply.

4. **Filter logic** — Failed tests always included, non-pass/non-fail (skipped, etc.) always included, passed tests only when `includePassed=true`. This keeps default output focused on actionable results.

## For Lambert

Tests needed for:
- `ParseTrxResultsAsync` — happy path, file not found, oversized file, disabled toggle
- `ParseTrxFile` — valid TRX, empty TRX, error truncation, includePassed filter, maxResults cap
- `hlx_test_results` MCP tool — URL resolution, config toggle, missing workItem
- `test-results` CLI command — basic invocation

---

# Decision: Status Filter Test Coverage Strategy

**By:** Lambert (Tester)
**Date:** 2026-02-15
**Context:** Status API migration from `includePassed: bool` to `filter: string`

## Decision

The `filter: "passed"` case introduces a new behavioral pattern where `failed` is null (the reverse of `filter: "failed"` nulling `passed`). Tests cover this symmetry explicitly, plus case-insensitivity and invalid input rejection.

Test naming convention follows the pattern `Status_Filter{Value}_{Assertion}` for consistency with the new API shape. The old `Status_AllTrue`/`Status_AllFalse` names are retired.

## Tests Added (5 new + 2 renamed)

| Test | Filter | Validates |
|------|--------|-----------|
| `Status_FilterFailed_PassedIsNull` | "failed" | passed=null (renamed) |
| `Status_FilterAll_PassedIncludesItems` | "all" | passed populated (renamed) |
| `Status_DefaultFilter_ShowsOnlyFailed` | (none) | default = "failed" behavior |
| `Status_FilterPassed_FailedIsNull` | "passed" | failed=null, passed populated |
| `Status_FilterPassed_IncludesPassedItems` | "passed" | item structure verification |
| `Status_FilterCaseInsensitive` | "ALL" | uppercase accepted |
| `Status_InvalidFilter_ThrowsArgumentException` | "invalid" | ArgumentException thrown |

## Impact

Test count: 364 → 369 (net +5). All 15 status tests pass.

### 2025-07-25: Cache security expectations documented in README

**By:** Kane
**What:** Added a "Cached data" subsection under Security in README.md. Documents what gets cached (API responses + artifacts), where it lives (SQLite on disk in user profile directory), that auth tokens are never cached (only hash prefix for directory isolation), and recommends `hlx cache clear` for shared machines or security context switches. Addresses threat model items I1 (information disclosure via cached data) and I2 (cache persists after session).
**Why:** The threat model (`.ai-team/analysis/threat-model.md`) explicitly recommended documenting cache security expectations. Users need to know the cache directory may contain sensitive CI data (console logs with accidental secrets) and understand the auth isolation model. This closes the documentation gap for I1/I2 without requiring code changes.

### 2026-02-15: Isolate DownloadFilesAsync temp directories per invocation

- Download temp directories include a per-invocation GUID so concurrent stdio/MCP processes never write into the same folder.
- Any disk-backed workflow that can run in parallel should avoid predictable shared temp paths.

**Decided by:** Ripley  
**Date:** 2025-07-23  
**Status:** Implemented

## Context

The publish workflow triggers on `v*` tags and extracts the version from the tag. Previously, nothing verified that the csproj and server.json versions matched the tag, and `dotnet pack` used whatever version was in the csproj.

## Decision

1. **Tag is source of truth for package version.** The Pack step now passes `/p:Version=` from the tag, overriding the csproj value.
2. **Validation step before Pack** checks that `HelixTool.csproj <Version>`, `server.json .version`, and `server.json .packages[0].version` all match the tag. Fails the build on mismatch with clear error messages.
3. **Belt-and-suspenders approach:** validation catches mistakes early for developers, `/p:Version=` override ensures the published artifact is always correct.

## Impact

- All team members must update csproj and server.json version fields before tagging a release.
- The workflow will fail fast with actionable error messages if versions are out of sync.
- No CI workflow exists yet (`ci.yml`), so validation is only in the publish workflow.

### 2026-02-27: Enhancement layer documentation (consolidated)

- The consolidated enhancement pass documented hlx’s value-add over raw Helix APIs in README and aligned docs cleanup around that framing.
- Remaining follow-ups were mostly llmstxt/tool-description completeness, not new architectural work.

**What:**

1. **Value-add inventory (Dallas, 2025-07-23):** Cataloged 12 local enhancements hlx provides over raw Helix REST APIs — 5 MAJOR (cross-process SQLite cache, smart TTL policy, failure classification, TRX parsing, remote content search), 3 SIGNIFICANT (URL parsing, cross-work-item file discovery, batch aggregation), 3 MODERATE (file type classification, computed duration, auth-isolated cache), 1 MINOR (log URL construction).

2. **Docs gap analysis (Kane, 2025-07-18):** Audited README (B+), MCP [Description] attributes (C), and llmstxt (C+). Key gaps: MCP descriptions don't convey that features like failure categorization and TRX parsing are local enhancements; hlx_search_file description misleadingly says "without downloading"; llmstxt missing hlx_search_file and hlx_test_results.

3. **README section added (Kane, 2025-07-18):** Wrote "How hlx Enhances the Helix API" section with two tables — 5 major enhancements (3-column: enhancement/what you get/why it matters) and 7 convenience enhancements (2-column). Placed between Failure Categorization and Project Structure.

**Why:** Users (human and LLM) need to understand what hlx adds beyond raw API access. The README covered features but didn't frame them as enhancements over the raw API. MCP tool descriptions are the primary documentation surface for AI agents and lacked this context.

**Remaining gaps (prioritized):**
- **P1:** llmstxt missing hlx_search_file and hlx_test_results — needs Ripley to update Program.cs raw string literal
- **P1:** `hlx_status` description should list `failureCategory` as a response field (completeness fix, not implementation disclosure)
- ~~**P1:** MCP [Description] attributes should flag local enhancements~~ — **Resolved 2026-02-27:** Dallas decided MCP descriptions describe what/inputs/outputs, not implementation details. See "MCP tool descriptions should expose behavioral contracts" decision below.
- **P3:** Failure categorization heuristic details (exit code→ category mapping) not yet documented

### 2025-07-23: MCP tool descriptions should expose behavioral contracts, not implementation mechanics

- Tool descriptions should tell agents what the tool does, what inputs it accepts, and what it returns — not whether the implementation caches, parses locally, or streams.
- Implementation details belong in README or deeper docs unless they change invocation behavior (for example, a new parameter or observable contract).

**Why:**

The question is: does knowing the implementation mechanism change an LLM agent's decision-making? The answer is no for almost every case, and the few exceptions are already handled by describing *behavior*, not *mechanism*.

**Analysis by category:**

1. **Caching ("results are cached")** — An agent should never care whether results are cached. It doesn't change the call signature, the return shape, or when to call the tool. If caching affected correctness (stale data), the right fix would be a `noCache` parameter, not a description warning. Currently our cache is transparent and correct. Verdict: **implementation detail, omit.**

2. **TRX parsing ("parses TRX locally")** — The agent cares that `hlx_test_results` returns *structured test results with names, outcomes, durations, and error messages*. That's already in the description. Whether we parse XML locally or call a hypothetical Helix "parse TRX" API is irrelevant to the consumer. The description already says what you get. Verdict: **implementation detail, omit.**

3. **Remote search ("searches without downloading")** — This is the closest case to being behavior-relevant, because it implies "this is fast and doesn't leave files on disk." But the agent doesn't manage disk space or care about temp files. The behavioral contract is already correct: `hlx_search_file` says "Returns matching lines with optional context" and `hlx_search_log` says "Returns matching lines with optional context." That tells the agent exactly what it gets. Verdict: **implementation detail, omit.**

4. **Failure classification ("classifies failures locally")** — The agent cares that `hlx_status` returns `failureCategory` in the response. It doesn't care whether we computed that classification or the Helix API returned it. The current description says "failed items (with exit codes, state, duration, machine)" — this should include `failureCategory` in the description since it's a field in the response. But that's a *completeness* fix, not an "implementation detail" disclosure. Verdict: **fix the field list, don't mention local processing.**

5. **URL resolution ("accepts full Helix URLs")** — This IS behavior-relevant and IS already in tool descriptions. Every tool says "Helix job ID (GUID), Helix job URL, or full work item URL." This tells the agent what inputs are accepted. Correct and sufficient.

**The principle:** Tool descriptions are an API contract for *consumers*. They answer three questions:
- What does this tool do? (purpose)
- What inputs does it accept? (parameters)  
- What does it return? (output shape)

They do NOT answer:
- How does this tool work internally? (implementation)
- What makes this tool fast/efficient? (optimization)
- Where does processing happen? (architecture)

**The README vs. Description distinction:** The README is for *humans evaluating whether to adopt hlx*. They care about value-adds, architecture, and implementation quality. Tool descriptions are for *LLM agents selecting and invoking tools at runtime*. They care about capabilities, parameters, and return shapes. These are different audiences with different information needs.

**One exception — behavioral implications:** If an implementation detail creates a behavioral contract the agent must respect, it belongs in the description. Example: if we added a `noCache` parameter, the description should say "Bypass cache and fetch fresh data" because that changes invocation behavior. But "this is cached" with no opt-out is invisible to the consumer.

**Action items:**
- Do NOT modify any existing `[Description]` attributes to add implementation details
- DO ensure descriptions accurately list all response fields (the `failureCategory` omission in `hlx_status` is a minor gap)
- The README's "How hlx Enhances the Helix API" section is the correct home for implementation detail documentation
- This principle applies to all future MCP tools added to the project

### 2025-07-24: UseStructuredContent refactor — APPROVED with one naming issue noted

- Structured MCP tools should return typed objects with `UseStructuredContent = true`; `hlx_logs` remains raw text because its value is the plain console output.
- Use `McpException` for tool-surface failures and keep JSON property names/wire format stable even when internal C# type names change.

**Why:**
1. The MCP SDK 1.0.0 `UseStructuredContent` feature generates JSON output schemas automatically, which improves tool discovery for LLM consumers. Typed returns are also more maintainable — no more manual `JsonSerializer.Serialize` calls with shared `JsonSerializerOptions`.
2. `hlx_logs` is the correct exception — it returns raw text content (console logs), not structured data. Forcing it through structured content would add unnecessary wrapping.
3. The `FileInfo_` type name (trailing underscore to avoid collision with `System.IO.FileInfo`) is an acceptable pragmatic choice — it's not part of the public MCP wire format (only the `[JsonPropertyName]` matters for serialization), and consumers never see the C# type name. A rename to `HelixFileInfo` would be cleaner but is not blocking.
4. All `[JsonPropertyName]` attributes use camelCase, matching the previous manual serialization output. No breaking wire-format changes.
5. Error handling correctly uses `McpException` for tool-level errors (missing work item, no matching files, binary file) and `ArgumentException` for invalid parameters (bad filter value). This matches MCP SDK conventions.

### 2026-03-01: Release version checklist

The publish workflow (`publish.yml`) validates all three match the git tag. Missing any one will fail the release.
**Why:** v0.2.0 release required a force-push to fix because `server.json` wasn't updated alongside the csproj. The workflow caught it, but we should get it right the first time.

### 2026-03-03: Default CLI behavior based on terminal context

**By:** Ripley (Backend Dev)
**What:** Use `Console.IsInputRedirected` to auto-detect context: interactive terminal defaults to `["--help"]`, redirected stdin defaults to `["mcp"]`. Previously, running `hlx` with no arguments in a terminal would hang waiting for JSON-RPC input.
**Why:** `Console.IsInputRedirected` is a reliable .NET API — standard idiom for CLI tools that need different behavior in interactive vs. non-interactive contexts. No additional dependencies or platform-specific code required.
- Every release must update all three version sources together: `HelixTool.csproj`, `.mcp/server.json` top-level `version`, and `packages[0].version`.
- The publish workflow validates these against the git tag, so partial bumps will fail release automation.

### 2026-03-07: Helix auth UX — hlx login architecture (consolidated)

**By:** Ash (analysis), Dallas (architecture)
**Date:** 2026-03-03 (analysis), 2026-03-03 (architecture), consolidated 2026-03-07
**Status:** Approved — Phase 1 ready for implementation

**What:**
- Helix API does **not** accept Entra JWT tokens — server-side limitation. Opaque tokens only via `Authorization: token <TOKEN>`.
- Phase 1 approved: `hlx login` + `git credential` storage + token resolution chain.
- Three new CLI commands: `hlx login` (browser open + token paste + validation), `hlx logout` (remove stored credential), `hlx auth status` (report source + test API).
- Credential storage via `git credential` (Option A): zero new dependencies, cross-platform, proven pattern (`darc` precedent).
- Token resolution chain (CLI): env var → stored credential → null. Env var wins for backward compatibility and CI/CD override.
- New classes: `ICredentialStore`, `GitCredentialStore` (in Core), `ChainedHelixTokenAccessor` (in Core).
- HTTP MCP fallback: Authorization header → env var → stored credential.
- 7 work items defined (WI-1: ICredentialStore, WI-2: ChainedHelixTokenAccessor, WI-3: DI wiring, WI-4: hlx login, WI-5: hlx logout, WI-6: hlx auth status, WI-7: error message update).
- Error messages in HelixService.cs updated from "Set HELIX_ACCESS_TOKEN..." to "Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN."

**Why:**
- Manual token generation + env var setup is the biggest UX friction point. Users must navigate to helix.dot.net → Profile → generate token → export env var in every shell.
- `git credential` is battle-tested, cross-platform, and already familiar to the user base. Zero new dependencies.
- Env var priority over stored credential preserves backward compatibility and enables CI/CD overrides.
- Entra auth deferred to Phase 3 (blocked on Helix server adding JWT Bearer support).

### 2026-03-07: Test result file discovery and xUnit XML support (consolidated)

**By:** Ripley
**Date:** 2025-07-24 (xUnit XML), 2026-03-07 (file patterns), consolidated 2026-03-07
**Status:** Implemented

**What:**
- `ParseTrxResultsAsync` auto-discovers test result files using priority-ordered `TestResultFilePatterns` array: `*.trx`, `testResults.xml`, `*.testResults.xml.txt`, `testResults.xml.txt`.
- xUnit XML format (`<assemblies>/<assembly>/<collection>/<test>`) supported alongside TRX. Format auto-detected via `DetectTestFileFormat`.
- Strict parsing (XXE-safe, `DtdProcessing.Prohibit`) for `.trx`; best-effort for `.xml` fallback.
- `IsTestResultFile()` is the canonical check used by CLI, MCP, and service code.
- Single file list query reduces API calls from 2-3 to 1.
- HelixException must be caught and rethrown as McpException in MCP tool handlers. MCP SDK only surfaces McpException messages to clients; other exceptions get wrapped as generic errors (root cause of issue #4).

**Why:**
- Runtime CoreCLR tests upload `{name}.testResults.xml.txt` to regular files (not testResults category); iOS/XHarness tests upload `testResults.xml`. Previous code only searched `*.trx` then `*.xml`.
- ASP.NET Core projects use `--logger xunit` producing `TestResults.xml` in xUnit format, not `.trx`. Without fallback, `hlx_test_results` failed on those work items.

### 2026-03-07: AzDO pipeline support — architecture and foundation (consolidated)

**By:** Dallas (architecture), Ripley (implementation)
**Date:** 2026-03-07
**Status:** Foundation IMPLEMENTED, full architecture DRAFT

**What:**
- Add AzDO pipeline wrapping to helix.mcp, following established Helix patterns. Enables ci-analysis skill to query AzDO builds, timelines, logs, and test results.
- Architecture mirrors Helix: `IAzdoApiClient` → `CachingAzdoApiClient` (decorator) → `AzdoService` → `AzdoMcpTools`.
- Code lives in `src/HelixTool.Core/AzDO/` — no separate project. AzDO and Helix data are tightly coupled (Helix jobs spawned by AzDO builds).
- Auth uses Azure Identity (`AzureDeveloperCliCredential`), keeping Helix PAT auth separate.
- `IAzdoTokenAccessor.GetAccessTokenAsync()` returns `Task<string?>` (async, unlike Helix's sync accessor). `az CLI` subprocess call is inherently async.
- Token caching is session-scoped — cached after first `az` call, not refreshed mid-session.
- `AzdoIdResolver` parses AzDO URLs to extract org/project/buildId. `TryResolve()` wraps `Resolve()` via try/catch (single code path for correctness).
- `AzdoBuildFilter` is client-side only (no JSON serialization attributes).
- `AzdoTimelineAttempt` record included for `previousAttempts` on timeline records (needed for retried job detection).
- Phase 1 scope: 7 core operations (get build, list builds, get timeline, get build log, get build changes, get test runs/results, URL parsing).
- Foundation files created: `AzdoModels.cs`, `IAzdoTokenAccessor.cs`, `AzdoIdResolver.cs`.

**Why:**
- Eliminates need for a separate AzDO MCP server. CI investigation inherently spans both Helix and AzDO.
- Shared cache infrastructure (`ICacheStore`, `SqliteCacheStore`) reused.
- Separate project adds complexity for zero benefit at current scale.
- Async token accessor avoids blocking with `.GetAwaiter().GetResult()` when callers are already async.

### 2026-03-07: Future direction — AzDO pipeline wrapping

**By:** Larry Ewing (via Copilot)
**What:** After issue #4 is wrapped up, explore wrapping Azure DevOps pipelines as MCP tools, similar to how we wrap Helix today.
**Why:** User request — captured for team discussion after current work completes.

### 2026-03-07: AzDO test patterns and conventions (consolidated)

**By:** Lambert, Dallas
**Updated:** 2026-03-08 — merged security testing conventions

#### Test file locations
- `src/HelixTool.Tests/AzDO/AzdoIdResolverTests.cs` — 31 tests for URL parsing
- `src/HelixTool.Tests/AzDO/AzdoTokenAccessorTests.cs` — 10 tests for auth chain
- `src/HelixTool.Tests/AzDO/AzdoSecurityTests.cs` — 63 security-focused tests
- Namespace: `HelixTool.Tests.AzDO` (matches Core's `HelixTool.Core.AzDO`)

#### General conventions
- **Resolve() vs TryResolve():** AzdoIdResolver exposes both. Test Resolve() for throw behavior, TryResolve() for bool-return path. Both use same internal logic.
- **Env var isolation:** `AzdoTokenAccessorTests` implements `IDisposable` to save/restore `AZDO_TOKEN` env var. Critical for parallel xUnit — env vars are process-global. Use try/finally blocks.
- **Az CLI timeout:** Tests that fall through to az CLI take ~1s. Future: consider `[Trait("Category", "Slow")]` if test suite grows.

#### Security test categories
- **URL input validation** — malicious URLs, path traversal, SSRF vectors, injection
- **Command injection** — shell safety, env var passthrough, process construction
- **Request construction** — SSRF prevention, token leakage, header injection
- **Cache isolation** — key namespacing, org/project separation, poisoning resistance
- **End-to-end** — AzdoService rejects bad input before any API call

#### Security test patterns
- **Token leakage assertion:** `Assert.DoesNotContain("token-value", ex.Message)` on all error paths (401/403/500)
- **SSRF prevention:** `Assert.StartsWith("https://dev.azure.com/", url)` and `Assert.Equal("dev.azure.com", uri.Host)`
- **Cache key isolation:** Verify `SetMetadataAsync` called with different keys for different org/project
- **Credential leakage:** `Assert.DoesNotContain("user"/"pass", org/project)` for URLs with embedded credentials
- **Rejection is safe:** Use `TryResolve` for inputs where rejection or safe parsing are both acceptable
- **No-API-call guard:** After throwing on bad input, verify `DidNotReceive()` on mock client
- **CapturingHttpHandler:** Each security test class defines a local `CapturingHttpHandler` (same pattern as `FakeHttpMessageHandler`). Avoids coupling test classes.

#### Edge cases identified
1. **Negative/zero buildIds are accepted** — `int.TryParse` succeeds for `-5` and `0`. No positivity validation in `AzdoIdResolver`. Recommend adding `buildId > 0` check.
2. **TryResolve out-param defaults are not null** — they're `DefaultOrg`/`DefaultProject`, unlike typical `TryParse` patterns. Callers should check the return value, not the out params.
3. **_resolved flag not thread-safe** — concurrent first calls to `AzCliAzdoTokenAccessor` without env var may spawn multiple `az` processes. Benign but wasteful. Consider `SemaphoreSlim` if this matters.
4. **Env var read on every call** — `AZDO_TOKEN` is not cached. This is intentional (allows runtime token rotation) but means env var tests must carefully set/unset between assertions.
5. **Duplicate query params:** `HttpUtility.ParseQueryString` concatenates with commas → `int.TryParse` rejects safely
6. **Embedded credentials in URLs:** `Uri` parses `user:pass@` as UserInfo; resolver only reads `Host`/`Path` — safe
7. **Path traversal in org/project:** `Uri` normalizes `../../` away; `CacheSecurity.SanitizeCacheKeySegment` strips remaining
8. **Int overflow buildId:** `long.MaxValue` as buildId fails `int.TryParse` → `ArgumentException`
9. **Newlines in token env var:** Returned verbatim — potential header injection if token used unsafely (mitigated by `AuthenticationHeaderValue`)

#### Testability concerns for future AzDO work
- `AzdoApiClient` will need `HttpMessageHandler` injection for mocking (same pattern as `HelixApiClient`)
- `AzdoService` should take `IAzdoApiClient` via constructor for NSubstitute mocking
- `AzdoMcpTools` can follow `HelixMcpToolsTests` pattern: mock service, test tool wrappers

### 2026-03-07: XXE prevention test regression after xUnit XML refactor

**By:** Lambert
**What:** `ParseTrx_RejectsXxeDtdDeclaration` test now fails after xUnit XML auto-discovery refactor. `XmlException` is swallowed by `TryParseTestFile`/`DetectTestFileFormat`, returning `HelixException("Found XML files but none were in a recognized format")` instead. DTD content is not processed (safe), but error message no longer indicates security rejection.
**Why:** Need to verify `DetectTestFileFormat` uses `DtdProcessing.Prohibit` and that the swallowed exception doesn't silently process DTD content. Test should be updated to assert `HelixException` not `XmlException`.

### 2026-03-08: Use Lazy<T> in CacheStoreFactory to prevent concurrent factory invocation

**By:** Ripley
**Status:** Implemented
**What:** Changed `ConcurrentDictionary<string, ICacheStore>` to `ConcurrentDictionary<string, Lazy<ICacheStore>>`. `Lazy<T>` (default `LazyThreadSafetyMode.ExecutionAndPublication`) guarantees factory runs exactly once per key. `Dispose()` checks `IsValueCreated` before accessing `.Value`.
**Why:** `ConcurrentDictionary.GetOrAdd(key, factory)` doesn't guarantee single-invocation of the factory. Under contention, multiple `SqliteCacheStore` constructors raced on `InitializeSchema()` for the same SQLite file, producing `ArgumentOutOfRangeException` from SQLitePCL on Windows CI. Standard .NET pattern — use `Lazy<T>` wrapping whenever `ConcurrentDictionary.GetOrAdd` factories have side effects.

### 2026-03-07: AzDO caching strategy

**By:** Ripley
**Status:** Implemented
**What:** CachingAzdoApiClient decorator for IAzdoApiClient. Cache keys use `azdo:` prefix. Dynamic TTL by build status: completed builds 4h, in-progress 15s, timelines never while running (4h completed), logs/changes 4h, build lists 30s, test runs/results 1h. No DTO layer needed — AzDO model types are `sealed record` with `[JsonPropertyName]`, directly serializable. Reuses `ICacheStore.IsJobCompletedAsync` with composite keys.
**Why:** Follows CachingHelixApiClient pattern. Dynamic TTL prevents stale data for in-progress builds while minimizing API calls for stable data.

### 2026-03-07: AzdoService method signatures

**By:** Ripley
**Status:** Implemented
**What:** AzdoService business logic layer — all `buildIdOrUrl` params resolve via `AzdoIdResolver.Resolve()`. `GetBuildSummaryAsync` returns flattened `AzdoBuildSummary` with computed `Duration` and `WebUrl`. `GetBuildLogAsync` has `int? tailLines` for server-side slicing. `ListBuildsAsync` takes raw org/project (no URL resolution). No exception wrapping yet — `HttpRequestException` propagates; will add `AzdoException` when MCP tools need it.
**Why:** Mirrors HelixService pattern. URL resolution at service layer simplifies MCP tool implementations.

### 2026-03-07: AzdoMcpTools — return model types directly

**By:** Ripley
**What:** AzdoMcpTools returns AzDO model types directly instead of creating separate MCP result wrappers. API model types already have `[JsonPropertyName]` attributes. `azdo_log` returns plain `string` (no UseStructuredContent) matching `hlx_logs` pattern.
**Why:** Avoids duplicating DTOs that already have correct JSON serialization. If reshaping is needed later, add wrapper types then.
**Impact:** Lambert — test against `[JsonPropertyName]` names (camelCase). Kane — 7 new MCP tools need docs. Dallas — wrapper types deferred.

### 2026-03-08: AzDO Security Review Findings

**By:** Dallas
**What:** Security review of AzDO integration code (8 files)
**Why:** Pre-merge security gate for PR #6

---

## Findings

#### SEC-1 — Query Parameter Injection via `prNumber`
- **Severity:** Medium
- **Title:** Unescaped `prNumber` allows query parameter injection into AzDO API calls
- **Location:** `AzdoApiClient.cs`, `ListBuildsAsync`, line 41
- **Description:** The `prNumber` field is interpolated directly into the query string without `Uri.EscapeDataString`:
  ```csharp
  queryParams.Add($"branchName=refs/pull/{filter.PrNumber}/merge");
  ```
  Compare with `branch` (line 43) and `statusFilter` (line 49), which ARE properly escaped. A `prNumber` value like `"123&$top=99999"` would inject an additional query parameter into the AzDO API URL, potentially altering results.
- **Recommendation:** Either validate `prNumber` as an integer (`int.TryParse`) or apply `Uri.EscapeDataString`. Integer validation is preferred since PR numbers are always integers — this also prevents semantic abuse.
  ```csharp
  if (!string.IsNullOrEmpty(filter.PrNumber))
  {
      if (!int.TryParse(filter.PrNumber, out var prNum))
          throw new ArgumentException("prNumber must be a valid integer.", nameof(filter));
      queryParams.Add($"branchName=refs/pull/{prNum}/merge");
  }
  ```
- **Exploit scenario:** An MCP client sends `prNumber: "123&definitions=999"` to the `azdo_builds` tool. The injected `definitions` parameter overrides or supplements the intended query, returning builds from a different pipeline than expected. Impact is limited to data integrity (results are still from `dev.azure.com`, not SSRF).

---

#### SEC-2 — HttpClient Created Without IHttpClientFactory
- **Severity:** Low
- **Title:** Raw `new HttpClient()` risks socket exhaustion under load
- **Location:** `HelixTool.Mcp/Program.cs`, line 57; `HelixTool/Program.cs`, lines 44, 616
- **Description:** Both DI registrations create `new HttpClient()` directly instead of using `IHttpClientFactory`. In the HTTP/MCP server where many scoped requests may be created, each scoped `AzdoApiClient` gets a new `HttpClient` instance. While not a direct security vulnerability, socket exhaustion under sustained load can cause denial of service via `SocketException`.
- **Recommendation:** Register a named `HttpClient` via `builder.Services.AddHttpClient<AzdoApiClient>()` or inject `IHttpClientFactory`. This also centralizes timeout and handler configuration.

---

#### SEC-3 — Unbounded Response Size on Log Retrieval
- **Severity:** Low
- **Title:** `GetBuildLogAsync` reads entire log into memory without size limits
- **Location:** `AzdoApiClient.cs`, `GetBuildLogAsync`, line 78
- **Description:** Build logs are read as a full string via `response.Content.ReadAsStringAsync`. AzDO build logs can be tens of megabytes. In multi-client HTTP mode, several concurrent log requests could exhaust server memory. The Helix side has the same pattern but it pre-dates multi-client mode.
- **Recommendation:** Add a configurable max response size (e.g., 10 MB) or stream logs with a size cutoff. The `tailLines` parameter in `AzdoService.GetBuildLogAsync` mitigates this at the service layer but only AFTER the full content is already in memory.

---

#### SEC-4 — No Configurable Timeout on AzDO HttpClient
- **Severity:** Low
- **Title:** Default 100s timeout may be too generous for CI tool use case
- **Location:** `HelixTool.Mcp/Program.cs`, line 57 (`new HttpClient()`)
- **Description:** The HttpClient uses the default 100-second timeout. A slow or unresponsive AzDO API could tie up server threads for extended periods. In multi-client HTTP mode, this could exhaust the thread pool.
- **Recommendation:** Set an explicit timeout (e.g., 30s) appropriate for the AzDO API call patterns. Can be centralized if SEC-2's `IHttpClientFactory` recommendation is adopted.

---

#### SEC-5 — AzCliAzdoTokenAccessor Not Thread-Safe
- **Severity:** Info
- **Title:** Race condition on `_resolved`/`_cachedToken` in singleton accessor
- **Location:** `IAzdoTokenAccessor.cs`, `AzCliAzdoTokenAccessor`, lines 23–37
- **Description:** `_cachedToken` and `_resolved` are not protected by a lock or `Lazy<T>`. In HTTP mode where the accessor is singleton and multiple requests arrive concurrently on startup, `TryGetAzCliTokenAsync` could execute multiple times. Not exploitable — string references are atomic in .NET, and double-execution just wastes a process spawn. However, it deviates from the `Lazy<T>` pattern established for `CacheStoreFactory`.
- **Recommendation:** Use `SemaphoreSlim` or `Lazy<Task<string?>>` for one-shot initialization, consistent with the project's existing patterns.

---

#### SEC-6 — AzDO CLI Token Never Refreshed After Initial Resolution
- **Severity:** Info
- **Title:** Singleton token accessor caches az CLI token indefinitely
- **Location:** `IAzdoTokenAccessor.cs`, `AzCliAzdoTokenAccessor`, line 32–33
- **Description:** Once `_resolved = true`, the cached token is returned forever. Azure CLI tokens (Entra ID JWT) typically expire after ~1 hour. For long-running MCP servers, the server will start returning 401 errors after token expiry. Not a security vulnerability (fails closed), but an operational concern for availability.
- **Recommendation:** Either track token expiry and re-fetch, or document that long-running servers should use `AZDO_TOKEN` env var with externally managed rotation.

---

## Areas Reviewed — No Issues Found

#### ✅ Command Injection (`AzCliAzdoTokenAccessor`)
The `az account get-access-token` command uses only hardcoded constants (`AzdoResourceId`). No user-controlled input flows into `ProcessStartInfo.Arguments`. `UseShellExecute = false` prevents shell metacharacter interpretation. **Safe.**

#### ✅ SSRF (`AzdoApiClient.BuildUrl`)
All HTTP requests are constructed via `BuildUrl` which hardcodes `https://dev.azure.com/` as the base URL. `org` and `project` parameters are escaped with `Uri.EscapeDataString`, preventing authority override (`@`), path traversal (`../`), or fragment injection (`#`). Even raw MCP parameters (in `azdo_builds` tool) cannot redirect requests to a non-AzDO host. **Safe.**

#### ✅ SSRF (`AzdoIdResolver`)
The resolver validates the host is either `dev.azure.com` or `*.visualstudio.com` and throws `ArgumentException` for all other hosts. Extracted org/project values are then used with `BuildUrl` (which hardcodes the target host). URL parsing uses `Uri.TryCreate` — no regex, no ReDoS risk. **Safe.**

#### ✅ Token Leakage
- Tokens are never logged or included in error messages. `ThrowOnAuthFailure` says "Set AZDO_TOKEN or run 'az login'" without echoing the token.
- `ThrowOnUnexpectedError` includes a 500-char snippet of the AzDO error response body — this is API error text, not credentials.
- Cache stores serialized response data, not tokens. Cache keys include org/project but not token material.
- `AzCliAzdoTokenAccessor` catches all exceptions silently (returns `null`) — no stack traces that might reveal token fragments.
**Safe.**

#### ✅ Cache Isolation (Multi-User HTTP Mode)
- `IAzdoTokenAccessor` is singleton (shared server credentials) — correct for server-side AzDO auth.
- `ICacheStore` is scoped by Helix token hash via `CacheOptions.AuthTokenHash` → separate SQLite databases per user.
- `CachingAzdoApiClient` cache keys use `CacheSecurity.SanitizeCacheKeySegment()` for org/project — path traversal and key delimiter injection are prevented.
- AzDO data cached under one user's Helix token hash cannot be accessed by another user.
**Safe.**

#### ✅ TLS Enforcement
`BuildUrl` hardcodes `https://` scheme. `AzdoIdResolver` accepts `http://` URLs for parsing only — the actual API request always uses the HTTPS URL from `BuildUrl`. Default `HttpClient` validates TLS certificates (no custom `HttpClientHandler`). **Safe.**

#### ✅ Input Validation (MCP Parameters)
- `buildId` parameters pass through `AzdoIdResolver.Resolve()` which validates format (integer or recognized AzDO URL).
- Integer parameters (`logId`, `runId`, `top`, `definitionId`) are type-safe at the MCP schema level.
- `branch` and `statusFilter` are properly `Uri.EscapeDataString`-escaped.
- Exception: `prNumber` — see SEC-1.

#### ✅ Consistency with Existing Helix Patterns
- Cache key sanitization reuses `CacheSecurity.SanitizeCacheKeySegment()` from the Helix caching code.
- URL construction uses `Uri.EscapeDataString`, consistent with Helix URL handling.
- Error handling follows the established `ThrowOnAuthFailure`/`ThrowOnUnexpectedError` pattern.
- The decorator caching pattern mirrors `CachingHelixApiClient`.

---

## Verdict

**PR #6 is safe to merge with one recommended fix (SEC-1).**

- **SEC-1 (Medium)** is the only finding that warrants a code change before merge. The fix is a one-line `int.TryParse` validation — minimal risk, high value. Without it, an MCP client can inject arbitrary query parameters into AzDO API calls, which is a violation of the principle of least surprise even though the blast radius is limited to `dev.azure.com`.

- **SEC-2/3/4 (Low)** are real but non-blocking improvements that can be addressed in a follow-up. They affect availability under load, not confidentiality or integrity.

- **SEC-5/6 (Info)** are correctness and operational concerns, not security vulnerabilities. Document as known limitations.

**Conditional approval: merge after fixing SEC-1.** The remaining findings should be tracked as follow-up work items.

### 2026-03-08: AzDO MCP Tool Context-Limiting Defaults

**By:** Ripley
**Status:** Implemented

**What:** Added safe output-size defaults to all AzDO MCP tools, matching Helix tool patterns:

| Tool | Parameter | Default | Rationale |
|------|-----------|---------|-----------|
| `azdo_log` | `tailLines` | `500` | Matches `hlx_logs`; logs can be 100MB+ |
| `azdo_timeline` | `filter` | `"failed"` | New param; non-succeeded + parent chain |
| `azdo_changes` | `top` | `20` | Reasonable commit history window |
| `azdo_test_runs` | `top` | `50` | Enough for most builds |
| `azdo_test_results` | `top` | `200` | Matches `hlx_test_results`; was hardcoded 1000 |

**Why:**
- Unbounded outputs exhaust agent context windows
- All parameters remain nullable/overridable for callers needing more data
- Defaults live in MCP tool method signatures (not service code)
- Cache keys include limit parameters to prevent stale partial results
- Timeline filtering is client-side (AzDO API has no timeline filter support): identifies non-succeeded records + walks parentId chain for hierarchical context

### 2026-03-08: User directive — AzDO artifacts must follow Helix patterns

**By:** Larry Ewing (via Copilot)
**What:** AzDO artifact and attachment tools must follow the same caching and search patterns as the Helix tools (hlx_files, hlx_find_files, hlx_search_file, hlx_download)
**Why:** User request — captured for team memory. Consistency between Helix and AzDO tool behavior is important for agent usability.

### 2026-03-08: AzDO Artifact/Attachment Test Patterns

**By:** Lambert
**Status:** Informational

**What:** 33 tests added for `azdo_artifacts` and `azdo_test_attachments` MCP tools. Patterns established:
1. CamelCase JSON assertions: use `GetProperty("name").GetString()` to avoid xUnit2002 on `JsonElement` (value type).
2. TestAttachments top limiting happens in service layer via `Take(top)` — AzDO API doesn't support server-side limiting for attachments.
3. Artifact caching uses `ImmutableTtl` (4h). Attachment caching uses `TestTtl` (1h).

**Why:** Documents test conventions and caching decisions for AzDO artifact tools. Total test count: 700.

### 2026-03-08: AzDO documentation uses subsections within existing README structure

**By:** Kane
**What:** AzDO tools are documented as a `### AzDO Tools` subsection under `## MCP Tools`, AzDO auth as `### Azure DevOps` under `## Authentication`, and AzDO TTLs as inline additions under `## Caching` — rather than creating separate top-level sections.
**Why:** Keeps the README scannable and reinforces that Helix and AzDO are parts of the same tool. The MCP Configuration section needed no changes because the same MCP server serves both tool sets. This pattern should be followed for any future API domains added to hlx.

### 2026-03-08: llmstxt updated with AzDO tools under a separate "AzDO MCP Tools" subsection

**By:** Kane
**What:** The `llmstxt` command output now includes all 9 AzDO MCP tools in a dedicated subsection, plus AzDO auth chain and AzDO-specific caching TTLs.
**Why:** LLM agents reading `llmstxt` need to know about AzDO tools to use them. Keeping Helix and AzDO tool lists visually separated makes it clear which tools work with which system.

### 2026-03-08: IHttpClientFactory with named clients for HTTP lifecycle management

**By:** Ripley
**What:** Replaced static `HttpClient` in HelixService and `new HttpClient()` in AzdoApiClient DI with IHttpClientFactory named clients ("HelixDownload" and "AzDO"), both configured with 5-minute timeout.
**Why:** Static HttpClient causes socket exhaustion and blocks DNS refresh. IHttpClientFactory manages handler lifecycle automatically. Named clients allow different timeout/config per use case. The optional `HttpClient?` constructor parameter on HelixService preserves backward compatibility with 17 test files.

### 2026-03-08: HttpCompletionOption.ResponseHeadersRead for all AzDO HTTP requests

**By:** Ripley
**What:** All AzDO API client methods (GetAsync, GetListAsync, GetBuildLogAsync) now use ResponseHeadersRead instead of default ResponseContentRead.
**Why:** Prevents buffering entire response bodies in memory before processing. Especially important for large build logs. HelixService.DownloadFromUrlAsync already used this pattern — now consistent across both backends.

### 2026-03-08: AzDO CLI commands mirror MCP tools 1:1

**By:** Ripley
**What:** Added 9 `azdo-*` CLI subcommands in a separate `AzdoCommands` class, each calling the same `AzdoService` methods as the MCP tools.
**Why:** Users without MCP clients can now use all AzDO functionality directly from the command line. Follows the existing Helix pattern (Commands class with ConsoleAppFramework attributes). Timeline filtering logic is duplicated from MCP tools rather than extracted to a shared method — flagging this for Dallas review on whether to extract.

### 2026-03-08: Proactive Test Patterns for SEC-2/3/4

**By:** Lambert
**Date:** 2026-03-08

#### Context
Ripley is working on SEC-2 (IHttpClientFactory), SEC-3 (streaming), SEC-4 (timeout config), and AzDO CLI subcommands in parallel. Tests written proactively to validate expected behavior.

#### Test Files Created
- `src/HelixTool.Tests/HttpClientConfigurationTests.cs` — 13 tests
- `src/HelixTool.Tests/StreamingBehaviorTests.cs` — 18 tests
- `src/HelixTool.Tests/AzDO/AzdoCliCommandTests.cs` — 22 tests

#### Key Patterns Established

### Timeout vs. Cancellation Testing

- Timeout: `TaskCanceledException` without cancelled token → wraps in `HelixException`
- Cancellation: `TaskCanceledException` with cancelled token → rethrows directly
- Use `DelayingHttpMessageHandler` to simulate real timeouts

### NSubstitute for Stream-returning methods

- Cannot use `.ThrowsAsync()` on `Task<Stream>` — use `.Returns<Stream>(_ => throw ...)` instead

### Init-only Properties in Test Helpers

- Models with `init;` setters need all values in object initializer
- Create helper methods with optional parameters: `CreateBuild(id, status, result, startTime?, finishTime?)`

#### What May Need Updating After Ripley's Changes
- **SEC-2:** If IHttpClientFactory is used, add tests for named client configuration
- **SEC-3:** If streaming replaces read-all-to-string, StreamingBehaviorTests may need refactoring
- **SEC-4:** If explicit timeout is configured in DI, add a test asserting the configured value
- **CLI:** If AzDO CLI commands are added as a new Commands class, add registration and argument parsing tests

### 2025-07-18: Promoted IsFileSearchDisabled to public visibility

- `HelixService.IsFileSearchDisabled` became public so the extracted `HelixTool.Mcp.Tools` assembly could reuse the same guard without new friend-assembly coupling.
- Shared static helpers that cross assembly boundaries should either move to a neutral utility type or become intentionally public.

### 2026-03-08: AzDO Search/Filter Gap Analysis (consolidated)

**By:** Ash
**Status:** P0 (`azdo_search_log`) implemented in PR #10. See "CI-analysis skill usage patterns" below for detailed analysis.

**Summary:** Gap analysis identified `azdo_search_log` (P0), test results name filter (P1), and timeline name filter (P2) as missing AzDO search capabilities. `SearchLines()` extraction to `TextSearchHelper` recommended and implemented. Superseded by the CI-analysis skill usage study which validated priorities from real agent usage patterns.

### 2026-03-08: CI-analysis skill usage patterns and AzDO search recommendations

**By:** Ash

**What:** Deep analysis of how the ci-analysis skill (in `lewing/agent-plugins` and `blazor-playground/copilot-skills`) uses AzDO and Helix tools in practice, with updated recommendations for AzDO search/filter tools based on real usage patterns.

**Why:** The prior AzDO search gap analysis (ash-azdo-search-gaps.md) was based on API surface review. This analysis examines how agents *actually* use these tools during CI failure investigation, revealing specific pain points and new tool ideas that weren't visible from the API alone.

---

## 1. How the CI-Analysis Skill Currently Works

### Tool Call Flow (happy path)

1. **Step 0 — Gather PR context**: GitHub MCP (`pull_request_read`, `list_commits`, `get_file_contents`) to classify PR type (code/flow/backport/merge), read labels, check existing comments
2. **Step 1 — Run Get-CIStatus.ps1**: PowerShell script that queries AzDO timeline, extracts Helix job IDs from build logs, fetches Helix console logs, and produces a `[CI_ANALYSIS_SUMMARY]` JSON
3. **Step 1b — Supplement with MCP**: When script output is insufficient, use `ado-dnceng-public` MCP tools (`get_builds`, `get_build_log`, `get_build_log_by_id`) for additional data
4. **Step 2 — Analyze**: Cross-reference `failedJobDetails` with `knownIssues`, correlate with PR changes, check build progression for multi-commit PRs
5. **Step 3 — Deep dive (if needed)**: Helix MCP tools (`hlx_search_log`, `hlx_search_file`, `hlx_status`, `hlx_test_results`, `hlx_logs`) for individual work item investigation
6. **Step 4 — Binlog analysis (if needed)**: `hlx_download` + `mcp-binlog-tool` for MSBuild-level diagnosis

### Key Design Decisions

- **Script does heavy lifting**: The PowerShell script handles AzDO auth, timeline parsing, Helix URL extraction, and error categorization. Agents are instructed to parse its output first, not re-query.
- **MCP tools as supplements, not primaries for initial scan**: The script can access AzDO and Helix via REST APIs independently. MCP tools fill gaps the script can't cover.
- **Helix search is remote-first**: `hlx_search_log` and `hlx_search_file` are preferred over `hlx_logs` (full download). This was a trained behavior — agents initially defaulted to download-first until explicit "prefer remote search" guidance was added.

### Service Access Stack

| Service | Primary | Fallback |
|---------|---------|----------|
| AzDO builds/timeline | `ado-dnceng-public-*` MCP tools | Script via `Invoke-RestMethod` |
| AzDO build logs | `ado-dnceng-public-pipelines_get_build_log_by_id` | Script via `Invoke-RestMethod` |
| Helix job status | `hlx_status` | Script / `curl` |
| Helix work item errors | `hlx_search_log` (remote search) | `hlx_logs` (full download) |
| Helix artifacts | `hlx_search_file` / `hlx_files` | `hlx_download` |
| GitHub | GitHub MCP tools | `gh` CLI |

---

## 2. Specific Pain Points Where Search/Filter Capabilities Would Help

### Pain Point 1: AzDO Build Log Crawling (P0 — validates prior analysis)

**Evidence (training log session b563ef92):** Agent spent 7+ tool calls crawling AzDO build logs after the script had already provided the error. The agent guessed log IDs by file size instead of searching for error content. Even after training improvements (Change 1), the script's `Get-BuildLog` function still fetches *entire* log content (`Invoke-RestMethod` on the full log endpoint) and then does client-side pattern matching with `Select-String`.

**What happens today:**
- Script fetches full build log via REST API (no size limit, no server-side search)
- Script has `Extract-BuildErrors` function with ~11 regex patterns for common errors (CS/MSB/NU errors, linker errors, etc.)
- Agents sometimes need to look at logs the script didn't fetch (e.g., checkout logs for build progression, step logs for package restore errors)
- When agents use `ado-dnceng-public-pipelines_get_build_log_by_id`, they get raw text — may be 10K+ lines with no way to search

**What `azdo_search_log` would enable:**
- Agent asks "search log 5 of build 1283986 for 'Merge' pattern" → gets 3 matching lines with context (vs downloading 650+ lines of git output)
- Agent asks "search log 565 for 'error'" → gets error lines immediately (vs downloading entire step log)
- The delegation pattern for build progression (Pattern 5 in delegation-patterns.md) explicitly needs to search checkout logs for a merge line around line 500-650 — a search tool would replace the workaround of fetching with `startLine` guessing

### Pain Point 2: Helix Console Log Search Already Works — Parity Gap (P0)

**Evidence (training log Session 3):** After SkillResearcher found that `hlx_search_log` wasn't mentioned in the skill, training added it and achieved 5/5 compliance across all models. Agents now reliably use `hlx_search_log(pattern="error", contextLines=3)` as the first step for Helix investigation.

**The parity gap:** Helix has `hlx_search_log` (pattern search with context lines) and `hlx_search_file` (search uploaded artifacts). AzDO has neither. When the agent needs to search an AzDO build log, it must:
1. Fetch the entire log with `get_build_log_by_id`
2. Pipe through `Select-String` in PowerShell (burns context window with full log content)
3. Or use the script's `Extract-BuildErrors` (only works for patterns the script knows about)

An `azdo_search_log` tool matching `hlx_search_log`'s interface would let agents use the same mental model for both services.

### Pain Point 3: Build Log ID Discovery (P1 — NEW finding)

**Evidence:** The `manual-investigation.md` reference and `build-progression-analysis.md` both mention hardcoded log IDs (`logId: 5` for checkout). The script discovers log IDs by traversing the timeline to find Helix tasks. But when agents need to search a *specific* step's log (e.g., the "Restore" step, or "Send to Helix"), they have no way to find its log ID without fetching the full timeline and scanning for matching record names.

**What would help:** The existing `ado-dnceng-public-pipelines_get_build_log` tool already returns a list of all logs with metadata. But a filter parameter (e.g., `nameFilter` on the timeline/records endpoint) would let agents find the right log ID without processing the full timeline.

### Pain Point 4: AzDO Test Results Name Filtering (P1 — validates prior analysis)

**Evidence:** The skill's `sql-tracking.md` reference has agents creating `failed_jobs` tables to track individual failures. The script extracts `failedJobDetails` from AzDO test runs, but when agents need to search for a specific test name across multiple builds (e.g., for build progression analysis), they must iterate through each build's test results.

**What would help:** A `testNameFilter` on the test results MCP tool. The `build_failures` SQL table pattern (build-progression-analysis.md) queries `SELECT test_name, COUNT(DISTINCT build_id) as fail_count` — this currently requires fetching all failures from each build and inserting into SQL. A server-side name filter would reduce round trips.

### Pain Point 5: Delegation Context Budget (P1 — NEW finding)

**Evidence (training log, delegation patterns):** The skill defines 5 delegation patterns for subagents. Pattern 1 (scanning console logs) and Pattern 4 (parallel artifact extraction) both involve subagents that need to search through content. Subagents run in separate context windows — they can't share the script's output. Each subagent independently fetches logs/artifacts.

**Impact:** When the main agent delegates "search 5 work items for [FAIL] lines" to a subagent, that subagent currently must either:
- Use `hlx_search_log` for each work item (efficient — remote search)
- Fetch full logs with `hlx_logs` for each (context-expensive)

For AzDO build logs, there's no search equivalent. A subagent delegated to "extract target HEAD from build checkout log" must fetch the entire log.

---

## 3. Updated Recommendations for AzDO Search Tools

### P0: `azdo_search_log` — Search within AzDO build step logs

**Priority elevated from prior analysis.** Real skill usage confirms this is the #1 gap.

**Interface (matching `hlx_search_log` for consistency):**
```
azdo_search_log(
    buildId: int,         // AzDO build ID
    logId: int,           // Log ID (from build log list or timeline)
    pattern: string,      // Search pattern (case-insensitive)
    contextLines: int = 2,  // Lines before/after each match
    maxMatches: int = 50    // Limit results
)
```

**Implementation notes:**
- Reuse `SearchLines()` from `HelixService` (already identified in prior analysis)
- AzDO REST API has no server-side search — must fetch log content and search client-side
- Could use `startLine`/`endLine` on `get_build_log_by_id` to paginate large logs, but search is still client-side
- Consider caching fetched logs to avoid re-downloading on repeated searches

**Use cases from real skill patterns:**
1. Build progression: search checkout log for `"HEAD is now at"` merge line
2. Error diagnosis: search step log for `"error"` pattern when script didn't capture it
3. Package restore: search restore log for `"NU1102"` or `"Unable to find package"`
4. Helix job discovery: search "Send to Helix" log for `"Sent Helix Job"` GUIDs

### P1: `azdo_search_timeline` — Search timeline records by name

**New recommendation** (not in prior analysis).

**Interface:**
```
azdo_search_timeline(
    buildId: int,
    nameFilter: string,     // Substring match on record name
    typeFilter: string = null,  // "Job", "Task", "Stage"
    resultFilter: string = null // "failed", "succeeded", "canceled"
)
```

**Use cases:**
1. Finding the log ID for a specific step (e.g., "Checkout", "Restore", "Send to Helix")
2. Filtering to just failed jobs (already partially done — `hlx_status` does this for Helix)
3. Finding Helix-related tasks within a specific job

**Implementation notes:**
- The AzDO timeline API returns all records. Client-side filter is fine.
- This replaces the pattern of fetching full timeline → PowerShell `Where-Object` filtering

### P1: Test results name filter (unchanged from prior analysis)

Add `testNameFilter` parameter to the existing `azdo_test_results` tool.

### P2: Timeline name filter (merged into P1 `azdo_search_timeline` above)

---

## 4. NEW Tool Ideas from Studying the Skill

### NEW P1: `azdo_search_log_across_steps` — Multi-step log search *(superseded by 2026-03-09 full design spec below)*

Superseded. See `### 2026-03-09: azdo_search_log_across_steps design spec` for the full implementation design.

### NEW P2: `azdo_build_summary` — Structured build failure summary

**Evidence:** The script produces `[CI_ANALYSIS_SUMMARY]` JSON with structured failure data. When MCP tools are the primary access method (no script), agents must piece together build status from timeline + test results + log snippets manually.

**Interface:**
```
azdo_build_summary(
    buildId: int,
    includeHelixJobs: bool = false  // Extract Helix job IDs from logs
)
```

Returns: `{ result, failedJobs: [{name, result, logId, errorSnippet}], canceledJobs: [...] }`

This would replicate the script's core value proposition in a single MCP tool call.

### NEW P2: `azdo_log_error_extract` — Pre-built error extraction

**Evidence:** The script's `Extract-BuildErrors` function has 11 carefully crafted regex patterns for .NET build errors. This domain knowledge (CS errors, MSB errors, NU errors, linker errors, AzDO annotations) could be baked into a tool.

**Interface:**
```
azdo_log_error_extract(
    buildId: int,
    logId: int,
    contextLines: int = 5
)
```

Returns only matching error lines with context — no need for the agent to know patterns. This is a specialized version of `azdo_search_log` with built-in .NET error knowledge.

---

## 5. Cross-Cutting Observations

### The "Remote Search First" Pattern is Proven

Training Session 3 proved that when agents have search tools (`hlx_search_log`), they prefer them over download (`hlx_logs`) — 5/5 across all 3 model families after guidance. This validates the entire `azdo_search_log` approach: agents WILL use search tools if they exist, and it dramatically reduces context consumption.

### 🚨 Blockquote Rules Drive Universal Compliance

The training log validates that format matters more than content depth. Every `🚨` rule in SKILL.md achieved 3/3 model compliance. When we add `azdo_search_log`, the skill should get a `🚨` rule: "Prefer `azdo_search_log` over `get_build_log_by_id` for finding specific content in build logs."

### Script vs MCP Tools Tension

The skill relies on a PowerShell script (Get-CIStatus.ps1, ~2000 lines) for initial data gathering, with MCP tools as supplements. As MCP tools gain search/filter capabilities, the script becomes less necessary. The long-term trajectory is: script handles orchestration logic (multi-step workflow), MCP tools handle individual data access. `azdo_build_summary` (P2) would be a step toward making the script optional for simple investigations.

### `SearchLines()` Extraction is the Key Implementation Step

The prior analysis identified that `SearchLines()` in `HelixService` should be extracted to a shared utility. This analysis confirms the need: both `azdo_search_log` and `azdo_search_log_across_steps` would use the same search logic. Extract to `TextSearchHelper.SearchLines()` in Core, then both Helix and AzDO tools can use it.

---

## Priority Summary (Updated)

| Priority | Tool | Status | Evidence |
|----------|------|--------|----------|
| **P0** | `azdo_search_log` | Confirmed from real usage | Training log session b563ef92, delegation Pattern 5, manual-investigation.md |
| **P1** | `azdo_search_timeline` | NEW — from skill analysis | Timeline traversal in multiple reference docs |
| **P1** | `azdo_search_log_across_steps` | NEW — from delegation patterns | Multi-step search needed for Helix URL discovery |
| **P1** | Test results name filter | Confirmed from real usage | build-progression-analysis.md, sql-tracking.md |
| **P2** | `azdo_build_summary` | NEW — script replacement path | Get-CIStatus.ps1 core function replicated as MCP tool |
| **P2** | `azdo_log_error_extract` | NEW — domain-specific search | Script's `Extract-BuildErrors` patterns |

### 2026-03-08: Extract search types to top-level namespace

**By:** Ripley  
**Date:** 2026-03-08  
**Context:** azdo_search_log implementation

## What

Extracted `LogMatch`, `LogSearchResult`, and `FileContentSearchResult` record types from `HelixService` (nested records) to top-level types in the `HelixTool.Core` namespace, in the new `TextSearchHelper.cs` file.

## Why

`SearchLines()` needed to be shared between Helix and AzDO search operations. Since `AzdoService` lives in `HelixTool.Core.AzDO`, the return types couldn't remain nested inside `HelixService`. Making them top-level is cleaner and follows the existing pattern where shared types are accessible across sub-namespaces.

## Impact

- Any code referencing `HelixService.LogSearchResult` etc. needs to drop the `HelixService.` prefix (no existing code used it that way)
- Tests already had `TextSearchHelperTests.cs` anticipating this extraction
- No breaking changes to MCP tool DTOs (those use their own `SearchMatch`/`SearchLogResult` types in `HelixTool.Mcp.Tools`)

### 2025-07-14: Incremental log fetching for AzDO build logs

- AzDO log APIs should support optional `startLine`/`endLine` ranges so tail reads, delta refresh, and cross-step search avoid full-log downloads when possible.
- Caching uses a full-log cache plus a short-lived freshness marker for in-progress builds, appending deltas instead of repeatedly re-downloading the entire log.

### 2026-03-09: azdo_search_log_across_steps design spec

**By:** Dallas
**What:** Incremental search across ALL log steps in an AzDO build, ranked by failure likelihood, with early termination.
**Why:** The existing `azdo_search_log` requires the caller to already know *which* log ID to search. In a build with 160–380 timeline records, an AI agent must make dozens of sequential tool calls to find the needle. This tool automates the scan-and-rank pattern that a human would follow: check failed steps first, skip boilerplate, stop when enough matches are found.

---

## 1. Tool Identity

| Surface | Name | Description |
|---------|------|-------------|
| MCP | `azdo_search_log_across_steps` | Search ALL log steps in an Azure DevOps build for lines matching a pattern. Automatically ranks logs by failure likelihood (failed tasks first, then tasks with issues, then large succeeded logs) and returns matches incrementally. Stops early when maxMatches is reached. Use instead of manually iterating azdo_search_log across many log IDs. |
| CLI | `hlx azdo search-log-all` | Search all build log steps for a pattern, ranked by failure priority. |

The name uses `across_steps` rather than `across_logs` because MCP consumers think in pipeline terms (stages/jobs/steps), not log IDs.

## 2. Parameters

### MCP Tool Signature

```csharp
[McpServerTool(Name = "azdo_search_log_across_steps",
               Title = "Search All AzDO Build Logs",
               ReadOnly = true,
               UseStructuredContent = true)]
public async Task<CrossStepSearchResult> SearchLogAcrossSteps(
    [Description("AzDO build ID (integer) or full AzDO build URL")]
    string buildIdOrUrl,

    [Description("Text pattern to search for (case-insensitive substring match)")]
    string pattern = "error",

    [Description("Lines of context before and after each match (default: 2)")]
    int contextLines = 2,

    [Description("Maximum total matches across all logs (default: 50). Search stops early once reached.")]
    int maxMatches = 50,

    [Description("Maximum number of individual log steps to download and search (default: 30). Limits API calls for very large builds.")]
    int maxLogsToSearch = 30,

    [Description("Minimum line count to include a log in the search (default: 5). Filters out tiny boilerplate logs.")]
    int minLogLines = 5)
```

### CLI Signature

```
hlx azdo search-log-all <buildId> [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N] [--json]
```

### Validation Rules

| Parameter | Rule | Exception |
|-----------|------|-----------|
| `pattern` | `ArgumentException.ThrowIfNullOrWhiteSpace` | `ArgumentException` |
| `contextLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| `maxMatches` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `maxLogsToSearch` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `minLogLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| env check | `HelixService.IsFileSearchDisabled` | `InvalidOperationException` / `McpException` |

No regex. Substring match only (`string.Contains(pattern, OrdinalIgnoreCase)`).

## 3. Return Types

### New types in `AzdoModels.cs` (Core layer)

```csharp
/// <summary>A log entry from the AzDO Build Logs List API (GET _apis/build/builds/{id}/logs).</summary>
public sealed record AzdoBuildLogEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("lineCount")]
    public long LineCount { get; init; }

    [JsonPropertyName("createdOn")]
    public DateTimeOffset? CreatedOn { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Matches found in a single log step during a cross-step search.</summary>
public sealed class StepSearchResult
{
    [JsonPropertyName("logId")] public int LogId { get; init; }
    [JsonPropertyName("stepName")] public string StepName { get; init; } = "";
    [JsonPropertyName("stepType")] public string? StepType { get; init; }
    [JsonPropertyName("stepResult")] public string? StepResult { get; init; }
    [JsonPropertyName("parentName")] public string? ParentName { get; init; }
    [JsonPropertyName("lineCount")] public long LineCount { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("matches")] public List<LogMatch> Matches { get; init; } = [];
}

/// <summary>Result of searching across all log steps in a build.</summary>
public sealed class CrossStepSearchResult
{
    [JsonPropertyName("build")] public string Build { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalLogsInBuild")] public int TotalLogsInBuild { get; init; }
    [JsonPropertyName("logsSearched")] public int LogsSearched { get; init; }
    [JsonPropertyName("logsSkipped")] public int LogsSkipped { get; init; }
    [JsonPropertyName("totalMatchCount")] public int TotalMatchCount { get; init; }
    [JsonPropertyName("stoppedEarly")] public bool StoppedEarly { get; init; }
    [JsonPropertyName("steps")] public List<StepSearchResult> Steps { get; init; } = [];
}
```

### New MCP result type in `McpToolResults.cs`

Not needed. `CrossStepSearchResult` already uses `[JsonPropertyName]` on all properties and `LogMatch` is already in `TextSearchHelper.cs`. The result type can live in Core because it's not reshaping — it IS the domain result. Same pattern as `TimelineSearchResult`.

## 4. Algorithm

### Phase 1: Metadata Collection (2 cheap API calls, parallelizable)

```
1. Resolve buildIdOrUrl → (org, project, buildId)
2. Parallel:
   a. GET _apis/build/builds/{buildId}/logs → List<AzdoBuildLogEntry>  (line counts, no content)
   b. GET _apis/build/builds/{buildId}/timeline → AzdoTimeline          (record states, log refs)
```

### Phase 2: Build Ranked Log Queue

Join timeline records to log entries by `record.Log.Id == logEntry.Id`:

```
For each timeline record with a log reference:
  - Lookup logEntry to get lineCount
  - Skip if lineCount < minLogLines (tiny boilerplate)
  - Assign priority bucket:
    Bucket 0: record.Result is "failed" or "canceled"
    Bucket 1: record.Issues is non-empty (warnings/errors attached)
    Bucket 2: record.Result is "succeededWithIssues"
    Bucket 3: record.Result is "succeeded" or null, lineCount >= minLogLines
  - Within each bucket: sort by lineCount descending (larger logs more likely to contain errors)
```

Orphan logs (logEntry.Id not referenced by any timeline record): appended at end (Bucket 4), sorted by lineCount desc. These are rare but possible in retried builds.

### Phase 3: Incremental Search (sequential downloads, early exit)

```
remainingMatches = maxMatches
logsSearched = 0

for each log in ranked queue (up to maxLogsToSearch):
  if remainingMatches <= 0: break (early exit)

  content = await GetBuildLogAsync(org, project, buildId, log.Id, ct)
  // Normalize line endings, split, search (reuse TextSearchHelper.SearchLines)
  searchResult = TextSearchHelper.SearchLines(
      identifier: $"log:{log.Id}",
      lines: normalizedLines,
      pattern: pattern,
      contextLines: contextLines,
      maxMatches: remainingMatches   // ← pass REMAINING, not total
  )

  if searchResult.Matches.Count > 0:
    add StepSearchResult with step metadata + matches
    remainingMatches -= searchResult.Matches.Count

  logsSearched++
```

### Normalization

Same `\r\n` → `\n`, `\r` → `\n` normalization as `SearchBuildLogAsync`. Extract into a private helper `NormalizeAndSplit(string content)` to DRY up.

## 5. Safety Guards & Limits

| Guard | Default | Rationale |
|-------|---------|-----------|
| `maxMatches` | 50 | Caps total matches across all logs. Primary context-overflow protection. |
| `maxLogsToSearch` | 30 | Caps API calls. A 380-log SDK build would need 380 HTTP requests without this. 30 covers all failures + the largest succeeded logs in typical builds. |
| `minLogLines` | 5 | Filters out ~60% of logs in a typical build (setup/teardown boilerplate with 1–4 lines). |
| No parallel downloads | — | Sequential downloads avoid hammering AzDO API. If we later add parallelism, limit to 3–5 concurrent. |
| `IsFileSearchDisabled` | env check | Same kill switch as all search tools. |
| No download size limit needed | — | `GetBuildLogAsync` already streams the response. Individual logs in AzDO builds rarely exceed 2MB. The `maxLogsToSearch` cap provides aggregate protection. |

### What about in-progress builds?

In-progress builds will have a partial timeline. The tool should work correctly — it just searches whatever logs exist. The timeline fetch is not cached for in-progress builds (established caching rule), so re-running the tool shows fresh results.

## 6. Relationship to `azdo_search_log`

**Complement, not replace.**

| Tool | Use case |
|------|----------|
| `azdo_search_log` | "Search log 47 for 'OutOfMemory'" — caller already knows the log ID (from timeline, from a previous search, from a URL). Fast, single API call. |
| `azdo_search_log_across_steps` | "Find 'error CS' anywhere in this build" — caller doesn't know which log(s) contain the pattern. Automated ranking + early exit. |

The `azdo_search_log_across_steps` description should mention `azdo_search_log` for targeted follow-up: "For targeted search of a specific log step, use azdo_search_log instead."

`azdo_search_log` remains the right tool when the caller has a specific log ID (common after `azdo_timeline`). The new tool is for the "I don't know where to look" workflow.

## 7. Interface & Client Changes

### `IAzdoApiClient` — new method

```csharp
/// <summary>List all build logs with metadata (line counts) without downloading content.</summary>
Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default);
```

### `AzdoApiClient` — implementation

```csharp
public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default)
{
    var url = BuildUrl(org, project, $"build/builds/{buildId}/logs");
    return await GetListAsync<AzdoBuildLogEntry>(url, ct);
}
```

### `CachingAzdoApiClient` — caching wrapper

Cache with same dynamic TTL rules (completed build → 4h, in-progress → 15s). The logs list is immutable once a build completes.

### `AzdoService` — new method

```csharp
public async Task<CrossStepSearchResult> SearchBuildLogAcrossStepsAsync(
    string buildIdOrUrl, string pattern,
    int contextLines = 2, int maxMatches = 50,
    int maxLogsToSearch = 30, int minLogLines = 5,
    CancellationToken ct = default)
```

This is where the ranking algorithm, incremental search, and early termination live. Follows existing pattern: MCP tool is thin wrapper, business logic in `AzdoService`.

## 8. Estimated Test Surface for Lambert

### Unit Tests (AzdoService layer)

| ID | Test | Notes |
|----|------|-------|
| T-1 | Empty build (no logs, no timeline) | Returns 0 matches, logsSearched=0 |
| T-2 | All logs below minLogLines | Returns 0 matches, all skipped |
| T-3 | Single failed log with matches | Bucket 0 prioritization, correct StepSearchResult |
| T-4 | Ranking order: failed → issues → succeededWithIssues → succeeded | Verify download order via mock call sequence |
| T-5 | Early termination at maxMatches | Set maxMatches=3, provide 5 matches across 2 logs, verify stoppedEarly=true and exactly 3 matches |
| T-6 | maxLogsToSearch limit | 50 eligible logs, maxLogsToSearch=5, verify only 5 downloaded |
| T-7 | Orphan logs (no timeline record) | Log in logs list but not in timeline → Bucket 4 |
| T-8 | Pattern not found in any log | Returns 0 matches, stoppedEarly=false |
| T-9 | Timeline record with no log reference | Skipped (no log to download) |
| T-10 | Context lines propagation | Verify contextLines flows to TextSearchHelper |
| T-11 | Line ending normalization | `\r\n` and `\r` normalized before search |

### MCP Tool Tests

| ID | Test | Notes |
|----|------|-------|
| M-1 | Successful search returns CrossStepSearchResult | UseStructuredContent=true, verify JSON shape |
| M-2 | IsFileSearchDisabled → McpException | Not InvalidOperationException |
| M-3 | Service throws InvalidOperationException → McpException | Exception remapping |
| M-4 | Service throws HttpRequestException → McpException | Exception remapping |
| M-5 | Service throws ArgumentException → McpException | Exception remapping |

### Integration-level Tests (IAzdoApiClient mock)

| ID | Test | Notes |
|----|------|-------|
| I-1 | GetBuildLogsListAsync returns correct AzdoBuildLogEntry list | Deserialization from AzDO format |
| I-2 | Caching: completed build logs list cached at 4h TTL | CachingAzdoApiClient test |
| I-3 | Caching: in-progress build logs list cached at 15s TTL | Dynamic TTL |

**Estimated total: ~19 tests.** Aligns with the ~700 existing test count.

## 9. Implementation Notes

### Extract `NormalizeAndSplit`

Both `SearchBuildLogAsync` and `SearchBuildLogAcrossStepsAsync` need the same line normalization. Extract to a private static method:

```csharp
private static string[] NormalizeAndSplit(string content)
{
    var normalized = content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
    var lines = normalized.Split('\n');
    if (normalized.EndsWith("\n", StringComparison.Ordinal) && lines.Length > 0)
        Array.Resize(ref lines, lines.Length - 1);
    return lines;
}
```

`SearchBuildLogAsync` should be updated to use this helper (minor refactor, not breaking).

### Timeline + Logs List Join

The join is by `timelineRecord.Log.Id == logEntry.Id`. Timeline records of type "Stage" and "Phase" rarely have logs, but if they do, include them. Records with `Log == null` are skipped (no downloadable log).

### Parallel Metadata Fetch

The timeline and logs list are independent API calls. Use `Task.WhenAll` to fetch both concurrently:

```csharp
var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
var logsListTask = _client.GetBuildLogsListAsync(org, project, buildId, ct);
await Task.WhenAll(timelineTask, logsListTask);
```

### Future Optimization: Parallel Log Downloads

Deferred. Sequential downloads are simpler and sufficient for Phase 1. If performance is a problem (unlikely with `maxLogsToSearch=30`), add a `SemaphoreSlim(3)` bounded parallel download later.

# Decision: Timeline search result types live in Core

**By:** Ripley
**Date:** 2025-07-19
**Context:** azdo_search_timeline implementation

## Decision

`TimelineSearchMatch` and `TimelineSearchResult` are defined in `HelixTool.Core.AzDO` (AzdoModels.cs), not in `McpToolResults.cs`. The MCP tool returns the Core types directly.

## Rationale

- Core can't reference Mcp.Tools (dependency direction: Mcp.Tools → Core, not reverse).
- Same pattern as `AzdoBuildSummary` — domain types in Core, MCP tools return them directly.
- `[JsonIgnore]` on `TimelineSearchMatch.Record` keeps MCP JSON flat while exposing the raw `AzdoTimelineRecord` for programmatic consumers and tests.

## Impact

- Future search-style features in Core should follow this pattern: define result DTOs with `[JsonPropertyName]` in Core, add `[JsonIgnore]` for any raw-data properties that shouldn't serialize.
- `McpToolResults.cs` is for MCP-specific wrapper types that don't map 1:1 to service returns (like `SearchBuildLogResult` which reshapes `LogSearchResult`).

---

### 2025-07-18: Performance review findings

- The main allocation hot spots were line-ending normalization, tail trimming, repeated substring work in pattern matching, and serializing large log strings unnecessarily.
- Prioritize perf fixes on search/log/cache paths where multi-megabyte content is handled repeatedly; minor cache-key and helper allocations are secondary.

### 2026-03-09: Cache format change — raw: prefix (Ripley perf fixes)

**By:** Ripley

**Context:** CachingAzdoApiClient stored log content via `JsonSerializer.Serialize<string>()`, double-escaping multi-MB strings. Changed to plain text with `raw:` sentinel prefix.

**Decision:** Backward-compatible migration via sentinel detection. `DeserializeLogContent` checks for `raw:` prefix first, falls back to JSON deserialization for legacy entries. No explicit migration step — natural TTL expiry handles transition.

**Risk:** Low. Legacy entries are still readable. New entries are written in the efficient format. Cache key structure is unchanged, so there's no key collision.

**For Dallas to review:** Is the `raw:` prefix approach acceptable long-term, or should we consider a versioned cache format? The prefix relies on log content never starting with `raw:` literally — extremely unlikely for AzDO build logs but worth noting.

### 2026-03-09: SearchConsoleLogAsync decoupled from disk download (Ripley perf fixes)

**By:** Ripley

**Context:** `SearchConsoleLogAsync` used `DownloadConsoleLogAsync` (stream→disk) then `File.ReadAllLinesAsync` (disk→memory). Changed to use `GetConsoleLogContentAsync` (stream→memory directly).

**Decision:** Safe to decouple because `DownloadConsoleLogAsync` is only used by the CLI download command and `SearchFileAsync` (which needs disk for binary detection). Search doesn't need disk presence.

**Risk:** None observed — 864/864 tests pass. If a future change adds caching or rate-limiting at the download layer, search would bypass it. Worth noting but not a current concern.

### 2026-03-09: Shared StringHelpers in Core (Ripley perf fixes)

**By:** Ripley

**Context:** Both AzdoService and HelixService had identical tail-trimming patterns. Extracted to `HelixTool.Core.StringHelpers` (internal static class).

**Decision:** `internal` visibility is sufficient — only Core code needs it. If CLI or MCP projects need it in the future, promote to `public`.

### 2026-03-09: CI repo profile analysis for MCP tool improvements

**By:** Ash
**What:** Analysis of CI repo profiles identifying improvements for MCP tool descriptions and error messages
**Why:** Real-world CI investigation patterns should inform tool guidance to reduce agent iteration cycles

---

## Executive Summary

Analyzed 6 CI repo profiles (runtime, aspnetcore, sdk, roslyn, efcore, vmr) plus the SKILL.md umbrella document against current MCP tool implementations in `HelixMcpTools.cs`, `AzdoMcpTools.cs`, and `HelixService.cs`. Found **14 actionable recommendations** across 4 categories. The highest-impact change is improving `helix_test_results` description and error messages — agents currently waste 2-3 tool calls discovering that TRX files don't exist for most repos.

---

## 1. Tool Description Improvements

### REC-1: `helix_test_results` — Add repo-aware guidance to tool description (P0)

**Current description:**
> "Parse TRX test result files from a Helix work item. Returns structured test results including test names, outcomes, durations, and error messages for failed tests. Auto-discovers all .trx files or filter to a specific one."

**Problem:** The name says "TRX" but the tool also parses xUnit XML. More critically, agents don't know that this tool **fails for 4 of 6 major repos** (aspnetcore, sdk, roslyn, efcore). They try it first, get an error, then have to figure out an alternative — wasting 2-3 tool calls per investigation.

**Recommended new description:**
> "Parse test result files (TRX or xUnit XML) from a Helix work item's blob storage. Returns structured test results including test names, outcomes, durations, and error messages. Auto-discovers files matching *.trx, testResults.xml, *.testResults.xml.txt. **Important:** Most dotnet repos do NOT upload test result files to Helix blob storage — the Arcade reporter consumes them locally and publishes to AzDO instead. This tool works for: runtime CoreCLR tests (job names containing 'coreclr_tests'), runtime iOS/Android XHarness tests. For all other repos/workloads (aspnetcore, sdk, roslyn, efcore, runtime libraries), use azdo_test_runs + azdo_test_results instead."

**Profiles motivating this:** runtime.md (lines 41-49, 63-71), aspnetcore.md (lines 9, 17-28), sdk.md (lines 37-39), roslyn.md (lines 43-47), efcore.md (lines 60-65)

---

### REC-2: `helix_search_log` — Add repo-specific search pattern guidance (P0)

**Current description mentions:**
> "Common patterns: '  Failed' (2 leading spaces) for xUnit test failures, 'Error Message:' for test error details, 'exit code' for process crashes."

**Problem:** The best search pattern varies dramatically by repo. `[FAIL]` works for runtime but not aspnetcore. `  Failed` works for aspnetcore but not runtime. Neither works for roslyn (crashes dominate). Agents pick the wrong pattern and get 0 results, then iterate.

**Recommended addition to description:**
> "Best search patterns vary by repo: runtime uses '[FAIL]' (xunit runner format); aspnetcore uses '  Failed' (2 leading spaces, dotnet test format); sdk uses 'Failed' or 'Error' (build-as-test, infra failures dominate); roslyn uses 'aborted' or 'Process exited' (crashes dominate, not assertion failures); efcore uses '[FAIL]' (xunit console runner). For build failures in any repo, try 'error MSB' or 'error CS'."

**Profiles motivating this:** runtime.md (lines 93-101), aspnetcore.md (lines 57-71), sdk.md (lines 90-101), roslyn.md (lines 75-86), efcore.md (lines 97-103)

---

### REC-3: `azdo_test_runs` — Warn about untrustworthy summary counts (P1)

**Current description:**
> "Get test runs for an Azure DevOps build. Returns test run summaries with total, passed, and failed counts. Use to get an overview of test execution for a build before drilling into individual test results with azdo_test_results."

**Problem:** Every single profile documents the same gotcha — run-level `failedTests: 0` metadata lies. Agents trust the summary and skip drilling into runs, missing real failures.

**Recommended addition:**
> "⚠️ Run-level failedTests counts can be 0 even when the run contains actual failures. Always call azdo_test_results on runs associated with failed Helix jobs — do not trust the summary count to determine if tests passed."

**Profiles motivating this:** runtime.md (lines 84-88), aspnetcore.md (lines 50-51), sdk.md (lines 63-67), roslyn.md (lines 68-71)

---

### REC-4: `azdo_timeline` — Add repo-specific task name guidance (P1)

**Current description:**
> "Get the build timeline showing stages, jobs, and tasks for an Azure DevOps build."

**Problem:** The Helix-dispatching task has different names across repos. Agents search for "Send to Helix" universally, but this fails for sdk (`🟣 Run TestBuild Tests`), roslyn (no separate task — Helix is inside `Run Unit Tests`/`Test`), and efcore (`Send job to helix`).

**Recommended addition to description:**
> "To find Helix job IDs, search for the Helix-dispatching task. Task names vary by repo: runtime/aspnetcore use 'Send to Helix'; sdk uses '🟣 Run TestBuild Tests'; roslyn embeds Helix inside 'Run Unit Tests' or 'Test' tasks (no separate Helix task); efcore uses 'Send job to helix'. Use azdo_search_timeline to find the right task."

**Profiles motivating this:** sdk.md (lines 48-55), roslyn.md (lines 16-29), efcore.md (line 178)

---

### REC-5: `helix_status` — Clarify exit code interpretation (P2)

**Current description:**
> "Get work item pass/fail summary for a Helix job. Returns structured JSON with job metadata, failed items (with exit codes, state, duration, machine, failureCategory), and passed count."

**Problem:** Exit codes mean different things and agents don't know how to interpret them. Also, runtime exit code 0 can mask test failures — the tool description should warn about this.

**Recommended addition:**
> "Common exit codes: 0 = passed (but runtime may report 0 with actual [FAIL] results — check console), 1 = test assertion failure, 130 = crash/SIGINT, -3 = timeout, -4 = infrastructure failure (docker/environment). Check failureCategory: 'InfrastructureError' or 'Crash' = infra problem, not a test bug."

**Profiles motivating this:** runtime.md (lines 152-156), sdk.md (lines 81-86), roslyn.md (lines 57-60), efcore.md (lines 91-95)

---

### REC-6: `azdo_search_timeline` — Suggest as the primary triage entry point (P2)

**Current description is functional but doesn't suggest workflow.** Every profile's recommended investigation order starts with `azdo_timeline(buildId, filter="failed")`, but agents often skip straight to Helix tools without knowing which jobs failed.

**Recommended addition:**
> "This is typically the first tool to call when investigating a build failure. Use pattern '*' with filter='failed' to see all failures, or search for specific task names like 'Send to Helix', 'Build', or 'Test' to find relevant steps."

**Profiles motivating this:** All 6 profiles list `azdo_timeline` as step 1

---

## 2. Error Message Improvements

### REC-7: `helix_test_results` "No test result files" error — Add actionable next steps (P0)

**Current error message (HelixService.cs line 931):**
> "No test result files found in work item '{workItem}'. Searched for: {patterns}."

The message already has crash-artifact detection and file-listing logic (good!), but the generic case (files found, no test results) suggests searching for `'  Failed'` only — which is wrong for runtime (`[FAIL]`) and roslyn (`aborted`).

**Recommended change:** Replace the generic fallback (line 943) with repo-aware guidance:

```
$"{fileNames.Count} files found but none match test result patterns. "
+ "Test results are likely published to AzDO instead of Helix blob storage. "
+ "Use azdo_test_runs + azdo_test_results to get structured test data. "
+ "For quick console triage, try helix_search_log with patterns: "
+ "'[FAIL]' (runtime/efcore), '  Failed' (aspnetcore), "
+ "'Failed' (sdk), 'aborted' (roslyn crashes)."
```

**Profile motivating this:** All non-runtime profiles document this as the primary failure mode

---

### REC-8: `helix_test_results` — Strengthen the "no files at all" message (P1)

**Current message (line 947):**
> "The work item has no uploaded files."

**Recommended change:**
> "The work item has no uploaded files. This typically means the test host crashed before producing results. Check helix_status for exit code and failureCategory. For roslyn, search console for 'aborted' or 'Process exited'. For sdk, check for architecture mismatch ('incompatible')."

**Profiles motivating this:** roslyn.md (lines 129-138), sdk.md (lines 131-138), efcore.md (line 111-112)

---

## 3. Missing Tool Capabilities

### REC-9: `azdo_search_log_across_steps` — Powerful but description needs workflow guidance (P1)

This tool exists and is well-implemented, but agents often don't know when to use it vs `azdo_search_log`. The profiles suggest clear use cases.

**Recommended addition to description:**
> "Especially useful for: VMR builds (44+ verticals, need to find which component failed — search for 'error MSB3073'); runtime builds (50-200+ jobs); sdk builds (find architecture mismatch errors across jobs — search for 'incompatible'). Use azdo_search_log when you already know the specific log ID."

**Profiles motivating this:** vmr.md (lines 86-93), runtime.md (lines 166-183)

---

### REC-10: No tool for extracting Helix job IDs from AzDO build logs (P2 — future capability)

**Problem:** All profiles describe a manual step: read AzDO task log → find Helix job GUID. For runtime/aspnetcore, job IDs appear in "Send to Helix" task output. For roslyn, they're in the body of "Run Unit Tests" output (`"Work item workitem_N in job <GUID> has failed"`). For sdk, they're in `🟣 Run TestBuild Tests` issue messages.

**Recommendation:** Consider a future tool or enhancement that auto-extracts Helix job IDs from an AzDO build. This would eliminate 1-2 manual tool calls per investigation. Could be implemented as an enhancement to `azdo_timeline` that parses known patterns from failed task messages/logs.

**Profiles motivating this:** All Helix-using profiles (runtime, aspnetcore, sdk, roslyn, efcore)

---

### REC-11: `helix_batch_status` could surface cross-job failure patterns (P3 — future)

**Problem:** When an entire Helix queue fails (e.g., efcore macOS crash exit -3 on all 20 work items, or sdk architecture mismatch on all items), agents need to recognize the pattern. Currently they check individual work items.

**Recommendation:** Future enhancement — `helix_batch_status` or `helix_status` could detect and flag patterns like "all N items on queue X failed with same exit code" → likely infrastructure issue, not individual test failures.

**Profiles motivating this:** efcore.md (lines 133-136), sdk.md (lines 126-128)

---

## 4. Search Pattern Guidance

### REC-12: Create a consolidated pattern reference in tool descriptions (P1)

The profiles document different console log patterns per repo. This knowledge should be surfaced somewhere accessible to agents. Options:

**Option A (recommended):** Enrich `helix_search_log` description with a concise pattern table (see REC-2 above)

**Option B:** Add a `helix_search_patterns` read-only tool that returns repo-specific search guidance when given a repo name. Low implementation cost but adds tool count.

**Option C:** Document in the ci-analysis skill prompt. Already partially done in the SKILL.md, but agents using raw MCP tools (not the skill) don't see it.

---

### REC-13: VMR builds don't use Helix — agents should know this (P1)

**Problem:** VMR (dotnet/dotnet) doesn't use Helix at all. Agents may waste calls trying `helix_status` on VMR builds.

**Recommended:** Add to `helix_status` description:
> "Note: VMR (dotnet/dotnet) builds do NOT use Helix. For VMR build failures, use azdo_timeline and azdo_search_log_across_steps to find build errors (typically 'error MSB3073')."

**Profile motivating this:** vmr.md (lines 14-18)

---

### REC-14: `azdo_test_results` — Clarify synthetic vs real results (P2)

**Problem:** SDK and roslyn produce synthetic `WorkItemExecution` results when Helix work items crash — these are not real test failures. Agents may misinterpret them.

**Recommended addition:**
> "For sdk/roslyn builds, watch for synthetic results with testCaseTitle ending in 'Work Item' and automatedTestName ending in 'WorkItemExecution' — these indicate Helix work item crashes, not actual test failures. Check helix_status for the underlying exit code."

**Profile motivating this:** sdk.md (lines 68-75)

---

## Priority Summary

| Priority | Rec | Tool/Area | Impact |
|----------|-----|-----------|--------|
| **P0** | REC-1 | `helix_test_results` description | Saves 2-3 wasted calls per investigation for 4/6 repos |
| **P0** | REC-2 | `helix_search_log` description | Prevents wrong-pattern searches, saves 1-2 iterations |
| **P0** | REC-7 | `helix_test_results` error message | Provides actionable next steps instead of dead end |
| **P1** | REC-3 | `azdo_test_runs` description | Prevents agents from trusting lying summary counts |
| **P1** | REC-4 | `azdo_timeline` description | Eliminates failed "Send to Helix" searches for 3/6 repos |
| **P1** | REC-8 | `helix_test_results` error (no files) | Better crash diagnosis guidance |
| **P1** | REC-9 | `azdo_search_log_across_steps` description | Better workflow guidance for large builds |
| **P1** | REC-12 | Pattern reference location | Ensures agents can find the right search pattern |
| **P1** | REC-13 | `helix_status` description | Prevents wasted Helix calls on VMR builds |
| **P2** | REC-5 | `helix_status` description | Exit code interpretation |
| **P2** | REC-6 | `azdo_search_timeline` description | Workflow guidance |
| **P2** | REC-10 | Future: auto-extract Helix job IDs | Eliminates manual log-reading step |
| **P2** | REC-14 | `azdo_test_results` description | Synthetic result disambiguation |
| **P3** | REC-11 | Future: cross-job pattern detection | Infrastructure failure recognition |

---

## Implementation Notes

- **REC-1, 2, 3, 4, 5, 6, 9, 13, 14** are pure description text changes in `HelixMcpTools.cs` and `AzdoMcpTools.cs` — no logic changes needed
- **REC-7, 8** are error message changes in `HelixService.cs` (lines 931-948)
- **REC-10, 11** are future capability proposals requiring design review
- **REC-12** is a decision about where to surface pattern knowledge — recommend Option A (enrich tool descriptions) as the simplest path
- All description changes should be verified by Lambert with existing tests to ensure no regressions

# Decision: Test Quality Review — Tautological Test Findings

**Date:** 2025-07-24
**Decided by:** Dallas (Lead)
**Status:** RECOMMENDATION — requires team discussion

## Executive Summary

Reviewed all 776 tests across 50 test files (~14,200 lines). Found **~40 problematic tests** (5% of total) across 4 categories, with the most significant issue being **redundant duplication between AzdoCliCommandTests and AzdoServiceTests** (~14 near-duplicate tests). The test suite is generally well-engineered — the problems are concentrated, not systemic.

**Severity: LOW-MEDIUM.** The test suite is not bloated to the point of harm, but the duplication wastes CI time and creates maintenance burden when service signatures change.

---

## Detailed Findings

### Category 4 — Redundant Tests (MOST SIGNIFICANT: ~20 tests)

**The biggest problem.** `AzdoCliCommandTests` was written "proactively for CLI subcommand registration" but tests the _same AzdoService methods_ as `AzdoServiceTests`. Both test files mock IAzdoApiClient, construct AzdoService, and call the same methods with the same patterns.

| AzdoCliCommandTests | AzdoServiceTests | Identical? |
|---|---|---|
| `GetBuildSummary_PlainBuildId_DefaultsToPublic` | `GetBuildSummaryAsync_PlainId_UsesDefaultOrgProject` | YES |
| `GetBuildSummary_AzdoUrl_ResolvesOrgProject` | `GetBuildSummaryAsync_DevAzureUrl_ParsesOrgAndProject` | YES |
| `GetBuildSummary_NotFound_ThrowsInvalidOperation` | `GetBuildSummaryAsync_NullBuild_ThrowsInvalidOperation` | YES |
| `GetBuildSummary_InvalidBuildId_ThrowsArgumentException` | `GetBuildSummaryAsync_InvalidUrl_ThrowsArgumentException` | YES |
| `GetTimeline_ValidBuild_ReturnsTimeline` | `GetTimelineAsync_PlainId_ResolvesToDefaultOrgProject` | YES |
| `GetTimeline_NoBuild_ReturnsNull` | `GetTimelineAsync_NullResult_ReturnsNull` | YES |
| `GetBuildLog_ReturnsContent` | `GetBuildLogAsync_NullTailLines_ReturnsFullContent` | ~90% |
| `GetBuildLog_WithTailLines_ReturnsLastN` | `GetBuildLogAsync_TailLines_ReturnsLastNLines` | YES |
| `GetBuildLog_NotFound_ReturnsNull` | `GetBuildLogAsync_NullContent_ReturnsNull` | YES |
| `GetBuildChanges_ReturnsChangeList` | `GetBuildChangesAsync_PlainId_PassesDefaultsToClient` | YES |
| `GetTestRuns_ReturnsRunsList` | `GetTestRunsAsync_PlainId_PassesDefaultsToClient` | YES |
| `GetTestResults_ReturnsResults` | `GetTestResultsAsync_Url_ResolvesOrgProject` | ~80% |
| `GetBuildSummary_CalculatesDuration` | `GetBuildSummaryAsync_Duration_ComputedFromStartAndFinish` | YES |
| `GetBuildSummary_InProgressBuild_NullDuration` | `GetBuildSummaryAsync_NullStartOrFinish_DurationIsNull` | YES |

Also **AzdoMcpToolsTests** overlaps with both:
- `Build_ReturnsBuildSummary` overlaps with `AzdoServiceTests.GetBuildSummaryAsync_MapsAllFieldsCorrectly`
- `Changes_ReturnsChangeList`, `TestRuns_ReturnsRunList`, `TestResults_ReturnsResultList` are passthrough-verifying tests that mostly just confirm the MCP tool delegates to the service

And in Helix:
- `HelixMcpToolsTests.Status_ReturnsValidJsonWithExpectedStructure` substantially overlaps with `HelixServiceDITests.GetJobStatusAsync_HappyPath_ReturnsAggregatedSummary`
- `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed` verify the same behavior (default filter = "failed")

**Recommendation:** CONSOLIDATE. Delete `AzdoCliCommandTests` entirely — it provides zero coverage that `AzdoServiceTests` doesn't already have. The CLI tests should only exist once CLI command classes exist and need registration/parsing testing. The `AzdoCliCommandTests.GetBuildArtifacts_*` and `GetBuildChanges_WithTopParameter_PassesToClient` tests are the only ones adding unique value — move those to `AzdoServiceTests`. Merge the two overlapping HelixMcpToolsTests filter tests.

---

### Category 2 — Identity-Transform / Passthrough Tests (~8 tests)

Tests where the code under test is essentially `return await _client.Method(...)` and the test just verifies the return value matches the mock:

| Test | What it actually tests |
|---|---|
| `AzdoServiceTests.ListBuildsAsync_EmptyList_ReturnsEmpty` | Passthrough — service calls client, returns result |
| `AzdoServiceTests.GetBuildChangesAsync_EmptyList_ReturnsEmpty` | Same |
| `AzdoServiceTests.GetTestRunsAsync_EmptyList_ReturnsEmpty` | Same |
| `AzdoServiceTests.ListBuildsAsync_PassesFilterToClient` | Only asserts `Received(1)` — verifies wiring, not logic |
| `AzdoMcpToolsTests.Changes_ReturnsChangeList` | MCP → Service → Client passthrough |
| `AzdoMcpToolsTests.TestRuns_ReturnsRunList` | Same |
| `AzdoMcpToolsTests.TestResults_ReturnsResultList` | Same |
| `AzdoMcpToolsTests.Builds_ReturnsBuildList` | Same |

**Recommendation:** KEEP with reduced priority. These do have marginal value as regression guards — if someone accidentally breaks the wiring, they'll catch it. But they should never be the ONLY tests for a feature. They're acceptable as "contract smoke tests" but should not be treated as meaningful coverage.

---

### Category 5 — Setup-Heavy / Assertion-Light (~5 tests)

| Test | Assertion |
|---|---|
| `HelixApiClientFactoryTests.ImplementsIHelixApiClientFactory` | `Assert.NotNull(factory)` |
| `HttpContextHelixTokenAccessorTests.ImplementsIHelixTokenAccessor` | `Assert.NotNull(accessor)` |
| `HelixMcpToolsTests.Constructor_AcceptsHelixService` | `Assert.NotNull(tools)` |
| `HelixApiClientFactoryTests.Create_WithToken_ReturnsValidClient` | `Assert.NotNull` + `IsAssignableFrom` |
| `HelixApiClientFactoryTests.Create_NullToken_ReturnsUnauthenticatedClient` | `Assert.NotNull` + `IsAssignableFrom` |

**Recommendation:** REMOVE the "ImplementsI*" and "Constructor_Accepts*" tests. These are compile-time guarantees — if the class doesn't implement the interface, the code won't compile. The `Create_*_ReturnsValidClient` tests have marginal value; consider keeping one and dropping the rest.

---

### Category 1 — Mock-Verifying Tests (~5 tests)

| Test | Pattern |
|---|---|
| `AzdoMcpToolsTests.Build_ReturnsBuildSummary` | Mock returns AzdoBuild, assert AzdoBuildSummary fields match |
| `StructuredJsonTests.Status_IncludesJobId` | 20 lines setup, asserts `result.Job.JobId == ValidJobId` |
| `StructuredJsonTests.Status_IncludesHelixUrl` | 20 lines setup, asserts URL construction |

**Recommendation:** The `Build_ReturnsBuildSummary` test is a duplicate of `AzdoServiceTests.GetBuildSummaryAsync_MapsAllFieldsCorrectly` — CONSOLIDATE. The StructuredJsonTests are borderline; they test real output structure but the setup-to-assertion ratio is high. KEEP but note they're low-value.

---

## Well-Written Tests (Positive Examples)

These files exemplify the patterns the team should follow:

1. **AzdoSecurityTests** — Tests real security boundaries with adversarial inputs (SSRF, XSS, SQL injection, path traversal, embedded credentials). Each test verifies defense-in-depth behavior. **Gold standard for security testing.**

2. **AzdoIdResolverTests / HelixIdResolverTests** — Pure-function tests. No mocking. Clear input→output contracts. Easy to read, fast to execute.

3. **TextSearchHelperTests** — Tests real algorithmic logic (context lines, max matches, case sensitivity, edge cases). No mocks.

4. **AzdoServiceTailTests** — Tests meaningful optimization logic (tail vs full fetch). Verifies both the optimization path AND the fallback. Uses `Received`/`DidNotReceive` to verify the correct API was called — this is the RIGHT way to use mock verification.

5. **CachingAzdoApiClientTests / CachingHelixApiClientTests** — Decorator pattern tests done right. Cache hit → skip inner. Cache miss → call inner + store. Dynamic TTL. These test real caching logic, not just passthrough.

6. **StreamingBehaviorTests** — Tests real I/O edge cases (empty streams, large content, tail behavior, UTF-8 encoding, stream disposal). The `TrackingMemoryStream` helper is a good pattern.

7. **SqliteCacheStoreTests / SqliteCacheStoreConcurrencyTests** — Integration tests with real SQLite. Tests real storage behavior.

---

## Recommendations

### Immediate (Lambert should action)

1. **Delete `AzdoCliCommandTests.cs`** — Move the 3 unique tests (`GetBuildArtifacts_DefaultPattern_ReturnsAll`, `GetBuildArtifacts_PatternFilter_FiltersResults`, `GetBuildChanges_WithTopParameter_PassesToClient`) to `AzdoServiceTests.cs`. Delete the rest (~16 tests). Net reduction: ~13 tests, ~280 lines.

2. **Delete the 3 "ImplementsI*" / "Constructor_Accepts*" tests** — Compile-time guarantees don't need runtime tests. Net reduction: 3 tests.

3. **Merge `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed`** in `HelixMcpToolsTests` — They test the same thing. Net reduction: 1 test.

### Future Guidelines

4. **Rule: No test file per layer for the same behavior.** When testing Service methods, one test file is enough. Don't create CLI-level and MCP-level test files that re-test the same Service calls. When a proactive test file (written before production code) overlaps with a later "real" test file, prune the proactive tests during the PR that adds the real tests — don't let duplicates accumulate. *(Consolidated from Lambert's independent finding on 2025-07-18.)*

5. **Rule: Passthrough methods get at most 1 smoke test,** not exhaustive input variations. If a method is `return await _client.Foo(args)`, one test proving the delegation is sufficient.

6. **Rule: Interface compliance tests are redundant.** The compiler already enforces `IFoo foo = new Bar()` — testing it at runtime wastes CI.

### Estimated Impact

- Tests to remove/consolidate: ~17
- Lines to remove: ~350
- Tests remaining: ~759
- Coverage impact: ZERO (all removed tests are duplicates of retained tests)

### 2026-03-10: Enriched CiKnowledgeService with full 9-repo knowledge base

**By:** Ripley
**What:** Expanded `CiRepoProfile` record with 9 new properties (PipelineNames, OrgProject, ExitCodeMeanings, WorkItemNamingPattern, KnownGotchas, RecommendedInvestigationOrder, TestFramework, TestRunnerModel, UploadedFiles) and added 3 new repos (maui, macios, android) to the knowledge base. Updated FormatProfile() and GetOverview() to render enriched fields. Updated CiKnowledgeTool.cs description for expanded repo set. Total coverage: 9 repos.
**Why:** The CI knowledge base was the single source of truth for agent investigation guidance but only covered 6 repos with basic fields. Agents investigating MAUI, macios, or android failures had no guidance at all. Critical operational knowledge (exit code meanings, known gotchas like failedTests=0 lying, org/project differences for devdiv repos, pipeline-specific investigation paths for MAUI's 3 pipelines) was missing. The enriched profiles prevent wasted tool calls (e.g., trying helix_* on devdiv repos) and encode hard-won investigation patterns from reference profile analysis.

### 2026-03-10: Updated MCP tool descriptions with CI knowledge

**By:** Ripley
**What:** Updated 5 MCP tool descriptions (helix_test_results, helix_search_log, azdo_test_runs, azdo_test_results, azdo_timeline) to embed repo-specific CI knowledge from CiKnowledgeService profiles. Added warnings about common failure paths, repo-specific search patterns, Helix task name mappings, and cross-references to helix_ci_guide.
**Why:** Agents were wasting calls on helix_test_results for repos that don't upload TRX (4/6 major repos), using wrong search patterns, and not knowing which Helix task names to look for in timelines. These description changes are the cheapest possible fix — zero runtime cost, immediate agent behavior improvement. Descriptions are the first thing agents read before deciding which tool to call, so getting them right eliminates entire classes of dead-end investigations.

### 2025-07-24: Architectural Analysis — Helix vs AzDO File Structure Separation

- Adopt folder-level symmetry before project splitting: `Core/Helix`, `Core/AzDO`, `Core/Cache`, parallel test folders, and separated MCP tool folders.
- Shared helpers such as pattern matching and file-search toggles should live outside `HelixService` so AzDO code does not depend on Helix-specific types.

### 2025-07-24: Restructure Execution Notes — Option A

- Option A was executed mechanically: Helix-specific files moved under `Core/Helix`, cache infrastructure got its own namespace, and shared helpers moved to `StringHelpers`.
- Folder and namespace moves should preserve behavior while making domain boundaries visible; use the new helper locations for future work instead of legacy wrapper methods.

### 2026-03-10: README structure — lead with value prop, promote caching and context reduction

**By:** Kane
**What:** Restructured README to prioritize "why" (value prop for AI agents), cross-process caching, and context-efficient design as the three top-level stories. Removed project structure, moved CLI reference to docs/cli-reference.md, de-emphasized TRX parsing from featured section to tool list entry.
**Why:** The previous README was comprehensive but organized by implementation surface (CLI commands, project structure, enhancement tables) rather than by what matters to someone evaluating the tool. The two biggest differentiators — that cached data is shared across MCP server instances, and that tools are designed to return minimal context-window-friendly output — were buried in subsections. The overhaul puts these front and center so the README answers "why should I use this?" before "how do I use it?".

### 2026-03-10: Keep the strict HelixService HttpClient requirement

**By:** Lambert
**What:** Validated the removal of `HelixService`'s implicit `new HttpClient()` fallback and found no remaining one-argument construction sites in repo code or tests. Existing DI wiring in `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` already supplies named `HelixDownload` clients, and constructor null-guard coverage exists in `src/HelixTool.Tests/Helix/HelixServiceDITests.cs`.
**Why:** This keeps the service aligned with explicit dependency injection and avoids silently bypassing configured HTTP policies. Focused tests plus the full suite passed, so there is no current production follow-up required for the fallback removal.

### 2026-03-10: Security boundaries and download transports must be explicit

**By:** Ripley
**What:** `CacheSecurity.ValidatePathWithinRoot` now treats path containment as an exact, case-sensitive boundary check after full-path normalization and root-boundary trimming. `HelixService` no longer creates a fallback `HttpClient`; every caller must provide one, and the constructor null-guards both dependencies.
**Why:** Ignore-case prefix checks can let a case-variant sibling path look like it is under the trusted root on case-sensitive filesystems. Requiring injected `HttpClient` instances keeps timeout/handler configuration centralized in DI and avoids hidden transport creation that bypasses host configuration.

### 2026-03-10: Mark review-fix findings resolved in planning artifacts

**By:** Ash
**What:** The README value-prop rewrite, cache path-boundary hardening, and `HelixService` explicit-`HttpClient` requirement are now confirmed by updated code, tests, and docs. Planning artifacts should record these as fixed findings and keep only residual follow-up work around discoverability and documentation/tool-description synchronization.
**Why:** Leaving those earlier review findings in the active backlog would make the knowledgebase stale and could send future analysts chasing already-resolved work instead of the remaining product gaps.

### 2026-03-10: User directive

**By:** Larry Ewing (via Copilot)
**What:** Treat the knowledgebase as a living document that should be updated from the latest file state rather than preserved as a static snapshot.
**Why:** User request — captured for team memory

# Dallas decisions inbox — Discoverability review (2026-03-10)

1. **Do not add a new composite failure-investigation tool in this increment.**
   Improve discoverability through existing surfaces: MCP tool descriptions, fallback/error messages, `helix_ci_guide`, README, and llmstxt/help output.

2. **Make `helix_ci_guide(repo)` the recommended repo-specific entry point for workflow selection.**
   Tool descriptions and failure messages should direct agents there when pattern choice or result-location expectations vary by repo.

3. **Clarify the behavioral contract of `helix_test_results`.**
   It should be described as structured Helix-hosted test-result parsing with existing fallback support, but not as the universal first choice across repos. Failure guidance must route callers to the correct next tool sequence.

4. **Clarify the behavioral contract of `helix_search_log`.**
   It should be positioned as the preferred remote-first console-log search path, with explicit note that search patterns vary by repo/test runner.

5. **Keep discoverability surfaces synchronized.**
   MCP descriptions, README, llmstxt/help output, and CI-guide wording must align on when to use `helix_test_results`, when to pivot to AzDO structured results, and when to use `helix_search_log`.

### 2026-03-10: Keep discoverability docs as a short routing note

**By:** Kane
**What:** Updated the README and CLI reference to explain the investigation path in three steps: start with `helix_ci_guide(repo)` when repo workflow expectations vary, use `helix_test_results` only for Helix-hosted structured results, then pivot to AzDO structured results or `helix_search_log` as appropriate.
**Why:** The design review called for better discoverability without turning docs into a manual. A compact routing note keeps the surfaces aligned with Dallas's decisions while preserving the README's concise, evaluator-friendly shape.

### 2026-03-10: Prefer explicit fallback routing in CI investigation copy

**By:** Ripley
**What:** MCP descriptions, `helix_test_results` failure text, `helix_ci_guide`, and `Program.cs` help output should explicitly route callers between `helix_test_results`, `azdo_test_runs + azdo_test_results`, `helix_search_log`, and `helix_ci_guide(repo)` without adding a composite tool or new parameters.
**Why:** Repo-specific CI workflows already exist, but vague warnings still cause wasted tool calls. Concise “use this when / otherwise go here next” wording improves discoverability while preserving the approved incremental surface.

### 2025-07-25: Rename "vmr" profile key to "dotnet"

**By:** Ripley
**Status:** Implemented

## Context

The CI knowledge profile for dotnet/dotnet used `"vmr"` as its dictionary key and `RepoName`. Agents searching for `dotnet` or `dotnet/dotnet` would miss it unless they knew about a special-case fallback, and overview listings showed `vmr` instead of the repo name agents actually recognize.

## Decision

- Dictionary key changed from `["vmr"]` to `["dotnet"]`
- `RepoName` changed from `"vmr"` to `"dotnet/dotnet"`
- `DisplayName` remains `"dotnet/dotnet (VMR)"`
- The special-case `dotnet`/`dotnet/dotnet` → `vmr` lookup is removed; direct key matching and existing short-name extraction handle the repo now
- String literals and doc comments referring to the profile key now use `dotnet`; `VMR` is preserved where it describes the product/build context

## Impact

- `GetProfile("dotnet")` now resolves by direct key match
- `GetProfile("dotnet/dotnet")` now resolves through the existing short-name extraction path
- `KnownRepos` and overview listings show `dotnet` instead of `vmr`
- `profile.RepoName` is now `dotnet/dotnet`
- 196 CI knowledge tests pass

### 2025-07-25: Document MCP resources and idempotent annotations

**By:** Lambert
**Requested by:** Larry Ewing

## Decision

- Add an `MCP Resources` section to `README.md` after the MCP tools table, documenting `ci://profiles` and `ci://profiles/{repo}` as client-discoverable mirrors of `helix_ci_guide`
- Add an `Idempotent annotations` row to the README's Context-Efficient Design table so the retry/cache safety contract is documented alongside other client-facing efficiency choices
- All read-only MCP tools should carry `Idempotent = true`; `helix_download` and `helix_download_url` keep `Idempotent = true` without `ReadOnly = true` because they write files to disk

## Why

- MCP resources deserve the same discoverability in docs as tools because agents can consume them directly
- `Idempotent = true` is a durable MCP convention for this repo: it lets clients retry and cache tool calls safely, and it should remain paired with all read-only tools going forward
- README documentation should reflect that safety contract so agents and humans see the same routing guidance

### 2026-03-13: AzDO list tools return truncation metadata via LimitedResults<T>

**By:** Ripley

## Context

User feedback from steveisok showed agents were hitting list/search caps and treating capped results as complete.

## Decision

For MCP-only list-style AzDO tools that previously returned raw lists (`azdo_builds`, `azdo_artifacts`, `azdo_test_results`, `azdo_test_attachments`), wrap results in a `LimitedResults<T>` type that:
- serializes as an object with `results`, `truncated`, and an optional `note`
- still implements `IReadOnlyList<T>` so direct C# callers and tests keep normal list ergonomics

## Why

A plain top-level object gives agents an explicit truncation signal, but changing local call sites to a non-list type would create unnecessary churn. The wrapper preserves runtime/test compatibility while making capped MCP responses self-describing.

## Follow-up

If more MCP tools need truncation metadata, prefer reusing this wrapper pattern instead of inventing per-tool response shapes.

### 2026-03-13: AzDO auth should use a narrow, scheme-aware credential chain (consolidated)

**By:** Dallas, Ripley
**What:** Keep AzDO auth layered as `AZDO_TOKEN` → `AzureCliCredential` → `az account get-access-token` subprocess → anonymous, without `DefaultAzureCredential`. `IAzdoTokenAccessor` should return scheme-aware `AzdoCredential` metadata plus `AuthStatusAsync` output so callers can choose `Basic` for PATs, `Bearer` for Entra/JWT sources, and safe auth-status reporting. Fallback Azure CLI / `az` credentials should cache with a refresh deadline, invalidate on 401/403, and derive `AzdoCredential.CacheIdentity` from stable auth-source identity (`auth path` + stable JWT claims such as `tid`/`oid`/`appid`/`sub`) rather than raw token bytes so `CachingAzdoApiClient` can partition AzDO cache keys by auth context after the first successful authenticated response. `AzdoCredential.Token` stays the on-wire payload, `DisplayToken` remains compatibility-only, `AZDO_TOKEN_TYPE=pat|bearer` remains the explicit override ahead of the JWT heuristic, and token-like material in unexpected AzDO error snippets must be redacted before surfacing. Keep cache-root selection stable via `CacheOptions.CacheRootHash` while leaving `AuthTokenHash` as the mutable AzDO cache-key partition, and seed that AzDO auth hash immediately after credential resolution so the first cached AzDO read is already partitioned correctly.
**Why:** The existing az CLI fallback is the proven escape hatch for WSL/libsecret failures, while a deliberately narrow Azure.Identity probe avoids the latency and opaque failure modes of `DefaultAzureCredential`. Expiry-aware fallback caching and 401/403 invalidation keep long-running CLI/MCP hosts from sticking to stale developer tokens, and stable auth-source identity keeps cache isolation refresh-safe while preventing one authenticated AzDO context from reusing another context's private cached responses. Safe `auth-status` metadata gives operators a supported way to inspect the active auth path without probing AzDO directly, and the stricter token/string handling closes the remaining low-effort leak and ambiguity cases from the STRIDE/CCA follow-up review. Separating stable cache-root selection from mutable AzDO key partitioning prevents misleading cache-root reporting and avoids an initial unpartitioned AzDO cache lookup before auth context is known.

### 2026-03-13: MCP tool descriptions should stay short and defer repo-specific guidance to helix_ci_guide (consolidated)

**By:** Ripley
**What:** The project first embedded repo-specific CI knowledge into selected MCP tool descriptions to improve routing, then tightened 17 `Description()` attributes to ≤35 words and moved repo-specific patterns/task-name guidance back into `helix_ci_guide`. Keep only high-value warnings and routing hints in tool descriptions; keep detailed repo workflows in the on-demand CI guide.
**Why:** Description text is loaded into every agent context whether the tool is used or not, so verbose repo-specific guidance creates a permanent token tax. Short behavioral descriptions still steer tool selection, while `helix_ci_guide` carries the richer repo detail only when a caller actually needs it.

### 2026-03-13: Helix MCP tool names should reflect actual scope (consolidated)

**By:** Ripley
**What:** Renamed MCP-visible tool names from `helix_test_results` to `helix_parse_uploaded_trx` and from `helix_search_log` to `helix_search` across tool registration, docs/help text, tests, and README while keeping the internal/CLI names that still fit (`ParseTrxResultsAsync`, `SearchLog`, `search-log`) stable.
**Why:** The earlier names were context traps: `helix_test_results` sounded like the universal first stop even though most repos publish structured results to AzDO, and `helix_search_log` no longer matched a tool that can search both console logs and uploaded files. Scope-accurate names improve agent discoverability without unnecessary internal churn.

### 2026-03-14: PR #29 review round 3 — AzDO auth cache identity updates

**By:** Ripley

## Decision

Store the resolved AzDO auth cache identity on `CacheOptions` and let both `CachingAzdoApiClient` and `AzdoApiClient` update the shared auth context through `UpdateAuthContext(...)`.

## Why

The auth-hash partition can no longer be treated as write-once because long-running processes may observe Azure CLI or `az` credential changes. Keeping the last resolved identity next to the derived hash lets both layers react consistently when the principal changes, while `CacheStoreFactory` now keys stores strictly by the stable effective cache root so auth-key churn never creates duplicate `SqliteCacheStore` instances for the same database path.

### 2026-03-14: PR #29 review round 4 — AzDO auth fallback identity and expiration parsing hardening

**By:** Ripley
**Requested by:** Larry Ewing

## Decision

- When an `AzdoCredential` arrives without `CacheIdentity`, derive the fallback identity with `AzdoCredential.BuildCacheIdentity(Source, DisplayToken)` instead of using the bare source label. This preserves principal-specific AzDO cache partitioning for PATs and JWTs that do not already carry a precomputed identity.
- Remove `TrySetAuthTokenHash`; callers now update the shared AzDO auth context consistently through `UpdateAuthContext(...)`.
- Use a shared `AzdoCredential.TryFromUnixTimeSeconds` helper for both JWT `exp` parsing and az CLI `expiresOn*` parsing so out-of-range Unix timestamps fail closed consistently instead of throwing.

## Why

- Source-only fallback identities can collapse different principals onto the same AzDO cache partition and reuse cached responses across distinct authenticated contexts.
- A single `UpdateAuthContext(...)` path keeps identity/hash updates coherent across layers and removes drift between callers.
- Centralized, range-checked Unix-time parsing hardens both credential sources against invalid expiration values and keeps failure behavior deterministic.

### 2026-03-14: helix-cli skill docs must stay behavior-accurate (consolidated)

**By:** Kane
**Requested by:** Larry Ewing
**What:** Keep the helix-cli docs in `.github/skills/helix-cli/SKILL.md` only, documenting shipped CLI behavior and dynamic discovery surfaces. Route CLI discovery through `hlx llms-txt`, command `--help`, and inline jq field hints; explicitly note there is no `hlx ci-guide` command yet; and describe `hlx search-log` as text-only in the CLI while routing structured Helix log consumers to MCP `helix_search`.
**Why:** Skill docs are an execution surface for agents. Behavior-accurate docs preserve the approved discovery path and avoid broken CLI workflows caused by aspirational parity claims. A second static reference surface would go stale, duplicate information already discoverable from shipped surfaces, and risk documenting unshipped CLI JSON for `hlx search-log`.
**Follow-up:** Track `hlx <command> --schema` as the long-term fix for per-command JSON field discovery; until then, the skill should stay single-source in `SKILL.md` and point structured Helix log consumers to MCP `helix_search`.

### 2026-03-14: `azdo search-log --schema` is mode-sensitive
**By:** Ripley
**What:** The new CLI `--schema` support prints `LogSearchResult` when `hlx azdo search-log` is scoped to a specific `--log-id`, and prints `CrossStepSearchResult` when the command is searching across ranked build logs.
**Why:** `azdo search-log` already has two distinct JSON payload shapes depending on whether `--log-id` is present. Schema discovery needs to mirror the active wire format instead of inventing a third shape or flattening both modes into one inaccurate contract.

### 2026-03-14: Source generator-backed CLI describe registry

**By:** Ripley

## Decision
Use a Roslyn source generator in `src/HelixTool.Generators/` to emit `HelixTool.Generated.CommandRegistry` for `hlx describe`.

## Why
- MCP `[Description]` attributes in `HelixTool.Mcp.Tools` remain the single source of truth for agent-facing command descriptions.
- CLI commands opt in with `[McpEquivalent("...")]`, which keeps the CLI/MCP mapping explicit without introducing shared description constants.
- `hlx describe` can stay runtime-light and strongly typed because it consumes generated data instead of reflecting over command metadata at startup.

## Key files
- `src/HelixTool.Generators/DescribeGenerator.cs`
- `src/HelixTool.Core/McpEquivalentAttribute.cs`
- `src/HelixTool/Program.cs`
