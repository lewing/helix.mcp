---
name: "mcp-structured-content"
description: "Pattern for migrating MCP tools from string returns to UseStructuredContent typed returns"
domain: "mcp-server-design"
confidence: "low"
source: "earned"
---

## Context
When upgrading an MCP server from manual JSON serialization (`Task<string>` + `JsonSerializer.Serialize`) to the SDK's `UseStructuredContent = true` feature, there is a systematic migration pattern that preserves wire compatibility while gaining auto-generated output schemas.

## Patterns

### Result Type Placement
Define MCP result types in a separate file (e.g., `McpToolResults.cs`) — not inline in the tool class or co-located with service-layer models. MCP result types are a presentation concern, distinct from domain models.

### Wire Compatibility via JsonPropertyName
Every property on result types MUST have a `[JsonPropertyName("camelCaseName")]` attribute matching the previous serialized output. The SDK's default naming policy may differ from the manual `JsonSerializerOptions` that were used before. Explicit attributes make the wire format independent of SDK configuration.

### Raw Text Exception
Tools that return raw text content (console logs, file contents) should NOT use `UseStructuredContent`. Return `Task<string>` directly. Structured content wrapping adds noise to text that consumers display verbatim.

### Error Path Migration
Replace `return JsonSerializer.Serialize(new { error = "..." })` with `throw new McpException("...")`. The MCP SDK translates exceptions into proper error responses. Use `McpException` for tool-level errors and `ArgumentException` for parameter validation.

### Type Naming
Avoid collisions with BCL types (e.g., `System.IO.FileInfo`) by using domain-specific prefixes (e.g., `HelixFileInfo`) rather than suffixes or underscores. The C# type name doesn't affect the wire format but should still follow conventions for maintainability.

## Anti-Patterns

### Duplicating Domain Models
Do NOT copy all fields from service-layer records into MCP result types and keep them in sync manually. Map only the fields the MCP consumer needs. If the tool adds computed fields (like formatted duration or Helix URLs), those belong only in the MCP result type.

### Missing JsonPropertyName
Relying on the SDK's default naming convention instead of explicit `[JsonPropertyName]` creates a hidden coupling. If the SDK changes its default or a different `JsonNamingPolicy` is configured, the wire format breaks silently.
