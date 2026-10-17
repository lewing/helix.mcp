# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, and test architecture.

**Current Focus:** v0.11.0 release cycle: Helix-aware evidence plan (#153), CLI paging (#154), collector (#155), post-merge findings (#156/#157). All major test suites passing; release shipped.

## Durable Test Patterns

- Prefer deterministic observable end-state over sleeps, retries, or race-outcome assertions. If a background task is part of contract, expose/await an internal completion handle rather than adding public hooks.
- Public APIs can mask low-level behavior under test; inspect SQLite rows directly when API TTL filters would hide whether startup cleanup actually deleted data.
- Disk-to-disk SQLite backup can copy WAL journal-mode headers; force `journal_mode=DELETE` when a clean snapshot fixture is required.
- Regression-first methodology is valuable for platform defects: write a Windows-only discriminator that fails pre-fix and turns green post-fix, with `[WindowsOnlyFact]` reporting a real skip on non-Windows platforms.
- Do not use test seams or global mutable hooks when existing reachable paths can produce the required failure deterministically.
- Keep assertion data in one fixture source when integration and unit tests share a scenario.

## Environment Note

Local runtime may be .NET 11 preview only while projects target `net10.0`; use `DOTNET_ROLL_FORWARD=Major` for builds/tests when needed.

## Learnings (Summary)

**Helix evidence-plan monitor coverage:** Live public timelines compact enough to distill into deterministic unit fixtures (build 1621192 monitor-only with `Monitor Helix Jobs` warning; build 1621133 combines same monitor + canceled leg + real artifact). Human CLI needs explicit `incompleteDetails[].code` rendering, not just `incompleteReasons`; otherwise deterministic collector failures (`monitor_unparseable`, `helix_failures_truncated`) invisible in non-JSON mode. Parser tests should cover direct GUID extraction, console fallback scoping, tree-line parsing, warning/tree dedupe separately from service tests.

**Acquisition error contract coverage (#152):** Added recorded/offline acquisition coverage for AzDO HTTP classification, Helix service classification, MCP structured error filtering/tool integration, CLI `--json` envelopes, cache failure non-persistence, eval/offline cache errors, `azdo_helix_jobs` fallback preservation. Assertions pin structured fields (`kind`, `provider`, `operation`, `resource`, `httpStatus`, `retryAfterSeconds`) not human message wording. Updated stale tests encoding old success-shaped behavior (`null`, `[]`, `string.Empty`) to #152 `HlxAcquisitionException`/structured acquisition contract while keeping legitimate empty successes green.

**Locked-out revisions:** Revised two rejected commits after Dallas locked out original author: paging contract falsely reported non-final pages as complete (revised: any partial page must fail closed with `helixFailuresTruncated=true`); AzDO HTTP 200 empty-body log ambiguity (revised: validate logId in logs-list or timeline, if absent throw `not_found`, prevent zero-length cache persistence). Both revised: 2095 full suite / 9 skipped; targeted #152 tests 98/98 passed.

**Snapshot miss and replay coverage:** Snapshot-miss tests assert wire kind (`"not_in_snapshot"`) and `source="snapshot"` not just enum names; keeps old `NotFound/cache` behavior visibly distinct at CLI/MCP boundaries. Negative replay coverage needs both storage-level and decorator-level assertions: store can persist rows correctly while `CachingAzdoApiClient`/`CachingHelixApiClient` still skip replay and fall through to `Offline*ApiClient`. Schema-v2 validation brittle around hand-written SQL; tiny hand-crafted v2 snapshot is good canary before relying on broader exporter tests.

**Collector stress test harness:** For collector command tests, use actual `CollectCommands` path once present, but keep fakes at `IAzdoApiClient`/`IHelixApiClient` + real caching decorators so snapshot consistency and negative replay tested through same cache keys MCP uses. Duplicate Helix monitor rows should be asserted at manifest/resource level (one attempt per tool/job/workItem, unique IDs) then through resume of both newly-written and hand-seeded legacy manifest with duplicate IDs. Streaming file-download regressions need stream that returns partial first read then throws from `ReadAsync`; with total bytes capped to sum of final successful downloads, leaked budget from failed reads becomes observable.

## Recent Sessions (Most Recent ~5)

### 2026-10-02T14:45 — PR 2 rejected-artifact revision

Revised Ripley's rejected collector implementation per Dallas gate review: explicit `--download-helix-files` streams selected Helix files to temp, enforces per-file and total byte caps before cache artifacts, records `size_limit`/`total_size_limit` skips, makes small files replayable offline. Eval-mode AzDO replay selects credential-free snapshot partition (`public` or `cache-xxxxxxxx`) with override via `HLX_EVAL_AZDO_PARTITION`; multi-partition snapshots fail closed. Resume rehydrates cached values so dependent phases remain represented. Focused 12/12 pass; full suite 2222 passed / 9 skipped.

### 2026-10-02T15:20 — Post-merge review regression coverage (#154/#155)

Added one focused offline regression per Copilot finding with finding IDs in test names. Coverage spans AzDO continuation tokens, marker-prefixed SQLite metadata, envelope cache-key existence, public partition selection, disabled-cache rejection, concurrent attempt accumulation, resume validation, manifest argv redaction, authenticated build cache keys, >1000 Helix failure paging merge, test-scope-all replay, selected-file resume/retry/classification/budget, export retention under small cache, non-retried transient outcomes, negative retry delays, Retry-After precedence. Collector stress fixtures must guarantee unique suggested-fetch coordinates. Small-cache export tests assert public contract: snapshot not marked complete when collected evidence evicted before export. Focused 20/20 pass; full 2242 / 9 skipped / 0 failed.

### 2026-10-02T16:00 — PR #156 review-finding regression coverage

Added one focused regression per Copilot finding plus property-style parser round-trip over every AzDO key-builder shape with adversarial org/project segments (`build`, `log`, `timeline`, `abcdef12`, `public`, `*-log`) in public and authenticated cache contexts. Duplicate Helix monitor rows asserted at manifest level and through resume. Streaming file-download regressions test partial first read then throw, with total bytes capped to final successful downloads. Focused 7/7 pass; full 2278 / 9 skipped after Ripley's concurrent paging update.

### 2026-10-02 — Independent-review provider-boundary regressions

Added `Collect/IndependentReviewRegressionTests.cs` with finding-suffixed coverage for real AzDO mid-body failures, real Helix SDK classification/negative recording/offline replay, export isolation, snapshot validation fail-closed manifests, all supported AzDO outcomes, optional-download cache-cap protection, expired-but-present final verification, cache-local default manifests. Verified discrimination against disposable untouched HEAD archive: all 32 cases fail pre-fix. Fake `IHelixApiClient` throwing classified exceptions proves envelope handling, not provider boundary classification. Real Helix wrapper tested through `HelixApiClient.CreateForTesting(HelixApiOptions)` with `HttpClientTransport(fakeHttpClient)` and SDK retries disabled to reach real SDK `RestApiException<ApiError>` and transport-wrapper paths offline.

### 2026-10-02 — Release cycle completion

All v0.11.0 test suites passing: full suite 2351 pass / 9 skipped / 0 failed (with `DOTNET_ROLL_FORWARD=Major`). Scope: helix-aware evidence plan, CLI paging, collector azdo-build, post-merge hardening, pre-release fixes. Required-evidence retention, atomic optional headroom/resume accounting, classified SDK discovery fallback all validated. Snapshot export/validate/eval readback surfaces verified. No platform-specific CI failures. Release shipped v0.11.0 @ e713995 (NuGet + container published).

