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
- 2026-10-02: PR 1 paging/cache compatibility landed by keeping MCP defaults capped while CLI `--all` writes complete versioned keys (`changes:v2`, `testruns:v3`, `testresults:v3`, `testattachments:v2`) that eval-mode capped calls probe first and slice in memory. AzDO full-log cache entries use the existing raw in-memory contract, but SQLite metadata must encode strings containing NUL bytes; otherwise full-log rows round-trip as empty and capped offline log tails miss or corrupt.
- 2026-10-02: `hlx collect azdo-build` should capture final AzDO auth state after the live fetches, not before, because `CachingAzdoApiClient` updates `CacheOptions.AuthTokenHash` lazily on first authenticated call. Public dnceng builds may still need an env `AZDO_TOKEN` for test-run APIs; exported snapshots replay those auth-scoped keys only when the same effective env token/type is present. Do not pre-download Helix uploaded files to enforce size caps unless file-list metadata exposes a trusted length first; record explicit skips instead.
- 2026-10-02: Post-merge collect hardening: collection must reject disabled caches before the first provider call; resume is safe only when the prior attempt's cache key matches the current key and the positive/error row still exists; `recorded_failure` means the negative row is actually saved, not merely non-retried. Helix selected downloads need the same retry/classification path as service fetches, must reserve the shared byte budget before/during streaming, and should final-verify ok/cached cache entries before export so eviction becomes explicit incompleteness. Retry-After is server-owned and should not be clamped by `--retry-max-delay` (keep only a large safety ceiling).
- 2026-10-02: SQLite TEXT binding can silently truncate/empty strings at embedded NULs for log-sized values. Cache metadata writes must encode every NUL-bearing or marker-prefixed value before binding, and all other cache TEXT write paths should reject raw NUL parameters rather than relying on SQLite/provider behavior. Empty raw AzDO log metadata rows are corrupt: live should refetch, eval should return `cache/invalid_response`, snapshot validation should flag them, and collector final verification should compare actual backing cache byte counts before export.

### 2026-10-02: PR #153 Copilot code review fixes (commit ae35fd3)
- Addressed 5 Copilot code-review findings on PR #153
- Implemented SAS/URL redaction for CLI/MCP serialization
- Fixed eval cache corrupt-cache misclassification (→ `cache/invalid_response`)
- Classified Helix service calls at individual API/download boundary
- Structured `download_helix_file` failure classification
- Extracted `CliAcquisitionErrorPipeline` seam for improved testability
- **Dallas verdict:** APPROVED (ae35fd3) — No secret-leak path found
- Full suite validation passed; Lambert proceeded to snapshot-misses revision

### 2026-10-02: PR #156 review fixes
- Centralized AzDO cache-key parsing in `AzdoCacheKeys.TryParse`; authenticated shapes must be matched before public shapes, and suffix segment counts should encode the builder contract so org/project names cannot affect partition or raw-log classification.
- AzDO list traversal should let continuation tokens fill the requested window first and only fall back to `$skip` when no continuation is returned; this preserves old large-top compatibility without duplicating rows when continuation is present.
- Collector-required evidence includes derived cache writes, not only provider fetches. The `--test-scope all` derived `Failed` test-results key is now a required attempt so final cache verification can downgrade export completeness if eviction or corruption removes it.
- Resume manifests from older buggy collectors may contain duplicate attempt IDs; loading should be last-wins tolerant instead of throwing before collection can repair the manifest.
- Helix uploaded-file streaming must classify read-time `HttpRequestException`, non-caller `TaskCanceledException`, and network-stream `IOException` as `download_helix_file` acquisition errors, not just stream-open failures.
- Live AzDO test-results `outcomes` rejected `NotRunnable`; all-scope collection should use only accepted outcome filters and still derive the default `Failed` replay key from the all-results cache entry.

### 2026-10-02: Independent pre-release review fixes (findings 1–7)
- Response-body reads happen *after* `SendAsync`'s own try/catch returns headers (`HttpCompletionOption.ResponseHeadersRead`), so every post-send `ReadAsStringAsync`/`ReadToEndAsync` needs its own classification (`HttpRequestException`/`IOException`→`transport_error`, `TaskCanceledException` when not caller-cancelled→`timeout`). A single shared `ReadResponseBodyAsync` helper keeps this from drifting out of sync across `GetAsync`/`GetListAsync`/`ThrowOnUnexpectedError`.
- `RunAsync`'s per-attempt loop must have a true last-resort `catch (Exception)` (after the `HlxAcquisitionException` and caller-`OperationCanceledException` catches) that synthesizes a `transport_error` and records a failed attempt. Without it, any raw exception escaping classification (including a local disk I/O exception during optional file caching) crashes the whole collect with zero manifest.
- The Helix SDK boundary (`HelixApiClient`) must classify every call, not just `ListJobsByBuildAsync` — `CachingHelixApiClient.CallAndMaybeRecordAsync` only records `HlxAcquisitionException`, so raw `RestApiException`/`HttpRequestException` from `GetJobDetailsAsync`/`GetWorkItemDetailsAsync`/`ListWorkItemFilesAsync`/`GetConsoleLogAsync`/`GetFileAsync` silently defeat recorded-failure replay (`not_in_snapshot` instead of `access_denied`/`not_found`).
- Azure.Core's pipeline can wrap a raw transport `HttpRequestException` as `Azure.RequestFailedException` (not the Helix SDK's `RestApiException`) with `Status == 0`; the original HTTP status code survives on `ex.InnerException as HttpRequestException`. Classify both exception types at the Helix client boundary.
- `--export` without `--cache-dir` must collect into an isolated per-run temp cache directory (not the shared cache root) — `SnapshotExporter` has no key filtering and backs up the entire `cache.db`, so without isolation the snapshot leaks every other build and every other AzDO auth partition ever cached. Record the resolved isolated path in the manifest's `cache.root`/`command.options.cacheDir`.
- Snapshot validation failure must flip `manifest.complete = false` and add a stable `snapshot_validation_failed` incomplete detail — only the export-exception path did this before; a failed-but-not-thrown `SnapshotValidator` result left `complete: true`.
- `--test-scope all` must request the full AzDO `TestOutcome` enum (`Unspecified,None,Passed,Failed,Inconclusive,Timeout,Aborted,Blocked,NotExecuted,Warning,Error,NotApplicable,Paused,InProgress,NotImpacted`) — `NotRunnable` is not a real `TestOutcome` value and AzDO rejects it even though some docs/tools list it.
- Optional Helix file downloads must never be able to evict required evidence via the cache's LRU cap: clamp the download budget to `min(--max-total-bytes, cacheCap - requiredBytesAlreadyCollectedThisRun)`, not just the raw policy value — the previous 2 GiB default budget vs 1 GiB default cache cap meant optional downloads could evict already-collected required evidence mid-run.
- Post-write verification must bypass TTL for presence checks (`GetMetadataIgnoringTtlAsync`, added to `ICacheStore` as a default-interface method so existing fakes don't need updates) — a long collect can outlive a 1h metadata TTL, and the row is still exportable/replayable even if technically "expired" for normal reads.
- Default manifest path must resolve next to the effective cache root, not `Directory.GetCurrentDirectory()` — a CWD default is how a manifest recording the local auth-partition hash and absolute cache paths previously got committed to the repo.
- Lambert's parallel `IndependentReviewRegressionTests.cs` pins exact operation-name strings (`get_helix_job`, `get_helix_work_item`, `download_helix_file`, not the `_details`-suffixed names I initially chose) and a `fileName` resource key (not `file`) — match collector's existing naming conventions for new classified operations before assuming a name is free to choose.

### 2026-10-02 — v0.11.0 release cut (PR #158)
- Branched `release/v0.11.0` from `origin/main` @ `7ebedec`; bumped `HelixTool.csproj <Version>` and both `server.json` version fields to `0.11.0` (verified via grep/jq).
- Wrote `.squad/release-notes/v0.10.3.md`-format release notes to `.squad/release-notes/v0.11.0.md` covering #153 (Helix-aware evidence plan + acquisition errors, closes #152, requested by Vitek Karas), #154 (CLI paging, BREAKING), #155 (`hlx collect azdo-build`), #156 (post-merge fixes + SQLite NUL data-loss fix reported by PureWeen), #157 (pre-release hardening). Included prominent Breaking changes, Data loss fix (`hlx cache clear` + re-collect), and Security sections.
- CHANGELOG.md: renamed `## [Unreleased]` → `## [v0.11.0] — 2026-10-02` and inserted a fresh empty `## [Unreleased]` above it; included CHANGELOG.md in the release commit as a deviation from the skill's standard 3-file list (noted in PR intro).
- Build: `DOTNET_ROLL_FORWARD=Major dotnet build HelixTool.slnx -c Release --no-incremental` → 0 Warning(s), 0 Error(s). Test: `DOTNET_ROLL_FORWARD=Major dotnet test HelixTool.slnx -c Release --no-build` → Passed 2384, Failed 0, Skipped 9.
- Single commit `34b4bec` "release: v0.11.0 (version bump)" (4 files: csproj, server.json, release-notes, CHANGELOG.md); pushed `release/v0.11.0`; opened PR #158 (ready for review, not draft), with "Do not auto-merge" note for Larry's tag-push step.
- Version rationale: minor bump — new observable CLI/MCP behavior (paging envelope, structured errors, collect command) plus a critical data-loss bug fix, no incompatible CLI/MCP tool signature changes.
- Next step after merge: `git checkout main && git pull`, verify version fields, `git tag -a v0.11.0 -m "Release v0.11.0"`, `git push origin v0.11.0` to trigger `publish.yml`.
- Note: this worktree had unrelated uncommitted `.squad/agents/lambert/history.md` edits present at task start; stashed them off `release/v0.11.0` (stash message `lambert-history-edits-not-for-release`) rather than including them in the release commit — restore via `git stash pop` on the appropriate branch when resuming that work.
