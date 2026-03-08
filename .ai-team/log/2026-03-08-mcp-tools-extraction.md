# Session: 2026-03-08-mcp-tools-extraction

**Date:** 2026-03-08
**Requested by:** Larry Ewing
**Agent:** Ripley

## Summary

Ripley extracted MCP tools (`HelixMcpTools`, `AzdoMcpTools`, `McpToolResults`) from `HelixTool.Core` into a new `HelixTool.Mcp.Tools` class library.

- PR #7 opened: "refactor: Extract MCP tools to HelixTool.Mcp.Tools class library"
- Build passes; 752/753 tests pass (1 pre-existing flaky test)
- Replaces stale PR #3 approach
