---
date: 2026-10-02
author: Dallas
status: proposed
topic: Scanner collection and paging design
---

# Decision: scanner collection is cache population plus manifest

Use the existing `hlx` cache/snapshot as Vitek's deterministic bundle. Add two coupled features in order:

1. **Paging/cache-key PR:** add CLI `--all`/`--offset`/`--limit`/`--allow-truncated`, consistent JSON list envelopes, exit `2` for produced-but-incomplete output, `azdo log --full`, and complete-list cache keys that eval-mode capped MCP calls can slice from. Keep MCP defaults capped; no MCP cursors in v1.
2. **Collector PR:** add `hlx collect azdo-build <build-id-or-url>` to drive evidence-plan acquisition, AzDO logs/tests, and `helixFailures[].suggestedFetches[]`, populate cache through the existing live service/client paths, write `hlx-collect-manifest.json`, and optionally chain `snapshot export`.

The critical cache rule: a complete collector key must satisfy a capped offline MCP call. For list data whose current keys include `top`, add complete keys such as `testresults:v3:{runId}:{outcomes}:all`; in eval mode, capped reads probe the complete key first and slice in memory before falling back to exact/legacy bounded keys. Logs, timeline, artifacts, Helix work-item details/files/console already have full-resource keys that capped agent calls can reuse.

`hlx collect azdo-build` should write a versioned manifest with every attempted fetch: provider, operation, resource, cache key, paging metadata, bytes/hash, timestamps, retry count, outcome (`ok`, `cached`, `recorded_failure`, `failed`, `skipped`), `AcquisitionError` when applicable, and skip details. Overall exits remain `0` complete, `2` produced-but-incomplete, `1` hard error.

The offline agent launch path is docs-first for v1:

```bash
HLX_EVAL_SNAPSHOT=/path/to/snapshot hlx mcp
```

Only add `hlx mcp --snapshot <path>` if Vitek's harness cannot set env vars for stdio MCP servers.

Full artifact: `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/collect-and-paging-design.md`.

## Open questions

1. Can the scanner harness set `HLX_EVAL_SNAPSHOT` for MCP stdio servers?
2. Should default collection include all test outcomes or failed-only test results?
3. Should default log collection include all logs or only failed/issue/monitor logs?
4. Are incomplete snapshots uploadable by default with exit `2`, or only with `--allow-incomplete`?
5. Are artifact/binlog/dump byte downloads required in v1, and what size caps are acceptable?
