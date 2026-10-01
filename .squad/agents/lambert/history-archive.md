# History Archive — Lambert

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

## 2026-02-15: US-31 SearchFileAsync Tests (Phase 1)

**SearchFileTests.cs** — 17 tests covering SearchFileAsync and config toggle:

- **Input validation (3 tests, Theory with 3 InlineData each):** null/empty/whitespace jobId, workItem, fileName all throw ArgumentException
- **Config toggle (2 tests):** SearchFileAsync and SearchConsoleLogAsync both throw InvalidOperationException when HLX_DISABLE_FILE_SEARCH=true. Env var set/reset in try/finally.
- **Binary file detection (1 test):** file content with null bytes → IsBinary=true, empty matches
- **Basic search (3 tests):** simple pattern match (correct line numbers, 1-based), case-insensitive matching ("ERROR" matches "error"), context lines (1 before + match + 1 after)
- **Max matches (1 test):** maxMatches=3 limits results, Truncated=true
- **No matches (1 test):** pattern not found → empty matches, IsBinary=false, Truncated=false
- **Total test count:** 322 → 339 (all passing)

## Learnings

- SearchFileAsync mock setup: requires both `ListWorkItemFilesAsync` (return IWorkItemFile list) and `GetFileAsync` (return stream). The DownloadFilesAsync flow filters by MatchesPattern, so the mock file name must match the fileName parameter exactly.
- Binary detection: null byte (0x00) anywhere in first 8KB triggers IsBinary=true. Use raw `byte[]` with `SetupFileBytes` helper.
- Config toggle test pattern: set env var before call, reset in finally block. Both SearchFileAsync and SearchConsoleLogAsync check `IsFileSearchDisabled` before argument validation.
- Truncated flag: set when `Matches.Count >= maxMatches` — tests can verify this by providing fewer maxMatches than matching lines.
- Each test class uses a UNIQUE ValidJobId GUID to avoid temp directory collisions during parallel xUnit execution (established pattern from DownloadTests).


📌 Team update (2026-02-13): HLX_DISABLE_FILE_SEARCH config toggle added as security safeguard for disabling file content search operations — decided by Larry Ewing (via Copilot)

📌 Team update (2026-02-13): US-31 hlx_search_file Phase 1 implemented (SearchFileAsync, MCP tool, CLI command, config toggle) — decided by Ripley

## 2026-02-15: US-32 TRX Parsing Tests

**TrxParsingTests.cs** — 15 tests covering ParseTrxResultsAsync (TRX file parsing):

- **Input validation (2 tests, Theory with 3 InlineData each):** null/empty/whitespace jobId and workItem both throw ArgumentException
- **Config toggle (1 test):** ParseTrxResultsAsync throws InvalidOperationException when HLX_DISABLE_FILE_SEARCH=true
- **Basic TRX parsing (3 tests):** mixed results with correct Passed/Failed/Skipped counts, failed tests include ErrorMessage+StackTrace, default (includePassed=false) excludes passed tests from Results list
- **Include passed (1 test):** includePassed=true returns all 3 results (Passed, Failed, NotExecuted)
- **Max results (1 test):** maxResults=1 limits output to single result
- **Error truncation (1 test):** ErrorMessage >500 chars truncated with "... (truncated)" suffix, StackTrace >1000 chars truncated similarly
- **No TRX files (1 test):** when no .trx files found, throws HelixException
- **XXE prevention (1 test):** DTD declaration in TRX XML causes XmlException (DtdProcessing.Prohibit)
- **Total test count:** 349 → 364 (all passing)

## Learnings

- ParseTrxResultsAsync uses DownloadFilesAsync internally — mock setup same as SearchFileTests: ListWorkItemFilesAsync (return IWorkItemFile list with .trx name) + GetFileAsync (return MemoryStream with TRX XML). The DownloadFilesAsync flow writes to disk, ParseTrxFile reads from disk files.
- Ripley's TRX implementation landed before tests — proactive test writing pattern still works, just needed minor confirmation that signatures matched spec.
- TRX outcome classification: "Passed" → passed++, "Failed" → failed++, everything else (including "NotExecuted") → skipped++. Tests for non-pass/non-fail outcomes always included in Results regardless of includePassed flag.
- Error truncation in ParseTrxFile: only extracts ErrorInfo for outcome="Failed" (case-insensitive). Truncation adds "... (truncated)" suffix — total length is limit + 15 chars for suffix.
- XmlReaderSettings includes `MaxCharactersInDocument = 50_000_000` and `Async = true` beyond the DTD/resolver settings. XmlException thrown by `XDocument.Load(reader)` when DTD encountered.

## 2026-02-15: Status API Filter Migration Tests

**HelixMcpToolsTests.cs** — Updated 2 existing tests and added 5 new tests for `filter: string` parameter migration:

- **Renamed:** `Status_AllFalse_PassedIsNull` → `Status_FilterFailed_PassedIsNull` (uses `filter: "failed"`)
- **Renamed:** `Status_AllTrue_PassedIncludesItems` → `Status_FilterAll_PassedIncludesItems` (uses `filter: "all"`)
- **New:** `Status_DefaultFilter_ShowsOnlyFailed` — verifies default (no filter arg) shows only failed, passed is null
- **New:** `Status_FilterPassed_FailedIsNull` — verifies `filter: "passed"` nulls out failed, populates passed
- **New:** `Status_FilterPassed_IncludesPassedItems` — verifies passed items have expected structure (name, exitCode, state, machineName)
- **New:** `Status_FilterCaseInsensitive` — verifies `filter: "ALL"` (uppercase) populates both failed and passed
- **New:** `Status_InvalidFilter_ThrowsArgumentException` — verifies invalid filter value throws ArgumentException
- **Total test count:** 364 → 369 (15 status tests total, all passing). 1 pre-existing failure in SearchConsoleLogAsync unrelated to changes.

## Learnings

- Status API `filter` parameter accepts "failed" (default), "passed", "all" — case-insensitive. Invalid values throw ArgumentException.
- `filter: "passed"` nulls `failed` array (mirrors `filter: "failed"` nulling `passed` array). `filter: "all"` populates both.
- Proactive test writing pattern continues to work: wrote tests against new `filter` API spec before Ripley's code landed, waited for build to succeed.

## Cache Concurrency Audit (2026-02-15)

### Production Code Concurrency Patterns (SqliteCacheStore.cs)
- **Connection-per-operation:** Each method opens and closes its own `SqliteConnection` via `OpenConnection()`. No shared connection → inherently thread-safe at the .NET level.
- **WAL mode:** `PRAGMA journal_mode=WAL;` set during `InitializeSchema()` (line 73). Enables concurrent reads across processes, single writer at a time.
- **busy_timeout:** `PRAGMA busy_timeout=5000;` set per-connection in `OpenConnection()` (line 44). SQLite retries for 5 seconds when encountering a write lock.
- **Cache=Shared:** Connection string includes `Cache=Shared` (line 31). Multiple in-process connections share a single SQLite page cache.
- **Atomic artifact writes:** `SetArtifactAsync` uses write-to-temp-then-rename pattern (lines 195-207). Temp file has GUID suffix to avoid collisions. `File.Move(temp, target, overwrite: true)` is atomic on most filesystems. Fallback on Windows `IOException`/`UnauthorizedAccessException` deletes temp and tolerates failure.
- **Artifact read sharing:** `GetArtifactAsync` opens `FileStream` with `FileShare.ReadWrite | FileShare.Delete` (line 177). Allows concurrent readers and allows eviction (deletion) while readers hold the file open.
- **LRU eviction:** `EvictLruIfOverCapAsync` is called at the end of `SetArtifactAsync` (line 227). It reads the full artifact list, selects LRU candidates, then deletes files and rows one by one in `DeleteArtifactRows`. File deletion uses `File.Delete` with `IOException` catch.
- **No transaction wrapping on eviction:** `DeleteArtifactRows` deletes file, then deletes SQLite row, with no transaction. A crash between these two steps leaves an orphan row (stale row cleanup exists in `GetArtifactAsync` via `File.Exists` check).
- **No lock between size-check and eviction:** `EvictLruIfOverCapAsync` reads total size, then evicts. A concurrent writer can insert between these two steps, meaning the cache can temporarily exceed `MaxSizeBytes`.

### Key File Paths
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs` — Main cache store, all concurrency patterns
- `src/HelixTool.Core/Cache/CachingHelixApiClient.cs` — Decorator calling SetArtifactAsync/GetArtifactAsync, drives the download-then-cache flow
- `src/HelixTool.Core/Cache/ICacheStoreFactory.cs` — ConcurrentDictionary-based factory, one SqliteCacheStore per auth token hash
- `src/HelixTool.Core/HelixService.cs:321-374` — DownloadFilesAsync: downloads to temp dir (not cache), uses File.Create (not atomic)
- `src/HelixTool.Tests/SqliteCacheStoreConcurrencyTests.cs` — 14 tests: 10 original multi-thread concurrency + 4 new gap tests (stale row cleanup, eviction-during-read, concurrent eviction+write integrity, same-key race)
- `src/HelixTool.Tests/SqliteCacheStoreTests.cs` — 18 CRUD/eviction tests, single-threaded
- `src/HelixTool.Tests/CacheStoreFactoryTests.cs` — 8 tests: factory thread safety, instance identity

## Cache Concurrency Gap Tests (2026-02-15)

**SqliteCacheStoreConcurrencyTests.cs** — 4 new tests added (10 → 14 total):

- **StaleRowCleanup_FileDeletedFromDisk_ReturnsNullAndCleansUp:** Verifies orphan SQLite row cleanup when artifact file is deleted externally. First `GetArtifactAsync` detects missing file, deletes orphan row, returns null. Second call confirms row is gone (fast null).
- **EvictionDuringRead_OpenStreamRemainsReadable:** Verifies `FileShare.Delete` behavior — opens a read stream, triggers LRU eviction via small `MaxSizeBytes`, confirms the already-opened stream reads complete, uncorrupted 1KB data.
- **ConcurrentEvictionAndWrite_ArtifactIntegrity:** Stress test with 2048-byte cap, 20 concurrent 256-byte writes triggering frequent LRU eviction plus 20 concurrent reads. Tolerates `FileNotFoundException` (known race between `File.Exists` and `FileStream` open in `GetArtifactAsync`). All successful reads verified for fill-byte integrity.
- **ConcurrentCachingClientSimulation_SameKey:** Two concurrent `SetArtifactAsync` on same key with different fill bytes ('A' vs 'B'). Verifies result is exactly 128 bytes of one consistent fill byte — no partial/mixed writes.

## Learnings

- `CacheOptions.GetEffectiveCacheRoot()` appends `/public` (no auth) or `/cache-{hash}` (auth) under `CacheRoot`. Tests that reference the artifacts directory must use `_opts.GetEffectiveCacheRoot()` not `_tempDir` directly.
- Known production race in `GetArtifactAsync`: `File.Exists` check (line 160) and `FileStream` open (line 177) are not atomic. Under concurrent eviction, the file can be deleted between these two calls, causing `FileNotFoundException`. Concurrency tests must tolerate this as a known gap (catch `FileNotFoundException`).
- `DeleteArtifactRows` catches `IOException` on `File.Delete` but not `UnauthorizedAccessException`. Under concurrent eviction + read on Windows, `File.Delete` can throw `UnauthorizedAccessException` when another thread holds the file open. Concurrency stress tests must tolerate both `IOException` and `UnauthorizedAccessException`.
- Tests using small `MaxSizeBytes` to trigger LRU eviction must create their own `CacheOptions`/`SqliteCacheStore` instances (not modify shared `_opts`/`_store`) to avoid interfering with other tests running in parallel.
- Write-to-temp-then-rename pattern in `SetArtifactAsync` ensures same-key concurrent writes produce complete, uncorrupted artifacts — the atomic `File.Move(overwrite: true)` guarantees one writer wins cleanly.


📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth for package version — decided by Ripley

## Archived from history.md (2026-03-08 summarization)

### Old team updates (2026-02-11 through 2026-02-15)
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
�� Team update (2026-02-15): CI version validation — decided by Ripley

### Old learnings (pre 2026-02-22)
- **ParseTrxResultsAsync auto-discovery:** Production code now tries `*.trx` first, falls back to `*.xml`, then throws `HelixException` with work-item name in the message. Two error paths: "No test result files found" (no files at all) and "Found XML files but none were in a recognized format" (files found but unrecognizable).
- **SetupMultipleFiles mock pitfall:** Files with `null` content don't configure `GetFileAsync`. If `DownloadFilesAsync` matches them by pattern, the null stream causes `NullReferenceException`. Always use non-matching extensions (`.binlog`, `.log`) for "no files found" tests, or provide actual content for downloadable files.
- **MCP error surfacing pattern:** `HelixMcpTools.TestResults` currently lets `HelixException` propagate uncaught. Use `Record.ExceptionAsync` + message assertions (not exception type) to write tests that pass both before and after a try/catch wrapper is added. Comment out `Assert.IsType<McpException>` as a contract marker.


## Archived from history.md (2026-03-09)

### 2026-03-07: AzDO Security Tests (63 tests)
- **AzdoSecurityTests** in `src/HelixTool.Tests/AzDO/AzdoSecurityTests.cs` — 63 tests across 5 categories:
  - AzdoIdResolver malicious URL inputs (embedded credentials, non-AzDO hosts/SSRF, path traversal, query injection, unicode, long URLs, scheme attacks, integer overflow)
  - AzCliAzdoTokenAccessor command injection safety (no shell execute, env var passthrough, CLI failure resilience)
  - AzdoApiClient request construction (SSRF prevention via host assertion, token leakage in errors, special chars in org/project, null/empty token)
  - CachingAzdoApiClient cache isolation (org/project key separation, azdo: prefix, no tokens in cached data, cache key poisoning via path traversal/colons, disabled cache)
  - AzdoService end-to-end (malicious URLs rejected before API call, null/empty/invalid inputs)
- **Security test patterns:** Token leakage (DoesNotContain on error), SSRF (host assertion), Cache isolation (different keys for org/project), No-API-call guard (DidNotReceive after rejection)
- **Edge cases:** HttpUtility.ParseQueryString comma concat safe via int.TryParse, Uri credential parsing safe (Host/Path only), Uri normalizes traversal, long.MaxValue fails int.TryParse safely, newlines in AZDO_TOKEN mitigated by AuthenticationHeaderValue
- **Total:** 594 tests (531 + 63 new).

### 2026-03-08: AzDO Artifact & Attachment Tests (33 tests)
- **AzdoArtifactTests** in `src/HelixTool.Tests/AzDO/AzdoArtifactTests.cs` — 33 tests: API Client (artifacts/attachments), Service Layer (URL resolution, top param), Caching (miss/hit, TTL 4h artifacts/1h attachments, azdo: prefix), MCP Tools (list/URL/empty), Edge Cases (invalid input, 2GB file, JSON round-trip)
- CamelCase JSON: `root.GetProperty("camelCaseName").GetXxx()` avoids xUnit2002
- Artifacts use ImmutableTtl (4h), TestAttachments use TestTtl (1h)
- **Total:** 700 tests (667 + 33 new).

### 2026-03-08: Proactive Tests for SEC-2/3/4 and AzDO CLI (53 tests)
- **HttpClientConfigurationTests** (13): null-guard, timeout range validation, timeout vs cancellation behavior, IHttpClientFactory pattern
- **StreamingBehaviorTests** (18): empty/large streams, tailLines edges, connection errors, stream disposal, special chars, input validation
- **AzdoCliCommandTests** (22): build summary, timeline, build log, changes, test runs/results, list builds, artifacts
- NSubstitute: `.Returns<Stream>(_ => throw new Ex())` for exception testing; init-only properties need object initializer
- AzdoBuildChange.Author is AzdoChangeAuthor (not AzdoIdentityRef)
- **Total:** 753 tests (700 + 53 new).

### 2026-03-08: AzDO Search Log & TextSearchHelper Tests (41 tests)
- **TextSearchHelperTests** (20): basic matching, context lines, case insensitivity (Theory), edge cases, max matches, overlapping context, large content (10K lines), special chars (literal not regex)
- **AzdoSearchLogTests** (21): happy path, no matches, context, max matches, large log, special chars, URL resolution, case insensitivity, input validation, null log, search disabled env var, result identifier
- TextSearchHelper: pure static class, 5 required params, no defaults
- AzdoService.SearchBuildLogAsync delegates to TextSearchHelper, uses IsFileSearchDisabled guard
- Env var test pattern: save/set/try-finally-restore for HLX_DISABLE_FILE_SEARCH
- **Total:** 791 tests (750 + 41 new).

### PR #10 Review Fix: Test Parallelism for Env Var Tests
- Added `[Collection("FileSearchConfig")]` to AzdoSearchLogTests for env var mutation safety
- Added FileSearchConfigCollection.cs with `[CollectionDefinition("FileSearchConfig", DisableParallelization = true)]`
- Convention: all classes mutating HLX_DISABLE_FILE_SEARCH must use this collection

### AzDO Search Timeline Tests (19 tests)
- **AzdoSearchTimelineTests** (19): name/issue matching, record type filtering, result filtering (all/failed/default), empty/null handling, input validation, parent name resolution, duration formatting, edge cases
- SearchTimelineAsync returns TimelineSearchResult (in AzdoModels.cs), null timeline throws InvalidOperationException
- Default resultFilter is "failed"; FormatDuration: >1h "Xh Ym", >1m "Xm Ys", else "Xs"
- Tests written in parallel with Ripley's implementation, adapted from tuple to final TimelineSearchResult class

## 2026-03-13: Archived from history.md during summarization

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
# Lambert — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Test project:** `src/HelixTool.Tests/HelixTool.Tests.csproj` — xUnit, net10.0, references HelixTool.Core and HelixTool.Mcp
- **Testable units:** HelixIdResolver (pure functions), MatchesPattern (internal static via InternalsVisibleTo), HelixService (via NSubstitute mocks of IHelixApiClient), HelixMcpTools (through HelixService)

## Core Context

- **Test stack:** `src/HelixTool.Tests/HelixTool.Tests.csproj` targets net10.0 with xUnit + NSubstitute; Helix tests live under `src/HelixTool.Tests/Helix/`, AzDO tests under `src/HelixTool.Tests/AzDO/`, and shared coverage stays at the test-project root.
- **Assertion conventions:** MCP-surface tests assert camelCase JSON names, env-var mutation tests use `[Collection("FileSearchConfig")]`, and disk-writing tests use unique GUID-based temp roots/job IDs to avoid parallel contention.
- **Mocking seams:** mock `IHelixApiClient` / `IAzdoApiClient` plus their projection interfaces, use fresh-stream lambdas for file/download tests, and prefer focused test runs before the full suite when reviewing changes.
- **High-value file paths:** `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs`, `src/HelixTool.Tests/CacheSecurityTests.cs`, and `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` are the main regression seams for current architecture decisions.

## Learnings

**Archive refresh (2026-03-13):** Detailed PR #15 cleanup, 9-repo `CiKnowledgeServiceTests` expansion, and the linked 2026-03-10 CI-knowledge update moved to `history-archive.md`. Durable takeaways: delete proactive duplicate tests once real coverage lands, and use broad Theory matrices for static CI-profile data.

- Tests for AzdoMcpTools should assert against `[JsonPropertyName]` names (camelCase). No separate MCP result wrappers for AzDO tools.
- **SearchBuildLogAcrossSteps (21 tests):** 3 categories — Unit (T-1–T-11: ranking, early termination, orphans, normalization), Validation (V-1–V-6: argument checks), MCP (M-1–M-2: exception remapping). Key: `SetupTimeline()`/`SetupLogsList()`/`SetupLogContent()` helpers. `LogsSkipped` tracks cap-limited logs, not minLines-filtered. `stoppedEarly` = budget exhausted OR eligible logs remain. Test count after: 812.
- Discoverability copy has two strong regression seams: reflect `DescriptionAttribute` text on MCP tool methods to lock routing promises, and assert rendered CI-guide section ordering with `IndexOf`/section slicing so “use AzDO first” guidance stays visible before pattern inventories.
- `helix_test_results` false-confidence regressions are best caught through MCP-layer exception assertions in `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs`; high-value cases are no structured-result files, empty uploads, and crash-artifact uploads, all of which should route callers toward `azdo_test_runs`/`azdo_test_results`, `helix_search_log`, and `helix_ci_guide`.
- Key file paths for discoverability coverage: `src/HelixTool.Tests/Helix/HelixMcpToolsTests.cs` now holds MCP description + fallback-routing assertions, `src/HelixTool.Tests/CiKnowledgeServiceTests.cs` locks guide wording/order for aspnetcore/runtime, `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` contains the live tool descriptions, and `src/HelixTool.Core/CiKnowledgeService.cs` renders the repo-specific CI guide text.
- User preference reinforced again: for review-driven test changes, run focused tests first to debug wording/assertion mismatches quickly, then run the full `src/HelixTool.Tests/HelixTool.Tests.csproj` suite before concluding the regression coverage is complete.

📌 Team updates (2026-03-09 – 2026-03-10 summary): CI profile analysis — 14 tool description/error message recommendations (Ash). Test quality review — net -17 tests, zero coverage loss, prune proactive tests when real tests land (Dallas). CiKnowledgeService expanded to 9 repos, 5 tool descriptions updated (Ripley).

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
- AzDO auth now centers on `AzdoCredential` instead of raw strings: `Token` is the wire value, `DisplayToken` preserves the original PAT/JWT for assertions and messages, and implicit string conversion returns `DisplayToken`, which keeps older mock patterns readable while still allowing scheme-aware auth tests.
- `AzCliAzdoTokenAccessor` checks `AZDO_TOKEN` on every call but only caches the fallback chain (`AzureCliCredential`/`az` CLI). High-value regression tests should lock both behaviors: env tokens short-circuit without marking fallback state resolved, while a resolved fallback returns the cached credential on later calls.

📌 Team update (2026-03-13): AzDO auth is now the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous, with scheme-aware `AzdoCredential` metadata and `DisplayToken` kept separate from the wire token. — decided by Dallas, Ripley

📌 Team update (2026-03-13): MCP-facing Helix names/descriptions should stay scope-accurate and low-context: use `helix_parse_uploaded_trx`, `helix_search`, and keep repo-specific routing in `helix_ci_guide`. — decided by Ripley
- For private or compile-time auth seams, prefer narrow seam tests: drive `AzdoApiClient` redaction through public 500-response behavior, and use reflection only for `AzdoCredential` API-surface assertions or `TryGetEnvCredential` env-only behavior.
- Edge case: `GetAccessTokenAsync()` does not return null when only `AZDO_TOKEN_TYPE` is set, because the accessor still falls through to `AzureCliCredential`/`az` fallback; the null assertion belongs at the env-resolution seam, not the full accessor chain.
- Edge case: redaction regexes are intentionally selective — `token|key|password|secret=` values, JWT-shaped triples, and 41+ char base64-like blobs redact independently, while ordinary `name=value` text such as `reason=timeout` should remain visible in exception snippets.
- Key file paths: `src/HelixTool.Tests/AzDO/AzdoApiClientRedactionTests.cs` covers error-body redaction via `GetBuildAsync`, and `src/HelixTool.Tests/AzDO/AzdoTokenAccessorTests.cs` now locks invalid override fallback, missing-token env behavior, and `AzdoCredential` operator metadata.

📌 Team update (2026-03-13): PR #28 merged the remaining AzDO auth quick wins — fallback Azure CLI/`az` credentials now refresh on deadline/401, cache isolation keys off stable auth-source identity instead of raw token bytes, and `hlx azdo auth-status` exposes safe auth-path metadata. High-value regression coverage should lock refresh invalidation, cache partitioning, and auth-status output. — decided by Ripley
- Test patterns established: for `AzCliAzdoTokenAccessor`, deterministic fallback-cache tests can be done by seeding the private cached resolution via reflection while forcing a PATH with no `az`; that cleanly distinguishes “fresh cached fallback returned” from “expired fallback re-resolved to anonymous” without depending on developer machine credentials.
- Edge cases discovered: JWT helpers should treat non-JWT text, missing `exp`, and malformed base64url payloads as non-fatal nulls; auth-status on env PATs should surface `environment variable` + PAT expiry warning, while anonymous fallback must still report a user-facing "No AzDO credentials resolved" warning.

📌 Team update (2026-03-13): Cache roots now stay stable via `CacheRootHash` while mutable `AuthTokenHash` partitions AzDO entries, and AzDO auth hashes are seeded before cached AzDO reads. — decided by Ripley

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane
- Added TDD-style schema coverage in `src/HelixTool.Tests/CliSchema/SchemaGeneratorTests.cs` for the planned `HelixTool.Core.CliSchema.SchemaGenerator` API: primitives, DateTime/DateTimeOffset, Guid, enums, flat/nested POCOs, collections, nullable unwraps, circular references, max-depth cutoff, `AzdoAuthStatus` smoke coverage, and generic-vs-Type overload parity.
- Key pattern for pre-implementation testability: call `SchemaGenerator.GenerateSchema<T>()` / `GenerateSchema(Type)` via reflection, then parse the returned JSON with `JsonDocument` and assert placeholder values and array/object shape. That keeps `dotnet build src/HelixTool.Tests/HelixTool.Tests.csproj --nologo` green before the production type exists while still giving Ripley runtime-red TDD coverage once the implementation lands.

📌 Team update (2026-03-14): `hlx azdo search-log --schema` must mirror the active JSON payload: `LogSearchResult` with `--log-id`, `CrossStepSearchResult` otherwise. — decided by Ripley
- Test patterns for source-generated code: exercise generated registries through the consuming assembly (`HelixTool.Generated.CommandRegistry`) instead of the generator project, and sync metadata tests by reflecting the runtime MCP assembly for `[McpServerTool]` + `[Description]` attributes before comparing routes, descriptions, categories, and parameter defaults.
- Key file paths for describe coverage: `src/HelixTool.Tests/CliSchema/DescribeTests.cs`, `src/HelixTool/Program.cs`, `src/HelixTool.Generators/DescribeGenerator.cs`, and `src/HelixTool.Mcp.Tools/{Helix/HelixMcpTools.cs,AzDO/AzdoMcpTools.cs,CiKnowledgeTool.cs}`.
- `src/HelixTool.Tests/CliSchema/SchemaGeneratorTests.cs` should call `SchemaGenerator.GenerateSchema<T>()` / `GenerateSchema(Type)` directly now that the API is public; schema assertions must follow JSON contract names, so `[JsonPropertyName]` coverage belongs in both a local DTO test and a real-model smoke test such as `AzdoBuild`.

📌 Team update (2026-03-16): MCP timeline truncation implementation complete — `TimelineResponse` record type introduced for `azdo_timeline` return value when output exceeds 200 records (first 100 returned + truncation metadata). May need test updates if assuming `AzdoTimeline?` return type. See `.squad/decisions/decisions.md` (section "Timeline Truncation Implementation") for details and design trade-offs. — implemented by Ripley
📌 Team update (2026-03-16): MCP timeline truncation implementation complete — `TimelineResponse` record type introduced for `azdo_timeline` return value when output exceeds 200 records (first 100 returned + truncation metadata). May need test updates if assuming `AzdoTimeline?` return type. See `.squad/decisions/decisions.md#timeline-truncation` for details and design trade-offs. — implemented by Ripley

📌 Team update (2026-05-20): Pagination standardization audit complete — Dallas audit found 2 🔴 tools (`azdo_changes`, `azdo_test_runs`) returning raw lists with no truncation metadata, 10 🟡 tools with bespoke response shapes. Phase 1 wraps reds in `CreateLimitedResults()` (~30 min). Phase 2 adds `truncated` field to yellows (~2-3 hours). Testing target: verify truncated flag set correctly when results exceed `top` parameter. See `.squad/decisions.md#dallas--pagination-architecture` for inventory table and upstream API reality.

### Pagination contract tests (2026-05-20)
- **What:** Added 13 contract tests (333 LOC) in `src/HelixTool.Tests/AzDO/PaginationContractTests.cs` verifying pagination standardization per Dallas's spec.
- **Coverage areas:**
  1. **CreateLimitedResults helper** (5 tests): truncation logic when count == top, count < top, top=0, empty results, count > top edge case
  2. **Phase 1 tools** (4 tests): `azdo_changes` and `azdo_test_runs` now return `LimitedResults<T>` with correct truncation flag
  3. **Default parameters** (4 tests): `azdo_builds`, `azdo_test_results`, `azdo_changes`, `azdo_test_runs` use reasonable defaults (20/50/20/50)
- **Branch coordination:** Ripley is implementing Phase 1 changes in parallel (modified `AzdoMcpTools.cs`, `HelixMcpTools.cs`, `AzdoModels.cs`). Ripley's work is WIP (incomplete Helix changes causing build errors), but Phase 1 changes to `azdo_changes` and `azdo_test_runs` match the test expectations exactly.
- **Test status:** Tests compile against `HelixTool.Core`. Full solution build blocked by Ripley's incomplete Helix work — expected and normal for parallel development. Tests will verify Ripley's implementation once complete.
- **Key file:** `src/HelixTool.Tests/AzDO/PaginationContractTests.cs`
- **Test count:** 1167 existing + 13 new = 1180 tests (when Ripley completes implementation).


📌 Team update (2026-05-08): MCP SDK v1.0.0 → v1.3.0 upgrade pending — parallel research (Ash) and inventory (Dallas) complete; recommendation to upgrade to v1.3.0 (low risk, no code changes required). Drift items flagged by Dallas: hardcoded ServerInfo.Version, stdio host missing WithResourcesFromAssembly, no Directory.Packages.props. May need test updates if SDK changes affect existing test coverage. See `.squad/decisions/inbox/*` for details.

📌 Team update (2026-05-08): MCP SDK 1.3.0 upgrade — Ripley shipped branch `squad/mcp-sdk-1.3.0-upgrade` with MCP SDK 1.0.0 → 1.3.0, Central Package Management adoption (Directory.Packages.props), stdio host resource visibility fix, and ServerInfo.Version de-hardcoding. Validation: dotnet restore ✅, dotnet build ✅ (0 errors, 6 NU1507 warnings pre-existing). **Test verification pending** — Lambert owns full suite validation and sign-off before merge.


### MCP SDK 1.0.0 → 1.3.0 upgrade verification (2026-05-08)
- **Branch:** `squad/mcp-sdk-1.3.0-upgrade`, commit 80bf9f2 by Ripley.
- **Test command:** `dotnet test` from repo root → **1167 passed, 0 failed, 0 skipped, ~3 s** on net10.0. No regressions; no test changes needed for the SDK bump itself.
- **Stdio smoke:** `dotnet run --project src/HelixTool -- mcp --help` exits cleanly; `dotnet run --project src/HelixTool -- --version` returns **0.5.4**, proving `AssemblyInformationalVersionAttribute` lookup replaces the old hardcoded "0.1.2".
- **HTTP smoke:** `dotnet src/HelixTool.Mcp/bin/Debug/net10.0/HelixTool.Mcp.dll --urls http://127.0.0.1:18765` boots, binds, logs `Application started.` with zero DI resolve errors. Killed cleanly.
- **Resource scan parity:** Both `src/HelixTool/Program.cs:944` and `src/HelixTool.Mcp/Program.cs:89` now call `.WithResourcesFromAssembly(typeof(HelixMcpTools).Assembly)` — stdio fix matches HTTP host so `ci://profiles` is reachable from CLI/stdio clients.
- **Version source:** both Program.cs files use `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0"` (stdio L939, http L84).
- **Gotcha:** Pre-existing `NU1507` warning (nuget.org + dotnet-eng without source mapping) is now surfaced under CPM. Out of scope but worth a follow-up. Verdict file: `.squad/decisions/inbox/lambert-mcp-sdk-upgrade-verdict.md`.
- **Verdict:** ✅ APPROVE.

📌 Team update (2026-05-08): MCP SDK 1.3.0 follow-ups — verification pending

**Context:** Ripley completed two PRs:
1. **PR #47** — `[AllowedValues]` sweep, `OpenWorld` annotations, `<packageSourceMapping>` (1167 tests pass ✅)
2. **PR #48** — Progress notifications on long-running tools (1167 tests pass ✅)

**Verification status:**
- Both PRs passed local test suite (1167/1167)
- Smoke tests confirmed (build clean, MCP help, version resolution)
- Awaiting sequential verification per squad orchestration

**Follow-ups from Ripley:**
- **PR #48:** Add unit tests for `ProgressReporter.CopyToWithProgressAsync` (event count band, monotonic progress, null sink no-op) + one end-to-end MCP-stdio test using SDK client's `WithProgress`
- **General:** Verify `[AllowedValues]` options match actual enum values in MCP schema generation

**Test count:** 1167 baseline maintained across both PRs.

### PR #47 (annotations + NU1507) and PR #48 (MCP progress notifications) verification (2026-05-08)
- **PR #47 verdict:** ✅ APPROVE. Build clean (0 warnings — NU1507 gone), 1167/1167 tests pass. Reflection probe over `HelixTool.Mcp.Tools.dll` confirms 25 `[McpServerTool]` methods with `OpenWorld` set explicitly on every one (22 true / 3 false: `azdo_auth_status`, `helix_auth_status`, `helix_ci_guide`). 10 params carry `[AllowedValues]`. Generated descriptors via `McpServerTool.Create()` show `enum` arrays in JSON schema (e.g. `azdo_builds.status.enum = ["all","cancelling","completed","inProgress","none","notStarted","postponed"]`) and `annotations.openWorldHint` in tool annotations.
- **PR #48 verdict:** ✅ APPROVE. Build clean, 1167/1167 baseline tests pass. Wrote a temporary `src/HelixTool.Tests/ProgressNotificationsSmokeTests.cs` (7 tests) to verify (1) `Download`, `FindFiles`, `SearchLog` declare a default-null `IProgress<ProgressNotificationValue>?` parameter with no `[Description]`, (2) `McpProgressAdapter.Wrap(null) == null` (fast path), (3) `Wrap(sink)` forwards `ProgressUpdate → ProgressNotificationValue` with float coercion, (4) `ProgressReporter.CopyToWithProgressAsync` emits initial+final updates over a 1 MiB stream. With smokes: 1174/1174 pass. Smokes were removed before returning to main (Ripley owns the branch); body preserved in the verdict file for re-use.
- **Pattern:** `McpProgressAdapter` is `internal` and `HelixTool.Mcp.Tools` has no `InternalsVisibleTo("HelixTool.Tests")`, so any future test of internal seams there must use reflection or land an IVT first. Recommended follow-up: ask Dallas/Ripley to add `[assembly: InternalsVisibleTo("HelixTool.Tests")]` to `HelixTool.Mcp.Tools`.
- **Probe pattern reused:** building a tiny `Probe` console csproj that `ProjectReference`s `HelixTool.Mcp.Tools` is the cleanest way to inspect MCP tool descriptors out-of-band — `McpServerTool.Create(method, target)` requires a non-null `target` for instance methods (use `RuntimeHelpers.GetUninitializedObject(method.DeclaringType)` when you don't want to construct the real DI graph).
- **Gotcha:** `HelixMcpTools` and `AzdoMcpTools` live in the flat `HelixTool.Mcp.Tools` namespace despite being under `Helix/` and `AzDO/` folders — don't add `using HelixTool.Mcp.Tools.Helix;`-style usings, they will not compile.
- **Branch sequencing observation:** during this verification, an external process (likely Scribe) checked out main and committed in the same working tree while Lambert was mid-verify. Sequential single-tree verification is workable but fragile — re-confirm the active branch with `git rev-parse --abbrev-ref HEAD` between stages, or move to git worktrees per the standing recommendation in the SDK 1.3.0 verdict.



---

## Archived from history.md on 2026-10-01T00:45:00Z

# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, test architecture.

**Focus (2026-06-24 through 2026-07-20):** Strict-mode implementation (PR #83–87), CCA cycles, test patterns, anticipated schema-reduction validation work.

---

## 2026-08-01 Through 2026-08-31: Summary

Completed integration test work for strict-mode implementation. Confirmed tools/list schema reduction, validated StructuredContent emission, and established test file patterns for future MCP work. All PR reviews passed; tests stable. No blocking issues.

---

## Recent Work

## 2026-09-11: Startup cache-eviction lifecycle tests (#129) — Dallas's decision implemented

**Context:** Dallas's read-only design review (`.squad/decisions/inbox/dallas-startup-cache-eviction-lifecycle.md`) specified an internal, awaitable `SqliteCacheStore.StartupMaintenance` task (construction-time cutoff pinning, cancel-then-join disposal ordering) to fix #129. I added the required deterministic test coverage while Ripley's production implementation landed concurrently in the same worktree.

**Tests added (8 new facts across 3 authorized files), all using `await store.StartupMaintenance` / `IsCompleted` — zero `Task.Delay`/`Thread.Sleep`/`SpinWait`/retry loops/`DisableParallelization`:**
- `SqliteCacheStoreTests.cs`: zero-TTL row written after construction survives (regression exact, #129 inverted into a permanent guard); a row expired before construction is removed by startup maintenance; startup maintenance doesn't weaken the explicit `EvictExpiredAsync()` contract; eval-mode `StartupMaintenance` already completed on construction; eval-mode open/evict/dispose leaves the db byte-identical with no new WAL/SHM sidecars.
- `SqliteCacheStoreConcurrencyTests.cs`: `Dispose()` joins startup maintenance and the database is immediately writable (proved with `busy_timeout=0` — fail-fast, not merely "eventually succeeds"); immediate + double `Dispose()` is quiet with 50 pre-seeded artifacts mid-eviction; 5 concurrently constructed stores over one root all complete maintenance and leave the db usable (extends `TwoStoreInstances_SameDb_ConcurrentAccess_IsSafe`'s shape, doesn't replace it).
- Updated the stale fire-and-forget-bug comments in `SnapshotEvalModeTests.cs` (`BackupWithRetryAsync` rationale — kept the retry, it still guards legitimate WAL-checkpoint/pool-release timing) and `ExpiredSnapshot.cs` (rationale header) to describe current, fixed behavior; also added an explicit `await writer.StartupMaintenance;` in `CreateStableSnapshotAsync` now that it's joinable.

**Hard-won lesson — a public API's own TTL filter can mask the very thing you're testing:** `GetMetadataAsync` re-evaluates `expires_at > UtcNow` at *call time*. A `TimeSpan.Zero` row therefore always reads back as "not found" through the public API regardless of whether the startup-maintenance DELETE actually ran — by the time you call `GetMetadataAsync`, real wall-clock time has already made the row look expired to that query. To prove a row survived (or was removed by) startup maintenance, you must check table-row *presence* directly via a raw `SqliteConnection`, not through the store's own TTL-filtered getters. Recorded because it is an easy, silently-wrong pattern: a test using `GetMetadataAsync` here would pass or fail for the wrong reason.

**Hard-won lesson — `SqliteConnection.BackupDatabase` (disk-to-disk) copies the source's persisted `journal_mode` header setting, not just its rows.** Seeding an eval-mode snapshot by backing up a live WAL-mode writer db onto a fresh destination file leaves the destination *tagged* as WAL-journaled in its header, even though no `-wal`/`-shm` sidecar exists yet on disk. The next connection to open it — even `Mode=ReadOnly`, even for a pure read — is then entitled to materialize a `-wal`/`-shm` pair to honor that persisted setting. This is normal SQLite protocol behavior, not a defect in the store under test, and it is *not* what a real exported snapshot looks like on disk: production's `SnapshotExporter` stages through an in-memory (`journal_mode=memory`) connection and writes the serialized bytes directly, then asserts `EnsureNoDatabaseSidecars`. My first pass at the eval-mode "no WAL/SHM sidecar" test used the disk-to-disk backup helper directly and flaked under the full suite; the fix was to explicitly force `PRAGMA journal_mode=DELETE;` on the seeded copy before asserting the "clean snapshot" baseline, rather than assuming the backup path alone produces one. Full 1989-test suite confirmed green across 3 repeated runs after the fix, with the 43 targeted tests also green across 3 repeated runs beforehand.

**Environment note (recurring, not code):** local runtime is .NET 11 preview only; `dotnet test`/`dotnet build` against `net10.0`-targeted projects require `DOTNET_ROLL_FORWARD=Major` (or `LatestMajor`) or the test host refuses to launch. Same quirk previously logged 2026-08 cycle; still applies.

**Validation:** 43 targeted tests (SqliteCacheStoreTests, SqliteCacheStoreConcurrencyTests, SnapshotEvalModeTests) green across 3 consecutive runs; full suite 1989 passed / 8 skipped / 0 failed across 3 consecutive runs, `DOTNET_ROLL_FORWARD=Major`.

### Same-day follow-up: non-cancellation worker-fault propagation through bounded disposal

Creator asked for deterministic coverage that bounded disposal does not hide a non-cancellation
worker fault — without a new broad/public seam or violating Dallas's no-hook/no-race constraints.
Found a real, already-reachable path instead of adding one: `DeleteArtifactRows` catches only
`IOException` around `CacheSecurity.ValidatePathWithinRoot`, which throws `ArgumentException` —
one of the exact types Dallas's §2.5 names as required to propagate out of `Dispose()` — when a
`cache_artifacts.file_path` resolves outside the artifacts root. Seeding one malicious row
directly via SQL (same technique `StaleRowCleanup_FileDeletedFromDisk_ReturnsNullAndCleansUp`
already uses) with `ArtifactMaxAge=Zero` makes it a guaranteed startup-maintenance eviction
candidate for the next store — deterministic, no timing, no injected hook.

Added `StartupMaintenance_NonCancellationWorkerFault_PropagatesThroughAwaitAndDispose`
(`SqliteCacheStoreConcurrencyTests.cs`): asserts the fault is directly observable by awaiting
`store.StartupMaintenance` (`ArgumentException`, unwrapped by `await`), then asserts the same exception propagates through `Dispose()` via the AggregateException timeout path. No new seam added; path is already reachable (malicious artifact path seeded directly via SQL, paired with ArtifactMaxAge=Zero, guarantees startup-maintenance eviction).

**Cross-agent coordination:** Ripley's production code landed concurrently in same worktree; I validated against Ripley's internal `SqliteCacheStore.StartupMaintenance` task contract while he validated fault propagation separately. No blocking dependencies; full suite green after Lambert's WAL-header fix. See `ripley-startup-cache-eviction-fault-reaffirmation.md` for the incidental BackupDatabase discovery.

**Decision:** `lambert-startup-cache-eviction-tests.md` (full coverage report, hard-won lessons on TTL-filtering and WAL headers, fault-propagation no-seam pattern).

**Orchestration log:** `.squad/orchestration-log/2026-09-11-1355-lambert-startup-cache-tests.md`
`store.Dispose()` also throws — the `AggregateException.Handle` predicate in Dispose only
absorbs `OperationCanceledException`/`SqliteException`/`IOException`, so `ArgumentException`
must resurface. Verified empirically on the first attempt (5 repeated runs green, full suite
1990 passed / 8 skipped / 0 failed across 3 repeated runs) — the reasoning about which exact
exception type escapes which catch matched Ripley's actual implementation exactly.

**Note for cleanup hygiene:** when `Dispose()` faults before reaching `ClearAllPools()`, pooled
native SQLite handles are never released by the store itself. The test's own `finally` calls
`SqliteConnection.ClearAllPools()` before deleting its temp directory — this is the same public
driver API production `Dispose()` already calls, not a new seam, but it matters: without it, a
lingering pooled connection can hold the db file open and make directory cleanup flaky, especially
on Windows.

---

## 2026-07-20: Tiered outputSchema Recommendation — PEER REVIEW

**Context:** Dallas refined "flatten all" → tiered (FLATTEN 10 / KEEP 3 / LEAVE 12).

**Anticipated Lambert work (pending user approval):**
- **Integration test:** Confirm tools/list shrinks ~5,450 bytes after tiered implementation
- **StructuredContent validation:** Verify responses still emit StructuredContent despite flattened schema
- **Test file patterns:** Reuse existing MCP_* or StructuredContent_* tests

**See also:** .squad/decisions/decisions.md (Dallas decision, merged from inbox 2026-07-20).

---

## 2026-06-24: Strict-Mode Implementation (PR #83–87)

### PR #83 Review — Issue #81 Stage A
**Blocking bug found:** Missing TypeInfoResolver in Program.cs → first-request crash (InvalidOperationException on read-only JsonSerializerOptions).
- SDK sets TypeInfoResolver auto when null, but only before MakeReadOnly
- If MakeReadOnly called first, auto-assign fails on read-only instance
- **Fix:** Add `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` to both Program.cs files

**Non-blocking:** Alias removal, result→resultFilter, arguments.Remove(), existing bindings all correct.

**Tests:** 8 tests cover 7 scenarios + 2 alias-collision regressions.

### PR #87 — CCA Follow-Up Cleanup (#83–#85)
**Real bugs fixed (by Lambert under lockout):**
1. **Alias-removal hole** (McpServerOptionsExtensions.cs:75): Used `continue` on canonical present → alias key never removed. Fix: always remove alias key, skip only canonical-value promotion.
2. **Missing newline** (line 199): Single-unknown path had no trailing `\n` → concatenated "Did you mean: X?Allowed parameters...". Fix: `sb.AppendLine()`.

**Tests added:** 2 new alias-collision regression tests; message-format tests updated with `\n`-transition assertions. (1450 → 1452 passed; 2 skipped).

**CCA cycle pattern:** CCA finds bug → Ripley (author) locked out → Lambert fixes + tests under lockout → Larry reviews CCA second pass → Larry merges.

---

## 2026-06-01 through 2026-06-24: Param Plumbing & Strict-Mode Architecture

### PR #75 — Numeric Alias Coercion (Gap Fix)
**Finding:** Numeric `build_id` values (JSON numbers) fail binding to string parameter `buildIdOrUrl`.
**Fix:** Implement `CoerceToStringElement()` in CallToolFilter; validate upstream value kinds.
**Lesson:** When binding alias parameters, consider jsonElement.ValueKind early. Test all upstream kinds, not just expected types.

### MCP 1.4.0 Bump Safety
Decompiled Microsoft.Extensions.AI.Abstractions 10.5.2 (shared by MCP 1.3.0 and 1.4.0):
- UnmappedMemberHandling.Disallow check gates on `!HasCustomParameterBinding`
- Our tools (all plain value params, no DI) → HasCustomParameterBinding = false → check WOULD run
- No changes to CallToolFilter API, McpException shape, ProtocolTool.InputSchema structure, or alias-normalization paths
- **Bump to 1.4.0 is safe.** Zero migration work required.

---

## Test Architecture Patterns (Reusable)

### `[Theory] + [InlineData]` Contract Test Pattern
Per-param coverage with high test count, low LOC:

**URL construction:**
```csharp
[Theory]
[InlineData("main", "branchName=main")]
[InlineData("refs/heads/main", "branchName=refs%2Fheads%2Fmain")]
public async Task ListBuildsAsync_Branch_AppearsInUrl(string branch, string expectedPart) { }
```

**Cache key discrimination:**
```csharp
[Theory]
[InlineData("main", "develop")]
public async Task ListBuildsAsync_DifferentBranch_DistinctCacheKeys(string b1, string b2) { }
```

### Redundant-Test Removal Heuristic

Test is redundant iff:
1. Tests only a normalization RULE (not the layer's behavior), AND
2. Same rule is now covered by a direct unit test of the shared normalizer

**Safe to remove:** Normalization unit tests if centralizer has own coverage.
**Must keep:** Tests that verify URL construction, cache TTL, cache hit/miss behavior (layer tests, not rule tests).

**Practical rule:** Keep if test would fail after correct normalizer but broken call site. Remove only if would pass by testing normalizer alone.

---

## Prior Work Archive

See `.squad/agents/lambert/history-archive.md` for:
- PR #66–#78 exception handling, parameter standardization, caching patterns
- Cache normalization, exit codes, doc coupling learnings
- Array safety (use IReadOnlyList/FrozenSet, not readonly string[])
- SQLite test flakiness pre-existing issues
- Extensive test patterns and code review feedback cycles

---

## 2026-07-28: helix_find_files workItem schema consistency tests

### Context

User reported hard schema-rejection error: `helix_find_files` was missing `workItem` while all 6 sibling work-item tools had it. Ripley was concurrently implementing the fix.

### Learnings

**Test infrastructure:**
- `McpToolDescriptionTests.cs` is the home for MCP schema/contract tests. It already had `McpServerToolParameters_HaveDiscoverableDescriptions` using reflection over `HelixMcpTools`, `AzdoMcpTools`, `CiKnowledgeTool`. Adding a `[Theory]` with `[InlineData]` for each expected tool is the right extension point for schema consistency tests.
- The existing test class exposes `GetMcpToolMethods()` and `GetToolName()` as private statics — add new tests to the same class to reuse them without changing visibility.
- `HelixMcpToolsTests.cs` is the home for per-tool behavioral tests. Setup pattern: `IHelixApiClient` mock via NSubstitute, `HelixService` + `HelixMcpTools` wired together.

**Reflection-based behavioral tests for anticipated parameters:**
- When testing behavior that depends on a parameter not yet in the codebase, use `method.GetParameters().FirstOrDefault(p => p.Name == "X")` as a guard inside the test. If the parameter is absent, the test fails early with a clear message. If present, the test proceeds with reflection-based invocation using a name-based arg selector (`p.Name switch { ... }`) — this is position-independent and survives Ripley inserting the param at any slot.
- `Task<T>` return types can be cast directly from `method.Invoke(...)` if you know the concrete generic type.

**Parameter ordering hazard:**
- When a new optional parameter is inserted before existing optional parameters in a method, callers using positional args silently bind to the wrong slot (or fail to compile). Two pre-existing tests (`FindFiles_ReturnsValidJsonWithScanResults`, `FindFiles_WildcardPattern_ReturnsAllFiles`) broke this way — `"*.trx"` bound to the new `workItem` slot instead of `pattern`. Fix: use named args (`pattern: "*.trx"`) for all optional params after the first.

**`ScannedItems` adjustment:**
- Ripley's implementation sets `ScannedItems = string.IsNullOrEmpty(workItem) ? maxItems : 1` when workItem is provided. Tests that assert `ScannedItems == 50` remain valid when no workItem is given.

---

## 2026-07-28 — PR #117 guard hardening (workItem shape validation)

### Learnings

**Reflection-based contract tests must assert parameter SHAPE, not just presence.**
Checking `p.Name == "workItem"` only proves the parameter exists; it does not prevent a future tool from declaring `string workItem` (required), `int workItem`, or `string workItem = "default"`, all of which would pass the old guard while still breaking callers. The correct assertion is all three:
1. `p.Name == "workItem"` — parameter exists
2. `p.ParameterType == typeof(string)` — correct CLR type
3. `p.HasDefaultValue && p.DefaultValue is null` — optional with a null default

Each distinct failure path should carry a self-contained message naming the tool and quoting the required declaration (`string? workItem = null`) so the author has an actionable fix without reading the test body.

---

## Known Patterns & Conventions

- **Validation layers:** Validate at user boundary (CLI/MCP) → canonicalize at semantic boundary (cache key, URL) → share algorithm across layers
- **Silent param drop detection:** Audit tool method signature vs REST API capabilities; missing params + missing URL plumbing produce identical symptom
- **Cache key normalization:** Always normalize null/whitespace/defaults to identical representations before hashing
- **External PR reviews:** Clear feedback → merge promptly → file follow-ups ourselves
- **CCA follow-up cycle:** Expects author lockout; fixer can be different agent; ensure fix closes entire bug class, not just test case
- **Schema consistency guard:** Use `[Theory] + [InlineData(toolName)]` in `McpToolDescriptionTests` to explicitly enumerate the set of tools that must share a parameter; fails loudly when a new tool is added without it

## 2026-07-28 — helix_find_files workItem parameter test coverage
Added schema-consistency test (WorkItemScopedHelixTools_HaveOptionalWorkItemParameter) covering 7 work-item-scoped Helix tools. Added 2 behavioral tests for workItem fast path. Fixed 2 pre-existing tests after parameter ordering change. Full suite: 1506 passed. Approved by Dallas; assigned non-blocking cleanup (simplify reflection-based tests, remove stale comments, harden schema test).

## 2026-07-28: PR #117 Review Round — Guard Hardening (lewing-fix-find-files-workitem-param)

### Task
Route final review comment on helix_find_files workItem parameter — hardening schema-consistency guard assertions.

### Fix Applied
**Hardened HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped** reflection guard:
- Added parameter type assertion (`typeof(string)`)
- Added optionality assertion (`HasDefaultValue && DefaultValue is null`)
- Each with distinct, actionable failure message

Prevents future regressions from wrong-type or required parameters slipping through while still violating MCP contract.

**Commit:** 445abcb  
**Test outcome:** 1500/0 failed / 2 skipped  
**Branch:** lewing-fix-find-files-workitem-param

### Lesson: Skill Extraction Timing
**TEAM LESSON (cross-agent):** Skill was extracted mid-session and captured the INTENDED design. Subsequent discovery-based implementation (Lambert) replaced the referenced method with superior pattern, leaving skill pointing at code that never existed. Consider deferring skill extraction until after review completion to capture actual shipped behavior.

---

## 2026-08-20 — T1–T4 test gates + G4 for MCP C# SDK 1.4.0 → 2.2.0 migration

Implemented Dallas's mandatory test gates for Ripley's SDK bump (`Directory.Packages.props`
1.4.0→2.2.0, `Program.cs` `SessionMode = Stateless`). Full report:
`.squad/decisions/inbox/lambert-csharp-mcp-sdk-tests.md`. Result: T1/T2/T4 pass clean; T3
passes but with a documented production-seam gap (escalated, not resolved unilaterally).
Targeted 26/26 pass; full suite 1528 total/1526 passed/2 skipped (pre-existing)/0 failed/0
new skips. Zero production files touched.

### Reusable technique: TCS-gating a substituted async dependency to prove a race-prone assertion
When asserting "at least one notification arrived *during* an in-flight async call" (T1's
core claim), don't just await the whole call and then check the notification list — that
proves the notification arrived *eventually*, not that it survived the transport while the
request was still open. Gate the one substituted dependency
(`IHelixApiClient.ListWorkItemFilesAsync` here) behind a
`TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously)`, await a
"first notification received" signal *before* completing the TCS, then assert and only
afterward let the call finish. This is the difference between "the notification eventually
showed up" and "the notification genuinely crossed the wire while the request was still
in flight" — the latter is what T1 actually requires.

### Reusable technique: `TestServer` + real `McpClient`/`HttpClientTransport` for MCP protocol tests
SDK 2.2.0's `HttpClientTransport(HttpClientTransportOptions, HttpClient, ILoggerFactory?,
bool ownsHttpClient)` constructor accepts *any* `HttpClient`, including
`Microsoft.AspNetCore.TestHost.TestServer.CreateClient()` (via `host.GetTestServer()` — note:
`IHost` itself has no `.GetTestClient()`; you must call `.GetTestServer().CreateClient()`).
This gets you a real MCP client speaking real Streamable-HTTP JSON-RPC/SSE against a real
`AddMcpServer().WithHttpTransport(...)` registration, entirely in-process — no sockets, no
Kestrel, no upstream SDK's heavier `KestrelInMemoryTest`/`KestrelInMemoryConnection` fixture
needed. Gotchas found by trial: `app.UseRouting(); app.UseEndpoints(e => e.MapMcp());` (not
bare `app.MapMcp()`, which is a `WebApplication`-only extension) — and this requires
`services.AddRouting()` explicitly in `ConfigureServices` when using `HostBuilder()
.ConfigureWebHost(...)` rather than `WebApplication.CreateBuilder()` (which adds routing
implicitly). `McpClient.CallToolAsync(...)` returns `ValueTask<CallToolResult>`; store it in
a variable and consume with `.AsTask().WaitAsync(timeout)` exactly once — don't await a
`ValueTask` twice.

### Reusable technique: non-destructive pre-bump baseline via `git worktree add --detach`
To measure a "before" value (G4's tools/list byte count) without touching the working tree
or doing anything destructive: first check `git diff HEAD` for the file(s) in question — if
the only uncommitted change *is* the bump itself, HEAD already **is** the pre-bump baseline,
no separate branch lookup needed. Then, from the *main* repo directory (`git worktree list`
to find it), run `git worktree add --detach <scratch-path> <baseline-commit>`, build there,
run the existing measurement harness against both trees, and `git worktree remove
<scratch-path>` when done. Zero risk to the working tree's state.

### Finding: the SDK's own `SessionMode`/`Stateless` default already flipped once in this exact migration
Verified by direct instantiation (not just XML-doc prose) against each package version in a
throwaway console app *outside* the repo tree (CPM in `Directory.Packages.props` rejects
explicit `<PackageReference Version="...">` inside the repo tree — NU1008 — so scratch
verification projects for a specific package version must live outside it):
`new HttpServerTransportOptions().Stateless` (bool, 1.4.0) == `false` (stateful by default);
`new HttpServerTransportOptions().SessionMode` (enum, 2.2.0) == `Stateless` by default. This
means Ripley's explicit `SessionMode = Stateless` pin isn't merely restating today's default
— it protects against a *third* flip on some future SDK bump, which is the correct framing
for why T3 pins intent rather than "just checking the default holds."

### Known limitation carried forward: T3 cannot exercise the literal `Program.cs`
`src/HelixTool.Mcp/Program.cs` (top-level statements, no `partial class Program` marker, no
`InternalsVisibleTo`, unconditional blocking `app.Run()`) cannot be driven via
`WebApplicationFactory<Program>` or reflection. T3 reconstructs the identical one-line
registration in test code and proves the *mechanism*, but a future edit to Program.cs's
`SessionMode` line would not be caught by this test. Escalated the standard, behavior-neutral
`public partial class Program;` seam to Dallas rather than adding it myself (out of scope
for a Tester per my boundaries) — see the decision doc for the ask.

---

## 2026-08-20 — F1/F3 final-review gates (dallas-csharp-mcp-sdk-final-review.md)

### F1: hermetic real-host tests without forcing/clearing ambient state
The rejected artifact (`HttpTransportSessionModeTests.cs`) failed only when the ambient
`HLX_API_KEY` was set, because it sent no `X-Api-Key` header while the real host's
`app.UseApiKeyAuthIfConfigured()` (read once, at pipeline-build time) had installed
`ApiKeyMiddleware`. The correct fix is **not** to clear or force the env var (that would stop
testing one of the two worlds) — it's to have the test read the *same* ambient variable the
middleware reads, at the same time, and mirror its exact non-empty check when deciding whether
to attach the header. This makes the test agree with whatever the real host actually did,
in both worlds, instead of asserting on one specific ambient state.

### Reusable technique: shared non-parallel xUnit collection for ambient-env-var tests
Any test class that mutates or reads a *process* environment variable (not scoped per-instance)
races with xUnit's default cross-class parallelism. This repo's established fix
(`AzdoTokenEnv`, `FileSearchConfig` collections) is a `[CollectionDefinition("Name",
DisableParallelization = true)]` marker + `[Collection("Name")]` on every participating class.
Added `HlxApiKeyEnvCollection.cs` defining `HlxApiKeyEnv` and joined all three classes that
touch `HLX_API_KEY` (`ApiKeyMiddlewareTests`, `HttpTransportSessionModeTests`, the new
`ApiKeyScopedRequestIsolationTests`) to it. Generalizable: **any future test touching a
process-wide ambient value (env var, `CultureInfo.CurrentCulture`, static mutable config, etc.)
should join or create a `DisableParallelization` collection**, not just save/restore in
ctor/Dispose — save/restore alone is necessary but not sufficient once other classes can run
concurrently.

### Reusable technique: prove per-request DI isolation with a deterministic recording `WebApplicationFactory`
For F3/G7 (proving `HttpContextHelixTokenAccessor` → `IHelixApiClientFactory.Create` →
`CacheOptions.ComputeTokenHash` → `ICacheStoreFactory.GetOrCreate` are all resolved fresh
per-request, with zero cross-request leakage), the technique that worked without touching
production code:
1. Subclass `WebApplicationFactory<Program>`, set the ambient `HLX_API_KEY` to a **fixed test
   constant** in the constructor (before the host is ever lazily built) and restore the
   original value in `Dispose(bool)` — this makes the fixture's auth-enabled behavior
   independent of whatever the *real* ambient key is, so the class doesn't depend on how it's
   invoked (works identically whether the outer suite run has `HLX_API_KEY` set or not).
2. Override `ConfigureWebHost` → `ConfigureServices` and replace only the two seams Program.cs
   itself already exposes as request-scoped extension points (`IHelixApiClientFactory`,
   `ICacheStoreFactory`) via `services.RemoveAll<T>()` + `services.AddSingleton<T>(recordingInstance)`
   (`Microsoft.Extensions.DependencyInjection.Extensions`). Both replacements are themselves
   singletons so their recorded call history persists across every request the shared factory
   instance serves — exactly what's needed to assert "request 2 recorded exactly its own token,
   not request 1's."
3. Give each recording fake a `ConcurrentQueue<T>` (thread-safe, preserves call order) and
   expose it as `IReadOnlyList<T>` for assertions; back `IHelixApiClientFactory.Create` with an
   NSubstitute-configured fake `IHelixApiClient` (only `GetJobDetailsAsync`/`ListWorkItemsAsync`
   need stubbing — an empty work-item list short-circuits `HelixService.GetJobStatusAsync`
   before it needs `GetWorkItemDetailsAsync`), and back `ICacheStoreFactory.GetOrCreate` with a
   fully in-memory no-op `ICacheStore` (avoids any real SQLite/disk I/O in a "smoke" test).
4. Use two separate `HttpClient`s from `_factory.CreateClient()` (same underlying host/DI
   container, real per-request scoping) each with a distinct `Authorization: Bearer` value,
   drive each through a real `McpClient`/`HttpClientTransport` `tools/call` (not raw JSON-RPC,
   not `StatelessMcpTestHost`'s singleton-host reconstruction) sequentially, then assert the
   recorded token/hash sequences equal `[tokenA, tokenB]` / `[hash(tokenA), hash(tokenB)]` and
   that the pair is mutually distinct.
5. **Keep the auth-gating facts (401/401/200) tool-free.** `HelixMcpTools`/the recording
   factories are only DI-resolved when a `tools/call` actually dispatches to a tool — never
   during `initialize`. Sending only raw `initialize` requests in the gating facts (mirroring
   F1's pattern) keeps the isolation fact's recorded-call count deterministic (exactly 2) even
   though four `[Fact]`s share one fixture/host instance, without needing to reason about xUnit
   fact execution order.

This pattern (fixed-value deterministic auth + `RemoveAll<T>`/`AddSingleton<T>(recordingFake)`
via `ConfigureWebHost`) is generalizable to any future "prove request N's production DI resolves
independently of request N-1" gate against a real `WebApplicationFactory<Program>` host, without
ever needing to change production architecture to add a test seam.

### Environment quirk (not a code issue): SDK/runtime mismatch requires `DOTNET_ROLL_FORWARD=LatestMajor`
This sandbox has only the .NET 11 preview runtime installed while the solution targets
`net10.0`; `dotnet test`/`dotnet run` fail with "You must install or update .NET to run this
application" unless `DOTNET_ROLL_FORWARD=LatestMajor` is set in the environment for the test
invocation. Not a repo bug — just a note for future agents running tests in this exact worktree
image, so they don't mistake it for a build regression.

---

## 2026-08-26: Snapshot Eval-Mode PoC — Test Implementation & Reviewer Gate

**Context:** Dallas approved `HLX_EVAL_SNAPSHOT` PoC. Ripley owns production; Lambert owns tests and review.

### What was tested
44 new tests across three files:
- `CacheOptionsTests.cs` (5): EvalMode property, `GetEffectiveCacheRoot` bypass for absolute/relative paths.
- `SqliteCacheStoreTests.cs` (17): TTL bypass, no-op eviction, no-op writes, `last_accessed` mutation guard, schema mismatch throw, WAL/SHM cleanup, normal-mode regression.
- `SnapshotEvalModeTests.cs` (new, ~22): `OfflineAzdoApiClient`/`OfflineHelixApiClient` stubs (all methods throw "eval mode"), composition (cache-hit/miss, path resolution), end-to-end CI-evidence scenario.

### Key learnings

**1. `TimeSpan.Zero` TTL race with background eviction**
`SqliteCacheStore` fires `_ = Task.Run(() => EvictExpiredAsync())` on construction (normal mode only). Eviction runs `DELETE WHERE expires_at <= @now`. Writing with `TimeSpan.Zero` (`expires_at = now`) races with this task — if the task runs AFTER the write, it deletes the just-inserted row. Fix: `await Task.Delay(30)` between store creation and the zero-TTL write lets eviction drain on the empty DB first. Since eviction is fire-and-forget (runs once), subsequent writes are safe.

**2. WAL/SHM re-creation by SQLite WAL mode**
Production code deletes stale WAL/SHM files BEFORE opening the connection. However, `SqliteCacheStore` sets `PRAGMA journal_mode=WAL` inside `InitializeSchema()`. This causes SQLite to recreate WAL/SHM files on every connection open. Tests must NOT assert `!File.Exists(walPath)` after the store is open — that assertion will always fail in WAL mode. Instead assert that the store opens without throwing and data is readable.

**3. Eval mode requires pre-existing valid DB**
Eval mode `InitializeSchema()` throws "schema version mismatch: expected 1, found 0" on any DB whose `PRAGMA user_version` is not 1. An empty directory has no DB (version = 0). Tests simulating "cache miss in eval mode" must pre-seed the DB with a schema-only normal-mode writer first, then open the eval store. The seed writer creates the schema but writes no data rows — eval store then sees a valid (empty) DB and can open successfully.

**4. Ripley fixed the `GetArtifactAsync` mutation bug proactively**
The pre-session bug report (missing `if (!_options.EvalMode)` guards on `UPDATE last_accessed` and `DELETE` in `GetArtifactAsync`) was already fixed by the time tests ran. Both guards are present in the committed code. The mutation test now correctly passes.

**5. `OfflineAzdoApiClient` methods throw synchronously**
Methods use `=> throw Blocked()` pattern (throw before returning Task). xUnit's `CS0619` obsolete error on `Assert.Throws<T>(Func<Task>)` requires upgrading to `await Assert.ThrowsAsync<T>(...)` with `async Task` test methods. `ThrowsAsync` correctly catches synchronous throws from Task-returning methods.

**6. Reviewer verdict**
**APPROVE** — All 1614 tests pass (1612 pass, 2 pre-existing skips). All acceptance criteria are met by the production implementation. No high-confidence correctness defects found in the final code.

---

## 2026-09-11: Closed remaining gap — snapshot-backed evidence planning integration test

**Task:** Prove `AzdoService.GetEvidencePlanAsync` (build+timeline+artifacts -> `AzdoEvidenceMatcher`)
consumes only snapshot/cache-backed data with network genuinely blocked, and still produces the
same deterministic mapping/completeness behavior the matcher-level fixture tests already
established — closing the gap between two suites that each proved half the story:
`SnapshotEvalModeTests.SnapshotCiEvidenceScenarioTests` (individual cached endpoints served
offline) and `AzdoEvidenceFixtureTests` (matcher mapping/completeness), but never both together
through the real `AzdoService` -> `CachingAzdoApiClient`(eval mode) -> `OfflineAzdoApiClient` path.

**What was added:** `src/HelixTool.Tests/AzDO/AzdoEvidencePlanSnapshotEvalModeTests.cs` (3 tests),
plus a one-line visibility change (`private` -> `internal`) on
`AzdoEvidenceFixtureTests.Build1569889Fixture()` so the new file could reuse the existing 7-job /
14-artifact fixture instead of duplicating it. No other production or test files touched.

1. `EvalMode_AutoStrategy_ProducesDeterministicCompleteMapping_FromSnapshotOnly` — success path:
   seeds a real snapshot (via `SnapshotEvalTestHarness.CreateStableSnapshotAsync`, i.e. an actual
   writer-store -> `BackupDatabase` snapshot, not a direct file copy) with build/timeline/artifacts
   cache entries, opens it in eval mode with `OfflineAzdoApiClient` as the network layer, and
   asserts `plan.Complete == true`, all 7 entries `"mapped"` with a single attempt-2 candidate.
2. `EvalMode_NormalizedExactStrategy_ProducesDeterministicAmbiguity_FromSnapshotOnly` — same
   snapshot, `Match = normalized-exact`: all 7 entries become `"ambiguous"` (2 candidates,
   attempts `[1,2]`), `plan.Complete == false`, `IncompleteReasons.Count == 7`. This is the
   completeness/ambiguity half of the gate, proven through the real service call path instead of
   calling `AzdoEvidenceMatcher.BuildPlan` directly.
3. `EvalMode_MissingArtifactsSnapshotEntry_ThrowsEvalModeError_NeverFallsBackToNetwork` — partial
   snapshot (artifacts cache entry omitted): `GetEvidencePlanAsync` throws
   `InvalidOperationException` with an "eval mode" message rather than silently falling back to a
   live AzDO call. Because `OfflineAzdoApiClient` throws on any call, a *successful* plan in tests
   1–2 is itself proof no live network call occurred; test 3 proves the cache-miss failure mode is
   explicit rather than a silent fallback.

**Validation:** Targeted filter (new class + `AzdoEvidenceFixtureTests` + `AzdoEvidenceMatcherTests`
+ `AzdoEvidenceSurfaceTests` + `SnapshotEvalModeTests`) — 187/187 passed. Full suite —
1981 passed, 8 pre-existing skips (matches the last known full-suite baseline), 0 failures.

**No production defect found.** `AzdoService.GetEvidencePlanAsync` and `CachingAzdoApiClient`
already compose correctly with eval mode; no production code changed.

**Reusable technique:** When a matcher/algorithm-level fixture (private helper method) already
encodes the exact deterministic scenario an integration test needs, promote its visibility to
`internal` (same assembly) rather than re-deriving equivalent fixture data inline. This keeps the
"13 unmatched" / "7 mapped, all attempt-2" / "7 ambiguous, both attempts" facts defined in exactly
one place, so future changes to those fixtures automatically keep both the unit- and
integration-level tests in sync — directly the test-discipline principle of not letting assertion
data drift from a single source of truth, applied across test files instead of just across a
prod/test boundary.

---

## 2026-09-11: Pre-fix regression coverage for #130 (SQLite pool scope + artifact source share)

**Context:** Dallas's design review (issue #130) established two distinct production defects to
fix — `SqliteCacheStore.Dispose()`'s process-global `SqliteConnection.ClearAllPools()` interfering
with unrelated cache roots, and `SnapshotExporter.CopyArtifactAsync`'s `FileShare.Read` live-artifact
handle blocking concurrent cache overwrite/eviction on Windows. My assignment was the pre-fix test
state only: land deterministic regression coverage before either production fix lands, so CI proves
the defects first and proves the fixes second in the same PR. Production code was read but not
touched (Ripley had already added the test-visible `SnapshotExporter.ArtifactSourceFileShare`
constant, still pinned to `FileShare.Read`, ahead of the real fix).

**Tests added (7 new facts across the three authorized files):**

- `SqliteCacheStoreConcurrencyTests.cs`:
  - `IndependentRoots_DisposeA_BStaysUsable_ARootDeletesCleanly` — two Guid-unique roots, warms
    both, disposes A, proves B remains fully read/write-usable, then asserts A's own root directory
    deletes with an **unswallowed** `Record.Exception`/`Assert.Null` (not a best-effort try/catch).
    This holds regardless of the pool-scope fix (POSIX/global-clear both permit self-root deletion)
    — it is a sanity baseline, not the discriminator.
  - `IndependentRoots_DisposeA_WindowsExclusiveOpenOfB_StillFails_BecauseBPoolUntouched`
    (`[WindowsOnlyFact]`) — the actual discriminator: warms B (idle pooled native handle), disposes
    independent A, asserts an exclusive `FileShare.None` open of B's `cache.db` still throws
    `IOException`. Pre-fix, A's global `ClearAllPools()` also evicts B's unrelated pool, so this
    assertion is expected to fail on Windows CI until the fix scopes clearing to the disposing
    store's own connection string. **This is a handle/share-policy invariant, not a claim of true
    in-loop temporal overlap** between A's `Dispose()` and any specific B operation — recorded
    explicitly per Dallas's framing so a future reader doesn't misread it as a race assertion.
  - Removed the test-side `SqliteConnection.ClearAllPools()` finally-block call/comment from
    `StartupMaintenance_NonCancellationWorkerFault_PropagatesThroughAwaitAndDispose` per the
    assignment (item 3) — directory cleanup there is now unguarded by that manual pool release.
- `SnapshotExportTests.cs` (`SnapshotExporterTests`/`SnapshotValidatorTests` classes):
  - `SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize` —
    opens a real artifact with the exporter's own `ArtifactSourceFileShare` constant, then calls
    `SetArtifactAsync` for the same key with different (and differently-sized) content while that
    handle remains open; asserts the read-back bytes and the `cache_artifacts.file_size` column both
    reflect the replacement. Verified green here (Unix, POSIX rename-over-open-fd semantics).
    Expected RED on Windows while the constant is `FileShare.Read`, because
    `SetArtifactAsync`'s `File.Move(..., overwrite: true)` can hit a sharing violation there and its
    `catch (IOException or UnauthorizedAccessException)` silently drops the write — this is the
    exact mechanism the fix (`FileShare.Read | FileShare.Delete`) must correct.
  - `Export_WhileNormalArtifactReadStreamRemainsOpen_SucceedsWithExactBytesSizeAndHash` — keeps a
    live `GetArtifactAsync` stream open across a full `SnapshotExporter.ExportAsync` of the same
    root, then verifies exact copied bytes, `FileInfo.Length`, and a SHA-256 hash match, plus
    `SnapshotValidator.ValidateAsync` success. This one is OS-agnostic by construction: it never
    exercises the source-share defect at all, since `GetArtifactAsync`'s own share flags
    (`ReadWrite | Delete`) are already permissive enough for a concurrent exporter read under either
    share policy — it's a positive concurrency regression, not a discriminator.
  - `Validate_PublishedSnapshot_WhileEvalModeStoreRemainsOpen_SucceedsWithNoWritesOrSidecars` — a
    real exported (not hand-copied) snapshot, opened by a genuine eval-mode `SqliteCacheStore` that
    stays alive across `SnapshotValidator.ValidateAsync`; asserts validation succeeds, the eval
    store's write attempt made during that window is still a true no-op, and the on-disk layout
    stays exactly `{cache.db, artifacts}` (no `-wal`/`-shm`) throughout — closing the same kind of
    "each half proven, never proven together" gap as the 2026-09-11 evidence-plan test above.
- `AzdoEvidenceSurfaceTests.cs`: removed the redundant `SqliteConnection.ClearAllPools()` call from
  `CliEvidencePlan_KeepAttemptPrefixFlag_ReachesSerializedPlan`'s `finally` block per assignment
  item 7; its `Directory.Delete` cleanup there was already unguarded (no try/catch), so no cleanup
  swallowing was introduced or removed.
- New shared file `WindowsOnlyFactAttribute.cs` — a generic Windows-only `FactAttribute` following
  the exact established pattern of `WindowsShortNameFactAttribute` in `SnapshotExportTests.cs`
  (`Skip` set in the constructor when `!OperatingSystem.IsWindows()`, so xUnit reports a genuine
  `Skipped` result rather than a vacuous pass from an early `return`). Added as a new file rather
  than reusing `WindowsShortNameFactAttribute` because that attribute's semantics are specifically
  about 8.3 short-name alias support, not a generic Windows gate — reusing it would have produced a
  misleading skip reason for an unrelated Windows-only test.

**Validation (this Unix machine):** targeted filter covering `SqliteCacheStoreConcurrencyTests`,
`SnapshotExporterTests`, `SnapshotValidatorTests`, `AzdoEvidenceSurfaceTests`,
`CacheStoreFactoryTests`, `CacheSecurityTests` — **187 passed, 7 skipped, 0 failed** (194 total).
The one new Windows-only fact reported `[SKIP]` as required; every other new/modified fact passed.
Did not run the full suite (out of scope for this pass) and did not commit/push per instructions.

**No decision-inbox item filed.** Everything encountered while implementing this assignment
(the exact silent-catch mechanism in `SetArtifactAsync`, the pool-eviction interaction, the
C3 handle-vs-temporal-overlap framing) was already anticipated by Dallas's design review and my
assignment brief — none of it is a new finding a future reader would need surfaced separately.

## 2026-09-11: Post-fix regression validation and File.Replace test coverage (#130, L2)

Validated production fixes against pre-written regression tests and added targeted post-fix coverage for File.Replace atomic-replacement logic:

**Post-R2 (c58340d) Validation:**
Pool-scope discriminator test turned GREEN (confirms scoped `ClearPool` fixes cross-root interference). Artifact-replacement test remained RED on Windows (proves permissive source-share alone insufficient). Evidence-driven scope amendment decision made: investigate `File.Move` platform semantics.

**Post-R3 (10149cf) Coverage:**
All 387 targeted tests now GREEN, including artifact-replacement test (confirms `File.Replace` solves Windows share-conflict gap). Full suite 1995 passed / 9 skipped / 0 failed. No regressions, no new flakes.

**Windows-Only Discriminators:**
- `IndependentRoots_DisposeA_WindowsExclusiveOpenOfB_StillFails_BecauseBPoolUntouched` — confirms pool-scope fix
- `SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize` — confirms artifact-replacement fix

Both properly marked with `[WindowsOnlyFact]` and skip cleanly on Unix CI.

**Cross-Platform Validation:**
- Local machine (Unix): 387 targeted, 1995 full-suite (all pass)
- Windows CI: 387 targeted equivalent, full-suite equivalent (all pass)
- Ubuntu CI: success
- Squad CI: success

**Learning:** Silent catch mechanism in original `SetArtifactAsync` masked the `File.Move` share-violation on Windows until `File.Replace` logic was explicitly added. Regression-test-first methodology correctly exposed the hidden defect that implementation-first would have shipped.

**Status:** COMPLETED — full pre-fix and post-fix regression coverage delivered, all tests green

## Learnings

### 2026-09-30: AzDO test-results silent-empty regressions (#149)

Added focused regression coverage for issue #149 in `TestResultsSilentEmptyTests`: real AzDO test-run JSON maps `unanalyzedTests` to `FailedTests` while preserving `IncompleteTests` and `NotApplicableTests`; 203/text-html/302 sign-in responses now assert auth guidance across `GetTestResultsAsync`, `GetTestRunsAsync`, and a non-list `GetAsync` path; test-run result 404s assert an explicit not-found/deleted error; caching wrappers are guarded against caching auth exceptions; and captured request URLs assert `$top` never exceeds AzDO's 10,000 cap.

Coordination note: Ripley's production changes landed while these tests were being written. An intermediate run caught `GetTestRunsAsync(..., top: 10001)` still sending `$top=10001`; Ripley fixed it before final validation. Updated the stale existing `AzdoApiClientTests.GetTestResultsAsync_404_ReturnsEmptyList` assertion to the new contract. Final targeted validation: new class 15/15 passed; AzDO namespace 1009 passed / 2 skipped / 0 failed.
