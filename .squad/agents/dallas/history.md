
**2026-05-21 10:33Z:** Ash audit complete. 3 decisions pending for your review: service-layer validation, azdo_auth_status sync vs async, structured error codes. See decisions.md.

**2026-05-21 17:58Z:** Completed design proposal for surfacing WorkItemSummary.ExitCode + ConsoleOutputUri. Filed to `.squad/decisions/inbox/dallas-surface-workitem-fields.md`. Key call: optimize GetJobStatusAsync (skip detail fetch for passed items), defer ConsoleOutputUri streaming.

## Learnings — Slop audit triage 2026-05-22

- **Structural duplication > boilerplate repetition for triage priority.** Ash's audit found 16 catch-throw handlers (high count) and 6 DTO classes (schema duplication, low count). Verdict: FIX the DTOs (functional duplication, maintainability hazard); DEFER the handlers (control-flow refactoring creates risk of silent behavior change). Boilerplate extracted without formal exception-path coverage can break silently.
- **Exception handler extraction requires test coverage before refactoring.** The catch blocks are a control boundary; refactoring them into a shared helper without exercising all 16 paths (pass-through behavior, exception propagation, stack trace preservation) risks silently changing exception semantics. Safer to defer extraction until we have explicit exception test coverage.
- **Style drift (attribute placement, naming) is not worth a refactor PR.** [JsonPropertyName] placement inconsistency (inline vs above-line) and usage (some with attributes, some relying on defaults) is low-priority. Bundle standardization into the next structural refactor rather than spinning a dedicated PR. Functional correctness (schema match) > consistency (attribute placement style).
- **Intentional API versioning (service models vs MCP results) is not slop.** The separation between AzdoModels.cs (API-level) and McpToolResults.cs (tool-level) is a deliberate stability boundary. Schema versions evolve independently at that boundary; this is good architecture, not tech debt. Flag false positives like this to reject without guilt.
- **Ripley sequencing after a metadata pass: avoid churn.** PR #57 just landed (description tightening). Adding structural refactors immediately after metadata PRs creates thrashing. Sequence the DTO consolidation as a separate, lower-priority PR. Do not bundle multiple refactors into a single spike unless they directly conflict.

---

- **Adapter pattern depth:** The SDK-to-interface adapter layer is shallow (3 classes: HelixApiClient adapters, CachingHelixApiClient DTOs). Adding fields is mechanical: interface → adapter → cache DTO. The cache DTO must match the interface for JSON round-trip fidelity.
- **IWorkItemSummary is intentionally thin:** Only `Name` was exposed. ExitCode lived exclusively on `IWorkItemDetails`. This forced an N+1 fetch pattern in `GetJobStatusAsync` that the new SDK fields can eliminate.
- **Nullability as version signal:** When extending interfaces with SDK-backed fields from beta packages, always make new properties nullable. Null means "server didn't provide it" — safe fallback to existing code paths.
- **Brady values:** Bullet-heavy proposals, concrete before/after call counts, explicit file lists for handoff, and clear rollout recommendations (patch vs minor). Keep it tight.
- **AzDO timeline has two orthogonal axes:** `state` (pending/inProgress/completed) is lifecycle; `result` (succeeded/failed/canceled/skipped/abandoned/succeededWithIssues) is outcome, only meaningful when state=completed. `issues` is a third orthogonal dimension. Never conflate these in a single filter without clearly documenting the mapping.
- **MCP filter design: prefer named presets over orthogonal params for LLM consumers.** Presets encode valid combinations and avoid invalid cross-product states. Shape A (richer enum) beats Shape B (two params) when the axes have dependent validity.
- **Two naming conventions coexist in MCP tools:** Pass-through params (values sent to AzDO API) use AzDO-verbatim casing (`inProgress`, `Stage`). Preset params (values interpreted by our logic) use friendly lowercase (`failed`, `running`). Don't mix conventions within a param type. Use silent aliases (NormalizeFilter) to bridge when an LLM carries a pass-through name into a preset param.
- **Key files for AzDO filter logic:** MCP layer: `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` (3 tools). Service layer: `src/HelixTool.Core/AzDO/AzdoService.cs` (SearchTimelineAsync, GetHelixJobsAsync). Model: `src/HelixTool.Core/AzDO/AzdoModels.cs` (AzdoTimelineRecord has State, Result, Issues).
- **MCP description audit cadence (2026-05-22):** Approved PR #57 (3c4728c) — second description-tightening pass, ~3 months after the 2026-02-13 original. 8 of 25 tools had drifted back above the rubric threshold. Pattern: audit quarterly, or after any batch of new tools lands. Key rubric rules: lead with verb, ≤25 words, push filter enums and defaults to param-level `[Description]`, keep cross-references (e.g. `azdo_log`, `azdo_search_timeline`) as the one exception. Domain knowledge (repo lists, org names like `devdiv`) belongs in `CiKnowledgeService` response content, not always-loaded tool metadata.
- **Test strategy for description metadata:** Pin routing-intent phrases (e.g. "Repo-specific CI guidance") in description-string tests, but assert domain-specific discoverability details (e.g. `devdiv`) against `CiKnowledgeService` response content where they actually live. Avoids fragile coupling between test assertions and metadata that should stay compact.


# Summary (archived 1 older sections)

See history-archive.md for complete history.
- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## 2026-05-22 — Slop Audit Triage Complete, PR #58 Merged

**Status:** Triaged Ash findings → Ripley implementation → PR #58 merged to main

Recommended DTO consolidation PR (Finding #1 verdict: FIX) successfully implemented and merged. Deferred exception handler extraction (Finding #2) to Q3 2026 pending exception test coverage improvement. Rejected findings #3–6 per architectural and risk analysis.

Slop audit pattern established. Ready for future audits.

## Learnings — Issue #59 Triage (2026-05-22)

- **Measurement-first audits beat word-counting for metadata work.** PR #57's headline claimed 29→14 and 45→20 word reductions, suggesting large agent-visible savings. Actual `tools/list` token-count audit (gpt-4o tokenizer) showed −164 tokens (−2.0%) across the release range. The real wins came from PR #51's `LimitedResults<T>` wrapper (zero description changes, −257 tokens) and PR #57's text trims (−110), but PR #56's filter-preset schema additions reclaimed +113. Description word-count metrics are insufficient; always measure the full JSON footprint.

- **Tokenizer-measurement methodology is reusable and should be standing practice.** The measurement snippet (bash + Python gpt-4o tokenizer) is repeatable and gives conclusive evidence. Overhead is ~30 minutes (snapshot before, land PR, snapshot after, diff). Adopted as checklist item for future description PRs: measure, record delta in PR body, store methodology in `.squad/skills/mcp-description-rubric.md` Appendix.

- **Schema growth from one PR can cancel text trims from another.** PR #56's filter presets added inputSchema properties; PR #57's text trims were contemporaneous. The two effects netted to a small total savings. Lesson: audit in measurement-first order, not PR order. Track all tools' token contributions across a release range, not just the tools targeted by any single PR.

- **Generalization patterns (like `LimitedResults<T>`) are high-leverage opportunities.** PR #51's wrapper migration was unplanned discovery: two tools got −164 and −93 tokens with zero description changes. Lewing identified three candidate tools for the same treatment. Worth a sub-question (Ripley audit) before committing: does the pattern generalize safely? Expected upside if it does: −150–250 additional tokens (3× the entire PR #57 word-trim win).

- **Defer diffuse follow-ups; do targeted ones immediately.** Issue #59 proposed three follow-ups: (1) Trim `azdo_search_timeline` params (+69 from PR #56) = targeted, measurable, ACCEPT. (2) "Top-5 tool 20% trim pass" = broad, unscoped, no evidence = DEFER pending Ash's investigation audit in 2–3 weeks. (3) `LimitedResults<T>` extension = targeted pattern, unknown safety = ACCEPT Phase 1 (Ripley audit), conditional on Phase 1 result.

- **Evidence-first decision sequencing avoids orphaned work.** Lewing provided per-tool token deltas with full release-range context. This made it possible to confidently accept or defer each follow-up without speculation. For Follow-up #2 (top-5 audit), the issue provided the math but no field audit. Decision: defer to investigation spike, then convert to a follow-up issue with per-tool targets. This prevents Ripley from guessing which tools to trim.

**Decision document:** `.squad/decisions/inbox/dallas-issue59-triage-2026-05-22.md`

---

### Retriage — New Structural Data (2026-05-22, 14:50Z)

blazor-playground/copilot-skills measurement agent provided field-level breakdown: **outputSchema 37% (3,032 tokens), inputSchema 33.5% (2,742 tokens), annotations 9.7% (791 tokens), description 7.2% (588 tokens)**. This inverts priority: PR #57 attacked the smallest contributor. Original triage was undersized; revised verdicts below.

**Key revelation:** PR #51's `LimitedResults<T>` win was outputSchema shrinkage by type simplification, not a one-off. Top-10 tools carry 58% of cost; many are list-returning. Pattern likely generalizes to −300–600 tokens (Phase 1 audit identifies candidates). Combined with a DTO-trim pass on top-10 tools (outputSchema + inputSchema reduction), ceiling jumps from original −80–300 to −940–2,080 tokens (11–25% of total cost).

**Revised verdicts:**
- **Follow-up 1 (param trim):** ACCEPT (elevated scope). Not just descriptions—inputSchema enum descriptions are the lever. Filter-preset enum on azdo_timeline alone = 88 tokens. Estimate: −40–80 tokens across affected tools.
- **Follow-up 2 (LimitedResults<T>):** ACCEPT (now headline lever). Expand to full top-10 outputSchema audit. Phase 1: Ripley identifies LimitedResults<T> + other simplification candidates. Phase 2: implement. Estimate: −300–600 tokens.
- **NEW Follow-up 4 (outputSchema DTO-trim on top-10):** **ACCEPT, highest priority.** Tighten DTO shapes, trim per-property descriptions, adopt $ref for shared schemas. Author ceiling: −500–1,500 tokens (50% of outputSchema cost, 10× PR #57 win). Phase 1: Ripley audits top-10, identifies targets. Phase 2: implement. Risk: DTO shape changes affect downstream; mitigate via measurement + wire-format compatibility.
- **NEW Follow-up 5 (annotations audit):** ACCEPT. Redundant annotations (e.g., openWorldHint=true matching SDK defaults) waste bytes. Estimate: −50–300 tokens. Quick 3–5 hour audit + cleanup.
- **NEW Follow-up 6 (drop primitive outputSchema):** ACCEPT. Tools returning primitives (e.g., helix_auth_status → boolean) don't need auto-generated schema. Estimate: −50–100 tokens. Zero risk. 2 hours.
- **Deferred (original broad top-5 audit):** Superseded. Refined follow-ups 4, 5, 6 provide field-level leverage points + sequenced Phase 1 audits.

**Execution plan:** Ripley runs Phase 1 audits on Follow-ups 2, 4, 5, 6 in parallel (weeks 1–2). Phase 2 implementation on highest-ROI targets next (Follow-up 4, then Follow-up 2). Follow-up 1 lands after Follow-up 2 Phase 1. Total scope: 20–32 hours over 3 weeks. Expected recovery: −940–2,080 tokens.

**Lesson learned:** Field-level structural breakdown is essential for prioritization. Metrics-first (word counts, boilerplate counts) miss the actual cost drivers. Next MCP audit should report token distribution by field. Dallas will update the standing rubric to mandate field-breakdown measurement.

**Retriage document:** `.squad/decisions/inbox/dallas-issue59-retriage-2026-05-22.md`

---

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

