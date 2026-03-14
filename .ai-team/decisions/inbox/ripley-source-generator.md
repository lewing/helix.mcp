# Ripley: source generator-backed CLI describe registry

## Decision
Use a Roslyn source generator in `src/HelixTool.Generators/` to emit `HelixTool.Generated.CommandRegistry` for `hlx describe`.

## Why
- MCP `[Description]` attributes in `HelixTool.Mcp.Tools` remain the single source of truth for agent-facing command descriptions.
- CLI commands opt in with `[McpEquivalent("...")]`, which keeps the CLI/MCP mapping explicit without introducing shared description constants.
- `hlx describe` can stay runtime-light and strongly typed because it consumes generated data instead of reflecting over command metadata at startup.

## Key files
- `src/HelixTool.Generators/DescribeGenerator.cs`
- `src/HelixTool.Core/McpEquivalentAttribute.cs`
- `src/HelixTool/Program.cs`
