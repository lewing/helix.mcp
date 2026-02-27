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
- **2026-02-15-cache-mermaid-diagram:** Added Mermaid flowchart diagram to README Caching section (after opening paragraph, before settings table). Diagram shows: IDE agents → hlx stdio processes → CachingHelixApiClient → cache hit/miss decision → SQLite+disk store or Helix API, plus auth isolation subdirectories. Added "Concurrency guarantees" bullet list below diagram covering WAL mode, atomic writes, FileShare flags, and per-invocation temp dirs.
- Mermaid diagrams render natively on GitHub — use ```mermaid code blocks. Keep diagrams focused on one story (e.g., multi-process sharing) rather than cramming all details.

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry
📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

📌 Session 2026-02-12-cache-implementation: SQLite-backed caching layer landed (d62d0d1). New CLI commands: `cache clear`, `cache status`. New env var: `HLX_CACHE_MAX_SIZE_MB`. llmstxt already updated by Ripley. README may need cache documentation section added.

📌 Session 2026-02-12-cache-security: Security hardening on cache layer (f8b49a3). (1) Auth context isolation — separate cache DBs per HELIX_ACCESS_TOKEN hash. (2) Path traversal hardening — new CacheSecurity.cs. llmstxt updated by Ripley with auth isolation docs. README may need security/auth context section update. — decided by Ripley

- **2026-02-15-readme-comprehensive-update:** Updated README with Caching section (SQLite cache, TTL policy, auth isolation, CLI commands table), HTTP multi-auth subsection under Authentication (Bearer/token header, per-request isolation, env var fallback), expanded Project Structure (Cache/ directory, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor, 298 tests), added ci-analysis replacement note in Architecture section. Known Issues section verified accurate (ListFilesAsync confirmed in HelixApiClient.cs:46). All updates match source code in HelixTool.Core/Cache/, HelixTool.Mcp/HttpContextHelixTokenAccessor.cs, and Program.cs files.
- Documentation pattern: Cache docs use a settings table + bold-label bullets for policies — concise, scannable, no prose paragraphs.
- Documentation pattern: HTTP auth documented as a subsection under Authentication (### level) since it extends the existing auth story rather than being a standalone topic.
- **2026-02-15-readme-v013-update:** Comprehensive README update for v0.1.3. Expanded MCP Tools table from 9 to 12 tools (added hlx_find_files, hlx_search_file, hlx_test_results; updated hlx_status with filter param; updated hlx_batch_status with max 50 note). Added full CLI Commands reference table (15 commands with exact signatures from Program.cs). Added Security section (XML parsing, path traversal, URL scheme, HLX_DISABLE_FILE_SEARCH, batch limits). Updated Authentication to document HLX_API_KEY / X-Api-Key header for HTTP server. Updated Quick Start examples (status filter as positional arg, added find-files/search-file/test-results examples). Updated test count 298→340. All signatures verified against HelixMcpTools.cs and Program.cs source.
- Task input listed `--all` flag and `--tail` on CLI logs, but actual code uses positional filter arg and CLI logs downloads to temp file (no --tail). Always verify signatures against source before documenting.
- hlx_find_files is the generalized version; hlx_find_binlogs delegates to it with `*.binlog` pattern. Both exist as separate MCP tools and CLI commands.
- HLX_API_KEY gates access to the HTTP server via X-Api-Key header (ApiKeyMiddleware). Separate from HELIX_ACCESS_TOKEN which authenticates to the Helix API. Both are env vars but serve different purposes.
- Security section pattern: bullet list with **bold label:** prefix per item. Matches the concise/scannable style used in Caching section.

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash
📌 Team update (2026-02-13): MCP API design review — 6 actionable improvements identified (P0: batch_status array fix, P1: add hlx_list_work_items, P2: naming, P3: response envelope) — reviewed by Dallas
📌 Team update (2026-02-13): Generalize hlx_find_binlogs to hlx_find_files with pattern parameter — update CLI help text, README to document new find-files command and pattern parameter — decided by Dallas


📌 Team update (2026-02-13): Remote search design — 2 new MCP tools (hlx_search_file, hlx_test_results) and CLI commands (search-file, test-results) designed. README and llmstxt will need updates when implemented — decided by Dallas


📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH config toggle added as security safeguard for disabling file content search operations — decided by Larry Ewing (via Copilot)

📌 Team update (2026-02-13): US-31 hlx_search_file Phase 1 implemented (SearchFileAsync, MCP tool, CLI command, config toggle) — decided by Ripley


📌 Team update (2026-02-13): Status filter changed from bool to enum (failed|passed|all) — decided by Larry/Ripley

- **2026-02-15-team-md-public:** Rewrote `.ai-team/TEAM.md` as a public-facing "Meet the Team" page. Links to Squad framework, describes all 6 agents in a concise roster table (Dallas/Ripley/Lambert/Kane/Ash/Scribe). Replaced the internal roster format (project context table + charter paths + status badges) with a reader-friendly intro + role descriptions. ~17 lines of markdown.
- Task input for cache security docs incorrectly described caching as "in-memory only, process lifetime." Actual implementation is SQLite-backed, persisted to disk, with TTL-based expiry. Always verify against source — `CachingHelixApiClient.cs` is the caching decorator, `SqliteCacheStore.cs` is the persistence layer, `CacheOptions.cs` defines auth isolation and directory layout.
- Cached data types: job details (JSON in SQLite `cache_metadata`), work item summaries, work item details, file listings (all as JSON metadata), console logs and uploaded files (as files in `artifacts/` directory tracked in `cache_artifacts` table). Auth tokens are NOT cached — only a SHA256 hash prefix is used for directory naming.
- CacheSecurity.cs has three methods: `ValidatePathWithinRoot` (path traversal guard), `SanitizePathSegment` (filename sanitization), `SanitizeCacheKeySegment` (cache key sanitization). All three are `internal static`.
- TTL values in CachingHelixApiClient: running jobs 15s (details/work items) or 30s (file listings), completed jobs 4h, console logs 1h (completed only, never cached for running jobs), job state 15s running / 4h completed.
- **2026-02-15-readme-intro-rewrite:** Rewrote README intro (lines 1-19). Changed title from "Helix Test Infrastructure CLI & MCP Server" to "MCP server and CLI for investigating .NET Helix CI failures". Expanded one-line description to mention 13 MCP tools, cross-process caching, and AI agent focus. Added "Why hlx?" section with problem statement paragraph + 4-bullet value prop list (structured output, cross-process caching, context-efficiency, zero config). Moved ci-analysis replacement note from after Architecture into the new Why section. No changes below Architecture section.
- README intro pattern: lead with what-it-is subtitle, follow with a "Why?" section that states the problem (1 paragraph) then value props (bullet list with bold labels + concrete tool examples). Avoids marketing tone — states facts with tool names.

📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth for package version — decided by Ripley

- **2025-07-18-value-add-audit:** Audited all three doc surfaces (README, MCP descriptions, llmstxt) for coverage of hlx's local enhancement layer. Key findings:
  - README "Why hlx?" section covers the high-level value prop well (B+ grade) but lacks per-tool breakdown of what hlx adds vs. raw API.
  - MCP `[Description]` attributes (C grade) almost never explain that features like failure categorization, TRX parsing, and file-type grouping are local enhancements — agents may assume these come from the Helix API.
  - `hlx_search_file` description says "without downloading" which is misleading — hlx downloads the file, searches locally, then deletes it.
  - llmstxt (C+ grade) is missing `hlx_search_file` and `hlx_test_results` from the MCP Tools list — documentation bug.
  - 12 specific enhancement capabilities cataloged; only 3 (URL resolution, caching, auth isolation) are well-documented across all surfaces.
  - Recommendations written to `.ai-team/decisions/inbox/kane-docs-audit.md` with 4 prioritized actions (P1: fix llmstxt + enhance MCP descriptions, P2: README enhancement table, P3: expand failure categorization docs).
- **2025-07-18-enhancements-section:** Added "## How hlx Enhances the Helix API" section to README.md (between Failure Categorization and Project Structure). Two tables: 5 major enhancements (failure classification, TRX parsing, remote search, cross-process cache, smart TTL) with 3-column format (enhancement/what you get/why it matters), and 7 convenience enhancements (URL parsing, file discovery, batch status, file classification, computed duration, log URL construction, auth-isolated cache) with 2-column format. ~26 lines of markdown tables. Source: Dallas's 12-enhancement audit. Completes the P2 action from the docs audit.

📌 Team update (2026-02-27): Enhancement layer documentation consolidated — Dallas cataloged 12 value-adds, Kane audited doc surfaces and wrote README section. Remaining P1: llmstxt missing hlx_search_file/hlx_test_results, MCP descriptions need local-enhancement flags — decided by Dallas, Kane
