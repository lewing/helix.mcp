# Decision: Timeline search result types live in Core

**By:** Ripley
**Date:** 2025-07-19
**Context:** azdo_search_timeline implementation

## Decision

`TimelineSearchMatch` and `TimelineSearchResult` are defined in `HelixTool.Core.AzDO` (AzdoModels.cs), not in `McpToolResults.cs`. The MCP tool returns the Core types directly.

## Rationale

- Core can't reference Mcp.Tools (dependency direction: Mcp.Tools → Core, not reverse).
- Same pattern as `AzdoBuildSummary` — domain types in Core, MCP tools return them directly.
- `[JsonIgnore]` on `TimelineSearchMatch.Record` keeps MCP JSON flat while exposing the raw `AzdoTimelineRecord` for programmatic consumers and tests.

## Impact

- Future search-style features in Core should follow this pattern: define result DTOs with `[JsonPropertyName]` in Core, add `[JsonIgnore]` for any raw-data properties that shouldn't serialize.
- `McpToolResults.cs` is for MCP-specific wrapper types that don't map 1:1 to service returns (like `SearchBuildLogResult` which reshapes `LogSearchResult`).
