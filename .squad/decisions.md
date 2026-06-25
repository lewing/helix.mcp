**Date:** 2026-06-24T15:37:15-05:00
**Author:** Ripley
**Branch:** fix/azdo-param-plumbing
**Status:** Implemented

## Context

Audit of AzDO MCP tools found three cases where REST API capabilities were
not reachable from callers: `minTime`/`maxTime`/`queryOrder` on builds,
the `top` parameter not forwarded to the test attachments URL, and the
`outcomes` filter hardcoded to `Failed` in test results.

## Decision: AllowedValues + server-side normalizer pattern for enum-like params

For string parameters that accept a fixed set of values (queryOrder,
filter, recordType, etc.), always apply the following defense-in-depth:

1. **`[AllowedValues(...)]`** on the MCP tool parameter — prevents binding
   unknown values before the method body runs.
2. **`Normalize*(string?)` helper on `AzdoService`** — trim + canonicalize
   (e.g., case-fold, alias expansion). Maps empty/whitespace → null.
3. **`IsValid*(string?)` check + `McpException` throw** in the tool method —
   server-side validation for callers who bypass schema constraints.
4. **Expose the constant array** (e.g., `AzdoQueryOrders`) as a public
   `static readonly string[]` on `AzdoService` so the tool's `AllowedValues`
   attribute can spread it without duplication.

## Decision: Cache key must include all discriminating params

When `CachingAzdoApiClient.HashFilter` or per-endpoint cache keys are
built, they must include **every parameter that affects the server response**
(not just the historically-implemented ones). Failure to include a new
param (e.g., `outcomes`, `queryOrder`) causes stale cache hits that return
wrong data silently.

Checklist for new params:
- [ ] Add to `AzdoBuildFilter` record (or method signature)
- [ ] Forward to REST URL
- [ ] Include in `HashFilter` / cache key string
- [ ] Expose on MCP tool
- [ ] Expose on CLI command

## Decision: AzDO REST time-range semantics

`minTime` / `maxTime` parameters are named generically. The time field
they filter against is determined by `queryOrder`:
- `queueTimeDescending` → filters queue time
- `finishTimeDescending` → filters finish time
- etc.

Document this coupling in the `minTime`/`maxTime` parameter descriptions
so callers know to pair them with the right `queryOrder`.

## Consequences

- Tools gain new capabilities without breaking existing callers (defaults
  preserve prior behavior in all cases: queryOrder defaults to
  queueTimeDescending, outcomes defaults to Failed).
- Pattern is consistent with existing `filter`/`recordType` validation on
  Timeline and SearchTimeline tools.

# Decision: Issue #81 + #82 Triage — Sequencing and Scoping

**Date:** 2026-06-24  
**Author:** Dallas  
**Status:** Proposed

---

## Sequencing Decision

**Recommended order:**

1. **Pre-work alias + #81 Stage A** (one PR, size S)
2. **#81 Stage B** (one PR, size M)
3. **#82 — full normalization refactor** (one PR, size M)

### Rationale

#81 Stage A is the highest-value/lowest-risk item in the set: a single serializer option change converts silent data-corruption failures into structured errors. It ships immediately, closes the production footgun, and has no dependency on #82. Getting the "did you mean" UX (#81 Stage B) in before the normalization refactor (#82) means callers start seeing useful errors sooner, and the refactor (#82) gets to land on a codebase where strict mode is already exercising the filter pipeline.

#82 is independent of both #81 stages but benefits from landing after #81 Stage A is in, so its contract tests can exercise the full failure surface (hallucinated params AND declared-but-not-forwarded params).

---

## Pre-work Required for #81 Stage A

### `result` → `resultFilter` alias **must land in the same PR as Stage A**

`azdo_search_timeline` exposes its filter param as `resultFilter`. The alias table (`NormalizeArgumentAliases` in `McpServerOptionsExtensions.cs`) has no entry for `result` → `resultFilter`. Once `UnmappedMemberHandling = Disallow` is set, any caller currently passing `result: 'failed'` will receive a hard rejection. Confirmed absent by source inspection — the alias array only contains three `buildIdOrUrl` aliases.

Action: add `("result", "resultFilter")` to `s_argumentAliases` in the same PR that enables strict mode. Lambert adds a regression test (alias normalizes before strict check fires).

### Scan for any other silent-tolerance aliases

Before Stage A ships, do a one-pass grep of session logs / issue history for other params being passed by callers in non-canonical form that the SDK currently tolerates. No further instances found in PR #78 / Ash's feasibility report, but confirm at implementation time.

---

## Issue #81 Decomposition

### Stage A (size S — one PR)
- Add `("result", "resultFilter")` alias entry
- Set `JsonSerializerOptions.UnmappedMemberHandling = Disallow` on `McpServerToolCreateOptions.SerializerOptions` at tool registration
- Tests: existing alias tests still pass; new rejection test confirms `ArgumentException` from `InvokeCoreAsync` is caught by existing `AddBindingErrorFilter` and wrapped as `McpException`

**Owner:** Ripley implements, Lambert writes tests. No doc surface change (error message is machine-generated, not schema-visible).

### Stage B (size M — one PR)
- Extend `AddBindingErrorFilter` (or a sibling CallToolFilter registered immediately after) with the canonical-param diffing logic
- Build `toolName → IReadOnlySet<string>(canonicalParams)` map at server startup from `tool.ProtocolTool.InputSchema["properties"]`, captured in the filter closure
- Compute `unknowns = normalizedArgKeys − canonicalParams`
- On non-empty unknowns: throw structured `McpException` with "did you mean" (Levenshtein ≤3) and full allowed-param list
- Stage A's `UnmappedMemberHandling = Disallow` can be removed if Stage B is in place (Stage B fires first in the filter pipeline, before SDK dispatch); or both can coexist as defense-in-depth

Tests per issue body: per-tool canonical pass, alias pass, unknown rejected, close-match hint present, no-match list only, multiple unknowns. Add `minFinishTime` → `azdo_builds` regression from PR #78's root cause.

**Owner:** Ripley implements. Lambert writes tests (reference `mcp-calltoolfilter-tests` SKILL.md for `RequestContext<CallToolRequestParams>` pattern). Kane: no user-visible schema change; the error message is in the response, not the schema — no doc update needed.

**Note:** Ash adds value here as a rubber-duck on the Levenshtein threshold (≤3 is Ash's recommendation; confirm no false positives in the existing param name set).

---

## Issue #82 Decomposition — One PR

All four sub-changes ship as a single coherent PR. Sub-changes 1 (normalizer), 2 (JSON cache key), and 3 (move defaults to domain) have a dependency chain and partial state in `main` is worse than the consolidated diff. Sub-change 4 (contract tests) validates the whole unit.

### Sub-change 3 first (no external dependency)
Move `AzdoApiClient.DefaultQueryOrder` to `AzdoBuildFilterDefaults` in the domain model. This is a pure rename/move with no behavioral change and unblocks sub-changes 1 and 2.

### Sub-change 1: `AzdoBuildFilterNormalizer`
Extract the normalization rules (whitespace → null, trim, default-collapse, lowercase) into a single static helper. Both `AzdoApiClient.ListBuildsAsync` and `CachingAzdoApiClient.HashFilter` call it; neither reimplements the rules. Apply the same pattern to `AzdoTestResultFilter` if it accumulates similar concerns.

### Sub-change 2: JSON-derived cache key
Replace hand-built `HashFilter` string concatenation with `JsonSerializer.Serialize(normalizedFilter, stableOptions)`. Stable options: alphabetical property ordering, omit nulls/defaults, invariant culture. New fields fail-safe — no explicit wiring needed.

### Sub-change 4: Contract tests per param
Per MCP/CLI param: (a) REST URL contains the value, (b) cache key contains the value, (c) service call shape is correct. Reference `azdo-rest-param-surface-audit` SKILL.md for pattern. This is the largest piece; estimate ~half the total effort for this issue.

**Owner:** Ripley implements all four. Lambert writes the normalizer unit tests and contract tests. No schema surface change → Kane not needed.

---

## Issue #74 Overlap

**No bundling.** #74 (schema token cost) is a `tools/list` cold-load size problem. #81 strict rejection is a runtime invocation-time problem. They are orthogonal — enabling strict mode does not add or remove bytes from `tools/list`. Dallas's existing CONDITIONAL NO verdict on #74 stands. Revisit triggers remain: per-turn re-fetch, tool count >40, or user-reported token pressure.

---

## Effort Summary

| Item | Size | Owner | Blocker for |
|------|------|-------|------------|
| Pre-work: `result` → `resultFilter` alias | S | Ripley + Lambert | #81 Stage A |
| #81 Stage A: `UnmappedMemberHandling = Disallow` | S | Ripley + Lambert | #81 Stage B |
| #81 Stage B: "did you mean" CallToolFilter | M | Ripley + Lambert (+ Ash rubber-duck) | — |
| #82: Centralize normalization (all 4 sub-changes) | M | Ripley + Lambert | — |

Total: ~1.5–2 days of Ripley + Lambert time.

---

## Open Questions

1. **Stage A vs. Stage B coexistence:** When Stage B lands, should `UnmappedMemberHandling = Disallow` from Stage A be removed (Stage B supersedes it) or kept as defense-in-depth for the `HasCustomParameterBinding == true` edge case? Decision deferred to Ripley at implementation time; document the choice in the PR.

2. **`AzdoQueryOrder` value object (#82 optional):** The issue mentions the `mcp-enum-with-aliases` skill as a natural fit. Not required for the core cleanup. Defer unless the normalizer helper reveals a natural seam for it.
# Decision: PR #83 Review Finding — `TypeInfoResolver` Required with `UnmappedMemberHandling.Disallow`

**Date:** 2026-06-24  
**Author:** Dallas  
**Status:** Blocking — change requested on PR #83

---

## Finding

Both `Program.cs` files in PR #83 pass `new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }` to `WithToolsFromAssembly` without a `TypeInfoResolver`. This is a **first-request crash** (not a startup crash).

## Root Cause (SDK Analysis)

Decompiled from `Microsoft.Extensions.AI.Abstractions 10.5.2`:

**`AIFunctionFactory.ReflectionAIFunctionDescriptor.GetOrCreate`:**
```csharp
JsonSerializerOptions jsonSerializerOptions = options.SerializerOptions ?? AIJsonUtilities.DefaultOptions;
jsonSerializerOptions.MakeReadOnly();                          // ← locks options here
...
value = new ReflectionAIFunctionDescriptor(key, jsonSerializerOptions);   // ← schema gen happens inside
```

**`AIJsonUtilities.CreateJsonSchemaCore`:**
```csharp
if (jsonSerializerOptions.TypeInfoResolver == null)
{
    // ← throws InvalidOperationException: Cannot mutate a read-only instance of 'JsonSerializerOptions'
    jsonSerializerOptions.TypeInfoResolver = DefaultOptions.TypeInfoResolver;
}
```

The SDK locks options BEFORE schema generation, then schema generation tries to auto-assign `TypeInfoResolver`. Setting any property on read-only `JsonSerializerOptions` throws.

## Impact

- All MCP tool registrations that pass custom `JsonSerializerOptions` without `TypeInfoResolver` will crash on first tool call
- Not caught at startup (factory lambdas are deferred)
- Lambert's test fix (`TypeInfoResolver = new DefaultJsonTypeInfoResolver()` in `CreateStrictFilteredToolHandler`) is correct but was not applied back to `Program.cs`

## Required Fix

In both `HelixTool.Mcp/Program.cs` and `HelixTool/Program.cs`:
```csharp
.WithToolsFromAssembly(typeof(HelixMcpTools).Assembly, new JsonSerializerOptions
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),  // required — SDK MakeReadOnly before schema gen
})
```

## Architectural Rule

**Any call to `WithToolsFromAssembly` (or `McpServerTool.Create`) with custom `JsonSerializerOptions` MUST include `TypeInfoResolver = new DefaultJsonTypeInfoResolver()`.** The SDK does not auto-populate it before calling `MakeReadOnly()`.

This rule should be documented in:
- `.squad/skills/mcp-strict-param-rejection/SKILL.md` — add `TypeInfoResolver` to the "How to Enable" code example
- Code comments in both `Program.cs` files (done as part of the fix)

## SKILL.md Update Required

Current `## How to Enable` example:
```csharp
.WithToolsFromAssembly(typeof(MyTools).Assembly, new JsonSerializerOptions
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
})
```

Must become:
```csharp
.WithToolsFromAssembly(typeof(MyTools).Assembly, new JsonSerializerOptions
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),  // required: SDK calls MakeReadOnly() before schema generation
})
```
# Decision: Issue #81 Stage A — Alias Key Removal and Loop Restructure

**Date:** 2026-06-24  
**Author:** Ripley  
**Status:** Implemented (branch `squad/81-strict-mode-stage-a`, commit `fce8686`)

---

## Design Choice: Remove Alias Key After Rename

`NormalizeArgumentAliases` previously added the canonical key but left the alias key in the dict. With `UnmappedMemberHandling = Disallow` enabled, the alias key (e.g. `build_id`) would have been flagged as an unknown param and thrown `ArgumentException` — defeating the purpose of the alias system.

**Decision:** Call `arguments.Remove(aliasKey)` immediately after setting the canonical value. The canonical is already set first so there is no window where neither key is in the dict (not relevant for single-threaded filter execution, but defensively correct).

---

## Design Choice: `return` → `continue` in Alias Loop

The previous `return` after the first successful alias rename was intentional for the original 3-alias case (all mapping to `buildIdOrUrl`). With the addition of `("result", "resultFilter")` — a different canonical — a caller to `azdo_search_timeline` that passes both `build_id` and `result` would have only `build_id` resolved. The `result` alias would remain in the dict and be rejected by strict mode.

**Decision:** Replace `return` with `continue`. "First match wins per canonical" is preserved because once the canonical is set, all subsequent entries for the same canonical see `HasArgument(canonical) == true` and skip.

---

## Design Choice: `AddBindingErrorFilter` Unchanged

Ash's feasibility report (2026-06-24) confirmed the strict-mode path throws `ArgumentException(paramName: "arguments")`. The existing catch clause matches on `ex.ParamName == BinderArgumentsParamName`. No extension needed.

If a future tool gains a DI-injected parameter (`HasCustomParameterBinding = true`), the SDK silently disables the strict check for that tool. Stage B's CallToolFilter-based approach is immune to this edge case and will supersede Stage A's defense.

---

## Open Question Resolution (from Dallas's triage)

> When Stage B lands, should `UnmappedMemberHandling = Disallow` from Stage A be removed or kept as defense-in-depth?

**Recommendation:** Keep both. Stage B fires first in the filter pipeline and provides better UX (did-you-mean hints, full allowed-param list). Stage A's `Disallow` setting catches the `HasCustomParameterBinding == true` blind spot. Defense-in-depth at the serializer level costs nothing — it's a one-line option flag, not duplicated algorithm code.
