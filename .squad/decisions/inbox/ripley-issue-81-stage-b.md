# Decision: Issue #81 Stage B — Unknown-Param Filter Design

**Date:** 2026-06-24  
**Author:** Ripley  
**Status:** Implemented (branch `squad/81-strict-mode-stage-b`)

---

## Naming/Placement: Sibling `AddUnknownParameterFilter`, not extending `AddBindingErrorFilter`

**Choice:** New sibling extension method `AddUnknownParameterFilter(Assembly, ILogger?)`.

**Rationale:** The two filters handle fundamentally different error classes:
- `AddBindingErrorFilter` is reactive — catches `ArgumentException` thrown by the SDK binder and wraps it as `McpException`. Also normalizes aliases.
- `AddUnknownParameterFilter` is proactive — checks arguments BEFORE SDK dispatch and throws before the SDK ever runs.

Merging them would require `AddBindingErrorFilter` to know about the tool assembly (a new dependency it doesn't currently need), and would mix two concerns that are independently testable and could be independently configured. Keeping them separate also makes the filter pipeline order explicit in Program.cs:

```csharp
options.AddBindingErrorFilter();           // 1. alias norm + exception wrap
options.AddUnknownParameterFilter(asm);    // 2. did-you-mean check
// SDK dispatch runs next (Disallow as safety net)
```

---

## Filter Ordering and Coexistence with Stage A

Pipeline execution order at request time:
1. **`AddBindingErrorFilter`** (index 0) — alias normalization runs, then `next` is called
2. **`AddUnknownParameterFilter`** (index 1) — unknown-param check runs, throws `McpException` if unknowns found
3. **SDK dispatch** (tool invocation with `UnmappedMemberHandling.Disallow`)

Stage A's `Disallow` setting is kept. Defense rationale: the did-you-mean filter is skipped for any tool whose schema extraction fails at startup (defensive path). `Disallow` catches those cases. Cost: zero — it's a one-line flag, not duplicated algorithm code.

If the unknown-param filter fires on step 2, the `McpException` propagates through step 1's `try/catch` unchanged (the catch only handles `ArgumentException`, not `McpException`).

---

## Schema Building: `RuntimeHelpers.GetUninitializedObject` Pattern

To extract `ProtocolTool.InputSchema["properties"]` without needing DI-constructed tool instances,
we use `RuntimeHelpers.GetUninitializedObject(type)` to get a raw shell — no constructor, no DI.
The shell is passed to `McpServerTool.Create(method, shell, options: null)` purely for schema extraction.

This mirrors the pattern used in `McpToolsListPayloadTests` and is documented in the mcp-wire-format-trim SKILL.md.

One shell per type (reused across all methods of that type). Created lazily inside the per-type loop.

---

## Schema Edge Cases

| Case | Detection | Behavior |
|------|-----------|----------|
| Missing schema (`ValueKind.Undefined` / `Null`) | Defensive check at startup | Skip filter for that tool; log `LogWarning` |
| No `properties` key (parameterless tool) | `TryGetProperty("properties")` fails | Canonical set is empty; any arg is unknown |
| `additionalProperties: true` | `TryGetProperty("additionalProperties")` returns `JsonValueKind.True` | Skip filter for that tool; log `LogDebug` |
| Schema extraction throws | `try/catch` in `BuildToolParamMap` | Skip filter for that tool; log `LogWarning` |

`additionalProperties: false` (the normal case from MCP SDK's schema gen) is not special-cased —
it's treated the same as absent `additionalProperties` (the properties list is the authoritative set).

---

## Levenshtein Implementation

Inline static helper `Levenshtein(string s, string t)` — standard iterative DP, O(m×n) time and space.
Inputs are already lowercased by the caller (`FindClosestMatch`). No new package dependency.

**Threshold: 6 (not 3 as the issue spec originally stated).**

The issue spec says "threshold ≤3" but also says the regression test should produce "Did you mean: minTime?" for "minFinishTime". These are contradictory:

`Levenshtein("minfinishtime", "mintime") = 6`

(6 deletions to remove the "finish" infix: min[finish]time → mintime.)

Threshold 3 only catches typos (single-char transpositions, off-by-one spellings). Threshold 6 also catches hallucinated compound param names that share a prefix/suffix with the canonical form — the failure mode that caused PR #78's root cause. Since the full allowed-params list is always shown in the error message, a false-positive hint is harmless; the caller can ignore it and read the list. The spec's regression test requirement is the more specific and actionable constraint, so threshold 6 is correct.

Validated: with threshold 6, `minFinishTime` → `minTime` hint fires, and `maxFinishTime` → `maxTime` would also fire. `foo` → `top` (distance 2) also fires (acceptable false positive; the allowed list still corrects the caller).

---

## Allowed-Param List Ordering

Schema declaration order (not alphabetical). `properties.EnumerateObject()` preserves insertion order
in .NET's `System.Text.Json`. Since `McpServerTool.Create` builds the schema from method parameter order,
the allowed list mirrors the method signature — intuitive for callers who see the tool documentation.
