---
date: 2026-10-02T14:10:00-05:00
author: Dallas
status: approved
topic: PR 1 paging/cache-key gate review
---

# Verdict

APPROVE Ripley's PR 1 paging/cache-key compatibility implementation.

Reviewed HEAD `8415f88f8cec9cdde39aaa4bd6a8792f0a6c2ef9` against `origin/main` and Dallas's PR 1 design. The implementation matches the intentionally breaking CLI contract for the updated AzDO list commands, preserves MCP output shape, and implements complete-key-first eval replay with legacy capped-key fallback for capped MCP calls.

## Required fixes

None.

## Compatibility decision

The default CLI `--json` shape change from bare arrays to the `{ ok, results, returned, total, offset, limit, complete, truncated, next, cache, note }` envelope is intentional and was part of the accepted PR 1 design. Default capped output exiting `2` when `truncated=true` is also intentional fail-closed behavior for scripts and matches the evidence-plan incomplete-output precedent. Existing callers that want old success semantics should pass `--allow-truncated`, and callers that need full success should pass `--all`.

MCP tool output shape did not change: `azdo_changes`, `azdo_test_runs`, `azdo_test_results`, `azdo_artifacts`, and `azdo_test_attachments` still return the existing `LimitedResults<T>`/string shapes, while the cache layer now lets capped MCP eval calls slice complete collector keys first and fall back to legacy capped keys when complete keys are absent.

## Gate notes

- Eval-mode slicing is complete-key-first, deterministic in provider order, and preserves full-set totals for CLI envelopes; final non-zero-offset pages remain `truncated` by design because they are not a full selected set.
- Cache key versioning is scoped to new complete/window keys (`changes:v2`, `testruns:v3`, `testresults:v3`, `testattachments:v2`) with legacy capped-key fallback for MCP/default capped reads. Existing schema-v1/v2 snapshots remain readable; NUL metadata encoding is forward-compatible and leaves existing non-encoded rows readable.
- Negative acquisition replay still happens after positive complete/window/legacy probes and before true `not_in_snapshot`, preserving #153 precedence.
- The full-log cache probe is isolated behind the internal `IAzdoCachedBuildLogReader` seam instead of leaking cache operations into `IAzdoApiClient`; offline clients remain pure miss stubs.
- `--all`, `--offset`, `--limit`/`--top` alias rejection and truncation exit behavior are validated by Lambert's PR tests. Local targeted test invocation compiled but could not run because this machine has only `Microsoft.NETCore.App 11.0.0-preview.6` and lacks the `10.0.0` runtime; I relied on the reported full-suite result plus static gate review.
