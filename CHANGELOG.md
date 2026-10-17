# Changelog

All notable changes to helix.mcp are documented here. Versions follow [semantic versioning](https://semver.org/).

For releases prior to v0.7.6, see the [GitHub Releases page](https://github.com/lewing/helix.mcp/releases).

---

## [Unreleased]

### Added

- **`azdo_timeline` triage view, selectors, and access-first delivery:** `azdo_timeline` (MCP) and
  `hlx azdo timeline` (CLI) now default to a **triage** projection — at most 10 rows, ancestors
  included, Phase rows omitted, errors/Helix-first ordering, and up to 5 deduplicated issue
  previews (200 Unicode scalars each) per row with log IDs and counts. New selectors `recordId`,
  `parentId`, `type`, `result`, `state`, `name` (glob), and `expand` resolve `filter` to `all`
  automatically once an explicit selector is given with no preset, so an exact lookup is never
  implicitly narrowed to failures. New `projection` values `compact` (identity/count rows) and
  `summary` (aggregate counts only) join the existing `full` (original record detail). Paging adds
  `offset`/`limit`/`viewId`/`next` with stale-`viewId` rejection. Large rows/records/issue windows
  are never refused: they are delivered as a verified evidence file/reference via the shared
  `hlx_read_evidence` reader instead of being dropped or erroring. CLI JSON gains `--delivery`-style
  `--all`/`--output` escape hatches and `--raw-json` for the legacy `{id, records}` shape used by
  offline snapshot replay (works for every projection, not just `--raw-json`).

### BREAKING

- **`azdo_timeline` default output shape changed.** The default MCP response is now a bounded
  triage view (≤10 rows, Phase omitted, issue text shortened to previews) instead of the full,
  unbounded record list. Pass `projection=full` (and page/`delivery=file` as needed) to recover the
  previous full-detail shape.
- **`hlx azdo timeline --json` now emits a `.results[]`-keyed envelope** (`ok`, `results`,
  `returned`, `complete`, `next`, `viewId`, `cache`, ...) instead of the legacy bare `{id, records}`
  object, and **exits 2** when the printed page is incomplete and `--allow-truncated` was not
  passed. Scripts/`jq` pipelines reading `.records[]` must switch to `.results[]` and handle
  `complete`/`truncated`/`next` for partial pages, or pass `--raw-json` to keep the old shape.

- **CLI `--delivery` flag:** `hlx azdo timeline` accepts `--delivery auto|inline|file|chunked`,
  mirroring the MCP `delivery` parameter; `--all`/`--output` remain available as CLI-specific
  escape hatches. `--all`/`delivery="file"` now retrieve the complete selected scope (there is no
  `delivery="all"` value — combine `--all` with `--delivery file`, or just pass `--all`).
  Inline `maxResponseBytes` is clamped to an 8 KiB–16 KiB effective range (default 12288); both
  the clamped `maxResponseBytes` and the caller's original `requestedMaxResponseBytes` are
  reported in the response.
- **`--output` vs. eval snapshots:** writing `--output` into an active read-only eval snapshot
  (`HLX_EVAL_SNAPSHOT`) is refused with a structured error that includes an exact,
  directly-executable recovery command to retry outside the snapshot.
- **Per-request evidence auth:** `hlx_read_evidence` resolves the AzDO auth-context partition for
  each read independently, matching the identity the originating authenticated call used instead
  of relying on scope-sharing.

## [v0.11.0] — 2026-10-02

### **Fixed — cache data loss**

- **SQLite NUL metadata encoding:** On v0.10.3 and earlier, cached values containing NUL characters could be stored as zero bytes by the SQLite metadata path. The main known impact was raw AzDO build logs around 6 KB and larger: they are cached as plain text with a NUL-prefixed `\0raw\n` marker, so live calls usually hid the issue by refetching, but offline/eval snapshots could replay empty logs while still validating. Metadata values containing NULs are now encoded losslessly before storage and decoded on read; empty raw-log rows are treated as corrupt (`cache/invalid_response` in eval/offline mode, refetched in live mode), and `hlx snapshot validate` flags empty/corrupt raw AzDO log metadata rows. Clear affected local caches with `hlx cache clear` and re-collect any snapshots produced by v0.10.3 or earlier. Reported by PureWeen (gist).

### Security

- **Collector export isolation:** `hlx collect azdo-build --export <dest>` without an explicit `--cache-dir` now collects into a fresh, isolated per-run temporary cache directory before export, so the exported snapshot contains only this run's evidence instead of the entire shared cache (other builds, other AzDO auth partitions). The isolated directory is recorded in the manifest at `command.options.cacheDir` and printed to stderr. Supplying an explicit `--cache-dir`, or running standalone `hlx snapshot export`, still collects/exports that whole cache directory as before — pass an isolated `--cache-dir` yourself if you need the same containment in those cases.

### Fixed

- **Bounded all-test collection:** `collect --test-scope all` now checks build-wide result estimates and refuses more than 10,000 results without an explicit sufficient `--max-test-results`. Attachments default to diagnostic outcomes with a 1,000-request build-wide cap; broader attachment coverage requires `--test-attachment-scope all --max-test-attachments <budget>`. Exclusions and limits are recorded in manifests, result/attachment progress and acquisition heartbeats go to stderr, and JSON stdout remains parseable.
- **Paging correctness:** AzDO list fetches now follow continuation tokens across all provider pages, fail closed on repeated continuation tokens/URLs or more than 1000 pages, and CLI JSON list envelopes report `cache.key` as the backing complete-list cache key used for replay (the same value as `cache.completeKey`).
- **Collector robustness:** `hlx collect azdo-build` now uses thread-safe manifest attempt appends, verifies cached evidence before marking resumed entries complete, retries selected Helix file downloads within byte budgets, honors provider `Retry-After` beyond `--retry-max-delay` up to a 1-hour safety ceiling, rejects disabled caching (`HLX_CACHE_MAX_SIZE_MB=0`), redacts credentials/query/fragment data from recorded argv URLs, lets `--test-scope all` also populate failed-result replay keys for default offline callers, and detects snapshot AzDO cache partitions without credentials.
- **Standalone manifest default path:** `hlx collect azdo-build` without `--manifest` now writes `hlx-collect-manifest.json` inside the effective cache directory being populated, rather than the current working directory. Exported snapshots are unaffected: the exported manifest is still `manifest/hlx-collect-manifest.json` inside the destination. Pass `--manifest <path>` to choose an explicit location.
- **Mid-body transport classification:** AzDO body reads and Helix console-log streams that fail partway through (not just at request time) now go through the same acquisition-error classifier as other calls, so transport/timeout failures are retried per policy; if retries are exhausted, the attempt is recorded as `failed` (transient kinds are never negative-cached), instead of surfacing as an unclassified exception.
- **Real Helix SDK failure classification + offline replay:** All seven Helix client operations, including job discovery (`azdo_helix_jobs`/`ListJobsByBuildAsync`), now classify `Azure.RequestFailedException` (as thrown by the real Azure SDK transport, not just a test fake) into the standard acquisition-error kinds. The six cached metadata/stream operations get negative-cache recording and offline snapshot replay; job discovery instead returns its classified error via a timeline fallback (`complete=false` with the structured acquisition error attached) since it has no cache key of its own.
- **Invalid-snapshot completeness:** the collector's post-export validation now sets `complete=false` and exit `1` with `snapshot_validation_failed` recorded in both the standalone and exported manifests when the exported snapshot fails validation (corrupt database, missing artifacts, bad schema), instead of reporting a misleadingly complete collection.
- **Full outcome coverage for `--test-scope all`:** All-outcome collection now requests every AzDO-accepted `TestOutcome` value (`Unspecified,None,Passed,Failed,Inconclusive,Timeout,Aborted,Blocked,NotExecuted,Warning,Error,NotApplicable,Paused,InProgress,NotImpacted`); the previously advertised `NotRunnable` value is rejected by the AzDO test-results API and is no longer requested. Derived `Failed` rows continue to be retained and verified.
- **TTL-independent final verification:** Final cache-evidence verification before manifest/export completion now checks that required rows are present in the SQLite cache regardless of their live-mode TTL, so expired-but-still-present rows are correctly treated as available for export/replay without triggering an unnecessary live refetch.
- **Optional-download budget:** required evidence is now acquired and verified before any optional Helix file download begins, and `--max-total-bytes` for `--download-helix-files` draws from a separate, atomically-accounted budget for this run's own new/resumed optional writes (not double-counting already-cached bytes). This protects required evidence from this collector's own optional downloads; it does not guard against unrelated external writers shrinking the cache concurrently.

### `hlx collect azdo-build` scanner snapshots

- **One-command snapshot collection:** Added `hlx collect azdo-build <build-id-or-url>` to collect deterministic AzDO/Helix evidence into the hlx cache, optionally export a replayable snapshot, and write a versioned manifest at `manifest/hlx-collect-manifest.json` inside exported snapshots.
- **Manifested completeness:** Collector manifests record every fetch attempt, skip, recorded provider failure, retry outcome, policy, auth/replay metadata, summary counts, and exit code so scanners can distinguish complete snapshots from declared gaps and `not_in_snapshot` replay misses.
- **Helix uploaded-file downloads:** `--download-helix-files <glob>` now downloads matching Helix files with streaming `--max-file-bytes` / `--max-total-bytes` caps; over-cap files are deleted, recorded as `size_limit` / `total_size_limit` skips, never cached, and in-cap files replay offline.
- **Credential-free snapshot replay:** Collector manifests record the non-secret AzDO cache partition in `auth.azdo.cachePartition` with replay mode `snapshot_partition`; eval mode reuses that recorded partition automatically without `AZDO_TOKEN` or `az login` (and auto-selects a single discovered partition when no manifest is present). `HLX_EVAL_AZDO_PARTITION=public`/`cache-xxxxxxxx` is only required to resolve a snapshot with multiple AzDO partitions and no recorded manifest selection; see [CLI reference](docs/cli-reference.md#snapshot-commands) for the full precedence.
- **Offline scanner workflow:** Documented the one-command flow: collect in CI with `hlx collect azdo-build <build> --export <snap>` → upload snapshot → investigate anywhere with `HLX_EVAL_SNAPSHOT=<snap> hlx mcp` and no AzDO credentials.

### CLI complete-list paging for AzDO scanners (BREAKING)

- **BREAKING — JSON list shape:** `hlx azdo changes`, `azdo test-runs`, `azdo test-results`, `azdo artifacts`, and `azdo test-attachments` now emit a JSON envelope with `{ ok, results, returned, total, offset, limit, complete, truncated, next, cache, note }` instead of a bare JSON array. Migration: read rows from `.results[]`; the old array length is now `.returned`, and the complete selected-list count is `.total`.
- **BREAKING — truncated pages fail closed:** bounded list output exits `2` when `truncated == true` unless `--allow-truncated` is supplied. Migration: fetch `next`, rerun with `--all`, or add `--allow-truncated` if the caller intentionally accepts a partial page and needs legacy success semantics.
- **New paging flags:** the same AzDO list commands now accept `--all`, `--offset`, `--limit`, and `--allow-truncated`; existing `--top` remains a compatibility alias for `--limit`.
- **Complete collection cache keys:** `--all` populates complete-list cache keys so eval-mode capped MCP/list calls can replay offline from scanner snapshots without changing MCP defaults. `azdo log --full` fetches/cache-populates complete build logs for the same workflow.

### Machine-readable acquisition errors and fail-closed evidence paging (#152)

- **Structured acquisition errors:** CLI JSON hard failures now emit `{ "ok": false, "error": { ... } }` from a central CLI filter, and MCP tool failures return `isError: true` with the same `structuredContent.error` object. The stable `kind` values are `not_found`, `access_denied`, `rate_limited`, `timeout`, `transport_error`, `invalid_response`, and `not_in_snapshot`; providers are `azdo`, `helix`, and `cache`.
- **Snapshot replay of recorded failures:** Schema v2 snapshots record deterministic live acquisition failures (`not_found`, `access_denied`, `invalid_response`) and replay them offline with the original `kind`, `provider`, `operation`, `resource`, and optional `httpStatus`, plus `source: "snapshot"`, `replayed: true`, and `recordedAt`. Transient failures are not recorded, live mode never serves recorded failures, and v1 snapshots remain loadable with a compatibility warning. Keys never collected into a snapshot fail as `kind=not_in_snapshot`, `provider=cache`.
- **Caller-owned retry policy:** Errors include operation/resource context plus optional `httpStatus` and `retryAfterSeconds`; callers decide whether to retry, skip, or fail collection. Genuinely empty successful results remain successes.
- **Security:** URL query strings and fragments are redacted from serialized error `resource` string values to avoid leaking signed URLs or tokens.
- **Helix operation classification:** Helix API calls now classify acquisition errors per operation, including file downloads as `download_helix_file`, so CLI/MCP errors and snapshot replay point to the exact failed fetch.
- **Evidence-plan paging fail-closed:** Any partial Helix-failure page (`helixFailureTotal > helixFailures.length`, including later offset pages) now reports `complete=false`, `truncated=true`, `helix_failures_truncated`, and CLI exit `2`. Human output uses explicit ranges such as `showing 1-1 of 2`; collectors should fetch/merge remaining pages or request a limit at least as large as `helixFailureTotal`.
- **Empty AzDO logs reclassified:** When a direct build-log body is empty, hlx validates the logId against build logs metadata; if logId is missing from both the logs list and timeline record.log.id, the operation fails with kind=not_found. Empty full logs are no longer cached as successes. See azdo_log tool documentation.
- Requested by Vitek Karas; see lewing/helix.mcp#152.

### Helix-aware evidence plan — arcade queue-monitor parsing

- **Evidence plan Helix support:** `azdo_evidence_plan` now parses arcade queue-monitor timeline issues into structured `helixFailures[]` rows (helixJobId, helixJobName, workItem, state, exitCode, sourceFormat, suggestedFetches) instead of reporting the monitor job as a missing artifact. A failed/canceled monitor job with parseable work-item failures is no longer incomplete just because no `Logs_Build_*` artifact exists for the monitor itself.
- **Paging for deterministic collectors:** Added `helixFailureOffset` / `helixFailureLimit` / `helixFailureTotal` / `helixFailuresTruncated` fields and CLI flags to support pagination. Default page size: 200, max: 1000. Use paging when `helixFailuresTruncated` is `true`; after #152, every partial page fails closed with exit `2` and `helix_failures_truncated` until a single response contains all parsed failures.
- **Machine-readable failure semantics:** Added stable `incompleteDetails[].code` values alongside human `incompleteReasons[]`. Codes: `artifact_ambiguous` = multiple artifact candidates for one selected job; `artifact_missing` = no matching artifact candidate; `candidates_truncated` = a job's candidate list exceeded the per-entry bound; `entries_truncated` = selected jobs exceeded the plan entry bound; `helix_failures_truncated` = the current response contains only a partial Helix-failure page; `monitor_unparseable` = a selected monitor-like job had no parseable Helix work-item failures; `monitor_unresolved_job_id` = failure-shaped monitor entries lacked a recoverable Helix job ID.
- **Deterministic drilldown:** Each `helixFailures[]` row includes `suggestedFetches[]` with tool names, Helix IDs, and work-item selectors. Scripts can read the plan, decide completeness, and use emitted `helix_work_item`, `helix_logs`, and `helix_files` fetch intents for deep investigation.
- **Backward compatible:** The artifact plan (`entries[]`) remains unchanged for non-monitor jobs. Monitor jobs without parseable failures remain selectable and report incompleteness with explicit codes. Empty `helixFailures[]` with failures unparseable is never reported as "no failures."

## [v0.10.3] — 2026-09-30

### Hidden AzDO test failures made visible (#150)

- **Real failed-test counts:** `azdo_test_runs` now derives failed-test totals from `unanalyzedTests` and related Azure DevOps counters (including incomplete and not-applicable results) instead of reporting zero failures.
- **Explicit auth and deletion errors:** Authentication redirects, HTTP 203 responses, and HTML sign-in pages now raise actionable auth errors instead of returning misleading empty results. A 404 while reading test-run results now reports that the run may have been deleted.
- **Correct large-result paging:** Test results and runs now continue paging past Azure DevOps' 10,000-item cap.
- **Stale cache avoidance:** Empty test-result lists are no longer cached, and AzDO test cache keys are versioned so older misleading entries are not served after upgrade.
- **No auto-follow redirects:** The MCP AzDO HTTP client no longer auto-follows redirects, preserving auth-error detection.
- **Windows cache sharing retries:** Artifact cache reads and writes retry transient Windows sharing violations to prevent spurious CI failures.

### Build Analysis evidence guidance clarified (#145)

- Build-analysis evidence and live monitor guidance now better describe available timeline, log, and Helix signals.

### Infrastructure

- **CoreCLR merged-runner CI guidance (#150):** CI guide notes that merged CoreCLR runners can exit 100 even when tests fail, helping investigators distinguish runner behavior from infrastructure errors.

### Dependencies

- **GitHub Actions updates** (#143, #146, #147, #148) — Updated `zizmor-action` to 0.6.4, `docker/build-push-action` to 7.4.0, `docker/setup-buildx-action` to 4.4.1, and `docker/setup-qemu-action` to 4.4.0.

## [v0.10.2] — 2026-09-11

### SQLite cache store isolation and snapshot export concurrency (#130)

- **Cache store disposal:** Disposing a SQLite cache store no longer clears unrelated connection-string pools. Scoped pool clearing now affects only the disposing store's pool group, preventing interference with independent cache roots and auth/eval store instances.
- **Snapshot export:** Snapshot exporter now opens live artifact source files with `FileShare.Read | FileShare.Delete` semantics, permitting concurrent eviction and deletion while the exporter continues reading.
- **Cache artifact replacement:** `SqliteCacheStore.SetArtifactAsync` now uses atomic file replacement and surfaces operation failures instead of reporting success when replacement fails. Replacement succeeds while readers holding delete-sharing handles keep reading old bytes, then swaps visibility atomically.

### Startup cache maintenance — tracked and deterministic lifecycle

Cache store now tracks its startup maintenance task and cancels/joins it on disposal. The startup pass captures its time-of-open and evicts only entries already stale at that moment, eliminating a race where entries written after the store opened could be incorrectly deleted (#129).

### Snapshot export and validation for offline replay mode

New `hlx snapshot` commands enable offline evaluation and reproducible test scenarios:

- **`hlx snapshot export <destination>`** — Export the live cache to a portable snapshot directory. Preserves all cache keys and artifact files. Prints auth-scoped replay limitations and usage instructions with `HLX_EVAL_SNAPSHOT`.
- **`hlx snapshot validate <snapshotPath>`** — Validate a snapshot for use with `HLX_EVAL_SNAPSHOT`. Checks SQLite integrity, schema version, sidecar absence (single-link requirement), and artifact references. Returns exit code 0 (valid) or 1 (invalid) with detailed error/warning diagnostics.

**Replay mode:** Set `HLX_EVAL_SNAPSHOT=/path/to/snapshot` to run `hlx` against exported cache data instead of live APIs. Useful for:
- Offline investigation (no network required)
- Reproducible analysis (same snapshot, same results)
- Test automation (deterministic cache for subprocess testing)

**Auth-scoped replay semantics:** Environment-keyed entries (via `AZDO_TOKEN`) are reproducible with the identical token and `AZDO_TOKEN_TYPE` classification. Anonymous/public entries always replay. AzureCliCredential/az CLI-derived partitions are not reproducible in eval mode; use `AZDO_TOKEN` to export if CLI auth was used.

**Snapshot layout:** Single-directory structure with `cache.db` (SQLite, exactly one hard link) and `artifacts/` (one link per file). Sidecars (`-wal`, `-shm`, `-journal`) must not be present.

### `azdo_helix_jobs` — primary binding filters

- The primary Helix strategy now binds parsed AzDO queue-monitor failure evidence to matching Helix jobs and applies all documented filters without per-job requests. Failure counts reflect attached evidence, unknown monitor IDs are disclosed but do not create synthetic rows, and unavailable timelines make failure filters explicitly inconclusive while preserving state-based filtering.

## [v0.10.1] — 2026-09-09

### `azdo_helix_jobs` — active queue-monitor evidence and GitHub source fallback

- Active queue-monitor tasks expose errors and warnings through build-level `timelineIssues` before the monitor finishes, while job metadata retains queue, state, work-item count, retry lineage, task error/warning counts, and bounded messages (#132).
- Fixed source-based Helix job discovery for GitHub-backed Azure DevOps builds whose repository name is blank. Source computation now falls back to the GitHub repository ID (for example, `dotnet/runtime`) without treating opaque non-GitHub repository IDs as source names (#135).

## [v0.10.0] — 2026-09-08

### `azdo_helix_jobs` — queue-monitor-compatible job discovery

`azdo_helix_jobs` now preserves Helix job summaries instead of reducing them to GUIDs. Its primary strategy queries Helix by the build's computed source and filters `Job.ListAsync(source)` results by the `BuildId` property; the existing AzDO timeline task-name scan remains the fallback.

The four existing job fields remain required. Additive optional fields expose `state`, `queueId`, `workItemCount`, `superseded`, `taskErrorCount`, `taskWarningCount`, and issue `messages` for fallback rows that have issues but no parseable Helix job ID. Messages preserve timeline order, are limited to 20 entries of 500 characters, and add an omitted-count marker when needed. Result-level `source` and `strategy` (`helix` or `timeline`) identify the lookup and interpretation. On the primary `helix` path, `result` reports only `completed` or `running`, and the independent `state` field reports lifecycle state.

Queue-monitor warning and aggregate-tree messages now yield job GUIDs and failed work-item names, preferring the GUID embedded in the job display name and using its console URL only as fallback. Issue-bearing timeline tasks without a GUID are retained rather than silently dropped. Retry predecessors are marked `superseded` within the returned set; counts are not filtered by this annotation.

Primary Helix summaries do not contain pass/fail outcomes. Consequently, primary responses set `FailedHelixJobs` to 0 and set the new `outcomeUnknownHelixJobs` count to `TotalHelixJobs`; a non-`all` filter is documented but not applied. Determining individual outcomes still requires an explicit later `helix_status` call.

A successful primary lookup is enriched by one AzDO timeline request while retaining `strategy: "helix"`. Its build-level `timelineIssues` includes every issue-bearing `Task`, without task-name or topology filtering. Each entry identifies the record, task, parent job, state, result, error and warning counts, and bounded messages. Omitted `timelineIssues` plus a `note` means timeline evidence was unavailable; `timelineIssues: []` means the timeline was fetched and contained no issues. This exposes errors on active tasks whose state is running and whose result is still unknown.

The successful primary path therefore makes three upstream requests—build, Helix job list, and timeline—not zero additional calls. It makes no per-job detail requests. Existing timeline `issues`/`running` filters and ranked log search remain available for deeper investigation before the build completes or the leg turns red.

The dotnet/sdk task name `🟣 Run TestBuild Tests` still limits only timeline fallback discovery because it lacks `helix`; it does not disable the primary Helix-side lookup. `[HelixJob:GUID]` tokens in test-run names remain a secondary workaround when the primary lookup is unavailable or empty and fallback misses.

### `azdo_evidence_plan` — Failed job → evidence artifact planner (MCP + CLI)

New read-only tool for planning which artifacts correspond to failed or canceled jobs in an AzDO build. Maps jobs to evidence via two strategies:

- **Primary:** GUID join (`artifact.source == job.id`) — 100% resolution on real builds, handles retried attempts correctly by construction.
- **Fallback:** Normalized-exact name matching (dotnet/runtime PR #132609 parity) — for unmapped jobs when GUID join leaves gaps.

Matching strategy is configurable via `--match` parameter: `auto` (default, recommended), `source-id`, `normalized-exact`, or `exact`. The `exact` strategy uses ordinal-ignore-case equality after prefix stripping, with no normalization.

**CLI:** `hlx azdo evidence plan <buildId> [--job-results RESULTS] [--artifact-pattern PAT] [--artifact-job-prefix PREFIX] [--keep-attempt-prefix] [--match MODE] [--json]`. `AttemptN_` is stripped and recorded by default; pass the bare `--keep-attempt-prefix` flag to retain it. The former `--strip-attempt-prefix` spelling is no longer recognized, but it only restated the default: remove it from scripts rather than replacing it with the opposite-meaning keep flag.

**MCP:** `azdo_evidence_plan(buildIdOrUrl, jobResults?, artifactPattern?, artifactJobPrefix?, stripAttemptPrefix?, match?, ...)` → structured plan with status per job (`mapped`, `ambiguous`, `missing`), ranked candidates, and completeness signal. MCP retains the positive `stripAttemptPrefix` parameter (default `true`); CLI `--keep-attempt-prefix` is its inverse.

**Exit codes:** `0` complete, `2` incomplete-but-useful (plan still output), `1` error.

**Key properties:**
- Never silently chooses ambiguous candidates — the full candidate count is reported, retained candidates are ranked, and the entry has `status: "ambiguous"`.
- Preserves attempt numbers for deterministic ranking (real builds retry with `Attempt1`, `Attempt2`, etc.).
- Read-only planning boundary: no download, extract, or write operations; analysis remains in binlog-mcp.
- Completeness contract: `complete` signals whether all jobs are unambiguously mapped and no output was truncated. `incompleteReasons[]` explains gaps.
- Warning contract: `warnings[]`, `warningTotal`, and `warningsTruncated` are always present. `warnings` contains the first 10 original diagnostics in deterministic order (never a synthetic truncation member); `warningTotal` reports the pre-cap count and `warningsTruncated` reports whether any were omitted.
- Structured output: job records (including timeline order and attempt when present), candidate artifacts (name, id, size, download URL, type, source GUID, attempt), and build provenance (PR metadata if applicable).
- Partial-response bounds: 200 entries, 10 candidates per entry. Every entry reports `candidateTotal` and `candidatesTruncated`; candidate overflow also supplies `candidateNote`. Entry or candidate overflow sets plan-level `complete: false` and `truncated: true`; `totalEntries` reports the selected-job total and `note` summarizes the truncation.

**Why `auto` is default:** Testing on real failed builds shows normalized-name matching (PR #132609) has a 12.7% miss rate because AzDO job display names include a matrix-leg `crossaot` suffix that artifact names omit, and 100% ambiguity on retried attempts. `auto` uses the source-GUID join first and normalized-name fallback only for unmapped jobs; `source-id` is the source-ID-only mode.

### Fixed: `StringHelpers.MatchesPattern` — trailing-`*` prefix globs now work

`MatchesPattern("Logs_Build_Attempt1_x", "Logs_Build_*")` now returns `true`. Previously, trailing-`*` globs (prefix patterns) matched nothing because they were treated as literal substrings.

This fixes `hlx azdo artifacts --pattern 'Logs_Build_*'` and `azdo_artifacts(pattern: 'Logs_Build_*')`, which are now usable for evidence planning. The fix is additive and ReDoS-free (O(n) scan with early exit). Suffix globs (`*.binlog`) and bare-substring matching remain unchanged.

---

## [v0.9.1] — 2026-07-28

### helix_find_files — optional `workItem` parameter for faster, scoped searches (#117)

`helix_find_files` now accepts an optional `workItem` parameter, making it compatible with all other Helix job-inspection tools and eliminating the hard parameter-rejection error callers hit when passing `workItem` to this tool.

When `workItem` is supplied, the search is scoped to that single work item (equivalent to calling `helix_files` on that item and filtering by pattern), avoiding costly scans of up to 50 work items.

This fixes a common LLM error: calling models that passed `workItem` to `helix_find_files` encountered a strict parameter-rejection error because it was the only work-item-scoped tool in its family missing that parameter. Now all work-item-scoped Helix tools accept `workItem`; `helix_status` and `helix_batch_status` intentionally remain job-scoped and do not expose `workItem`.

Additional fixes in PR #117:
- Missing work items now report a specific "work item not found" error instead of "Job not found".
- Fixed a whitespace-predicate mismatch (`IsNullOrEmpty` vs `IsNullOrWhiteSpace`) that caused `workItem: " "` to silently scan the whole job while the tool reported only one item scanned.
- Added a reflected schema guard asserting every `jobId`-taking tool also takes an optional `string? workItem` (with explicit exceptions for `helix_status` and `helix_batch_status`).

### Squad governance (#117)

Scribe agents can now commit agent-extracted skills directly, removing a manual step from the skill-extraction workflow.

---

## [v0.9.0] — 2026-07-14

### Containerized MCP server (#77)

New `Dockerfile` publishes a multi-platform stdio MCP image (`linux/amd64`, `linux/arm64`) to `ghcr.io/lewing/helix.mcp` on release tags.

### Canonical Helix-side job enumeration for AzDO builds (#92, #96)

`azdo_helix_jobs` now resolves job IDs via canonical Helix metadata (`Job.ListAsync`); AzDO timeline task-name parsing is retained as fallback when Helix returns no results.

### Arcade alignment — JobDetails fields and test-file extensions (#93, #95)

`JobDetails` field surface aligned with arcade canonical definitions; expanded test-result file extension recognition.

### Work item `ExitCode` and `ConsoleOutputUri` (#91, #94)

`WorkItemSummary` now surfaces `ExitCode` and `ConsoleOutputUri` following the `Microsoft.DotNet.Helix.Client 11.0.0-beta.26325.102` bump.

### HTTP 204 No Content handling (#105, #106)

AzDO GET helpers now treat 204 No Content as an empty result instead of throwing; consistent with 200 + empty-body responses.

### Dependency updates

- `Microsoft.DotNet.Helix.Client` → 11.0.0-beta.26325.102 (#91, #94)
- `actions/checkout` (#103), `actions/setup-dotnet` (#107), `zizmorcore/zizmor-action` 0.5.6 → 0.5.7 (#104)
- Docker CI actions: `docker/build-push-action` → 7.3.0 (#108), `docker/metadata-action` → 6.2.0 (#109), `docker/setup-buildx-action` → 4.2.0 (#110), `docker/setup-qemu-action` → 4.2.0 (#111), `docker/login-action` → 4.4.0 (#112)

---

## [v0.8.0] — 2026-06-24

### Strict parameter rejection with "Did you mean?" hints (#81, PRs #83/#84/#87)

MCP tools now reject unknown or mistyped parameter names immediately, with a structured error message that includes:
- A **"Did you mean: X?"** suggestion when the unknown name is close to a known parameter (Levenshtein distance ≤ 6)
- The **full list of allowed parameter names** so callers can self-correct without consulting docs

Previously, unknown params were silently dropped (the SDK discarded them before invoking the tool). This change turns silent data-loss failures into immediate, actionable errors.

Example response when an LLM passes a hallucinated param name:
```
Unknown parameter 'minFinishTime' for tool 'azdo_builds'.
Did you mean: minTime?
Allowed parameters: org, project, top, branch, prNumber, definitionId, status, minTime, maxTime, queryOrder
```

### AzDO parameter plumbing — `minTime`/`maxTime`, `outcomes`, `top` (#78)

Three parameters that were accepted by MCP tools but silently not forwarded to the REST API are now plumbed end-to-end:

| Tool | Parameter | Was | Now |
|------|-----------|-----|-----|
| `azdo_builds` | `minTime`, `maxTime`, `queryOrder` | accepted, dropped | forwarded to AzDO REST API |
| `azdo_test_results` | `outcomes` | hardcoded to `Failed` | forwarded; configurable (default still `Failed`) |
| `azdo_test_attachments` | `top` | accepted, dropped | forwarded to AzDO REST API |

All defaults preserve prior behavior — no breaking changes for existing callers.

`minTime`/`maxTime` filter the time field selected by `queryOrder`. For example, `queryOrder=finishTimeDescending` means `minTime`/`maxTime` filter by finish time. Default `queryOrder` is `queueTimeDescending`.

### Alias support (#75)

Tools that accept a build identifier resolve common parameter name aliases automatically before strict validation runs:

| Alias | Canonical | Tools affected |
|-------|-----------|----------------|
| `buildId` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `build_id` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `buildUrl` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `result` | `resultFilter` | `azdo_search_timeline` |

### AzDO filter normalization (#82, PR #85)

Internal refactor — centralized AzDO filter normalization (trim, case-fold, default-collapse). User-visible side effects:

- `queryOrder` values are now sent lowercase in REST URLs (e.g. `finishtimedescending` instead of `finishTimeDescending`). AzDO treats this as case-insensitive; behavior is unchanged.
- Cache key format changed. One-time invalidation on deploy; self-heals within the normal TTL (≤ 30s for in-progress builds, ≤ 4h for completed builds).

### Dependency updates

- `ModelContextProtocol` 1.3.0 → 1.4.0
- `SQLitePCLRaw` pinned to 3.x for [CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)

---

## [v0.7.6] — 2026-05-29

- **User-Agent identifier** (PR #73, @akoeplinger): All outbound HTTP traffic from hlx now carries `User-Agent: helix.mcp/{version}` and a custom `X-Helix-Mcp-Tool: helix.mcp` header on AzDO and Helix clients, enabling arcade-services to distinguish hlx traffic from other callers.
- **Work item status bucketing fix** (PR #71, backport of #70): `GetWorkItemDetailAsync` now applies `IsCompleted` bucketing correctly — in-progress and waiting work items are no longer miscounted as failed in detailed work item queries.
