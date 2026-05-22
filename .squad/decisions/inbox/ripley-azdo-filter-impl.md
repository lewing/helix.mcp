# Decision: AzDO filter helper placement and Helix placeholder shape

**Date:** 2026-05-22  
**Author:** Ripley  
**Status:** Proposed implementation note

## Context

Dallas's approved AzDO timeline filter preset design requires one shared normalization/predicate implementation across `HelixTool.Core` and `HelixTool.Mcp.Tools`, plus state-based `azdo_helix_jobs` results even when no issue text has exposed a Helix job GUID yet.

## Decisions

1. **Shared helper placement:** place `NormalizeFilter`, `MatchesFilter`, validation helpers, and canonical filter metadata on `AzdoService` as `public static` members.
   - Reason: the MCP timeline tool applies the same predicate in `HelixTool.Mcp.Tools`, but that assembly does not currently have friend access to `HelixTool.Core` internals.
   - This avoids adding a new `InternalsVisibleTo` coupling or duplicating the filter switch in a second assembly.

2. **State-only Helix task encoding:** when a `running` / `pending` / `incomplete` Helix task matches the state preset but yields no extractable Helix job IDs, return the existing `HelixJobFromBuild` shape with `HelixJobId = ""`.
   - Reason: this preserves the current record contract while surfacing the meaningful fact that the Helix submission task exists and is still active/incomplete.
   - No model/schema change is required, and callers can distinguish these rows by the empty job ID.

3. **`failed` compatibility in `azdo_helix_jobs`:** keep the existing final `Result != succeeded` trimming after extraction so current `failed` output stays unchanged, even though task selection now flows through the shared predicate helpers.

## Consequences

- One canonical filter implementation now drives timeline filtering, timeline search filtering, and Helix job task selection.
- Silent aliases remain schema-invisible while still being accepted operationally.
- `azdo_helix_jobs` can now surface active Helix submission tasks before issue messages expose a GUID.
