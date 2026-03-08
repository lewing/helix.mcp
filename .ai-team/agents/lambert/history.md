# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context (summarized through 2026-02-22)

**Test infrastructure:** xUnit on net10.0 with NSubstitute 5.* for mocking. `MatchesPattern` exposed via `InternalsVisibleTo`. DI test pattern: shared `_mockApi`/`_svc` fields, per-test mock arrangement.

**Mock patterns:**
- IHelixApiClient projection interfaces: IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile
- NSubstitute gotcha: helper methods with `.Returns()` cannot be nested inside another `.Returns()` call — configure inline
- NSubstitute gotcha: `GetMetadataAsync` default return is empty string (not null) — must explicitly return `Task.FromResult<string?>(null)` for cache miss
- NSubstitute lambda pattern: `.Returns(_ => new MemoryStream(...))` for fresh stream per call
- `ThrowsAny<ArgumentException>` covers both `ArgumentException` and `ArgumentNullException`

**Test suites (369 total through 2026-02-15):**
- HelixIdResolver tests, MatchesPattern tests, HelixServiceDI (19), HelixMcpTools (17), ConsoleLogUrl (3), US-24 Download validation (3), US-30 Structured JSON (3), HelixIdResolverUrl (7), McpInputFlexibility (4), JsonOutput (3)
- Cache tests (L-CACHE-1–10): 56 tests — CachingHelixApiClientTests (26), SqliteCacheStoreTests (18), CacheOptionsTests (12)
- Cache security: 24 tests — ValidatePathWithinRoot, SanitizePathSegment, SanitizeCacheKeySegment, integration tests
- HTTP/SSE auth (L-HTTP-1–5): 46 tests — HelixTokenAccessorTests (5), HelixApiClientFactoryTests (5), CacheStoreFactoryTests (8), SqliteCacheStoreConcurrencyTests (14), HttpContextHelixTokenAccessorTests (17)
- Download (US-6): 46 tests — DownloadFilesTests (27), DownloadFromUrlParsingTests (5), DownloadSanitizationTests (6), DownloadPatternTests (8)
- Search (US-31): 17 tests — SearchFileAsync input validation, config toggle, binary detection, pattern matching, context lines
- TRX parsing (US-32): 15 tests — ParseTrxResultsAsync validation, config toggle, mixed results, XXE prevention
- Status filter migration: 5 new tests — filter enum (failed|passed|all), case-insensitive, invalid value
- Security validation: 18 tests — URL scheme (10), batch size limit (5), MCP enforcement (2)
- Cache concurrency: 4 gap tests — stale row cleanup, eviction-during-read, concurrent eviction+write, same-key race

**Key patterns:**
- Each test class uses a UNIQUE ValidJobId GUID to avoid temp dir collisions during parallel xUnit
- Cache tests use temp dirs with GUID; sequential `.Returns()` for miss→hit flow
- Security tests: `Record.ExceptionAsync` + `Assert.IsNotType<ArgumentException>` for scheme acceptance
- URL scheme: schemeless strings throw `UriFormatException` before validation — accept both exception types
- `HelixService.MaxBatchSize` is `internal const int` — accessible via `InternalsVisibleTo`
- `CacheOptions.GetEffectiveCacheRoot()` appends `/public` or `/cache-{hash}` — use this, not `_tempDir`
- Known race in `GetArtifactAsync`: `File.Exists` and `FileStream` open not atomic — tolerate `FileNotFoundException`
- Write-to-temp-then-rename in `SetArtifactAsync` ensures atomic artifact writes
- **Older team updates (2026-02-11 to 2026-02-15):** 15 cross-agent updates received covering US-10/23, US-21, HTTP/SSE auth, multi-auth deferral, US-9/31, security fixes, hlx_find_files, remote search, status filter, temp dirs, CI validation. Archived to history-archive.md.
- **Older learnings (pre 2026-02-22):** ParseTrxResultsAsync auto-discovery (two error paths), SetupMultipleFiles mock pitfall (null content → NRE), MCP error surfacing pattern (Record.ExceptionAsync + message assertions). Archived to history-archive.md.

📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas

## Learnings

- **Pre-existing XXE test broken:** `ParseTrx_RejectsXxeDtdDeclaration` fails after production refactoring — `TryParseTestFile` swallows the `XmlException` from DTD prohibition and returns null. Filed to decisions inbox as a potential security regression.
- **Ripley's refactored ParseTrxResultsAsync (squad/4):** Production code now uses `IsTestResultFile()` with `TestResultFilePatterns` array (4 patterns: `*.trx`, `testResults.xml`, `*.testResults.xml.txt`, `testResults.xml.txt`). Auto-discovery fetches file list once, filters by patterns, downloads matching files. TRX parsed strictly, xUnit XML parsed best-effort. Both format results returned together (no TRX-preferred fallback). Error message includes work item name, searched patterns, and available file list.
- **XunitXmlParsingTests added (43 tests):** Covers `IsTestResultFile` pattern matching (13 inline data cases), `TestResultFilePatterns` verification, xUnit XML parsing (name, counts, failure messages, stack traces, skip reasons, duration formatting), multi-assembly aggregation, empty assembly, single `<assembly>` root, maxResults limiting, error truncation, error message clarity (work item name, patterns, available files, not generic), XXE prevention (best-effort and strict paths), mixed TRX+xUnit returns both.
- **MatchesTestResultPattern vs MatchesPattern:** `MatchesTestResultPattern` is a separate private method for `IsTestResultFile`; uses `fileName.Equals()` for non-wildcard patterns (exact match). Different from the `MatchesPattern` used in `DownloadFilesAsync` which uses `Contains()` for non-wildcard.
- **Total test count after squad/4:** 425 tests (up from 369).
- **AzDO test patterns established (AzdoIdResolverTests, AzdoTokenAccessorTests):** 55 tests in `src/HelixTool.Tests/AzDO/` namespace `HelixTool.Tests.AzDO`. Tests aligned to Ripley's actual API: `Resolve()` returns tuple + throws, `TryResolve()` returns bool. `AzCliAzdoTokenAccessor` returns `Task<string?>` (nullable — null means anonymous/no auth).
- **AzdoIdResolver has both Resolve() and TryResolve():** Unlike HelixIdResolver which only has `ResolveJobId()`, AzdoIdResolver has a throwing `Resolve()` and a safe `TryResolve()`. Tests cover both surfaces separately.
- **TryResolve out-param defaults:** On failure, out params are set to `DefaultOrg`/`DefaultProject`/`0` (not null). This differs from typical TryParse patterns where out params are default(T).
- **Negative/zero buildId edge case:** `AzdoIdResolver.Resolve()` accepts negative and zero buildIds (both plain integer and URL query param paths). `int.TryParse` succeeds for these. Documented with tests — may warrant a positivity check in production code.
- **AzCliAzdoTokenAccessor caching subtlety:** Env var `AZDO_TOKEN` is read on EVERY call (not cached). Only the az CLI subprocess result is cached via `_resolved` flag. This means changing the env var between calls is reflected immediately.
- **AzCliAzdoTokenAccessor thread safety:** The `_resolved` flag is not protected by a lock/semaphore. Concurrent first calls without an env var may spawn multiple az CLI processes. Tests document this but tolerate it since the race is benign (all return same result).
- **Az CLI tests take ~1s each:** Tests that fall through to az CLI path (no env var set) wait for the subprocess to fail/timeout. Use env var path tests for fast feedback.
- **Total test count after AzDO tests:** 480 tests (425 + 55 new AzDO tests).
- **FakeHttpMessageHandler pattern for AzdoApiClient:** Use a simple inner class extending `HttpMessageHandler` with configurable `StatusCode` and `ResponseContent` properties plus a `LastRequest` capture. NSubstitute mocks `IAzdoTokenAccessor`. This avoids needing a real HTTP server. Key: use `RequestUri.AbsoluteUri` (not `ToString()`) when asserting percent-encoded URL segments — `Uri.ToString()` unescapes `%20` back to spaces.
- **AzdoApiClient error handling contracts:** 404 → null for single-item gets (`GetAsync<T>`), empty list for list endpoints (`GetListAsync<T>`). 401/403 → `HttpRequestException` with "Authentication failed" + status code + "AZDO_TOKEN" + "az login" hint. 500 → `HttpRequestException` with status code + body snippet truncated at 500 chars with `…` suffix.
- **AzdoApiClient token accessor per-request:** `ApplyAuthAsync` calls `_tokenAccessor.GetAccessTokenAsync` once per HTTP request. Null/empty token → no `Authorization` header (anonymous access for public repos). Non-empty → `Bearer` scheme.
- **AzdoListResponse wrapper unwrapping:** `GetListAsync<T>` deserializes to `AzdoListResponse<T>` and returns `.Value ?? []`. Null value property gracefully returns empty list.
- **Total test count after AzdoApiClient tests:** 531 tests (480 + 51 new AzdoApiClientTests).

📌 Team update (2026-03-07): Auth UX Phase 1 approved — testing needed for ICredentialStore, ChainedHelixTokenAccessor, hlx login/logout/auth-status commands (WI-1 through WI-7). — decided by Dallas
📌 Team update (2026-03-07): AzDO architecture consolidated — Dallas architecture + Ripley foundation. AzDO lives in HelixTool.Core/AzDO/, mirrors Helix patterns. — decided by Dallas, Ripley
📌 Team update (2026-03-07): CacheStoreFactory uses Lazy<T> wrapping — CacheStoreFactoryTests now pass reliably including ParallelCallsReturnSameInstance. — decided by Ripley

📌 Team update (2026-03-07): AzDO caching strategy implemented — CachingAzdoApiClient with dynamic TTL by build status, azdo: key prefix, reuses ICacheStore infrastructure. Tests needed. — decided by Ripley

📌 Team update (2026-03-07): AzdoService method signatures defined — 7 methods with URL resolution, GetBuildSummaryAsync flattened record, tailLines server-side slicing. Tests needed. — decided by Ripley

### 2026-03-07: Decision — AzdoMcpTools returns model types directly
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
