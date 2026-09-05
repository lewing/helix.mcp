# Orchestration Log: Dallas — Helix Queue Monitor Design Review

**Date:** 2026-09-04T14:50:50.914-05:00
**Agent:** Dallas (Lead/Reviewer)
**Task:** Adjudicate proposals; produce ranked roadmap
**Status:** COMPLETED

## Summary

Reviewed Ash and Ripley's parallel investigations into queue-monitor compatibility. Both correctly identified the problem as a projection bug, not a capabilities gap. Verified all major claims against local code and arcade source. Produced ranked roadmap approving six items (four fixes + one enhancement + one doc correction), deferring two items to a later gate with evidence, rejecting all new-tool proposals.

## Key Findings

- **Architectural insight:** `ListJobNamesByBuildAsync` discards `JobSummary` metadata with `.Select(j => j.Name)`, including `QueueId`, `Properties`, job status — all needed for queue-monitor support.
- **Ripley P4 error confirmed:** Arcade uses lineage-leaf rule, not group-by-attempt.
- **All new-tool proposals rejected:** Existing tools (`azdo_timeline`, `azdo_search_timeline`, `azdo_search_log`) already compose to the same result.
- **D5 split:** Separate annotation (now) from filtering (later, gated on evidence).
- **Compatibility:** All approved items work for both legacy and queue-monitor topologies.

## Decisions Produced

- **Approved NOW:** D1 (widen projection), D2 (emit build-wide errors), D3 (parse monitor format), D4 (doc fix), D6 (parallelize discovery)
- **Approved LATER:** D5a (expose lineage, now), D5b (filter superseded, gated on evidence)
- **Rejected:** Seven new-tool proposals; Ripley P4 as specified

## Coordination

No lockout on Ash or Ripley. Future proposals for rejected tools must include real failing transcripts.

## Outcome

**Status:** VERDICT COMPLETE — ACCEPT WITH CORRECTIONS
- Investigation approved as evidence
- Ranked roadmap approved with ranked decisions
- D1–D6 ready for implementation planning
- D5b gated on D5a evidence from live build
