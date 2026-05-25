# Lambert MCP Exception-Coverage Baseline Audit — 2026-05-25

**Scope:** all `[McpServerTool]` methods in `src/HelixTool.Mcp.Tools/` (`AzdoMcpTools`, `HelixMcpTools`, `CiKnowledgeTool`).
**Standing rule:** Dallas verdict says every MCP tool method must have ≥1 unhappy-path test proving exceptions surface as structured MCP errors.
**Baseline note:** This matrix reflects `main` before Lambert's initial tests on `test/issue61-mcp-exception-coverage`.

## Summary

- **Tools audited:** 25
- **Direct MCP happy-path coverage:** 14/25 (56%)
- **Any direct MCP unhappy-path coverage:** 7/25 (28%)
- **High-quality service-exception MCP wrapping tests:** 2/25 (8%)
- **Direct coverage gap:** most AzDO service-facing tools have service-level tests but no MCP wrapper unhappy-path tests.
- **Bug B exhibit A:** `azdo_build_analysis` has good service happy-path coverage, but zero direct MCP tool tests and zero AggregateException/TaskCanceledException coverage.

## Coverage matrix

| Tool | Has happy-path test? | Has unhappy-path test? | Unhappy-path test quality |
|---|---:|---:|---|
| `azdo_build` | Yes (direct MCP) | No | Gap: no service exception / not-found MCP wrapper test. |
| `azdo_builds` | Yes (direct MCP) | No | Gap on baseline; initial Lambert wave adds `HttpRequestException` MCP wrapper test. |
| `azdo_timeline` | Yes (direct MCP) | Yes | Guard-only: invalid filter asserts `McpException` message; no service exception or timeout coverage. |
| `azdo_log` | Yes (direct MCP) | No | Gap: no log fetch exception wrapper test. |
| `azdo_changes` | Yes (direct MCP) | No | Gap: no changes fetch exception wrapper test. |
| `azdo_test_runs` | Yes (direct MCP) | No | Gap: no test-runs fetch exception wrapper test. |
| `azdo_test_results` | Yes (direct MCP) | No | Gap: no test-results fetch exception wrapper test. |
| `azdo_artifacts` | Yes (direct MCP) | Yes | Good for `ArgumentException`: invalid build id asserts `McpException` and message prefix. |
| `azdo_search_log` | No direct happy-path; service happy-path exists | Yes | Good for `ArgumentException`/config guard, but not network or timeout. |
| `azdo_search_timeline` | Service happy-path only | No direct MCP | Gap: service tests assert raw exceptions; no MCP wrapper test. |
| `azdo_test_attachments` | Yes (direct MCP) | No | Gap: no attachment fetch exception wrapper test. |
| `azdo_helix_jobs` | Service happy-path only | No direct MCP | Gap: high-value bridge tool has no MCP wrapper tests. |
| `azdo_build_analysis` | Service happy-path only | No direct MCP | Worst gap: Bug B repro surface; no AggregateException / TaskCanceledException MCP coverage. |
| `azdo_auth_status` | Token accessor service tests only | No direct MCP | Low-risk local/status tool; no direct result/error coverage. |
| `helix_status` | Yes (direct MCP) | Yes | Guard-only: invalid filter asserts `McpException`; no Helix/HTTP service exception wrapper test. |
| `helix_logs` | Yes (direct MCP) | No | Gap: no console-log fetch exception wrapper test. |
| `helix_files` | Yes (direct MCP) | Yes | Guard-only: missing work item asserts `McpException`; no service exception wrapper test. |
| `helix_download` | No direct happy-path | Yes | Semantic guard coverage: no files / no matching files asserts `McpException`; no download I/O exception wrapper test. |
| `helix_find_files` | Yes (direct MCP) | No | Gap: no find-files service exception wrapper test. |
| `helix_work_item` | No direct MCP | No direct MCP | Gap: no happy or unhappy MCP tests. |
| `helix_search` | Service happy-path only | No direct MCP | Gap: no MCP wrapper test for search exceptions/binary failures beyond service tests. |
| `helix_parse_uploaded_trx` | No direct happy-path | Yes | Good semantic error-surfacing tests for no result files/crash artifacts/missing work item; not network/timeout. |
| `helix_batch_status` | Yes (direct MCP) | No | Gap: high-traffic aggregation tool has no exception wrapper test. |
| `helix_auth_status` | No direct MCP | No | Low-risk local/status tool; no direct result/error coverage. |
| `helix_ci_guide` | Service happy-path + metadata tests only | No direct MCP | Low-risk after wrapper fix; no direct tool exception test. |

## Top 5 worst gaps

1. **`azdo_build_analysis`** — Bug B repro surface; no direct MCP tests, no AggregateException/TaskCanceledException coverage, uses `Task.WhenAll` under the tool boundary.
2. **`azdo_build`** — high-traffic lookup tool; happy path exists, but no direct not-found/network/aggregate wrapper test.
3. **`azdo_helix_jobs`** — high-value bridge from AzDO to Helix; service tests only, no MCP wrapper test.
4. **`azdo_search_timeline`** — common debugging surface; service tests only, raw service exceptions verified but no MCP structured-error test.
5. **`helix_batch_status`** — high-traffic aggregation tool; happy path exists, but no exception wrapper test for partial/multi-job service failures.

## Initial test-wave selection

Lambert's initial tests target representative exception families without attempting exhaustive rollout:

- `azdo_build_analysis`: skipped contract test for AggregateException surfaced from a mocked HTTP failure inside the `Task.WhenAll` boundary.
- `azdo_builds`: active `HttpRequestException` wrapper test; passes on current `main` and documents the structured message assertion style.
- `azdo_timeline`: skipped contract test for `TaskCanceledException` timeout behavior pending Ripley's centralized handler.

## Follow-up rollout recommendation

Prioritize one direct MCP exception test per remaining service-facing tool, with the first pass focused on high-traffic AzDO tools (`azdo_build`, `azdo_build_analysis`, `azdo_helix_jobs`, `azdo_search_timeline`, `azdo_test_results`). For auth/status tools, direct happy-path coverage can be added after the service-facing exception floor is closed.
