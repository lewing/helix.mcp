# Dallas — History (Summarized)

## Current Work (2026-05-25 through 2026-06-01)

### Summary of Recent Decisions

**Issue #61 — Silent MCP Failures (May 22–25):** Re-triaged boilerplate exception handling. Promoted Finding #2 (catch-throw pattern) from Q3 deferral to "fix now" based on user-visible evidence (silent failures in production on May 25). Key learning: **Name exceptions by exercising them, not by source-read.** Verified via C# repro that `await Task.WhenAll` unwraps to the inner exception (not AggregateException as narrative suggested). Fix still correct. PRs #62 (parameter standardization), #64 (exception centralization), #63 (exception coverage) all merged.

**PR #66 Review (May 28):** Approved external contributor's fix for Helix waiting work items miscounted as failed. ExitCode null handling pattern corrected; identified same pattern in GetWorkItemDetailAsync for follow-up. Calibration: **Additive wire-format claims must be verified** (new fields use defaults, no renames/type changes). External PR reviews require clear feedback, merge promptly when correct, file follow-ups ourselves.

**Issue #74 — Ground-Truth Schema Measurement (June 1):** Ripley completed measurement of `tools/list` payload per Ash's decision gate. Real payload: **28.26 KB** (44% larger than issue estimate of 16.2 KB). inputSchema verified at 11.07 KB (within 2% of Ash's estimate). outputSchema contributes 8.88 KB (critical discovery). Payload is in-scope for trimming per decision criteria (>15 KB if per-turn). **Decision gate:** go/no-go on trimming, measure live caching behavior, or defer.

---

## Detailed Work Log (Archived)

See sections below for full context on Issue #61 and PR #66 decisions, calibration learnings, and technical findings.

### Issue #61 — Silent MCP Failures (May 22–25)

#### Bug B Re-triage & Merge Gate (Archived Details)

The May 22 deferral of Finding #2 (boilerplate, Q3 2026) was no longer valid by May 25. Production-impact evidence emerged: silent failures in live session caused by uncaught exception types. **VERDICT: PROMOTE Finding #2 to FIX NOW** — 3–4h Ripley work.

**Why May 22 deferral was defensible then; invalid now:**
- **Then:** No documented user-visible impact; extraction risk without test evidence
- **Now:** User-visible bug in production; test evidence provided by Ash

**Technical findings:**
- `await Task.WhenAll(t1, t2)` unwraps to the first inner exception, NOT AggregateException (only `.Wait()` throws AggregateException)
- Ash's narrative was imprecise but the fix (centralized catch-all) was correct regardless
- Actual uncaught types: TaskCanceledException, OperationCanceledException (already caught by fix)
- Defensive dead code in McpExceptionHandler approved (harmless guard against future `.Wait()` callers)

**Merge order & PRs:**
- PR #62 (Ripley, parameter standardization): ✅ Merged
- PR #64 (Ripley, exception centralization): ✅ Merged
- PR #63 (Lambert, exception coverage): ✅ Merged with follow-ups

**Calibration learning:** Control boundaries (exception handlers, routers, validators) deserve special triage attention. Boilerplate at control boundaries masks gaps that surface under load. Heuristic: if refactoring target is at control boundary, flag as "lower priority unless user impact emerges," not "safe to defer indefinitely." Future deferrals should include **revisit triggers** (event or time-based), not hard dates.

---

### PR #66 — External Contributor Review (May 28)

**Fix:** Helix waiting work items miscounted as failed due to null ExitCode → sentinel -1 coercion.

**Root cause:** ExitCode == null for Waiting/Running/Unscheduled states. Code `details.ExitCode ?? -1` coerced null to -1, then results filtered by `r.ExitCode != 0` classified -1 as failed. Fix: derive `IsCompleted = details.ExitCode.HasValue`, three-way bucket (InProgress / Failed / Passed).

**Pattern hazard:** Null-coercion of nullable ints to sentinels is recurring when the sentinel value (-1) falls in the domain of valid failure codes.

**Additive wire-format verification:** Confirmed new fields use `init` properties with defaults, no renames/type changes, no tests assert absence of new fields.

**External PR best practices:** (a) Be clear and specific in feedback. (b) Merge promptly when correct; don't delay external contributors behind internal PRs. (c) Production evidence in PR body (AzDO IDs, Helix job IDs) enables efficient verification. (d) File follow-ups ourselves.

**Follow-up:** Same ExitCode null-coercion pattern found in GetWorkItemDetailAsync line 563 (deliberately scoped out; detail view is informational).

---

## Decision Archive

See `.squad/decisions.md` for full decision documentation on Issue #61 Policy (CallToolFilters), Issue #74 Schema Cost Framework, and Issue #74 Ground-Truth Measurement.

For full work history from earlier 2026 sessions, see `.squad/agents/dallas/history-archive-2026-06-01.md`.
