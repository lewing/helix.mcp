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

📌 Team update (2026-03-07): AzDO pipeline support architecture adopted — new MCP tools for AzDO builds/timelines/logs. Documentation updates will be needed for README and llmstxt. — decided by Dallas
📌 Team update (2026-03-07): Auth UX Phase 1 approved — hlx login/logout/auth-status commands coming. README will need auth documentation. — decided by Dallas

📌 Team update (2026-03-07): AzdoService implemented — method signatures stable, can begin MCP tool descriptions. — decided by Ripley

### 2026-03-07: Decision — AzdoMcpTools returns model types directly
7 new MCP tools need documentation: azdo_build, azdo_builds, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results.

📌 Team update (2026-03-08): AzDO security review complete — SEC-1 fix (prNumber validation) will change azdo_builds tool behavior. Security review patterns documented in decisions.md. — decided by Dallas

📌 Team update (2026-03-08): AzDO context-limiting defaults — 6 AzDO MCP tools have safe defaults (tailLines=500, filter="failed", top=20/50/200). Tool descriptions may need updating. — decided by Ripley

📌 Team update (2026-03-08): AzDO README + llmstxt documentation — 9 AzDO MCP tools documented in README (AzDO Tools subsection under MCP Tools) and llmstxt. AzDO auth chain, caching TTLs, and project structure added. Test count updated to 700. — documented by Kane

## Learnings

- AzDO tools are MCP-only (no CLI subcommands) — README documents them only in the MCP Tools section, not in CLI Commands
- AzDO auth chain pattern: env var → az CLI → anonymous — different from Helix which uses env var → git credential → error
- AzDO caching uses the same SqliteCacheStore but with distinct TTL rules per endpoint type (builds 4h completed/15s in-progress, logs 4h, tests 1h)
- AzdoIdResolver accepts both dev.azure.com and visualstudio.com URL formats, plus plain integer build IDs
- When adding a new API domain (AzDO alongside Helix), use subsections (### Helix Tools / ### AzDO Tools) rather than separate top-level sections to keep the README scannable
- llmstxt raw string literal in Program.cs must stay flush-left — no indentation inside the `"""..."""` block
- AzDO tools total 9 (not 7 as originally noted in earlier decision): azdo_build, azdo_builds, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_artifacts, azdo_test_attachments
- Key AzDO source files: `src/HelixTool.Core/AzDO/AzdoMcpTools.cs` (tool definitions), `AzdoService.cs` (core logic), `AzdoIdResolver.cs` (URL/ID parsing), `CachingAzdoApiClient.cs` (cache wrapper)
- AzDO MCP tool source moved from `src/HelixTool.Core/AzDO/AzdoMcpTools.cs` to `src/HelixTool.Mcp.Tools/AzdoMcpTools.cs` — must grep for actual location before editing
- AzDO tools total is now 12 (was 9): added azdo_search_log, azdo_search_timeline, azdo_search_log_across_steps. Helix tools = 11. Grand total = 23.
- `azdo_search_log_across_steps` MCP name maps to CLI command `hlx azdo search-log-all` — naming convention: MCP uses underscores, CLI uses kebab-case, and "across_steps" was shortened to "all" for CLI brevity
- Incremental log fetching is documented in Caching section (TTL policy — AzDO paragraph), not as a standalone section — consistent with keeping caching details in one place
- `azdo_search_log_across_steps` is gated by `HLX_DISABLE_FILE_SEARCH` same as other search tools — added to Security section's file search toggle list

📌 Team update (2026-03-08): `IsFileSearchDisabled` promoted from internal to public on `HelixService` — needed for MCP tools extraction to separate assembly. Consistent with existing public statics `MatchesPattern` and `IsTestResultFile`. — decided by Ripley

📌 Team update (2025-07-18): Perf review identified 17 allocation issues — decided by Ripley

📌 Team update (2026-03-09): CI profile analysis — 14 recommendations for MCP tool descriptions. Tool descriptions in HelixMcpTools.cs and AzdoMcpTools.cs will change. README and llmstxt may need updates once descriptions are implemented. — decided by Ash

📌 Team update (2026-03-10): CiKnowledgeService expanded to 9 repos with 9 new CiRepoProfile properties. MCP tool descriptions now embed repo-specific CI knowledge. 171 new tests added. — decided by Ripley

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

📌 Team update (2026-03-10): README overhaul — restructured around value proposition, caching, and context reduction. Removed project structure section, moved CLI reference to docs/cli-reference.md, de-emphasized TRX support, consolidated auth. PR #18. — documented by Kane

- README structure: Why → Context-Efficient Design → Cross-Process Caching → MCP Tools → Installation → MCP Config → Auth → Security. This order leads with value prop for evaluators.
- CLI reference lives at docs/cli-reference.md — README links to it but doesn't include full command listings. Keep CLI details there, MCP tool tables in README.
- "How hlx Enhances the Helix API" section was removed — its content overlapped heavily with the Why section and the context-efficiency table. Avoid duplicate storytelling.
- TRX support is one row in the Helix tools table, not a featured section. It's a feature, not a differentiator.
- MCP tool table descriptions should be short (one line). Detailed parameter docs belong in [Description] attributes on the actual tools, not README.
- README went from 589 → ~270 lines. Conciseness is a feature for a README — readers are evaluating, not studying.

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley
