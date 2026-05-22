# DTO consolidation

## Use when
- The same output contract is defined in multiple files or layers.
- One copy is CLI-facing and another is MCP/API-facing.
- You need to remove duplication without changing wire format.

## Safe pattern
1. **Diff the actual wire contract first**: property names, casing, nullability, ignored/default fields, and extra properties.
2. **Do not assume near-identical DTOs are reusable as-is**. If one side relies on default serializer naming and the other uses `[JsonPropertyName]`, they are different contracts.
3. **Centralize definitions in one results file**, but keep separate DTO types when contracts differ.
4. **Alias centralized types at call sites** to minimize behavioral edits.
5. **Delete the old duplicate definitions only after the call sites compile against the centralized types.**
6. **Verify wire compatibility twice**:
   - full build + existing tests
   - schema or serialized-output spot-checks on the commands/tools that changed

## HelixTool example (2026-05-22)
- Moved CLI JSON DTOs out of `src/HelixTool/Program.cs` into `src/HelixTool.Mcp.Tools/McpToolResults.cs`.
- Kept separate CLI DTOs because CLI output intentionally mixes PascalCase and camelCase, while MCP DTOs are explicit camelCase.
- Reused the shared results file as the single definition location, not the same CLR type for both surfaces.
