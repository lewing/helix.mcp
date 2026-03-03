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

### W7 Post-Refactoring Test Verification (2026-07-18)
- **Result:** 373 tests passed, 0 failed, 0 skipped. Duration ~8s. Clean run.
- Ripley's Core NuGet packaging refactor (W1-W6, W9) — model extraction to `src/HelixTool.Core/Models/`, new `HelixTool.Mcp.Tools` project, centralized versioning — caused zero test breakage.
- Test project references and namespaces remained intact after the restructuring. `InternalsVisibleTo` still works correctly for `MatchesPattern` and `MaxBatchSize` access.
- Test count grew from 369 (last recorded) to 373 — 4 new tests added since last baseline.
- Project structure post-refactor: `src/HelixTool.Core/`, `src/HelixTool.Mcp/`, `src/HelixTool.Mcp.Tools/`, `src/HelixTool/`, `src/HelixTool.Tests/`
- CS1591 warnings (missing XML doc comments) are pervasive in Core — not test-blocking but noted for future cleanup.

📌 Team update (2026-03-03): HelixTool.Core published as standalone NuGet (lewing.helix.core) — 373 tests verified passing post-refactor. — decided by Dallas, executed by Ripley
📌 Team update (2026-03-03): Phase 1 auth UX approved — `hlx login`/`logout`/`auth status`, `git credential` storage. 7 work items with test requirements for each. — decided by Dallas

📌 Team update (2026-03-03): API review findings — decided by Dallas, Ash
