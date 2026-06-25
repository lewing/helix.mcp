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

📌 Team update (2026-05-21): Pagination contract tests — wrote 13 tests (333 LOC) for Phase 1+2 pagination spec in src/HelixTool.Tests/AzDO/PaginationContractTests.cs. All 13/13 passing; full suite 1180/1180 passing. Commits 181ff5b + d5fde34. ⚠️ BRANCH-HYGIENE: committed to local main instead of squad/pagination-standardize per manifest instruction; Larry will handle branch/push decision.

📌 Cross-agent heads-up (2026-05-21T17:22Z, from Ripley dependency audit): xunit v3 migration held for v0.8.0+ (not v0.7.1); Roslyn 5.3.0 bump also held (requires generator verification). FYI for test framework planning.

## Learnings
- **2026-05-21:** For SDK adapter/cache seam coverage, keep `WorkItemSummaryAdapter` and `WorkItemSummaryDto` private and test them via reflection from `HelixTool.Tests`; instantiate the real SDK model, invoke the DTO `From()` factory, and round-trip JSON with `JsonSerializer` to verify backward-compatible missing-field behavior.
- **2026-05-21:** `HelixService.GetJobStatusAsync` optimization tests should drive `IWorkItemSummary.ExitCode` directly on the summary mock and assert `GetWorkItemDetailsAsync` call counts with NSubstitute `Received`/`DidNotReceive`; passed summary-path items intentionally keep `State`/`MachineName`/`Duration` as `null`.
- **2026-05-21:** Project testing conventions here remain xUnit + NSubstitute, and MCP surface tests should serialize actual result types (`StatusResult`/`StatusWorkItem`) to assert camelCase JSON property names without introducing extra schema helpers.
- **2026-05-22:** Description-string tests are fragile when they pin repo-specific phrases; keep MCP metadata checks focused on routing intent, and assert discoverability details like `devdiv` in `CiKnowledgeService` response content instead.

# Summary (archived 19 older entries)

See history-archive.md for detailed history.
- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## Learnings — Issue #61 MCP exception coverage audit 2026-05-25
- Baseline: 25 MCP tools audited; 14/25 have direct MCP happy-path tests, 7/25 have any direct unhappy-path tests, and only 2/25 had high-quality service-exception wrapper tests before this branch.
- Standing rule from Dallas: every `[McpServerTool]` method needs at least one unhappy-path test proving exceptions surface as structured MCP errors with non-empty messages.
- AggregateException pattern: model Bug B by returning `Task.FromException<T>(new AggregateException(new HttpRequestException("...")))` from the API mock at a `Task.WhenAll` boundary, then assert `McpException` message content after centralization.
- TaskCanceledException pattern: model timeout/cancellation with `Task.FromException<T>(new TaskCanceledException("..."))`; keep as a skipped contract test until Ripley's centralized handler catches cancellation families.
- Mocking approach: instantiate real `AzdoMcpTools` with `AzdoService` over an `IAzdoApiClient` NSubstitute mock; assert the MCP tool boundary, not just service-layer exceptions.

---

## 2026-05-25: Issue #61 — Exception Coverage Baseline Audit (PR #63)

**Session:** Issue #61 Silent MCP failures fix  
**Status:** PR Merged ✅  
**Task:** Baseline audit of MCP exception handling across 25 tools

### Audit Results

- **Tools audited:** 25
- **Direct MCP happy-path coverage:** 14/25 (56%)
- **Direct MCP unhappy-path coverage:** 7/25 (28%)
- **Worst gaps:** azdo_build_analysis, azdo_build, azdo_helix_jobs, azdo_search_timeline, helix_batch_status

### Tests Written

1. **azdo_build_analysis:** Skipped test for AggregateException (pending #64) ⏸
2. **azdo_builds:** Active HttpRequestException wrapper test ✅
3. **azdo_timeline:** Skipped test for TaskCanceledException (pending #64) ⏸

PR #63 MERGED ✅. Skipped tests will be unskipped after PR #64 merges.

### Key Calibration Learning

**Name an exception by exercising it, not by guessing from source-read.**

Ash's investigation identified the right fix (centralize exceptions) but incorrectly named the uncaught exception as "AggregateException from Task.WhenAll." Reality: `await Task.WhenAll` unwraps to the inner exception; only `.Wait()` throws AggregateException. The actual uncaught types were TaskCanceledException and OperationCanceledException.

**Better practice:**
1. Write 10-line repro
2. Run it: `catch (Exception ex) { Console.WriteLine(ex.GetType()); }`
3. Only then name the type

**This is critical for Task.WhenAll, Task.WhenAny, ConfigureAwait.**

**Net impact:** Narrative correction; zero production risk. Your skipped tests will validate the real exception path after #64 merges.

### Issue #61 Closed — 3 PRs Merged

- PR #62 (Ripley): Parameter standardization ✅
- PR #64 (Ripley): Exception centralization ✅
- PR #63 (yours): Exception coverage audit ✅

Both bugs fixed. Follow-up issue #65 tracks unskip tests, add companion test for real path, rolling coverage tests, preserve calibration lesson.

### Standing Rule (Your Policy)

Every MCP tool method must have ≥1 test covering the unhappy path (exception → structured error). Baseline now documented (28% floor). Rolling implementation: Week 2–3.


## Learnings — PR #68 review iteration + rebase 2026-05-28
- Rebase-conflict discipline: when SKILL.md has cross-PR overlap, resolve semantically rather than choosing one side; keep Ripley's implementation facts and Lambert's schema-audit findings/captures when they describe different layers.
- Forward-looking claim lesson: do not mark a fix as complete until the implementation has merged to the branch being documented; after PR #69 merged, the CallToolFilters claim became present-tense accurate.
- Use `git push --force-with-lease` after rebasing an open PR branch so the linear-history update does not overwrite unexpected remote work.

## 2026-05-28: PR #68 — MCP Tool Schema Audit & Description Improvements

- Audited all 25 [McpServerTool] methods for parameter description clarity
- Improved [Description] attributes across AzdoMcpTools.cs, CiKnowledgeTool.cs, Helix/HelixMcpTools.cs
- Added new src/HelixTool.Tests/McpToolDescriptionTests.cs reflection coverage test
- Disambiguated jobId (Helix) vs buildIdOrUrl (AzDO) in 25/25 tool descriptions
- Flagged 8 schema follow-ups: conditional-required params, JSON numeric→string ID binding, custom annotations for combo rules
- Token measurement: 25 tools = 7,966 tokens (down 246 tokens / -3.0% from v0.7.3 baseline)
- PR #68 shipped and merged; schema audit findings ready for v0.7.6 planning

- **2026-06-01:** MCP `CallToolFilter` tests can wrap a capture handler for argument-mutation unit coverage, then wrap real `McpServerTool.InvokeAsync` for SDK binding coverage. Added 11 `McpServerOptionsExtensionsTests` cases for `buildIdOrUrl` alias normalization: 3 alias mappings, canonical conflict, missing-param binding error preservation, `azdo_build_analysis` and `azdo_search_timeline` end-to-end calls, multi-alias precedence (`build_id` wins), and 3 case-insensitive alias keys; full suite passed at 1312 passed / 2 skipped. **Status:** Dallas verdict and Ripley implementation both approved. All tests passing. Decision merged to decisions.md. Ready for team commit (2026-06-01T19:57:01Z).


## 2026-06-01: Numeric buildIdOrUrl alias telemetry regression

- Added regression coverage for real telemetry where `build_id` / `buildId` arrive as JSON numbers (for example `2989057`) instead of sanitized string samples.
- Test count delta: +4 cases in `McpServerOptionsExtensionsTests`; full suite now passes at 1316 passed / 2 skipped after `dotnet test --nologo --no-build`.
- Lesson: wire-format flexibility tests must mirror real telemetry shapes, not just sanitized examples.

## Copilot PR #75 — Numeric JsonElement Regression Tests (2026-06-01)

Added end-to-end regression tests for Ripley's numeric coercion fix.

**Coverage:** 
- `build_id: 2989057` → canonical `buildIdOrUrl: "2989057"` (string)
- `buildId: 2989057` → canonical `buildIdOrUrl: "2989057"` (string)
- Verified azdo_search_timeline SDK binding succeeds with numeric build IDs

**Commit:** `015d304`  
**Tests:** 1316 passed, 2 skipped (0 failed) — 4 new tests all green  
**Branch:** `ripley/azdo-buildidorurl-aliases` (same as Ripley)


### 2026-06-24: PR #78 AzDO Param Plumbing Shipped

- Ripley's three parameter plumbing bugfixes shipped after four Copilot review rounds
- Covers minTime/maxTime/queryOrder plumbing, top forwarding, outcomes filter exposure
- 14 new tests covering whitespace normalization, cache key semantics, boundary protection
- All 1337 tests pass

**Related:** Session log: `.squad/log/2026-06-24-pr78-azdo-param-plumbing-and-followups.md`
**Follow-up:** Issue #82 (architectural cleanup: centralize AzDO filter normalization)

## Learnings — Issue #81 Stage A Tests (2026-06-24)

- **`McpServerToolCreateOptions.SerializerOptions` requires `TypeInfoResolver`** — when creating a `JsonSerializerOptions` with `UnmappedMemberHandling = Disallow` and passing it to `McpServerToolCreateOptions`, `DefaultJsonTypeInfoResolver` must be set or the SDK throws `InvalidOperationException` at tool-create time. Add `using System.Text.Json.Serialization.Metadata;` and set `TypeInfoResolver = new DefaultJsonTypeInfoResolver()`.
- **Strict-mode test helper pattern** — `CreateStrictFilteredToolHandler` follows the same shape as `CreateFilteredToolHandler` but passes `McpServerToolCreateOptions` with Disallow + DefaultJsonTypeInfoResolver to `McpServerTool.Create`.
- **Alias-key-removal regression tests** use the capture-handler pattern (`CreateFilteredHandler`) — no end-to-end tool invocation needed; just assert the dict state after the filter runs.
- **`dotnet test --no-build` skips rebuild** — always run `dotnet test` (with build) when verifying new code; `--no-build` will run stale binaries and give false failures.
- **PR #83** — 8 tests added, 1345 passed / 0 failed / 2 skipped. Ready for Dallas review.

## 2026-06-24: PR #83 Dallas Review Fix (commit 443c31f)

**Pattern: reviewer-driven fix by a different agent.** Ripley wrote PR #83; Dallas rejected (blocking issue). Per reviewer-lockout convention, Lambert applied the one-liner fix because it mirrors what Lambert already did in test setup code.

**The Fix:** Added `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` (and `using System.Text.Json.Serialization.Metadata;`) to both `src/HelixTool.Mcp/Program.cs` and `src/HelixTool/Program.cs` `WithToolsFromAssembly` calls.

**SDK code path Dallas traced:**
`AIFunctionFactory.GetOrCreate` → `MakeReadOnly()` called on `JsonSerializerOptions` before tool descriptor runs → constructor calls `AIJsonUtilities.CreateFunctionJsonSchema` → `CreateJsonSchemaCore` → tries `jsonSerializerOptions.TypeInfoResolver = DefaultOptions.TypeInfoResolver` → **throws `InvalidOperationException`** because options are already read-only.

Crash is deferred (not at startup) — fires on first MCP request that triggers tool singleton resolution. Lambert's test helper `CreateStrictFilteredToolHandler` already set `TypeInfoResolver` correctly; the production `Program.cs` files did not.

**SKILL.md** updated with `TypeInfoResolver` as required companion setting, including the full SDK code path.

**Test result:** 1345 passed / 0 failed / 2 skipped (unchanged). PR comment: https://github.com/lewing/helix.mcp/pull/83#issuecomment-4794361161

## Orchestration: Issue #81 + #82 Testing Plan (2026-06-24)
Dallas triaged #81 and #82. Your scope: write tests for Stage 1 alias regression, Stage 2 CallToolFilter (reference mcp-calltoolfilter-tests pattern), and #82 contract tests (reference azdo-rest-param-surface-audit). See decisions.md for blocking chain and effort summary.
## Learnings — Issue #81 Stage A Tests (2026-06-24)

- **`McpServerToolCreateOptions.SerializerOptions` requires `TypeInfoResolver`** — when creating a `JsonSerializerOptions` with `UnmappedMemberHandling = Disallow` and passing it to `McpServerToolCreateOptions`, `DefaultJsonTypeInfoResolver` must be set or the SDK throws `InvalidOperationException` at tool-create time. Add `using System.Text.Json.Serialization.Metadata;` and set `TypeInfoResolver = new DefaultJsonTypeInfoResolver()`.
- **Strict-mode test helper pattern** — `CreateStrictFilteredToolHandler` follows the same shape as `CreateFilteredToolHandler` but passes `McpServerToolCreateOptions` with Disallow + DefaultJsonTypeInfoResolver to `McpServerTool.Create`.
- **Alias-key-removal regression tests** use the capture-handler pattern (`CreateFilteredHandler`) — no end-to-end tool invocation needed; just assert the dict state after the filter runs.
- **`dotnet test --no-build` skips rebuild** — always run `dotnet test` (with build) when verifying new code; `--no-build` will run stale binaries and give false failures.
- **PR #83** — 8 tests added, 1345 passed / 0 failed / 2 skipped. Ready for Dallas review.

## 2026-06-24: PR #83 Dallas Review Fix (commit 443c31f)

**Pattern: reviewer-driven fix by a different agent.** Ripley wrote PR #83; Dallas rejected (blocking issue). Per reviewer-lockout convention, Lambert applied the one-liner fix because it mirrors what Lambert already did in test setup code.

**The Fix:** Added `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` (and `using System.Text.Json.Serialization.Metadata;`) to both `src/HelixTool.Mcp/Program.cs` and `src/HelixTool/Program.cs` `WithToolsFromAssembly` calls.

**SDK code path Dallas traced:**
`AIFunctionFactory.GetOrCreate` → `MakeReadOnly()` called on `JsonSerializerOptions` before tool descriptor runs → constructor calls `AIJsonUtilities.CreateFunctionJsonSchema` → `CreateJsonSchemaCore` → tries `jsonSerializerOptions.TypeInfoResolver = DefaultOptions.TypeInfoResolver` → **throws `InvalidOperationException`** because options are already read-only.

Crash is deferred (not at startup) — fires on first MCP request that triggers tool singleton resolution. Lambert's test helper `CreateStrictFilteredToolHandler` already set `TypeInfoResolver` correctly; the production `Program.cs` files did not.

**SKILL.md** updated with `TypeInfoResolver` as required companion setting, including the full SDK code path.

**Test result:** 1345 passed / 0 failed / 2 skipped (unchanged). PR comment: https://github.com/lewing/helix.mcp/pull/83#issuecomment-4794361161


## 2026-06-24: PR #84 — Stage B Unknown-Param Filter Tests (Issue #81)

Implemented Ripley's 9 test scenarios plus 5 additional direct/boundary tests for
`AddUnknownParameterFilter` (did-you-mean Levenshtein hints).

**Tests added (14 new):**

Ripley's 9 scenarios:
1. `UnknownParamFilter_CanonicalArgsOnly_DoNotThrow` — smoke: canonical args pass
2. `UnknownParamFilter_AliasedArgPassesAfterNormalization` — alias resolved before filter fires
3. `UnknownParamFilter_SingleUnknownCloseMatch_ThrowsMcpExceptionWithHint` — hint fires
4. `UnknownParamFilter_SingleUnknownNoCloseMatch_ThrowsMcpExceptionWithoutHint` — no hint
5. `UnknownParamFilter_MultipleUnknowns_AllSurfacedInMessage` — both keys named
6. `UnknownParamFilter_Threshold6Regression_MinFinishTimeGetsMinTimeHint` — distance-6 fires
7. `UnknownParamFilter_MissingRequiredParam_StillWrapsMcpException` — Stage A behavior unchanged
8. `UnknownParamFilter_ParameterlessTool_AnyArgFlaggedUnknown` — `azdo_auth_status`, allowed=(none)
9. `UnknownParamFilter_CaseInsensitiveCanonicalMatch_KnownPassesUnknownFlagged` — ORG passes, MINFINISHTIME flagged

Additional direct/boundary tests:
10. `Levenshtein_MinFinishTimeToMinTime_IsExactlyThreshold6` — exact DP boundary
11. `FindClosestMatch_Distance6_ReturnsSuggestion`
12. `FindClosestMatch_FarDistance_ReturnsNull` — `zzzzzzzzzz` > threshold from all params
13. `ExtractToolParamInfo_AdditionalPropertiesTrue_ReturnsNull`
14. `ExtractToolParamInfo_MissingSchema_ReturnsNull`

**Production visibility changes (minimal, test-only):**
- `FindClosestMatch`, `Levenshtein`, `ExtractToolParamInfo`, `ToolParamInfo`: `private`→`internal`
- Added `InternalsVisibleTo Include="HelixTool.Tests"` to `HelixTool.Mcp.Tools.csproj`

**Scenario 4 note:** With threshold=6, `foo` → `top` distance=2 gets a hint (false positive,
harmless). Used `zzzzzzzzzz` (10 z's, ≥10 distance from all params) for the no-match test.
Ripley's design doc explicitly acknowledges this tradeoff.

**Concurrent work:** Ash's threshold-6 vs threshold-3 review pending in
`.squad/decisions/inbox/ash-pr-stage-b-threshold-review.md`. Not a blocker.

**Build/test:** 1359 passed / 0 failed / 2 skipped (was 1345; +14 new).
**PR:** https://github.com/lewing/helix.mcp/pull/84 — ready-for-review (non-draft).
**Branch:** `squad/81-strict-mode-stage-b`, commit `3016611`.

## 2026-06-24: PR #85 — Issue #82 Contract Tests + Normalizer Unit Tests

**Branch:** `squad/82-centralize-azdo-normalization`, commit `d04e828`  
**Status:** PR open, ready-for-review (non-draft)  
**PR:** https://github.com/lewing/helix.mcp/pull/85

### Tests added (+91 total)

| File | Tests | Description |
|---|---|---|
| `AzdoBuildFilterNormalizerTests.cs` (new) | 44 | Direct unit tests — every rule × every field |
| `AzdoBuildContractTests.cs` (new) | 42 | Per-param contract tests (URL + cache key + service received) |
| `CachingAzdoApiClientTests.cs` (5 additions) | 5 | Cache-key stability: branch share, distinct query orders, fail-safe, outcomes |

**Before:** 1359 passed / 0 failed / 2 skipped  
**After:** 1450 passed / 0 failed / 2 skipped

### Tests removed: 0

Per Ripley's explicit recommendation, existing layer-smoke tests were kept. The normalizer unit tests cover the rules; layer tests now document "the layer calls the normalizer." No deletions were made.

### Reusable Patterns Captured

#### `[Theory]+[InlineData]` contract test pattern

For per-param coverage with high test count and low LOC, the pattern is:

```csharp
// (a) URL — one [Theory] per param, multiple values
[Theory]
[InlineData("main", "branchName=main")]
[InlineData("refs/heads/main", "branchName=refs%2Fheads%2Fmain")]
public async Task ListBuildsAsync_Branch_AppearsInUrl(string branch, string expectedPart) { ... }

// (b) cache key discrimination — [Theory] or [Fact] depending on how many value pairs
[Theory]
[InlineData("main", "develop")]
public async Task ListBuildsAsync_DifferentBranch_DistinctCacheKeys(string b1, string b2)
{
    // always-miss cache
    _cache.GetMetadataAsync(...).Returns((string?)null);
    _inner.ListBuildsAsync(...).Returns(new List<AzdoBuild>());
    await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = b1 });
    await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = b2 });
    // (c) service received check
    await _inner.Received(1).ListBuildsAsync("org", "proj",
        Arg.Is<AzdoBuildFilter>(f => f.Branch == b1), Arg.Any<CancellationToken>());
    await _inner.Received(1).ListBuildsAsync("org", "proj",
        Arg.Is<AzdoBuildFilter>(f => f.Branch == b2), Arg.Any<CancellationToken>());
}
```

Key: (b) and (c) combine naturally — if inner is called 2× with different filters, you get both assertions for free.

#### Redundant-test-removal heuristic

A test in layer L is redundant iff:
1. It tests only a normalization *rule* (not the layer's behavior), AND
2. The same rule is now covered by a direct unit test of the shared normalizer.

**Safe to remove:** `AzdoApiClient` tests that only verify "whitespace QueryOrder → default in URL" when the normalizer unit test covers that rule.  
**Must keep:** Tests that verify URL construction (e.g., `definitions=777` format), cache TTL semantics, or cache hit/miss behavior. These are layer tests, not rule tests.

**Practical rule:** Keep if the test would still fail after a correct normalizer but a broken call site. Remove only if it would pass by testing the normalizer in isolation.

Per Ripley's recommendation for this PR: keep all existing tests. The incremental LOC cost of duplicate rule coverage is negligible; the protection against regression at the call site is real.

#### Pre-existing flaky SQLite test

`SqliteCacheStoreSecurityTests.GetArtifactAsync_ManipulatedDbRow_ThrowsOrReturnsNull` fails intermittently under parallel xUnit execution due to SQLite connection lifetime. Passes in isolation. Pre-existing issue; not caused by these changes.


## 2026-06-24: PR #87 — CCA Follow-Up Cleanup for #83 #84 #85

**Branch:** `squad/cca-cleanup-83-84-85`, commit `820830c`
**PR:** https://github.com/lewing/helix.mcp/pull/87 — ready-for-review (non-draft)
**Status:** Open, awaiting CCA second pass + Larry merge

### Context

Copilot Coding Agent reviewed all three PRs (#83, #84, #85) but auto-merge ran before CCA could weigh in. Dallas corrected the workflow (no auto-merge — Larry presses the button after CCA review). This PR is the retroactive cleanup for the five findings. Lambert applied the two real bug fixes (lockout-compliant: different agent from Ripley, the original author).

### Fixes applied

**Real bugs:**

1. **Alias-removal hole** (`McpServerOptionsExtensions.cs:75`, CCA PR #83): `NormalizeArgumentAliases` used `continue` when canonical was already present — alias key was never removed. Callers passing `{ buildIdOrUrl: "42", build_id: "42" }` got strict-mode rejection on `build_id`. Fix: always remove alias key; skip only the canonical-value promotion when canonical already exists. First-value-wins semantics preserved.

2. **Missing newline** (`McpServerOptionsExtensions.cs:199`, CCA PR #84): single-unknown path had no trailing `\n`, producing `Did you mean: X?Allowed parameters: …`. Fix: `sb.AppendLine()` at end of single-unknown block.

**Cleanups:**

3. `maxTime` (valid param) replaced with `maxFinishTime` (genuinely unknown) in `StrictOptions_MultipleUnknownParams` test.
4. Handoff doc threshold updated from ≤3 to 6 with regression-case note.
5. `ShareCacheKey2` renamed to `NullAndWhitespaceTrimmedFailedOutcomes_ShareCacheKey`.

### Tests

- +2 new alias-collision regression tests:
  - `NormalizeArgumentAliases_AliasAndCanonicalBothPresent_RemovesAliasKeepsCanonical`
  - `NormalizeArgumentAliases_TwoAliasesSameCanonical_BothRemovedFirstValueWins`
- 2 existing message-format tests updated with `\n`-transition assertions.
- **Before:** 1450 passed / 2 skipped; **After:** 1452 passed / 2 skipped.

### CCA-follow-up cycle (reusable pattern)

**Cycle:** CCA finds bug → Ripley (author) locked out → Lambert fixes + tests under lockout → Larry reviews CCA second pass → Larry merges.

**Key lesson captured below in decisions inbox.** Short version: when a fix is merged, check that it closes the *entire bug class*, not just the case in the test. The alias-removal hole passed the existing rename-only tests because those tests only had alias-without-canonical; they didn't catch alias-plus-canonical.

