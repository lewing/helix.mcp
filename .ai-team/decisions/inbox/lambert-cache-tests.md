### 2025-02-12: Cache Test Suite Complete (L-CACHE-1 through L-CACHE-10)
**By:** Lambert
**What:** 56 tests covering all 10 Dallas cache test action items, across 3 new test files.

---

## Files Created

| File | Tests | Coverage |
|---|---|---|
| `CachingHelixApiClientTests.cs` | 26 | L-CACHE-1 (cache hit), L-CACHE-2 (cache miss), L-CACHE-3 (TTL selection), L-CACHE-4 (console log bypass), L-CACHE-5 (console log cached), L-CACHE-10 (disabled cache) |
| `SqliteCacheStoreTests.cs` | 18 | L-CACHE-6 (CRUD), L-CACHE-7 (eviction), L-CACHE-8 (idempotent schema) |
| `CacheOptionsTests.cs` | 12 | L-CACHE-9 (XDG path resolution, defaults) |

## Test Count

126 → 182 (all passing, 0 warnings)

## Decisions Made

1. **No null-guard constructor tests:** Ripley's `CachingHelixApiClient` constructor does not null-guard its parameters. Rather than writing tests that would immediately fail, I wrote a `Constructor_MaxSizeZero_DisablesCache` test that verifies the important behavioral contract instead. **Suggestion for Ripley:** Consider adding `ArgumentNullException.ThrowIfNull()` guards to the constructor — this is a cheap safety net.

2. **Temp directories for SqliteCacheStore tests:** `SqliteCacheStore` requires file-based SQLite (constructor calls `Directory.CreateDirectory`). Tests use `Path.GetTempPath()` + GUID subdirs with cleanup in `Dispose()`/finally blocks. This is slightly slower than `:memory:` but matches the real usage pattern.

3. **Console log cache miss mock pattern:** The decorator's console log flow calls `GetArtifactAsync` twice (once for miss check, once to return stored result). NSubstitute's `.Returns(null, stream)` sequential return pattern handles this correctly.

## Notes for Ripley

- The `SchemaCreation_OpenTwice_NoErrors` test opens two `SqliteCacheStore` instances on the same directory. This works because of WAL mode but may show occasional `SQLITE_BUSY` under CI load. If this becomes flaky, consider adding `busy_timeout` to the test or serializing access.
- The LRU eviction test uses `MaxSizeBytes = 100` with 60-byte artifacts. This verifies the eviction fires but doesn't deeply test the LRU ordering — a more thorough test would need controlled `last_accessed` timestamps.
