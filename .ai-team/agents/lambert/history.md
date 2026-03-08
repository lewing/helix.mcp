# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context (summarized through 2026-03-08)

**Test infrastructure:** xUnit on net10.0 with NSubstitute 5.* for mocking. `MatchesPattern` exposed via `InternalsVisibleTo`. DI test pattern: shared `_mockApi`/`_svc` fields, per-test mock arrangement.

**Mock patterns:**
- IHelixApiClient projection interfaces: IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile
- NSubstitute gotchas: no nested `.Returns()`, `GetMetadataAsync` default is empty string (not null), lambda pattern `.Returns(_ => new MemoryStream(...))` for fresh streams
- `ThrowsAny<ArgumentException>` covers both `ArgumentException` and `ArgumentNullException`

**Test suites (753 total through 2026-03-08):**
- Helix core: HelixIdResolver, MatchesPattern, HelixServiceDI, HelixMcpTools, ConsoleLogUrl, US-24 Download, US-30 Structured JSON, HelixIdResolverUrl, McpInputFlexibility, JsonOutput
- Cache: 56 tests (CachingHelixApiClient 26, SqliteCacheStore 18, CacheOptions 12) + 24 security + 4 concurrency gap
- HTTP/SSE auth: 46 tests (HelixTokenAccessor, ApiClientFactory, CacheStoreFactory, ConcurrencyTests, HttpContextTests)
- Download (US-6): 46 tests + Search (US-31): 17 tests + TRX (US-32): 15 tests + xUnit XML: 43 tests
- Security: 18 validation + 5 status filter
- AzDO: AzdoIdResolver (55), AzdoApiClient (51), AzdoSecurity (63), AzdoArtifacts (33), AzdoCli (22), HttpClientConfig (13), StreamingBehavior (18)

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
- NSubstitute `.Returns<Stream>(_ => throw new Ex())` for exception testing

📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). — decided by Dallas

## Learnings

Tests for AzdoMcpTools should assert against the model types' `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers exist for AzDO tools.

### 2026-03-07: AzDO Security Tests (63 tests)
- **AzdoSecurityTests** in `src/HelixTool.Tests/AzDO/AzdoSecurityTests.cs` — 63 tests across 5 categories:
  - AzdoIdResolver malicious URL inputs (embedded credentials, non-AzDO hosts/SSRF, path traversal, query injection, unicode, long URLs, scheme attacks, integer overflow)
  - AzCliAzdoTokenAccessor command injection safety (no shell execute, env var passthrough, CLI failure resilience)
  - AzdoApiClient request construction (SSRF prevention via host assertion, token leakage in errors, special chars in org/project, null/empty token)
  - CachingAzdoApiClient cache isolation (org/project key separation, azdo: prefix, no tokens in cached data, cache key poisoning via path traversal/colons, disabled cache)
  - AzdoService end-to-end (malicious URLs rejected before API call, null/empty/invalid inputs)
- **Security test patterns established:**
  - Token leakage: `Assert.DoesNotContain(tokenValue, ex.Message)` on all error paths
  - SSRF: `Assert.StartsWith("https://dev.azure.com/", url)` and host assertion
  - Cache isolation: verify `SetMetadataAsync` called with different keys for different org/project
  - No-API-call guard: `DidNotReceive()` assertion after input rejection
- **Edge cases discovered:**
  - `HttpUtility.ParseQueryString` concatenates duplicate query params with commas → `int.TryParse` rejects safely (not first-value)
  - `Uri` parses embedded credentials as UserInfo; resolver only reads Host/Path — safe by design
  - `Uri` normalizes `../../` in path; `CacheSecurity.SanitizeCacheKeySegment` strips remaining traversal chars
  - `long.MaxValue` as buildId fails `int.TryParse` → `ArgumentException` (safe overflow handling)
  - Newlines in AZDO_TOKEN env var returned verbatim — potential header injection risk (mitigated by `AuthenticationHeaderValue`)
- **Total test count after AzDO security tests:** 594 tests (531 + 63 new).

📌 Team update (2026-03-08): AzDO security review complete — 6 findings (1 Medium, 3 Low, 2 Info). Security test conventions consolidated into AzDO test patterns decision. 667 total tests. — decided by Dallas

### 2026-03-08: AzDO Artifact & Attachment Tests (33 tests)
- **AzdoArtifactTests** in `src/HelixTool.Tests/AzDO/AzdoArtifactTests.cs` — 33 tests across 5 categories:
  - API Client: GetBuildArtifactsAsync (valid build, empty list), GetTestAttachmentsAsync (valid result, empty list)
  - Service Layer: URL resolution for artifacts, plain ID defaults, top parameter limiting, top exceeds count
  - Caching: cache miss/hit for both artifacts and attachments, TTL assertions (4h immutable for artifacts, 1h test TTL for attachments), azdo: prefix, disabled cache pass-through
  - MCP Tools: azdo_artifacts (list, URL acceptance, empty), azdo_test_attachments (list, top, custom org/project, empty)
  - Edge Cases: invalid buildId, empty string, null resource, large file size (2GB), createdDate, JSON round-trip
- **CamelCase JSON verification pattern:** Use `root.GetProperty("camelCaseName").GetXxx()` to assert both property existence AND value — avoids xUnit2002 warnings on JsonElement (struct) with `Assert.NotNull()`.
- **TestAttachments top parameter:** `AzdoService.GetTestAttachmentsAsync` applies top limiting via `results.Take(top).ToList()` after API call (API doesn't support server-side limiting for test attachments).
- **Artifacts use ImmutableTtl (4h):** Artifacts are immutable once published, so caching uses the same 4h TTL as build logs and changes.
- **TestAttachments use TestTtl (1h):** Consistent with test runs/results caching.
- **Total test count after artifact tests:** 700 tests (667 + 33 new).

📌 Team update (2026-03-08): AzDO context-limiting defaults — all AzDO MCP tools now have safe output-size defaults (tailLines=500, filter="failed", top=20/50/200). All overridable. 667 tests pass. — decided by Ripley

### 2026-03-08: Proactive Tests for SEC-2/3/4 and AzDO CLI (53 tests)
- **HttpClientConfigurationTests** in `src/HelixTool.Tests/HttpClientConfigurationTests.cs` — 13 tests:
  - AzdoApiClient constructor null-guard validation
  - HttpClient timeout configuration validation (reasonable range 30s–10min, not infinite)
  - Timeout vs. cancellation behavior: timeout wraps in HelixException, user cancellation rethrows directly
  - AzdoApiClient mid-request timeout throws TaskCanceledException
  - IHttpClientFactory readiness: validates factory-created HttpClient pattern
  - DelayingHttpMessageHandler helper for timeout simulation
- **StreamingBehaviorTests** in `src/HelixTool.Tests/StreamingBehaviorTests.cs` — 18 tests:
  - Empty stream → empty string; empty stream with tailLines → empty string
  - Large content (1000 lines) reads fully; large content with tailLines returns last N
  - TailLines edge cases: exceeds line count, single line, single-line-no-newlines
  - Connection error handling: HttpError → HelixException, NotFound → HelixException, Unauthorized → mentions login
  - Stream disposal verified via TrackingMemoryStream
  - Special characters: ANSI escape codes, UTF-8 emoji preserved
  - Input validation: null/empty/whitespace jobId and workItem
  - Moderate-size log (~50KB) reads to string correctly
- **AzdoCliCommandTests** in `src/HelixTool.Tests/AzDO/AzdoCliCommandTests.cs` — 22 tests:
  - Build summary: plain ID defaults, URL resolution, not-found, invalid buildId, duration calculation, in-progress null duration
  - Timeline: valid returns timeline, null returns null
  - Build log: content, tailLines, not-found
  - Build changes: returns list, top parameter passed through
  - Test runs/results: list returns, results returned
  - List builds: filter passthrough
  - Artifacts: default pattern, pattern filter, top limiting
- **NSubstitute `.Returns<Stream>` pattern for exceptions:** `ThrowsAsync` doesn't compile for `Task<Stream>` — use `.Returns<Stream>(_ => throw new Ex())` instead.
- **Init-only properties in test helpers:** AzdoBuild uses `init;` setters — must set StartTime/FinishTime via object initializer parameters, not post-construction assignment.
- **AzdoChangeAuthor vs AzdoIdentityRef:** `AzdoBuildChange.Author` is `AzdoChangeAuthor`, not `AzdoIdentityRef`. Separate type with only `DisplayName`.
- **Total test count after proactive tests:** 753 tests (700 + 53 new).

### 2026-03-08: AzDO Search Log & TextSearchHelper Tests (41 tests)
- **TextSearchHelperTests** in `src/HelixTool.Tests/TextSearchHelperTests.cs` — 20 tests:
  - Basic matching: single match, multiple matches, correct line numbers (1-based)
  - Context lines: zero (null context), 1-line, 3-line, clamped at start/end of content
  - Case insensitivity: Theory with 4 case variants (error/ERROR/Error/eRrOr)
  - Edge cases: no matches, empty content, empty pattern (matches all lines)
  - Max matches: limit to 2 of 10, limit to 1 of 3
  - Overlapping context: close matches produce independent context windows
  - Large content: 10K lines with single match at line 5000, with and without context
  - Identifier passthrough: verifies WorkItem field in result
  - Special characters: parentheses, brackets — literal string match, not regex
- **AzdoSearchLogTests** in `src/HelixTool.Tests/AzDO/AzdoSearchLogTests.cs` — 21 tests:
  - Happy path: single/multiple error matches with correct line numbers
  - No matches: returns empty matches list with correct TotalLines
  - Context lines: zero (null context), 5-line extended context with correct boundaries
  - Max matches: limits output to first N matches
  - Large log: 10K lines, single match at line 5000
  - Special characters: parentheses and brackets matched literally (no regex)
  - URL resolution: dev.azure.com URL resolves org/project correctly, Received() assertion
  - Case insensitivity: uppercase pattern matches lowercase log content
  - Input validation: null/empty/whitespace pattern throws ArgumentException
  - Null log content: throws InvalidOperationException
  - Search disabled: HLX_DISABLE_FILE_SEARCH=true throws InvalidOperationException (env var test with cleanup)
  - Result identifier: contains logId in WorkItem field (Ripley uses `log:{logId}`)
- **TextSearchHelper is a pure static class** — 5 required params (identifier, lines, pattern, contextLines, maxMatches), no defaults. Tests use DefaultContext=0 and DefaultMaxMatches=50 constants.
- **AzdoService.SearchBuildLogAsync** delegates to TextSearchHelper after fetching log via GetBuildLogAsync. Uses `HelixService.IsFileSearchDisabled` guard (shared with Helix search).
- **Env var test pattern:** save original, set, try/finally restore — for `HLX_DISABLE_FILE_SEARCH` testing.
- **Total test count after search log tests:** 791 tests (750 baseline + 41 new).
📌 Team update (2026-03-08): Search types extracted to top-level namespace — `LogMatch`, `LogSearchResult`, `FileContentSearchResult` moved from `HelixService` nested records to `HelixTool.Core` namespace in `TextSearchHelper.cs`. — decided by Ripley

### PR #10 Review Fix: Test Parallelism for Env Var Tests
- **Problem:** `AzdoSearchLogTests.SearchBuildLog_WhenSearchDisabled_ThrowsInvalidOperation` mutates the process-wide `HLX_DISABLE_FILE_SEARCH` env var, which can cause flaky failures when xUnit runs tests in parallel.
- **Fix:** Added `[Collection("FileSearchConfig")]` to `AzdoSearchLogTests` to match the existing pattern used by `TrxParsingTests`, `SearchFileTests`, and `XunitXmlParsingTests`. Classes in the same collection run sequentially, preventing concurrent env var access.
- **Also added:** `FileSearchConfigCollection.cs` — a `[CollectionDefinition("FileSearchConfig", DisableParallelization = true)]` class. The three existing test classes used `[Collection("FileSearchConfig")]` without a formal definition; adding the definition is best practice and makes the intent explicit.
- **Env var cleanup was already correct:** The test uses save-original / try / finally-restore pattern (lines 239–249).
- **Convention:** All test classes that mutate `HLX_DISABLE_FILE_SEARCH` must use `[Collection("FileSearchConfig")]`.
