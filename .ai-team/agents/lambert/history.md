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

**Archive refresh (2026-03-13):** Detailed PR #15 cleanup, 9-repo `CiKnowledgeServiceTests` expansion, and the linked 2026-03-10 CI-knowledge update moved to `history-archive.md`. Durable takeaways: delete proactive duplicate tests once real coverage lands, and use broad Theory matrices for static CI-profile data.

- Tests for AzdoMcpTools should assert against `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers for AzDO tools.
- **SearchBuildLogAcrossSteps (21 tests):** 3 categories — Unit (T-1–T-11: ranking, early termination, orphans, normalization), Validation (V-1–V-6: argument checks), MCP (M-1–M-2: exception remapping). Key: `SetupTimeline()`/`SetupLogsList()`/`SetupLogContent()` helpers. `LogsSkipped` tracks cap-limited logs, not minLines-filtered. `stoppedEarly` = budget exhausted OR eligible logs remain. Test count after: 812.
- Discoverability copy has two strong regression seams: reflect `DescriptionAttribute` text on MCP tool methods to lock routing promises, and assert rendered CI-guide section ordering with `IndexOf`/section slicing so “use AzDO first” guidance stays visible before pattern inventories.
- `helix_test_results` false-confidence regressions are best caught through MCP-layer exception assertions in `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`; high-value cases are no structured-result files, empty uploads, and crash-artifact uploads, all of which should route callers toward `azdo_test_runs`/`azdo_test_results`, `helix_search_log`, and `helix_ci_guide`.
- Key file paths for discoverability coverage: `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs` now holds MCP description + fallback-routing assertions, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs` locks guide wording/order for aspnetcore/runtime, `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` contains the live tool descriptions, and `src/HelixTool.Core/CiKnowledgeService.cs` renders the repo-specific CI guide text.
- User preference reinforced again: for review-driven test changes, run focused tests first to debug wording/assertion mismatches quickly, then run the full `src/HelixTool.Tests/HelixTool.Tests.csproj` suite before concluding the regression coverage is complete.

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

- Cache path-boundary hardening is now regression-covered in `src/HelixTool.Tests/CacheSecurityTests.cs`; the important edge case is a sibling path that differs only by casing (`test-root` vs `TEST-ROOT`), which must be rejected to avoid false containment on case-sensitive filesystems.
- `HelixService` no longer supports a null/implicit `HttpClient`; `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` covers both `ArgumentNullException` branches, and focused/full-suite runs confirmed current CLI/MCP construction sites already inject `IHttpClientFactory` clients.
- Key file paths for this review: `src/HelixTool.Core/Cache/CacheSecurity.cs` contains the Ordinal child-boundary check, `src/HelixTool.Core/Helix/HelixService.cs` owns the strict constructor requirement, `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` wire named `HelixDownload` clients, and `src/HelixTool.Tests/HttpClientConfigurationTests.cs` exercises timeout/cancellation behavior with explicit `HttpClient` injection.
- User preference reinforced: validate review fixes with targeted tests first, then run the full `src/HelixTool.Tests/HelixTool.Tests.csproj` suite before concluding coverage is sufficient.

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash

📌 Team update (2026-03-10): Discoverability routing decisions merged — keep the current tool surface, route repo-specific workflow selection through `helix_ci_guide(repo)`, treat `helix_test_results` as structured Helix-hosted parsing rather than a universal first step, and keep `helix_search_log`/docs/help guidance synchronized across surfaces. — decided by Dallas, Kane, Ripley

### Idempotent annotation sweep (2025-07-25)
- **What:** Added `Idempotent = true` to all 22 `[McpServerTool]` attributes that had `ReadOnly = true` across 3 files: `AzdoMcpTools.cs` (12 tools), `HelixMcpTools.cs` (9 tools), `CiKnowledgeTool.cs` (1 tool).
- **Why:** MCP best practices (Anthropic, OpenAI, AWS, arxiv 2602.14878) recommend safety annotations on all tools. `Idempotent = true` signals to clients that these tools are safe to retry and cache, complementing the existing `ReadOnly = true`.
- **Verification:** `helix_download` and `helix_download_url` correctly have `Idempotent = true` WITHOUT `ReadOnly = true` — they write files to disk, so they're idempotent but not read-only. No tools were found missing `ReadOnly = true`.
- **Key files:** `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs`, `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs`, `src/HelixTool.Mcp.Tools/CiKnowledgeTool.cs`
- **Test count:** 1047 (1046 pass, 1 pre-existing flaky: `AzdoTokenAccessorTests.ConcurrentCallsWithoutEnvVar`).

📌 Team update (2026-03-13): Scribe merged decision inbox items covering `dotnet` as the VMR profile key, `helix_search`/`helix_parse_uploaded_trx` naming, tighter MCP descriptions, and explicit truncation metadata (`truncated`, `LimitedResults<T>`). README/docs now also call out `ci://profiles` resources and idempotent annotations.
