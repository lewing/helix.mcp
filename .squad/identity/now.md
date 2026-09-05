---
updated_at: 2026-09-04T17:15:34.685-05:00
focus_area: PR 132 external review corrections
status: implementation_committed_external_review
investigation: Addressing external review feedback on the committed PR 132 implementation
---

# What We're Focused On

**Status:** The implementation is committed on open PR #132, final internal review is approved, and external review feedback is being addressed. It is not merged or externally approved.

**Implemented:** Ripley restored Helix submission metadata projection, optional output fields, original leg names, lineage annotation without count filtering, monitor warning/tree parsing, and issue-only fallback rows. Lambert added coverage. Kane corrected tool guidance and the changelog.

**Final design correction:** Successful Helix discovery remains the primary strategy even when every job outcome is unknown: `FailedHelixJobs = 0`, `OutcomeUnknownHelixJobs = N`, and completion timestamps do not infer outcomes. One timeline enrichment request returns every issue-bearing Task as build-level `timelineIssues`, without topology, task-name, or GUID gates. Omitted `timelineIssues` means enrichment was unavailable and `Note` warns; `[]` means enrichment ran and found no issues.

**Resilience:** Ash narrowly handles expected JSON/offline enrichment failures (`JsonException`, `InvalidOperationException`) while propagating caller cancellation; regression coverage is included.

**Validation:** Final targeted suite: 117 passed with `DOTNET_ROLL_FORWARD=Major` (local runtime 11, tests target 10). An earlier pre-correction full suite passed 1,943 with 8 existing skips; it is not final-suite validation. Parent confirmed the final diff check was clean.

**Deferred:** Superseded-job count filtering and parallel discovery scans.

**Session directive:** Sol/no Opus applies only to this session.
