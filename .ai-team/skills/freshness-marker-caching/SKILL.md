# Skill: Freshness Marker Pattern for Delta Caching

**Confidence:** low
**Source:** earned

## Problem

You have a cache that auto-deletes expired entries (no stale reads), but you need to implement "stale-while-revalidate" or delta-append semantics for append-only data sources. When the TTL expires, you want to keep the old content and fetch only the new data — not re-download everything.

## Pattern

Use **two cache keys** per entry:

1. **Content key** — stores the actual data with a *long* TTL (e.g., 4h). Survives multiple refresh cycles.
2. **Freshness key** — a lightweight sentinel (value `"1"`) with a *short* TTL (e.g., 15s). Controls re-fetch cadence.

### Flow

```
on read(key):
    content = cache.get(contentKey)
    if content is null:
        // Cache miss — full fetch
        content = fetchAll()
        cache.set(contentKey, content, longTTL)
        cache.set(freshKey, "1", shortTTL)
        return content

    isFresh = cache.get(freshKey) is not null
    if isFresh:
        return content                      // fast path — no API call

    // Stale — delta fetch
    delta = fetchSince(lineCount(content))  // or offset, cursor, etc.
    if delta is not empty:
        content = content + delta
        cache.set(contentKey, content, longTTL)
    cache.set(freshKey, "1", shortTTL)      // reset freshness
    return content
```

### Key Properties

- **No cache interface changes required.** Uses existing get/set primitives.
- **Content is never lost on refresh.** Long TTL ensures the data outlives many freshness cycles.
- **Append-only correctness.** Only valid when the data source is append-only (new data doesn't invalidate old).
- **Natural termination.** When data stops changing (e.g., build completes), stop setting the freshness key. Content settles to normal long-TTL behavior.

## When to Use

- Append-only logs, event streams, or time-series data
- Cache stores that delete expired entries (no `getEvenIfExpired` method)
- Polling scenarios where re-downloading the full content is wasteful
- Any data source where `fetchSince(offset)` is supported

## When NOT to Use

- Mutable data (where old content can change — stale prefix is dangerous)
- Cache stores that support stale reads natively (use built-in stale-while-revalidate instead)
- Data without a stable offset/cursor mechanism for delta fetches

## Example: AzDO Build Logs

AzDO build logs are append-only. The REST API supports `startLine` for range fetches. For in-progress builds:

```csharp
var contentKey = $"log:{buildId}:{logId}";
var freshKey   = $"log-fresh:{buildId}:{logId}";

var cached = cache.Get(contentKey);       // 4h TTL — survives refreshes
var isFresh = cache.Get(freshKey) != null; // 15s TTL — controls cadence

if (cached != null && !isFresh) {
    var lineCount = cached.AsSpan().Count('\n') + (cached.Length > 0 && !cached.EndsWith('\n') ? 1 : 0);
    var delta = api.GetLog(buildId, logId, startLine: lineCount);
    if (!string.IsNullOrEmpty(delta))
        cached += delta;
    cache.Set(contentKey, cached, TimeSpan.FromHours(4));
    cache.Set(freshKey, "1", TimeSpan.FromSeconds(15));
}
```
