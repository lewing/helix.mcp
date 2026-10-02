# Ripley — History (Condensed)

## Executive Summary

**Role:** Backend development, performance optimization, audit, and verification work.

**Current Focus:** Issue #149 fix is complete and committed as `b47deae` on branch `lewing-fix-test-results-failures-hidden`. Full pre-condense detail was archived to `history-archive.md` on 2026-10-01T00:45:00Z.

## Durable Patterns and Learnings

- Prefer service/decorator boundaries for cache and offline behavior; mock `IHelixApiClient` / AzDO client interfaces rather than transport internals.
- Normalize optional parameters at the semantic boundary and reuse the same algorithm for URL construction, cache keys, and validation.
- Strict-mode compatibility lives in `CallToolFilter` aliases before parameter binding; remove alias keys after promotion so unknown-param checks stay meaningful.
- Startup/background maintenance must retain a completion handle, pin construction-time cutoffs, and dispose in cancel → bounded join → fault observation → pool cleanup order.
- Windows file replacement needs the right primitive: `File.Replace` for existing artifacts, `File.Move` for absent artifacts. Do not rely on exception-driven retry to choose publication strategy.
- Eval/snapshot tests should prove no live network fallback by using offline clients that throw and by asserting exact snapshot-derived plans.

## Recent Completed Work

### 2026-09-11 — Startup cache eviction lifecycle (#129)

Implemented Dallas's accepted lifecycle design in `SqliteCacheStore.cs`: retained `StartupMaintenance`, construction-time cutoff pinning, cancellation checkpoints, and bounded disposal. Reaffirmed non-cancellation fault propagation through `Dispose()`. Validation reached full suite 1989 passed / 8 skipped.

### 2026-09-11 — Pool scope and artifact replacement (#130)

Delivered scoped SQLite pool cleanup and artifact replacement fixes. R2 scoped pool clearing and made exporter source reads `FileShare.Read | FileShare.Delete`; R3 used `File.Replace` for existing artifacts after Windows regression evidence proved `File.Move(overwrite: true)` insufficient. Validation: targeted 387 passed / 7 skipped; full suite 1995 passed / 9 skipped; Windows, Ubuntu, and Squad CI green.

### 2026-09-30 — AzDO test run/result failure visibility (#149)

Fixed silent-empty AzDO failures: derive test-run failed count from `unanalyzedTests`; treat redirects, HTTP 203, and explicit non-JSON success bodies as authentication failures; scope test-result 404s to explicit not-found/deleted errors; cap paged test-results requests at `$top<=10000`; and avoid caching empty `azdo_test_results` lists. Anonymous live calls now throw auth guidance and failed counts are correct (`run 44793916 Failed: 9`).

## Key Test/Validation Baselines

- Issue #149 production validation coordinated with Lambert's 15-test coverage; final full suite: 2016 passed / 9 skipped.
- Local .NET runtime quirk persists: projects target `net10.0`; this machine may need `DOTNET_ROLL_FORWARD=Major` or `LatestMajor` when only .NET 11 preview is installed.

### 2026-09-30 — Windows artifact cache sharing retries

Hardened `SqliteCacheStore` artifact reads/writes against Windows `ReplaceFile` sharing windows: `GetArtifactAsync` now retries sharing/access-denied and transient missing-file opens before returning a cache miss, and `SetArtifactAsync` retries publish sharing/access-denied before deleting the temp file and skipping the cache write. Also guarded `CachingHelixApiClient` so a skipped artifact cache write falls back to the live Helix stream instead of returning null. Validation: `DOTNET_ROLL_FORWARD=Major dotnet test src/HelixTool.Tests/HelixTool.Tests.csproj --no-restore` passed (2016 passed / 9 skipped).

### 2026-09-30 — AzDO test cache compatibility review fixes

Review follow-up for #150: version AzDO test-run/result cache keys when serialized failure-count semantics change, and treat cached empty test-results lists as misses because old releases could persist misleading empties. Regression tests should seed legacy keys explicitly and assert the new versioned key is used; paging tests need a full first page (10,000 items) plus a remainder page or they only test early-exit behavior.

## Learnings

- 2026-10-02: Queue-monitor timeline issues are sufficient for `azdo evidence plan` to represent failed Helix work items without Helix API calls; keep parsed monitor jobs out of artifact `entries[]`, but fail closed with `incompleteDetails[].code` when monitor rows are unparseable, unresolved, or paged/truncated.
- 2026-10-02: Acquisition errors must be classified at the provider boundary and serialized with an explicit converter; MCP SDK default JSON options can ignore enum converter attributes in `structuredContent`, so use acquisition-owned JSON options for error envelopes. Eval/offline cache misses are now `provider=cache` acquisition errors, not `InvalidOperationException`, and valid HTTP-200 empty logs must remain successful even when the requested log id is surprising.
- 2026-10-02: Lockout protocol — when Dallas rejects commit X and locks out the author, a different agent (Lambert) must perform revisions. AzDO empty-log 200-response ambiguity specifically: empty body + absent logId in logs-list and timeline records = `not_found` acquisition error; empty body + logId present in either = success. Validation must occur in `AzdoService.GetBuildLogAsync` (service layer), not in raw `AzdoApiClient` (provider layer), and not cached by `CachingAzdoApiClient` until proven. Commit 2794a94 resolved both paging (28beceb rejection) and empty-log (5f11d95 rejection) issues.
- 2026-10-02: Acquisition errors need defense-in-depth redaction: sanitize URL query/fragment values in `AcquisitionErrorFactory` and again in `HlxAcquisitionException` so manually constructed test/MCP errors cannot leak SAS tokens. CLI-wide acquisition handling belongs in a `ConsoleAppFramework` global filter; direct unit tests that instantiate command classes bypass that filter and must be updated to exercise the generated CLI/filter pipeline or a shared writer explicitly.
- 2026-10-02: Snapshot replay uses `not_in_snapshot` only for true misses; deterministic live provider failures are negative cache rows keyed exactly like the positive evidence and replay with original provider/kind plus `source=snapshot`, `replayed=true`, `recordedAt`. Public live validation must avoid Azure CLI auth-scoped AzDO cache keys (or provide the same env token in eval), otherwise positive public entries populate an unreplayable auth partition.

### 2026-10-02: PR #153 Copilot code review fixes (commit ae35fd3)
- Addressed 5 Copilot code-review findings on PR #153
- Implemented SAS/URL redaction for CLI/MCP serialization
- Fixed eval cache corrupt-cache misclassification (→ `cache/invalid_response`)
- Classified Helix service calls at individual API/download boundary
- Structured `download_helix_file` failure classification
- Extracted `CliAcquisitionErrorPipeline` seam for improved testability
- **Dallas verdict:** APPROVED (ae35fd3) — No secret-leak path found
- Full suite validation passed; Lambert proceeded to snapshot-misses revision
