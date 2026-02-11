# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)
- **No tests exist yet** — test project needs to be created

## Learnings

- All three projects (Core, CLI, MCP) share `namespace HelixTool` — the MCP project forces this with `<RootNamespace>HelixTool</RootNamespace>`. This causes ambiguity.
- `HelixApi` from `Microsoft.DotNet.Helix.Client` is `new()`'d in three places: HelixService.cs (field initializer), HelixMcpTools.cs (static field), Program.cs/Commands (field initializer). No DI anywhere.
- `HelixMcpTools` uses a `private static readonly HelixService` — static state prevents injection. ModelContextProtocol SDK does support DI for tool classes.
- `HelixIdResolver` is a clean static utility with pure functions — good pattern, easily testable.
- No exception handling anywhere in the codebase. API errors propagate as raw exceptions.
- `HelixService` model records (`JobSummary`, `WorkItemResult`, `FileEntry`, `BinlogResult`) are nested inside the class — should be extracted to top-level types.
- CLI has `Spectre.Console` as a dependency but uses raw `Console.ForegroundColor` instead. Empty `Display/` folder suggests rendering was planned but not implemented.
- `MatchesPattern` in HelixService is documented as "glob" but only handles `*`, `*.ext`, and substring match.
- No `CancellationToken` support on any async method.
- `SemaphoreSlim` in `GetJobStatusAsync` throttles concurrent API calls to 10 — good pattern but semaphore isn't disposed.
- `JsonSerializerOptions { WriteIndented = true }` is allocated 4 times in HelixMcpTools.cs — should be a shared static field.
- ConsoleAppFramework v5.7.13 is used for CLI command routing. Commands class is registered via `app.Add<Commands>()`.
- MCP server uses `ModelContextProtocol.AspNetCore` v0.8.0-preview.1 with `WithHttpTransport().WithToolsFromAssembly()` pattern.

📌 Team update (2026-02-11): MatchesPattern changed to internal static; InternalsVisibleTo added to Core csproj for test access. — decided by Lambert
📌 Team update (2026-02-11): Documentation audit found 15 improvements needed — missing XML docs on public records, no install instructions, no LICENSE file. — decided by Kane
