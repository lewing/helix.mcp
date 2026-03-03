# Kane — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **README.md** exists with Quick Start, MCP Tools table, Project Structure, Requirements
- **llmstxt command** exists in Program.cs — prints CLI documentation for LLM agents
- **MCP tool descriptions** are in HelixMcpTools.cs via [Description] attributes
- **XML doc comments** exist on CLI commands and HelixService class

## Core Context (summarized through 2026-02-27)

**Documentation infrastructure:**
- llmstxt and README are the authoritative docs — when new commands or MCP tools are added, both must be updated together
- llmstxt uses `var text = """..."""; Console.Write(text);` raw string literal pattern (flush-left output)
- Key naming: **package** = `lewing.helix.mcp`, **repo** = `helix.mcp`, **CLI command** = `hlx`
- When configs differ only by path/key, use ONE canonical example + table (not duplicated blocks)
- `--yes` flag required on all `dnx` args in MCP server configs (non-interactive launch)
- MCP is default mode when no subcommand given
- Mermaid diagrams render natively on GitHub — keep focused on one story per diagram
- README intro pattern: what-it-is subtitle → "Why?" section with problem + value props (bold label bullets)
- Cache docs: settings table + bold-label bullets — concise, scannable
- HTTP auth: subsection under Authentication (extends existing auth story)
- Security section: bullet list with **bold label:** prefix per item
- MCP descriptions describe what/inputs/outputs, not implementation details

**Documentation sessions completed (13 total):**
- docs-fixes, mcp-stdio-docs, shipped-features-docs, new-features-docs, install-section-rework, dnx-zero-install, package-rename, readme-consolidation, readme-audit, stale-hlx-refs, cli-examples-hlx, cache-mermaid-diagram, readme-v013-update, readme-intro-rewrite, team-md-public, value-add-audit, enhancements-section, llmstxt-missing-tools, readme-comprehensive-update

**Key files:**
- HelixMcpTools.cs consolidated into HelixTool.Core — discovered via `typeof(HelixMcpTools).Assembly`
- Tool packaged as dotnet tool: `<PackAsTool>true</PackAsTool>`, `<ToolCommandName>hlx</ToolCommandName>`
- HLX_API_KEY gates HTTP server (ApiKeyMiddleware), separate from HELIX_ACCESS_TOKEN (Helix API auth)
- hlx_find_files is generalized; hlx_find_binlogs delegates with `*.binlog` pattern
- README references MIT license but no LICENSE file exists

**Remaining docs gaps:** HelixIdResolver XML docs, HelixMcpTools class-level doc comment, LICENSE file missing, llmstxt had missing tools (fixed 2025-07-18).

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry
📌 Team update (2025-02-12): NuGet Trusted Publishing workflow — publish via git tag v*
📌 Team update (2026-02-12): SQLite cache landed (d62d0d1) — decided by Ripley
📌 Team update (2026-02-12): Cache security hardening (f8b49a3) — decided by Ripley
📌 Team update (2026-02-13): Requirements audit — audited by Ash
📌 Team update (2026-02-13): MCP API design review — reviewed by Dallas
📌 Team update (2026-02-13): hlx_find_files generalization — decided by Dallas
📌 Team update (2026-02-13): Remote search design (hlx_search_file, hlx_test_results) — decided by Dallas
📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH toggle — decided by Larry Ewing
📌 Team update (2026-02-13): US-31 hlx_search_file implemented — decided by Ripley
📌 Team update (2026-02-13): Status filter changed — decided by Larry/Ripley
📌 Team update (2026-02-15): Per-invocation temp dirs — decided by Ripley
📌 Team update (2026-02-15): CI version validation — decided by Ripley
📌 Team update (2026-02-27): Enhancement layer documentation consolidated — decided by Dallas, Kane
📌 Team update (2026-02-27): MCP descriptions: behavioral contracts only, not implementation details — decided by Dallas
📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas

## Learnings

- **Library docs section pattern:** "Using as a Library" placed after CLI/MCP usage sections, before reference sections. Structure: NuGet install → basic setup (no DI) → quick example → auth note → DI registration → cross-link to MCP.
- **HelixTool.Core NuGet metadata:** PackageId is `lewing.helix.core`, TFM is `net10.0`, defined in `src/HelixTool.Core/HelixTool.Core.csproj`
- **HelixService public API surface:** Constructor takes `IHelixApiClient`. Key methods: `GetJobStatusAsync`, `SearchConsoleLogAsync`, `ParseTrxResultsAsync`, `GetWorkItemFilesAsync`, `DownloadFilesAsync`, `FindFilesAsync`, `GetBatchStatusAsync`
- **HelixApiClient construction:** Takes optional `string? accessToken` — null means unauthenticated (public jobs)
- **Library section code examples use the same job ID** (`02d8bd09`) as the CLI examples for consistency

📌 Team update (2026-03-03): HelixTool.Core published as standalone NuGet (lewing.helix.core) — MCP tools extracted to HelixTool.Mcp.Tools, version centralized in Directory.Build.props. — decided by Dallas, executed by Ripley
📌 Team update (2026-03-03): Phase 1 auth UX approved — `hlx login`/`logout`/`auth status` commands coming. Docs update will be needed once implemented. — decided by Dallas

📌 Team update (2026-03-03): API review findings — decided by Dallas, Ash
📌 Team update (2026-03-03): Library docs extracted to docs/library.md — "Using as a Library" section moved out of README into dedicated doc, expanded with nuget.config requirement, error handling, auth clarification, key types reference, temp file cleanup notes. README retains a brief pointer. — executed by Kane
