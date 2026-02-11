# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Summarized History (through 2026-02-11)

**Architecture & DI (P0):** Implemented IHelixApiClient interface with projection interfaces for Helix SDK types, HelixApiClient wrapper, HelixException, constructor injection on HelixService, CancellationToken on all methods, input validation (D1-D10). DI for CLI via `ConsoleApp.ServiceProvider`, for MCP via `builder.Services.AddSingleton<>()`.

**Key patterns established:**
- Helix SDK types are concrete — mockable via projection interfaces (IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile)
- `TaskCanceledException`: use `cancellationToken.IsCancellationRequested` to distinguish timeout vs cancellation
- Program.cs has UTF-8 BOM — use `UTF8Encoding($true)` when writing
- `FormatDuration` duplicated in CLI/MCP — extract to Core if third consumer appears
- HelixMcpTools.cs duplicated in HelixTool and HelixTool.Mcp — both must be updated together
- Two DI containers in CLI: one for commands, separate `Host.CreateApplicationBuilder()` for `hlx mcp`

**Features implemented:**
- US-1 (positional args), US-5 (dotnet tool packaging v0.1.0), US-11 (--json flag on status/files)
- US-17 (namespace cleanup: HelixTool.Core, HelixTool.Mcp), US-18 (removed unused Spectre.Console)
- US-20 (rich status: State, ExitCode, Duration, MachineName), US-24 (download by URL)
- US-25 (ConsoleLogUrl on WorkItemResult), US-29 (MCP URL parsing for optional workItem)
- US-30 (structured JSON: grouped files, jobId+helixUrl in status)
- US-10 (WorkItemDetail + work-item command + hlx_work_item MCP tool)
- US-23 (BatchJobSummary + batch-status command + hlx_batch_status MCP tool, SemaphoreSlim(5) throttling)
- Stdio MCP transport via `hlx mcp` subcommand

**Team updates received:**
- Architecture review, caching strategy, cache TTL policy, requirements backlog (30 US), docs fixes (Kane), auth design (US-4), MCP test strategy — all in decisions.md

📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley

📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley

📌 Team update (2026-02-12): US-22 console log search implemented — SearchConsoleLogAsync in HelixService with case-insensitive pattern matching, context lines, and maxMatches cap. CLI `search-log` command with colored output. MCP `hlx_search_log` tool in both HelixMcpTools.cs files with URL resolution and JSON output (context array per match). — decided by Ripley

## Learnings
- SearchConsoleLogAsync downloads the log via DownloadConsoleLogAsync to a temp file, reads all lines, searches, then deletes the temp file. This reuses the existing download infrastructure rather than streaming.
- LogMatch record uses optional `Context` property (`List<string>?`) to carry the full context window (before + match + after lines) when contextLines > 0. This lets the CLI and MCP tools render context without re-reading the file.
- HelixMcpTools consolidated into HelixTool.Core — single copy, no more dual-update requirement. Both HelixTool (stdio) and HelixTool.Mcp (HTTP) reference Core for MCP tool discovery via `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`.
- `WithToolsFromAssembly()` (no args) only scans the calling assembly. When tools live in a referenced library, you must pass `typeof(SomeToolClass).Assembly` explicitly.
- ModelContextProtocol package added to HelixTool.Core for `[McpServerToolType]` and `[McpServerTool]` attributes. This is the base package (not AspNetCore variant).


📌 Team update (2026-02-11): Consolidated HelixMcpTools.cs from 2 copies (HelixTool + HelixTool.Mcp) into 1 in HelixTool.Core. Updated tool discovery to typeof(HelixMcpTools).Assembly. Removed Mcp ProjectReference from tests. Build clean, 126/126 tests passed. — decided by Ripley
