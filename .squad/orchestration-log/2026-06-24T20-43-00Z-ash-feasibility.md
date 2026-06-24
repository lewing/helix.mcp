# Orchestration Log: Ash-Feasibility (MCP 1.4.0 Strict Unknown-Param Rejection Study)

**Date:** 2026-06-24T20:43:00Z  
**Agent:** ash-feasibility (background)  
**Task:** Feasibility study: strict unknown-parameter rejection at MCP boundary  
**Issue:** #81 (filed after study completion)  
**Status:** Complete

## Execution Summary

Evaluated technical approaches for catching unknown parameters at MCP tool call site before SDK binder silently drops them.

## Findings Delivered

### Option A: UnmappedMemberHandling.Disallow (Stopgap)
- Available in M.E.AI 10.5.2 (already pinned by MCP 1.3.0 and 1.4.0)
- Enables strict binding: unknown JSON object properties cause explicit JsonException
- Applied globally or per-tool
- **Trade-off:** No "did you mean" suggestions; raw JSON errors

### Option B: CallToolFilter with Custom Validation (Recommended)
- Validate arguments object against expected parameter names
- Emit McpException with helpful "did you mean X?" suggestions
- Hook into existing request-filter infrastructure
- **Advantage:** User-friendly error messages + structured observability

### Technical Findings
- MCP 1.3.0 vs 1.4.0: UnmappedMemberHandling is equally available in both (verified via ModelContextProtocol.JsonRpc NuGet source)
- CallToolFilter API confirmed on 1.3.0; 1.4.0 bump is safe
- One breaking case flagged: existing code using `result:'failed'` on `azdo_search_timeline` would fail with strict validation (needs alias)

## Recommendation

**Stage A (immediate):** Apply UnmappedMemberHandling.Disallow as catch-all protection  
**Stage B (future):** Implement CallToolFilter with "did you mean" suggestions for high-value tools

## Status

✅ Study complete. Follow-up issue #81 filed for implementation queue.
