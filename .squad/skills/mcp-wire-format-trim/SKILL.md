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
