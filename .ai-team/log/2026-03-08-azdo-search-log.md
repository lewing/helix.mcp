# Session: 2026-03-08-azdo-search-log

**Requested by:** Larry Ewing

## Summary

Implemented `azdo_search_log` — the P0 AzDO search capability identified in prior gap analysis. Extracted `TextSearchHelper` from `HelixService`, added `AzdoService.SearchBuildLogAsync`, exposed as MCP tool and CLI command.

## Work Completed

- **Ripley** implemented azdo_search_log:
  - Extracted `TextSearchHelper` from `HelixService.SearchLines()` to shared utility
  - Added `AzdoService.SearchBuildLogAsync` for AzDO build log search
  - Created MCP tool `azdo_search_log` matching `helix_search_log` interface
  - Added CLI command for `azdo_search_log`
- **Lambert** wrote 41 tests (20 TextSearchHelper, 21 AzdoSearchLog) — all pass
- **Coordinator** fixed validation order bug (pattern check before `IsFileSearchDisabled`)

## Decisions

- Extracted search types (`LogMatch`, `LogSearchResult`, `FileContentSearchResult`) from `HelixService` nested records to top-level types in `HelixTool.Core` namespace
- `TextSearchHelper.cs` shared between Helix and AzDO search operations

## PR

- PR #10: https://github.com/lewing/helix.mcp/pull/10

## Branch

`feature/azdo-search-log`
