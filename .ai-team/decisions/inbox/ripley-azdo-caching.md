# Decision: AzDO Caching Strategy

**By:** Ripley
**Date:** 2026-03-07
**Status:** Implemented

## Context

CachingAzdoApiClient decorator added for IAzdoApiClient, following the CachingHelixApiClient pattern.

## Decisions

1. **Cache key namespace:** All AzDO cache keys use `azdo:` prefix (e.g., `azdo:{org}:{project}:build:{id}`). Build completion state tracked under `azdo-build:` prefix in the shared `cache_job_state` table.

2. **Dynamic TTL by build status:**
   - Completed builds: 4h (stable data)
   - In-progress builds: 15s (rapidly changing)
   - Timelines: never cached while build is running (changes constantly), 4h when completed
   - Logs & changes: 4h (immutable once written)
   - Build lists: 30s (browsing queries)
   - Test runs/results: 1h (stable shortly after build)

3. **No DTO layer needed:** AzDO model types are `sealed record` with `[JsonPropertyName]` — directly serializable/deserializable via `System.Text.Json`. Unlike Helix's interface-based types that require private DTO records for JSON round-tripping.

4. **Reuses ICacheStore.IsJobCompletedAsync:** The `jobId` parameter is just a string key, not Helix-specific. AzDO uses composite keys like `azdo-build:{org}:{project}:{buildId}`.

## Impact

- **Lambert:** Tests needed for CachingAzdoApiClient — 7 methods, each with cache-hit/miss/disabled paths. Dynamic TTL for GetBuildAsync (completed vs in-progress). Timeline never-cache-while-running path.
- **Dallas:** No architectural changes — reuses existing ICacheStore/CacheOptions/CacheSecurity infrastructure.
