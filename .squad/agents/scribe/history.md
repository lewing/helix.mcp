# Scribe — History

## Learnings

### 2026-02-11: Session logging and decision merging
- Logged session `2026-02-11-p0-foundation` (design review ceremony)
- Merged 2 inbox decisions: Ash requirements extraction, Dallas P0 design review (D1–D10)
- Propagated team updates to Ripley, Lambert, Dallas, Kane
- No deduplication needed — merged decisions are complementary to existing entries

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry


📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash

### 2026-03-10: Review-fix session bookkeeping
- Logged `2026-03-10-review-fix-session` for Larry Ewing
- Merged 3 inbox decisions into `decisions.md` (README overhaul, strict HttpClient review validation, path-boundary/transport review fixes)
- Propagated a consolidated 2026-03-10 team update to Ash, Dallas, Kane, Lambert, and Ripley
- Checked agent history sizes; no summarization/archive action needed

### 2026-03-10: Ash knowledgebase refresh bookkeeping
- Logged `2026-03-10-ash-kb-refresh` for Larry Ewing
- Merged 2 inbox decisions into `decisions.md` (Ash knowledgebase-refresh note, living-document directive)
- Propagated a consolidated 2026-03-10 knowledgebase-refresh update to Ash, Dallas, Kane, Lambert, and Ripley
- Checked agent history sizes; no summarization/archive action needed

### 2026-03-10: Discoverability pass bookkeeping
- Logged `2026-03-10-discoverability-pass` for Larry Ewing
- Merged 3 inbox decisions into `decisions.md` (Dallas discoverability review, Kane discoverability docs note, Ripley fallback-routing note)
- Propagated a consolidated 2026-03-10 discoverability-routing update to Dallas, Kane, Lambert, and Ripley histories
- Checked agent history sizes; no summarization/archive action needed

### 2026-03-13: Limits/truncation session bookkeeping
- Logged `2026-03-13-bump-limits-truncation` for Larry Ewing
- Merged 7 inbox decisions into `decisions.md`, consolidating Lambert's README/resources and idempotent-annotation notes into one shared block
- Propagated a consolidated 2026-03-13 team update to Ash, Dallas, Kane, Lambert, and Ripley
- Summarized Dallas, Lambert, and Ripley histories into archive/context after the size check

### 2026-03-13: Auth remaining bookkeeping
- Logged `2026-03-13-auth-remaining` for Larry Ewing
- Merged 1 inbox decision into `decisions.md`, folding the PR #28 auth-source/cache-isolation/auth-status note into the consolidated 2026-03-13 AzDO auth decision and removing overlapping duplicate decision blocks
- Propagated the merged auth decision to Ash, Dallas, Kane, and Lambert histories
- Summarized Ripley history after the size check

### 2026-08-26: Snapshot export hardening batch merge
- Logged `2026-08-26T1716-scribe-snapshot-hardening-batch` orchestration entry
- Merged 2 inbox decisions into `decisions.md`: Dallas design approval and multi-agent review gate (Ripley/Kane accepted, Lambert/Parker rejected/locked, Bishop approved)
- Removed merged inbox files: `dallas-snapshot-hardening-design.md`, `dallas-snapshot-hardening-review.md`
- Updated `identity/now.md` focus area to snapshot export hardening completion state
- Checked agent history sizes: Dallas 564, Ripley 512, Lambert 351, Kane 118 — no summarization/archive action needed
- Noted escalation requirement: recruit independent .NET concurrency/filesystem test specialist for future revisions

### 2026-10-02: PR #153 snapshot misses and review fixes merge
- Logged `2026-10-02T1905Z-pr153-review-and-snapshot-misses` session summary with all four agent participation records
- Merged 3 inbox decisions into `decisions.md`: Windows-safe filenames (hard rule), snapshot misses design, Dallas PR #153 review gates (rounds 1-2)
- Removed merged inbox files: `copilot-windows-safe-filenames.md`, `dallas-snapshot-misses.md`, `dallas-review-153-round2.md`
- Created 4 orchestration-log entries (Windows-safe UTC naming): Ripley review fixes, Dallas gate reviews, Lambert snapshot-misses implementation, Kane documentation
- ⚠️ **Windows-safe filename rule now hardened in Scribe charter:** All .squad file names must use compact UTC (`2026-10-02T1905Z-{slug}.md`), never colons or `<>"|?*\`. This rule is enforced in all .squad operations going forward.
- Checked agent history sizes: Kane 15,886 bytes (≥15360 threshold); summarization pending
- No new deduplication needed — all three merged decisions are distinct topics

### 2026-10-03: Post-release session history summarization

**Threshold:** 15,360 bytes (HARD GATE)
**Files exceeding threshold:** kane (20K), ripley (17K), dallas (30K), lambert (34K)

Archived prior session entries from each agent into existing history-archive.md files:
- **Kane:** archived 3 sessions (pre-10-02 documentation/help work); retained 2026-10-02 R3 doc-accuracy findings and Dallas-gate learning notes
- **Ripley:** archived 6 sessions (pre-10-02 implementation/post-merge work); retained 2026-10-02 v0.11.0 release cut and PR156 deviations
- **Dallas:** archived 12 sessions (pre-10-02 review gates); retained 2026-10-02 final release approval and R1–R3 blocker summaries
- **Lambert:** archived 8 sessions (pre-10-02 test/refactor work); retained 2026-10-02 lifecycle test coverage, hard-error regression cases, and green builds

**No new deduplication action required.** Archive operations and history retention are append-only per Scribe charter.
