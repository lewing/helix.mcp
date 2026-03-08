# Session: AzDO Context-Limiting Defaults

**Date:** 2026-03-08
**Requested by:** Larry Ewing
**Agent:** Ripley (Backend Dev)

## Summary

Ripley added context-limiting defaults to all AzDO MCP tools:
- `azdo_log`: `tailLines=500`
- `azdo_timeline`: `filter="failed"` (new parameter, client-side filtering with parent chain)
- `azdo_test_results`: `maxResults=200`
- `azdo_changes`: `top=20`
- `azdo_test_runs`: `top=50`

All parameters remain nullable/overridable. Defaults live in MCP tool method signatures.

## Result

All 667 tests pass.
