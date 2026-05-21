# Decisions

## 2026-03-16T21:04:47Z: User Directive — Partial Response Pattern

**By:** Larry Ewing (via Copilot)

When truncating large MCP tool outputs, use the partial response pattern (return what fits with a continuation indicator) rather than just dropping data.

---

## 2026-03-16: MCP Timeline Tool Analysis — Discoverability & Output Size

**By:** Ripley  
**Date:** 2026-07-18  
**Context:** Larry's gist showing a CI analysis session where `azdo_timeline` returned 447KB and 601KB responses, forcing the agent to save to temp files and parse with Python scripts.

### Findings

#### 1. `azdo_timeline` filter='failed' — limited output reduction for VMR builds

The `filter` parameter (default `"failed"`) works at the MCP tool layer (`AzdoMcpTools.cs:93–121`). It filters to non-succeeded records plus their parent chain, which does reduce record count. However, it returns full `AzdoTimelineRecord` objects with all fields (issues arrays, previousAttempts, log references, workerName, etc.). For VMR builds with hundreds of records and many partially-succeeded verticals, the filtered output can still be 400–600KB — too large for LLM context windows.

#### 2. `azdo_search_timeline` — solves the problem but isn't discoverable

`azdo_search_timeline` (`AzdoService.cs:182–290`) fetches the same raw timeline but returns compact `TimelineSearchMatch` DTOs: only recordId, name, type, state, result, duration, logId, matchedIssues, parentName. This is an order-of-magnitude smaller. The agent in the gist would have gotten far better results calling `azdo_search_timeline(buildId, "error")` or `azdo_search_timeline(buildId, "fail")` instead of `azdo_timeline`.

#### 3. Other tools that could have helped

- `azdo_search_log` — searches across all build step logs with ranked prioritization (failed steps first). The agent could have gone directly from build ID to error text without ever touching the timeline.
- `azdo_log` — fetches a specific step log by logId (with tail support), but requires knowing the logId from timeline first.
- The optimal workflow: `azdo_search_timeline` to find which steps failed → `azdo_search_log` for error details → `azdo_log` if full context needed.

#### 4. Discoverability gap — ci_guide never mentions `azdo_search_timeline`

All 9 repo profiles in `CiKnowledgeService.cs` recommend:
```
azdo_timeline(buildId, filter='failed') → identify failed jobs
```
None mention `azdo_search_timeline` anywhere. The generic overview section also omits it. The MCP tool description itself is accurate ("Search build timeline record names and issue messages for a pattern") but an LLM following the ci_guide workflow will never see it.

#### 5. MCP layer does real work, not just pass-through

`AzdoService` handles URL resolution, parallel metadata fetches, ranked log search with early termination, and result shaping. The API client layer (`AzdoApiClient`) is thin HTTP + auth. The service layer is where business logic lives.

### Recommended Actions

1. **Update ci_guide investigation workflows** to mention `azdo_search_timeline` as the preferred first step for large builds (especially VMR), or at least as an alternative to `azdo_timeline` when you know what pattern to search for.
2. **Consider adding an `azdo_timeline` output budget** — e.g., if the filtered result would exceed N records or N bytes, automatically summarize or truncate with a note pointing to `azdo_search_timeline`.
3. **Cross-reference in tool descriptions** — `azdo_timeline`'s description could say "For large builds, consider azdo_search_timeline to search by pattern instead."

### Files Examined

- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` — MCP tool definitions (all 11 AzDO tools)
- `src/HelixTool.Core/AzDO/AzdoService.cs` — service layer with SearchTimelineAsync, SearchBuildLogAcrossStepsAsync
- `src/HelixTool.Core/AzDO/AzdoModels.cs` — all AzDO DTOs including TimelineSearchMatch, TimelineSearchResult
- `src/HelixTool.Core/AzDO/AzdoApiClient.cs` — raw API client (thin HTTP layer)
- `src/HelixTool.Core/CiKnowledgeService.cs` — ci_guide content for all 9 repos

---

## 2026-03-16: Timeline Truncation Implementation — Partial Response Pattern

**By:** Ripley  
**Date:** 2026-07-18  
**Status:** Implemented, needs review

### Decision

When `azdo_timeline` returns more than 200 records, the MCP tool layer now returns a **partial response**: the first 100 records plus truncation metadata (`truncated: true`, `totalRecords: N`, `note: "⚠️ ..."`) pointing the agent to `azdo_search_timeline` as a better alternative.

This implements Larry's directive: "When truncating large MCP tool outputs, use the partial response pattern (return what fits with a continuation indicator) rather than just dropping data."

### What Changed

1. **`AzdoMcpTools.Timeline()`** — return type changed from `AzdoTimeline?` to `TimelineResponse?`. New `TimelineResponse` record adds `truncated`, `totalRecords`, and `note` fields (omitted from JSON when not set). Threshold: 200 records; budget: 100.
2. **`azdo_timeline` description** — now mentions `azdo_search_timeline` as an alternative for large builds (~35 words, within repo convention).
3. **`CiKnowledgeService.cs`** — 5 repo profiles (runtime, sdk, roslyn, efcore, VMR) updated to recommend `azdo_search_timeline` as the first investigation step. Generic overview sections also updated. Profiles with non-timeline-first workflows (aspnetcore, maui, macios, android) left unchanged.

### Trade-offs

- **Wire format change:** `TimelineResponse` replaces `AzdoTimeline` as the return type. Non-truncated responses are wire-compatible (same `id` + `records` structure). Truncated responses add three new fields. Downstream consumers (CLI, tests) may need updates if they depend on the exact type.
- **Fixed thresholds:** 200/100 are hardcoded consts. Could be made configurable later if needed, but keeping it simple for now.
- **Partial data ordering:** Takes the first N records by their position in the API response (timeline order). This preserves the hierarchical structure (stages appear before their child jobs/tasks) but may cut off later stages.

### For Dallas

- Review whether the wire format change from `AzdoTimeline?` to `TimelineResponse?` needs any CLI-side updates.
- Consider whether the 200/100 thresholds are appropriate, or if they should be configurable.

---

## 2026-05-08: Decision — Cut v0.6.0 release

**Author:** Ripley (Backend Dev)  
**Date:** 2026-05-08  
**Status:** Proposed (PR open, awaiting CI + human merge)

### Scope

v0.6.0 is a **minor** release on top of v0.5.4 (~1 month). No breaking API changes.

Included:
- **#46** Bump MCP SDK to 1.3.0; adopt central package management (`Directory.Packages.props`); fix package version drift.
- **#47** Tool/parameter annotations: explicit `OpenWorld` on all 25 MCP tools, `[AllowedValues]` on 10 params flowing into JSON Schema `enum` arrays + `annotations.openWorldHint` on protocol descriptors. Fixes NU1507. Closes #44, #45.
- **#48** Optional `IProgress<ProgressNotificationValue>` on `helix_download`, `helix_find_files`, `azdo_search_log` via internal `McpProgressAdapter`; no-token fast path preserved; initial+final progress beats from `ProgressReporter.CopyToWithProgressAsync`.

### Version rationale

**0.5.4 → 0.6.0** (minor). New user-visible features (annotations, progress), SDK upgrade, no breaking signature changes.

### Mechanics

- Branch: `squad/release-v0.6.0` from `origin/main`.
- Files bumped: `src/HelixTool/HelixTool.csproj` (`<Version>`), `src/HelixTool/.mcp/server.json` (`.version` and `.packages[0].version`).
- Release notes draft: `.squad/release-notes/v0.6.0.md`.
- PR: https://github.com/lewing/helix.mcp/pull/49
- Tag (post-merge, do NOT create pre-merge): `v0.6.0` on the merge commit.

### Post-merge command

```sh
gh release create v0.6.0 \
  --target main \
  --title "v0.6.0 — MCP SDK 1.3.0, tool annotations, progress notifications" \
  --notes-file .squad/release-notes/v0.6.0.md
```

Tag push triggers `.github/workflows/publish.yml`, which validates version consistency and publishes to NuGet / MCP registry.

### Risks / CI expectation

- Diff is **version metadata only** (no source changes). `ci.yml` should pass green; the build/test baseline (1167/1167) was already validated by Lambert on PRs #46/#47/#48.
- Publish workflow's version-consistency guard will pass: csproj `0.6.0`, server.json `0.6.0` ×2, tag `v0.6.0`.

---

## 2026-05-08: Lambert — PR #47 & PR #48 Verification Verdicts

**Date:** 2026-05-08  
**Verifier:** Lambert  
**Requested by:** Larry Ewing

### ✅ PR #47 — `squad/mcp-tool-annotations-and-cleanup` — APPROVE

**Scope:** `[AllowedValues]` on enum-like params; explicit `OpenWorld` on every MCP tool; NU1507 fix via `nuget.config` packageSourceMapping. Closes #42, #44, #45.

**Build:** ✅ `dotnet build` clean — **0 warnings, 0 errors**. NU1507 is gone (verified).  
**Tests:** ✅ `dotnet test` — **1167 passed / 0 failed** in ~3s.

**Annotation verification (reflection probe against `HelixTool.Mcp.Tools.dll`):**
- 25 `[McpServerTool]` methods discovered.
- `OpenWorld`: **22 true / 3 false / 0 null** — every tool sets it explicitly. The 3 `OpenWorld=false` are exactly the local-only tools that should be: `azdo_auth_status`, `helix_auth_status`, `helix_ci_guide`. ✓
- 10 parameters carry `[AllowedValues(...)]`, all on the right tools (`azdo_builds.{org,project,status}`, `azdo_timeline.filter`, `azdo_search_timeline.{recordType,resultFilter}`, `azdo_test_attachments.{org,project}`, `azdo_helix_jobs.filter`, `helix_status.filter`).

**Generated MCP tool descriptors confirm the annotations flow into protocol:**
- `azdo_builds`: `annotations.openWorldHint = true`, `annotations.idempotentHint = true`, `annotations.readOnlyHint = true`. Schema `properties.org.enum = ["dnceng-public","dnceng","devdiv"]`, `properties.project.enum = ["public","internal"]`, `properties.status.enum = ["all","cancelling","completed","inProgress","none","notStarted","postponed"]`.
- `azdo_search_timeline`: `recordType.enum = ["Stage","Job","Task"]`, `resultFilter.enum = ["failed","all"]`.
- `helix_status`: `filter.enum = ["failed","passed","all"]`.

`McpServerTool.Create()` succeeds for every tool — registration startup is clean (no schema-generation crashes).

**Verdict: ✅ APPROVE.** Merge.

### ✅ PR #48 — `squad/mcp-progress-notifications` — APPROVE

**Scope:** wires MCP progress notifications into `helix_download`, `azdo_search_log`, `helix_find_files` via the SDK 1.3.0 auto-injected `IProgress<ProgressNotificationValue>?` parameter. Adds `ProgressUpdate` record + `ProgressReporter` helpers in `HelixTool.Core` and an internal `McpProgressAdapter` at the tool boundary. Closes #43.

**Build:** ✅ `dotnet build` clean — **0 warnings, 0 errors**.  
**Tests:** ✅ `dotnet test` — **1167 passed / 0 failed** baseline (no existing test broken by the new parameter shape).

**Smoke verification (a temporary `ProgressNotificationsSmokeTests.cs` was added locally, run, then removed; not committed to Ripley's branch):**
- All three instrumented tools (`HelixMcpTools.Download`, `HelixMcpTools.FindFiles`, `AzdoMcpTools.SearchLog`) declare an `IProgress<ProgressNotificationValue>?` parameter that:
  - is optional with `default(null)`, and
  - has no `[Description]` — so the SDK keeps it out of the JSON schema and treats it as auto-injected. ✓
- `McpProgressAdapter.Wrap(null)` returns `null` (preserves the no-allocation fast path when the client supplies no progress token). ✓
- `McpProgressAdapter.Wrap(sink)` returns an `IProgress<ProgressUpdate>` that forwards `ProgressUpdate(Current, Total, Message)` to the SDK as `ProgressNotificationValue { Progress = (float)Current, Total = (float?)Total, Message }`. Two reports fed through, two captured at the SDK sink with correct float-coerced values. ✓
- `ProgressReporter.CopyToWithProgressAsync` over a 1 MiB stream emits ≥ 2 updates (initial + final), and final `Current` equals payload length and `Total` is preserved on every report. ✓

**Total with smoke tests in place: 1174 passed / 0 failed.**

The smoke test file is intentionally **not committed** — it lives in this verdict only. If Ripley wants regression coverage in the branch, the body is reproducible from this verdict (key idea: reach the internal `McpProgressAdapter` via reflection on the tools assembly because there is no `InternalsVisibleTo` from `HelixTool.Mcp.Tools` to `HelixTool.Tests`).

**Recommendation for follow-up (not blocking):** add `[assembly: InternalsVisibleTo("HelixTool.Tests")]` to `HelixTool.Mcp.Tools` so future internal seams (like `McpProgressAdapter`) can be tested without reflection. Either Dallas (architecture call) or Ripley (small implementation tweak) could land that.

**Verdict: ✅ APPROVE.** Merge.

### Notes for Larry / Scribe

- Both branches build and test green on net10.0, MCP SDK 1.3.0, with central package management.
- PR #47 also clears the lingering NU1507 warning from the SDK upgrade (the only outstanding noise on the SDK 1.3.0 branch).
- During this session, PR #47 already appears merged to `origin/main` as commit `9db7582`; PR #48 is still on its branch awaiting merge. The verification above was run against the unmerged branch tips for both.

---

## 2026-05-20T18:40:00Z: Pagination Phase 1+2 Implementation

**By:** Ripley (Backend Dev)  
**Status:** Complete, 1180/1180 tests passing

### What

Standardized pagination across MCP tools per Dallas's pagination audit spec. Implemented Phase 1 (wrapped 2 raw-list tools in `LimitedResults<T>`) and Phase 2 (added `truncated`/`note` fields to 8 bespoke result types).

### Decision

- **Phase 1:** `azdo_changes` and `azdo_test_runs` now return `LimitedResults<AzdoBuildChange>` / `LimitedResults<AzdoTestRun>` with `truncated` bool + `note` string. Backward compatible (MCP SDK serializes as JSON superset of raw array).
- **Phase 2:** Added `truncated` and `note` fields to 8 bespoke result types: `StatusResult`, `FilesResult`, `FindFilesResult`, `TestResultsToolResult`, `BatchStatusResult`, `TimelineSearchResult`, `HelixJobsFromBuildResult`, `BuildAnalysisResult`. Additive-only, no wire-format break.
- **helix_find_files truncation logic:** Wrapped result type as `FindFilesResults` wrapper (not raw list). Truncation logic updated to track total work items before `.Take(maxItems)`. `truncated = true` when total work items > `maxItems` parameter.

### Caveats & Trade-offs

- Most tools do not have explicit caps beyond fetching all data from upstream APIs, so `truncated` remains `false` by default. Only `helix_find_files` and the 2 Phase-1 tools have active truncation logic.
- `helix_search` and `azdo_search_log` already had truncation metadata (noted in Dallas's spec), so no changes needed.
- Wire format changes are all additive (new `LimitedResults<T>` wrapper for Phase 1 tools, new optional fields for Phase 2 types). Backward compatible with prior JSON clients that ignore new fields.

### Build Result

✅ **Clean build:** `dotnet build` — 0 warnings, 0 errors.  
✅ **Full test suite:** 1180/1180 passing.

### Branch & Commits

- **Branch:** `squad/pagination-standardize` (Note: committed to local main due to branch-hygiene issue; see Scribe logs)
- **Commit:** 0a82e58

---

## 2026-05-20T22:00:00Z: Pagination Contract Tests

**By:** Lambert (Tester)  
**Status:** Complete, 13/13 tests passing

### What

Wrote 13 pagination contract tests (333 LOC) in `src/HelixTool.Tests/AzDO/PaginationContractTests.cs` verifying pagination standardization across Phase 1 tools per Dallas's audit spec.

### Test Coverage

- **CreateLimitedResults helper** (5 tests): Validates truncation flag, note population, backward compatibility.
- **Phase 1 tools** (4 tests): Verifies `azdo_changes` and `azdo_test_runs` return correct types and truncation metadata.
- **Default parameter validation** (4 tests): Ensures parameter defaults align with spec (default `top`=20, `maxItems`=50).

### Design

- Tests use NSubstitute mocks to simulate service truncation behavior.
- Employed anticipatory testing pattern — tests written against Dallas's spec while Ripley implemented in parallel.
- Edge cases covered: empty list, single item, full capacity, overflow scenarios.

### Test Result

✅ **13/13 passing** when run independently against HelixTool.Core.  
✅ **Full suite:** 1180/1180 passing (including all prior tests + 13 new).

### Branch & Commits

- **Branch:** `squad/pagination-standardize` (Note: committed to local main due to branch-hygiene issue; see Scribe logs)
- **Commits:** 181ff5b + d5fde34
