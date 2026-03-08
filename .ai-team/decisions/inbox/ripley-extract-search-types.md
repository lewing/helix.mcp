# Decision: Extract search types to top-level namespace

**By:** Ripley  
**Date:** 2025-07-19  
**Context:** azdo_search_log implementation

## What

Extracted `LogMatch`, `LogSearchResult`, and `FileContentSearchResult` record types from `HelixService` (nested records) to top-level types in the `HelixTool.Core` namespace, in the new `TextSearchHelper.cs` file.

## Why

`SearchLines()` needed to be shared between Helix and AzDO search operations. Since `AzdoService` lives in `HelixTool.Core.AzDO`, the return types couldn't remain nested inside `HelixService`. Making them top-level is cleaner and follows the existing pattern where shared types are accessible across sub-namespaces.

## Impact

- Any code referencing `HelixService.LogSearchResult` etc. needs to drop the `HelixService.` prefix (no existing code used it that way)
- Tests already had `TextSearchHelperTests.cs` anticipating this extraction
- No breaking changes to MCP tool DTOs (those use their own `SearchMatch`/`SearchLogResult` types in `HelixTool.Mcp.Tools`)
