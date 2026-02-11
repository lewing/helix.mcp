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

- **Helix SDK return types are concrete classes**, not interfaces: `JobDetails`, `WorkItemSummary`, `WorkItemDetails`, `UploadedFile` (all in `Microsoft.DotNet.Helix.Client.Models`). Defined mockable projection interfaces (`IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile`) in `IHelixApiClient.cs` with adapter classes in `HelixApiClient.cs`.
- **All Helix SDK API methods already accept `CancellationToken`** as their last parameter with a default value — `IJob.DetailsAsync`, `IWorkItem.ListAsync`, `IWorkItem.DetailsAsync`, `IWorkItem.ListFilesAsync`, `IWorkItem.ConsoleLogAsync`, `IWorkItem.GetFileAsync`.
- **MCP SDK (`ModelContextProtocol` v0.8.0-preview.1) supports DI for instance `[McpServerToolType]` classes** when registered as singletons. `WithToolsFromAssembly()` resolves them from the service provider. Scoped/transient not supported.
- **ConsoleAppFramework v5 supports DI** via `ConsoleApp.ServiceProvider = serviceProvider`. Constructor injection works for command classes registered with `.Add<T>()`.
- **`TaskCanceledException` timeout vs. cancellation**: Don't check `ex.CancellationToken == cancellationToken` — it matches when both are `CancellationToken.None` (timeout from `default` parameter). Use `cancellationToken.IsCancellationRequested` instead: `true` = real cancellation, `false` = HTTP timeout.
- **Short string guard for `id[..8]`**: Use `id.Length >= 8 ? id[..8] : id` in temp file naming paths (`DownloadConsoleLogAsync`, `DownloadFilesAsync`).
- **New file locations**: `IHelixApiClient.cs` (interface + DTOs), `HelixApiClient.cs` (SDK wrapper + adapters), `HelixException.cs` — all in `HelixTool.Core/`.
- **HelixIdResolver now throws `ArgumentException` on invalid input** instead of silent pass-through. All callers go through `HelixService` which has its own `ArgumentException.ThrowIfNullOrWhiteSpace` guards first.
- **DI pattern for CLI**: `ServiceCollection` → register singletons → `ConsoleApp.ServiceProvider = provider`. For MCP: `builder.Services.AddSingleton<>()` in `Program.cs`.
- **ConsoleAppFramework v5 `[Argument]` attribute** works on positional parameters. Applied to `jobId` on all commands and `workItem` on logs/files/download. Named flags (e.g., `--pattern`, `--max-items`, `--all`) remain as regular parameters with defaults.
- **Helix SDK `WorkItemDetails` has 19 properties** including `State` (string), `MachineName` (string), `Started` (DateTimeOffset?), `Finished` (DateTimeOffset?), `Duration` (string), `ExitCode` (int?), `FailureReason` (enum). We compute Duration as `TimeSpan?` from Started/Finished rather than using the SDK's pre-formatted string.
- **`IWorkItemDetails` interface expanded** with State, MachineName, Started, Finished fields. `WorkItemResult` record now has 5 fields: `(string Name, int ExitCode, string? State, string? MachineName, TimeSpan? Duration)`.
- **Duration formatting** uses human-readable format: "2m 34s", "45s", "1h 2m". Helper method `FormatDuration` defined in both CLI (`Commands` class, `internal static`) and MCP (`HelixMcpTools`, `private static`).
- **Program.cs has UTF-8 BOM** (EF BB BF) — the `view` tool strips it but `edit` tool's old_str matching fails if it's not accounted for. Use PowerShell `[System.IO.File]::WriteAllText` with `UTF8Encoding($true)` to preserve it.

📌 Session 2026-02-11-p0-implementation: P0 foundation complete. IHelixApiClient, HelixApiClient, HelixException created; HelixService refactored with constructor injection, CancellationToken, error handling, input validation; MCP tools converted to instance class with DI; both hosts updated. All 38 tests pass.

📌 Session 2026-02-11-p1-features: Implemented US-1 (positional arguments on all 5 commands) and US-20 (rich status output with State, ExitCode, Duration, MachineName per work item). Updated CLI display and MCP JSON responses. FormatDuration duplicated in CLI/MCP — extract to Core if third consumer appears. 38/38 tests pass.
📌 Team update (2026-02-12): Kane completed docs fixes — llmstxt indentation fixed, MCP tools added to llmstxt, README updated with architecture/install/known-issues, XML doc comments on all P0 public types. llmstxt + README are now authoritative docs — update both when adding commands/tools.
