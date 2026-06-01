## Learnings — Issue #61 Bug B Re-triage (2026-05-25)

### Deferral Calibration: When "Wait for Evidence" Becomes "Fix Now"

- **The May 22 deferral was defensible at the time, but not revisited.** Finding #2 (16 identical catch-throw blocks) had high count but no documented user impact. Correctly decided: "Don't refactor control boundaries without test evidence." That principle is sound. HOWEVER, the deferral should have come with a **revisit trigger**, not a hard "Q3 2026" date.

- **Low-probability-but-systemic findings need revisit triggers, not blanket deferrals.** The pattern:
  1. Finding: "16 repetitive catch blocks (boilerplate + control boundary)"
  2. Deferral: "Wait for exception test coverage before refactoring"
  3. ❌ WRONG: Set hard date (Q3 2026) and move on
  4. ✅ RIGHT: Set revisit trigger ("If exception goes uncaught in production → promote immediately")

- **The cost of the deferral was higher than estimated.** User-visible silent failures (`success=False, result=None` with no error message) appeared in production on 2026-05-25 — three days after deferral. The boilerplate pattern itself was masking exception handling gaps (AggregateException, TaskCanceledException not caught). Had we had a weekly revisit trigger, we'd have promoted Finding #2 to "fix now" on 2026-05-25 instead of discovering it reactively when Ash investigated a live incident.

- **Test coverage is not a blocker; it's a validation gate.** My May 22 call was "don't refactor without test coverage." The correct interpretation is: centralize now (fixes production bug), validate with tests afterward (Lambert writes exception-path tests in weeks 2–3). The user-visible bug provides sufficient justification to proceed. This converts the deferral gate from a precondition ("before you start") to a validation gate ("after you ship").

- **Control boundaries deserve special triage attention.** Exception handling, parameter validation, and task coordination are control boundaries where small oversights cause silent failures. Boilerplate at control boundaries can mask gaps that manifest only under load or concurrency. Heuristic: if a refactoring target is at a control boundary (exception handler, router, cache validator), flag it as "lower priority unless user impact emerges," not "safe to defer indefinitely."

- **Deferred Finding #2 should have been tracked in a revisit backlog.** The `.squad/decisions.md` summary said "deferred to Q3 2026," but there was no tracking of the precondition ("if exception goes uncaught") that would trigger an earlier revisit. Future: maintain a revisit-trigger index in `.squad/constraint-tracking.md` or similar, so that findings with emerging evidence (user reports, incident logs) are automatically elevated.

**Recommendation for future deferrals:** Every deferred finding gets a **revisit trigger** (event or time-based), not a hard date. Example:
- Deferral: "Found #2 (catch-throw boilerplate). Defer until we have exception test coverage or user reports silent failures."
- Revisit trigger: "Time: Q3 2026 OR Event: any user report of `success=False, result=None` with no error message"
- Status tracking: Add to `.squad/constraint-tracking.md` with owner (Dallas reviews Q3 2026 or upon trigger)

**Retriage document:** `.squad/decisions/inbox/dallas-issue61-bugb-retriage-2026-05-25.md`

## Learnings — Issue #61 merge gate 2026-05-25

- **`await Task.WhenAll` does NOT throw `AggregateException`.** Verified via C# repro: `await` unwraps to the first inner exception (e.g., `HttpRequestException`). Only `.Wait()` / `.Result` throws `AggregateException`. The `Task.Exception` property IS an `AggregateException` (for inspection), but `await` strips it. This means Ash's narrative ("AggregateException from Task.WhenAll is uncaught") was wrong — but the fix (centralized catch-all handler) was correct regardless.

- **Copilot reviewer caught what human review missed.** The `AggregateException` framing error propagated through Ash → Dallas retriage → Lambert tests → Ripley handler without anyone exercising the actual failure path. Copilot's review on #63/#64 correctly identified the semantic mismatch. Lesson: automated reviewers complement human review precisely for "obvious if you check, invisible if you don't" issues.

- **The real uncaught exception family was `TaskCanceledException` / `OperationCanceledException`.** The original catch clauses (`when (ex is InvalidOperationException or HttpRequestException or ArgumentException)`) already caught the exceptions `await Task.WhenAll` would unwrap. The production silent failure in session 9de92b14 was primarily Bug A (parameter name mismatch → MCP SDK binding failure before method entry).

- **Name the exception by exercising it, not by guessing from source-read.** Future root-cause analyses must write a 10-line repro exercising the failure path before naming the exception type. `Task.WhenAll` → `AggregateException` is the `.Wait()` mental model, not the `await` model. A repro would have caught this in 5 minutes.

- **Defensive dead code in handlers is acceptable when harmless.** PR #64's `AggregateException` unwrap in `McpExceptionHandler` is dead code under normal `await` usage, but it's harmless defensive code that guards against future `.Wait()` callers. Approved without change; follow-up #65 filed for flatten-vs-first-inner improvement.

- **Merge-conflict resolution is a Lead responsibility when sequencing matters.** PR #62 (param rename) and #64 (exception centralization) both touched `AzdoMcpTools.cs`. Merging #62 first (correct order) created conflicts in #64. Resolved by taking #64's centralized handler pattern and applying #62's `buildIdOrUrl` rename. Build + 1292 tests pass.

**Decision document:** `.squad/decisions/inbox/dallas-issue61-merge-gate-2026-05-25.md`
**Follow-up issue:** #65 (schema tests, flatten AggregateException, unskip Lambert tests, calibration process)

---

## 2026-05-25: Issue #61 — Two Decision Gates + Merge Gate Review

**Session:** Issue #61 Silent MCP failures (Bug A + Bug B)  
**Status:** Issue Complete; all 3 PRs merged ✅  
**Role:** Decision maker (Bug B re-triage) + Merge gate reviewer

### Bug B Re-triage Decision (May 25)

The May 22 deferral of Finding #2 (boilerplate, Q3 2026) is **no longer valid.** Ash's investigation revealed production-impact evidence: silent failures in live session caused by uncaught exception types (AggregateException, TaskCanceledException).

**VERDICT: PROMOTE Finding #2 to FIX NOW** — 3–4h Ripley work to centralize exception handling.

**Why the May 22 deferral was defensible then; why it's no longer valid now:**
- **Then:** No documented user-visible impact; extraction risk without test evidence
- **Now:** User-visible bug in production; test evidence provided by Ash

**Reasoning:**
- User-visible bug justifies proceeding (not hypothetical)
- Centralization is lower-risk than leaving scattered
- Test coverage is parallel, non-blocking (Lambert runs audit in parallel)
- Timeline predictable (3–4h effort)

### Merge Gate Review (Final Decision)

Reviewed all three PRs (Ripley ×2, Lambert ×1) and verified technical claims.

**Technical finding:** Verified via C# repro that `await Task.WhenAll(t1, t2)` unwraps to the **first inner exception**, NOT AggregateException. Only `.Wait()` throws AggregateException. This corrects Ash's narrative but validates her fix (centralized catch-all still correct).

**Per-PR Verdicts:**
- PR #62 (Ripley, parameter standardization): APPROVE & MERGE ✅
- PR #63 (Lambert, exception coverage): APPROVE WITH FOLLOW-UP ✅
- PR #64 (Ripley, exception centralization): APPROVE & MERGE ✅

**Merge order:** #62 → #64 → #63 (all executed successfully)

### Key Calibration Learning

**Name an exception by exercising it, not by guessing from source-read.**

Ash's investigation correctly identified the gap and the right fix, but incorrectly named "AggregateException from Task.WhenAll" as the uncaught exception. In reality, `await Task.WhenAll` unwraps to the inner exception; only `.Wait()` throws AggregateException. The actual uncaught types were TaskCanceledException and OperationCanceledException.

**Better practice:**
1. Write 10-line repro that forces failure
2. Observe: `catch (Exception ex) { Console.WriteLine(ex.GetType()); }`
3. Only then name the type in narrative

**This is especially critical for Task.WhenAll, Task.WhenAny, ConfigureAwait** — await machinery has non-obvious unwrapping behavior.

**Net impact:** Narrative error (cosmetic); zero production risk. Fix still correct (catch-all pattern catches everything). This lesson should be preserved for future exception investigations.

### Issue #61 Closed — 3 PRs Merged

- PR #62: Standardize `buildIdOrUrl` parameter (Bug A) ✅
- PR #64: Centralize MCP exception handling (Bug B) ✅
- PR #63: Exception coverage audit + tests (baseline) ✅

Both bugs fixed. Follow-up issue #65 filed for: schema test, flatten exceptions, unskip tests, rolling coverage tests, preserve calibration lesson.

### Deferral Calibration Reflection

**What you got right on May 22:**
- Recognized control-flow refactoring is risky without test evidence
- Correctly identified precondition ("better exception test coverage")
- Decision logic was sound

**What you missed:**
- Should have set a **revisit trigger** (e.g., "if any user report of silent exception behavior → promote immediately")
- Didn't weight "low-probability but high-impact" findings heavily enough
- The boilerplate pattern itself masks gaps that surface sooner than Q3

**Calibration for future:** Low-count but high-risk structural findings get a **REVISIT TRIGGER**, not blanket deferral. Revisit trigger: "If any exception goes uncaught in production → promote immediately."

**Cost of deferral was higher than estimated:** Not wrong, but should have checked for production evidence weekly. Ripley/Lambert can help with lightweight weekly checks on deferred findings.

## Learnings — PR #66 external contributor review 2026-05-28

- **Bug pattern: null ExitCode → sentinel -1 → mis-bucketed as failure.** Helix `/details` returns `ExitCode == null` for Waiting/Running/Unscheduled work items. The code `details.ExitCode ?? -1` coerced null to -1, then `results.Where(r => r.ExitCode != 0)` classified -1 as failed. Fix: derive `IsCompleted = details.ExitCode.HasValue`, three-way bucket (InProgress / Failed / Passed). Null-coercion of nullable ints to sentinels is a recurring hazard when the sentinel value (-1) falls in the domain of valid failure codes.

- **Additive wire-format discipline must be verified, not trusted.** PR claimed "additive" — verified by confirming: (a) new fields use `init` properties with default values (int → 0, nullable list → null with `JsonIgnore(WhenWritingNull)`), (b) existing field names/types/positions unchanged, (c) no tests assert absence of new fields. For MCP DTOs, "additive" means: new fields only, no renames, no type changes, nullable or defaulted so old consumers see no difference.

- **External contributor reviews differ from internal team PRs.** (a) Be especially clear and specific in review feedback — can't easily ping for follow-ups. (b) Merge first when the fix is correct — don't make external contributors wait behind internal PRs that don't conflict. (c) Production evidence in the PR body (specific AzDO build IDs, Helix job IDs, queue names) made verification efficient. (d) File follow-up issues ourselves rather than requesting them from external contributors.

- **Second code path with same bug pattern (`GetWorkItemDetailAsync` line 563) not addressed by PR.** Deliberately scoped to the aggregation path only — correct prioritization since the detail view is informational. Filed as follow-up. Pattern: when fixing a bug in one code path, grep for the same pattern in other paths and explicitly note what's in/out of scope.


## 2026-05-28: PR #66 Review & Issue #67 Policy Decision

**PR #66 Review:** Approved akoeplinger's external contribution fixing Helix waiting work items counted as failed. Identified follow-up on GetWorkItemDetailAsync line 563 ExitCode pattern. Coordinated merge sequencing with PR #68/69.

**Issue #67 Policy Decision:** Reviewed Ash's silent MCP failure investigation. Decided on CallToolFilters middleware as central solution (ArgumentException → McpException, ~10 LOC, all 25 tools). Sequenced v0.7.5 release with CallToolFilters as primary item, schema audit parallel. Deferred per-tool validation prologues (only for combo-rules, narrow scope).

**Deliverables:** PR #66 review document + McpException policy decision document (merged into decisions.md 2026-05-28)

## 2026-06-01T19:01:23Z: Issue #74 Ash Follow-up — Critical Gap Alert (Decision Required)

**From Ash (Analyst) via Scribe:**
Analysis revealed critical measurement gap: Ash's ground-truth assessment excluded `outputSchema` (20/25 tools use `UseStructuredContent=true`). Real `tools/list` response likely 15–25 KB, not 11.3 KB (inputSchema alone). This means:
- Issue #74's original estimate (16.2 KB) may accidentally be close to reality via the wrong path
- **Before approving any trim work, Ripley must measure real `tools/list` payload including outputSchema** (1–2 hour task)

**Your decision gate:**
1. **Measure first?** (Ripley: run `McpServerTool.Create` + `ProtocolTool` serialization per `.squad/skills/mcp-wire-format-trim/SKILL.md`)
2. **Proceed with conservative trim?** (−1 KB, v0.7.8, post-helix_status rename)

**Recommendation:** Prioritize measurement. Aligns with GitHub study caveat: schema pruning yields 0% savings if context dominated by other content.

**Full analysis:** `.squad/decisions.md` (merged Issue #74 ground-truth section)

---

## 2026-06-01T14:02:01.195-05:00: Issue #74 Ground-Truth Measurement Complete (Ripley)

**Status:** Measurement complete. Ripley delivered ground-truth `tools/list` payload size: **28,941 bytes (28.26 KB)**.

**Key measurements:**
- Full payload: 28.26 KB — 44% larger than issue #74's 16.2 KB heuristic estimate
- inputSchema: 11.07 KB — Ash's estimate accurate within 2% ✅
- outputSchema: 8.88 KB — critical discovery; validates Ash's concern about the measurement gap
- 20/25 tools have `UseStructuredContent=true` (structured output schemas)

**Per-tool breakdown:** azdo_timeline (2,099 B) and azdo_search_log (2,027 B) are top contributors. Top 5 outputSchema targets (azdo_timeline 1,123 B, helix_status 1,001 B, azdo_build 929 B, azdo_search_log 800 B, helix_parse_uploaded_trx 656 B) sum to ~4.5 KB.

**Trim opportunities:** Conservative (descriptions only) → −0.5 KB. Moderate (outputSchema pattern 2 on top 5) → −4.5 KB + descriptions → target 23 KB.

**Test artifact:** `src/HelixTool.Tests/McpToolsListPayloadTests.cs` added as regression guard.

**Decision required from Dallas:**
1. **Go** — Approve conservative trim (−0.5 KB, v0.7.8, post-helix_status rename) OR
2. **Go with measurement** — Measure live caching behavior first (determine if tools/list is per-turn or per-session) OR
3. **No-go** — Defer trimming; payload is acceptable for now

**Recommendation:** Payload is in-scope per Ash's decision criteria (28.26 KB > 15 KB IF called per-turn). Decide on caching behavior measurement vs. proceed directly to trim authorization.

