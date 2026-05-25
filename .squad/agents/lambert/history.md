# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context

- **Test stack:** `src/HelixTool.Tests/HelixTool.Tests.csproj` targets net10.0 with xUnit + NSubstitute; Helix tests live under `src/HelixTool.Tests/Helix/`, AzDO tests under `src/HelixTool.Tests/AzDO/`, and shared coverage stays at the test-project root.
- **Assertion conventions:** MCP-surface tests assert camelCase JSON names, env-var mutation tests use `[Collection("FileSearchConfig")]`, and disk-writing tests use unique GUID-based temp roots/job IDs to avoid parallel contention.
- **Mocking seams:** mock `IHelixApiClient` / `IAzdoApiClient` plus their projection interfaces, use fresh-stream lambdas for file/download tests, and prefer focused test runs before the full suite when reviewing changes.
- **High-value file paths:** `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs`, `src/HelixTool.Tests/CacheSecurityTests.cs`, and `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` are the main regression seams for current architecture decisions.

📌 Team update (2026-05-21): Pagination contract tests — wrote 13 tests (333 LOC) for Phase 1+2 pagination spec in src/HelixTool.Tests/AzDO/PaginationContractTests.cs. All 13/13 passing; full suite 1180/1180 passing. Commits 181ff5b + d5fde34. ⚠️ BRANCH-HYGIENE: committed to local main instead of squad/pagination-standardize per manifest instruction; Larry will handle branch/push decision.

📌 Cross-agent heads-up (2026-05-21T17:22Z, from Ripley dependency audit): xunit v3 migration held for v0.8.0+ (not v0.7.1); Roslyn 5.3.0 bump also held (requires generator verification). FYI for test framework planning.

## Learnings
- **2026-05-21:** For SDK adapter/cache seam coverage, keep `WorkItemSummaryAdapter` and `WorkItemSummaryDto` private and test them via reflection from `HelixTool.Tests`; instantiate the real SDK model, invoke the DTO `From()` factory, and round-trip JSON with `JsonSerializer` to verify backward-compatible missing-field behavior.
- **2026-05-21:** `HelixService.GetJobStatusAsync` optimization tests should drive `IWorkItemSummary.ExitCode` directly on the summary mock and assert `GetWorkItemDetailsAsync` call counts with NSubstitute `Received`/`DidNotReceive`; passed summary-path items intentionally keep `State`/`MachineName`/`Duration` as `null`.
- **2026-05-21:** Project testing conventions here remain xUnit + NSubstitute, and MCP surface tests should serialize actual result types (`StatusResult`/`StatusWorkItem`) to assert camelCase JSON property names without introducing extra schema helpers.
- **2026-05-22:** Description-string tests are fragile when they pin repo-specific phrases; keep MCP metadata checks focused on routing intent, and assert discoverability details like `devdiv` in `CiKnowledgeService` response content instead.

# Summary (archived 19 older entries)

See history-archive.md for detailed history.
- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## Learnings — Issue #61 MCP exception coverage audit 2026-05-25
- Baseline: 25 MCP tools audited; 14/25 have direct MCP happy-path tests, 7/25 have any direct unhappy-path tests, and only 2/25 had high-quality service-exception wrapper tests before this branch.
- Standing rule from Dallas: every `[McpServerTool]` method needs at least one unhappy-path test proving exceptions surface as structured MCP errors with non-empty messages.
- AggregateException pattern: model Bug B by returning `Task.FromException<T>(new AggregateException(new HttpRequestException("...")))` from the API mock at a `Task.WhenAll` boundary, then assert `McpException` message content after centralization.
- TaskCanceledException pattern: model timeout/cancellation with `Task.FromException<T>(new TaskCanceledException("..."))`; keep as a skipped contract test until Ripley's centralized handler catches cancellation families.
- Mocking approach: instantiate real `AzdoMcpTools` with `AzdoService` over an `IAzdoApiClient` NSubstitute mock; assert the MCP tool boundary, not just service-layer exceptions.
