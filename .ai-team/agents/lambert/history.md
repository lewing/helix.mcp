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

📌 Team updates (2026-03-01 – 2026-03-09 summary): UseStructuredContent refactor approved (Dallas). Incremental log fetching spec + P0 CountLines fix — 864 tests (Dallas). azdo_search_log_across_steps spec (Dallas). Timeline types in Core (Ripley). Perf review — 17 allocations (Ripley). Cache raw: prefix + StringHelpers shared (Ripley).

## Learnings

- Tests for AzdoMcpTools should assert against `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers for AzDO tools.
- **SearchBuildLogAcrossSteps (21 tests):** 3 categories — Unit (T-1–T-11: ranking, early termination, orphans, normalization), Validation (V-1–V-6: argument checks), MCP (M-1–M-2: exception remapping). Key: `SetupTimeline()`/`SetupLogsList()`/`SetupLogContent()` helpers. `LogsSkipped` tracks cap-limited logs, not minLines-filtered. `stoppedEarly` = budget exhausted OR eligible logs remain. Test count after: 812.

### Redundant test cleanup (PR #15)
- **Deleted `AzdoCliCommandTests.cs`** (22 tests → 19 removed, 3 rescued): The file was written proactively for CLI subcommands that were never implemented. 19 of 22 tests were near-identical duplicates of `AzdoServiceTests` — same mock setup, same assertions, just different variable names. Rescued 3 unique tests (artifact default/pattern filtering, changes with top parameter) into `AzdoServiceTests.cs`.
- **Removed 3 "ImplementsInterface" / "Constructor_Accepts" tests**: `HelixApiClientFactoryTests.ImplementsIHelixApiClientFactory`, `HttpContextHelixTokenAccessorTests.ImplementsIHelixTokenAccessor`, `HelixMcpToolsTests.Constructor_AcceptsHelixService`. These are compile-time guarantees — if the class doesn't implement the interface, the project won't build.
- **Merged 2 overlapping filter tests** in `HelixMcpToolsTests`: `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed` tested the same behavior (default filter is "failed"). Combined into one test that verifies both the default and explicit "failed" filter.
- **Pattern observed**: Proactive test files written before production code tends to produce near-duplicates of the actual test file once it lands. Worth catching during PR review.
- **Test count**: 864 → 844 (net -20 tests removed). All 844 pass.

📌 Team updates (2026-03-09 – 2026-03-10 summary): CI profile analysis — 14 tool description/error message recommendations (Ash). Test quality review — net -17 tests, zero coverage loss, prune proactive tests when real tests land (Dallas). CiKnowledgeService expanded to 9 repos, 5 tool descriptions updated (Ripley).

### CiKnowledgeService enrichment tests (2025-07-25)
- **Expanded `CiKnowledgeServiceTests.cs`** from ~23 tests (14 [Fact] + 9 [Theory] cases) to 57 test methods with 159 InlineData entries covering all 9 repos.
- **New repo coverage:** maui, macios, android — profile lookup by short name, full path (`dotnet/maui`, `xamarin/macios`, `xamarin/android`), case insensitivity (`MAUI`, `Macios`, `ANDROID`).
- **Enriched property tests (all 9 repos via Theory):** TestFramework, TestRunnerModel, WorkItemNamingPattern, KnownGotchas, RecommendedInvestigationOrder, PipelineNames, UploadedFiles, CommonFailureCategories — all verified non-empty.
- **OrgProject correctness:** devdiv/DevDiv for macios + android, dnceng-public/public for the other 7.
- **UsesHelix matrix:** Theory covering all 9 repos with expected bool values.
- **ExitCodeMeanings split:** non-empty for Helix repos + vmr, empty for macios/android (no Helix = no exit codes).
- **Edge cases:** maui has 3 pipelines verified, macios/android KnownGotchas warn about devdiv, android mentions fork PRs, roslyn has empty HelixTaskNames, efcore has lowercase 'Send job to helix'.
- **FormatProfile rendering:** KnownGotchas section renders for new repos, ExitCodes section omitted when empty, OrgProject/TestFramework rendered, Maui guide lists all 3 pipelines.
- **GetOverview:** 9 repos in table, devdiv warning present, OrgProject column has both orgs, Quick Reference table format verified.
- **DisplayName correctness:** xamarin/macios, dotnet/android, dotnet/dotnet (VMR) — verifies non-dotnet org display names.
- **Key patterns:** [Theory] with all 9 repos for property-existence tests, [Fact] for repo-specific behavioral assertions. No mocking needed — CiKnowledgeService is pure static data.
- **Test count:** 1038 total (was ~1020 before enrichment, net +~18 test methods but many more test cases via InlineData).

📌 Team update (2026-03-10): CiKnowledgeService expanded from 6 stubs to 9 full repo profiles with 9 new properties. 5 MCP tool descriptions updated with repo-specific CI knowledge. Future test work should cover the enriched CiRepoProfile fields. — decided by Ripley
