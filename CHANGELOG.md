# Changelog

All notable changes to helix.mcp are documented here. Versions follow [semantic versioning](https://semver.org/).

For releases prior to v0.7.6, see the [GitHub Releases page](https://github.com/lewing/helix.mcp/releases).

---

## [v0.8.0] — 2026-06-24

### Strict parameter rejection with "Did you mean?" hints (#81, PRs #83/#84/#87)

MCP tools now reject unknown or mistyped parameter names immediately, with a structured error message that includes:
- A **"Did you mean: X?"** suggestion when the unknown name is close to a known parameter (Levenshtein distance ≤ 6)
- The **full list of allowed parameter names** so callers can self-correct without consulting docs

Previously, unknown params were silently dropped (the SDK discarded them before invoking the tool). This change turns silent data-loss failures into immediate, actionable errors.

Example response when an LLM passes a hallucinated param name:
```
Unknown parameter 'minFinishTime' for tool 'azdo_builds'.
Did you mean: minTime?
Allowed parameters: org, project, top, branch, prNumber, definitionId, status, minTime, maxTime, queryOrder
```

### AzDO parameter plumbing — `minTime`/`maxTime`, `outcomes`, `top` (#78)

Three parameters that were accepted by MCP tools but silently not forwarded to the REST API are now plumbed end-to-end:

| Tool | Parameter | Was | Now |
|------|-----------|-----|-----|
| `azdo_builds` | `minTime`, `maxTime`, `queryOrder` | accepted, dropped | forwarded to AzDO REST API |
| `azdo_test_results` | `outcomes` | hardcoded to `Failed` | forwarded; configurable (default still `Failed`) |
| `azdo_test_attachments` | `top` | accepted, dropped | forwarded to AzDO REST API |

All defaults preserve prior behavior — no breaking changes for existing callers.

`minTime`/`maxTime` filter the time field selected by `queryOrder`. For example, `queryOrder=finishTimeDescending` means `minTime`/`maxTime` filter by finish time. Default `queryOrder` is `queueTimeDescending`.

### Alias support (#75)

Tools that accept a build identifier resolve common parameter name aliases automatically before strict validation runs:

| Alias | Canonical | Tools affected |
|-------|-----------|----------------|
| `buildId` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `build_id` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `buildUrl` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `result` | `resultFilter` | `azdo_search_timeline` |

### AzDO filter normalization (#82, PR #85)

Internal refactor — centralized AzDO filter normalization (trim, case-fold, default-collapse). User-visible side effects:

- `queryOrder` values are now sent lowercase in REST URLs (e.g. `finishtimedescending` instead of `finishTimeDescending`). AzDO treats this as case-insensitive; behavior is unchanged.
- Cache key format changed. One-time invalidation on deploy; self-heals within the normal TTL (≤ 30s for in-progress builds, ≤ 4h for completed builds).

### Dependency updates

- `ModelContextProtocol` 1.3.0 → 1.4.0
- `SQLitePCLRaw` pinned to 3.x for [CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)

---

## [v0.7.6] — 2026-05-29

- **User-Agent identifier** (PR #73, @akoeplinger): All outbound HTTP traffic from hlx now carries `User-Agent: helix.mcp/{version}` and a custom `X-Helix-Mcp-Tool: helix.mcp` header on AzDO and Helix clients, enabling arcade-services to distinguish hlx traffic from other callers.
- **Work item status bucketing fix** (PR #71, backport of #70): `GetWorkItemDetailAsync` now applies `IsCompleted` bucketing correctly — in-progress and waiting work items are no longer miscounted as failed in detailed work item queries.

---

For releases prior to v0.7.6, see the [GitHub Releases page](https://github.com/lewing/helix.mcp/releases).
