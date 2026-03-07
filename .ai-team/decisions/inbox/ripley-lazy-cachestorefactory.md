# Decision: Use Lazy<T> in CacheStoreFactory to prevent concurrent factory invocation

**Author:** Ripley  
**Date:** 2026-03-08  
**Status:** Implemented  

## Context

`ConcurrentDictionary.GetOrAdd(key, factory)` does not guarantee single-invocation of the factory lambda for a given key. Under contention, multiple threads can invoke the factory concurrently. In `CacheStoreFactory`, this caused multiple `SqliteCacheStore` constructors to race on `InitializeSchema()` for the same SQLite database file, producing `ArgumentOutOfRangeException` from SQLitePCL on Windows CI.

## Decision

Changed `ConcurrentDictionary<string, ICacheStore>` to `ConcurrentDictionary<string, Lazy<ICacheStore>>`. The `Lazy<T>` wrapper (default `LazyThreadSafetyMode.ExecutionAndPublication`) guarantees the factory runs exactly once per key regardless of contention. `Dispose()` checks `IsValueCreated` before accessing `.Value` to avoid triggering lazy init during cleanup.

## Impact

- **All team members:** This is a standard .NET pattern — use `Lazy<T>` wrapping whenever `ConcurrentDictionary.GetOrAdd` factories have side effects.
- **Lambert:** Existing `CacheStoreFactoryTests` now pass reliably, including the `ParallelCallsReturnSameInstance` thread-safety test.
