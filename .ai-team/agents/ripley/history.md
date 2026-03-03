# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context

> Summarized from older entries on 2026-03-03. Full text in history-archive.md.

**Architecture & DI (P0):** Implemented IHelixApiClient interface with projection interfaces, HelixApiClient wrapper, HelixException, constructor injection, CancellationToken on all methods, input validation. DI for CLI via `ConsoleApp.ServiceProvider`, for MCP via `builder.Services.AddSingleton<>()`.

**Cache & Auth (2026-02-12–13):** SQLite-backed caching (ICacheStore, CachingHelixApiClient decorator, WAL mode). Auth isolation per token SHA256 hash. HTTP/SSE multi-auth via IHelixApiClientFactory + ICacheStoreFactory. Connection-per-operation for concurrency.

**MCP API (2026-02-13–15):** `MatchesPattern` public static. `FileEntry` simplified to `(Name, Uri)`. `FindFilesAsync` generalized (find_binlogs is convenience wrapper). Batch status uses `string[]`. MCP JSON is camelCase, CLI is PascalCase.

**Features (through 2026-02-15):** US-1 (positional args), US-5 (dotnet tool), US-10/US-23 (work-item/batch-status), US-11 (--json), US-17 (namespaces), US-20 (rich status), US-24 (download URL), US-29 (URL parsing), US-30 (structured JSON), US-31 (search-file), US-32 (TRX parsing), status filter refactor, P1 security fixes (URL scheme + batch cap), CI version validation, stdio MCP transport.

**Key patterns:**
- Two DI containers in CLI: one for commands, separate `Host.CreateApplicationBuilder()` for `hlx mcp`
- `FormatDuration` duplicated in CLI/MCP — extract to Core if third consumer appears
- Path traversal defense-in-depth: sanitize inputs AND validate resolved paths
- `WithToolsFromAssembly()` needs explicit `typeof(SomeToolClass).Assembly` for referenced libraries
- Per-invocation temp dirs (`helix-{id}-{Guid}`) to prevent cross-process file races

## Learnings

- Added `failureCategory` to the `hlx_status` MCP tool `[Description]` parenthetical field list (line 23 of HelixMcpTools.cs). The field was already returned in JSON output (line 46) but omitted from the description. This is a documentation-completeness fix per Dallas's decision that MCP descriptions should accurately list returned fields.

📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas

## Learnings (Core NuGet packaging, 2026-03-03)

- `Directory.Build.props` at repo root centralizes `<Version>` for all projects. Individual csproj files no longer carry `<Version>`.
- `src/HelixTool/.mcp/server.json` still has hardcoded version fields — the CI publish workflow validates these match the git tag. No automated sync exists.
- `HelixTool.Mcp.Tools` is a new class library (`src/HelixTool.Mcp.Tools/`) containing `HelixMcpTools.cs` and `McpToolResults.cs`. Both CLI and HTTP server reference it.
- MCP tools kept `namespace HelixTool.Core` after the move to avoid call-site changes. The project name (`Mcp.Tools`) differs from the namespace — this is intentional to minimize churn.
- `IsFileSearchDisabled` was promoted from `internal static` to `public static` on `HelixService` because `HelixMcpTools` (now in a separate assembly) needs it.
- `MatchesPattern` was already `public static` — no change needed. `MaxBatchSize` and `MaxSearchFileSizeBytes` remain `internal const` since only tests (via `InternalsVisibleTo`) use them from outside Core.
- 11 nested record types extracted from `HelixService` to `src/HelixTool.Core/Models/` as top-level types in `namespace HelixTool.Core`. Zero call-site changes needed because no callers used the `HelixService.RecordName` qualified form — all used `var` or type inference.
- `HelixTool.Core.csproj` now has `GenerateDocumentationFile=true`, which produces CS1591 warnings for all public members without XML doc comments. These are preexisting gaps, not new issues.
- CI publish workflow (`publish.yml`) packs both `lewing.helix.mcp` (tool) and `lewing.helix.core` (library) into a shared `nupkg/` output directory. Both are pushed to NuGet in a single step.

📌 Team update (2026-03-03): Core NuGet packaging complete — HelixTool.Core publishes as lewing.helix.core, MCP tools extracted to HelixTool.Mcp.Tools, version centralized in Directory.Build.props, CI packs both packages. Lambert: run full test suite (W7). Kane: add library consumption docs to README (W8). — decided by Ripley
📌 Team update (2026-03-03): Phase 1 auth UX approved by Dallas — `hlx login`/`logout`/`auth status`, `git credential` storage (Option A), `ChainedHelixTokenAccessor`. 7 work items: WI-1 ICredentialStore+GitCredentialStore, WI-2 ChainedHelixTokenAccessor, WI-3 DI wiring, WI-4 hlx login, WI-5 hlx logout, WI-6 hlx auth status, WI-7 error message update. — decided by Dallas
📌 Team update (2026-03-03): Helix auth UX analysis by Ash — Helix API uses opaque tokens only, no Entra. `git credential` recommended for storage. Env var must take precedence over stored credential. — decided by Ash

📌 Team update (2026-03-03): API review findings — decided by Dallas, Ash

## API Surface Review Implementation (2026-03-03)

Implemented 7 items from Dallas's API surface review:

1. **Sealed `HelixService` and `HelixException`** — added `sealed` keyword to prevent unintended inheritance.
2. **`List<T>` → `IReadOnlyList<T>` in all record types** — `JobSummary`, `BatchJobSummary`, `FileSearchResult`, `TrxParseResult`, `WorkItemDetail`, `LogSearchResult`, `FileContentSearchResult`, `LogMatch`. Internal code creates `List<T>` which implicitly converts. MCP tool `SearchMatch.Context` needed explicit `.ToList()` since the DTO keeps `List<string>?`.
3. **`MatchesPattern` made `internal`** — added `InternalsVisibleTo` for `HelixTool` and `HelixTool.Mcp.Tools` so CLI/MCP can still use it. Tests already had access.
4. **Renamed `JobSummary.Failed` → `FailedItems` and `.Passed` → `PassedItems`** — updated all consumers: `HelixService.cs`, `HelixMcpTools.cs`, `Program.cs`, and 5 test files.
5. **Fixed auth error message** — changed from CLI-specific "Run 'hlx login'" to library-appropriate "Provide a valid Helix access token via the HelixApiClient constructor or HELIX_ACCESS_TOKEN environment variable." across all 7 occurrences.
6. **Added XML doc comments** — `HelixIdResolver` class + `ResolveJobId` + `TryResolveJobAndWorkItem`, `HelixApiClient` constructor, `HelixService.MatchesPattern`, `FindBinlogsAsync`, `GetWorkItemDetailAsync`, `GetBatchStatusAsync`.
7. **Moved `FailureCategory` enum** to `Models/FailureCategory.cs` — extracted from `HelixService.cs`.
