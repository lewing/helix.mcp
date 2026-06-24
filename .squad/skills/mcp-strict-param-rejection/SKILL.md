---
name: "mcp-strict-param-rejection"
description: "Enable strict unknown-parameter rejection on MCP tools using UnmappedMemberHandling.Disallow."
domain: "mcp-binding"
confidence: "high"
source: "earned"
---

## Context

Use when adding strict unknown-parameter rejection so callers receive a structured `McpException` instead of silent data loss when they pass a hallucinated or misspelled parameter name.

## SDK Mechanism

`Microsoft.Extensions.AI.Abstractions 10.5.2` (shipped with MCP 1.3.0+) includes a strict check in `ReflectionAIFunction.InvokeCoreAsync`:

```
if (SerializerOptions.UnmappedMemberHandling == Disallow && !HasCustomParameterBinding)
    throw ArgumentException(paramName: "arguments",
        message: "The arguments dictionary contains an unexpected key 'X' that does not correspond to any parameter of 'Y'.");
```

`HasCustomParameterBinding` is `true` only when a tool parameter has a non-null `BindParameter` callback (DI injection). Plain value-type and string params have `HasCustomParameterBinding = false` → strict check fires.

## How to Enable

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

## Alias Normalization Prerequisite

**Critical:** If `NormalizeArgumentAliases` (or equivalent) renames an alias key to canonical but does not remove the original alias key, the original key remains in the dict and is rejected by strict mode. Always call `arguments.Remove(aliasKey)` immediately after copying to the canonical key.

```csharp
arguments[canonical] = value;
arguments.Remove(aliasKey);   // ← required for strict mode compat
```

## Error Surfacing

The `ArgumentException(paramName: "arguments")` is caught by `AddBindingErrorFilter` in this codebase. No filter change is required — the existing catch clause `ex.ParamName == "arguments"` matches.

## Edge Cases

- **DI-injected params:** `HasCustomParameterBinding = true` silently disables the strict check for that tool. Stage B's CallToolFilter-based approach (canonical-param diffing) is immune to this. Keep both as defense-in-depth.
- **Multiple alias canonicals per request:** Alias normalization loop must use `continue` (not `return`) after each successful rename so callers passing aliases for two different canonicals (e.g. `build_id + result` on `azdo_search_timeline`) have all entries resolved before strict mode fires.
- **Alias conflict (two aliases for same canonical, no canonical present):** First entry in the alias table wins; subsequent entries see `HasArgument(canonical) == true` and skip. The losing alias key remains in the dict and will be rejected by strict mode — acceptable tradeoff for a malformed call.

## Files in This Codebase

- `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` — alias table and `NormalizeArgumentAliases`
- `src/HelixTool.Mcp/Program.cs` — `WithToolsFromAssembly` with serializer options (HTTP)
- `src/HelixTool/Program.cs` — `WithToolsFromAssembly` with serializer options (stdio)
- `src/HelixTool.Tests/McpServerOptionsExtensionsTests.cs` — alias and binding-error tests
