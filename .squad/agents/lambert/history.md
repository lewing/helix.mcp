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

### 2026-10-03 — Audit P0-1 timeline/access-first contract coverage (gpt-6.1-sol)

Added 92 cases in `TimelineViewContractTests`, `TimelineViewReplayTests`, `TimelineProjectorContractTests`, and `EvidenceDeliveryContractTests`, sharing sanitized anonymous 7.1 timeline subsets from public builds 1621192/1600801. Fixtures preserve provider ordinal, record/parent GUIDs, attempts, log IDs, the concrete Process.Tests monitor failure, and five genuinely repeated XA1040 warnings. Coverage includes every selector/preset/projection/expansion, Phase topology, error ranking, formatting-only dedupe, Unicode, malformed graphs, AND/empty selections, fingerprints, truthful final-page/issue-window completeness, executable recovery, oversized records/all/name selectors, private evidence reading, SDK HTTP and actual stdio wire budgets, actual CLI envelopes/raw/output, immutable credential-free old-key snapshot replay, and evidence-plan/Helix-jobs regressions. No production changes or commits; legacy direct tests migrated to explicit full/compact and injected the new evidence-store dependency.

Verified regression-first: initial released-head-compatible wire tests compiled and produced 45 failures/9 validation passes before the new timeline surface existed. Final WIP focused run: 11 passed/81 failed/92 total. Final full run: 2395 passed/81 failed/9 skipped/2485 total; all failures are new contract tests, with zero legacy failures. The implementation gate remains RED. Reported expected-versus-actual gaps to Ripley: missing `records`/`ok`/explicit null `next`, 16-character versus full SHA-256 fingerprint, GUID-reordered full/compact rows, invalid selectors accepted, final record/issue pages incorrectly complete, missing exact recovery, unshaped oversized full output, reader byte-budget parameter absent/binary auto base64, split UTF-8 scalar, eval delivery inside the snapshot, and CLI flags/envelope/output not yet implemented. Logs and fetched timelines persist in session `files/lambert-timeline/`.

Durable harness learnings: SDK `CallToolResult.IsError` is nullable; successful results normally omit it, so assert `IsError is true` rather than `Assert.False(IsError)`. Actual child CLI/stdio tests should use `dotnet exec --runtimeconfig HelixTool.Tests.runtimeconfig.json --depsfile HelixTool.Tests.deps.json HelixTool.dll`; the copied CLI dependency graph alone cannot locate shared `Microsoft.Extensions.Http`. Compare deterministic evidence-plan content after normalizing `GeneratedAt`, not by expecting two live generation timestamps to match. Capture the actual HTTP response body/stdio JSON-RPC line for byte assertions instead of treating DTO JSON length as the wire size. Keep pure projector discriminators as well as protocol tests so missing envelope fields do not mask ordering/completeness bugs.

### 2026-10-03 — P0-1 completed-implementation verification (supersedes WIP gate above)

After Ripley's implementation handoff, the original 92 contract cases reached 91 passed/1 failed; full suite reached 2475 passed/1 failed/9 skipped. Added four adversarial default-budget cases sharing one escaped-long-name fixture across actual HTTP and child stdio. These exposed a broader default shaping bug in the then-current four-times-budget fallback: 200/400 repeated escaped name segments produced HTTP bodies of 15783/28783 bytes and stdio messages of 15971/28971 bytes against the default 12288 target, exceeding the harness-spill floor. Reported the concrete reproduction to Ripley; those four tests pass after the subsequent implementation update.

Final verification: **95 passed/1 failed/96 focused cases; 2479 passed/1 failed/9 skipped/2489 full-suite cases**. The single remaining implementation gap is `AccessFirst_OversizedNameAndSelectorAction_UseReadableDeliveryWithBoundedHeader`: a complete, readable delivery receipt is **4351 bytes against a requested 4096-byte target**. Kept the assertion intact and reported the exact overage; no other regressions remain. Final logs are `files/lambert-timeline/focused-handoff.log`, `full-handoff.log`, and the pre-fix discriminating `remaining-wire-bugs.log`.

Added cleanup of specifically issued test evidence files/sidecars, including eval-mode files outside the immutable snapshot, without deleting shared delivery directories. No production edits, commits, or branch changes. Build/test slot released to Ripley. The gate is not entirely green until the remaining requested-budget overage is fixed or explicitly accepted by the product owner.

### 2026-10-03 — Exact default-budget boundary verification

The replacement absolute `+1024` overage allowance still exceeded the default 12288-byte target for a smaller escaped name: 150 repetitions produced HTTP 12533 bytes and stdio 12721 bytes. Added this boundary to both shared-fixture transport theories, bringing coverage to 98 cases, and reported the actual measured overages. Both new cases pass after the subsequent implementation update; assertions were not relaxed.

Latest results supersede earlier counts: **97 passed/1 failed/98 focused; 2481 passed/1 failed/9 skipped/2491 full suite**. Only the known 4096-byte receipt assertion remains red. Logs: `absolute-overage-boundary.log` records the discriminating failures; `focused-boundary-handoff.log` and `full-boundary-handoff.log` record current results under session `files/lambert-timeline/`. No production edits or commits by Lambert.

### 2026-10-03 — Multi-row budget boundary and final handoff

Added six cases (120/130/140 escaped-name repetitions, HTTP and actual stdio) with a second diagnostic Task to cover multi-row pagination, not only unique selections. Gating the absolute overage tolerance on `selectedTotal > 1` still let the 140-repeat case exceed the default target: HTTP 12627 bytes and stdio 12815 bytes versus 12288. Reported this separately; all six cases pass after the latest shaping update.

Corrected a related test assumption: a 4096-byte compact request can legitimately return a complete evidence-file receipt rather than an advancing inline row. The byte-shortened inline paging test now uses 8192 (still forcing multiple pages for the real fixture), asserts actual wire size on every page, and changes to 12288 when checking budget-independent fingerprints. The separate 4096-byte receipt assertion was not weakened.

Final authoritative verification: **103 passed/1 failed/104 focused cases; 2487 passed/1 failed/9 skipped/2497 full-suite cases**. Only `AccessFirst_OversizedNameAndSelectorAction_UseReadableDeliveryWithBoundedHeader` remains red. Logs: `multirow-budget-boundary.log` captures the pre-fix failures; `focused-multirow-final.log` and `full-multirow-final.log` capture current results. History/tests saved, no production edits or commits by Lambert, build/test slot released.

### 2026-10-03 — Dallas rejection contract fixes and complete-fixture gate

Applied Larry/Dallas option (a): positive targets clamp to 8192..16384, with both `requestedMaxResponseBytes` and effective `maxResponseBytes` asserted, including final framed receipts/read responses and preserved provenance. The previous 4096 receipt assertion now verifies requested=4096/effective=8192 and exact recovered original content; it is not a ceiling-only relaxation.

Added 53 cases across `TimelineDallasGateRegressionTests`, `TimelineDallasCliRegressionTests`, and `TimelineAuthenticatedHttpRegressionTests`, plus six additional target-normalization boundaries in the existing suite. Imported complete sanitized anonymous timelines as deterministic embedded gzip resources: **1524 records for 1621192 and 189 for 1600801**, preserving every provider ordinal/ID/parent/attempt/log ID/issue message while omitting workers, URLs and issue-data maps. Small slices remain useful for pure discriminators but no longer stand in for complete-selection/ranking acceptance.

New gates cover every numbered production item 2–9: real production HTTP host with fresh scoped CacheOptions and independent AzDO identities (same Helix root token), same-context reader round trip and other-context denial; all/file/chunked complete selections with omitted/small limits and deliberate filters; CLI delivery parity; nondefault and auto-shortened executable continuations; single clipped undeduplicated issue recovery; failed monitor ordering over the complete real selection and first page; omitted-limit nonzero issue windows; empty-source CLI enum validation, zero limits and overflowing read lengths; and both new-file and existing-cache.db eval-output destinations with exact outside-snapshot recovery argv executed.

Verified red against an isolated archive of **ce0631b**, overlaying only current tests: `FullyQualifiedName~Dallas` yielded **49 failed/15 passed/64 total**, warning-free, with a failing discriminator for every reviewed defect. The HTTP test reproduced the actual immediately returned reader's credential-partition lookup error rather than a setup failure. Logs: session `files/lambert-timeline/dallas-red-baseline.log`.

Fixed two coupled harness assumptions without weakening production contracts: selector tests now follow all pages rather than require eight rows inline at 12288; timeline replay fixture setup normalizes the file-backup WAL header with `PRAGMA journal_mode=DELETE` before freezing/hash capture, matching production serialized snapshot behavior. Snapshot file/sidecar mutation assertions remain intact.

After Ripley's readiness signal, current focused gate: **163 passed/0 failed**; complete suite: **2547 passed/0 failed/9 skipped/2556 total**, no compiler warnings. Current logs: `dallas-focused-current.log` and `dallas-full-current.log`. No remaining implementation bugs observed by these gates, no production edits or commits by Lambert, build/test slot released.

### 2026-10-03 — Second Dallas rejection: WAL, production serialization and physical containment

Added 17 cases in `TimelineReReviewRegressionTests` and `TimelineWalPartitionRegressionTests`. Real schema-v2 fixtures checkpoint only main/public evidence, then commit authenticated metadata or acquisition errors exclusively to WAL with autocheckpoint disabled and the writer held open. Gates cover auth-only auto selection, mixed-partition fail-closed selection, explicit partition acceptance and actual CLI replay, unchanged persistent database/WAL hashes, and unchanged snapshot file names. Existing SHM lock contents are explicitly transient SQLite reader state, not committed evidence.

Production wire gates now walk complete 1600801/1621192 defaults over the real HTTP host and actual child stdio process, including raw framing and production nullable fields; no global null-omission migration is allowed to hide budget errors. Further gates verify human file path/hash/read-action receipts, logical incomplete-graph and auto-materialized partial-page exit codes independent of verified physical bytes, outside-directory symlink snapshot containment, and failed monitor hints before generic Task errors in Core/HTTP.

Pinned **9620719** baseline with current tests overlaid: **14 failed/3 passed/17 cases**, warning-free, discriminating all five reviewed issues. Measured MAUI HTTP 12797 and stdio 12671 bytes versus effective 12288. Corrected the old full-projection filter test to follow every page rather than assume all 11 records remain inline after production null fields are accurately counted. Child temp directories now live under the owned fixture root, so opaque human-delivery files from red tests are also cleaned safely.

First current verification: 179 passed/1 failed focused; 2563 passed/1 failed/9 skipped full. The remaining real defect was auto-materialized backing `complete=false`, `returned=1`, `selectedTotal=2` exiting 0 because physical delivery verified; reported separately and rechecked after Ripley's fix.

Final authoritative results: **180 passed/0 failed focused; 2564 passed/0 failed/9 skipped/2573 total full suite**, no compiler warnings. Logs in session `files/lambert-timeline/`: `rereview-red-baseline.log`, `rereview-focused-final.log`, `rereview-full-final.log`. No remaining covered implementation bugs, no production edits or commits by Lambert, build/test slot released.

### 2026-10-03 — Third rejection: ancestor chains and pre-delivery completeness

Added 10 cases in `TimelineThirdReviewRegressionTests`. Directory-link probes place an existing `artifacts` directory beneath an outside symlink into the owned eval snapshot, testing both a new nested destination and an existing immutable sentinel. All snapshot files are hash-compared and the exact safe recovery argv is executed. Complete-record siblings compare 32-byte inline and 1 MiB auto-materialized successful records, verifying backing `selectedTotal=returned=1`, full raw issue coverage, logical `complete=true`, physical SHA, and exit 0. Genuine record pages and issue windows remain exit 2, with issue windows checked for auto/inline/file/chunked delivery.

Pinned **7ce53b4** baseline: **5 failed/5 passed/10 cases**, warning-free. Reproduced both ancestor-link mutations and the complete oversized record's false exit 2; additionally caught partial issue windows in file/chunked modes falsely exiting 0. Reported those partial-window cases so the complete-record fix could not become another physical-bytes-equals-logical-completeness shortcut.

After Ripley's readiness, final gate is **190 passed/0 failed focused; 2574 passed/0 failed/9 skipped/2583 total full suite**, no compiler warnings. Logs under session `files/lambert-timeline/`: `third-red-baseline.log`, `third-focused-current.log`, `third-full-current.log`. No remaining covered behavioral bugs, no production edits or commits by Lambert, build/test slot released.

### 2026-10-03 — Fourth rejection: one logical-completeness source of truth

Added 26 cases in `TimelineFourthReviewRegressionTests`: eight exact/over-covering issue-limit cases across auto/inline/file/chunked, two exact-record/default-ancestor-expansion one-of-two pages, and a 16-case delivery x graph coverage x issue coverage matrix. Consolidated the complete oversized record into `TimelineViewFixture.FullIssueLookup`, reused by the third-review control. Materialized backing bytes, SHA, counts, raw issue coverage, incomplete graph details and logical completeness are asserted before CLI exit codes, so physical delivery success cannot mask incomplete evidence.

Pinned **ed4e9d6** baseline: **14 failed/12 passed/26 cases**, warning-free. All eight covering issue-limit cases incorrectly exited 2; both ancestor-expanded partial pages incorrectly exited 0; four complete matrix combinations incorrectly exited 2. Twelve incomplete controls remained green. Durable rule: issue coverage alone cannot replace overall projector completeness, because complete issues can coexist with a missing ancestor or a partial record selection. Ripley's final shared `IsLogicallyComplete` uses the actual pre-receipt `result.Complete`; receipt row clearing and delivery flags do not alter that captured truth.

After readiness, authoritative current-worktree verification: **216 passed/0 failed focused; 2600 passed/0 failed/9 skipped/2609 total full suite**, no compiler warnings. Logs: session `files/lambert-timeline/fourth-red-baseline.log`, `fourth-focused-current.log`, `fourth-full-current.log`. No remaining covered implementation bugs; no production edits, commits or branch changes by Lambert. Build/test slot released.
