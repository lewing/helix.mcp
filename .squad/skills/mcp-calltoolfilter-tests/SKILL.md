---
name: "mcp-calltoolfilter-tests"
description: "Test MCP CallToolFilter behavior with real CallToolRequestParams and McpServerTool invocation."
domain: "mcp-testing"
confidence: "medium"
source: "earned"
---

## Context
Use this when validating `McpServerOptions.Filters.Request.CallToolFilters`, especially filters that mutate inbound `CallToolRequestParams.Arguments` before SDK parameter binding.

## Pattern
- Unit-test the filter by wrapping a capture handler and asserting the request's argument dictionary after the filter runs.
- For binding behavior, wrap `McpServerTool.InvokeAsync` created from the real attributed tool method; this exercises SDK binding instead of a helper-only path.
- Construct `RequestContext<CallToolRequestParams>` with a real `CallToolRequestParams { Name = toolName, Arguments = ... }` and JSON-backed `JsonElement` values via `JsonSerializer.SerializeToElement`.
- Assert both success routing and failure wrapping: missing required arguments should still become the repo's structured `McpException`.

## Example Scenarios
- Alias-only argument normalizes before binding.
- Canonical parameter wins when canonical and alias are both present.
- Multiple aliases have documented precedence.
- Non-standard alias casing is accepted when the alias table is case-insensitive.
