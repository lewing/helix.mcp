---
name: "azdo-rest-param-surface-audit"
description: "Audit MCP/CLI tool parameter surface against underlying REST API capabilities to find silently-dropped params."
domain: "azdo-integration"
confidence: "high"
source: "earned"
---

## Context

Use when adding a new AzDO REST endpoint wrapper, or when auditing existing
tools for missing capabilities. The symptom of a missing param is always the
same: the parameter is accepted by the MCP binding layer (or CLI), but the
value is silently ignored — indistinguishable from a successful call with
the filter applied.

## The Audit

For each AzDO MCP tool, compare:
1. The AzDO REST API reference for that endpoint (query parameters available)
2. The tool method signature (what MCP/CLI accepts)
3. The URL construction in `AzdoApiClient` (what actually reaches the server)

If a REST param exists but is absent from either the method or the URL
construction, it is a bug.

## Known Gaps (fixed 2026-06-24)

| Tool | REST param | Root cause |
|---|---|---|
| `azdo_builds` | `minTime`, `maxTime` | Not in `AzdoBuildFilter`, not forwarded to URL |
| `azdo_builds` | `queryOrder` | Hardcoded to `queueTimeDescending`, not exposed |
| `azdo_test_attachments` | `$top` | Present in signature, not appended to URL |
| `azdo_test_results` | `outcomes` | Hardcoded to `Failed`, not exposed |

## AzDO Time Range Semantics

`minTime` and `maxTime` are not named after a specific field — the field
they filter is determined by `queryOrder`:
- `queueTimeDescending` → filters by queue time
- `startTimeDescending` → filters by start time
- `finishTimeDescending` → filters by finish time

Always document this coupling in the parameter description so callers
know to pair them correctly.

## Patterns

- **Check `AzdoApiClient` URL construction** against the REST reference for
  every param accepted by the method signature.
- **Check `AzdoBuildFilter`** properties against `ListBuildsAsync` query params.
- **Check cache keys** (`HashFilter`, per-endpoint key strings) to ensure every
  discriminating parameter is included. Missing a param → stale cache hit.
- **Add a test** that calls the method with the new param set and asserts the
  param appears in the captured URL (via `FakeHttpMessageHandler.LastRequest`).

## Anti-Patterns

- Accepting `top` / `outcomes` / `queryOrder` in the method signature but
  constructing the URL before the param (classic "accepted but never used" bug).
- Hardcoding REST defaults (e.g., `outcomes=Failed`) in URL strings — use
  `param ?? "Failed"` so the caller can override.
- Cache keys that omit new params → different queries return cached results
  from the first query's filter set.

## Whitespace Normalization (PR #78 learnings)

For **optional string params with a server-side default**, always use
`IsNullOrWhiteSpace` + `Trim()`, not `IsNullOrEmpty`:

```csharp
// AzdoApiClient — correct
var outcomesParam = string.IsNullOrWhiteSpace(outcomes) ? "Failed" : outcomes.Trim();

// CachingAzdoApiClient — normalize once, use in both key and inner call
var normalizedOutcomes = string.IsNullOrWhiteSpace(outcomes) ? null : outcomes.Trim();
var key = BuildCacheKey(..., $"...:{normalizedOutcomes ?? "Failed"}");
var result = await _inner.GetTestResultsAsync(..., normalizedOutcomes, ct);
```

Rationale:
- `""` and `"   "` from a CLI or MCP caller should fall back to the default, not produce
  `outcomes=` or `outcomes=%20%20%20` in the URL (a confusing 400 from AzDO).
- Without normalization, null / "" / "   " produce three distinct cache keys for
  semantically-identical requests, causing stale-cache bugs.
- Use `null` as the canonical "use default" value in internal layers; the API client
  resolves null → "Failed" as close to the URL as possible.

For **validation params** (e.g., `queryOrder`), mirror MCP validation in the CLI too:
```csharp
queryOrder = AzdoService.NormalizeQueryOrder(queryOrder);  // trims, null-if-empty
if (!AzdoService.IsValidQueryOrder(queryOrder))
{
    Console.Error.WriteLine(AzdoService.GetInvalidQueryOrderMessage(queryOrder!));
    return;
}
```
Don't rely on the MCP path to protect against bad CLI input — both entry points must validate.
