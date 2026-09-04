---
updated_at: 2026-09-04T14:50:50.914-05:00
focus_area: Helix queue-monitor compatibility roadmap accepted with corrections
status: investigation_complete
investigation: Multi-agent analysis of queue-monitor topology adoption and implementation defects
---

# What We're Focused On

**Investigation:** Helix queue-monitor compatibility roadmap.

**Status:** Investigation complete; implementation not yet started.

**Outcome:** Three agents (Ash, Ripley, Dallas) converged on identical finding: queue-monitor support is a projection bug, not a capabilities gap. The Helix API client already fetches `JobSummary` metadata but discards it with `.Select(j => j.Name)`. Restoring the projection costs zero additional HTTP calls.

**Verdict:** APPROVE WITH CORRECTIONS
- **Approved NOW (D1–D4, D6):** Four concrete defects fixed, one enhancement (parallelize discovery), one documentation correction
- **Approved LATER (D5a/D5b):** Expose lineage metadata now; filter superseded jobs gated on evidence
- **Rejected:** Seven new-tool proposals (existing tools compose to same capability)

**Implementation Roadmap**
- **Phase 1 (now):** D1 (widen projection), D2 (emit build-wide errors), D3 (parse monitor format), D4 (doc fix), D5a (expose lineage), D6 (parallelize discovery)
- **Phase 2 (later):** D5b (filter superseded, gated on D5a evidence)

**Ownership:** Ripley owns D1–D3, D5a, D5b, D6; Kane owns D4.

**Material Correction:** Ripley's initial P4 dedup algorithm (group by attempt, max) was incorrect. Arcade uses lineage-leaf rule (`PreviousHelixJobName` tracking), which is the corrected form; Ripley retains ownership with correction. Sequencing insight: annotate (D5a) before filtering (D5b).

**Key Architectural Finding:** `HelixApiClient.ListJobNamesByBuildAsync` response already contains `QueueId`, `Properties` (PhaseName, JobDisplayName, JobAttempt, PreviousHelixJobName, BuildId), `Created`, `Finished`, `InitialWorkItemCount`, `FailureReason`. Zero additional HTTP cost to restore.

**Reference:** `.squad/decisions.md` (merged queue-monitor decision), `.squad/orchestration-log/` (per-agent logs), `.squad/log/2026-09-04-helix-queue-monitor-investigation.md` (session summary).
