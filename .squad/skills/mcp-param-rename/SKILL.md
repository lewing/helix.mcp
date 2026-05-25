---
name: "mcp-param-rename"
description: "Rename MCP tool parameters when the wire schema drifted from semantic intent."
domain: "mcp-server-design"
confidence: "medium"
source: "earned"
---

## Context
Use this when an MCP tool parameter name is part of the caller-visible JSON schema and no longer matches the semantic contract.

## Patterns
- Audit all MCP tools with the same semantic input, not just the initially reported tool.
- Prefer one canonical parameter name across the tool family; for AzDO build identifiers that accept either numeric IDs or full URLs, use `buildIdOrUrl`.
- Make an explicit wire-compat decision: hard rename for clean schema alignment, or alias only when known consumers require old names.
- Update generated/help guidance and tests that show MCP call shapes.
- Manually verify `tools/list` exposes the new parameter, a call with the new parameter succeeds, and the old parameter fails with an explicit `isError` response rather than silent null.

## Anti-Patterns
- Renaming only local variables while leaving the MCP method parameter unchanged.
- Adding aliases by default; aliases preserve drift unless there is a known compatibility need.
- Mixing CLI argument names with MCP schema names. CLI positional arguments can keep short names while MCP JSON parameters use the canonical wire name.
