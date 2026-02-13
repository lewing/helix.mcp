# Kane — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **README.md** exists with Quick Start, MCP Tools table, Project Structure, Requirements
- **llmstxt command** exists in Program.cs — prints CLI documentation for LLM agents
- **MCP tool descriptions** are in HelixMcpTools.cs via [Description] attributes
- **XML doc comments** exist on CLI commands and HelixService class

## Summarized History (through 2026-02-14)

**Documentation infrastructure:**
- llmstxt and README are the authoritative docs for the public API surface — when new commands or MCP tools are added, both must be updated together
- llmstxt uses `var text = """..."""; Console.Write(text);` raw string literal pattern (flush-left output)
- Key naming distinction: **package name** = `lewing.helix.mcp`, **repo name** = `helix.mcp`, **CLI command** = `hlx`
- When configs differ only by file path and key name, use ONE canonical example + a table of locations (not duplicated blocks)
- `--yes` flag required on all `dnx` args in MCP server configs (non-interactive launch)
- MCP is the default mode when no subcommand is given (`app.Run(args.Length == 0 ? ["mcp"] : args)`)

**Documentation sessions completed:**
- **2026-02-12-docs-fixes:** Fixed llmstxt indentation bug, added MCP tool docs to llmstxt, added Architecture/Installation/Known Issues to README, added XML doc comments to all public types
- **2026-02-12-mcp-stdio-docs:** Updated README/llmstxt for `hlx mcp` stdio transport (stdio primary, HTTP fallback)
- **2026-02-12-shipped-features-docs:** Documented download-url, all 6 MCP tools, auth section, install-as-global-tool
- **2026-02-12-new-features-docs:** Documented US-10/US-21/US-22/US-23 (work-item, batch-status, search-log, failure categorization)
- **2026-02-12-install-section-rework:** Restructured Installation section — global tool primary, local build secondary, build-from-source tertiary
- **2026-02-13-dnx-zero-install:** Added `dnx` zero-install as primary MCP server config approach
- **2026-02-13-package-rename:** Updated all docs for `hlx` → `lewing.helix.mcp` package rename
- **2025-02-13-readme-consolidation:** Consolidated 3 duplicate MCP config blocks into 1 example + table, added `--yes` flag, removed stale "not yet published" notes
- **2025-02-13-readme-audit:** Fixed stale `mcp` subcommand in dnx example, updated Architecture section, fixed Project Structure to match actual layout
- **2025-02-14-stale-hlx-refs:** Fixed title (`helix.mcp`), clone URL, directory name after repo rename
- **2025-02-14-cli-examples-hlx:** Replaced all `dotnet run --project` with `hlx` in Quick Start examples

**Remaining docs gaps:** HelixIdResolver XML docs, HelixMcpTools class-level doc comment, LICENSE file missing.

## Learnings

- HelixMcpTools.cs consolidated into HelixTool.Core — single copy, discovered via `typeof(HelixMcpTools).Assembly`
- Core files: IHelixApiClient.cs, HelixApiClient.cs, HelixException.cs, HelixIdResolver.cs, HelixService.cs, HelixMcpTools.cs
- CLI command names in Program.cs match `[Command("...")]` attributes; MCP tool names match `[McpServerTool(Name = "...")]`
- Tool is packaged as dotnet tool: `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>hlx</ToolCommandName>`
- README references MIT license but no LICENSE file exists in the repo root

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry
📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

📌 Session 2026-02-12-cache-implementation: SQLite-backed caching layer landed (d62d0d1). New CLI commands: `cache clear`, `cache status`. New env var: `HLX_CACHE_MAX_SIZE_MB`. llmstxt already updated by Ripley. README may need cache documentation section added.

