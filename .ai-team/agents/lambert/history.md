# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **No test project exists yet** — needs to be created from scratch
- **Testable units identified:**
  - HelixIdResolver.ResolveJobId — pure function, easy to test (GUID parsing, URL extraction)
  - HelixService.MatchesPattern — private static, but logic is testable
  - HelixService methods — need mocking of HelixApi dependency
- **HelixApi is `new()`'d directly** — no DI, will need refactoring or test doubles for service tests

## Learnings
- Test project lives at `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core
- Solution file `HelixTool.slnx` uses `<Folder Name="/test/">` for test projects
- `MatchesPattern` changed from `private static` to `internal static` in HelixService.cs, exposed via `<InternalsVisibleTo Include="HelixTool.Tests" />` in HelixTool.Core.csproj
- xUnit on net10.0 needs explicit `using Xunit;` — implicit usings don't cover it
- HelixIdResolver.ResolveJobId handles: bare GUIDs (with/without dashes, any case), Helix URLs with/without API version prefix, non-GUID passthrough, empty string, and edge cases like "jobs" as last segment
- MatchesPattern: `*` = match all, `*.ext` = suffix match (case-insensitive), anything else = substring match (case-insensitive)
- xUnit packages used: `Microsoft.NET.Test.Sdk 17.*`, `xunit 2.*`, `xunit.runner.visualstudio 2.*`
- NSubstitute 5.* added for mocking `IHelixApiClient` — uses `Substitute.For<T>()` and `.Returns()` / `.ThrowsAsync()` via `NSubstitute.ExceptionExtensions`
- DI test pattern: constructor creates shared `_mockApi` and `_svc` fields — each test arranges its own mock returns
- Mock return types from D1 spec: `IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile` — all NSubstitute proxies with `.Returns()` on properties
- Cancellation vs timeout distinction in tests: `TaskCanceledException` with default token = timeout; `TaskCanceledException` with matching `cts.Token` = real cancellation. Tests cover both per D6
- `ThrowsAny<ArgumentException>` used for input validation tests — covers both `ArgumentException` and `ArgumentNullException` subclass
- Proactive parallel test writing works: write tests against design spec, accept compile failures from missing types as expected. Only 1 unique error (`IHelixApiClient` not found) — rest cascades from that
- HelixIdResolver tests split into "happy path keeps working" (GUID/URL extraction) and "breaking change: invalid input throws" (D7). Old pass-through tests replaced with `Assert.Throws<ArgumentException>` equivalents

📌 Session 2026-02-11-p0-implementation: P0 foundation complete. 19 DI/error handling tests + 7 updated HelixIdResolver tests all passing (38 total). NSubstitute validated as mock framework. Proactive parallel test writing worked — tests compiled and passed on first run with Ripley's code.

📌 Team update (2026-02-11-p1-features): Ripley implemented US-1 (positional args) and US-20 (rich status). `IWorkItemDetails` expanded with State, MachineName, Started, Finished. `WorkItemResult` record updated. Mock setup in `HelixServiceDITests` updated for new fields. 38/38 tests pass.
📌 Team update (2026-02-12): Kane completed docs fixes — XML doc comments on all P0 public types (IHelixApiClient, HelixApiClient, HelixException, HelixService + all records). llmstxt + README now authoritative docs.

📌 Team update (2026-02-11): Architecture review filed — P0: DI/testability + error handling needed. Tests will need updating when DI is added to HelixService. — decided by Dallas
📌 Team update (2026-02-11): Documentation audit found 15 improvements needed across README, XML docs, llmstxt, MCP descriptions. — decided by Kane
📌 Team update (2026-02-11): Caching strategy proposed — HelixService gets optional HelixCache parameter; tests will need to account for cache behavior. — decided by Dallas
📌 Team update (2026-02-11): Requirements backlog formalized — 30 user stories. P0: US-12 and US-13 must land before feature work. — decided by Ash
📌 Team update (2026-02-11): P0 Foundation design decisions D1–D10 merged — IHelixApiClient is the only mock boundary, add NSubstitute, write tests for HelixService with mocked IHelixApiClient. See decisions.md. — decided by Dallas

📌 Session 2025-07-18-mcp-tests: Added 17 HelixMcpTools tests (55 total). Tests cover: Status JSON structure (4 tests), FormatDuration through Status output (6 tests covering seconds/minutes/hours/null), Files with tags (2 tests), FindBinlogs scan results (2 tests), Download error JSON (2 tests), constructor (1 test). Pattern: mock IHelixApiClient → construct HelixService → construct HelixMcpTools → call MCP tool method → parse JSON output → assert structure. Added ProjectReference to HelixTool.Mcp in test csproj.
- HelixTool.Mcp uses `namespace HelixTool` (via `<RootNamespace>HelixTool</RootNamespace>`) so HelixMcpTools is in same namespace as HelixService — no extra `using` needed
- FormatDuration is private static, only testable through Status output — tested all 6 branches (seconds, m+s, exact minutes, h+m, exact hours, null)
- HelixTool.Mcp.csproj uses `Microsoft.NET.Sdk.Web` — may cause issues if a running MCP server process locks bin output; killed PID 14288 during test run
- Download error path: when `DownloadFilesAsync` returns empty list, `Download` returns `{error: "No files matching '...' found."}` — tested with pattern mismatch and empty file list

📌 Team update (2026-02-11): US-4 auth design approved — HELIX_ACCESS_TOKEN env var, optional token on HelixApiClient constructor. No IHelixApiClient changes, no test impact. — decided by Dallas
📌 Team update (2026-02-11): Stdio MCP implemented as `hlx mcp` subcommand. HelixMcpTools.cs duplicated in CLI project. If removed from HelixTool.Mcp, test ProjectReference must change. — decided by Dallas/Ripley
📌 Team update (2026-02-11): Ripley's stdio MCP implementation creates separate DI container for mcp command. HelixMcpTools.cs now in both projects. 55/55 tests pass. — decided by Ripley
