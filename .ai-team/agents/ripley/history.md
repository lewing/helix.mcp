# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync
- **HelixIdResolver:** Handles both bare GUIDs and full Helix URLs (extracts job ID from URL path)
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Learnings

📌 Team update (2026-02-11): Architecture review filed — P0: DI/testability + error handling needed before feature work. No changes until Larry confirms priorities. — decided by Dallas
📌 Team update (2026-02-11): MatchesPattern changed to internal static; InternalsVisibleTo added to Core csproj for test access. — decided by Lambert
📌 Team update (2026-02-11): Documentation audit found missing XML doc comments on public records and HelixIdResolver. — decided by Kane
📌 Team update (2026-02-11): Caching strategy proposed — two-tier (memory LRU + disk) with job-completion-aware invalidation. Optional HelixCache parameter on HelixService. — decided by Dallas
📌 Team update (2026-02-11): Cache TTL policy revised — console logs never cached for running jobs, completed jobs: 4h memory / 7d disk, 500MB auto-eviction. See decisions.md. — decided by Dallas
📌 Team update (2026-02-11): Requirements backlog formalized — 30 user stories (US-1 through US-30). P0: US-12 (DI/testability) and US-13 (error handling) must land before feature work. — decided by Ash
📌 Team update (2026-02-11): P0 Foundation design decisions D1–D10 merged — IHelixApiClient interface, constructor injection, HelixException, CancellationToken, input validation, mock boundaries. You are assigned implementation. See decisions.md. — decided by Dallas
