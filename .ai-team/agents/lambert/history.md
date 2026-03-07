# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context (summarized through 2026-02-15)

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

📌 Team update (2026-02-11): US-10/US-23 implemented — decided by Ripley
📌 Team update (2026-02-11): US-21 failure categorization — decided by Ripley
📌 Team update (2026-02-13): HTTP/SSE multi-client auth — decided by Dallas
📌 Team update (2026-02-13): Multi-auth deferred — decided by Dallas
📌 Team update (2026-02-13): US-9 script removability — decided by Ash
📌 Team update (2026-02-13): Requirements audit — audited by Ash
📌 Team update (2026-02-13): MCP API design review — reviewed by Dallas
📌 Team update (2026-02-13): hlx_find_files generalization — decided by Dallas
📌 Team update (2026-02-13): P1 security fixes E1+D1 — decided by Ripley
📌 Team update (2026-02-13): Remote search design — decided by Dallas
📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH toggle — decided by Larry Ewing
📌 Team update (2026-02-13): US-31 hlx_search_file — decided by Ripley
📌 Team update (2026-02-13): Status filter changed — decided by Larry/Ripley
📌 Team update (2026-02-15): DownloadFilesAsync per-invocation temp dirs — decided by Ripley
📌 Team update (2026-02-15): CI version validation — decided by Ripley
📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas

## Learnings

- **ParseTrxResultsAsync auto-discovery:** Production code now tries `*.trx` first, falls back to `*.xml`, then throws `HelixException` with work-item name in the message. Two error paths: "No test result files found" (no files at all) and "Found XML files but none were in a recognized format" (files found but unrecognizable).
- **SetupMultipleFiles mock pitfall:** Files with `null` content don't configure `GetFileAsync`. If `DownloadFilesAsync` matches them by pattern, the null stream causes `NullReferenceException`. Always use non-matching extensions (`.binlog`, `.log`) for "no files found" tests, or provide actual content for downloadable files.
- **MCP error surfacing pattern:** `HelixMcpTools.TestResults` currently lets `HelixException` propagate uncaught. Use `Record.ExceptionAsync` + message assertions (not exception type) to write tests that pass both before and after a try/catch wrapper is added. Comment out `Assert.IsType<McpException>` as a contract marker.
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
