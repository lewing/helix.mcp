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

# Decision: MCP Schema Token Cost Measurement (Issue #74)

**Date:** 2026-07-20T14:10:14-05:00  
**Author:** Ripley  
**Status:** Measurement complete — decision deferred to Dallas

## Measurement Details

Empirical ground truth from live MCP server:
- **25 tools**, **32,703 bytes**, **~8,175 tokens** for `tools/list` payload
- **outputSchema breakdown:** 10,862 bytes (~2,715 tokens) — 33.2% of total
- **inputSchema structure:** 12,370 bytes (~3,092 tokens) — 37.8% of total
- **Parameter descriptions:** 5,379 bytes (~1,344 tokens) — 16.4% of total
- **Tool descriptions:** 3,189 bytes (~797 tokens) — 9.8% of total

### Repeated Param Description Waste
- **buildIdOrUrl:** 106 bytes × 11 tools = 1,060 bytes wasted
- **jobId:** 102 bytes × 8 tools = 714 bytes wasted
- **Total deduplication opportunity:** 2,543 bytes (~635 tokens)

### Reduction Levers for Evaluation
1. **Strip outputSchema → minimal `{"type":"object"}`** — ~8.9 KB / 33% savings, zero runtime impact
2. **Description tightening** — ~1 KB from deduplication and shortening repeated boilerplate
3. **Enum/AllowedValues removal** — not recommended; breaks validation contract
4. **Tool gating/removal** — deferred; needs usage telemetry
5. **$ref shared schemas** — SDK does not support; skipped

Decision ownership: Dallas (awaiting go/no-go on implementation).

---

# Decision: MCP Schema Size Reduction — Ranked Levers and Recommendations

**Date:** 2026-07-20  
**Author:** Dallas  
**Status:** Proposed (pending Ripley's measurement)

## Top Recommendation: DO FIRST — Minimal outputSchema (Lever 1)

**What:** Replace auto-generated `outputSchema` (full property graph of each return DTO) with minimal `{"type":"object"}`.

**How:** `McpServerToolCreateOptions.OutputSchema` is explicitly settable. Post-registration loop iterates tools and sets `tool.ProtocolTool.OutputSchema` to `{"type":"object"}`.

**Impact:** ~8.5–8.9 KB removed from `tools/list` (31% of baseline). 20 structured tools × ~440 bytes average → ~17 bytes each.

**Risk:** LOW
- Runtime responses identical — structured content continues to emit
- No known MCP client parses `outputSchema` for validation
- Reversible if consumer emerges

**Effort:** S (half-day Ripley)

**Compatibility:** Works with Lever 2 (description tightening), does NOT interact with strict-mode PR #83.

## Secondary Considerations

- **Lever 2 (description tightening):** ~1 KB, low-medium risk, incremental effort
- **Lever 3 (AllowedValues):** DO NOT PURSUE — breaks validation contract
- **Lever 4 (tool gating):** DEFER — needs usage telemetry
- **Lever 5 ($ref):** SKIP — SDK doesn't support

## Decision Gate

Proceed with Lever 1 if **any** of:
1. Per-turn `tools/list` re-fetch confirmed (not hypothetical)
2. Tool count grows >30 (currently 20)
3. User reports token budget pressure with evidence

OR proceed now as low-risk hygiene improvement (Lever 1 effort is S, risk is provably zero).

---

# Decision: Issue #81 Stage B — Unknown-Param Filter Design

**Date:** 2026-06-24  
**Author:** Ripley  
**Status:** Implemented (branch `squad/81-strict-mode-stage-b`)

## Filter Pipeline Architecture

**New sibling extension:** `AddUnknownParameterFilter(Assembly, ILogger?)` — separate from `AddBindingErrorFilter` for clarity.

Pipeline order:
1. `AddBindingErrorFilter` — alias normalization + exception wrapping
2. `AddUnknownParameterFilter` — proactive unknown-param check (did-you-mean hints)
3. SDK dispatch with `UnmappedMemberHandling.Disallow` (defense-in-depth)

## Key Implementation Decisions

### Schema Extraction
- Use `RuntimeHelpers.GetUninitializedObject(type)` pattern to extract `ProtocolTool.InputSchema["properties"]` without DI construction
- One shell per type, reused across methods, created lazily inside type loop
- Pattern mirrors `McpToolsListPayloadTests` (documented in mcp-wire-format-trim SKILL.md)

### Levenshtein Threshold: 6 (not 3)
- Threshold 3 only catches typos (single-char transpositions)
- Threshold 6 catches hallucinated compound names ("minFinishTime" → "minTime" is distance 6)
- Spec regression test (`minFinishTime` → `minTime` hint) requires threshold 6
- False positives harmless; full allowed-params list always shown

### Edge Cases Handled
- Missing schema (`Undefined`/`Null`) — skip filter, log warning
- Parameterless tools — empty canonical set, any arg is unknown
- `additionalProperties: true` — skip filter, log debug
- Schema extraction throws — skip filter, log warning

### Allowed-Param List
Displayed in schema-declaration order (mirrors method signature), not alphabetical.

---

# Decision: Compatibility — Lever 1 + Strict-Mode (PR #83)

**Date:** 2026-07-20  
**Author:** Dallas, Ripley  
**Status:** Confirmed compatible

Minimal outputSchema (Lever 1) does NOT interact with `UnmappedMemberHandling.Disallow` or `TypeInfoResolver` requirements.

- **outputSchema:** Governs tool response *documentation* in `tools/list` — purely informational
- **inputSchema + TypeInfoResolver:** Governs parameter binding — architectural requirement for Disallow mode

Both can coexist without conflict. `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` is required regardless of outputSchema minimization.

# Decision: Progressive Disclosure for tools/list

**Date:** 2026-07-20T16:16:05-05:00  
**Author:** Dallas  
**Status:** Evaluated — NOT RECOMMENDED as primary lever

---

## Context

tools/list is ~29 KB / 25 tools, all advertised on every session. Question: can we hide niche/rarely-used tools and reveal them only when needed?

## Tool Bucketing

| Bucket | Count | Est. Bytes | Tools |
|--------|-------|-----------|-------|
| CORE (always needed) | 8 | ~12.8 KB | azdo_build, azdo_builds, azdo_timeline, azdo_helix_jobs, azdo_search_log, helix_status, azdo_build_analysis, helix_ci_guide |
| WORKFLOW-GATED (need build/job first) | 11 | ~12.5 KB | azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_search_timeline, azdo_artifacts, helix_logs, helix_files, helix_work_item, helix_search, helix_find_files |
| NICHE (narrow scenarios) | 6 | ~4.6 KB | helix_parse_uploaded_trx¹, helix_batch_status, helix_download, azdo_test_attachments, helix_auth_status, azdo_auth_status |

¹ Self-identifies as "Niche" in description.

## Mechanism Feasibility (SDK 1.4.0)

### 1. Dynamic ToolCollection + `notifications/tools/list_changed`
**SDK support: YES.** ToolCollection supports Add/Remove; SDK auto-sends the notification.  
**Practical problem: NO TRIGGER.** MCP has no client→server signal for "I'm about to need drill-down tools." The server can only react AFTER a core tool is called — but models plan multi-step chains BEFORE calling anything. Hiding workflow-gated tools degrades the model's ability to plan an investigation sequence. The chicken-and-egg problem is fundamental.

### 2. Meta/gateway tool (single dispatcher)
**Feasible** but degrades model accuracy. Models are trained on discrete tool schemas; a dispatcher with sub-operation names reduces tool-use precision. Not recommended for a 25-tool server.

### 3. Resources instead of Tools
CiKnowledgeResource already exists. Moving helix_ci_guide to resource-only saves 479 bytes but loses tool-call discoverability (models invoke tools, they don't proactively read resources). Marginal gain.

### 4. Config-based profiles (static filtering)
**Simplest and most practical.** Operator picks a profile at server startup: "minimal" (core 8), "azdo-only" (14), "full" (25). No runtime complexity. Useful for embedded deployments that only need one system.

## Verdict

**Progressive disclosure is NOT the right lever for this server.**

| Option | Token Savings | Feasibility | Ergonomic Cost | Score |
|--------|:---:|:---:|:---:|:---:|
| flatten-10/keep-3 (outputSchema) | ~5.5 KB | Trivial | Zero | ★★★★★ |
| Config profiles (option 4) | up to ~16 KB | Easy | Per-deployment | ★★★☆☆ |
| Dynamic disclosure (option 1) | ~4.6–12.5 KB | Moderate | Model planning degradation | ★★☆☆☆ |
| Meta/gateway (option 2) | ~4.6 KB | Easy | Worse accuracy | ★☆☆☆☆ |

**Reasoning:**
- The NICHE bucket (only realistic disclosure candidates) saves just 4.6 KB — less than outputSchema flattening alone.
- The WORKFLOW-GATED bucket is 12.5 KB but hiding it breaks investigation planning.
- flatten-10/keep-3 delivers comparable savings with zero ergonomic risk and zero runtime complexity.
- These levers compose (flatten reduces per-tool cost; profiles reduce tool count) but addressing them in priority order means flatten first, profiles only if an operator actively needs a smaller surface.

## Recommendation

1. **Ship flatten-10/keep-3** as designed (outputSchema tiering). Already scoped, no risk.
2. **If further cuts are needed:** implement config-based profiles (option 4) — a `--tools-profile minimal|azdo|helix|full` flag at startup.
3. **Do NOT invest in runtime dynamic disclosure** — the trigger problem makes it a net-negative for model planning quality.
4. **Interaction with flatten-10/keep-3:** Orthogonal. Flatten reduces per-tool cost (descriptions stay, schemas shrink). Profiles reduce tool count. They compose cleanly but flatten is strictly higher ROI as the first move.

---

# Kane: hlx CLI Skill & Discoverability Assessment
**Date:** 2026-07-20T16:52:27-05:00  
**Author:** Kane (Docs)  
**Status:** Recommendation — awaiting approval before edits

## Verdict

We already have `.github/skills/helix-cli/SKILL.md` and it's **good** — arguably richer than maestro-cli's in the places that matter most (4-level progressive discovery ladder, 7 workflow-oriented patterns, real jq field paths with comments). However, three small gaps exist in the skill doc itself, and the **bigger problem is discoverability**: nothing in the MCP configuration path, the README header, or the cli-reference explicitly tells someone "if MCP isn't running, use `hlx` directly."

## Part A — Gap Analysis

### Where ours is already better
| Area | Our advantage |
|------|--------------|
| Progressive discovery | 4-level ladder (`hlx describe` → `hlx describe <cmd>` → `--schema` → `--help`) vs. maestro's 3-step; `describe` specifically surfaced as "the fastest first step" |
| jq examples | Real field paths with inline comments (`.failed[].Name`, `.records[].log.id`, `.steps[].matchCount`); maestro's are sparser |
| Common patterns | 7 numbered, workflow-oriented patterns (build → timeline → logs → tests) vs. maestro's per-command snippets |
| AzDO auth chain | 4-tier chain with scheme-aware description (PAT vs Entra auto-detected); maestro has a simpler 3-tier |
| jq-not-required note | Explicit: "if jq is not available, plain CLI output is still useful" |

### Where maestro has an edge or we have gaps
| Area | Gap |
|------|-----|
| Cache sharing callout | Maestro explicitly says "using CLI warms cache for MCP and vice versa" as a named feature. Our Cache section has one sentence: "shared SQLite-backed cache across CLI and stdio MCP usage" — accurate but easy to miss and not framed as a benefit |
| `dnx` no-install path | SKILL.md shows `dotnet tool install -g` and `dotnet run` but not `dnx lewing.helix.mcp` — the README's recommended no-install path. Maestro equivalently documents its single install command |
| Cache section depth | Maestro's cache note is a bullet under Usage; ours is a stub that says "full TTL details in `hlx llms-txt`" — an unnecessary chase for the reader |
| Frontmatter trigger | Both say "when MCP tools aren't loaded" — neither says "when the MCP server fails to start or is not configured," which is the primary real-world scenario the user is asking about |

## Part B — Concrete SKILL.md Edits (ranked by impact)

### 1. Add `dnx` to Installation (highest impact / zero risk)
Current Installation block shows `dotnet tool install -g` and `dotnet run`. Add:
```bash
# Zero-install (requires .NET 10 SDK):
dnx lewing.helix.mcp <command>
```
Place it first — it's the lowest-friction entry.

### 2. Expand the Cache section (one sentence addition)
Current: `hlx uses a shared SQLite-backed cache across CLI and stdio MCP usage.`  
Add after: `Running \`hlx\` from the terminal warms that cache for later MCP calls — and vice versa — so you never pay the API cost twice.`

### 3. Widen the frontmatter USE FOR trigger
Current: `when MCP tools aren't loaded`  
Change to: `when MCP tools aren't loaded, when the MCP server isn't configured or fails to start`  
This is the phrase an agent would have in its context when the MCP isn't running.

### 4. Inline the key cache locations in the Cache section
Rather than "full TTL details in `hlx llms-txt`," add the two-line OS table (already in README) so the section is self-contained. Reduces chase.

## Part C — Discoverability (the heart of the question)

A `.github/skills/` file is surfaced to agents that load skill manifests — but **not** to humans who can't start the MCP server, and **not** to agents whose MCP session failed to initialize. The skill doc is good once someone finds it; the problem is the path to finding it.

### Recommended touchpoints (ranked by leverage)

**1. README headline / top of file** ← highest leverage  
Line 3 says "An increasingly inaccurately named CLI and MCP server" — self-deprecating about the CLI. Replace or augment with a one-line callout in the Why? section or just below the title:
> **No MCP? `hlx` works standalone.** Install with `dotnet tool install -g lewing.helix.mcp` or run `dnx lewing.helix.mcp <command>`. [CLI reference →](docs/cli-reference.md)

This is the first thing a human reads when they find the repo and MCP is unavailable.

**2. MCP Configuration section in README** ← high leverage  
The MCP config block ends with client config tables. Add one sentence after:
> If MCP configuration isn't working, the same commands are available directly: `hlx azdo timeline <id>`, `hlx status <jobId>`, etc. — see the [CLI reference](docs/cli-reference.md).

**3. SKILL.md frontmatter description** ← medium leverage (agent-facing)  
Add "when the MCP server isn't configured or fails to start" to USE FOR (see Part B #3 above). Agents whose MCP init failed will match this description and route here.

**4. docs/cli-reference.md first line** ← medium leverage  
Currently: "`hlx` is the command-line interface for helix.mcp."  
Change to: "`hlx` is the standalone CLI for helix.mcp — it works without any MCP server or configuration."  
This is what someone lands on from Google or a direct link, and it should immediately confirm this is the fallback path.

### Not recommended
- Adding a new separate CLI-only README or docs file — creates maintenance split. All edits belong in existing files.
- Changing the skill location — `.github/skills/helix-cli/SKILL.md` is exactly right.

## Summary Edit List (ranked)

| Rank | File | Change | Size |
|------|------|--------|------|
| 1 | README.md (near top) | Add "No MCP? `hlx` works standalone" callout | 1-2 lines |
| 2 | SKILL.md | Add `dnx` install option first in Installation | 3 lines |
| 3 | SKILL.md | Expand Cache section with "warms cache for both" | 1 sentence |
| 4 | README.md (MCP Config section) | Add "same commands available as `hlx` if MCP not working" | 1 sentence |
| 5 | SKILL.md frontmatter | Widen USE FOR to include "MCP not configured or fails to start" | phrase edit |
| 6 | docs/cli-reference.md (line 1) | Add "works without any MCP server" to opening sentence | phrase edit |
---
date: 2026-07-28
author: ripley
status: decided
---

# Decision: Add `workItem` parameter to `helix_find_files`

## Problem

A calling model passed `workItem` to `helix_find_files` and received a hard schema-rejection error from strict-mode parameter validation:

> Unknown parameter 'workItem' for tool 'helix_find_files'. Did you mean: maxItems?

All sibling tools (`helix_files`, `helix_logs`, `helix_search`, `helix_download`, `helix_work_item`, `helix_parse_uploaded_trx`) accept `workItem`. `helix_find_files` was the sole exception.

## Decision

Add `workItem` as an optional parameter to `helix_find_files`. When supplied:
- Skip `ListWorkItemsAsync` (which pages through all work items in the job)
- Call `ListWorkItemFilesAsync` directly on the named work item
- Return a `FindFilesResults` with `TotalWorkItems = 1`, `Truncated = false`

This is equivalent to `helix_files` + client-side glob filter, implemented at the service layer for proper error handling.

## Rationale

- Consistency: models learn the jobId/workItem pattern from one sibling and apply it to all. Breaking consistency is a usability defect, not a feature.
- Performance: single-work-item fast path avoids listing all work items in the job.
- No breaking change: `workItem` is optional with `null` default; existing callers unaffected.

## Scope

- `src/HelixTool.Core/Helix/HelixService.cs` — `FindFilesAsync` signature + single-item fast path
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` — MCP tool: added `workItem`, URL extraction, updated description
- `FindBinlogsAsync` internal caller updated to use named `cancellationToken:` arg

## Other inconsistencies checked

No other schema inconsistencies found. `helix_status` and `helix_batch_status` are intentionally job-scoped and do not need `workItem`.

---
date: 2026-07-28T16:18:09-05:00
author: lambert
status: proposed
---

# Decision: Schema Consistency Tests for Work-Item-Scoped Helix Tools

## Problem

A hard schema-rejection error (`Unknown parameter 'workItem' for tool 'helix_find_files'`) reached production because there was no automated gate catching when a new tool in the Helix family omitted a parameter that all its siblings had. The parameter validation layer did exactly what it should — it rejected the unknown param — but the gap in the schema itself was never caught before ship.

The existing `McpServerToolParameters_HaveDiscoverableDescriptions` test in `McpToolDescriptionTests.cs` guards descriptions but not cross-sibling parameter consistency.

## Decision: Explicit Enumeration Test in McpToolDescriptionTests

Add a `[Theory]` test (`WorkItemScopedHelixTools_HaveOptionalWorkItemParameter`) that enumerates every Helix MCP tool expected to have an optional `workItem` parameter:

```
helix_logs, helix_files, helix_download, helix_find_files,
helix_work_item, helix_search, helix_parse_uploaded_trx
```

The test uses reflection to assert each tool has a `workItem` parameter that is optional (nullable string with null default).

## Rationale

- **Explicit enumeration > heuristic derivation.** The description-based approach (look for "work item" in the description) doesn't work reliably: `helix_find_files` says "Search work items" (plural), while the tools that had it say "for a Helix work item" (singular). The explicit list is the ground truth.
- **Fails loudly on regression.** If a new tool is added to this family without `workItem`, the test fails with a message that names the tool and its actual parameter list.
- **Self-documents the invariant.** The test and its inline comments are the canonical answer to "which Helix tools need workItem?"

## Alternatives Rejected

- **Heuristic over description text:** Unreliable (see above).
- **Single `[Fact]` over all tools:** No — `[Theory]` is better so each tool gets its own pass/fail row in the test runner.

## Consequences

- Any future Helix tool in the work-item-scoped family must be added to the `[InlineData]` list, or the test will error with "tool not found."
- The test is green today (Ripley's fix already landed) and must stay green.

## Companion Tests Added

- `HelixMcpToolsTests.FindFiles_WithWorkItem_ScansOnlyNamedWorkItem` — behavioral: when `workItem` is provided, only that item is queried; `ListWorkItemsAsync` is not called.
- `HelixMcpToolsTests.FindFiles_WithWorkItem_DoesNotReturnOtherWorkItems` — behavioral: results contain only the requested work item.
- Two pre-existing tests fixed to use named args (`pattern: "*.trx"`, `pattern: "*"`) after Ripley's parameter ordering change.

# Review: helix_find_files workItem parameter addition

**Date:** 2026-07-28T16:18:09-05:00
**Reviewer:** Dallas (Lead)
**Artifacts:** Ripley (implementation), Lambert (tests)
**Verdict:** ✅ APPROVE

---

## 1. Parameter Ordering — Acceptable

**MCP tool level (`FindFiles`):** `workItem` is placed after `jobId` and before `pattern`. This matches the sibling convention exactly — `Download`, `Logs`, `Files`, `Search`, and `ParseUploadedTrx` all place `workItem` as the second parameter after `jobId`. MCP parameters bind by name (JSON schema), not positionally, so the wire protocol is unaffected. The two test callsites updated to named arguments (`pattern: "*.trx"`) are the only C# callers outside tests. No package is published; this is not external API surface.

**Service level (`FindFilesAsync`):** `workItem` is appended after `progress` and before `cancellationToken` — the lowest-disruption position. The only internal caller change is `FindBinlogsAsync` switching `cancellationToken` from positional to named, which Ripley handled correctly. `Program.cs` passes 3 positional args and is unaffected.

No source-compat concern. Ordering follows established convention.

## 2. Fast Path Correctness — Sound

- **Glob matching:** Fast path uses the same `MatchesPattern(f.Name, pattern)` as the scan path. ✓
- **Error handling:** A nonexistent work item will hit `ListWorkItemFilesAsync` → HTTP 404 → caught by the existing `catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)` block → surfaces as `HelixException`. Same behavior as the scan path hitting a missing job. ✓
- **`maxItems` interaction:** Silently ignored when `workItem` is set. The tool description says "instead of scanning up to maxItems work items," which is clear. The MCP tool sets `ScannedItems = 1` in the response, so callers see the actual scope. Acceptable.
- **`Truncated` flag:** Fast path returns `Truncated: false`, `TotalWorkItems: 1` — correct since there is no truncation when targeting a single item. ✓

## 3. Tool Description — Clear and Consistent

The updated description adds: "When workItem is supplied, scopes the search to that single work item (equivalent to helix_files + glob filter) instead of scanning up to maxItems work items."

The `workItem` parameter description — "Helix work item name; optional only when jobId is a full Helix work item URL" — is character-for-character identical to sibling tools (`helix_logs`, `helix_files`, `helix_search`, etc.). ✓

The URL-extraction fallback (`HelixIdResolver.TryResolveJobAndWorkItem`) matches the pattern in all sibling tools. ✓

## 4. Test Quality — Adequate with Caveats

**Schema consistency test (`WorkItemScopedHelixTools_HaveOptionalWorkItemParameter`):**
Uses `[InlineData]` with a hardcoded list of 7 tool names. This means a future work-item-scoped tool added without `workItem` will NOT be caught unless someone remembers to add it to the list. However, auto-detection of "work-item-scoped" tools is non-trivial (no attribute or marker distinguishes them), so a hardcoded list is a pragmatic choice. The `GetMcpToolMethods()` discovery mechanism is sound for the tools it covers.

**Behavioral tests:** Correct in their assertions — they verify `ListWorkItemsAsync` is NOT called and only the named work item is scanned. However, both tests use reflection-based invocation unnecessarily complex for this scenario. Named arguments (`FindFiles(ValidJobId, workItem: "target-wi", pattern: "*.trx")`) would be simpler and more readable.

**Stale comments:** Test doc-comments say "RED until Ripley adds the optional workItem parameter" — but the parameter already exists. These are harmless but noisy.

## Non-Blocking Follow-Ups

1. **Lambert:** Simplify the two behavioral tests to use direct named-argument calls instead of reflection. The reflection was needed when writing tests anticipatorily (before the param existed), but now that it's landed, direct calls are clearer.
2. **Lambert:** Remove or update the "RED until Ripley's fix" comments — the fix has landed.
3. **Future consideration:** If a new work-item-scoped tool is added, the `[Theory]` list in `McpToolDescriptionTests` must be updated manually. Consider adding a code comment near the `[InlineData]` block reminding future authors to extend the list.

