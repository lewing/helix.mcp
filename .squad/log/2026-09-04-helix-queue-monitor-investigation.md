# Session Log: Helix Queue Monitor Investigation

**Date:** 2026-09-04  
**Investigation:** Multi-agent analysis of queue-monitor topology adoption and compatibility  
**Session ID:** 999c29b4-dc4a-46d1-9306-e0fc3b38ab21  
**Status:** Investigation complete; implementation roadmap staged

## Task Summary

Three background agents completed parallel investigations into Helix queue-monitor support:
- **Ash (Product Analyst):** Requirements and adoption survey
- **Ripley (Backend Dev):** Local implementation audit
- **Dallas (Lead):** Architectural review and roadmap adjudication

## Outcome

**Verdict:** APPROVE WITH CORRECTIONS

- **Investigation finding:** Queue-monitor support is a projection bug, not a capabilities gap. The Helix API client already fetches rich `JobSummary` data but discards it with `.Select(j => j.Name)`.
- **Fixes approved:** Four concrete defects fixed (D1–D4), one enhancement (D6), one documentation correction (Kane owns D4).
- **Deferred:** Two items gated on evidence (D5a now, D5b later).
- **Rejected:** Seven new-tool proposals; existing tools compose to same result.

## Key Architectural Finding

`HelixApiClient.ListJobNamesByBuildAsync` response already contains:
- `QueueId` (for platform/config mapping)
- `Properties` (System.PhaseName, System.JobDisplayName, System.JobName, System.JobAttempt, PreviousHelixJobName, BuildId)
- `Created`, `Finished` (for job status)
- `InitialWorkItemCount` (for result aggregation)
- `FailureReason` (for pass/fail inference)

Restoring the projection costs zero additional HTTP calls and unblocks both legacy and queue-monitor workflows.

## Implementation Roadmap

### Phase 1: Core Fixes (Now)
- D1: Widen Helix-side projection (owner: Ripley)
- D2: Emit build-wide errors (owner: Ripley)
- D3: Parse monitor message format (owner: Ripley)
- D4: Correct CiKnowledgeService docs (owner: Kane)
- D5a: Expose lineage metadata (owner: Ripley)
- D6: Parallelize artifact discovery (owner: Ripley)

### Phase 2: Gated Evidence (Later)
- D5b: Filter superseded jobs (owner: Ripley, blocked on D5a evidence)

## Related Materials

- `.squad/decisions.md` — Full decision entry with success criteria
- `.squad/orchestration-log/2026-09-04-*` — Per-agent logs
- `.squad/decisions/inbox/` — (archived to decisions.md)
