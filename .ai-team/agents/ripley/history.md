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
