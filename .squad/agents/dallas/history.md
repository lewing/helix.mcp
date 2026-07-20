# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, and strict-mode architecture.

**Recent Focus (2026-06-24 through 2026-07-20):**
- Refined blanket "flatten all outputSchema" → tiered approach (FLATTEN 10 / KEEP 3 / LEAVE 12)
- Authorized PR #78 (azdo param plumbing: minTime/maxTime/queryOrder, top, outcomes)
- Designed CallToolFilter layer for unknown-param rejection (Issue #81) vs SDK-layer strict mode
- Triaged Issue #81/#82 sequencing (correctness before cleanup)

---

## 2026-07-20: Tiered outputSchema Recommendation (REFINED)

**Challenge:** User questioned blanket "flatten all 20 tools" — which genuinely warrant richer schema?

**Method:** Read result DTOs; assess each tool on: (1) output chained downstream, (2) shape ambiguous, (3) drives decision.

**Recommendation:**
- **FLATTEN 10:** helix_status, azdo_build, helix_parse_uploaded_trx, azdo_search_timeline, helix_batch_status, helix_work_item, helix_search, helix_find_files, helix_files, helix_download (5,541 bytes, 18% savings)
- **KEEP 3:** azdo_timeline (log.id disambiguation), azdo_helix_jobs (HelixJobId bridge), azdo_build_analysis (known/unmatched discrimination) (2,212 bytes retained)
- **LEAVE 12:** Already minimal or degenerate (68-byte LimitedResults, string-only returns, azdo_search_log)
- **Optional enrich:** 2 field descriptions (~90 bytes)

**Net savings:** ~5,450 bytes (18% vs. 28% blanket). Retains extraction-critical guidance on 3 chaining junctions.

**Decision filed:** `.squad/decisions/decisions.md` (merged from inbox 2026-07-20)

---

## 2026-06-24: Param Plumbing & Alias Coercion

### PR #78 — Three AzDO Bugs Fixed
1. **azdo_builds:** minTime/maxTime/queryOrder missing from filter + URL
2. **azdo_test_attachments:** top param not forwarded to REST URL
3. **azdo_test_results:** outcomes hardcoded (no override)

Four Copilot review rounds; 14 new tests added; all 1337 tests pass.

### PR #75 — Numeric Alias Coercion (Gap fix)
**Finding:** Numeric `build_id` values (JSON numbers) fail string parameter binding.
**Fix:** Implement `CoerceToStringElement()` in CallToolFilter. Ripley + Lambert executed.

---

## 2026-06-01: Parameter Alias Layer Decision

**Problem:** Agents passed `build_id`/`buildUrl` instead of canonical `buildIdOrUrl`.

**Layer choice:** CallToolFilter in McpServerOptionsExtensions (pre-binding), not per-tool attributes or method signatures.

**Rationale:** Key-normalization problem, not value problem. MCP SDK 1.3.0 has no tool-level alias surface.

**Pattern:** Flat global alias map (OrdinalIgnoreCase); insertion order significant; combine with binding-error filter.

**Calibration:** Drift telemetry must be built in from day one (Debug-level logging).

---

## Issue #81/#82 Framing (2026-06-24)

**Sequencing heuristic:** User-visible correctness (#81 Stage A) before architectural cleanup (#82).

**Stage A/B coexistence:** Both UnmappedMemberHandling.Disallow (SDK layer) and CallToolFilter "did you mean" (pipeline layer) can coexist; Stage B makes Stage A redundant for our tools. Document the choice in PR, don't silently remove.

**Pre-work scope rule:** When enabling enforcement gate, grep telemetry/issue history for known-tolerated variants before flipping switch. Example: `result` → `resultFilter` alias landed in Stage A PR, not as follow-up.

---

## Previous Work Archive

See `.squad/agents/dallas/history-archive-2026-06-01.md` for:
- Issue #61 (silent MCP failures, exception centralization)
- PR #66 (external contributor review, null-coercion pattern)
- Issue #74 (schema measurement, 28.26 KB baseline, conditional NO)
- MCP 1.4.0 bump safety analysis

---

## Key Decisions Referenced

- **Issue #74:** Conditional NO on active trimming (28.26 KB, <1% session budget). Lever 1 available (~8.9 KB zero-risk).
- **Issue #81/#82:** Stage A (strict unknown-param) correct; Stage A/B can coexist; centralize normalization (cleanup after correctness).
- **PR #78:** Param plumbing shipped; auth + cache remain; 14 new tests added.
- **Lever 1 (Tiered outputSchema):** Ready for go/no-go; ~5,450 bytes savings with extraction guidance preserved.

---

## 2026-07-20: Progressive Disclosure Analysis

### Finding: SDK supports dynamic ToolCollection (v1.4.0)
- `ToolCollection` property on server supports Add/Remove at runtime
- SDK auto-sends `notifications/tools/list_changed` to clients
- Mechanism exists in protocol and SDK — feasible technically

### Finding: No natural trigger in MCP protocol
- No client gesture / context hint flows server-side mid-session
- Server has no visibility into what the model is doing
- Only triggers available: (a) tool A was called → reveal tool B, (b) timer/config

### Tool Bucketing (25 tools, ~29 KB total)
**CORE (always needed, 8 tools, ~12.8 KB):**
azdo_build (1531), azdo_builds (2103), azdo_timeline (2099), azdo_helix_jobs (1425), azdo_search_log (2027), helix_status (1771), azdo_build_analysis (est ~1200), helix_ci_guide (479)

**WORKFLOW-GATED (need build/job in hand, 11 tools, ~12.5 KB):**
azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_search_timeline, azdo_artifacts, helix_logs, helix_files, helix_work_item, helix_search, helix_find_files

**NICHE (narrow scenarios, 6 tools, ~4.6 KB):**
helix_parse_uploaded_trx (1703, self-identifies "Niche"), helix_batch_status, helix_download, azdo_test_attachments, helix_auth_status (330), azdo_auth_status (330)

### Mechanism Feasibility
1. **Dynamic list + list_changed:** FEASIBLE in SDK 1.4.0. But trigger problem is real — after azdo_builds/helix_status called, reveal drill-down tools. Complexity: moderate (state machine per session).
2. **Meta/gateway tool:** FEASIBLE but anti-pattern for models. Worse tool-use accuracy. Not recommended.
3. **Resources vs Tools:** CiKnowledgeResource ALREADY exists alongside helix_ci_guide tool. Resources are NOT in tools/list — they're in resources/list. Moving guide to resource-only saves 479 bytes but loses tool-call discoverability. Marginal.
4. **Config-based profiles:** FEASIBLE, simplest. Operator picks "minimal" (8 core) or "full" (25). Static at startup. No runtime complexity.

### Verdict
Progressive disclosure is NOT worth pursuing as a primary lever here.
- Maximum reclaim from hiding NICHE bucket: ~4.6 KB (16% of total)
- Maximum reclaim from hiding WORKFLOW-GATED: ~12.5 KB (43%)
- But WORKFLOW-GATED tools are what models need to SEE to plan multi-step investigations — hiding them degrades planning quality
- The trigger problem means you'd reveal tools AFTER the model already decided it needed them (chicken-and-egg)
- flatten-10/keep-3 outputSchema lever saves ~5.5 KB with ZERO ergonomic cost and no runtime complexity
- Combined: flatten + description trim could reach ~8-9 KB savings without progressive disclosure

### Recommendation
1. Ship flatten-10/keep-3 (outputSchema tiering) — already designed, zero-risk
2. If further cuts needed: config-based "minimal" profile (option 4) for operators who only need AzDO OR only Helix
3. Do NOT pursue runtime dynamic disclosure — complexity/trigger problem outweighs marginal byte gain
4. Progressive disclosure and outputSchema flatten compose (orthogonal) but overlap in motivation — flatten is strictly better ROI
