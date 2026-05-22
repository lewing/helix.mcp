---
name: "mcp-filter-api-design"
description: "Design filter/enum parameters for MCP tools consumed by LLM clients, balancing expressiveness with discoverability."
domain: "mcp-server-design"
confidence: "medium"
source: "earned"
---

## Context

Use this when adding or redesigning filter parameters on MCP tools — especially when the underlying data model has multiple orthogonal filtering dimensions (e.g., state vs result vs issues). The goal is a filter API that LLMs can use correctly without domain expertise.

## Patterns

### Prefer named presets over orthogonal parameters
When the underlying data has N independent axes, do NOT expose N separate parameters if:
- Some axis combinations are invalid (e.g., `state=pending` + `result=failed` is contradictory)
- The valid combinations map to a small set of common queries
- The consumer is an LLM that picks from `AllowedValues`, not a human reading docs

Instead, use a single enum parameter whose values are named presets, each mapping to a documented (axis₁, axis₂, …) predicate.

### Make preset names self-describing
Preset names ARE the documentation for LLM consumers. `'running'` beats `'in-progress'`; `'failed'` beats `'non-succeeded'`. Match the mental model of the caller, not the API's enum names.

### Additive expansion is free; breaking changes are expensive
Adding new enum values to `AllowedValues` is backward-compatible. Renaming parameters, changing defaults, or splitting one parameter into two is a schema break that forces all callers to update.

### Keep defaults stable
If `'failed'` is the default and existing callers rely on it, don't change the default even if a new preset would be "better." Add the new preset and let callers opt in.

### Extract shared predicate logic
When multiple tools use the same filter axis, extract the predicate into a shared helper (e.g., `MatchesFilter(record, filterValue)`) rather than duplicating switch logic. The helper becomes the single source of truth for what each preset means.

### Parent/context walking follows filtering
If a filter narrows records to a subset, the parent-walk logic (including ancestor records for context) should apply uniformly to all non-`'all'` presets. Don't make parent walking preset-specific unless there's a strong reason.

## Examples

- AzDO timeline records have `state` (pending/inProgress/completed) and `result` (succeeded/failed/…). Rather than exposing `state` and `result` as two params, use presets: `'failed'`, `'running'`, `'pending'`, `'incomplete'`, `'issues'`, `'all'`.
- A Helix work item has `State` (Running/Passed/Failed/…). Rather than a free-text state filter, use presets: `'failed'` (default), `'all'`, `'running'`.

## Anti-Patterns

- Exposing raw API enum values as two separate filter params and expecting the LLM to know which combinations are valid
- Using a single `filter` param that conflates orthogonal concepts without documenting the predicate mapping (the original `'failed' | 'all'` bug)
- Adding a `'strict'` or `'advanced'` mode that changes how other params are interpreted
- Defaulting to `'all'` when most callers want a narrowed view — forces every caller to add a filter
