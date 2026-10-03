# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, strict-mode architecture, and merge-gate reviews.

**Current Focus:** v0.11.0 release cycle: helix-aware evidence plan (#153), CLI paging (#154), collector (#155), post-merge findings (#156/#157), and pre-release hardening. All blockers (R1/R2/R3) resolved and shipped v0.11.0 (e713995, NuGet + container).

## Durable Decision Principles

- Reframe issue titles into root-cause defect bundles before assigning implementation; tracking a task is not enough if its cutoff/time semantics remain wrong.
- Make minimal-public-impact requirements mechanically checkable with exact file boundaries.
- Prefer narrow enumerated exception handling over prose rules like "avoid broad swallowing."
- Ban race-outcome assertions; tests should prove deterministic end-state and observable contracts.
- Treat changed SDK defaults as silent diffs: force important defaults into source and guard with tests.
- Verify agent-reported release metadata and claimed platform constraints against primary sources or repo greps.
- Evidence-driven scope amendments are valid when red/green tests disprove an architectural assumption.

## Standing Architecture Context

- Stable folders: `src/HelixTool.Core/{Helix,AzDO,Cache}/` and `src/HelixTool.Mcp.Tools/{Helix,AzDO}/`.
- Keep business logic in services, MCP tools thin, and caching/offline behavior behind decorators.
- AzDO/Helix API clients should maintain strict input validation, cache isolation by auth context, and explicit auth failure surfacing.

## Learnings (Summary)

**Helix-aware evidence plan:** `entries[]` artifact-only; represent parsed monitor failures in additive `helixFailures[]` to avoid weakening `status`/`candidates[]` invariants. Timeline issues contain enough arcade monitor evidence without Helix API calls. Monitor detection parse-first; deterministic CI collection needs stable `incompleteDetails[].code`, explicit truncation totals/pages, and Helix fetch coordinates so parse gaps fail closed.

**Acquisition error contract:** MCP 2.2.0 needs `CallToolFilter` for structured errors. Treat provider acquisition and collection policy as separate layers. Empty success only valid after successful provider shape (`value: []`, empty HTTP-200 log, zero matches). Preserve exit 2 for produced-but-incomplete output; use exit 1 + JSON envelope for hard acquisition failures.

**Paging completeness:** Later pages with fewer rows than total must fail closed, not claim completeness. Regression tests should not encode "last page complete" unless `limit >= total`. Completeness is about full selected-failure evidence, not whether more rows exist after current offset.

**Snapshot misses and negative replay:** True eval/offline cache misses need distinct `not_in_snapshot` kind (keep `provider=cache`, let scripts inspect `error.kind`). Negative snapshot replay preserves original provider failure + adds `source="snapshot"`, `replayed=true`, `recordedAt`. Only `not_found`, `access_denied`, and `invalid_response` recordable. Schema v2 adds `cache_acquisition_errors`; v1 snapshots valid with warning.

**Collector design:** Manifest records every fetch/skipped/failure with `AcquisitionError` shape, retry count, paging, bytes/hash, completeness semantics. Keep MCP capped in v1; deterministic completeness in CLI collection. `hlx collect azdo-build` populates live cache + optionally exports snapshot.

**Lockout protocol:** When lead rejects commit and locks out author, different agent must revise. AzDO empty-log ambiguity: empty body + absent logId = `not_found`; empty body + logId present = success. Validate in `AzdoService` (service layer), not `AzdoApiClient` (provider layer), not cached until proven.

## Recent Sessions (Most Recent ~5)

### 2026-10-02 — PR #156 pinned gate, confirmed paging and volume evidence

Confirmed REJECT at `a20fb36` for release. Accept six direct Copilot fixes but require all-result preflight budget, safe attachment selection, and observable stderr progress. Measured run `44916566`: 133,036 unique results in 66.745s over 14 advancing 10,000-row pages, 285 MiB HTTP bytes. Uncached `--all` completed in 60.765s, complete=true. Cache inspection found 13 complete all-outcome sets (219K rows) + 43K empty attachment lists. Require build-wide `TotalTests` preflight, explicit `--max-test-results` above conservative threshold. Preserve failed-only replay and capped MCP. Full suite 2351 pass / 9 skip / 0 fail.

### 2026-10-02 — Final pre-tag gate at 0794f61

REJECT for release. Accept all prior volume/progress revisions and findings 1–5/7, but finding 6 remains reproducible. Deterministic single-worker fixture: 1 MiB cache, 600 KiB required console, 700 KiB optional file. Pre-Helix reserve sees ~4.8 KiB AzDO; optional download succeeds, evicts console, yields `artifact_missing/complete=false/exit 2`. Required Helix artifacts must be acquired/reserved before optional publication. Real-SDK blocker: `ListJobsByBuildAsync` doesn't use shared `Azure.RequestFailedException` handling; narrowed catch cannot preserve structured primary-error timeline fallback. Multi-partition snapshot source discrepancy: auto-selects manifest partition, contradicting CLI/docs claim of fail-closed-unless-environment. Full suite 2330 pass / 9 skip / 0 fail.

### 2026-10-02 — Release re-review at 083e252

REJECT for release, documentation only. Lambert override authorized; both implementation blockers accepted. Reran eviction probe at concurrency 1 and 6: both complete/exit 0, optional file marked `total_size_limit`, exported snapshots valid and replay full required console bytes. Real SDK HTTP-403 now yields classified `HlxAcquisitionException/access_denied/list_helix_jobs_by_build`. Service returns incomplete timeline fallback with PrimaryAcquisitionError, not leaked `Azure.RequestFailedException`. All seven public provider calls use `ClassifyAsync` with caller cancellation preserved. Full suite 2351 pass / 9 skip / 0 fail.

### 2026-10-02 — Documentation re-review at 3bc3c5b

REJECT on one remaining Markdown example path; Kane eligible. Four corrected CHANGELOG entries match source. docs/cli-reference.md omitted `public/` in standalone manifest path: base `/tmp/hlx-collect-cache/<guid>`, but `GetEffectiveCacheRoot` appends `public` and `ResolveManifestPath` writes there. Correct comment/--manifest to `<guid>/public/hlx-collect-manifest.json`, or omit --manifest so original --cache-dir resolves default automatically. Keep --cache-dir at base without `public`.

### 2026-10-02 — Final release approval at 9a939a4

**APPROVE** HEAD for release; supersedes earlier rejections, no blockers remain. Verified corrected example against source: retain recorded base --cache-dir, omit --manifest, let `ResolveManifestPath` use effective partition directory (`public/` in CLI) while exporting to new destination. Confirmed final breaking/security/data-loss/features/fixes release notes with required-evidence retention, atomic optional headroom/resume accounting, classified SDK discovery fallback. Prior independently rerun probes and 2351-pass/9-skip full suite remain applicable (subsequent commits documentation-only). Decision/history appended; no source/release-doc/test/tag changes.

