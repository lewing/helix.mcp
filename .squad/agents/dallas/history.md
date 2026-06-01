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

## Learnings

### Parameter Alias Layer Choice — `buildIdOrUrl` Review (2026-06-01)

**Problem:** Agents supplied `build_id`/`buildUrl` (intuitive names) instead of canonical `buildIdOrUrl`, causing MCP SDK binding failure before tool code executed. Two rejection patterns confirmed from session e9c219bd telemetry.

**Layer decision: `CallToolFilter` in `McpServerOptionsExtensions`, not per-tool attribute metadata.**

Rationale: The failure is a *key*-normalization problem, not a *value*-normalization problem. `AzdoService.NormalizeFilter` handles value normalization post-binding — it cannot help when the binder rejects a call because the required key is absent. The only layer with access to `CallToolRequestParams.Arguments` before binding is a `CallToolFilter`. `McpServerToolAttribute` has no alias-map surface in MCP SDK 1.3.0. Per-tool method signatures (adding `buildId`/`buildUrl` as optional params) would require 11 method signature changes, add schema bytes to `tools/list`, and create coalesce logic in 11 bodies — wrong granularity.

**One flat global alias map is correct until a second use case appears.** `buildIdOrUrl` is unique enough as a canonical key that global scope is safe. Pattern: `Dictionary<string, string>(OrdinalIgnoreCase)` mapping alias → canonical; first match wins; insertion order is significant for multi-alias calls.

**Combine normalization INTO the existing binding-error filter, or enforce order via a composite helper.** Separate filter registration leaves an ordering dependency unchecked by the type system. Pre-invocation hygiene concerns (alias normalization + exception translation) belong together.

**Drift telemetry must be built in from day one.** An alias filter with no logging cannot tell us whether the problem is improving. `ILogger? logger = null` parameter; `Debug`-level log when alias fires. Cheap to add; expensive to retrofit.

**Approved: APPROVE WITH CHANGES.** Verdict at `.squad/decisions/inbox/dallas-buildidorurl-verdict-2026-06-01.md`.

**Status:** Verdict approved 2026-06-01. Ripley implemented same date per 4 directives. Lambert completed 11 test cases (all 7 scenarios), full suite 1312 pass / 2 skip. Decision merged to decisions.md.

---

### Issue #74 Schema Trim — Lead Verdict (2026-06-01)

**Verdict: CONDITIONAL NO.** Do not pursue active `tools/list` trimming at 28.26 KB. Rationale: `tools/list` is a cold-load cost cached per-session by all major MCP clients. 28 KB amortized over sessions processing hundreds of KB of build/test data is <1% of token budget. The GitHub study's caveat ("0% benefit when context dominated by other content") applies directly.

**Lever ranking (value-vs-risk):**
1. **Selective outputSchema removal** (4.5–8.9 KB, 16–31%): Best lever, HOLD until needed. Pattern 2 in SKILL.md preserves wire payload. No known consumer parses `tools/list` outputSchema.
2. **Description tightening** (~0.5–1 KB): Do opportunistically during normal tool work, not as a dedicated project.
3. **Lazy/scoped tool loading** (up to 50%): Architecturally interesting, defer to v0.9+. Premature at 25 tools.
4. **Parameter consolidation** (~0.5 KB): REJECTED. Breaks v0.7.x API contracts for negligible savings.

**Decision flip trigger:** Consumer confirmed to re-fetch `tools/list` per-turn, tool count >40, or real user reports token budget pressure.

**Calibration learning:** Estimated-vs-measured gap was 44% (16.2 KB estimate vs 28.9 KB real). Two errors partially cancelled (inputSchema overcount + outputSchema omission). Lesson: **always measure the full wire path before deciding on optimization work**. Ash's decision to gate on ground-truth measurement before approving any trim work was correct and saved us from acting on wrong numbers in either direction.

For full work history from earlier 2026 sessions, see `.squad/agents/dallas/history-archive-2026-06-01.md`.
