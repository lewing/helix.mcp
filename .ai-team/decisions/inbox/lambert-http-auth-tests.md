### Lambert: HTTP/SSE auth test suite written (L-HTTP-1 through L-HTTP-4)

**By:** Lambert
**Date:** 2026-02-12

**What:** Created 28 tests across 4 new test files for the HTTP/SSE multi-client auth abstractions defined in Dallas's design spec.

**Files created:**
- `src/HelixTool.Tests/HelixTokenAccessorTests.cs` — 5 tests for EnvironmentHelixTokenAccessor
- `src/HelixTool.Tests/HelixApiClientFactoryTests.cs` — 5 tests for HelixApiClientFactory
- `src/HelixTool.Tests/CacheStoreFactoryTests.cs` — 8 tests for CacheStoreFactory (including thread safety via Parallel.For)
- `src/HelixTool.Tests/SqliteCacheStoreConcurrencyTests.cs` — 10 tests for SqliteCacheStore concurrent access

**Status:** All test files compile syntactically. Build is currently blocked by Ripley's in-progress SqliteCacheStore connection-per-operation refactor (R-HTTP-CACHE-1). Once Ripley completes that refactor, tests should build and pass.

**For Ripley:** The `SqliteCacheStoreConcurrencyTests` specifically exercise the connection-per-operation pattern you're implementing. Key scenarios: concurrent reads, concurrent writes to different/same keys, concurrent mixed read+write, and two `SqliteCacheStore` instances sharing the same SQLite DB (simulating HTTP mode). The `CacheStoreFactoryTests` verify `GetOrCreate` deduplication and `Parallel.For` thread safety.
