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

📌 Session 2026-02-12-cache-security: Security hardening on cache layer (f8b49a3). (1) Auth context isolation — separate cache DBs per HELIX_ACCESS_TOKEN hash. (2) Path traversal hardening — new CacheSecurity.cs. llmstxt updated by Ripley with auth isolation docs. README may need security/auth context section update. — decided by Ripley

- **2026-02-15-readme-comprehensive-update:** Updated README with Caching section (SQLite cache, TTL policy, auth isolation, CLI commands table), HTTP multi-auth subsection under Authentication (Bearer/token header, per-request isolation, env var fallback), expanded Project Structure (Cache/ directory, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor, 298 tests), added ci-analysis replacement note in Architecture section. Known Issues section verified accurate (ListFilesAsync confirmed in HelixApiClient.cs:46). All updates match source code in HelixTool.Core/Cache/, HelixTool.Mcp/HttpContextHelixTokenAccessor.cs, and Program.cs files.
- Documentation pattern: Cache docs use a settings table + bold-label bullets for policies — concise, scannable, no prose paragraphs.
- Documentation pattern: HTTP auth documented as a subsection under Authentication (### level) since it extends the existing auth story rather than being a standalone topic.

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash
📌 Team update (2026-02-13): MCP API design review — 6 actionable improvements identified (P0: batch_status array fix, P1: add hlx_list_work_items, P2: naming, P3: response envelope) — reviewed by Dallas
📌 Team update (2026-02-13): Generalize hlx_find_binlogs to hlx_find_files with pattern parameter — update CLI help text, README to document new find-files command and pattern parameter — decided by Dallas


📌 Team update (2026-02-13): Remote search design — 2 new MCP tools (hlx_search_file, hlx_test_results) and CLI commands (search-file, test-results) designed. README and llmstxt will need updates when implemented — decided by Dallas


📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH config toggle added as security safeguard for disabling file content search operations — decided by Larry Ewing (via Copilot)

📌 Team update (2026-02-13): US-31 hlx_search_file Phase 1 implemented (SearchFileAsync, MCP tool, CLI command, config toggle) — decided by Ripley


📌 Team update (2026-02-13): Status filter changed from bool to enum (failed|passed|all) — decided by Larry/Ripley