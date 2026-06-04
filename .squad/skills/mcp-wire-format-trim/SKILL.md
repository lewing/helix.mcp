# MCP Wire-Format Trim

## When to use
- You need to reduce `tools/list` token/byte cost without changing tool behavior.
- You are auditing MCP metadata bloat in `McpServerTool` attributes or auto-generated schemas.

## Pattern 1: default-annotation audit
1. Verify SDK defaults from the exact package version in use (`ModelContextProtocol.Core` source or reflection).
2. Only remove attribute properties whose explicit value matches the SDK default.
3. In SDK 1.3.0, the important defaults are:
   - `OpenWorld = true`
   - `ReadOnly = false`
   - `Idempotent = false`
   - `Destructive = true`
   - `UseStructuredContent = false`
4. Consequence: `OpenWorld=true` is removable noise, but `Destructive=false` must stay.

## Pattern 2: drop outputSchema while preserving wire payload
Use this only for tiny results where schema carries little value and you must keep the actual tool-call payload stable.

1. Change the tool method return type to `CallToolResult` (or `Task<CallToolResult>`).
2. Remove `UseStructuredContent = true` from the `[McpServerTool]` attribute so the SDK stops advertising `outputSchema`.
3. Return `CallToolResult` manually with both:
   - `Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions) }]`
   - `StructuredContent = JsonSerializer.SerializeToElement(value, McpJsonUtilities.DefaultOptions)`
4. This keeps the tool-call JSON payload/structured content intact while shrinking `tools/list`.

## Pattern 3: candidate triage
- Already-primitive string tools with `UseStructuredContent=false` are already optimal; they emit no `outputSchema`.
- Small DTOs are better candidates than broad result objects.
- Skip wrappers whose only trim path would change the top-level wire shape (for example object-with-property → bare array).

## Measurement

- Preferred: measure before/after byte count of the serialized `tools/list` result.
- Reliable fallback: build the assembly, create each `McpServerTool` via `McpServerTool.Create(...)`, serialize `ProtocolTool` with `McpJsonUtilities.DefaultOptions`, and count UTF-8 bytes.
- Report both total delta and per-change breakdown when possible.

### Concrete How-To (verified 2026-06-01, SDK 1.3.0, net10.0)

```csharp
// Full runnable implementation: src/HelixTool.Tests/McpToolsListPayloadTests.cs
// The pseudocode below shows the key steps for a single tool; see the test file for
// the complete loop, per-tool reporting, and sanity assertions.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

// 1. Enumerate all [McpServerTool] methods from tool classes
var toolTypes = new[] { typeof(AzdoMcpTools), typeof(HelixMcpTools), typeof(CiKnowledgeTool) };

// 2. Create uninitialized shell instances (no DI needed — schema only, never invoked)
//    McpServerTool.Create() REQUIRES a non-null instance for instance methods.
//    RuntimeHelpers.GetUninitializedObject bypasses constructors safely.
foreach (var toolType in toolTypes)
{
    var shell = RuntimeHelpers.GetUninitializedObject(toolType);
    var methods = toolType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

    // 3. Create McpServerTool and extract ProtocolTool (one per method)
    foreach (var method in methods)
    {
        var mcpTool = McpServerTool.Create(method, shell, options: null);
        var proto = mcpTool.ProtocolTool;  // ModelContextProtocol.Protocol.Tool

        // 4. Serialize with canonical wire options
        var json = JsonSerializer.Serialize(proto, McpJsonUtilities.DefaultOptions);
        var bytes = Encoding.UTF8.GetByteCount(json);

        // 5. Per-field breakdown
        //    proto.InputSchema is JsonElement (always present, non-nullable)
        var inputBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(proto.InputSchema, McpJsonUtilities.DefaultOptions));
        //    proto.OutputSchema is JsonElement? (nullable — null when UseStructuredContent=false)
        var outputBytes = proto.OutputSchema.HasValue
            ? Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(proto.OutputSchema.Value, McpJsonUtilities.DefaultOptions))
            : 0;
    }
}

// 6. Full tools/list payload ({"tools":[...]}) for total wire size
var allProtos = toolTypes
    .SelectMany(t =>
    {
        var shell = RuntimeHelpers.GetUninitializedObject(t);
        return t.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, shell, options: null).ProtocolTool);
    })
    .ToList();
var listPayload = JsonSerializer.Serialize(new { tools = allProtos }, McpJsonUtilities.DefaultOptions);
var listPayloadBytes = Encoding.UTF8.GetByteCount(listPayload);
```

**Full working test:** `src/HelixTool.Tests/McpToolsListPayloadTests.cs`

### Baseline (2026-06-01, main branch, 25 tools)

| Metric | Bytes | KB |
|---|---|---|
| tools/list full payload | **28,941** | **28.26** |
| inputSchema total | 11,068 | 10.81 |
| outputSchema total | 8,882 | 8.67 |
| input + output | 19,950 | 19.48 |

- 20/25 tools have outputSchema (UseStructuredContent=true)
- Fattest: azdo_timeline (2,099), azdo_search_log (2,027), azdo_search_timeline (1,929)
- outputSchema top: azdo_timeline (1,123), helix_status (1,001), azdo_build (929)

## Genericity / Reuse Assessment

### What the reflection harness is coupled to

The test in `src/HelixTool.Tests/McpToolsListPayloadTests.cs` has three coupling points:

1. **Hardcoded tool types** — `typeof(AzdoMcpTools)`, `typeof(HelixMcpTools)`, `typeof(CiKnowledgeTool)`. Changing this list is the only code change needed to point at a different .NET server.
2. **`[McpServerToolAttribute]` discovery** — the predicate `m.GetCustomAttribute<McpServerToolAttribute>() is not null` is specific to the `ModelContextProtocol` .NET SDK 1.x. Any .NET server built on the same SDK is covered.
3. **`McpServerTool.Create` + `McpJsonUtilities.DefaultOptions`** — these are SDK types, not helix.mcp types. They serialize the schema exactly as the SDK would emit it on the wire.

The core measurement logic (lines 43–57) — `JsonSerializer.Serialize(proto, McpJsonUtilities.DefaultOptions)`, `Encoding.UTF8.GetByteCount`, `proto.InputSchema`, `proto.OutputSchema` — is entirely generic C#. There is no helix.mcp business logic in it.

`RuntimeHelpers.GetUninitializedObject` is a general workaround; it applies to any .NET class that stores services in fields but never reads them during schema generation.

### Other .NET MCP servers — as-is vs. modified

**As-is:** Zero reuse. The `ToolTypes` array hardcodes helix.mcp types.

**Minimal modification (30 min):** Replace `ToolTypes` with an assembly scan:
```csharp
// Drop-in for any .NET MCP server assembly
var assembly = Assembly.LoadFrom("YourMcpServer.dll");
var toolTypes = assembly.GetTypes()
    .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
    .ToArray();
```
Then the rest of the code runs unchanged. This handles any .NET server using the same SDK.

**Proper extraction (1–2 hrs):** Pull the measurement loop into a static helper or small standalone console app (`mcp-schema-measure.exe <assembly.dll>`) that accepts an assembly path, auto-discovers tool types, and prints the report. No helix.mcp dependency at all.

### TypeScript / Python / Go MCP servers — not feasible from this harness

Reflection against .NET types cannot touch non-.NET servers. The approach is fundamentally incompatible — there are no `MethodInfo` objects, no `McpServerToolAttribute`, no `McpServerTool.Create`. A fundamentally different technique is required.

### Language-agnostic alternative: live JSON-RPC measurement

Since `tools/list` is a standard JSON-RPC 2.0 request over stdio or SSE, any running MCP server can be measured with:

```bash
# stdio example (works for any MCP server in any language)
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"measure","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}
{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | your-mcp-server \
  | grep '"tools"' \
  | wc -c
```

Or in Python/Node.js — launch the server as a subprocess, speak the 3-message MCP handshake over stdin/stdout, read the `tools/list` response, `len(json.dumps(response).encode())` for bytes. Works for TypeScript, Python, Rust, Go, anything.

**Tradeoffs: reflection vs. live JSON-RPC**

| | Reflection (.NET) | Live JSON-RPC |
|---|---|---|
| Language coverage | .NET only | Any |
| Requires server boot | No | Yes |
| Requires credentials/config | No | Often (auth, env vars) |
| Measures exactly what ships | Yes (SDK serialization) | Yes (actual wire bytes) |
| Fast enough for CI | Yes (< 200ms) | Depends (server startup) |
| Works offline / in test | Yes | No |
| Catches runtime schema transforms | No | Yes |

The reflection path is better as a **regression guard in CI** for .NET servers — no startup, no credentials, fast. The live JSON-RPC path is better as a **one-shot measurement tool across polyglot repos**.

### Concrete recommendation for a cross-server byte counter

**Cleanest design: two-layer tool**

Layer 1 — **Live JSON-RPC measurer** (language-agnostic, ~50 lines Python):
- Input: a shell command to launch the MCP server (stdio mode) OR an SSE URL
- Does: spawn the server, send `initialize` + `notifications/initialized` + `tools/list`, read response
- Reports: total response bytes, per-tool breakdown from `response["result"]["tools"]`
- Reusable on any MCP server in any language

Layer 2 — **Reflection measurer** (.NET-only, already exists here):
- Input: an assembly path (or the `ToolTypes` array)
- Does: exactly what this harness does
- Useful for CI regression without booting the server

**Reuse from the existing harness:** The measurement core (lines 43–57 of `McpToolsListPayloadTests.cs`) is directly reusable for Layer 2. Extract into a static method, accept `IEnumerable<Type> toolTypes`, keep the rest identical. The JSON-RPC layer (Layer 1) would be a new, separate script.


Use this to sanity-check before paying for a full build measurement. Regex-extract `[Description(...)]` and `McpServerTool(Name=...,Title=...)` attributes, build a representative compact JSON object per tool (name, title, description, inputSchema.properties), and `len(json.dumps(obj, separators=(',',':')))`. This understates real size because:
1. outputSchema is excluded (tools with `UseStructuredContent=true` emit extra schema)
2. Placeholder param names are used (add ~4 bytes/param correction for real names)
3. Annotations field may vary
Typical result: ~11 KB for 25 tools (inputSchema only). Add outputSchema for a realistic 15–20 KB for a full 25-tool server.

## Issue #74 Reference Calibration (2026-06-01)
- 25 tools, 39 total params, 5,047 total description chars (tool + param)
- Static estimate (inputSchema only, compact JSON): ~11,317 bytes — confirmed accurate (actual: 11,068 bytes, within 2%)
- Issue #74 heuristic estimate: 16,212 bytes — understated by 44% (wrong in two ways: 80 bytes/param overcount offset by missing outputSchema entirely)
- **Measured ground truth** (2026-06-01, McpServerTool.Create + ProtocolTool serialization):
  - inputSchema total: 11,068 bytes (10.81 KB)
  - outputSchema total: 8,882 bytes (8.67 KB)  ← 20/25 tools, was entirely missing from issue estimate
  - **Full tools/list payload: 28,941 bytes (28.26 KB)**
