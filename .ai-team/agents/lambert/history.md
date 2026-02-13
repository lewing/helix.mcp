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

## Learnings

- US-21 FailureCategoryTests: 12 tests written in `FailureCategoryTests.cs` — 10 static ClassifyFailure tests + 2 integration tests via GetJobStatusAsync mock
- ClassifyFailure is a public static method on HelixService — directly testable without mocks
- Priority order in ClassifyFailure matters: timeout state check → crash (exit code < 0 or >= 128, with -1/null-state special case) → build keyword → test keyword/dll suffix → infrastructure error state → exit code 1 default → unknown
- WorkItemResult record now has 7 positional params (added FailureCategory? as 7th)
- FailureCategory is set to null for passing work items (exit code 0) and non-null for failures — tested via GetJobStatusAsync integration tests

📌 Team update (2026-02-12): US-22 console log search implemented — SearchConsoleLogAsync, LogSearchResult, LogMatch records added to HelixService. CLI `search-log` command and MCP `hlx_search_log` tool in both HelixMcpTools.cs files. Tests needed. — decided by Ripley

- US-22 SearchLogTests: 14 tests written in `SearchLogTests.cs` (8 test methods, 3 are Theory with 3 InlineData each)
- SearchConsoleLogAsync calls DownloadConsoleLogAsync internally, which uses `_api.GetConsoleLogAsync` (returns Stream). Mock returns MemoryStream with known content — the method writes to temp file then reads back, so file I/O happens transparently
- LogMatch.LineNumber is 1-based; LogMatch.Context includes the match line itself plus surrounding lines
- contextLines=1 produces 3 context entries (1 before + match + 1 after); contextLines=0 means Context is null
- maxMatches caps early — loop stops scanning once limit reached, so only first N matches returned
- SetupLogContent helper pattern: `_mockApi.GetConsoleLogAsync(...).Returns(_ => new MemoryStream(...))` — lambda needed so each call gets a fresh stream


📌 Team update (2026-02-11): Wrote 14 tests for US-22 search-log in SearchLogTests.cs. Test count 112 → 126. — decided by Lambert
📌 Team update (2026-02-11): HelixMcpTools.cs consolidated into HelixTool.Core — test using directives changed from HelixTool.Mcp to HelixTool.Core, Mcp ProjectReference removed from HelixTool.Tests.csproj. — decided by Ripley

📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry


📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

- Cache tests (L-CACHE-1 through L-CACHE-10): 56 tests written across 3 new files. Test count 126 → 182.
- CachingHelixApiClientTests.cs: 26 unit tests — cache hit/miss, TTL selection, console log bypass for running jobs, disabled cache pass-through. Uses NSubstitute mocks of ICacheStore and IHelixApiClient.
- SqliteCacheStoreTests.cs: 18 integration tests — metadata/artifact/job-state CRUD, clear, status, TTL expiry eviction, LRU eviction with small MaxSizeBytes, idempotent schema creation. Uses temp directories with real SQLite.
- CacheOptionsTests.cs: 12 unit tests — GetEffectiveCacheRoot() explicit/default/Windows/XDG/fallback paths, default values, record `with` expression.
- Key pattern: SqliteCacheStore requires file-backed SQLite (constructor calls `Directory.CreateDirectory`), so integration tests use `Path.GetTempPath()` + GUID subdirs with cleanup in `Dispose()`/finally.
- CachingHelixApiClient constructor is 3-arg: `(IHelixApiClient inner, ICacheStore cache, CacheOptions options)`. No null-guard on params — `_enabled = options.MaxSizeBytes > 0` controls pass-through.
- Console log cache miss flow: decorator calls inner, stores via SetArtifactAsync, disposes original stream, then returns `GetArtifactAsync()` result. Mock setup needs `.Returns(null, stream)` for sequential returns.
- CachingHelixApiClient uses private DTOs (JobDetailsDto, WorkItemSummaryDto, etc.) that implement the interface types for JSON round-tripping. Cache hit deserialization returns these DTOs, not the original mock objects.
- GetTtlAsync internally calls IsJobCompletedAsync which may trigger GetJobDetailsAsync (itself cached). Mock setup for TTL tests needs to account for this call chain.

📌 Session 2026-02-12-cache-implementation: All 56 cache tests (L-CACHE-1 through L-CACHE-10) pass against Ripley's implementation. 182 total tests, build clean. Committed as d62d0d1, pushed to origin/main.

- CacheSecurityTests: 24 security tests written in `CacheSecurityTests.cs` across 3 test classes. Test count 182 → 206.
- CacheSecurityTests (15 unit tests): ValidatePathWithinRoot (7 tests — path inside root, `..` escape, deep `../../..` traversal, `..` in middle, path equal to root, forward slash traversal, URL-encoded %2e%2e handled as literal), SanitizePathSegment (6 tests — normal name unchanged, `..`→`_`, `/`→`_`, `\`→`_`, full attack sanitized, empty/null graceful), SanitizeCacheKeySegment (5 tests — GUID unchanged, path separators replaced, traversal stripped, empty/null).
- SqliteCacheStoreSecurityTests (2 integration tests): SetArtifactAsync with `..` in job ID stays within cache dir (verified via file enumeration), GetArtifactAsync with tampered DB row (injected `../../outside-artifact.txt` via direct SQLite UPDATE) throws ArgumentException or returns null.
- CachingHelixApiClientSecurityTests (3 integration tests): Job ID with path separators produces sanitized cache key, work item name with `..` produces sanitized key, file name with `/../../` produces sanitized artifact key.
- Key patterns: CacheSecurity is `internal static` — testable via `InternalsVisibleTo`. CacheOptions.GetEffectiveCacheRoot() appends `public/` subdir when AuthTokenHash is null — integration tests must use effective root, not raw CacheRoot, for DB path and artifacts dir assertions. DB tampering test requires dispose→WAL checkpoint→reopen cycle (SQLite WAL mode doesn't expose writes to separate connections until checkpoint).
- NSubstitute gotcha for CachingHelixApiClient security tests: `GetMetadataAsync` default return is empty string (not null), which triggers JSON deserialization errors. Must explicitly return `Task.FromResult<string?>(null)` for cache miss scenarios.

📌 Session HTTP-auth-tests: Wrote 4 new test files (L-HTTP-1 through L-HTTP-4) for HTTP/SSE multi-client auth abstractions:
- HelixTokenAccessorTests.cs: 5 unit tests — constructor token passthrough, null, empty, stability, interface compliance
- HelixApiClientFactoryTests.cs: 5 unit tests — Create with token, null, different tokens → different instances, same token → different instances, interface check
- CacheStoreFactoryTests.cs: 8 tests — GetOrCreate same hash → same instance, different hashes → different instances, null → "public" key, null vs explicit separation, thread safety (Parallel.For), different keys parallel, type check
- SqliteCacheStoreConcurrencyTests.cs: 10 tests — concurrent reads (metadata, job state, nonexistent keys), concurrent writes (different keys, same key, job state), concurrent read+write (metadata, artifacts), concurrent status+eviction, two store instances on same DB
- Total new tests: 28. All test files compile. Build blocked by Ripley's in-progress SqliteCacheStore connection-per-operation refactor (removed _connection field, methods not yet updated to use OpenConnection()).
- CacheStoreFactory: implements IDisposable, uses ConcurrentDictionary<string, ICacheStore>, GetOrAdd with key = AuthTokenHash ?? "public"
- EnvironmentHelixTokenAccessor: sealed class, single constructor param string? token, GetAccessToken() returns it directly
- HelixApiClientFactory: sealed class, Create(string? accessToken) → new HelixApiClient(accessToken)

📌 Session HTTP-context-token-tests: Wrote HttpContextHelixTokenAccessorTests.cs (L-HTTP-5) — 17 tests for the HTTP-specific token accessor.
- Tests cover: Bearer extraction, `token` format, env var fallback, no auth/no env → null, empty header fallback, case insensitivity (Bearer/bearer/BEARER/TOKEN/Token), whitespace trimming, malformed headers (Bearer with no value, Bearer with only spaces), unknown scheme fallback to env var, null HttpContext, IHelixTokenAccessor interface compliance.
- Uses DefaultHttpContext + HttpContextAccessor from Microsoft.AspNetCore.Http (transitive via Mcp reference).
- IDisposable pattern: saves/restores HELIX_ACCESS_TOKEN env var per test to ensure isolation.
- Added `<ProjectReference Include="..\HelixTool.Mcp\HelixTool.Mcp.csproj" />` to HelixTool.Tests.csproj.
- Expected compile error: `HttpContextHelixTokenAccessor` type not found — will compile once Ripley implements the class in HelixTool.Mcp.
- Build confirms only 1 error (missing type), all other test files still compile clean.

