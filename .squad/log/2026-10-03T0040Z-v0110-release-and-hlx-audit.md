# Session: v0.11.0 release and hlx usage audit — 2026-10-02 to 2026-10-03

## Overview

Post-merge review cycle completing #153–#158 (snapshot hardening, paging, collection, post-merge fixes, audit synthesis, release v0.11.0).

**Branch:** lewing-audit-p0 (fresh from origin/main = v0.11.0)
**Date range:** 2026-10-02T12:00:00-05:00 → 2026-10-03T00:40:00-05:00
**Manifest commits:** 78b7297 (Scribe baseline) → current

## Agents and contributions

| Agent | PR/Session | Role | Status | Notes |
|-------|-----------|------|--------|-------|
| Ripley | #154, #155, #156, #157 | Implementation: paging/cache keys, post-merge fixes, collector PRs, independent review fixes | Approved | Locked from revision at gates 2–3; no new commits this session |
| Lambert | #156, #157 | Tests: paging surface, post-merge/independent fixes, evidence-plan/issue-list test additions | Approved | Revision gates #156 round 2 (text/doc); eligible for R1–R3 fixes |
| Dallas | 18 findings, 4 gates | Review/gate: post-merge (#154/#155/18 findings), PR1/paging gate, PR2/collector gate/re-review, evidence-plan gate, post-merge review (#156 volume gate, final pre-release) | Mixed | Rejection (R1–R3), approval (post-merge round 2, PR1, PR2 final, evidence-plan, audit summary) |
| Kane | Docs | Documentation/help/changelog: README, CLI reference, post-merge findings, release notes, collector docs/help text, evidence-plan scope notes | Approved | Eligible for R3 doc fixes |
| Ash | Audit | hlx usage audit synthesis: 372 findings from 199 sessions, offline gap analysis, product roadmap | Proposed | Documented in decisions, audit artifact in session files |

## Key decisions

1. **Bundle = offline snapshot:** Larry's directive reframes scanner bundle as existing offline snapshot model (not separate format). Evidence flow: collection → cache → snapshot export → eval-mode MCP/CLI replay.

2. **GPT alternation directive:** Alternate gpt-6.1-sol and hydrafusion for GPT picks in coordinator fallback chains; experiment tracking noted. Claude/Gemini unaffected.

3. **PR1 paging/cache-key compatibility:** Breaking CLI change for list commands (envelope/truncation semantics). MCP unchanged (capped output). Eval complete-key-first with legacy fallback. Versioned complete keys (changes:v2, testruns:v3, testresults:v3, testattachments:v2).

4. **PR2 collector contract:** `hlx collect azdo-build` with manifest/paging/completeness semantics. v1: selective Helix file skips (no bytes yet), deterministic offline replay without live token for public artifacts. Manifest records provider/operation/cache-key/paging/bytes/outcome for every fetch.

5. **Post-merge blocker (unbounded paging + NUL ambiguity):** Fixed in two rounds: AzDO continuation capping (1000 pages), SQLite v2 metadata envelope (unambiguous NUL encoding). v1 snapshots remain compatible with migration warning.

6. **Evidence-plan Helix awareness:** Timeline monitor failures → helixFailures[] with stable incomplete codes, suggested fetches, paging. Deterministic completeness: failed/canceled monitor with parsed/untruncated failures stays complete; unparseable/truncated monitor incomplete with `[monitor_unparseable]` / `[helix_failures_truncated]`.

7. **Pre-release blocker (3 gates → R1–R3):** R1: required evidence evicted by optional downloads (needs fix). R2: Helix job-discovery leaks Azure SDK exception (needs fix). R3: release docs inaccurate (needs correction).

## Merged inbox decisions

**Files merged (12 inbox files → decisions.md):**
- ash-hlx-usage-audit.md
- copilot-directive-20261002T2121Z.md
- dallas-collect-and-paging.md
- dallas-review-collect-pr2.md
- dallas-review-paging-pr1.md
- dallas-review-postmerge.md
- dallas-review-pr156-fixes.md
- ripley-collect-deviations.md
- ripley-indep-review-deviations.md
- ripley-paging-deviations.md
- ripley-postmerge-deviations.md
- ripley-pr156-deviations.md

**Merged content summary:**
- User directive: GPT-6.1-sol / hydrafusion alternation experiment
- PR1 validation: no deviations from design; auth token workaround for test APIs
- PR2 implementation deviations: v1 Helix file skips, test-API auth requirement
- Independent review (findings 1–8): transport/Helix exception fixes, export isolation, outcome coverage, eviction budget clamping, TTL-bypass verification, test-seam pattern documentation
- PR156 deviations: test-scope stall (live all-scope incomplete), NotRunnable outcome exclusion, snapshot validation form
- Post-merge review gates (rounds 1–2): unbounded paging fix, SQLite NUL encoding v2 fix, release notes
- PR156 comprehensive gate: volume/attachment-selection/progress fixes accepted; R1–R3 blockers identified
- hlx usage audit: 372 findings → 5 PR groups (build discovery, artifacts, timeline, cross-build, Helix bulk)

**Deduplication:** No duplicates found; all merged decisions are complementary topics.

## Release status

**v0.11.0:** Tagged and published (commit e713995, NuGet + container). Release body set from changelog notes. Missing v0.10.3 CHANGELOG section added by Kane.

**Known blockers for next tag:** R1 (eviction), R2 (job-list exception), R3 (docs).

## Cross-team updates required

- **Ripley:** New assignment needed for R1–R2 implementation (eviction fix, Helix job-list classification, Helix client refactor if needed)
- **Lambert:** Eligible to implement R1–R2 fixes as revision to Ripley's artifact
- **Kane:** R3 documentation updates (CHANGELOG, CLI reference, help text alignment)
- **Dallas:** Final gate post-R1–R3 before next tag
- **Ash:** hlx usage audit complete; roadmap prioritization to Larry

## Orchestration

| Component | Date | Coordinator | Status |
|-----------|------|-----------|--------|
| Post-merge 18-finding gate | 2026-10-02T12:04–12:20 | Dallas | 2 rounds, approved |
| Paging/cache-key PR gate | 2026-10-02T14:10 | Dallas | 1 round, approved |
| Collector PR gate | 2026-10-02T14:45–15:45 | Dallas | 3 rounds (1 Ripley lock, 1 Lambert revision, 1 Kane text), approved |
| Evidence-plan review | 2026-10-02T11:29–12:04 | Lambert/Dallas | Coverage: parser/matcher/service/CLI/MCP, 2 implementation bugs fixed |
| Volume/performance gate | 2026-10-02T16:00–16:20 | Dallas | 1 acceptance + 3 R-blockers identified |
| Pre-release final gate | 2026-10-02T16:20→ | Dallas | Rejects HEAD on R1–R3; no tag yet |
| hlx audit synthesis | 2026-10-02→ | Ash | 199 sessions, 372 findings, 5 group roadmap |
| Scribe merge | 2026-10-03T00:40 | Scribe | 12 inbox files, 0 duplicates, commit pending |

## Open work

- **R1 (eviction fix):** Ripley or Lambert, required before next tag
- **R2 (Helix exception):** Ripley or Lambert, required before next tag
- **R3 (docs):** Kane, required before next tag
- **Post-fix validation:** Dallas, final gate before next tag
