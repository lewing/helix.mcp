# Session: 2026-02-12-cache-security

**Requested by:** Larry Ewing
**Date:** 2026-02-12
**Focus:** Security hardening of SQLite cache — auth context isolation and path traversal defense

## Participants

| Agent | Role |
|---|---|
| Ripley | Fixed both security issues: auth context isolation + path traversal hardening |
| Lambert | Wrote 24 security tests (182 → 206 total) |

## Summary

Larry Ewing identified two security issues in the new cache layer:

1. **Auth context leak:** Unauthenticated `hlx` instances could read cached private job data from authenticated instances, because all instances shared a single `cache.db` and `artifacts/` directory regardless of `HELIX_ACCESS_TOKEN`.

2. **Path traversal:** Crafted inputs (job IDs, work item names, file names from Helix API responses) containing `..`, `/`, `\`, or other traversal characters could escape the cache or temp directory, enabling arbitrary file read/write.

### Fix 1: Auth Context Isolation (Ripley)

Separate SQLite databases and artifact directories per auth context:
- No token → `{base}/public/cache.db` + `{base}/public/artifacts/`
- Token present → `{base}/cache-{hash}/cache.db` + `{base}/cache-{hash}/artifacts/`

Where `{hash}` = first 8 hex chars of SHA256 of the token. `CacheOptions` gained `AuthTokenHash` property and `ComputeTokenHash()` static helper. Both DI containers pass the hash. `cache clear` wipes all auth contexts; `cache status` shows current context.

### Fix 2: Path Traversal Hardening (Ripley)

New `Cache/CacheSecurity.cs` with defense-in-depth:
1. **Sanitization** (proactive): `SanitizePathSegment()` strips `..` and replaces `/`, `\` with `_`. `SanitizeCacheKeySegment()` does the same for cache key components.
2. **Validation** (reactive): `ValidatePathWithinRoot()` resolves paths via `Path.GetFullPath` and verifies the result stays within the expected root directory.

Applied to `SqliteCacheStore` (Get/Set/Delete artifact), `CachingHelixApiClient` (all 6 cache key construction sites), and `HelixService` (3 download methods).

### Tests (Lambert)

24 security tests in `CacheSecurityTests.cs`:
- `CacheSecurityTests` (15 unit tests): ValidatePathWithinRoot (7 tests), SanitizePathSegment (6 tests), SanitizeCacheKeySegment (5 tests — note: 3 classes with some overlap in count)
- `SqliteCacheStoreSecurityTests` (2 integration tests): artifact path confinement, tampered DB row rejection
- `CachingHelixApiClientSecurityTests` (3 integration tests): sanitized keys for job IDs, work item names, file names with traversal characters

## Outcome

- All 206 tests pass, build clean
- Committed as f8b49a3, pushed to origin/main

## Decisions Made

- Cache auth isolation: separate SQLite DB + artifacts per HELIX_ACCESS_TOKEN hash (SHA256, first 8 hex chars)
- Path traversal hardening: defense-in-depth with sanitization + validation via new CacheSecurity class
- Both decisions documented in decisions/inbox/ and merged to decisions.md

## Files Changed

### New Files
- `src/HelixTool.Core/Cache/CacheSecurity.cs`
- `src/HelixTool.Tests/CacheSecurityTests.cs`

### Modified Files
- `src/HelixTool.Core/Cache/CacheOptions.cs` (AuthTokenHash, ComputeTokenHash, GetBaseCacheRoot)
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs` (path validation on artifact ops)
- `src/HelixTool.Core/Cache/CachingHelixApiClient.cs` (cache key sanitization)
- `src/HelixTool.Core/HelixService.cs` (download path sanitization)
- `src/HelixTool/Program.cs` (pass AuthTokenHash in both DI containers, cache clear/status updates)
- `src/HelixTool.Tests/CacheOptionsTests.cs` (updated for /public subdirectory)
