# Skill: Extending SDK-Backed Adapter Interfaces

**When to use:** Adding new fields from an upstream SDK (e.g., `Microsoft.DotNet.Helix.Client`) through our mockable interface → adapter → cache-DTO layer.

## Pattern

Our Core layer wraps SDK concrete types behind mockable interfaces (`IWorkItemSummary`, `IJobDetails`, etc.). Three classes must stay in sync when a new SDK field is surfaced:

1. **Interface** (`IHelixApiClient.cs`) — add nullable property
2. **Adapter** (`HelixApiClient.cs` inner class) — map from SDK type
3. **Cache DTO** (`CachingHelixApiClient.cs` record) — add to constructor + `From()` factory

## Checklist

- [ ] New property is **nullable** (SDK may not populate on older data)
- [ ] Adapter delegates directly to SDK property (no transformation)
- [ ] Cache DTO includes field in both the record positional params and `From()` method
- [ ] Old cached data (missing new fields) deserializes to `null` — verify JSON round-trip
- [ ] Update any mocks/fakes in test projects that implement the interface
- [ ] If the field enables skipping a downstream API call, add the optimization in `HelixService`, not in the adapter layer

## Files (always this set)

| Layer | File |
|-------|------|
| Interface | `src/HelixTool.Core/Helix/IHelixApiClient.cs` |
| SDK Adapter | `src/HelixTool.Core/Helix/HelixApiClient.cs` |
| Cache Adapter | `src/HelixTool.Core/Helix/CachingHelixApiClient.cs` |
| Service Logic | `src/HelixTool.Core/Helix/HelixService.cs` |

## Anti-patterns

- Don't rename SDK fields at the interface boundary — keep names aligned to reduce cognitive load
- Don't add transformation logic in adapters — they are pass-through only
- Don't make non-nullable what the SDK provides as nullable
