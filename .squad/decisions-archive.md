# Archived Decisions

> Decisions archived from decisions.md on 2026-10-17. See decisions.md for current decisions.

---

# Dallas gate review — commit 28beceb

Date: 2026-10-02T12:04:09-05:00
Reviewer: Dallas
Commit: 28beceb "Make azdo evidence plan Helix-aware"
Verdict: REJECT

I reject this gate on one scriptability bug in the Helix failure paging contract. Parser behavior, monitor warning extraction, tree/unresolved fail-closed handling, `azdo_helix_jobs` compatibility, MCP description scope, and the focused test surface otherwise look aligned with the design. Because Ripley authored the artifact and is locked out of revising it, Lambert or another non-Ripley agent should make the revision.

## Required fixes

1. `src/HelixTool.Core/AzDO/AzdoService.cs:1428-1455` — `helixFailuresTruncated` is computed as `offset + shown < total`, so a non-zero final page can return `complete=true`, `truncated=false`, no `helix_failures_truncated` detail, and exit 0 even though the output contains only one page of a larger `helixFailures[]` set. Live evidence: build 1620983 with `--helix-failure-offset 1 --helix-failure-limit 1 --json` returned `complete=true`, `helixFailuresTruncated=false`, `helixFailureTotal=2`, `helixFailures.Count=1`, exit 0. The design says `helixFailuresTruncated` is true when `helixFailureTotal > helixFailures.Count`, and `Complete` means no evidence truncation for selected failures. Expected behavior: any partial page must fail closed with `helixFailuresTruncated=true`, `truncated=true`, `complete=false`, `incompleteDetails[].code == "helix_failures_truncated"`, and CLI exit 2 unless the page contains the entire parsed failure set. Update the reason/warning/note wording so offset pages do not falsely say "showing first N" when they are showing a later page.

2. `src/HelixTool.Tests/AzDO/AzdoEvidenceHelixPlanTests.cs:261-284` — the paging test asserts the unsafe behavior (`second.HelixFailuresTruncated == false`, `second.Complete == true`) for a second page that returns 1 of 3 total rows. Replace this with fail-closed assertions matching the fix above, and add/keep a positive case where `limit >= total` is the only paged response that can be complete.

## Optional nits

- None blocking beyond the paging contract. The live sample 1621036 correctly stayed incomplete for a monitor job whose timeline contained only `Failed work item information:` plus generic bash failure and no recoverable work-item rows.

## Validation reviewed

- Clean worktree at 28beceb: focused suite passed, 190 tests.
- Live CLI checks at 28beceb:
  - 1621192 and 1621133 parsed monitor warnings and exited 0.
  - Additional recent failed runtime PR builds 1621136, 1621037, 1621013, 1620983, 1621034, 1620976, 1620986 parsed successfully.
  - 1621036 failed closed with `artifact_missing` + `monitor_unparseable`, as expected for unresolved timeline evidence.
# Dallas gate review — commit 5f11d95

Date: 2026-10-02T12:40:00-05:00
Reviewer: Dallas
Commit: 5f11d95 "Add machine-readable acquisition errors (#152); fail closed on paged evidence plans"

## (a) Evidence-plan paging revision — APPROVE

Lambert's revision satisfies both required fixes from the 28beceb rejection. `AzdoService` now treats any partial `helixFailures[]` page as incomplete by comparing `helixFailureTotal > helixFailures.Count`, sets `helixFailuresTruncated`, adds `incompleteDetails[].code == "helix_failures_truncated"`, and drives CLI exit 2. The wording now reports the displayed range (`showing 3-3 of 3`) instead of falsely saying "first N". The regression test now asserts the second page is incomplete and keeps the positive complete case when `limit >= total`.

Live re-check: `azdo evidence plan 1620983 --helix-failure-offset 1 --helix-failure-limit 1 --json` exited 2 and returned one `helixFailures[]` row with incomplete/truncated state.

## (b) #152 acquisition-error implementation — REJECT

The implementation correctly covers the core model, AzDO 401/403/404/429/timeouts/invalid JSON classification, MCP `isError + structuredContent.error`, CLI JSON error envelopes, cache/offline miss surfacing, and failure non-caching for thrown acquisition errors. It still misses the central live AzDO log ambiguity behind #152: a nonexistent log ID can return HTTP 200 with an empty body and is still exposed as successful `""`.

Live evidence:

- `azdo log 1621466 999999 --json` exited 0 and printed `""`.
- `GET https://dev.azure.com/dnceng-public/public/_apis/build/builds/1621466/logs` returned IDs `[1,2,3]`; `999999` was absent.
- `GET https://dev.azure.com/dnceng-public/public/_apis/build/builds/1621466/logs/999999` returned HTTP 200, `content-length: 0`, `text/plain`.

Required fixes:

1. `src/HelixTool.Core/AzDO/AzdoService.cs:196-211` — `GetBuildLogAsync` already fetches the build logs list for default/tail calls, observes the requested `logId` is absent, then falls through to the direct log endpoint and returns an empty string. Expected: only treat an empty HTTP-200 log body as success after validating that the `logId` exists in successful provider metadata. If the empty body's `logId` is absent from both `GetBuildLogsListAsync` and timeline `record.log.id` references, throw `HlxAcquisitionException` with `kind=not_found`, `provider=azdo`, `operation=get_build_log`, and resource `{org, project, buildId, logId}`; do not return `""`.

2. `src/HelixTool.Core/AzDO/CachingAzdoApiClient.cs:241-251` — full-log fetches cache `""` before any service-level metadata validation can prove the log exists, so the success-shaped missing log can persist. Expected: do not cache empty full-log bodies unless/until metadata validation proves the log ID exists; the simplest acceptable P0 behavior is to never cache zero-length AzDO log bodies. Non-empty logs and range responses keep current caching behavior.

3. `src/HelixTool.Tests/AzDO/AzdoAcquisitionErrorTests.cs:15-21`, `src/HelixTool.Tests/AzDO/AzdoCliAcquisitionErrorTests.cs:11-41`, and `src/HelixTool.Tests/AcquisitionErrorMcpTests.cs:88-110` — tests currently prove raw empty HTTP 200 and fake empty MCP content stay successful, but they do not cover live-shaped "empty direct log + absent metadata". Expected Lambert coverage:
   - service-level: direct log returns `""`, logs list excludes `logId`, timeline excludes `record.log.id` ⇒ `not_found` acquisition error with `logId`;
   - service-level: direct log returns `""`, logs list contains `logId` ⇒ successful `""`;
   - service-level: direct log returns `""`, logs list excludes `logId`, timeline contains `record.log.id` ⇒ successful `""` for in-progress/zero-byte referenced logs;
   - cache-level: empty full-log result is not stored as a successful cached log;
   - CLI JSON and MCP tool calls for the absent-metadata case return exit 1 / `isError=true` with the structured `not_found` envelope.

## (c) Decision: AzDO HTTP-200 empty log validation

Decision: validate `logId` metadata only on empty direct log bodies. Do not preflight every log request and do not leave the behavior documented as-is. This preserves the fast path and compatibility for normal/non-empty logs, avoids extra calls for ordinary reads, and fixes the exact missing-vs-empty ambiguity on the only path where HTTP status is insufficient.

Implementation spec for Ripley:

1. Keep `AzdoApiClient.GetBuildLogAsync` as the raw provider call: HTTP 200 with zero bytes remains a raw successful `""`; HTTP 404/401/403/429/etc. remains classified there.
2. In `AzdoService.GetBuildLogAsync`, after a full direct log fetch returns `""`, validate the requested `logId`:
   - reuse the logs list already fetched for `tailLines > 0`, or fetch `GetBuildLogsListAsync` if it was not fetched;
   - if any `AzdoBuildLogEntry.Id == logId`, return `""`;
   - otherwise fetch `GetTimelineAsync` and scan all timeline records for `record.Log.Id == logId`; if found, return `""`;
   - otherwise throw `not_found` as described above. The message should explicitly say the log endpoint returned an empty body but the ID was absent from build log metadata/timeline references, so callers can understand why HTTP 200 became not_found.
3. Only run this validation for empty direct bodies. Non-empty direct bodies return success even if metadata is stale or absent.
4. Do not cache zero-length full-log bodies unless this validation has proven the log ID exists; P0 may simply skip caching all zero-length full logs. Never cache thrown acquisition errors.
5. Leave successful post-acquisition no-match behavior unchanged: successful list `value: []`, no search matches, and no uploaded files remain successful empty results.

Test expectations for Lambert are the five bullets in required fix 3, plus a recorded/offline/eval-mode assertion that the structured shape is stable (`kind`, `provider`, `operation`, `resource.logId`) and does not depend on exception message text.

## (d) Re-review — commit 2794a94 — APPROVE

Lambert's revision satisfies the prior rejection and decision (c). `AzdoService.GetBuildLogAsync` now validates only empty full direct-log bodies: a logs-list hit returns `""`, a timeline `record.log.id` hit returns `""`, and absence from both throws structured `not_found` with `operation=get_build_log` and `resource.logId`. Metadata acquisition failures remain classified and propagate instead of being converted to the absent-log fallback. Non-empty logs keep the previous fast path with no metadata preflight, and `CachingAzdoApiClient` no longer stores zero-length full logs as successful cached content.

Test coverage now includes service absent/logs-list/timeline-positive cases, cache non-persistence for empty full logs, CLI JSON exit-1 envelopes, MCP `isError + structuredContent`, and positive empty-log success. Sanity check of the broader #152 surface found no PR-blocking issue. Validation: targeted #152 tests passed (98/98), full `HelixTool.Tests` passed (2095 passed, 9 skipped), and live `DOTNET_ROLL_FORWARD=Major dotnet run -- azdo log 1621466 999999 --json` now exits 1 with `kind=not_found`, `provider=azdo`, `operation=get_build_log`, `resource.logId=999999`.
---
date: 2026-10-02
author: Ash
status: proposed
---

# Bundle is snapshot

Larry's directive reframes the scanner "bundle" as the existing offline snapshot/cache model, not a separate artifact format. A deterministic scanner should populate the `hlx` cache, export/validate a snapshot, and let the agent run the same MCP/CLI tools with `HLX_EVAL_SNAPSHOT` set.

The branch now supports this framing better than the earlier analysis assumed: Helix-aware evidence plans provide `helixFailures[]`, `suggestedFetches[]`, stable incomplete codes, and fail-closed paging; #152 provides structured acquisition errors across CLI JSON, MCP structured content, and eval-mode cache misses (`provider=cache`, `kind=not_found`).

Remaining product gap: first-class snapshot population. We still need `hlx collect` / `hlx snapshot populate` plus a manifest that records every intended fetch, cache key/resource, success/skip/failure, acquisition error, byte/hash provenance, paging completeness, and upload/replay metadata. MCP tools should stay capped for agents; the collector should perform uncapped/paged acquisition before export.
### 2026-10-02T12:30:44-05:00: User directive
**By:** Larry Ewing (via Copilot)
**What:** Treat the scanner "deterministic bundle" as an offline cache/snapshot, not a separate artifact format. The evidence flow already works this way: collection populates an offline cache, and the agent then uses the same hlx tools it would use live, served from that snapshot. Bundle/collect work (`hlx collect`, manifests) should build on the existing snapshot/eval-mode/offline cache rather than inventing a parallel bundle format.
**Why:** User framing in response to Vitek Karas — "local cache" and "bundle" are the same concept; the agent interaction stays identical between live and offline.

---

date: 2026-10-02
author: Ash
status: proposed
topic: Scanner scenarios gap analysis for hlx
---

# Proposal: make hlx scanner-safe without sacrificing agent defaults

Vitek's scanner rewrite scenario is valid and broader than issue #152's concrete `azdo_log` example. The current surface is agent-friendly but not deterministic-collector-friendly: several AzDO 404/204/empty/malformed paths become `null` or `[]`, `azdo_log` MCP can return an empty string for a missing log, `azdo_helix_jobs` can hide primary Helix lookup failure behind timeline fallback, and most CLI commands lack stable JSON errors or exit-code semantics. Evidence plan is the strong counterexample: bounded output, completeness fields, and exit 2 for incomplete plans.

Recommendation:

1. Prioritize #152 as a shared Core acquisition outcome model with machine-readable kinds: `not_found`, `access_denied`, `rate_limited`, `timeout`, `transport_error`, `invalid_response`.
2. Keep MCP caps and failure-first defaults for agent context, but add CLI `--all`/paging and JSON completeness fields for scanner scripts.
3. Add a first-class `hlx collect` only after the error model exists. It should write a deterministic bundle directory plus manifest, driven initially by `azdo_evidence_plan`, and record acquisition facts rather than embedding retry/skip policy.
4. Coordinate with Dallas's separate Helix-aware `azdo_evidence_plan` design so queue-monitor failed work items become evidence inputs and the monitor job itself is not treated as the missing artifact.

Full analysis artifact: `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/scanner-scenarios-gap-analysis.md`.

---

---
date: 2026-10-02T11:29:27-05:00
author: Dallas
status: proposed
topic: Acquisition error contract for lewing/helix.mcp#152
---

# Decision: classify provider acquisition failures before scanner work

## Decision

Implement #152 as a shared Core acquisition error contract first. Add `HlxAcquisitionException` carrying an `AcquisitionError` record with stable `kind`, `provider`, `operation`, `resource`, `httpStatus`, `retryAfterSeconds`, and human `message`. Stable kind wire strings are:

- `not_found`
- `access_denied`
- `rate_limited`
- `timeout`
- `transport_error`
- `invalid_response`

Classification belongs in `AzdoApiClient` and Helix client/service wrappers, not in MCP or CLI text handling. HTTP 404/204/empty/malformed provider payloads must stop becoming unqualified `null`, `[]`, notes, or `string.Empty`. Successful empty provider payloads remain success only when the provider actually returned a valid success shape: HTTP-200 empty text log, JSON list wrapper with `value: []`, successful file list with zero files, or filtering/search yielding no matches after acquisition succeeded.

## MCP contract

The repo uses ModelContextProtocol 2.2.0. `CallToolResult` supports `IsError` and `StructuredContent`, but `McpException` is text-only. Therefore, add a `CallToolFilter` that catches `HlxAcquisitionException` and returns:

- `isError: true`
- text content containing the human message
- `structuredContent: { "error": { ...AcquisitionError... } }`

Keep existing `McpException` behavior for validation, binding, and non-acquisition domain errors. Update `McpExceptionHandler` to rethrow `HlxAcquisitionException` unchanged so the filter can see it.

## CLI contract

Keep evidence plan's exit-code convention:

- `0`: complete success
- `1`: hard validation/acquisition error
- `2`: output produced but incomplete/truncated/skipped by policy

Do not allocate one process exit code per error kind. Under `--json`, emit a stable JSON error envelope and let scripts inspect `error.kind`, `httpStatus`, and `retryAfterSeconds`. Preserve `azdo evidence plan` exit 2 for `complete=false`; use exit 1 only for hard acquisition failure.

## P0 implementation scope

Land in the first PR:

1. Core error model and classifiers.
2. AzDO classification in `GetBuildLogAsync`, generic object/list helpers, auth/unexpected HTTP handling, retry-after parsing, malformed/empty JSON handling.
3. Helix classification for HTTP/SDK/timeouts in service wrappers, including `ListJobsByBuildAsync`.
4. MCP acquisition error filter with structured content.
5. CLI JSON error envelope for touched commands, especially `azdo log`, `azdo timeline`, and hard errors from `azdo evidence plan`.
6. Recorded/offline tests using fake handlers and asserting structured fields, not exact wording.

P0 behavior changes to call out: `azdo_log` 404 becomes an MCP error/nonzero CLI instead of empty success; missing timeline stops looking like zero records; `azdo_helix_jobs` should preserve fallback but report primary/timeline acquisition errors and incompleteness when evidence is inconclusive.

## Cache rule

Never cache acquisition failures as successful `null`, `[]`, or `""`. Valid empty successes may still be cached. Do not add negative-result caching in P0. Offline/eval cache misses should be structured `provider=cache` acquisition errors, and corrupt cached JSON should be `invalid_response`.

## Phasing recommendation

Approve #152 as an error-contract PR, not a scanner omnibus. For pagination, choose Ash's option A first: CLI `--all`/paging and completeness metadata while keeping MCP caps agent-safe. Defer MCP cursors unless real transcripts prove agents need them. Approve `hlx collect` as the long-term bundle writer, but only after acquisition errors and CLI pagination exist; otherwise the manifest will encode ambiguous failure states.

Full design artifact: `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/acquisition-errors-design.md`.

---

---
date: 2026-10-02T11:29:27-05:00
author: Dallas
status: for-review
topic: Helix-aware evidence plan (#152 coordination)
---

# Decision: Helix-aware evidence plan

Implement `azdo_evidence_plan` Helix monitor awareness as a timeline-only extension. Parsed arcade monitor failures become top-level `helixFailures[]` rows carrying `monitorJobId`, `monitorTaskId`, `helixJobId`, `helixJobName`, `leg`, `queue`, `workItem`, `state`, `exitCode`, `details`, and `sourceFormat`. Keep `entries[]` artifact-only for backward compatibility.

## Completeness

A failed/canceled monitor job with parsed, untruncated Helix failures is represented and must not produce a missing-artifact entry or CLI exit 2. A monitor-like job with no parseable failures, unresolved failure-shaped rows, or truncated `helixFailures[]` remains incomplete with explicit reasons.

Deterministic collector update from Larry/Vitek: add stable `incompleteDetails[].code` alongside human `incompleteReasons`, add `suggestedFetches[]` to each Helix failure so scripts can drive exact `helixJobId` + `workItem` collection, and expose `helixFailureOffset`/`helixFailureLimit`/`helixFailureTotal`/`helixFailuresTruncated` so caps are fail-closed. Empty `helixFailures[]` with monitor parse failures is incomplete, never "no failures."

## Architecture

Extract the existing monitor parser from `AzdoService` into a pure `AzdoMonitorFailureParser`; keep `AzdoEvidenceMatcher` pure by adding a selected-job overload. `GetEvidencePlanAsync` still performs exactly three cached AzDO GETs and makes no Helix API calls. MCP/CLI copy should route Helix drilldown to `azdo_helix_jobs` and `helix_*` tools rather than adding a new tool.

Handoff artifact: `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/evidence-plan-helix-design.md`.

---

---
date: 2026-10-02T11:29:27-05:00
author: Lambert
status: for-review
topic: Helix evidence plan test coverage
---

# Test status: Helix evidence plan

Added Helix-aware evidence-plan tests covering the accepted Dallas design:

- Pure parser coverage in `AzdoMonitorFailureParserTests`: legacy messages, arcade warnings, state-only details, console URL fallback, sibling-console isolation, tree lines, ambiguous fallback, and warning/tree dedupe.
- Pure matcher coverage for `BuildPlanFromSelectedJobs`.
- Service/JSON coverage in `AzdoEvidenceHelixPlanTests`: parsed monitor jobs become `helixFailures[]` and complete plans, unparseable and unresolved monitor rows fail closed with stable `incompleteDetails[].code`, mixed artifact plus Helix evidence, multiple monitor jobs, paging/truncation fields, suggested fetch coordinates, and backward-compatible JSON fields.
- Surface coverage in existing AzDO tests: JSON-property reflection for the new DTOs, CLI/MCP schema parameters, MCP description requirements, and CLI JSON/human behavior.

## Validation

- Targeted suite: 255 passed / 2 failed.
- Full suite: 2045 passed / 2 failed / 9 skipped.

Implementation bugs (not test bugs):

1. `CliEvidencePlan_UnparseableMonitorHumanOutput_ExitsTwoWithStableReasonCode` — Expected stable reason code `[monitor_unparseable]` in human output, but missing.
2. `CliEvidencePlan_HelixFailurePaging_ReportsTruncationAndSecondPage` — Expected `[helix_failures_truncated]` reason code in first-page human output, but missing.

Design basis: `evidence-plan-helix-design.md` requires human incomplete output to include stable reason codes (e.g. `- [monitor_unparseable] ...`) for deterministic collectors to key off stable codes.

---

---
date: 2026-10-02T11:29:27-05:00
author: Ripley
status: resolved
topic: Evidence plan Helix documentation scope
---

# Implementation note: evidence plan Helix documentation scope

Implemented Dallas's Helix-aware evidence-plan behavior in Core, CLI help/XML comments, and MCP tool descriptions only. Did not update README.md or docs/cli-reference.md per charter scope (XML doc comments, MCP descriptions, and CLI help text only).
# 2026-10-02: Post-merge review fixes, volume guard, and release v0.11.0

## User directive: GPT alternation experiment

### 2026-10-02T16:22:25-05:00: User directive (supersedes 16:21 version)
**By:** Larry Ewing (via Copilot)
**What:** Wherever the coordinator's model selection, fallback chains, or "switch to code specialist" rules would pick a GPT model (gpt-5.x, gpt-5.x-codex, gpt-5.x-mini, gpt-4.1), use a MIX of `gpt-6.1-sol` and `hydrafusion` instead — alternate between them across spawns (experiment) and note which model each agent ran on so results can be compared. Claude/Gemini choices are unaffected.
**Why:** User request — trying the two side by side to see how they perform.

## Design decisions and implementation deviations

### PR1: paging and cache-key compatibility

**Date:** 2026-10-02T13:45:00-05:00
**Author:** Ripley
**Status:** Implemented — no deviations from Dallas's PR 1 design
**Note:** Snapshot replay was validated with `AZDO_TOKEN`/`AZDO_TOKEN_TYPE=bearer` to work around dnceng-public test API 302 redirect under anonymous auth.

### PR2: hlx collect azdo-build implementation

**Date:** 2026-10-02T14:15:00-05:00
**Author:** Ripley
**Status:** Implemented with documented deviations

1. `--download-helix-files <glob>` v1 records matching uploaded files as explicit `skipped` attempts (`skip.kind=size_limit`) instead of downloading bytes. The Helix file-list model exposes name/link but not a trusted byte length before `GetFileAsync`; downloading first would populate the cache before enforcing `--max-file-bytes`. Default behavior remains: no arbitrary Helix uploaded-file bytes.

2. Live replay validation for dnceng-public build 1621192 required `AZDO_TOKEN`/`AZDO_TOKEN_TYPE=bearer` to collect and replay AzDO test-run data. Anonymous public Build/Timeline/Log calls replay, but AzDO test APIs returned auth/redirect without a token. Manifest records `auth.azdo.replay=environment_token_required`.

### Independent review (findings 1–8): implementation fixes and deviations

**Date:** 2026-10-02T16:45:00-05:00
**Author:** Ripley
**Status:** Implemented with noted deviations

1. **Transport failures (Finding #1):** Added `ReadResponseBodyAsync` helper classifying `HttpRequestException`/`IOException` as `transport_error`, non-caller `TaskCanceledException` as `timeout`. Added Helix stream exception handling in `HelixService.GetConsoleLogContentAsync`. Last-resort catches in `AzdoBuildCollector.RunAsync` and CLI boundary.

2. **Helix failures not recorded (Finding #2):** All `HelixApiClient` methods now use shared `ClassifyAsync` helper. Discovered Azure.Core wrapper: `Azure.RequestFailedException` with `Status == 0` falls back to inner `HttpRequestException?.StatusCode`. Real-SDK gap found via throwaway probe; added `HelixAcquisition.FromRequestFailed` fallback. Existing test fake insufficient; Lambert's new `IndependentReviewRegressionTests.cs` uses real client + fake handler (recommended pattern).

3. **Export isolation (Finding #3):** Choose "isolated per-run temp cache dir" — `collect azdo-build --export <dir>` without `--cache-dir` now collects into `{TempPath}/hlx-collect-cache/{guid}` and records resolved path in manifest. Live validation (build 1621192): snapshot's `cache.db` contains only `azdo:7af1ee30:...:1621192` keys.

4. **Snapshot validation completeness (Finding #4):** `!validation.IsValid` now sets `Complete = false` and appends `snapshot_validation_failed` incomplete detail. Added progress hook immediately before `SnapshotValidator.ValidateAsync`.

5. **Test outcome coverage (Finding #5):** Replaced outcome list with full `TestOutcome` enum (verified against Microsoft Learn), excluding `NotRunnable` per PR156 finding that AzDO rejects it.

6. **Optional budget eviction (Finding #6):** Clamp optional budget to remaining cache capacity: `min(--max-total-bytes, cacheCap - requiredBytesThisRun)`. Conservative reserve computed once per `CollectHelixSuggestedFetchesAsync`; does not account for concurrent metadata fetches (acceptable residual risk at `MaxConcurrency=6`).

7. **TTL-filtered verification (Finding #7):** Added `ICacheStore.GetMetadataIgnoringTtlAsync` default-interface method. `SqliteCacheStore` overrides with true TTL-bypass. `VerifyCacheEvidenceAsync` uses it for metadata presence checks. Binary artifact reads already TTL-unfiltered.

**Also:** Manifest path defaults to `{EffectiveCacheRoot}/hlx-collect-manifest.json` instead of CWD.

**Test seam note (for Lambert):** `SnapshotValidationFailure_FailsClosedInBothManifests_IndepReview4` required progress hook before `SnapshotValidator.ValidateAsync` to properly hook artifact corruption. Transient early failure resolved after hook addition; test now passes reliably. No residual issue, documented for reference.

### PR156: review fixes and performance gate

**Date:** 2026-10-02T16:00:00-05:00
**Author:** Ripley
**Status:** Implemented with documented deviations

1. Live `--test-scope all` validation for build 1621192 started but was stopped after 15 minutes with no observable progress. Implementation path covered by focused tests and required-attempt verification; requested release-path live validation completed with default scope.

2. **AzDO rejection of `NotRunnable`:** Provider rejects `NotRunnable` in test-results `outcomes` query despite docs/tools listing it. Collector's `all` scope now omits that unsupported outcome while deriving and verifying default `Failed` cache key.

3. Snapshot validation run via supported `hlx snapshot validate <snapshot>` form; `--json` flag not supported by this command.

### Post-merge review (items 1–18) and NUL data-loss fix

**Date:** 2026-10-02T15:55:00-05:00
**Author:** Dallas (reviewing commit 85c5bd1)
**Status:** Approved after two rounds of fixes

#### First gate (15:55) — REJECT

**Blockers:** unbounded continuation paging, incomplete marker-prefix backward compatibility for SQLite NUL encoding.

**Required fixes:**

1. **Unbounded AzDO continuation paging:** `GetListAsync` follows `x-ms-continuationtoken` indefinitely with no max-page guard. Malformed/hostile AzDO responses can make list commands loop unbounded. Required: explicit bound, cycle detection, structured acquisition error. Added tests for repeated token and page-cap failure.

2. **Marker-prefix ambiguity:** New writes correctly encode values with NUL or `hlx:nul-base64\n` prefix. Invalid legacy plaintext starting with that prefix whose suffix is valid Base64 silently decodes differently. Required: move encoded rows to unambiguous marker/version or validate decoded values. Added regression where legacy-plaintext Base64-valid suffix round-trips unchanged.

#### Second gate (16:20) — APPROVE

**Fixes verified:**

- `AzdoApiClient.GetListAsync` caps continuation-token paging at `MaxContinuationPages = 1000`, detects repeated tokens/URLs, fails with `invalid_response`.
- `SqliteCacheStore` v2 envelope: `hlx:b64:v2:<length>:<sha256>\n<base64>`. Validates length/SHA-256 on decode. Legacy `hlx:nul-base64\n` only decoded when proven from old encoder (NUL or marker-prefixed). Valid-Base64 plaintext no longer collides.
- Snapshot validation decodes metadata before raw-log corruption checks. Exported/eval snapshots replay collected items. v1 snapshots accepted with compatibility warnings.
- Local targeted validation: 78 passed / 0 failed / 0 skipped (paging/cache-key/collector/auth/immutability/SQLite/export/validator tests).

#### Release-note items (if fixes land)

- **Breaking changes:** CLI list commands have new JSON envelope/paging semantics; truncated output exits 2 unless `--allow-truncated`; `hlx collect azdo-build` has deterministic manifest completeness; eval-mode AzDO snapshots may require `HLX_EVAL_AZDO_PARTITION`.
- **Security/privacy:** collection redacts URL credentials, query secrets, fragments from argv; auth-scoped snapshots use non-secret cache partition IDs.
- **Data-loss/corruption fix:** SQLite cache metadata now preserves NUL-containing values. Users should clear caches and re-collect snapshots from v0.10.3 or earlier; already-truncated zero-byte rows cannot be recovered.
- **Reliability:** Retry-After honored beyond configured backoff cap with one-hour safety ceiling; collect verifies evidence before export, records replayable provider failures.

### PR156 comprehensive pre-release gate

**Date:** 2026-10-02T16:20:00-05:00 onwards
**Reviewer:** Dallas
**Final Status:** REJECT for release (blockers R1–R3 require fixes before tag)

#### Executive summary

Accept Lambert's three required volume/progress revisions. Do not tag: independent finding 6 (eviction) is unresolved, job-list classification missed real SDK exception, documentation inaccurate. Ripley/Lambert both authored rejected implementation artifact; escalate to Larry. Kane eligible for documentation-only updates.

#### Validation evidence

All-test acquisition on build `1621192` run `44916566`:
- **133,036 distinct results** (IDs 100000..233035)
- **14 HTTP-200 pages** (10K rows each + final 3,036)
- **285,338,304 bytes** before projection
- **66.745 seconds** with 180-second cancellation timeout
- CLI independently completed in **60.765 seconds**, exit 0

Without guard, full build entails **~1.4 million attachment-list calls** (all selected results, including passed/not-applicable). Current stall reproduces: attachment work captured in cache but no observable stderr progress.

#### Release blockers

**R1 – Required Helix evidence still evicted by optional downloads**

Probe with `MaxConcurrency=1`, 1MB cache cap, 614K required console, 716K optional file:
- Pre-Helix reserve: only 4,766–4,767 bytes
- Optional file fit advertised budget
- Actual write evicted previously acquired console
- File remained `ok`, console became `failed/not_in_snapshot`, `artifact_missing`, `complete=false`

Current test `OptionalDownloads_CannotEvictRequiredEvidenceUnderSmallCacheCap_IndepReview6` passes only because optional files exceed per-file caps; it does not test individually in-budget file causing eviction.

**Required revision:** Protect run's required artifacts across whole acquisition, including concurrent console fetches. Options: finish required acquisition before determining optional headroom; coordinate publication/eviction; pin evidence. Add single-worker, multi-work-item/concurrent cases, snapshot validation and offline replay. Remove eviction-guarantee language.

**R2 – Helix job discovery leaks Azure SDK exceptions**

Six metadata methods use `ClassifyAsync` including `Azure.RequestFailedException` branch. `ListJobsByBuildAsync` retains separate catch omitting that exception.

Probe: fake transport throws HTTP 403. Both `ListJobsByBuildAsync` and `AzdoService.GetHelixJobsAsync` threw raw `Azure.RequestFailedException`. Service only catches `HlxAcquisitionException` on primary path.

**Required revision:** Route job-list call through same client-boundary classifier, preserving cancellation and `list_helix_jobs_by_build` operation. Add real-SDK transport-wrapper regressions proving service fallback to timeline with classified primary error.

**R3 – Release documentation inaccurate**

- Multi-partition snapshot claim vs. actual `EvalSnapshotAzdoPartitionSelector.Select` precedence (explicit env > manifest > single partition > fail).
- Export isolation per-command with fresh cache; resume does not rediscover old temp root. Show cache-dir reuse workflow.
- Unreleased notes should record export isolation fix, cache-local manifest default, mid-body transport classification, invalid-snapshot completeness, outcome coverage, TTL-independent verification.
- Align optional-download docs with corrected R1 behavior. Manifest `policy.caps.maxTotalBytes` records requested value, not cache-derived budget.

Kane may revise Markdown docs/CHANGELOG. Source-help and behavioral fixes escalate to Larry.

#### Accepted changes (all other items)

- Build-wide result guard: 64-bit sum, 10K default boundary, explicit consent, negative fail-closed, `test_result_limit`, resume recheck, policy-only allow-incomplete.
- Safe attachment policy: diagnostic outcomes by default, 1K build-wide cap, wider scope requires explicit cap, deterministic selection, aggregate exclusions, failed-result replay.
- Observable progress: synchronized stderr reporting, 2-second throttle, 5-second heartbeat, cancellation joins timer tasks, JSON stdout parseable.
- Finding 1 (transport): classified failures, retries, persisted manifest, caller cancellation preserved.
- Finding 2 (Helix): six named operations classified, real-SDK negative coverage, test-fake insufficient (Lambert's real client + fake handler pattern correct).
- Finding 3 (export): fresh per-run temp root before lazy store, unrelated rows stay out, explicit cache-dir still includes whole directory.
- Finding 4 (validation): validation failure sets complete=false, exit 1, snapshot_validation_failed in manifests.
- Finding 5 (outcomes): all 15 TestOutcome values, excluding NotRunnable, derived Failed retained/verified.
- Finding 6 (eviction): **REJECTED** — reproducible required-evidence destruction; documented as R1 blocker above.
- Finding 7 (TTL): TTL-bypassing verification without changing live TTL reads; expired-but-present rows export/replay.
- Test seam: CreateForTesting internal static, SDK-taking private, no public reflection path, existing friend assembly unchanged.
- Manifest cleanup/default: accidental tracked manifest removed/ignored, default standalone manifest at cache-root, exported manifest at manifest/path.

Six original Copilot fixes remain: structured cache-key parsing, raw-log recognition, suggested-fetch dedup/legacy handling, derived-Failed verification, streamed file classification/budget, bounded continuation. v1/v2 snapshot and legacy capped-key compatibility covered.

#### Validation results

- Focused selection: 136 passed, zero failed/skipped
- Complete suite (.NET 11 preview): 2,330 passed, 9 skipped, zero failed, zero warnings
- CLI on 1621192 isolated cache, logs/Helix disabled: ordinary all scope refused 1,405,433 rows in 2.16 seconds, exit 2, zero attachments, one required-budget skip. JSON parseable, stderr included 49-run estimate and remediation.
- Lambert's 311-second opted-in live all-scope: 1,405,433 rows/five attachments, default/resume/offline regressions.
- No production/test/README/CLI-ref/CHANGELOG edits; only decision/history appends and disposable diagnostics.

## hlx usage audit synthesis

**Date:** 2026-10-02
**Author:** Ash
**Status:** Proposed

Audited 372 hlx pain-point findings from 199 local Copilot sessions against current code, changelog, commits #153–#156. Larry's "bundle = offline snapshot" directive: every direct AzDO/Helix/GitHub fallback becomes offline hard failure unless `hlx collect azdo-build` collects evidence into cache snapshot first.

**Current branch fixes foundation:** structured acquisition errors, `not_in_snapshot`, recorded provider failures, CLI list paging (AzDO list commands), `hlx collect azdo-build`, manifest verification, credential-free replay, Retry-After handling, NUL raw-log cache corruption detection.

**Still-open product gaps:** build-set discovery, AzDO artifact/binlog bytes, test attachment/dump bytes, timeline/log/search cursors/selectors, cross-build history, Helix bulk/file collection beyond selected globs.

**Recommended PR groups:**
- (A) build discovery + collector driver
- (B) AzDO artifact/attachment byte downloads with caps
- (C) timeline/search complete projections
- (D) cross-build history
- (E) Helix bulk offline collection

**Full audit artifact:** `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/hlx-audit/hlx-usage-audit.md`

---


---

## 2026-10-02 User Directive

### 2026-10-02T21:35:48-05:00: User directive — access, not restriction
**By:** Larry Ewing (via Copilot)
**What:** hlx's goal is NOT to limit what an agent can get — limits just make the agent curl the file and stop using hlx. The goal is to make it easier and more efficient to access what it needs. Defaults shape responses for efficiency (triage-first, paging, previews, summaries), but any explicit request must succeed through hlx: no refusals for size, every truncated/capped response carries the exact next step (next page, full projection, file path / download command), and large content is delivered efficiently (streamed to disk/cache, member-level selection, path handoff) rather than blocked. Safety caps exist only to protect the machine (disk/cache headroom, runaway unattended bulk), never to deny an explicit request; when hit, they report the precise way through.
**Why:** User request — audit showed agents abandon hlx for direct REST whenever hlx hides or refuses data; offline that fallback doesn't exist.

---

## 2026-10-02 P0 Audit (Dallas)

---
date: 2026-10-02T22:04:26-05:00
author: Dallas
model: gpt-6.1-sol
status: final
topic: Audit P0 architecture and PR phasing
---

# Audit P0: bounded views over complete offline evidence

**Finalized under Larry's overriding access-not-restriction directive** (`copilot-directive-20261003T0235Z.md`): hlx makes requested evidence easier/more efficient to obtain; it does not deny supported explicit requests by size. This supersedes earlier hard response ceilings, 512/50-MiB gates and quota refusal text. Keep triage ordering/previews, complete offline snapshots, integrity/auth/machine protection and GPT alternation. Dallas ran gpt-6.1-sol, without delegation or `src/` implementation.

Every truncated/preview/deferred response includes the **exact next tool arguments, verified path/evidence reference, or ready CLI command**. Supported explicit full/all/named-file requests stream/page with progress; default unattended safeguards may checkpoint only with the original request plus **one --allow-large** continuation. Physical space/auth/corruption are genuine constraints, not disguised size policies.

## P0-1: timeline

`azdo_timeline` defaults to **triage**, not counts-only compact: diagnostic Jobs/Tasks plus Stage/ancestor context; Phase duplicates suppressed; actual id/parentId/type/name/result/attempt/log.id and **up to five deduplicated 200-Unicode-scalar issue previews**, with type/count/issueTotal/truncation metadata. Keep original parentId and expose nearest displayed contextParentId; never rewrite cached topology. Rank error/parsed-or-unresolved monitor evidence before generic status/warnings, preserving concrete failed-work-item warning text. Dedupe full messages before truncation, only formatting-equivalent issues within a record/attempt; never erase codes/IDs/paths/test names or merge equal clipped prefixes.

Compact/full/summary remain available. Implicit triage excludes bare skipped statuses, but exact selectors bypass an omitted failure preset/suppressed Phase and expose effective scope. **12/16 KiB are inline shaping targets, never a size-error path.** Larger counts, previews, records/issues/full sets use verified cache-backed file delivery or advancing chunks, not a domain error or counts-only fallback. Preserve #154 inline completeness; separately mark complete accessible delivery only after actual write/hash verification.

Add shared **hlx_read_evidence** in PR1: scoped evidenceId, offsetBytes/lengthBytes, UTF-8 or explicitly requested bounded base64 chunks, exact next and full delivery descriptor. Runtime derived files are outside immutable snapshots; they add **no selector-specific timeline evidence keys**. CLI --output/file receipts keep stdout small. Remote text is readable through MCP; binary delivery stays server-local/CLI-on-debugger-host. Every shortened issue has the actual full-record action, not "try full" without coordinates.

Fresh v0.11.0 stdio measurements:

| Build | Full records | Old default failed MCP wire bytes / returned | Ranked triage prototype, five 200-char previews |
| --- | ---: | ---: | ---: |
| 1621192 | 1,524 | 241,726 /122, untruncated | **4 rows /10,840 wire bytes** |
| 1600801 | 189 | 66,591 /49, untruncated | **9 rows /11,505 wire bytes** |

Refined evidence is 162 timeline findings /142 sessions: failed/issues 112, counts 53, monitor/Helix 34, too-large 29, log IDs 27, name 19, attempts 16, record/parent/child 12. Current default's 71 Phase duplicates and measured first-ten triage sizes justify small efficient inline pages, **not withheld data**. The prototype pages above stay below 12 KiB; final headers must include exact actions and full-access delivery. Production uses the real pure parser and tests complete reconstruction, not just a small first response.

Scope the current global `result -> resultFilter` alias to search/the declaring schema before adding canonical timeline/build `result`. Reuse one bounded general `*`/`?` glob matcher, retaining bare-substring compatibility; this also addresses the audit's internal-wildcard failure.

## P0-2: build-set discovery

Build filters remain additive/full-field/top=20-compatible; larger full results get exact pages/file handoff rather than raw spill. No new 100-row or collector access ceiling. **50/25/5000 and 10000/1000 are unattended planning defaults**, not denial of larger explicit queries. --allow-large resumes the same captured query/checkpoint with progress and actual headroom; explicit all/larger counts run through efficient batches. Duplicate exact definition names form a declared union; numeric page-work thresholds checkpoint, while real token/URL cycles still fail.

Use canonical complete/window/definition/paged keys, old hash fallback and manifest coverage; complete evidence serves any requested projection/window and provably narrower queries offline. Large query acquisition/cache/output is paged/streamed, not a giant array. Default deferral persists source checkpoint and exact one-flag resume; no false all key/global count. Offline missing coverage has the precise live recollection/new-export action.

Preserve queue/start/finish time-field semantics. For a recognized rejected status/order pairing, advance the same ordered/time-bounded query with exact local status filtering, not an unconditional switch to finishTime. A fresh public completed+queueTimeDescending probe succeeds, so the historical rejection is not universally reproduced.

Manifest schema v2 is additive/read-compatible with single-build v1; snapshot SQLite schema remains v2. All members' required evidence must finish before optional downloads; preserve credential-free partition selection and legacy single-build resume.

## P0-3: artifact and attachment bytes

Add two download tools/CLI with exact/name/member selectors, small complete-access receipts and source/member hashes, reusing the shared reader. Explicit larger artifacts/binlogs/dumps/successful-leg/non-Logs requests proceed without size/classification/unlocking flags. Default triage plans are preferences only; explicit scope overrides them. **Member selection stays P0; binlogs remain download-only.**

**Anonymous PipelineArtifact exact-member fast path verified:** resolve metadata downloadUrl's content route, set format=file and **leading-slash artifact-relative subPath=/Checked/Build.binlog**. Received 2,148,286 bytes; SHA-256 matches the extracted full-ZIP member. Large /Release/Build.binlog probes returned Content-Length 43,057,642 and 226,525,958 (only 64 KiB read). A synthetic artifact-name prefix/missing leading slash fails. Directory ZIP scoping works; file/malformed ZIP subpaths can return HTTP-200 empty 22-byte ZIPs, so status alone cannot prove selection. Discovered dedup nodes/chunks/Pipelines signedContent redirected to sign-in; anonymous complete glob enumeration was not established.

Unknown/glob paths stream ZIPs to private seekable disk with actual headroom, not a numeric archive ceiling; retain only requested members/catalogs, not a full ZIP or unselected bytes. Avoid hidden whole-memory buffering, preserve safe paths/CRC/hash and source provenance; valid large catalogs use disk-backed work/progress rather than an entry-count refusal. Direct requests do not invent ZIP hashes; generated timestamps/hash are not stable member identity.

New member/catalog keys include canonical relative-path SHA through AzdoCacheKeys, never SAS/output paths. Manifest ordinary bytes/sha remain persistent member/catalog evidence; separate transfer fields record unretained ZIP SHA/size. Replay requires covered catalog/member bytes or an explicitly collected full ZIP; missing/wider member selection is not_in_snapshot. Keep #156 required-first/cache-headroom/atomic publication and credential-free partition rules.

**Superseded gates:** no typed 512/50-MiB or default total denial for explicit data. Measurements guide physical disk/cache/spool reservations, constant-buffer transfer, concurrency, cache retention leases/temporary target expansion or caller output. A normal cache target is not authorization to reject a bigger file. Unattended 2 GiB planning can checkpoint with the exact --allow-large action; required evidence remains pinned, and actual physical exhaustion gets exact alternate-storage/resume detail. Mutable data can be captured as observed-at/fingerprinted, not refused until terminal.

**Efficient automatic plan:** opt-in failed Logs/Build.binlog, possibly zero build ZIPs on monitor-only-failed 1621192. But an explicit artifact/member/glob/archive/all request is never silently intersected with that plan or required to add scope=selected. Attachment selectors acquire needed metadata even when unrelated bulk tests are off.

**Resume/ranges:** native-member and ZIP Range probes both returned HTTP 400 `"Content-Range is not accepted as a part of the request."`; no-range native control returned 200. Use upstream byte resume only when verified; otherwise restart that file without Range, retaining completed members/checkpoints and progress. Locally materialized evidence supports exact ranges/chunks regardless of upstream; no fake range-resume success.

**Flagged assumption:** server-local HTTP paths are acceptable for P0, with CLI on the debugger host as the supported transfer path, per coordinator/Larry's lack of objection. No new remote transfer/upload service is authorized.

## Phasing and near-free P1 work

One feature PR per P0 stays **timeline -> build sets -> bytes**. PR1 adds full delivery/scoped reader; PR2 progressive query/collection/checkpoints; PR3 efficient source/member transfer and retention. Acceptance proves complete obtainable data and exact next action, not just a small header or compliant quota. Preserve real integrity/auth/operator constraints; no public transfer/upload service.

## Existing-behavior follow-ups (inspected, not implemented here)

- **#157 --test-scope all above 10000:** replace manifested refusal with paged cache/output collection and progress for explicit all. Keep estimates, cancellation and diagnostic attachment defaults; default fan-out deferral has the exact original all-scope --allow-large --resume action, not "use failed."
- **Attachment metadata 1000/default explicit-max requirement:** explicit all/known dumps proceed incrementally; do not auto-enumerate every passed test just because results scope is all.
- **MCP test-results/runs/attachments/changes/artifacts/builds:** replace CreateLimitedResults' vague "higher top" with exact cursor/full-file access; reuse complete cache keys and the shared reader.
- **AzDO/Helix search and 50-MiB uploaded-text search:** stream text, provide log/line continuation or complete file; do not error due file length/read all lines at once.
- **Evidence-plan 200 entries/10 candidates/1000 Helix failures:** complete plan/candidates via paging/file; keep no-download planning and fail-closed parsing, not hidden collector evidence.
- **Helix batch status >50:** split work into batches, not reject valid larger job sets.
- **Page-count ceiling/cache target/mutable source:** checkpoint valid work and captured observations/retention, preserve genuine cycle/corruption/auth protections; no artificial data ceiling.
- **Dead-end not-found/binary/snapshot paths:** truthful outcome plus exact scoped listing/download/recollection/reader command. No fabricated success or curl workaround.

No product question blocks this final access-first scope. The full design includes exact new delivery/reader/allow-large fields, CLI recovery, cache/manifest/progress semantics, machine protection and source-level follow-up matrix. No existing behavior is claimed fixed by this design-only revision.

Full exact tool/parameter/field names, descriptions, CLI flags, key grammars, replay semantics, manifest changes, tests and docs:

`/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/audit-p0-design.md`

---

## 2026-10-03 Dallas P0-1 timeline gate review

# Dallas P0-1 timeline gate - 2026-10-03

**Verdict: REJECT** `a6e24e9a9ae9bcfae1f0107df511abb795649658`.

Requested by Larry Ewing. Reviewer: Dallas, Lead, gpt-6.1-sol. Reviewed HEAD and its diff against origin/main, the P0-1/shared design, ACCESS-FIRST, Ripley's deviations, and Dallas's history. Production/test/release-document files were not edited. Only this decision and Dallas's history are appended by this review.

## Decision on the failing receipt test

Choose **(a): an 8 KiB minimum effective inline target**, not descriptor removal or a test-only relaxation.

For both timeline delivery and the shared evidence reader, positive requested targets normalize to `effectiveMaxResponseBytes = Math.Clamp(requestedMaxResponseBytes, 8192, 16384)`; the default remains 12288. Report the original requested value separately as `requestedMaxResponseBytes`, and report the effective value as `maxResponseBytes`. Nonpositive values are invalid input, not a request to clamp silently. Preserve `sourceFingerprint`, `backingCacheKey`, `capturedAt`, `freshness`, and the rest of the delivery descriptor.

**Ripley implements the production normalization/envelope/budget changes. Lambert updates the contract expectation and adds the boundary cases.** The 4096-byte pathological-selector request must still succeed and recover the exact original name; it now asserts requested=4096, effective=8192 and an actual final wire response within the effective target. Update the existing 2048-floor expectation as well. Measure the final response after descriptor, continuation, note and framing are present; changing only the test's upper bound is not sufficient.

## Deviation #1

**Reject a 4x overage allowance.** It defeats the harness-spill rationale: 4 x 12288 permits roughly 48 KiB inline. ACCESS-FIRST requires a lossless access route, not that large data remain inline. A zero-row receipt with a working evidence reader is valid; a repeated zero-row page is not.

The pinned HEAD already uses an **absolute 1024-byte allowance**, not 4x, at `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:103`. Its row reduction and preview-length/count reduction at lines 53-84 are the right shaping order. Accept that narrow allowance only for advancing multi-row pagination after those reductions, with the final inline wire response no larger than 16 KiB. A unique over-target selection must use delivery rather than overage. Keep shortened previews useful and attach exact full-detail actions. Ripley must correct the stale deviation document.

During review, unrelated agent work added uncommitted helper/test changes restricting overage to selectedTotal>1 and adding another escaped-name case. Those changes are not part of this HEAD approval; do not treat the pinned 95/1 result as validation of the new working tree.

## Numbered required fixes

1. **Receipt target and final accounting - Ripley; contracts Lambert.** `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs:142`, `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:35,103,114`, `src/HelixTool.Mcp.Tools/AzDO/HlxEvidenceMcpTools.cs:40`, `src/HelixTool.Tests/AzDO/TimelineViewContractTests.cs:547`. Implement the floor/reporting decision above; account for the actual final receipt, not just the pre-receipt projector result. Preserve provenance. Restrict the small overage as specified above and update `.squad/decisions/inbox/ripley-timeline-deviations.md`.

2. **Authenticated HTTP evidence reads - Ripley; contracts Lambert.** `src/HelixTool.Mcp.Tools/AzDO/HlxEvidenceMcpTools.cs:39`, `src/HelixTool.Mcp/Program.cs:56,125`. Resolve the current AzDO credential/cache identity before reader lookup, using the same identity resolution as the originating tool, without trusting the descriptor or scanning other partitions. HTTP creates fresh scoped CacheOptions: the timeline request sets AuthTokenHash, but the next reader request sees null and searches public. Live authenticated HTTP delivery of all 1524 records succeeded when limit=2000; its immediately returned read action failed `Evidence reference ... was not found in this credential/cache partition`. The same normal-size read works in stdio. Require a production-host multi-request authenticated HTTP round trip and cross-context denial; singleton/public fixture DI does not cover this defect.

3. **Complete explicit delivery and CLI flag parity - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:238`, `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs:197`, `src/HelixTool/Program.cs:1668`. Core ignores request.All; all=true and delivery=file merely bypass byte shaping while retaining limit=10. Live filter=all/full/expand=none selected 1524 records but both delivered files contained only 10 and complete=false, although the receipt claimed the complete selection was written. Make all/file retrieve the entire selected scope without changing deliberate filters or hiding incomplete acquisition/graphs. Wire delivery=chunked to the documented file-backed evidence-reader behavior, rather than silently treating it as auto (live: six inline rows, no delivery). Add the specified CLI --delivery flag; today it is unrecognized. Test all/file with omitted and smaller limits, not only limit equal to fixture count.

4. **Executable preview/page continuations - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:462`, `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:80`. Carry previewChars, previewIssueLimit, effective byte target and other relevant delivery/view settings in next/whole-selection actions. For a byte-shortened candidate, its action must reproduce that candidate's semantic view. Live previewChars=40, limit=1 returns a continuation that omits previewChars and immediately fails its own stale-viewId check against the unchanged source. Execute every nondefault and automatically shortened action in tests.

5. **Clipped preview recovery - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:660,793,809`. Include message clipping in previewsTruncated/fullAction eligibility, not only group omission or deduplication. Live Build Tests on 1621192 and Build DeviceTests on 1600801 have messageTruncated=true but no fullAction; the former also has previewsTruncated=false. Every shortened message must carry an executable exact full-record action. Add a one-long-issue/no-dedupe/no-omitted-group case.

6. **Monitor/error-before-warning ranking - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:414-423`. Preserve the shared failed inclusion predicate, but do not use it as a failure-severity predicate: it includes every warning-bearing success, making the warning-only band unreachable. Add the missing failed-monitor nameHint rank, then substantive errors/actual failed diagnostics, then successful warning-only rows and context. Live 1621192 currently returns Monitor Helix Jobs Task followed by a succeeded Build Tests warning row, while its failed monitor Job is displaced behind warning-bearing Tasks. Test the complete real selection and first-page ordering, not only the four-record monitor slice.

7. **Honest unbounded issue-window completeness - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:564-582`. With recordId/full/expand=none/issueOffset=2 and no issueLimit, the live three-issue monitor record returns one issue but complete=true, issueComplete=true, truncated=false and no continuation in both MCP and CLI (CLI exit 0). Nonzero final issue windows remain incomplete and carry an exact whole-record recovery action, including the no-issueLimit and beyond-end cases. Preserve the existing offset-zero complete behavior.

8. **Shared validation and overflow-safe advancing reads - Ripley; contracts Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:297`, `src/HelixTool.Mcp.Tools/AzDO/HlxEvidenceMcpTools.cs:41`, `src/HelixTool.Core/Delivery/FileEvidenceDeliveryStore.cs:156`. Validate projection/expand/result/state/delivery, issueOffset>=0, issueLimit>0 when supplied, and positive range/target sizes in shared logic so CLI and MCP agree, including empty timelines. Live CLI accepts projection=bogus as a full-shaped success, expand=bogus without expansion, and state=bogus as complete empty success. issueLimit=0 creates a nonadvancing issue next action in both CLI/MCP. Reader lengthBytes=0/-1 is silently changed to 1. More importantly, valid positive lengthBytes=9223372036854775804 over a 793107-byte evidence file wraps lengthBytes+4, returns zero bytes and repeats offset=0 in next. Bound each physical read before allocation/addition with overflow-safe arithmetic; larger positive ranges must still succeed through advancing chunks. Assert nonzero progress whenever unread source bytes remain.

9. **Immutable eval destinations - Ripley; contracts Lambert.** `src/HelixTool/Program.cs:1779`. The shared runtime store correctly places eval delivery outside the snapshot, but CLI --output writes the caller path without an eval-snapshot containment check. Protect the snapshot from caller-selected output paths too: reject an unsafe snapshot-contained destination with an exact outside-snapshot delivery action, or redirect through the runtime delivery store while reporting the actual destination. Add an eval-mode --output-inside-snapshot test asserting no new/changed snapshot files. This is a source-confirmed uncovered path, not a claim that the passing runtime-placement test failed.

10. **Public docs, breaking notes and routing examples - Kane.** `README.md:141`, `docs/cli-reference.md:146`, `CHANGELOG.md:9`, `.github/skills/helix-cli/SKILL.md:110-112`. HEAD contains no P0-1 updates to these surfaces; Unreleased is empty. Document triage/Phase/row-default changes, selectors and preset interaction, exact continuation/evidence-reader delivery, the 8 KiB floor, file/chunked/CLI output behavior and snapshot immutability. Declare the intentional CLI .results envelope/exit-2 migration and the explicit --raw-json full/Phase escape path. Fix the skill's timeline jq pipeline from .records[] to .results[] and teach partial-page handling so pipe consumers do not silently lose later rows. MCP timeline routing copy and scoped result alias are otherwise good; do not claim all/file/chunked access is complete until fixes 2-4 are implemented.

## Live measurements and validation

Freshly built HEAD processes ran in normal live mode, with HLX_EVAL_SNAPSHOT/HLX_EVAL_AZDO_PARTITION removed. Numeric build IDs used dnceng-public/public. Both SDK text and structuredContent copies are counted. Stdio measurements include the JSON-RPC line and newline; HTTP measurements include the returned JSON-RPC/SSE body, not HTTP headers.
