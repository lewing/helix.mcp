
**2026-05-21 10:33Z:** Ash audit complete. 3 decisions pending for your review: service-layer validation, azdo_auth_status sync vs async, structured error codes. See decisions.md.

**2026-05-21 17:58Z:** Completed design proposal for surfacing WorkItemSummary.ExitCode + ConsoleOutputUri. Filed to `.squad/decisions/inbox/dallas-surface-workitem-fields.md`. Key call: optimize GetJobStatusAsync (skip detail fetch for passed items), defer ConsoleOutputUri streaming.

## Learnings

- **Adapter pattern depth:** The SDK-to-interface adapter layer is shallow (3 classes: HelixApiClient adapters, CachingHelixApiClient DTOs). Adding fields is mechanical: interface → adapter → cache DTO. The cache DTO must match the interface for JSON round-trip fidelity.
- **IWorkItemSummary is intentionally thin:** Only `Name` was exposed. ExitCode lived exclusively on `IWorkItemDetails`. This forced an N+1 fetch pattern in `GetJobStatusAsync` that the new SDK fields can eliminate.
- **Nullability as version signal:** When extending interfaces with SDK-backed fields from beta packages, always make new properties nullable. Null means "server didn't provide it" — safe fallback to existing code paths.
- **Brady values:** Bullet-heavy proposals, concrete before/after call counts, explicit file lists for handoff, and clear rollout recommendations (patch vs minor). Keep it tight.
- **AzDO timeline has two orthogonal axes:** `state` (pending/inProgress/completed) is lifecycle; `result` (succeeded/failed/canceled/skipped/abandoned/succeededWithIssues) is outcome, only meaningful when state=completed. `issues` is a third orthogonal dimension. Never conflate these in a single filter without clearly documenting the mapping.
- **MCP filter design: prefer named presets over orthogonal params for LLM consumers.** Presets encode valid combinations and avoid invalid cross-product states. Shape A (richer enum) beats Shape B (two params) when the axes have dependent validity.
- **Two naming conventions coexist in MCP tools:** Pass-through params (values sent to AzDO API) use AzDO-verbatim casing (`inProgress`, `Stage`). Preset params (values interpreted by our logic) use friendly lowercase (`failed`, `running`). Don't mix conventions within a param type. Use silent aliases (NormalizeFilter) to bridge when an LLM carries a pass-through name into a preset param.
- **Key files for AzDO filter logic:** MCP layer: `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` (3 tools). Service layer: `src/HelixTool.Core/AzDO/AzdoService.cs` (SearchTimelineAsync, GetHelixJobsAsync). Model: `src/HelixTool.Core/AzDO/AzdoModels.cs` (AzdoTimelineRecord has State, Result, Issues).


# Summary (archived 1 older sections)

See history-archive.md for complete history.
