# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context (summarized through 2026-03-09)

> Older history archived to history-archive.md on 2026-03-09.

**Test infrastructure:** xUnit on net10.0 with NSubstitute 5.* for mocking. `MatchesPattern` exposed via `InternalsVisibleTo`. DI test pattern: shared `_mockApi`/`_svc` fields, per-test mock arrangement.

**Mock patterns:**
- IHelixApiClient projection interfaces: IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile
- NSubstitute gotchas: no nested `.Returns()`, `GetMetadataAsync` default is empty string (not null), lambda pattern `.Returns(_ => new MemoryStream(...))` for fresh streams
- `ThrowsAny<ArgumentException>` covers both `ArgumentException` and `ArgumentNullException`
- NSubstitute `.Returns<Stream>(_ => throw new Ex())` for exception testing (ThrowsAsync doesn't compile for Task<Stream>)

**Test suites (812 total through 2026-03-09):**
- Helix core: HelixIdResolver, MatchesPattern, HelixServiceDI, HelixMcpTools, ConsoleLogUrl, US-24 Download, US-30 Structured JSON, HelixIdResolverUrl, McpInputFlexibility, JsonOutput
- Cache: 56 tests (CachingHelixApiClient 26, SqliteCacheStore 18, CacheOptions 12) + 24 security + 4 concurrency gap
- HTTP/SSE auth: 46 tests (HelixTokenAccessor, ApiClientFactory, CacheStoreFactory, ConcurrencyTests, HttpContextTests)
- Download (US-6): 46 tests + Search (US-31): 17 tests + TRX (US-32): 15 tests + xUnit XML: 43 tests
- Security: 18 validation + 5 status filter
- AzDO: AzdoIdResolver (55), AzdoApiClient (51), AzdoSecurity (63), AzdoArtifacts (33), AzdoCli (22), HttpClientConfig (13), StreamingBehavior (18)
- AzDO Search: TextSearchHelper (20), AzdoSearchLog (21), AzdoSearchTimeline (19), SearchBuildLogAcrossSteps (21)

**Archived session summaries (2026-03-07 — 2026-03-08):**
- **AzDO Security (63 tests):** 5 categories covering SSRF, token leakage, cache isolation, command injection, input rejection. Patterns: `DoesNotContain` on error messages, host assertions, `DidNotReceive()` guards. Edge cases: query param comma concat, Uri UserInfo, traversal normalization, int overflow.
- **AzDO Artifacts (33 tests):** API/service/caching/MCP/edge-case coverage. Artifacts=ImmutableTtl (4h), TestAttachments=TestTtl (1h). CamelCase JSON via `root.GetProperty()`.
- **Proactive SEC-2/3/4 + CLI (53 tests):** HttpClientConfig (13, timeout/cancellation), StreamingBehavior (18, streams/disposal/special chars), AzdoCli (22, build/timeline/log/changes/tests/artifacts). AzdoBuildChange.Author is AzdoChangeAuthor not AzdoIdentityRef.
- **Search Log & TextSearchHelper (41 tests):** TextSearchHelper is pure static (5 params). AzdoService.SearchBuildLogAsync delegates to it via IsFileSearchDisabled guard. Env var test pattern: save/set/try-finally-restore.
- **PR #10 Fix:** `[Collection("FileSearchConfig")]` for env var mutation tests. Added formal `FileSearchConfigCollection.cs` definition. Convention: all HLX_DISABLE_FILE_SEARCH mutators use this collection.
- **Search Timeline (19 tests):** SearchTimelineAsync returns TimelineSearchResult (AzdoModels.cs). Default resultFilter="failed". FormatDuration: >1h "Xh Ym", >1m "Xm Ys", else "Xs". Null timeline → InvalidOperationException.

**Key patterns:**
- Each test class uses UNIQUE ValidJobId GUID for temp dir isolation
- Cache tests use temp dirs with GUID; sequential `.Returns()` for miss→hit
- Security tests: `Record.ExceptionAsync` + `Assert.IsNotType<ArgumentException>` for scheme acceptance
- `CacheOptions.GetEffectiveCacheRoot()` appends `/public` or `/cache-{hash}`
- Known race in `GetArtifactAsync`: `File.Exists` and `FileStream` not atomic — tolerate `FileNotFoundException`
- Write-to-temp-then-rename in `SetArtifactAsync` for atomic writes
- **FakeHttpMessageHandler** for AzdoApiClient: configurable StatusCode/ResponseContent + LastRequest capture
- AzdoApiClient errors: 404→null/empty, 401/403→auth hint, 500→body snippet (500 chars)
- CamelCase JSON verification: `root.GetProperty("camelCaseName").GetXxx()` avoids xUnit2002
- `[Collection("FileSearchConfig")]` required for all env var mutation tests (HLX_DISABLE_FILE_SEARCH)

📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). — decided by Dallas

## Learnings

Tests for AzdoMcpTools should assert against the model types' `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers exist for AzDO tools.

### SearchBuildLogAcrossSteps Tests (21 tests)
- **SearchBuildLogAcrossStepsTests** in `src/HelixTool.Tests/AzDO/SearchBuildLogAcrossStepsTests.cs` — 21 tests across 3 categories:
  - Unit Tests (T-1 through T-11): Empty build, minLogLines filtering, single failed match, ranking order verification (bucket 0→1→2→3), early termination (stoppedEarly=true), maxLogsToSearch limit (Received(5) assertion), orphan logs (Bucket 4), pattern not found, no-log-reference skip, context lines propagation, line ending normalization (\r\n and \r)
  - Validation Tests (V-1 through V-6): null/empty/whitespace pattern → ArgumentException, negative contextLines → ArgumentOutOfRangeException, zero maxMatches/maxLogsToSearch → ArgumentOutOfRangeException, negative minLogLines → ArgumentOutOfRangeException, IsFileSearchDisabled → InvalidOperationException
  - MCP Tests (M-1 through M-2): FileSearchDisabled → McpException (not InvalidOp), ArgumentException → McpException remapping
- **Key patterns used:**
  - Mock setup: `SetupTimeline()` / `SetupLogsList()` / `SetupLogContent()` helpers for clean arrange-act-assert
  - `AzdoBuildLogEntry` constructed with `Id` and `LineCount` to control ranking behavior
  - `[Collection("FileSearchConfig")]` for env var mutation tests (shared with existing search tests)
  - `Received(N)` assertions to verify download count limits
  - `GenerateLogContent()` helper creates N-line logs with optional error at specific line
- **Implementation detail discovered:**
  - `LogsSkipped` tracks eligible-but-not-searched logs (due to `maxLogsToSearch` cap), NOT logs filtered by `minLogLines`. Logs below `minLogLines` never enter the ranked queue, so `LogsSkipped=0` when all logs are too small.
  - Orphan logs get synthetic `AzdoTimelineRecord` with `Name = "log:{id}"` — test verifies by `LogId` not `StepName`
  - `stoppedEarly` is true when `remainingMatches <= 0` OR when `logsSearched >= maxLogsToSearch` but eligible logs remain
- **Total test count after cross-step search tests:** 812 tests (791 + 21 new).

📌 Team update (2026-03-09): Incremental log fetching full design spec merged — API range support (startLine/endLine), append-on-expire caching (two-key pattern), tail optimization. P0 CountLines off-by-one fixed. 864/864 tests passing. PR #13 opened. — decided by Dallas
📌 Team update (2026-03-09): P0 CountLines off-by-one fix — Split('\n') overcounts by 1 for trailing newline content. Fix: subtract 1 when content ends with '\n'. Affects delta-fetch startLine computation. — decided by Dallas
📌 Team update (2026-03-09): azdo_search_log_across_steps full design spec merged — 4-bucket ranking, early termination, GetBuildLogsListAsync, NormalizeAndSplit extraction. 19 estimated tests. — decided by Dallas
📌 Team update (2026-03-09): Timeline search result types live in Core — TimelineSearchMatch/TimelineSearchResult in AzdoModels.cs, not McpToolResults.cs. MCP tools return Core types directly. [JsonIgnore] on Record for flat JSON. — decided by Ripley

📌 Team update (2025-07-18): Perf review identified 17 allocation issues — decided by Ripley

📌 Team update (2026-03-09): Cache format changed to raw: prefix (backward-compatible sentinel), SearchConsoleLogAsync decoupled from disk download, StringHelpers.TailLines shared in Core — decided by Ripley

### Redundant test cleanup (PR #15)
- **Deleted `AzdoCliCommandTests.cs`** (22 tests → 19 removed, 3 rescued): The file was written proactively for CLI subcommands that were never implemented. 19 of 22 tests were near-identical duplicates of `AzdoServiceTests` — same mock setup, same assertions, just different variable names. Rescued 3 unique tests (artifact default/pattern filtering, changes with top parameter) into `AzdoServiceTests.cs`.
- **Removed 3 "ImplementsInterface" / "Constructor_Accepts" tests**: `HelixApiClientFactoryTests.ImplementsIHelixApiClientFactory`, `HttpContextHelixTokenAccessorTests.ImplementsIHelixTokenAccessor`, `HelixMcpToolsTests.Constructor_AcceptsHelixService`. These are compile-time guarantees — if the class doesn't implement the interface, the project won't build.
- **Merged 2 overlapping filter tests** in `HelixMcpToolsTests`: `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed` tested the same behavior (default filter is "failed"). Combined into one test that verifies both the default and explicit "failed" filter.
- **Pattern observed**: Proactive test files written before production code tends to produce near-duplicates of the actual test file once it lands. Worth catching during PR review.
- **Test count**: 864 → 844 (net -20 tests removed). All 844 pass.

📌 Team update (2026-03-09): CI profile analysis — 14 recommendations for MCP tool descriptions and error messages. Description changes (REC-1–6, 9, 13, 14) are text-only. Error message changes (REC-7, 8) in HelixService.cs need test verification. — decided by Ash

📌 Team update (2025-07-24): Test quality review — Delete AzdoCliCommandTests.cs (rescue 3 unique tests), remove 3 ImplementsInterface tests, merge 2 overlapping filter tests. Net -17 tests, zero coverage loss. Test guideline: prune proactive tests when real tests land. — decided by Dallas
