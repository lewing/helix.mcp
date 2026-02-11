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

📌 Team update (2026-02-11): Architecture review filed — P0: DI/testability + error handling needed. Tests will need updating when DI is added to HelixService. — decided by Dallas
📌 Team update (2026-02-11): Documentation audit found 15 improvements needed across README, XML docs, llmstxt, MCP descriptions. — decided by Kane
📌 Team update (2026-02-11): Caching strategy proposed — HelixService gets optional HelixCache parameter; tests will need to account for cache behavior. — decided by Dallas
📌 Team update (2026-02-11): Requirements backlog formalized — 30 user stories. P0: US-12 and US-13 must land before feature work. — decided by Ash
📌 Team update (2026-02-11): P0 Foundation design decisions D1–D10 merged — IHelixApiClient is the only mock boundary, add NSubstitute, write tests for HelixService with mocked IHelixApiClient. See decisions.md. — decided by Dallas
