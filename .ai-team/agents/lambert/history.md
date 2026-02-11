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

📌 Team update (2026-02-11): Architecture review filed — P0: DI/testability + error handling needed. Tests will need updating when DI is added to HelixService. — decided by Dallas
📌 Team update (2026-02-11): Documentation audit found 15 improvements needed across README, XML docs, llmstxt, MCP descriptions. — decided by Kane
