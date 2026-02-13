# Decision: Cache Auth Isolation (Security Fix)

**Date:** 2026-02-13
**Author:** Ripley (Backend Dev)
**Requested by:** Larry Ewing

## Problem

Shared SQLite cache is a data leak. Multiple `hlx` instances with different HELIX_ACCESS_TOKEN values share the same `cache.db` and `artifacts/` directory. An unauthenticated instance can read cached data from private Helix jobs fetched by an authenticated instance.

## Solution

Separate SQLite databases and artifact directories per auth context, derived from HELIX_ACCESS_TOKEN:

- **No token** → `{base}/public/cache.db` + `{base}/public/artifacts/`
- **Token present** → `{base}/cache-{hash}/cache.db` + `{base}/cache-{hash}/artifacts/`

Where `{hash}` = first 8 hex chars of SHA256 of the token (lowercase, deterministic).

## Files Changed

- `CacheOptions.cs`: Added `AuthTokenHash` property, `GetBaseCacheRoot()` method, `ComputeTokenHash()` static helper. `GetEffectiveCacheRoot()` now subdivides by auth context.
- `Program.cs`: Both DI containers (CLI + MCP stdio) pass `CacheOptions.ComputeTokenHash(HELIX_ACCESS_TOKEN)` as `AuthTokenHash`.
- `Program.cs`: `cache clear` wipes ALL auth context subdirectories (public + all cache-{hash} dirs). `cache status` shows current auth context info.
- `CacheOptionsTests.cs`: Updated 7 tests to expect `/public` subdirectory for unauthenticated context.
- `llmstxt`: Updated caching docs to describe auth isolation.

## Design Details

- `SqliteCacheStore` unchanged — it already uses `GetEffectiveCacheRoot()` for DB and artifact paths.
- `cache clear` is destructive across all contexts (security-first: ensures full cleanup).
- `cache status` shows only the current context (what you'd actually query against).
- Same token always produces the same hash → cache reuse across restarts.
- Different tokens → different caches (correct: different access levels).

## For Lambert

New behavior to test:
- `ComputeTokenHash(null)` → `null`, `ComputeTokenHash("")` → `null`, `ComputeTokenHash("token")` → 8-char hex
- Same token → same hash (deterministic)
- Different tokens → different hashes
- `GetEffectiveCacheRoot()` with `AuthTokenHash = null` ends in `/public`
- `GetEffectiveCacheRoot()` with `AuthTokenHash = "abcd1234"` ends in `/cache-abcd1234`
- `GetBaseCacheRoot()` still returns the old base path (no subdivision)
