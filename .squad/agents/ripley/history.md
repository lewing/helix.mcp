# Ripley — History (Condensed)

## Executive Summary

**Role:** Backend development, performance optimization, audit, and verification work.

**Current Focus:** v0.11.0 release cycle: Helix-aware evidence plan (#153), CLI paging (#154), collector (#155), post-merge findings (#156/#157), pre-release hardening. All implementations shipped; full test suite passing.

## Durable Patterns and Learnings

- Prefer service/decorator boundaries for cache and offline behavior; mock `IHelixApiClient` / AzDO client interfaces rather than transport internals.
- Normalize optional parameters at semantic boundary and reuse same algorithm for URL construction, cache keys, validation.
- Strict-mode compatibility lives in `CallToolFilter` aliases before parameter binding; remove alias keys after promotion so unknown-param checks stay meaningful.
- Startup/background maintenance must retain completion handle, pin construction-time cutoffs, dispose in cancel → bounded join → fault observation → pool cleanup order.
- Windows file replacement needs right primitive: `File.Replace` for existing artifacts, `File.Move` for absent artifacts. Don't rely on exception-driven retry.
- Eval/snapshot tests should prove no live network fallback by using offline clients that throw and asserting exact snapshot-derived plans.

## Recent Completed Work

### 2026-09-11 — Startup cache eviction lifecycle (#129) + Pool scope and artifact replacement (#130)

Implemented Dallas-accepted lifecycle design in `SqliteCacheStore.cs`: retained `StartupMaintenance`, construction-time cutoff pinning, cancellation checkpoints, bounded disposal with non-cancellation fault propagation. Delivered scoped SQLite pool cleanup and artifact replacement fixes. R2 scoped pool clearing, made exporter source reads `FileShare.Read | FileShare.Delete`; R3 used `File.Replace` for existing artifacts after Windows regression proved `File.Move(overwrite: true)` insufficient. Full suite 1995 passed / 9 skipped; Ubuntu/Windows/Squad CI green.

### 2026-09-30 — AzDO test run/result failure visibility (#149)

Fixed silent-empty AzDO failures: derive test-run failed count from `unanalyzedTests`; treat redirects/HTTP 203/non-JSON success as auth failures; scope test-result 404s to explicit not-found/deleted; cap `$top` at 10,000; avoid caching empty lists. Anonymous live calls now throw auth guidance; failed counts correct (`run 44793916 Failed: 9`). Hardened `SqliteCacheStore` artifact reads/writes against Windows share conflicts with retries. Updated test cache compatibility: version keys when semantics change, treat cached empty test-results as misses (old releases could persist misleading empties). Full suite 2016 passed / 9 skipped.

## Learnings (Summary)

**Queue-monitor evidence plan:** Timeline issues sufficient for `azdo evidence plan` to represent failed Helix work items without Helix API calls; keep parsed monitor jobs out of artifact `entries[]`, but fail closed with `incompleteDetails[].code` when monitor rows unparseable/unresolved/paged/truncated.

**Acquisition error classification:** Must classify at provider boundary and serialize with explicit converter; MCP SDK default JSON options can ignore enum converter attributes in `structuredContent`, so use acquisition-owned JSON options for error envelopes. Eval/offline cache misses now `provider=cache` acquisition errors, not `InvalidOperationException`. Valid HTTP-200 empty logs remain successful even when requested logId surprising.

**Lockout protocol:** When lead rejects commit and locks out author, different agent must revise. AzDO empty-log ambiguity: empty body + absent logId = `not_found`; empty body + logId present = success. Validate in `AzdoService.GetBuildLogAsync` (service layer), not `AzdoApiClient` (provider layer), not cached until proven.

**Acquisition error defense-in-depth:** Sanitize URL query/fragment values in `AcquisitionErrorFactory` and again in `HlxAcquisitionException` so manually constructed test/MCP errors cannot leak SAS tokens. CLI-wide acquisition handling belongs in `ConsoleAppFramework` global filter; direct unit tests instantiating command classes bypass filter and must exercise generated CLI/filter pipeline or shared writer explicitly.

**Snapshot replay and negative cache:** `not_in_snapshot` only for true misses; deterministic live provider failures are negative cache rows keyed exactly like positive evidence, replay with original provider/kind + `source=snapshot`, `replayed=true`, `recordedAt`. Public live validation must avoid Azure CLI auth-scoped AzDO cache keys (or provide same env token in eval). PR 1 paging/cache keeps MCP capped, CLI `--all` writes complete versioned keys (`changes:v2`, `testruns:v3`, etc.) that eval-mode capped calls probe first and slice in memory. SQLite metadata must encode NUL bytes; otherwise full-log rows round-trip as empty and capped offline log tails miss/corrupt.

**Collector final details:** Capture final AzDO auth state after live fetches (not before), because `CachingAzdoApiClient` updates `CacheOptions.AuthTokenHash` lazily on first authenticated call. Optional Helix file downloads never evict required evidence: clamp budget to `min(--max-total-bytes, cacheCap - requiredBytes)`, not raw policy. Post-write verification bypass TTL for presence checks (`GetMetadataIgnoringTtlAsync`). Default manifest path resolve next to effective cache root, not CWD. SQLite TEXT binding silently truncates at embedded NULs; all NUL-bearing/marker-prefixed values must encode before binding.

## Recent Sessions (Most Recent ~5)

### 2026-10-02: PR #153 Copilot code review fixes (commit ae35fd3)

Addressed 5 Copilot code-review findings. Implemented SAS/URL redaction for CLI/MCP serialization. Fixed eval cache corrupt-cache misclassification (→ `cache/invalid_response`). Classified Helix service calls at individual API/download boundary. Structured `download_helix_file` failure classification. Extracted `CliAcquisitionErrorPipeline` seam for improved testability. Dallas verdict: APPROVED. Full suite validation passed; Lambert proceeded to snapshot-misses revision.

### 2026-10-02: PR #156 review fixes

Centralized AzDO cache-key parsing in `AzdoCacheKeys.TryParse`; authenticated shapes matched before public, suffix segment counts encode builder contract. AzDO list traversal lets continuation tokens fill window, falls back to `$skip` only when no continuation returned. Collector-required evidence includes derived cache writes, not only fetches. Resume tolerant of duplicate attempt IDs (last-wins). Helix uploaded-file streaming classifies read-time `HttpRequestException`, non-caller `TaskCanceledException`, `IOException`. Live test-results `outcomes` rejects `NotRunnable`; all-scope uses only accepted filters, derives default `Failed` replay key from all-results cache.

### 2026-10-02: Independent pre-release review fixes (findings 1–7)

Response-body reads happen *after* `SendAsync`'s own try/catch returns headers, so every post-send `ReadAsStringAsync` needs own classification. Single shared `ReadResponseBodyAsync` helper keeps classification in sync across `GetAsync`/`GetListAsync`/`ThrowOnUnexpectedError`. `RunAsync` per-attempt loop needs true last-resort `catch (Exception)` after `HlxAcquisitionException` and caller-`OperationCanceledException` catches that synthesizes `transport_error` and records attempt. Helix SDK boundary `HelixApiClient` must classify every call, not just `ListJobsByBuildAsync`. Azure.Core pipeline wraps raw `HttpRequestException` as `Azure.RequestFailedException` with `Status == 0`; original HTTP survives on `ex.InnerException`. Classify both exception types. `--export` without `--cache-dir` collects into isolated per-run temp cache (not shared root). Snapshot validation failure flips `manifest.complete = false` + adds `snapshot_validation_failed` incomplete detail. `--test-scope all` requests full AzDO `TestOutcome` enum (no `NotRunnable`). Optional Helix file downloads clamp to `min(--max-total-bytes, cacheCap - requiredBytes)`. Post-write verification bypasses TTL. Default manifest path resolves next to effective cache root. Independent finding 6 (Azure SDK blocker) escalated per Dallas; all others resolved.

### 2026-10-02 — v0.11.0 release cut (PR #158)

Branched `release/v0.11.0` from `origin/main` @ `7ebedec`; bumped `HelixTool.csproj <Version>` and `server.json` versions to `0.11.0`. Wrote release notes to `.squad/release-notes/v0.11.0.md` covering #153 (Helix-aware evidence plan + acquisition errors), #154 (CLI paging, BREAKING), #155 (`hlx collect azdo-build`), #156 (post-merge + SQLite NUL fix by PureWeen), #157 (pre-release hardening). Included Breaking, Data-loss, Security sections. CHANGELOG: renamed `[Unreleased]` → `[v0.11.0] — 2026-10-02`, inserted fresh empty `[Unreleased]`. Build 0 warnings/errors; test 2384 passed / 0 failed / 9 skipped. Single commit `34b4bec` "release: v0.11.0"; pushed `release/v0.11.0`; opened PR #158 (ready, not draft) with "Do not auto-merge" for tag-push step.

### 2026-10-02 — Final release validation and tagging

All release blockers (R1 evidence-eviction, R2 Helix job-discovery, R3 documentation) resolved and shipped. v0.11.0 tagged @ e713995 (NuGet + container published). Full test suite passing: 2351 pass / 9 skip / 0 fail (with DOTNET_ROLL_FORWARD=Major). Scope: helix-aware evidence plan, CLI paging, collector azdo-build, post-merge hardening, pre-release fixes. Release notes document breaking changes, data-loss fix (v0.10.3-and-earlier cache corruption requiring `hlx cache clear` + re-collect), security hardening (URL redaction), new features (paging, structured errors, collect), and bug fixes (18 post-merge findings + NUL metadata).

