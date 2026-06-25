# Handoff: Issue #82 — Centralized AzDO Filter Normalization

**Date:** 2026-06-24  
**From:** Ripley  
**To:** Lambert  
**Branch:** `squad/82-centralize-azdo-normalization`

---

## What was implemented (production code)

### Sub-change 1: `AzdoBuildFilterDefaults` (domain layer, `AzdoModels.cs`)
```csharp
public static class AzdoBuildFilterDefaults
{
    public const string QueryOrder = "queueTimeDescending";
    public const string Outcomes  = "Failed";
}
```
All magic strings are gone from `AzdoApiClient` and `CachingAzdoApiClient`.

### Sub-change 2: `AzdoBuildFilterNormalizer.Normalize()` (`AzdoBuildFilterNormalizer.cs`)
Single static helper. Rules applied once:
- All string fields: `IsNullOrWhiteSpace` → `null`; otherwise `Trim()`
- `QueryOrder`: after trim, if OrdinalIgnoreCase-equals `AzdoBuildFilterDefaults.QueryOrder` → `null`; otherwise `ToLowerInvariant()`
- Returns a **new** record via `with` (originals are immutable)

Call sites:
- `AzdoApiClient.ListBuildsAsync` calls `AzdoBuildFilterNormalizer.Normalize(filter)` at the top, uses `f` (normalized) for URL construction
- `CachingAzdoApiClient.HashFilter` calls it before JSON serialization

### Sub-change 3: JSON-based cache key (`CachingAzdoApiClient.HashFilter`)
Replaced hand-built string concatenation with:
```csharp
var normalized = AzdoBuildFilterNormalizer.Normalize(filter);
var json = JsonSerializer.Serialize(normalized, s_stableCacheKeyOptions);
var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
return Convert.ToHexString(hash)[..12].ToLowerInvariant();
```
`s_stableCacheKeyOptions`:
- `DefaultIgnoreCondition = WhenWritingNull` — nulls omitted
- `TypeInfoResolver.Modifiers` — properties sorted alphabetically (Ordinal) for deterministic output

⚠️ **Cache invalidation note:** This changes all `builds:*` cache keys (one-shot invalidation). In-flight entries become unreachable; they will expire naturally. No data corruption — old entries are simply not found. This is the intended behavior.

### Test files updated (to match new behavior)
- `AzdoApiClientTests.cs` — two tests updated: `QueryOrder=finishTimeDescending` in the URL is now `queryOrder=finishtimedescending` (normalized lowercase). The tests are still semantically valid; they verify a non-default value reaches the URL — the value is now canonically lowercased.

---

## Test matrix: what Lambert should write

### A. `AzdoBuildFilterNormalizer` direct unit tests (no I/O, fast)

Cover every rule × every field. Suggested test class: `AzdoBuildFilterNormalizerTests`.

| Input | Expected output |
|---|---|
| `QueryOrder = null` | `null` |
| `QueryOrder = ""` | `null` |
| `QueryOrder = "   "` | `null` |
| `QueryOrder = "queueTimeDescending"` | `null` (collapse to default) |
| `QueryOrder = "QUEUETIMEDESCENDING"` | `null` (case-insensitive collapse) |
| `QueryOrder = "finishTimeDescending"` | `"finishtimedescending"` (lowercase) |
| `QueryOrder = "FINISHTIMEDESCENDING"` | `"finishtimedescending"` |
| `Branch = "  refs/heads/main  "` | `"refs/heads/main"` (trimmed) |
| `Branch = ""` | `null` |
| `PrNumber = "  42  "` | `"42"` (trimmed) |
| `StatusFilter = "  inProgress  "` | `"inProgress"` (trimmed, not lowercased) |
| All fields null | all fields null |

### B. Cache-key stability tests (`CachingAzdoApiClientTests`)

These replace the behavior-equivalence tests that existed for the old `HashFilter`. The key insight: same logical filter → same cache key, regardless of surface form.

**Tests to add:**

```
ListBuildsAsync_NullQueryOrder_AndWhitespace_ShareKey    (null, "", "   ")
ListBuildsAsync_ExplicitDefault_CollapseToSameKey        (null, "queueTimeDescending", "QUEUETIMEDESCENDING")
ListBuildsAsync_NonDefaultCasings_ShareKey               ("finishTimeDescending", "FINISHTIMEDESCENDING", "FinishTimeDescending")
ListBuildsAsync_DifferentQueryOrders_DistinctKeys        ("finishtimedescending" vs "starttimeascending")
ListBuildsAsync_DifferentTimeRanges_DistinctKeys         (already exists — keep)
ListBuildsAsync_NullAndWhitespaceBranch_ShareKey         (null vs "  " → same key)
```

**Tests that are now redundant (same rules now tested in normalizer unit tests):**
The following existing cache-key tests in `CachingAzdoApiClientTests` already pass and don't need to be deleted, but their normalization rule coverage is now duplicated by the normalizer unit tests:
- `ListBuildsAsync_NullAndWhitespaceQueryOrder_ShareCacheKey` — keep (layer smoke: delegates to normalizer)
- `ListBuildsAsync_NullAndExplicitDefaultQueryOrder_ShareCacheKey` — keep (layer smoke)
- `ListBuildsAsync_DifferentCasingsSameQueryOrder_ShareCacheKey` — keep (layer smoke)

Lambert decision: keep or remove based on taste. The normalizer unit tests cover the rules; these layer tests cover "the layer actually calls the normalizer."

### C. Per-MCP/CLI parameter contract tests

For every MCP/CLI-visible param on `azdo_builds`, verify three things:
1. The value appears in the AzDO REST URL (via `FakeHttpMessageHandler.LastRequest`)
2. The value distinguishes cache keys (different value → different key → inner called twice)  
3. The service method is called with the value (NSubstitute `Received()`)

**Parameters to cover on `azdo_builds`:**

| Param | `AzdoBuildFilter` field | REST URL param | Notes |
|---|---|---|---|
| `top` | `Top` | `$top=N` | Skip if 0 or null |
| `pr_number` | `PrNumber` | `branchName=refs/pull/N/merge` | |
| `branch` | `Branch` | `branchName=...` | URL-escaped |
| `definition_id` | `DefinitionId` | `definitions=N` | Skip if 0 or null |
| `status_filter` | `StatusFilter` | `statusFilter=...` | |
| `min_time` | `MinTime` | `minTime=...` | ISO 8601, URL-escaped |
| `max_time` | `MaxTime` | `maxTime=...` | ISO 8601, URL-escaped |
| `query_order` | `QueryOrder` | `queryOrder=...` | Normalizer lowercases non-default |

**Mock pattern to use** (see `AzdoApiClientTests.cs` for `FakeHttpMessageHandler` and `CachingAzdoApiClientTests.cs` for NSubstitute `_inner`):

```csharp
// REST URL assertion pattern (AzdoApiClientTests style):
_handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
await _client.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = "main" });
Assert.Contains("branchName=main", _handler.LastRequest!.RequestUri!.ToString());

// Cache key discrimination pattern (CachingAzdoApiClientTests style):
_cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
    .Returns((string?)null);  // always miss
_inner.ListBuildsAsync(...).Returns(new List<AzdoBuild>());
await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = "main" });
await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter { Branch = "develop" });
await _inner.Received(2).ListBuildsAsync(...);  // distinct keys → two inner calls
```

### D. `AzdoApiClient.GetTestResultsAsync` — outcomes param

Cover the same three assertions for `outcomes`:
- REST URL: `outcomes=Passed,Failed` (non-null caller value forwarded verbatim after trim)
- REST URL: `outcomes=Failed` when `outcomes=null` (default applied)
- Cache key: `outcomes=null` and `outcomes="Failed"` share a cache key (same key via `AzdoBuildFilterDefaults.Outcomes`)
- Cache key: `outcomes=null` and `outcomes="  "` share a cache key

---

## Existing tests that become redundant after Lambert's normalizer unit tests

These ad-hoc tests in `AzdoApiClientTests.cs` test normalization rules that are now the normalizer's responsibility. They remain valid (URL-level smoke), but Lambert should note their overlap:

- `ListBuildsAsync_WhitespaceQueryOrder_FallsBackToDefault` — normalizer unit test covers the rule; this test verifies the URL layer delegates correctly
- `ListBuildsAsync_EmptyQueryOrder_FallsBackToDefault` — same

Recommendation: keep both. They are now "does the call site call the normalizer" tests rather than "does the rule work" tests.

---

## Files changed in this PR

| File | Change |
|---|---|
| `src/HelixTool.Core/AzDO/AzdoModels.cs` | Added `AzdoBuildFilterDefaults` class |
| `src/HelixTool.Core/AzDO/AzdoBuildFilterNormalizer.cs` | **New** — centralizes all normalization rules |
| `src/HelixTool.Core/AzDO/AzdoApiClient.cs` | Removed `DefaultQueryOrder` const; calls normalizer; uses `AzdoBuildFilterDefaults` |
| `src/HelixTool.Core/AzDO/CachingAzdoApiClient.cs` | Replaced `HashFilter` string with JSON+normalizer; uses `AzdoBuildFilterDefaults` |
| `src/HelixTool.Tests/AzDO/AzdoApiClientTests.cs` | Updated 2 URL-assertion tests to expect lowercase `finishtimedescending` |

---

## Build/test status

- Build: **clean** (0 warnings, 0 errors)
- Tests: **1359 passed**, 2 skipped, 0 failed (same as pre-PR baseline)
