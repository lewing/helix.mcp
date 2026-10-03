# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, and test architecture.

**Current Focus:** Issue #149 regression coverage is complete and committed with Ripley's fix as `b47deae` on branch `lewing-fix-test-results-failures-hidden`. Full pre-condense detail was archived to `history-archive.md` on 2026-10-01T00:45:00Z.

## Durable Test Patterns

- Prefer deterministic observable end-state over sleeps, retries, or race-outcome assertions. If a background task is part of the contract, expose/await an internal completion handle rather than adding public hooks.
- Public APIs can mask low-level behavior under test; inspect SQLite rows directly when API TTL filters would hide whether startup cleanup actually deleted data.
- Disk-to-disk SQLite backup can copy WAL journal-mode headers; force `journal_mode=DELETE` when a clean snapshot fixture is required.
- Regression-first methodology is valuable for platform defects: write a Windows-only discriminator that fails pre-fix and turns green post-fix, with `[WindowsOnlyFact]` reporting a real skip on non-Windows platforms.
- Do not use test seams or global mutable hooks when existing reachable paths can produce the required failure deterministically.
- Keep assertion data in one fixture source when integration and unit tests share a scenario.

## Recent Completed Work

### 2026-09-11 — Startup cache eviction lifecycle tests (#129)

Added deterministic coverage for `SqliteCacheStore.StartupMaintenance`, disposal joining, eval-mode no-op behavior, stale comment cleanup, and non-cancellation worker-fault propagation. Full suite reached 1990 passed / 8 skipped after repeated validation.

### 2026-09-11 — Pool scope and artifact replacement coverage (#130)

Added pre-fix and post-fix regression coverage for cross-root SQLite pool interference and Windows artifact replacement. The artifact test proved permissive source share alone was insufficient and drove the accepted `File.Replace` fix. Final validation: targeted 387 passed / 7 skipped; full suite 1995 passed / 9 skipped; CI green.

### 2026-09-30 — AzDO test-results silent-empty regressions (#149)

Added `src/HelixTool.Tests/AzDO/TestResultsSilentEmptyTests.cs` with 15 focused tests: real AzDO run JSON maps `unanalyzedTests` to `FailedTests`; 203/text-html/302 sign-in responses assert auth guidance across list and non-list paths; test-run result 404s assert explicit not-found/deleted errors; caching wrappers do not cache auth exceptions; and request URLs prove `$top` never exceeds 10,000. Updated stale 404 expectation to the new contract. Final validation: new class 15/15 passed; AzDO namespace 1009 passed / 2 skipped; full suite 2016 passed / 9 skipped.

## Environment Note

Local runtime may be .NET 11 preview only while projects target `net10.0`; use `DOTNET_ROLL_FORWARD=Major` for builds/tests when needed.

## Learnings

### 2026-10-02T11:29:27-05:00 — Helix evidence-plan monitor coverage

- Live public timelines are compact enough to distill into deterministic unit fixtures: dotnet/runtime build 1621192 is monitor-only with a `Monitor Helix Jobs` warning for `System.Diagnostics.Process.Tests`; build 1621133 combines the same monitor shape with a canceled `osx-arm64 Debug Libraries_CheckedCoreCLR` leg and a real `Logs_Build_Attempt1_osx__arm64_Debug_Libraries_CheckedCoreCLR` artifact.
- Human CLI output needs explicit `incompleteDetails[].code` rendering, not just human `incompleteReasons`; otherwise deterministic collector failures like `monitor_unparseable` and `helix_failures_truncated` are invisible in non-JSON mode even when JSON is machine-readable.
- Parser tests should cover direct GUID extraction, console fallback scoping, tree-line parsing, and warning/tree dedupe separately from service tests; that isolates production parsing bugs from evidence-plan wiring bugs.

## 2026-10-02T12:05:00Z — Session handoff: Test findings + #152 phasing

Cross-agent context from Scribe:

**For Lambert (Testing):**
- Your evidence-plan Helix tests are high quality (255 targeted / 2 failed); the 2 failures are implementation gaps, not test bugs
- Missing stable reason codes in human CLI output: Dallas's design requires them for deterministic collectors (`[monitor_unparseable]`, `[helix_failures_truncated]`)
- Ripley's implementation calculated codes but did not emit them in human formatter
- Recommendation: flag both test failures as "design-implementation gap" for Ripley to fix before merge
- Dallas approved acquisition error contract (#152) in parallel; your error test patterns will scale to MCP + CLI JSON envelopes for acquisition failures
- Phasing: error contract first, then scanner pagination/completeness, then bundle writer

**Parallel work:**
- Ash's scanner analysis aligns with Dallas's #152 phasing recommendation
- All decisions merged and ready for Larry's review

### 2026-10-02T12:04:09-05:00 — Acquisition error contract coverage (#152)

- Added recorded/offline acquisition coverage for AzDO HTTP classification, Helix service classification, MCP structured error filtering/tool integration, CLI `--json` envelopes, cache failure non-persistence, eval/offline cache errors, and `azdo_helix_jobs` fallback preservation of primary acquisition errors. Assertions pin structured fields (`kind`, `provider`, `operation`, `resource`, `httpStatus`, `retryAfterSeconds`) rather than human message wording.
- Updated stale tests that encoded old success-shaped behavior (`null`, `[]`, `string.Empty`, `HelixException`, `InvalidOperationException`) to the #152 `HlxAcquisitionException`/structured acquisition contract, while keeping legitimate empty successes (`200` empty logs, valid `value: []`, empty Helix file lists) green.
- Useful implementation feedback caught during test-first iteration: invalid-response paths initially omitted `httpStatus`, MCP structured content initially exposed enum names instead of stable wire strings, composite timeline failures should be represented as incomplete results with `timelineAcquisitionError`, and unexpected non-acquisition exceptions should not trigger Helix fallback. Ripley fixed the implementation gaps during the session; final focused acquisition validation was 67 passed / 0 failed, full suite 2088 passed / 9 skipped.

### 2026-10-02T12:55:00-05:00 — AzDO empty-log metadata validation follow-up (#152)

- Dallas's rejected live case proved raw HTTP `200` + zero-byte AzDO log bodies are not sufficient evidence of success: build 1621466/log 999999 returns an empty body, but the build logs list and timeline both exclude that log ID. Service-level validation now treats that shape as `not_found` while preserving genuinely empty logs when the ID appears in either metadata source.
- The raw AzDO client can still model provider transport literally (`200` empty text => `""`); ambiguity is resolved in `AzdoService`, where build-log list and timeline metadata are available and failures from those metadata calls can propagate as their own classified acquisition errors.
- Cache tests should pin both failure non-persistence and empty-success non-persistence for full AzDO logs. Skipping zero-length full-log cache writes is the safe P0 because a cached empty body can outlive the metadata context needed to distinguish "real empty log" from "nonexistent log ID."

### 2026-10-02T12:50:00-05:00 — Locked-out revisions: paging and empty-log fixes

- Revised two rejected commits after Dallas locked out original author (Ripley):
  - **Commit 28beceb rejection:** paging contract falsely reported non-final pages as complete. Ripley's calculation `offset + shown < total` missed final-page cases. Revised to: any partial page must fail closed with `helixFailuresTruncated=true`, `truncated=true`, `complete=false`, and `incompleteDetails[].code == "helix_failures_truncated"`; only pages where `limit >= total` can be complete. Updated reason text to avoid false "showing first N" claims on later pages.
  - **Commit 5f11d95 rejection:** AzDO HTTP 200 empty-body log ambiguity. Dallas's spec required metadata validation in `AzdoService.GetBuildLogAsync`: after direct fetch returns `""`, validate requested logId in logs-list (already fetched for tail calls) or timeline records; if absent, throw `not_found` acquisition error; if present, return `""`. Prevent cache persistence of zero-length logs until metadata validation proves logId exists.
- Both revisions validated: 2095 full suite passed / 9 skipped; targeted #152 tests 98/98 passed; live `azdo log 1621466 999999 --json` now exits 1 with `kind=not_found`, `provider=azdo`, `operation=get_build_log`, `resource.logId=999999`.

### 2026-10-02T13:30:00-05:00 — PR #153 acquisition regression follow-up

- Regression coverage should assert the serialized public boundary (`AcquisitionError`, CLI `{ ok:false, error }`, MCP `structuredContent`) for secret-bearing URLs, not just exception fields; SAS token leaks can survive if only service-level messages are checked.
- Eval/offline corrupt-cache tests are most useful as endpoint theories over every cached API method with `Offline*ApiClient` underneath; the expected discriminator is `provider=cache`, `kind=invalid_response`, so corrupt primary evidence cannot silently degrade into a cache miss/not_found.
- CLI acquisition error handling needs a central wrapper shared by Helix and AzDO commands. Removing per-command catches without invoking the wrapper causes JSON-capable commands to throw `HlxAcquisitionException` instead of emitting the machine-readable failure envelope.

### 2026-10-02T13:55:00-05:00 — Snapshot miss and replay coverage

- Snapshot-miss tests should assert the wire kind (`"not_in_snapshot"`) and `source="snapshot"` rather than relying on enum names only; that keeps old `NotFound/cache` behavior visibly distinct at CLI/MCP boundaries.
- Negative replay coverage needs both storage-level and decorator-level assertions: the store can persist rows correctly while `CachingAzdoApiClient`/`CachingHelixApiClient` still skip replay and fall through to `Offline*ApiClient`, producing `not_in_snapshot`.
- Snapshot schema-v2 validation is brittle around hand-written SQL; a validator query bug (`ORDER BY` term not in result set) can break all exports, so a tiny handcrafted v2 snapshot is a good canary before relying on broader exporter tests.

### 2026-10-02: PR #153 snapshot misses implementation and hardening (commits ae35fd3 → b7a97e1)
- **Iteration 1 (ae35fd3):** Implemented `not_in_snapshot` acquisition kind, SQLite schema-v2 `cache_acquisition_errors` table, AzDO/Helix replay logic, service-level empty-log recorder, initial test coverage
- **Dallas rejection:** 3 required fixes identified:
  1. Terminal-state probes inside `catch` blocks could replace original acquisition error
  2. Empty-log `not_found` recorded without terminal-build check (risk of permanent offline marking for in-progress logs)
  3. Negative rows outlive validity; expired negatives never evicted before snapshot export
- **Iteration 2 (b7a97e1) — Hardened revision:**
  1. Probe failures now safely caught; original `HlxAcquisitionException` preserved unchanged
  2. Empty-log `not_found` gated on `IsBuildCompletedAsync`; in-progress builds rethrow without recording
  3. Atomic positive/negative cache clearing; `EvictExpiredAsync` deletes expired entries before export
- **Test coverage:** Windows-flaky expired-negative test fixed; all 3 findings covered by regression scenarios
- **Validation:** Full suite 2175 passed / 0 failed / 9 skipped, 0 warnings
- **Dallas verdict:** APPROVED (b7a97e1) — Ready for merge

### 2026-10-02T13:45:00-05:00 — PR 1 paging/cache-key compatibility coverage

- Added test-first PR 1 coverage in `AzdoPagingPr1Tests`: CLI JSON list envelopes for `azdo changes`, `azdo test-runs`, `azdo test-results`, `azdo test-attachments`, and `azdo artifacts`; `--offset`/`--limit`, `--all`, `--allow-truncated`, invalid `--all` combinations, exit 2 vs 0 semantics, `azdo log --full`, no-secret cache provenance, complete-key writes, eval-mode MCP slicing from complete keys, legacy capped-key replay, negative-cache precedence, and capped-live-key then eval-`--all` miss behavior.
- Updated stale cache tests for the new v3 window/complete keys and compatibility writes: bounded successful results now write both v3 window and legacy keys, complete test-runs write `testruns:v3:{buildId}:all`, and valid empty test-results may cache the v3 window while legacy empty capped snapshots still replay as misses.
- Implementation gap found: `azdo log --full` writes the full `log:{buildId}:{logId}` entry, but offline MCP `azdo_log` with default tail still calls `GetBuildLogsListAsync` first for tail optimization. If the snapshot only has the full log key, it fails with `not_in_snapshot` for `list_build_logs` instead of tailing the cached full log. The design requires capped `azdo_log` to read from the full-log key offline.

### 2026-10-02T14:15:00-05:00 — PR 2 collect azdo-build coverage

- Added `src/HelixTool.Tests/Collect/AzdoBuildCollectorPr2Tests.cs` with offline fakes and CLI-style `CliAcquisitionErrorPipeline`/`TestConsoleCapture` invocation of `collect azdo-build`. Coverage exercises runtime-like collection, manifest schema/attempt shape, export manifest placement, snapshot validation, eval-mode CLI/MCP replay consistency for every collected ok surface, scope-policy skips, evidence-plan incompleteness exit 2, transient retry exhaustion, deterministic negative replay, size-cap skip semantics, resume/idempotency, and secret redaction.
- Test harness lesson: for collector command tests, use the actual `CollectCommands` path once present, but keep fakes at `IAzdoApiClient`/`IHelixApiClient` plus real caching decorators so snapshot consistency and negative replay are tested through the same cache keys MCP uses.
- Current implementation gap after the coordinator-directed assertion update: skipped/failed attempts must keep a stable serialized `bytes: null` field; focused collector tests otherwise pass against the updated transient and size-cap contracts.

### 2026-10-02T14:45:00-05:00 — PR 2 rejected-artifact revision

- Revised Ripley's rejected collector implementation per Dallas's gate review: explicit `--download-helix-files` now streams selected Helix files to temp files, enforces per-file and total byte caps before writing cache artifacts, records `size_limit`/`total_size_limit` skips, and makes small accepted files replayable offline. Tests now assert small-file eval replay, over-cap `not_in_snapshot`, and total-cap skips.
- Eval-mode AzDO replay now selects a credential-free snapshot cache partition (`public` or `cache-xxxxxxxx`) from the snapshot/manifest, with explicit override via `HLX_EVAL_AZDO_PARTITION`; multi-partition snapshots fail closed without selection. Tests cover auth-scoped collection replaying with no `AZDO_TOKEN`, plus multi-partition fail/select behavior.
- Resume now rehydrates cached values by running the same cache-backed service call instead of returning `default`, so dependent log/test/Helix phases remain represented in resumed manifests. Focused collector/eval tests passed (12/12), then the full suite passed: 2222 passed / 9 skipped / 0 failed.

### 2026-10-02T15:20:00-05:00 — Post-merge review regression coverage (#154/#155)

- Added one focused offline regression per Copilot review finding, with finding IDs in test names. Coverage spans AzDO continuation tokens, marker-prefixed SQLite metadata, envelope cache-key existence, public eight-hex org partition selection, disabled-cache rejection, concurrent attempt accumulation, resume validation of prior failures/successes, manifest argv redaction, authenticated build cache keys, >1000 Helix failure paging merge, test-scope-all replay, selected-file resume/retry/classification/budget behavior, export retention under small cache, non-retried transient outcomes, negative retry delays, and Retry-After precedence.
- Collector stress fixtures must guarantee unique suggested-fetch coordinates. A duplicate fake Helix job/work-item key looked like a lost concurrent append (399/400) even after production locking; fix the fixture before reporting concurrency bugs.
- Small-cache export tests should assert the public contract: a snapshot must not be marked complete when any collected evidence is evicted before export. The missing evidence may be console/log metadata or downloaded artifacts depending on eviction order, so tests should accept a manifest-level incomplete/missing-cache detail rather than overfitting to one operation.
- Final validation after Ripley's fixes: focused `FullyQualifiedName~Finding` suite passed 20/20; full `HelixTool.Tests` passed 2242 / skipped 9 / failed 0.

### 2026-10-02T16:00:00-05:00 — PR #156 review-finding regression coverage

- Added one focused regression per Copilot finding 4169759118, 4169759181, 4169759234, 4169759267, 4169759299, and 4169759328, plus a property-style parser round-trip over every AzDO key-builder shape with adversarial org/project segments (`build`, `log`, `timeline`, `abcdef12`, `public`, `*-log`, `log-fresh`) in public and authenticated cache contexts.
- Useful harness pattern: duplicate Helix monitor rows should be asserted at the manifest/resource level (`one attempt per tool/job/workItem` and unique IDs) and then through resume of both the newly written manifest and a hand-seeded legacy manifest with duplicate IDs; this catches both fetch dedupe and `LoadPriorAttemptsAsync` duplicate tolerance.
- Streaming file-download regressions need a stream that returns a partial first read and then throws from `ReadAsync`; with total bytes capped to the sum of final successful downloads, leaked budget from failed reads becomes observable because later files would be skipped.
- Validation: focused `FullyQualifiedName~Finding416975|FullyQualifiedName~AzdoCacheKeyParser_RoundTripsAllBuilders` passed 7/7; full `HelixTool.Tests` passed 2278 / skipped 9 / failed 0 after Ripley's concurrent paging update fixed the transient >10,000-row skip-batching failures.

### 2026-10-02 — Independent-review provider-boundary regressions

- Added `Collect/IndependentReviewRegressionTests.cs` with finding-suffixed coverage for real AzDO mid-body failures, real Helix SDK classification/negative recording/offline replay, export isolation across builds and auth partitions, snapshot validation fail-closed manifests, all supported AzDO outcomes, optional-download cache-cap protection, expired-but-present final verification, and cache-local default manifests.
- Verified discrimination against a disposable archive of untouched HEAD `e0a257a`: all 32 cases fail before the production fixes. The archived build was removed after preserving its output in the session's `files/independent-review-baseline-tests.log`; no branch switches or commits were needed.
- A fake `IHelixApiClient` throwing `HlxAcquisitionException` is not evidence that the real SDK/client/cache boundary classifies or records errors. `AzdoBuildCollectorPr2Tests.RecordingHelixApiClient` has this blind spot in its console/files/file-open failure queues; `SnapshotMissReplayTests.HelixLiveMode_NotFoundCompletionProbeFailure_RethrowsOriginalAndDoesNotRecord` also injects already-classified provider/probe failures. CLI JSON and MCP envelope tests inject classified exceptions intentionally and only prove envelope handling, not provider acquisition.
- Service tests such as `HelixAcquisitionErrorTests` and `HelixOperationClassificationRegressionTests` inject raw HTTP failures at the interface but still bypass SDK behavior. The actual Azure SDK transport wraps `HttpRequestException` in `Azure.RequestFailedException`, exposing an additional uncaught exception path that those tests cannot detect.
- The real Helix wrapper is now tested through `HelixApiClient.CreateForTesting(HelixApiOptions)` with `HttpClientTransport(fakeHttpClient)` and SDK retries disabled. This reaches the real SDK's `RestApiException<ApiError>` and transport-wrapper paths while remaining fully offline. The initial untouched-HEAD discrimination used private-field injection before Ripley's named factory landed; the regression helper now needs no reflection or ambiguous constructor overload.
- Deterministic expiry tests can move SQLite `expires_at` behind the current instant in the collector's pre-Helix progress callback, after startup maintenance has completed. Verify that live TTL-filtered reads miss, final verification still reports `ok`, and exported eval replay succeeds for both the all-results and derived Failed rows.
- SnapshotExporter validates before publication, so corrupt source fixtures only test `snapshot_export_failed`. Testing the collector's separate `snapshot_validation_failed` branch requires a synchronous post-export/pre-validation callback that removes an exported artifact and then checks both persisted manifests.
- Test-first feedback found and Ripley corrected three closely coupled implementation gaps: unclassified `TaskCanceledException` during AzDO body reads, noncanonical Helix operation/resource names, and `Azure.RequestFailedException.Status == 0` hiding the wrapped HTTP exception's 403/404. Artifact mutation/inspection must recurse into the snapshot's per-job artifact directories; top-level enumeration was a test bug, not a production failure.
- Final validation: focused independent-review tests passed 32/32; full `HelixTool.Tests` passed 2310 / skipped 9 / failed 0, with no compiler/analyzer warnings. Implementation bugs found during iteration are resolved. Changes made by Lambert are limited to the new test file and this requested history append; no production edits, branch changes, or commits.

### 2026-10-02 — Reviewer-designated all-test collection revision (a20fb36)

- Revised the Dallas-rejected collector artifact after Ripley's independent-review edits landed, using Larry's explicit authorization for Lambert to edit production on rejected artifacts. No commits or branch changes.
- Added nullable explicit `--max-test-results` consent with a 10,000-row implicit threshold. The guard sums build-wide run totals in a long accumulator before all-outcome result acquisition, refuses insufficient budgets with `test_result_limit` and remediation, and cannot be bypassed by resume. Negative provider totals fail as invalid responses rather than silently reducing the estimate. Policy-only `--allow-incomplete` preserves `complete=false` while allowing exit 0.
- Decoupled attachment acquisition from result scope: default `diagnostic` selects Failed/Error/Timeout/Aborted/Inconclusive/Blocked/Warning, with a 1,000-request build-wide cap. Wider `all` requires explicit `--max-test-attachments`; deterministic run/result-ID ordering selects bounded coverage, and exclusions/limits are aggregated per run instead of producing millions of manifest rows.
- Added synchronized, throttled stderr attachment progress and five-second acquisition heartbeats, with cancellation joining timer tasks. A TimeProvider-based throttling test checks the exact 1,999ms/2,000ms boundary without sleeps. Result acquisition and attachment selection are separate phases; only bounded diagnostic candidates are retained.
- Added 20 reviewer-revision cases for threshold boundaries, insufficient/raised budgets, safe wide totals, invalid totals, failed-only compatibility, CLI exit/JSON behavior, budget changes on resume, older manifest readability, exact attachment-call counts, default Failed replay/export, heartbeat/cancellation, and throttling. Final full suite: 2330 passed / 9 skipped / 0 failed; no compiler/analyzer warnings.
- Bounded live build 1621192: ordinary all scope refused 1,405,433 estimated rows in 4.37s, exit 2, explicit policy skip, no all-result acquisition. With `--max-test-results 2000000`, collection completed in 311.22s, exit 0, complete=true, exactly 1,405,433 acquired rows and five attachment-list entries instead of roughly 1.4M requests. Stderr showed run acquisition and elapsed heartbeats throughout; JSON stdout parsed cleanly.
- Live default collect/export and resume/export both remained complete and validated (6.94s and 0.78s; resume reused 95 entries). Credential-free offline replay of run 44916260 returned exactly five Failed rows, exit 0. Reports/TRX are retained in session files; temporary validation caches/snapshots were cleaned afterward.
- Kane handoff: README, CLI reference, and Unreleased changelog now document both budgets, independent attachment scope, manifested policy gaps, stderr progress, and cache-local manifests/export isolation. Removed the stale NotRunnable outcome from the CLI reference.

### 2026-10-02 — Larry-authorized final-gate release blocker repairs

- Repaired production only for Dallas's two escalated blockers on `lewing-pr156-review-fixes`, with Larry's explicit reviewer-lockout override. No branch changes or commits; Kane's README/docs/CHANGELOG edits were left untouched. Updated only the related `CollectCommands` source help.
- R1: acquire all required Helix details, consoles, and file lists in a completed first phase, then verify collected evidence before any optional download. Derive new-artifact headroom from SQLite's actual total artifact footprint, not manifest metadata byte estimates or a pre-console reserve. Conservatively reserve unrelated and already-cached artifacts too; this run's optional writes therefore do not need to trigger LRU eviction, and no new SQLite pinning API/schema was necessary.
- Optional byte accounting has two atomically reserved limits: the requested build-wide optional-file total and new bytes that fit remaining artifact capacity. Cached resume files consume the requested total but not cache-growth headroom again; failed/skipped stream reads release both new-byte reservations. Compare against remaining budgets rather than adding potentially overflowing long values.
- Added the exact `RequiredEvidenceSurvivesOptionalDownloads_DallasGate` reproduction at concurrency 1 and 6: 1 MiB cache, 600 KiB required console, 700 KiB optional file. Both now produce a `total_size_limit` skip, complete=true, exit 0, and validated snapshots with exact credential-free console replay. Additional regressions force two work-item downloads to overlap after both required consoles exist, assert one accepted/one skipped optional file and exact cache occupancy, exercise an exact-cap accepted file through resume, and reserve a hot unrelated artifact without sacrificing required evidence.
- R2: `ListJobsByBuildAsync` now uses the same `ClassifyAsync` boundary as the other six public Helix acquisition methods. Audited all seven methods: none now has a separate SDK-exception catch path. Added real-SDK fake-transport 403/404 regressions through both the internal `CreateForTesting` seam and `AzdoService.GetHelixJobsAsync`, asserting `list_helix_jobs_by_build`, source/build/count, classified HTTP metadata, timeline fallback, and complete=false. Real-SDK caller cancellation propagates without timeline fallback. Extended the six cached-method regressions to test wrapped HTTP 403/404 exceptions for every method, including negative recording/offline replay.
- Regression-first evidence: before production edits, Dallas's exact eviction cases failed with missing required consoles, concurrent acquisition violated the required-before-optional barrier, and discovery transport cases exposed raw `Azure.RequestFailedException`. Baseline output is retained in session files as `dallas-gate-baseline.log`.
- Final validation: `DOTNET_ROLL_FORWARD=Major dotnet build HelixTool.slnx --no-restore --no-incremental -warnaserror -v:minimal` succeeded with 0 warnings / 0 errors. Complete suite: 2351 passed / 9 existing skips / 0 failed. Build output, full-suite output, and TRX are retained in session files as `dallas-gate-build.log`, `dallas-gate-full.log`, and `dallas-gate-full.trx`; regression fixtures cleaned their temporary caches/snapshots.

### 2026-10-02 — PR #157 environment isolation and collector lifecycle regressions

- Consolidated environment-sensitive and process-global CLI tests into the existing `AzdoTokenEnv` collection (`DisableParallelization = true`), removing the redundant API-key, file-search, evidence-console, and snapshot-process collection definitions. Explicit selector readers need isolation too, not just tests calling `Environment.SetEnvironmentVariable`; collectors also read Helix auth, and CLI fixtures mutate process-wide console writers/exit codes.
- Moved 23 classes: `AzdoCacheKeyReviewRegressionTests`, `AzdoBuildCollectorPr2Tests`, `IndependentReviewRegressionTests`, `HttpContextHelixTokenAccessorTests`, `CacheOptionsTests`, `SearchLogTests`, `HelixOperationClassificationRegressionTests`, `CliJsonAcquisitionEnvelopeRegressionTests`, `ApiKeyMiddlewareTests`, `ApiKeyScopedRequestIsolationTests`, `HttpTransportSessionModeTests`, `SearchFileTests`, `TrxParsingTests`, `XunitXmlParsingTests`, `AzdoSearchLogTests`, `SearchBuildLogAcrossStepsTests`, `SnapshotCommandOutputTests`, `AzdoEvidenceSurfaceTests`, `AcquisitionRedactionRegressionTests`, `AzdoCliAcquisitionErrorTests`, `AzdoPagingPr1CliTests`, `AzdoPagingPr1CacheCompatibilityTests`, and `SnapshotMissErrorShapeTests`. Existing token/security/eval-auth classes already used this collection.
- Replaced unconditional partition/token cleanup with original-value restoration in collector/eval fixtures. The cache-key regression now clears its selector only within a save/restore `try/finally`. File-search toggle tests preserve preexisting values, API-key host disposal restores its environment in `finally`, and eval fixture filesystem setup runs before changing the environment. Added three regression guards for collection membership and non-null sentinel preservation through fixtures/toggle tests.
- Added 30 lifecycle cases in `Collect/CollectorHardErrorReviewRegressionTests.cs`: three failure points (initial auth resolution, final auth status, final verification), explicit nested/default cache-local manifest paths, collector/CLI entry points, and unexpected-error/provider-timeout variants; six additional combinations within those 30 exercise actual caller cancellation. Persisted JSON and CLI output must agree on `complete=false`, `exitCode=1`, and `collector_hard_error`, retain prior successful attempts, and omit raw token values, SAS signatures, exception names, and stack frames.
- Initial 12 unexpected-error cases failed before production edits; six caller-cancellation cases passed. Tracked `src` at HEAD was byte-equivalent to requested baseline `9a939a4`, so the first run discriminates that exact source behavior without a branch switch or archive build. The first production draft persisted manifests but leaked raw exception-message token assignments into manifest/CLI JSON; all 12 cases caught this. Ripley's safe generic message fixed those cases. Added provider-timeout variants to expose the second coupled bug: excluding every `OperationCanceledException` from the lifecycle catch also excludes provider timeouts when the caller token is not canceled.
- Intermediate validation: focused run was 24 passed / 12 provider-timeout failures; complete suite including the timeout variants was 2372 passed / 12 provider-timeout failures / 9 existing skips, with no unrelated regressions. All originally requested unexpected-error and caller-cancellation cases passed after safe-message hardening; the remaining implementation blocker was the broad cancellation exclusion, reported to Ripley. The first full draft suite was 2360 passed / 12 secret-leak failures / 9 existing skips. One overlapping build failed generating `MvcTestingAppManifest.json`; subsequent validation runs use one MSBuild node and a single coordinated build owner. Lambert edited tests and this requested history only; no production edits, branch changes, or commits.
- Clean rebuild follow-up: Ripley's earlier green report used an older case set. Explicit `dotnet build ... --no-restore --no-incremental -m:1 -warnaserror` rebuilt every referenced project with 0 warnings/errors; the freshly rebuilt boundary-only suite still produced 12 unexpected-error passes / 12 provider-timeout failures. This rules out cached binaries or an archived baseline as the cause. Distinguish exception type from cancellation ownership: `TaskCanceledException` with an uncanceled caller token is a provider failure, whereas an actually canceled caller token must propagate without a hard-error manifest.
- Final validation after Ripley's cancellation-ownership fix: all 33 new regression cases passed under a warning-as-error build; full suite passed 2384 / skipped 9 existing platform cases / failed 0. Added source org/project/buildId/buildUrl assertions for every boundary, including auth-resolution failures, after production moved the pure source parse ahead of auth. Both implementation bugs found during test-first iteration are resolved. Final logs/TRX: session `files/pr157-green-focused.*` and `files/pr157-full-final.*`; baseline and intermediate failure evidence remains under `pr157-*`. Use unique TRX filenames with `-warnaserror`: overwriting an existing result file emits a runner warning and can yield exit 1 despite every test passing.
- Post-warning follow-up: after Ripley removed an unused exception binding from the minimal hard-error fallback catch, reran a fully non-incremental warning-as-error build of the test project and every reference: 0 warnings / 0 errors. The subsequent complete suite again passed 2384 / skipped 9 / failed 0. Latest artifacts are `files/pr157-post-warning-build.log` and `files/pr157-post-warning-full.log/.trx`; no broad bin/obj deletion, commits, or production edits by Lambert were needed.
