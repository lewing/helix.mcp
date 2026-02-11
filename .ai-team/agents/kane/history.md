# Kane — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **README.md** exists with Quick Start, MCP Tools table, Project Structure, Requirements
- **llmstxt command** exists in Program.cs — prints CLI documentation for LLM agents
- **MCP tool descriptions** are in HelixMcpTools.cs via [Description] attributes
- **XML doc comments** exist on CLI commands and HelixService class

## Learnings

- **README.md** — solid quick-start and MCP config sections; missing LICENSE file, CONTRIBUTING.md, architecture diagram, troubleshooting, error handling docs, and `dotnet tool install` instructions
- **llmstxt output** in `src/HelixTool/Program.cs` lines 140–161 — uses hard-coded indented string literal; does not document MCP tools or error semantics; does not mention the `llmstxt` command itself
- **MCP tool descriptions** in `src/HelixTool.Mcp/HelixMcpTools.cs` — [Description] attributes are present on all tools and parameters; descriptions are functional but could add error return shapes and example values
- **XML doc comments** — present on `HelixService` class and all `Commands` methods; missing on public records (`WorkItemResult`, `JobSummary`, `FileEntry`, `BinlogResult`) and on `HelixIdResolver` class/method
- **Public records** `WorkItemResult`, `JobSummary`, `FileEntry`, `BinlogResult` are defined inside `HelixService.cs` and lack XML doc comments
- **HelixIdResolver** (`src/HelixTool.Core/HelixIdResolver.cs`) — public static class with no XML doc comments on the class or `ResolveJobId` method
- **HelixMcpTools** class-level doc comment is missing (only `[McpServerToolType]` attribute, no `<summary>`)
- The `llmstxt` command output uses leading whitespace from the raw string literal — will produce indented output when printed
- Tool is packaged as a dotnet tool (`<PackAsTool>true</PackAsTool>`, `<ToolCommandName>hlx</ToolCommandName>`) but README never mentions `dotnet tool install`
- README references MIT license but no LICENSE file exists in the repo root

📌 Team update (2026-02-11): Architecture review filed — P0: DI/testability + error handling. Documentation changes should wait until P0 refactoring is done to avoid rework. — decided by Dallas
📌 Team update (2026-02-11): MatchesPattern changed to internal static; InternalsVisibleTo added to Core csproj. — decided by Lambert
