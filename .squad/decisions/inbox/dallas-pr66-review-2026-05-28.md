# PR #66 Review — fix(helix): waiting work items must not be counted as failed

**Reviewer:** Dallas (Lead)
**Date:** 2026-05-28
**Author:** akoeplinger (Alexander Köplinger, external contributor — dotnet runtime team)
**Branch:** `fix/in-progress-workitems-not-failed`

---

## Per-Section Verdicts

### 1. Bug Analysis Correctness — ✅ Verified

The bug is real and correctly diagnosed. On `main`:
- `CreateDetailedResultAsync` line 110: `var exitCode = details.ExitCode ?? -1;` coerces null → -1 for Waiting/Running/Unscheduled work items.
- `GetJobStatusAsync` lines 85-86: `results.Where(r => r.ExitCode != 0)` then buckets -1 as failed.
- Helix SDK `IWorkItemDetails.ExitCode` is `int?` (confirmed in `IHelixApiClient.cs:48`), returning `null` for in-progress states.
- Production shape confirmed: 2 osx jobs × (1 Finished + 24 Waiting) → misreported as `failedCount: 24` each.

### 2. Fix Correctness and Completeness — ✅ Verified (with minor follow-up)

- `IsCompleted = details.ExitCode.HasValue` correctly covers all in-progress states (Waiting, Running, Unscheduled — all return `ExitCode == null`).
- Three-way bucketing `(!IsCompleted → InProgress, IsCompleted && != 0 → Failed, IsCompleted && == 0 → Passed)` is correct and exhaustive.
- `FailureCategory` classification now correctly gated by `isCompleted && exitCode != 0`.
- `CreatePassedResult` (line 94) doesn't specify `IsCompleted`, defaulting to `true` — correct since it's only called when `wi.ExitCode == 0` at summary level.
- `CachingHelixApiClient` preserves `ExitCode` as `int?` through the cache layer; `IsCompleted` is derived at the service layer. No cache deserialization risk.

**Minor follow-up (not a blocker):** `GetWorkItemDetailAsync` at line 563 has the same `ExitCode ?? -1` → `ClassifyFailure` pattern for single work item detail views. This is a different code path (informational, not aggregation) and the `State` field gives consumers context, but ideally should get the same `IsCompleted` treatment for consistency. File as follow-up issue.

### 3. Wire Format / API Contract — ✅ Verified Additive

- New fields: `inProgressCount` (int, defaults 0), `inProgress` (nullable list, `JsonIgnore(WhenWritingNull)`), `totalInProgress` (int, defaults 0).
- Existing fields (`failedCount`, `passedCount`, `totalFailed`, `totalPassed`) unchanged in name, type, and position.
- `failedCount` semantics changed: previously included in-progress items (bug), now correctly excludes them. This is a **bug fix**, not a breaking change — consumers who were getting inflated counts now get correct ones.
- JSON property casing: all `camelCase` (`inProgressCount`, `totalInProgress`, `inProgress`) — consistent with existing DTO conventions.
- `JsonIgnore(WhenWritingNull)` on `inProgress` list means old consumers won't see the field when empty — clean additive behavior.
- No tests assert absence of `inProgressCount` in JSON output.

### 4. CLI Behavior — ✅ Verified

- `hlx status` text: adds "In progress: N" section with yellow coloring, only when `InProgress.Count > 0`. Existing output lines unchanged.
- `hlx batch-status` text: appends `, N in progress` to per-job and overall lines only when present. Existing format preserved.
- JSON output (`--json`): `inProgressCount` and `inProgress` added additively.
- No CI scripts in this repo parse `hlx status` text output.

### 5. Tests — ✅ Sufficient

- `GetJobStatusAsync_WaitingWorkItems_AreInProgress_NotFailed`: 1 finished + 2 waiting, asserts `Failed.Count == 0`, `InProgress.Count == 2`, `IsCompleted == false`.
- `GetBatchStatusAsync_WaitingWorkItems_DoNotInflateTotalFailed`: Reproduces exact production shape (2 jobs × 1 finished + 24 waiting), asserts `TotalFailed == 0`, `TotalInProgress == 48`.
- Regression verification: Tests reference `InProgress`/`TotalInProgress` which don't exist on main — compilation failure against pre-fix code confirms the tests are structurally tied to the fix.
- Tests follow project patterns: xUnit, NSubstitute mocking, same file (`HelixServiceStatusOptimizationTests`), consistent `Arrange/Act/Assert` style.
- **Nice-to-have edge cases** (not blocking): mixed Finished+Waiting+Failed in same job; all-Waiting job; cached replay with `IsCompleted` absent. These can be follow-up.

### 6. Merge Conflict Risk — ✅ Manageable

- **PR #68 (Lambert)**: Touches `HelixMcpTools.cs` attribute `[Description]` strings on line 26 (`helix_status`). PR #66 touches `HelixMcpTools.cs` body at lines 58-75 (adding `InProgressCount`/`InProgress` to `StatusResult` construction). Different hunks — **no conflict**.
- **PR #68**: Also touches `.squad/` files (deleted decision inbox files, added test). No overlap with #66.
- **PR #69 (Ripley)**: Touches `Program.cs` at line 864+ (`Mcp()` method — CallToolFilters middleware). PR #66 touches `Program.cs` at lines 299-600 (`Status()` and `BatchStatus()` methods). Different methods — **no conflict**.
- **PR #69**: Also touches `HelixTool.Mcp/Program.cs` which PR #66 does NOT touch. No conflict.

### 7. Build + Test Gate — ✅ Verified

- `dotnet build`: 0 warnings, 0 errors.
- `dotnet test`: 1295 passed, 0 failed, 2 skipped (unchanged skip count from main).
- CI checks: `build (ubuntu-latest)` and `build (windows-latest)` were IN_PROGRESS at review time; `Squad CI / test` completed SUCCESS.
- New tests exercise the exact fix path (Waiting work items with `ExitCode == null`).

### 8. Style / Conventions — ✅ Clean

- Follows existing record patterns in `HelixService.cs` (`WorkItemResult`, `JobSummary`, `BatchJobSummary`).
- `IsCompleted = true` default parameter on `WorkItemResult` record — backward-compatible for all existing callers.
- C# naming conventions followed (`IsCompleted`, `InProgress`, `TotalInProgress`).
- No new dependencies.
- XML doc comments updated on `WorkItemResult` and `JobSummary`.

---

## Merge Sequencing

**Recommended order: #66 first, then #68, then #69.**

1. **#66 (this PR)** — Real production bug fix from external contributor. No conflicts with either in-flight PR. Merge first to unblock the external contributor and fix the bug.
2. **#68 (Lambert)** — Description attribute improvements. Independent of #66 changes (different hunks in shared files).
3. **#69 (Ripley)** — CallToolFilters middleware. Independent of #66 (different methods in Program.cs).

No rebase needed for any ordering since all three PRs touch non-overlapping hunks.

---

## Follow-Up Items (non-blocking)

1. **`GetWorkItemDetailAsync` ExitCode ?? -1 consistency**: Line 563 has the same sentinel pattern for single work item detail views. Should get `IsCompleted` treatment for consistency. File as separate issue.
2. **Additional test edge cases**: Mixed Finished+Waiting+Failed in same job; all-Waiting job. Lambert can add in a follow-up.

---

## Final Verdict

**APPROVE & MERGE**

Clean, well-scoped bug fix with solid regression tests. Wire-format changes are strictly additive. No conflicts with in-flight PRs. External contributor provided thorough write-up with production evidence. Merge first, before our internal PRs.
