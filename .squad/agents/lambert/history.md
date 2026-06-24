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
