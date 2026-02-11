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
📌 Team update (2026-02-11): P0 Foundation design decisions D1–D10 merged — DI refactoring and error handling. Documentation changes should wait until P0 implementation lands. — decided by Dallas

📌 Session 2026-02-11-p0-implementation: P0 implementation landed. IHelixApiClient, HelixException, refactored HelixService with DI — public API surface changed. XML doc comments and README updates can now proceed without rework risk.

📌 Session 2026-02-12-docs-fixes: Completed all four documentation tasks:
1. Fixed llmstxt indentation bug — raw string literal now uses `var text = """...""";` pattern with proper indentation stripping. Output verified flush-left.
2. Added MCP tool docs to llmstxt — all five tools (hlx_status, hlx_logs, hlx_files, hlx_download, hlx_find_binlogs) with return JSON shapes, parameters, and CLI-vs-MCP guidance.
3. Updated README.md — added Architecture section (Core/CLI/MCP split), Installation section (build-from-source + NuGet feed requirement for dotnet-eng), Known Issues section documenting the ListFiles workaround for dnceng#6072 (US-28).
4. Added XML doc comments to IHelixApiClient (all 6 methods + dnceng#6072 note on ListWorkItemFilesAsync), HelixApiClient (class + `<inheritdoc />` on all methods), HelixException (class + constructor), HelixService (constructor, all public methods, all public records: WorkItemResult, JobSummary, FileEntry, BinlogResult).
- Build: 0 warnings, 0 errors. Tests: 38/38 pass.
- Remaining docs gaps: HelixIdResolver class/method XML docs, HelixMcpTools class-level doc comment, LICENSE file missing.

📌 Team update (2026-02-11): Ripley implemented US-1 (positional args on all commands) and US-20 (rich status output with State, ExitCode, Duration, MachineName). `IWorkItemDetails` interface expanded, `WorkItemResult` record updated. FormatDuration duplicated in CLI/MCP. 38/38 tests pass.

📌 Session 2026-02-12-mcp-stdio-docs: Updated documentation for `hlx mcp` stdio transport:
1. **README.md** — Architecture section updated to note HelixTool serves both CLI commands and stdio MCP. MCP Server section now shows both stdio (recommended) and HTTP transports. MCP Configuration section reorganized: VS Code/GitHub Copilot shows stdio as primary with HTTP fallback; added Claude Desktop config; updated Claude Code/Cursor with stdio-first config. Project Structure updated to show HelixMcpTools.cs in both HelixTool and HelixTool.Mcp.
2. **llmstxt output** in Program.cs — MCP Server section now documents `hlx mcp` (stdio) and `hlx-mcp`/`dotnet run` (HTTP) as two separate lines.
- No code logic changes — documentation only.
- When the `hlx mcp` command implementation lands (Ripley), the docs are already in place.

📌 Team update (2026-02-11): US-4 auth design approved — HELIX_ACCESS_TOKEN env var. README auth section and MCP client config example needed (D-AUTH-6). — decided by Dallas
📌 Team update (2026-02-11): Stdio MCP implemented as `hlx mcp` subcommand. README/llmstxt already updated in previous session. — decided by Dallas/Ripley

📌 Session 2026-02-12-shipped-features-docs: Updated docs for shipped features:
1. **llmstxt output** — Rewrote raw string literal using `var text = """..."""; Console.Write(text);` pattern. Added `download-url` command, MCP Tools section listing all 6 tools (hlx_status, hlx_logs, hlx_files, hlx_download, hlx_download_url, hlx_find_binlogs), auth section ("Set HELIX_ACCESS_TOKEN env var for internal jobs"), noted consoleLogUrl in status output, noted hlx_files groups by type.
2. **README.md** — Added `download-url` CLI example in Quick Start. Added `hlx_download_url` row to MCP Tools table. Added "Install as Global Tool" subsection under Installation (`dotnet pack` + `dotnet tool install -g`). Verified Authentication section is accurate (unchanged).
- Build: 0 warnings, 0 errors.
- The `download-url` CLI command and `hlx_download_url` MCP tool are documented but implementation was not verified in this session (docs-only scope).

📌 Team update (2026-02-11): US-29 MCP input flexibility — `workItem` now optional on `hlx_logs`, `hlx_files`, `hlx_download` MCP tools. [Description] attributes updated. llmstxt and README may need updating to reflect optional workItem and URL-based input. — decided by Ripley


📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley



📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley

📌 Session 2026-02-12-new-features-docs: Updated docs for US-10, US-21, US-22, US-23:
1. **README.md** — Added `work-item`, `batch-status`, `search-log` CLI examples to Quick Start. Added `hlx_work_item`, `hlx_batch_status`, `hlx_search_log` rows to MCP Tools table. Added Failure Categorization section documenting auto-classification categories and `failureCategory` field.
2. **llmstxt output** in Program.cs — Added `work-item`, `batch-status`, `search-log` to CLI Commands list. Added `hlx_work_item`, `hlx_batch_status`, `hlx_search_log` to MCP Tools list. Added Failure Categorization section. Expanded Output section to cover new commands.
- Build: 0 warnings, 0 errors.
- US-22 (search-log) documented proactively — implementation being built in parallel by Ripley.

📌 Session 2026-02-12-install-section-rework: Reworked README.md Installation section:
1. **"Install as Global Tool (Recommended)"** — now the primary section, showing `dotnet tool install -g hlx` as first-class pattern. Noted not yet published to nuget.org. Added `dotnet tool install --local` option for repo-local tool manifests.
2. **"Install from Local Build"** — moved the `dotnet pack` + `--add-source` flow into its own subsection.
3. **"Build from Source"** — kept as-is, moved below install sections since most users want to install, not build.
- Key file: `README.md` lines 15–53.
- No code changes — documentation only.

📌 Session 2026-02-13-dnx-zero-install: Updated README.md to add `dnx` zero-install pattern as the primary MCP server configuration approach:
1. **Installation section** — Added "Run with dnx (no install needed)" subsection before "Install as Global Tool". Shows `dnx hlx mcp` as the simplest way to run the tool. Demoted "Install as Global Tool" heading (removed "Recommended" label).
2. **MCP Configuration section** — Updated all three client configs (VS Code/GitHub Copilot, Claude Desktop, Claude Code/Cursor) to use `"command": "dnx", "args": ["hlx", "mcp"]` as the primary pattern. Added blockquote note to each: "If you've installed hlx as a global tool, you can use `hlx` directly instead of `dnx hlx`."
3. **Authentication section** — Updated MCP client env config example to use `dnx` command with `["hlx", "mcp"]` args.
4. **Requirements section** — No change needed; already says ".NET 10 SDK" which is required for `dnx`.
- `dnx` is a .NET 10 feature that auto-downloads and runs NuGet tool packages without pre-installation — ideal for MCP server configs where the client launches the tool.
- No code changes — documentation only.

