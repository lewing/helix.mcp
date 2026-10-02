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
