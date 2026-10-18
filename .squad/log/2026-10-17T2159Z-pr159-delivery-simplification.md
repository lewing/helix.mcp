# Session: PR #159 Delivery Simplification (2026-10-17T21:59Z)

## Summary

Merged PR #159 (Timeline triage view, selectors, paging and access-first delivery / P0-1) after 5 Dallas rejection cycles. Consolidated inbox decisions into decisions.md; archived oversized decisions; logged PR #159 completion and handoff to P0-3 (artifact bytes).

## PR #159 Milestone: Timeline P0-1 Delivery

**Status:** Merged (commit e66688a)
**Reviewer:** Dallas (5 rejection rounds)
**Implementation:** Ripley, Lambert (tests/contracts), Kane (docs)

### Delivery Store Simplification

Major architecture change implemented across final rounds:
- **Deleted** separate private runtime security subsystem: `UnixDirectorySecurity.cs`, `WindowsDirectorySecurity.cs`, `CrossProcessPartitionLock.cs`
- **Simplified** to shared hlx cache root: `<GetEffectiveCacheRoot()>/delivery/<credential-partition>/`
- **Eval/offline mode:** `<normal-user-base-cache-root>/eval-delivery/<SHA256(normalized absolute snapshot path)>/<partition>/`
- **External fallback:** When overlapping snapshot detected, use SQLite external-managed partition discovery instead of private directory ACLs
- **Windows & Unix:** Plain directory creation (0700 on Unix, 0600 for new files); no native ACL/owner APIs or subprocess probes

### Final Copilot Fixes

Two critical fixes addressed in late Copilot review (commits 4ad90c5, 11cbd4d):

1. **Credential Identity Resolution** (4ad90c5): Fixed evidence credential fallback logic to correctly resolve auth-partition identity when HTTP evidence read encounters credential mismatch. Ensures partitioned evidence storage aligns with actual credential context.

2. **Continuation View Shaping** (11cbd4d): Fixed timeline continuation preview field handling during multi-page delivery. Preserves continuation metadata correctly across advance-page transitions; prevents view-shaping code from discarding page-continuation hints.

### Dallas Rejection Cycle (5 Rounds)

Dallas's five rejection rounds addressed:
1. **Round 1 (ce0631b):** Budget floor enforcement (8,192–16,384 bytes), dual requested/effective reporting, complete delivery routing
2. **Round 2 (9620719):** SQLite WAL partition discovery, production JSON serializer alignment, exit-code logical completeness
3. **Round 3 (7ce53b4):** Nested symlink resolution recursion, single-record completeness bypass
4. **Round 4 (ed4e9d6):** Root-cause completeness refactor; unified ShapeTimeline return (Result, LogicallyComplete)
5. **Round 5 (989b306):** Issue-window completeness preference fix; simplify to `result.Complete` directly

**Test Status:** 2600 pass / 0 fail / 9 skip / 2609 total (no warnings)
**Live Validation:** Real builds 1621192/1600801; HTTP evidence round-trip; default sizes ≤12,288 bytes

## Inbox Decisions Merged

All 5 inbox files consolidated into decisions.md:
- `copilot-directive-20261003T0235Z.md` — User directive (access-not-restriction principle)
- `dallas-audit-p0.md` — P0 audit architecture decision
- `dallas-review-timeline-p01.md` — Timeline gate review findings
- `ripley-pr159-review-deviations.md` — Copilot review findings and fixes
- `ripley-timeline-deviations.md` — Implementation deviations from Dallas design

**Inbox cleaned up.** Decisions.md split: 72K (current) + 60K archive.

## Next PR: P0-3 Artifact Bytes

**Scope:** Complete P0-3 (artifact and attachment bytes) implementation per Dallas audit design section 4. Build on P0-1's delivery infrastructure. Estimated work: size filtering, streaming download counts, metadata completeness validation.

## Files Changed

- `.squad/decisions.md` — appended inbox files; trimmed to 72K (archive older at 60K)
- `.squad/decisions-archive.md` — created archive of older decisions
- `.squad/decisions/inbox/` — cleaned (5 files deleted)
- `.squad/log/2026-10-17T2159Z-pr159-delivery-simplification.md` — this log (created)

## Session Context

- **Branch:** lewing-p0-3-bytes (from origin/main, PR #159 merged)
- **Current datetime:** 2026-10-17T21:59Z
- **Merge baseline:** origin/main at 6eb3905 (v0.9.1 release)
- **PR #159 commits:** 15 commits from a6e24e9 through e66688a
- **No production code changes in this session.** Only .squad/ metadata management.
