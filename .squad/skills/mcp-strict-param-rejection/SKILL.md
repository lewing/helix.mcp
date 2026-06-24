---
name: "mcp-strict-param-rejection"
description: "Strict unknown-parameter rejection for MCP tools: Stage A (UnmappedMemberHandling.Disallow) + Stage B (did-you-mean CallToolFilter)."
domain: "mcp-binding"
confidence: "high"
source: "earned"
---

## Context

Use when adding strict unknown-parameter rejection so callers receive a structured `McpException` instead of silent data loss when they pass a hallucinated or misspelled parameter name.

Two complementary layers — deploy both for best UX + safety:
- **Stage A** — SDK-level `UnmappedMemberHandling.Disallow` (stopgap; no hints)
- **Stage B** — `AddUnknownParameterFilter` CallToolFilter (polished UX with "did you mean" hints)

---

## Stage A — SDK Strict Rejection (Stopgap)

### SDK Mechanism

`Microsoft.Extensions.AI.Abstractions 10.5.2` (shipped with MCP 1.3.0+) includes a strict check in `ReflectionAIFunction.InvokeCoreAsync`:

```
if (SerializerOptions.UnmappedMemberHandling == Disallow && !HasCustomParameterBinding)
    throw ArgumentException(paramName: "arguments",
        message: "The arguments dictionary contains an unexpected key 'X' that does not correspond to any parameter of 'Y'.");
```

`HasCustomParameterBinding` is `true` only when a tool parameter has a non-null `BindParameter` callback (DI injection). Plain value-type and string params have `HasCustomParameterBinding = false` → strict check fires.

### How to Enable

Pass a `JsonSerializerOptions` with `UnmappedMemberHandling = Disallow` **and** `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` to `WithToolsFromAssembly`:

```csharp
using System.Text.Json.Serialization.Metadata;

.WithToolsFromAssembly(typeof(MyTools).Assembly, new JsonSerializerOptions
{
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    TypeInfoResolver = new DefaultJsonTypeInfoResolver(),  // ← required companion; see below
})
```

Apply to **both** HTTP and stdio transport registrations if both are used.

### Why `TypeInfoResolver` is required

`AIFunctionFactory.GetOrCreate` (in `Microsoft.Extensions.AI.Abstractions`) calls `MakeReadOnly()` on the passed `JsonSerializerOptions` **before** the tool descriptor is constructed. The constructor then calls `AIJsonUtilities.CreateFunctionJsonSchema` → `CreateJsonSchemaCore`, which contains:

```csharp
// Decompiled AIJsonUtilities:
if (jsonSerializerOptions.TypeInfoResolver == null)
    jsonSerializerOptions.TypeInfoResolver = DefaultOptions.TypeInfoResolver; // ← throws on read-only options
```

Setting any property on already-read-only options throws `InvalidOperationException`. The crash does **not** happen at startup (tools are registered via DI factory lambdas) — it fires on the **first MCP request** that triggers tool singleton resolution.

**Fix:** Always set `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` upfront, before `MakeReadOnly()` is called.

### Alias Normalization Prerequisite

**Critical:** If `NormalizeArgumentAliases` (or equivalent) renames an alias key to canonical but does not remove the original alias key, the original key remains in the dict and is rejected by strict mode. Always call `arguments.Remove(aliasKey)` immediately after copying to the canonical key.

```csharp
arguments[canonical] = value;
arguments.Remove(aliasKey);   // ← required for strict mode compat
```

### Stage A Error Surfacing

The `ArgumentException(paramName: "arguments")` is caught by `AddBindingErrorFilter` in this codebase. No filter change is required — the existing catch clause `ex.ParamName == "arguments"` matches. Error has no "did you mean" hint — use Stage B for that.

---

## Stage B — `AddUnknownParameterFilter` (Polished UX)

### What It Does

Runs as a `CallToolFilter` **before** SDK dispatch. Computes `unknowns = argKeys − canonicalParams`.
On non-empty unknowns, throws a structured `McpException` with "did you mean" hints:

```
Unknown parameter 'minFinishTime' for tool 'azdo_builds'.
Did you mean: minTime?
Allowed parameters: org, project, top, branch, prNumber, definitionId, status, minTime, maxTime, queryOrder.
```

For multiple unknowns:
```
Unknown parameters for tool 'azdo_builds':
  'minFinishTime' — did you mean: minTime?
  'fooBar'
Allowed parameters: org, project, top, branch, prNumber, ...
```

### Registration Order (Critical)

```csharp
options.AddBindingErrorFilter();           // 1. alias norm + exception wrap
options.AddUnknownParameterFilter(asm);    // 2. did-you-mean check (Stage B)
// SDK dispatch with Disallow runs next     // 3. safety net (Stage A)
```

`AddBindingErrorFilter` **must** be registered first so alias normalization runs before the unknown-param check.

### Canonical-Param Map at Startup

`AddUnknownParameterFilter(Assembly)` builds the `toolName → ToolParamInfo` map once at registration
using the `RuntimeHelpers.GetUninitializedObject` + `McpServerTool.Create` pattern (no DI needed,
no constructor runs, safe for startup):

```csharp
var shell = RuntimeHelpers.GetUninitializedObject(type);
var tool = McpServerTool.Create(method, shell, options: null);
var schema = tool.ProtocolTool.InputSchema;
// Parse schema["properties"] keys → canonical param set
```

The map is captured in the filter closure — no per-request parsing.

### Schema Edge Cases

| Condition | Behavior |
|-----------|----------|
| `InputSchema.ValueKind` is `Undefined` or `Null` | Skip filtering for that tool (log Warning) |
| No `"properties"` key in schema | Parameterless tool — any argument is unknown |
| `"additionalProperties": true` | Skip filtering for that tool (log Debug) |
| Schema extraction throws | Skip filtering for that tool (log Warning) |

### Levenshtein Hint Threshold

Threshold: **6** (not 3 as originally spec'd). Rationale:
- `minFinishTime` → `minTime` has Levenshtein distance = 6 (removes "finish" infix)
- Threshold 3 catches only typos; threshold 6 also catches hallucinated compound names
- The full allowed-params list is always shown, so false-positive hints are harmless

### Stage A Coexistence

Keep `UnmappedMemberHandling.Disallow` alongside Stage B. Defense-in-depth rationale:
Stage B skips filtering when schema extraction fails at startup. Stage A catches those cases.
Stage B fires first in the filter pipeline → callers see the better UX error; Stage A's raw error
is only a fallback for the `HasCustomParameterBinding == true` edge case and schema-extraction failures.

---

## Edge Cases

- **DI-injected params:** `HasCustomParameterBinding = true` silently disables Stage A's check for that tool. Stage B is immune to this (schema-based diffing, not SDK introspection). Keep both.
- **Multiple alias canonicals per request:** Alias normalization loop must use `continue` (not `return`) after each successful rename so callers passing aliases for two different canonicals (e.g. `build_id + result` on `azdo_search_timeline`) have all entries resolved before Stage B fires.
- **Alias conflict (two aliases for same canonical, no canonical present):** First entry in the alias table wins; subsequent entries see `HasArgument(canonical) == true` and skip. The losing alias key remains in the dict and will be rejected by Stage B/A — acceptable tradeoff.

## Files in This Codebase

- `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` — `AddBindingErrorFilter`, `AddUnknownParameterFilter`, alias table, Levenshtein helper
- `src/HelixTool.Mcp/Program.cs` — `WithToolsFromAssembly` with serializer options (HTTP) + both filter registrations
- `src/HelixTool/Program.cs` — same for stdio transport
- `src/HelixTool.Tests/McpServerOptionsExtensionsTests.cs` — alias, binding-error, and unknown-param tests
