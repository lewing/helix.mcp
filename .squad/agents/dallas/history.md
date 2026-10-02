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
