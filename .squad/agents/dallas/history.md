# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, strict-mode architecture, and merge-gate reviews.

**Current Focus:** Recent merge-gate work centered on startup cache eviction (#129) and pool/artifact replacement (#130). Full pre-condense detail was archived to `history-archive.md` on 2026-10-01T00:45:00Z.

## Durable Decision Principles

- Reframe issue titles into root-cause defect bundles before assigning implementation; tracking a task is not enough if its cutoff/time semantics remain wrong.
- Make minimal-public-impact requirements mechanically checkable with exact file boundaries.
- Prefer narrow enumerated exception handling over prose rules like "avoid broad swallowing."
- Ban race-outcome assertions; tests should prove deterministic end-state and observable contracts.
- Treat changed SDK defaults as silent diffs: force important defaults into source and guard with tests.
- Verify agent-reported release metadata and claimed platform constraints against primary sources or repo greps.
- Evidence-driven scope amendments are valid when red/green tests disprove an architectural assumption.

## Recent Decision Cycles

### 2026-09-08 — v0.10.0 release prep

Prepared release commit `00c18d2` on `lewing-release-v0-10-0`; bumped the three authoritative version surfaces and promoted CHANGELOG. Build, test, and pack validation succeeded. No tag/push was performed.

### 2026-09-11 — Startup cache eviction lifecycle (#129)

Accepted a design with construction-time cutoff pinning, retained internal `StartupMaintenance`, cancel → bounded join → fault observation disposal, and deterministic tests with no public hooks or race-outcome assertions. Approved Ripley/Lambert/Kane results after full validation.

### 2026-09-11 — Pool scope and artifact replacement (#130)

Approved pre-fix regression coverage, then accepted an evidence-driven scope amendment when Windows tests proved `File.Move(overwrite: true)` insufficient despite permissive source share. Final accepted fix: scoped SQLite pool cleanup plus `File.Replace` for existing artifacts and `File.Move` for absent artifacts. Validation: targeted and full suites green; Ubuntu, Windows, and Squad CI success.

## Standing Architecture Context

- Stable folders: `src/HelixTool.Core/{Helix,AzDO,Cache}/` and `src/HelixTool.Mcp.Tools/{Helix,AzDO}/`.
- Keep business logic in services, MCP tools thin, and caching/offline behavior behind decorators.
- AzDO/Helix API clients should maintain strict input validation, cache isolation by auth context, and explicit auth failure surfacing.

## Learnings

### 2026-10-02T11:29:27-05:00 — Helix-aware evidence plan

- For `azdo_evidence_plan`, keep `entries[]` artifact-only when adding queue-monitor support; represent parsed monitor failures in additive top-level `helixFailures[]` to avoid weakening existing `status`/`candidates[]` invariants.
- Timeline issues already contain enough arcade monitor evidence for v1 (`Work item ... failed (...)` and failure-tree rows), so preserving the three-AzDO-GET/no-Helix-call contract is preferable to coupling evidence planning to Helix auth or API availability.
- Monitor detection should be parse-first and name-hint-second: parseable monitor issue content is robust evidence, while `Monitor Helix Jobs` remains only a fallback hint for unresolved incomplete reasons.
- For deterministic CI evidence collection, human `incompleteReasons` are insufficient: scripts need stable `incompleteDetails[].code`, explicit truncation totals/pages, and Helix fetch coordinates so parse gaps fail closed instead of looking like an empty failure set.

### 2026-10-02T11:29:27-05:00 — Acquisition error contract

- ModelContextProtocol 2.2.0 can carry tool-call `structuredContent` on `CallToolResult`, but `McpException` itself is text-only; structured acquisition errors therefore need a `CallToolFilter` that catches a Core exception and returns an `isError` result.
- Treat provider acquisition and collection policy as separate layers: Core reports `not_found`, `access_denied`, `rate_limited`, `timeout`, `transport_error`, or `invalid_response`; scripts decide whether a missing resource is retryable, skippable, or fatal.
- Empty success is only valid after a successful provider shape (`value: []`, empty HTTP-200 text log, zero file matches after list); 404/204/empty JSON/malformed payloads must not become `null`, `[]`, notes, or empty MCP strings.
- Preserve evidence-plan exit 2 for produced-but-incomplete output; use exit 1 plus a JSON error envelope for hard acquisition failures instead of assigning separate process codes per acquisition kind.

### 2026-10-02T12:04:09-05:00 — Evidence-plan paging gate

- Rejected commit 28beceb only on Helix failure paging: a later page returning fewer rows than `helixFailureTotal` must still be considered a truncated representation of the full plan, not a complete plan. Otherwise a deterministic collector can start at a non-zero offset and get exit 0 with only a suffix of the evidence.
- Regression tests must not encode "last page is complete" unless that page contains the entire failure set (`limit >= total` or equivalent). Completeness is about full selected-failure evidence, not whether more rows exist after the current offset.

### 2026-10-02T12:40:00-05:00 — Acquisition error gate

- Approved Lambert's evidence-plan paging revision because `helixFailureTotal > helixFailures.Count` now fails closed even for a non-zero final page, and live `1620983 --helix-failure-offset 1 --helix-failure-limit 1` exits 2.
- Rejected the #152 acquisition-error implementation on live AzDO behavior: `_apis/build/builds/{id}/logs/{missingLogId}` can return HTTP 200 with a zero-byte body while the build logs list proves the ID is absent, so `azdo log` still returns success-shaped `""`.
- Empty HTTP-200 text logs are not self-authenticating. Treat them as success only after provider metadata proves the log ID exists: logs list first, timeline log references as the in-progress/zero-byte fallback, otherwise structured `not_found`.
- Caching must not persist ambiguous zero-byte direct log responses before validation; skipping cache writes for empty full AzDO logs is acceptable for P0.

### 2026-10-02T12:40:00-05:00 — Acquisition error re-review

- Approved commit 2794a94 for #152: empty direct AzDO log bodies now validate against logs-list metadata first, timeline log references second, and only then become structured `not_found/get_build_log`; referenced zero-byte logs remain successful `""`.
- The right cache boundary for this ambiguity is below service validation: never cache zero-length full AzDO log bodies as success, while preserving non-empty full-log and range caching behavior.
- Live validation matters for AzDO's unusual HTTP-200/zero-byte missing-log shape: `azdo log 1621466 999999 --json` now exits 1 with the stable structured envelope instead of `""`.

### 2026-10-02T12:44:36-05:00 — Snapshot misses and negative replay

- Decided true eval/offline cache misses need a distinct acquisition kind `not_in_snapshot` rather than overloading `not_found provider=cache`; keep provider as `cache` and make scripts/MCP inspect `error.kind`.
- Negative snapshot replay should preserve the original provider failure (`kind/provider/operation/resource/httpStatus`) and add `source="snapshot"`, `replayed=true`, and `recordedAt`; only `not_found`, `access_denied`, and provider `invalid_response` are recordable.
- Live mode must never serve negative entries. Positive cache writes delete same-key negatives; eval mode checks positive evidence first, replayed negatives second, and offline-stub `not_in_snapshot` last.
- Schema v2 should add `cache_acquisition_errors`; v1 snapshots remain valid with a warning and simply cannot replay original provider failures.
- The empty-log metadata validation path requires a narrow AzDO failure recorder because that `not_found/get_build_log` is classified in `AzdoService`, above `CachingAzdoApiClient`.

### 2026-10-02T13:20:00-05:00 — PR #153 round 2 gate

- Approved Ripley's Copilot review-fix artifact and Lambert's minimal `CliAcquisitionErrorPipeline` seam: redaction, corrupt-cache classification, per-call Helix classification, download classification, and central CLI acquisition filtering looked aligned.
- Rejected the snapshot-misses/negative-replay artifact on negative-cache side effects: completion probes inside recording predicates must not mask the original endpoint acquisition error; service-level empty-log `not_found` recording must honor terminal-build gating; and schema-v2 maintenance must evict expired negative rows and avoid positive/negative key conflicts before snapshot export.
- Recommended Lambert revise the rejected Ripley artifact.

### 2026-10-02T13:45:00-05:00 — PR #153 round 2 revision gate

- Approved Lambert's `b7a97e1` revision: AzDO/Helix terminal-state probe failures during negative recording now skip persistence and preserve the original endpoint acquisition error, with regressions asserting the original operation and no negative row.
- Empty-log `not_found/get_build_log` recording is now terminal-build gated at the service recorder boundary; completed builds persist the negative row, while in-progress or unknown builds rethrow without recording.
- Negative cache writes now transact away same-key positive metadata/artifact rows, positive writes still clear negatives, and eviction removes expired `cache_acquisition_errors` before export; durable lesson: conflict-free snapshot validity should be proven at the row-mutator boundary plus at least one export/validation path for expiry.

### 2026-10-02: PR #153 design and review gates (snapshot misses + review fixes)
- **Design lead:** Proposed `not_in_snapshot` acquisition kind, negative cache schema (v2), replay logic, empty-log recorder requirements
- **Round 1 review:** APPROVED Ripley's review fixes (ae35fd3); identified 3 findings in Lambert's snapshot-misses implementation
  - Required fixes: probe safety (terminal-state checks), terminal-build gating, negative eviction
- **Round 2 review:** APPROVED Lambert's hardened revision (b7a97e1) after all 3 findings resolved
  - Probes no longer replace original errors; terminal-state check gated on completed builds; atomic conflict clearing + expired eviction
- **Validation:** 2175 passed / 9 skipped, 0 warnings; no secret-leak path in serialized surface
- **Status:** PR #153 ready for merge with all gate verdicts APPROVED

### 2026-10-02T13:32:27-05:00 — Scanner collect and paging design

- Designed Vitek scanner workflow as two coupled PRs: first CLI paging/cache-key compatibility, then `hlx collect azdo-build`.
- Critical cache decision: collector-grade complete list keys must satisfy capped eval-mode MCP calls by probing complete keys first and slicing in memory; scanner scripts must not guess every future MCP cap key.
- Keep MCP agent tools capped in v1 and avoid MCP cursors; deterministic completeness belongs in CLI collection and the manifest.
- Choose `hlx collect azdo-build <build-id-or-url>` over `snapshot populate` because v1 populates a live cache and optionally exports a snapshot.
- Manifest contract records every fetch/skipped/failure outcome with existing `AcquisitionError` shape, retry count, paging metadata, bytes/hash, and overall `0/2/1` completeness semantics.
- Snapshot MCP launch remains docs-first: `HLX_EVAL_SNAPSHOT=<snapshot> hlx mcp`; add `hlx mcp --snapshot <path>` only if Vitek's harness cannot set stdio env vars.
- Artifacts written:
  - `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/collect-and-paging-design.md`
  - `.squad/decisions/inbox/dallas-collect-and-paging.md`

### 2026-10-02T14:10:00-05:00 — PR 1 paging/cache-key gate

- Approved HEAD `8415f88` for CLI paging/cache-key compatibility. Required fixes: none.
- Confirmed the CLI `--json` envelope and default exit `2` on truncated capped output were intentional PR 1 design choices, despite breaking old bare-array scripts; users who want success on capped output must pass `--allow-truncated`, and full-output scripts should pass `--all`.
- Confirmed MCP tool shapes remain unchanged (`LimitedResults<T>`/string), with capped eval-mode MCP calls slicing complete collector keys first and falling back to legacy capped keys when complete keys are absent.
- Cache compatibility accepted: versioned complete/window keys, legacy capped-key fallback, schema-v1/v2 snapshot readability, NUL metadata encoding forward compatibility, and #153 positive-before-negative-before-`not_in_snapshot` replay ordering.
- Local targeted test run compiled but testhost could not start because this machine lacks `Microsoft.NETCore.App 10.0.0`; gate relied on reported full suite `2210 pass / 0 fail / 9 skip / 0 warnings` plus static review.

### 2026-10-02T14:45:00-05:00 — PR 2 collect gate

- Rejected HEAD `7a36ac4` for `hlx collect azdo-build` despite reported `2219 pass / 9 skip / 0 warnings` and successful live/offline validation.
- Required `--download-helix-files` to become functional in v1 via download-to-temp streaming caps before cache population; the current unconditional `size_limit` skip makes the explicit flag misleading.
- Required auth-scoped snapshots to replay without live AzDO credentials by selecting a recorded non-secret partition id in eval mode, with fail-closed handling for multiple partitions.
- Found a resume correctness bug: cached prior attempts return `default`, so dependent log/test/Helix phases can disappear while the resumed manifest still exits 0.

### 2026-10-02T15:20:00-05:00 — PR 2 collect re-review

- Rejected HEAD `12d1b8a` narrowly because required stale user-facing messaging remains in source: `SnapshotCommands` still says auth-scoped replay requires identical `AZDO_TOKEN` and Azure CLI / `az` partitions are not reproducible, and `CollectCommands` still says `--download-helix-files` records unsupported skips.
- Accepted Lambert's implementation fixes: Helix file downloads now stream to temp, enforce per-file/total caps before caching, delete over-cap temps, and replay only in-cap files; eval replay selects non-secret snapshot partitions credential-free and fails closed for multi-partition snapshots; resume rehydrates cached values and preserves downstream coverage.
- Live-mode auth partitioning remains intact through credential-derived AzDO cache keys; no credential material found in manifest/test coverage surfaces.
- Validation: `DOTNET_ROLL_FORWARD=Major dotnet test src/HelixTool.Tests/HelixTool.Tests.csproj --no-restore --verbosity minimal` passed with 2222 passed / 9 skipped / 0 failed; non-roll-forward testhost still cannot run locally without `Microsoft.NETCore.App 10.0.0`.
- Ripley and Lambert are locked out; escalate this final text fix to Larry.

### 2026-10-02T15:45:00-05:00 — PR 2 collect final text/doc gate

- Approved HEAD `c10caff`: Kane fixed the two stale help strings, and source help now correctly describes capped Helix upload downloads plus credential-free snapshot partition replay.
- Skimmed README, CLI reference, and CHANGELOG against source for collect flags/defaults, exit codes, manifest path, and `HLX_EVAL_AZDO_PARTITION`; no PR-blocking factual errors found in current `hlx collect` docs.
- Noted the older v0.10.2 changelog paragraph still records prior auth-scoped snapshot behavior historically, but the new Unreleased entry supersedes it and it does not block the PR.
- No new validation run for this text-only final gate; relied on reported full suite 2222 passed / 9 skipped / 0 warnings plus static review of `git log origin/main..HEAD`.

### 2026-10-02T15:55:00-05:00 — Post-merge findings gate

- Rejected HEAD `85c5bd1` on two release-blocking gaps after reviewing the 18 Copilot post-merge finding fixes and PureWeen's SQLite NUL data-loss fix.
- Accepted that all 18 findings have named regression tests and the implementation shape matches the intended fixes: complete list keys, auth partition parsing, disabled-cache rejection, deterministic attempt merges, resume verification, argv redaction, authenticated cache keys, Helix truncation cleanup, all-test replay, selected-file resume/retry/budgets, export evidence verification, retry-delay validation, and Retry-After handling.
- Required a bounded AzDO continuation-token loop in `AzdoApiClient.GetListAsync`: the current loop follows `x-ms-continuationtoken` with no max page count or repeated-token guard, so malformed provider responses can hang list/collect paths indefinitely.
- Required fuller compatibility for legacy marker-prefixed plaintext metadata: `DecodeMetadataValue` now catches invalid Base64 but still silently decodes existing `hlx:nul-base64\n...` plaintext when the suffix happens to be valid Base64.
- Accepted the one-hour Retry-After safety ceiling, per-task merge determinism, snapshot validation of empty/corrupt raw log rows, and live-mode auth partitioning; release notes should call out the unrecoverable v0.10.3-and-earlier cache/snapshot data-loss fix and advise clearing caches/re-collecting snapshots.

### 2026-10-02T16:20:00-05:00 — Post-merge findings re-review

- Approved HEAD `c6b7b9a`: Lambert/Kane resolved both prior blockers with bounded AzDO continuation paging and an unambiguous SQLite metadata v2 envelope.
- `AzdoApiClient.GetListAsync` now caps continuation traversal at 1000 pages, detects repeated continuation tokens and repeated request URLs, fails closed as `invalid_response`, and has regressions for repeated token and page-cap failure.
- `SqliteCacheStore` now stores NUL/marker-prefixed metadata as `hlx:b64:v2:<length>:<sha256>\n<base64>` and decodes legacy `hlx:nul-base64\n` rows only when the decoded value proves old-encoder origin (contains NUL or starts with an encoded marker), preserving valid-Base64 legacy plaintext.
- Verified snapshot export/validate/eval readback surfaces: validation decodes metadata before raw-log integrity checks, collected snapshots replay manifest items offline, schema-v1 snapshots remain accepted with compatibility warnings, and docs/changelog include cache-clear/re-collect guidance for v0.10.3-or-earlier corrupted snapshots.
- Validation: targeted `DOTNET_ROLL_FORWARD=Major dotnet test ... --filter "FullyQualifiedName~AzdoPagingPr1Tests|FullyQualifiedName~SqliteCacheStoreTests|FullyQualifiedName~SnapshotExportTests|FullyQualifiedName~SnapshotEvalModeTests|FullyQualifiedName~AzdoBuildCollectorPr2Tests"` passed 78/78; relied on reported full suite 2271 passed / 9 skipped / 0 warnings for final release breadth.
- Status: APPROVED for merge and release; no blocking issues remain.

### 2026-10-02T16:45:00-05:00 — PR #156 review-fix gate

- Rejected HEAD `a20fb36` despite accepting the six direct Copilot finding fixes: structured AzDO cache-key parsing, raw-log schema detection, suggested-fetch dedupe/resume, derived `Failed` key verification, streaming read classification, and bounded continuation paging all looked correct under static review and targeted tests.
- Diagnosed Ripley's stopped `collect azdo-build 1621192 --test-scope all` as a tests-phase scalability bug rather than a continuation-token loop. Build `1621192` has 49 test runs and 1,405,433 total selected test rows; the largest run `44916566` returned 133,036 all-outcome results in 65.3s, proving paging advances.
- Found the release blocker: after all-scope test-result collection, `CollectTestsAsync` enumerates `list_test_attachments` for every result, including passed and not-applicable rows, implying about 1.4M attachment-list calls for `1621192`. A bounded 180s collect with logs/Helix disabled printed only phase-level progress and timed out silently in `azdo.tests`.
- Required fix: preflight selected test-result totals from `azdo test-runs`, refuse/skip all-scope collection over a safe default unless an explicit `--max-test-results` is raised, make attachment collection safe by default for all-scope runs (failed/unanalyzed/error-like only or an explicit guarded attachment scope/limit), add stderr progress for per-run result/attachment phases, and re-run live validation on `1621192`.
- Validation performed: targeted PR156 tests passed 35/35; both available PR156 snapshots validated under HEAD; authenticated offline capped/default replay returned the expected five failed results for run `44916260`.

### 2026-10-02 — PR #156 pinned gate, confirmed paging and volume evidence

- Confirmed REJECT for release at `a20fb3639dbf9918171fded8c7c6ad90fd9ae9d5`; superseded the draft inbox review with independently measured evidence. Accept the six direct fixes, but require an all-result preflight budget, safe attachment selection independent of result scope, and observable stderr progress before tagging. Lambert is eligible to revise.
- Bounded instrumented client/service probe fetched run `44916566`'s 133,036 unique results in 66.745s: 14 advancing 10,000-row `$skip` pages (last 3,036), no continuation tokens, 285,338,304 HTTP response bytes. Actual uncached HEAD CLI `--all` independently completed in 60.765s, exit 0, complete=true. No paging-loop or 1,000-page-cap defect was reproduced at this scale; do not remove the protective cap.
- Read-only inspection of Ripley's interrupted cache found 13 complete all-outcome result sets (219,424 rows) and 43,427 distinct empty attachment lists persisted over roughly 15 minutes. The apparent hang was hidden acquisition progress; collecting all 1,405,433 build results would otherwise imply about 1.4M attachment-list calls.
- Require build-wide `TotalTests` preflight, explicit sufficiently raised `--max-test-results` above a conservative threshold, and a stable manifest policy skip with complete=false/exit 2 unless policy-only incompleteness is allowed. Avoid `policy_excluded` for budget refusal because existing completeness logic ignores that skip kind. Preserve failed-only replay and capped MCP behavior.
- Focused tests covering actual paging class names, parser, collector, auth/immutability, SQLite, export and validation passed 218 with six platform skips; CLI build had zero warnings/errors. Retained #156 default snapshot validated and replayed five Failed results credential-free. #155/#156 builder formats are unchanged; #155 backward compatibility is supported by static comparison and tests, not a newly exercised live #155 fixture.
- Review stayed read-only except the requested inbox/history records and temporary diagnostic artifacts. Concurrent HEAD cleanup `e0a257a` and subsequent other-agent source changes were observed and left untouched; this verdict does not approve those source changes. All started diagnostic processes ended.

### 2026-10-02 - Final pre-tag gate at 0794f61

- REJECT for release; accept all three prior volume/progress revisions and independent findings 1-5/7 in their reviewed paths, but independent finding 6 remains reproducible. Both Ripley and Lambert authored the implementation; escalate its revision to Larry. Kane is eligible for Markdown-only documentation repairs.
- Deterministic single-worker fixture probe: 1 MiB cache, 600 KiB required console, 700 KiB optional file, each file cap 1 MiB. The pre-Helix reserve sees only about 4,767 AzDO bytes; optional download succeeds and evicts the console, yielding artifact_missing, complete=false, exit 2. Required Helix artifacts must be acquired/reserved or pinned before optional publication, not approximated from pre-Helix attempt bytes. The existing IndepReview6 test only skips oversized optional files and misses this case.
- Additional real-SDK blocker: ListJobsByBuildAsync does not use the shared classifier's Azure.RequestFailedException handling. The regression transport's raw HTTP 403 becomes Azure.RequestFailedException at both the real client and AzdoService.GetHelixJobsAsync; the narrowed HlxAcquisitionException catch cannot preserve structured primary-error timeline fallback. Require client-boundary classification plus real-SDK service regression.
- Source/doc discrepancy confirmed by exported fixture: a multi-partition collector snapshot automatically selects its manifest partition (Source=manifest), contrary to the unconditional fail-closed-unless-environment claim in CHANGELOG, CLI reference and SnapshotCommands help. Document actual precedence or deliberately revise the behavior. Document reuse of the recorded cacheDir plus prior manifest/new destination for resuming implicit-export caches; manifest alone does not reuse the prior cache.
- CreateForTesting remains internal static with a private SDK-taking constructor; public reflection lookup confirms it is absent. Export without cache-dir isolates before lazy cache-store creation; normal one-command scanner flow and cache-local/exported manifest paths are correct. Explicit cache-dir still exports the whole selected cache.
- Reran focused regressions: 136 pass; reran full suite with DOTNET_ROLL_FORWARD=Major: 2330 pass / 9 skip / 0 fail, no compiler/analyzer warnings. Independently ran current live CLI on 1621192: 1,405,433 estimated results refused in 2.16s, exit 2, zero attachment attempts, clean JSON and stderr estimate/remediation. Lambert's full opted-in/default/resume/offline live results remain reported corroboration, not my reruns.
- Appended detailed blockers, verification matrix, documentation handoff and breaking/security/data-loss/features/fixes release-note items to .squad/decisions/inbox/dallas-review-pr156-fixes.md. No implementation/docs/test edits, tag or commit; all review commands completed.

### 2026-10-02 - Release re-review at 083e252

- REJECT for release, documentation only. Larry authorized Lambert's override revision; both implementation blockers are now accepted. Kane is eligible for the remaining Markdown documentation/CHANGELOG corrections; no new implementation repair is requested.
- Reran the original eviction probe at concurrency 1 and 6: 1 MiB cache, 600 KiB required console, 700 KiB optional file. Both are complete/exit 0, optional file is total_size_limit, and exported valid snapshots replay all 614400 required console bytes. Required writes/verification now precede optional downloads, with atomic headroom from occupied artifacts and no resume double-counting.
- Real SDK HTTP-403 wrapper probe now yields classified HlxAcquisitionException/access_denied/list_helix_jobs_by_build. The service returns incomplete timeline fallback with PrimaryAcquisitionError rather than leaking Azure.RequestFailedException. All seven public provider calls use ClassifyAsync, with caller cancellation preserved.
- Focused suite 74 pass; full suite rerun 2351 pass / 9 skip / 0 fail, no compiler/analyzer warnings, with DOTNET_ROLL_FORWARD=Major.
- Remaining CHANGELOG.md:25-30 errors: transient body failures are failed, not recorded_failure; job discovery is now classified despite the entry saying otherwise; standalone snapshot validate does not mutate manifests (collector post-export validation does); the old optional-eviction caveat contradicts the required-first/atomic-budget revision.
- Resume example in docs/cli-reference.md points --manifest at the original exported snapshot's manifest, which is also overwritten as the new output. Use the recorded cache's standalone manifest or a copied standalone file to avoid mutating the prior bundle. Clarify normal-root default applies without export; implicit export creates a new isolated root.
- Appended detailed documentation-only verdict and release-note adjustment: add required-evidence retention/atomic resumed-byte accounting and structured SDK job-discovery fallback to Fixes; other categories remain unchanged. No code, test, release-doc, tag or commit changes; diagnostic processes completed.

### 2026-10-02 - Documentation re-review at 3bc3c5b

- REJECT on one remaining Markdown example path; Kane remains eligible. All four corrected CHANGELOG entries now match 083e252 source, and implementation approval/probe results remain valid.
- docs/cli-reference.md:686,696 omits public/ in the standalone manifest path: command.options.cacheDir is the base /tmp/hlx-collect-cache/<guid>, but GetEffectiveCacheRoot appends public and ResolveManifestPath writes there. Correct comment/--manifest to <guid>/public/hlx-collect-manifest.json, or omit --manifest so the original --cache-dir resolves the existing default automatically. Keep --cache-dir at the base, without public.
- The wrong explicit path supplies no prior resume attempts and creates another manifest, despite cached provider reads potentially masking it. No source/test changes or tests rerun for this documentation-only delta; decision/history appended.

### 2026-10-02 - Final release approval at 9a939a4

- APPROVE HEAD 9a939a456714d4420ab0efb91815542ca53f083b for release; supersedes earlier rejections and leaves no release blockers.
- Verified the corrected example against source: retain the recorded base --cache-dir, omit --manifest, and let ResolveManifestPath use the effective partition directory (public/ in CLI) while exporting to a new destination. Flag/schema descriptions now include the partition subdirectory.
- Confirmed the final breaking/security/data-loss/features/fixes release-note list, with required-evidence retention, atomic optional headroom/resume accounting and classified SDK discovery fallback added. Prior independently rerun probes and 2351-pass/9-skip full suite remain applicable because subsequent commits are documentation-only.
- Decision/history appended; no source, release-doc, test, tag or commit changes and no long-running processes.
