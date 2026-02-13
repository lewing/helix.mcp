# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Summarized History (through 2026-02-11)

**Test infrastructure:** xUnit on net10.0 with NSubstitute 5.* for mocking. `MatchesPattern` exposed via `InternalsVisibleTo`. DI test pattern: shared `_mockApi`/`_svc` fields, per-test mock arrangement.

**Mock patterns:**
- IHelixApiClient projection interfaces: IJobDetails, IWorkItemSummary, IWorkItemDetails, IWorkItemFile
- NSubstitute gotcha: helper methods with `.Returns()` cannot be nested inside another `.Returns()` call — configure inline
- Cancellation vs timeout: `TaskCanceledException` with `cancellationToken.IsCancellationRequested` false = timeout
- `ThrowsAny<ArgumentException>` covers both `ArgumentException` and `ArgumentNullException`
- `DownloadFromUrlAsync` uses static HttpClient — only argument validation testable without HTTP mock

**Test suites written (88 total):**
- HelixIdResolver tests (GUID/URL extraction + invalid input throws)
- MatchesPattern tests (glob matching)
- HelixServiceDI tests (19 DI/error handling tests)
- HelixMcpTools tests (17 tests: Status JSON, FormatDuration, Files, FindBinlogs, Download)
- ConsoleLogUrl tests (3 tests: URL format, GUID resolution, special chars)
- US-24 DownloadFromUrlAsync validation tests (3 tests)
- US-30 Structured JSON tests (3 tests: grouped files, helixUrl, resolved jobId)
- HelixIdResolverUrl tests (7 tests: TryResolveJobAndWorkItem patterns)
- McpInputFlexibility tests (4 tests: US-29 optional workItem)
- JsonOutput tests (3 tests: US-11 --json CLI flag structure)

**Key learnings:**
- `WorkItemResult` record: 6 positional params (Name, ExitCode, State, MachineName, Duration, ConsoleLogUrl)
- `JobSummary` first param is resolved GUID `JobId`, not raw input
- US-17 namespace cleanup: all test files need `using HelixTool.Core;` and `using HelixTool.Mcp;`
- CLI `status --json` uses raw `Duration?.ToString()` while MCP uses `FormatDuration()` — intentional difference
- Proactive parallel test writing works — write tests against spec, accept compile failures as expected

📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley

📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley

## Sessions (2026-02-12)

**Cache tests (L-CACHE-1 through L-CACHE-10):** 56 tests across 3 files. CachingHelixApiClientTests (26 unit), SqliteCacheStoreTests (18 integration), CacheOptionsTests (12 unit). Key patterns: temp dirs with GUID for SQLite integration tests, sequential `.Returns()` for cache miss→hit flow, private DTOs for JSON round-tripping. Test count 126 → 182.

**Cache security tests:** 24 tests in CacheSecurityTests.cs. ValidatePathWithinRoot (7), SanitizePathSegment (6), SanitizeCacheKeySegment (5), SqliteCacheStoreSecurityTests (2 integration), CachingHelixApiClientSecurityTests (3 integration). DB tampering test requires dispose→WAL checkpoint→reopen cycle. Test count 182 → 206.

**HTTP/SSE auth tests (L-HTTP-1 through L-HTTP-5):** 46 tests across 5 files. HelixTokenAccessorTests (5), HelixApiClientFactoryTests (5), CacheStoreFactoryTests (8 incl. thread safety), SqliteCacheStoreConcurrencyTests (10), HttpContextHelixTokenAccessorTests (17). Total: 252 tests, all passing.

## Learnings

- CachingHelixApiClient constructor: 3-arg `(IHelixApiClient, ICacheStore, CacheOptions)`. `_enabled = options.MaxSizeBytes > 0`.
- Console log cache miss: decorator calls inner, stores via SetArtifactAsync, disposes original, returns GetArtifactAsync. Mock needs `.Returns(null, stream)`.
- CacheStoreFactory: IDisposable, ConcurrentDictionary, GetOrAdd with key = AuthTokenHash ?? "public"
- HttpContextHelixTokenAccessor tests: IDisposable pattern saves/restores HELIX_ACCESS_TOKEN env var per test
- NSubstitute gotcha: `GetMetadataAsync` default return is empty string (not null) — must explicitly return `Task.FromResult<string?>(null)` for cache miss


📌 Team update (2026-02-13): HTTP/SSE multi-client auth architecture decided — scoped DI with IHelixTokenAccessor, IHelixApiClientFactory, ICacheStoreFactory. Affects test infrastructure for auth-related tests. — decided by Dallas
📌 Team update (2026-02-13): Multi-auth support deferred — single-token-per-process model retained. No additional multi-auth test coverage needed. — decided by Dallas

- US-6 DownloadTests: 46 tests written in `DownloadTests.cs` across 4 test classes. Test count 252 → 298.
- DownloadFilesTests (27 tests): happy path single/multi-file download, pattern matching (*.binlog, *.trx, *, specific name, case-insensitive), empty results (no match, no files), correct temp dir placement, path traversal protection (forward slash, backslash, `..`), empty file streams, binary content preservation, same-name file overwrite, URL-based job ID resolution, input validation (null/empty/whitespace jobId and workItem), error handling (404, 401, 403, server error, timeout, cancellation).
- DownloadFromUrlParsingTests (5 tests): argument validation (null, empty, whitespace), invalid/relative URL format, URL-encoded character parsing. Cannot mock static HttpClient — tests verify argument validation and URI parsing only.
- DownloadSanitizationTests (6 tests): normal filename preserved, forward slash sanitized, `..` sanitized, path traversal stays within outDir, spaces preserved, unicode preserved.
- DownloadPatternTests (8 tests): Theory with 4 InlineData for extension/wildcard/substring patterns, default pattern downloads all, case-insensitive extension matching, case-insensitive substring matching.
- Key pattern: Each test class that writes to disk uses a UNIQUE ValidJobId constant (different GUID) to avoid temp directory collisions during parallel xUnit execution. File contention was observed when all classes shared the same GUID — `helix-{idPrefix}` dir was shared.
- DownloadFilesAsync flow: ListWorkItemFilesAsync → filter with MatchesPattern → create `helix-{id[..8]}` temp dir → foreach file: GetFileAsync → SanitizePathSegment(Path.GetFileName(name)) → ValidatePathWithinRoot → File.Create → CopyToAsync.
- DownloadFromUrlAsync uses static `s_httpClient` — only testable for argument validation and URI parsing. HTTP errors (401/403/404/timeout) cannot be tested without an HTTP mock or test server.
- NSubstitute lambda pattern for streams: `.Returns(_ => new MemoryStream(...))` — lambda needed so each call gets a fresh stream instance. Sequential `.Returns(first, second)` works for overwrite tests.

📌 Team update (2026-02-13): US-9 script removability analysis complete — 100% core API coverage, Phase 1 migration can proceed with zero blockers — decided by Ash

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash
📌 Team update (2026-02-13): MCP API design review — 6 actionable improvements identified (P0: batch_status array fix, P1: add hlx_list_work_items, P2: naming, P3: response envelope) — reviewed by Dallas
📌 Team update (2026-02-13): Generalize hlx_find_binlogs to hlx_find_files with pattern parameter — update existing FindBinlogsAsync tests, add FindFilesAsync tests with various patterns — decided by Dallas

- MCP camelCase migration: `s_jsonOptions` now uses `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` — all JSON property assertions in tests must use camelCase (`name`, `uri`, `exitCode`, `state`, `machineName`) not PascalCase
- `FindBinlogs` MCP tool delegates to `FindFiles` — JSON output uses `"files"` key (not `"binlogs"`) and includes `"pattern"` field
- `FileEntry` simplified to `(string Name, string Uri)` — no more `IsBinlog`/`IsTestResults` boolean tags; classification done at MCP layer via `MatchesPattern`
- `BatchStatus` MCP tool accepts `string[]` not comma-separated string — test with `new[] { id1, id2 }`
- `Status` parameter renamed from `all` to `includePassed` — update all test call sites accordingly
- Test count 298 → 304: fixed 8 camelCase failures, added 6 new tests (FindFiles pattern/wildcard, FindBinlogs delegation, BatchStatus array, GetWorkItemFiles simple FileEntry, FindFilesAsync pattern filtering)

## 2026-02-15: Cross-agent note from Scribe

- **Decision merged:** "camelCase JSON assertion convention" (Lambert, 2026-02-13) — all MCP test assertions must use camelCase property names.
- **Decision merged:** "MCP API Batch — Tests Need CamelCase Update" (Ripley, 2026-02-15) — tests referencing PascalCase JSON props or `binlogs` key need updating to camelCase and `files`.

## 2026-02-15: Security Validation Tests (P1 Threat Model)

**SecurityValidationTests.cs** — 18 tests covering threat-model findings E1 and D1:

- **URL scheme validation (E1):** 10 tests — HTTPS/HTTP accepted (no ArgumentException), file:///ftp://data:/javascript:/ssh:// all throw ArgumentException, null/empty throw, no-scheme throws (UriFormatException or ArgumentException both acceptable).
- **Batch size limit (D1):** 5 tests — MaxBatchSize const = 50 verified, single job accepted, 50 boundary accepted, 51 throws ArgumentException, 200 throws, empty throws.
- **MCP tool enforcement:** 2 tests — hlx_batch_status rejects 51 IDs, accepts 50 IDs with correct JSON output.
- **Total test count:** 304 → 322 (all passing).

## Learnings

- URL scheme validation in `DownloadFromUrlAsync` runs after `new Uri(url)` — a schemeless string throws `UriFormatException` from the Uri constructor before scheme validation can run. Tests for "no scheme" should accept both `ArgumentException` and `UriFormatException`.
- `HelixService.MaxBatchSize` is `internal const int` — accessible from tests via `InternalsVisibleTo`.
- Security test pattern: for HTTP scheme acceptance tests, use `Record.ExceptionAsync` + `Assert.IsNotType<ArgumentException>` rather than asserting no exception (the method will still fail with network errors, which is fine — we only care that it wasn't rejected at the validation layer).
- Batch boundary tests need mock setup for all N job IDs — use `Enumerable.Range` with formatted GUID strings (`$"{i:x8}-0000-0000-0000-000000000000"`) for bulk generation.

📌 Team update (2026-02-13): P1 security fixes E1+D1 implemented (URL scheme validation, batch size cap, MCP description) — decided by Ripley


📌 Team update (2026-02-13): Remote search design — 2 new tools (hlx_search_file, hlx_test_results) designed with 8 decisions pending Larry's review. US-31/US-32 created. Lambert to write tests for search logic and TRX parsing (including XXE, oversized, malformed .trx files) — decided by Dallas
