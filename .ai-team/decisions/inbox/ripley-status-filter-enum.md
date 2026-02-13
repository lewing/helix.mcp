# Decision: Status filter refactored from boolean to enum-style string

**By:** Ripley
**Date:** 2025-07-23
**Requested by:** Larry Ewing

## Context

The `status` command/tool previously used a boolean `--all`/`includePassed` flag to toggle showing passed work items. This was limited — you could only see "failed only" or "everything". There was no way to see "passed only".

## Decision

Replaced the boolean with a three-value string `filter` parameter:
- `"failed"` (default) — shows only failed items (backward-compatible default)
- `"passed"` — shows only passed items
- `"all"` — shows both failed and passed items

Validation throws `ArgumentException` for invalid values. Comparison uses `StringComparison.OrdinalIgnoreCase`.

## Impact

- **MCP tool (`hlx_status`):** `bool includePassed` → `string filter = "failed"`. When filter is `"passed"`, `failed` is null in output. When `"failed"`, `passed` is null. When `"all"`, both populated.
- **CLI command (`status`):** `bool all` → `[Argument] string filter = "failed"`. Second positional arg. Usage: `hlx status <jobId> [failed|passed|all]`.
- **Breaking change:** Existing callers using `--all` or `includePassed=true` must update to `filter="all"`.
- **Tests:** Lambert needs to update tests for the new parameter signature and filter logic.
