---
name: "centralized-filter-normalization"
description: "Domain-layer normalizer + JSON-stable cache key pattern for filter types with server-side defaults and case-insensitive fields."
domain: "azdo-integration"
confidence: "high"
source: "earned"
---

## Context

Use when a filter record (e.g., `AzdoBuildFilter`) has optional string fields with server-side defaults
and case-insensitive server interpretation. Multiple layers (HTTP client, cache key builder, service
layer) historically re-implement the same canonicalization rules, causing algorithmic drift.

The pattern was extracted from the Issue #82 refactor of `AzdoBuildFilter`, which fixed four rounds
of reviewer feedback on PR #78 (each round finding the same class of normalization bug at a different
layer).

## The Principle

> **"Canonicalize at boundaries, share the algorithm."**

- **Validate at user/input boundaries** (CLI/MCP) — for early, useful error messages
- **Canonicalize at semantic boundaries** (URL construction, cache key derivation) — where the value's meaning is consumed
- **Centralize the canonicalization algorithm** — multiple layers call the shared helper; none re-implement

"Normalize at every layer" (a common first instinct) leads to algorithm duplication and drift.

## Pattern: Three Types, One File Per Filter

### 1. Defaults class (in domain model file, alongside the filter record)

```csharp
public static class AzdoBuildFilterDefaults
{
    public const string QueryOrder = "queueTimeDescending";
    public const string Outcomes   = "Failed";
}
```

- Lives in the domain layer (same project/namespace as the filter record)
- Both HTTP client and cache layer reference these constants — no layer depends on another for constants

### 2. Normalizer class (domain layer, `{FilterType}Normalizer.cs`)

```csharp
public static class AzdoBuildFilterNormalizer
{
    public static AzdoBuildFilter Normalize(AzdoBuildFilter filter) =>
        filter with
        {
            PrNumber     = NormalizeString(filter.PrNumber),
            Branch       = NormalizeString(filter.Branch),
            StatusFilter = NormalizeString(filter.StatusFilter),
            QueryOrder   = NormalizeQueryOrder(filter.QueryOrder),
        };

    private static string? NormalizeString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeQueryOrder(string? value)
    {
        var trimmed = NormalizeString(value);
        if (trimmed is null) return null;
        if (string.Equals(trimmed, AzdoBuildFilterDefaults.QueryOrder, StringComparison.OrdinalIgnoreCase))
            return null;  // collapse explicit server default to null
        return trimmed.ToLowerInvariant();  // lowercase for case-insensitive equivalence
    }
}
```

**Rules in order:**
1. `IsNullOrWhiteSpace` → `null` (whitespace is equivalent to "not specified")
2. `Trim()` non-null values
3. Collapse explicit server defaults to `null` (OrdinalIgnoreCase) — null and the default are semantically identical
4. Lowercase case-insensitive server values (prevents cache fragmentation on casing)
5. Return a **new** record via `with` (records are immutable)

**Visibility:** `public` — downstream projects (CLI/MCP entry points) may need to call it for validation purposes without duplicating the rules.

### 3. Stable JSON options for cache key (in cache layer)

```csharp
private static readonly JsonSerializerOptions s_stableCacheKeyOptions = new()
{
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver
    {
        Modifiers =
        {
            static typeInfo =>
            {
                if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
                var sorted = typeInfo.Properties
                    .OrderBy(p => p.Name, StringComparer.Ordinal)
                    .ToList();
                typeInfo.Properties.Clear();
                foreach (var p in sorted)
                    typeInfo.Properties.Add(p);
            }
        }
    }
};

private static string HashFilter(AzdoBuildFilter filter)
{
    var normalized = AzdoBuildFilterNormalizer.Normalize(filter);
    var json = JsonSerializer.Serialize(normalized, s_stableCacheKeyOptions);
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
    return Convert.ToHexString(hash)[..12].ToLowerInvariant();
}
```

**Why JSON + alphabetical sort?**
- New fields on the record automatically participate in the cache key — no explicit wiring needed
- Alphabetical property ordering (Ordinal) ensures the same filter always serializes to the same JSON regardless of declaration order
- `WhenWritingNull` omits null/unset fields — the "empty filter" produces `{}` which hashes to one stable key
- The `TypeInfoResolver.Modifiers` modifier is cached by the resolver; runtime cost is negligible

## Call Sites

```csharp
// HTTP client — before URL construction
public async Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(..., AzdoBuildFilter filter, ...)
{
    var f = AzdoBuildFilterNormalizer.Normalize(filter);
    // use f.QueryOrder ?? AzdoBuildFilterDefaults.QueryOrder for URL
}

// Cache layer — before key derivation
private static string HashFilter(AzdoBuildFilter filter)
{
    var normalized = AzdoBuildFilterNormalizer.Normalize(filter);
    // serialize normalized, hash
}
```

Neither layer re-implements the normalization rules. If a rule changes, it changes in one place.

## Cache Key Invalidation Note

Changing `HashFilter` from hand-built strings to JSON changes all cache keys. This is a **one-shot invalidation** on deployment:
- Old in-flight entries become unreachable (not returned)
- Entries expire naturally under their existing TTL
- No data corruption
- Document this in PR body when applying the pattern

## Anti-Patterns

- `HashFilter` that hand-codes each field's quirks — breaks when a new field is added
- `string.IsNullOrEmpty` instead of `IsNullOrWhiteSpace` — whitespace produces malformed URLs or distinct cache keys
- Normalization constants on the transport layer (`AzdoApiClient.DefaultQueryOrder`) referenced by the cache layer — cross-layer coupling
- Lowercase in the cache layer but not in the HTTP layer (or vice versa) — layers drift

## When to Apply

Apply this pattern when ALL of the following are true:
- A filter record has 3+ optional string fields
- Some fields have server-side defaults (null = use server default)
- Some fields are case-insensitive on the server
- The filter is used in both URL construction AND a cache key

For filters with only 1-2 simple fields, inline normalization is fine.

## References

- `AzdoBuildFilter` / `AzdoBuildFilterDefaults` / `AzdoBuildFilterNormalizer` — `src/HelixTool.Core/AzDO/`
- `CachingAzdoApiClient.HashFilter` — `src/HelixTool.Core/AzDO/CachingAzdoApiClient.cs`
- Issue #82 (source), PR #78 (root cause), history.md round 3/4 learnings
