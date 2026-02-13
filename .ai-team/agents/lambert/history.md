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
