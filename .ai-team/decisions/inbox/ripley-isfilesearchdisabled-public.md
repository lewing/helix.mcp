### 2025-07-18: Promoted IsFileSearchDisabled to public visibility

**By:** Ripley
**What:** Changed `HelixService.IsFileSearchDisabled` from `internal static` to `public static` as part of the Mcp.Tools extraction.
**Why:** HelixMcpTools moved to a separate assembly (`HelixTool.Mcp.Tools`) and references this property. Making it internal with InternalsVisibleTo would couple Core to the new project. Public is consistent with `MatchesPattern` and `IsTestResultFile` which are already public statics on HelixService used by MCP tools.
