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

### 2026-10-02T19:40:00-05:00 - Audit P0 design

- Designed Larry's approved P0 batch on gpt-6.1-sol without delegation or source implementation: timeline complete projections/selectors, query-backed build-set discovery/collection, AzDO artifact and run/result attachment bytes. Read decisions, bundle=snapshot directive, GPT alternation directive, audit/raw representative findings/digests, released cache/parser/paging/collector contracts, and MCP design/routing guidance.
- Independently measured v0.11.0 stdio azdo_timeline: build 1621192 default failed response is 241726 wire bytes for 122 records; 1600801 is 66591 bytes for 49 records. Both claim untruncated. Compact 10-row SDK serialization probes are 10405/10247 bytes; 20 rows already exceed 17 KiB. Recommend compact default, limit 10, 12 KiB default /16 KiB hard response ceiling counting SDK text+structured duplication and transport escaping.
- Timeline selectors/pages slice the existing complete timeline key in memory; no selector snapshot keys. Preserve failed preset meaning, stable records/log IDs and explicit full CLI replay. Scope the global result-to-resultFilter alias before adding canonical result predicates; reuse a general bounded glob matcher rather than perpetuating unsupported internal-star patterns.
- Build-set collector should freeze a semantic query's membership, persist versioned complete/window query keys and definition mappings, support provably covered narrower queries offline through the existing manifest, and perform one required-first run-wide collection/export. Retain legacy hash compatibility and credential-free partitions. Public completed+queueTimeDescending currently succeeds; do not silently change time-field semantics based on the historical rejection.
- AzDO bytes are opt-in, streamed/hash-verified stable-ID cache artifacts with bounded MCP reports and CLI delivery paths. Share actual cache-headroom/total-byte reservation with Helix files, support run/result attachments, and keep binlogs download-only. Manifest v2 is distinct from unchanged snapshot DB schema v2; no parallel bundle format.
- Persisted full design to session files/audit-p0-design.md and concise decisions to .squad/decisions/inbox/dallas-audit-p0.md. Recommended one PR per P0 in that order, carrying routing-copy near-free wins; open Larry choices are presentation/JSON migration defaults, build-set/global volume ceilings, dump byte limits, and remote HTTP delivery expectations. Measurement proofs are session-local; no feature/test/release-doc edits or PR creation.

### 2026-10-02T21:35:07-05:00 - Final audit P0 revision

- Finalized the design and decision inbox with Larry's user-directed triage default, collect-only 50/25/5000 and run-wide result/attachment limits, 512 MiB diagnostic versus 50 MiB other caps, and P0 member selection. Supersedes the earlier compact default, universal 50 MiB cap and member-extraction deferral. Interactive build-list defaults/quotas remain unchanged; HTTP server-local delivery is flagged as the accepted working assumption.
- Triage drops duplicate Phase rows, prioritizes errors/monitor evidence and carries up to five 200-scalar full-message-deduplicated previews with source issue counts/shortening. Source graph/IDs/attempts stay intact; contextParentId traverses suppressed Phase. Revised SDK serialization proof: default first pages 4 rows/10840 bytes for 1621192 and 9 rows/11505 bytes for 1600801, under 12 KiB; keep 16 KiB ceiling below observed spill floor, do not discard error text to preserve ten rows.
- Anonymous PipelineArtifact single-file access is verified at generated content route format=file with leading-slash artifact-relative subPath=/Checked/Build.binlog. Complete 2148286-byte response SHA matches extracted full-ZIP member. Native large-file probes returned Content-Length 43057642 and 226525958, reading only 64 KiB. Missing leading slash/artifact wrapper prefixes fail; ZIP file-path/malformed selection can return empty HTTP-200 22-byte ZIP, not valid evidence.
- Correct resource-area discovery showed anonymous dedup nodes/chunks and Pipelines signedContent redirect to sign-in; no anonymous full enumeration was proved. Exact paths use direct HTTP; globs use complete catalogs or bounded private seekable ZIP spooling with selected-member-only retention. Keep independent 2 GiB retained/source totals, hash/CRC/path/entry/spool guards and separate transfer provenance from persistent catalog/member SHA. Binlogs remain download-only.
- Confirmed 117 PipelineArtifacts, 68 Logs_Build artifacts and metadata payload sum 29.1455 GiB; artifactsize is not ZIP transfer length. Failed-logs opt-in default maps actual failed source Jobs, selecting zero build ZIPs for monitor-only-failed 1621192, not all 68. Broader sources/archive mode require explicit policy.
- Final exact APIs/flags/key grammars/manifest/tests/docs and unchanged timeline -> build sets -> bytes phasing are persisted in session files/audit-p0-design.md and .squad/decisions/inbox/dallas-audit-p0.md. No production source/tests/release docs, commits or PRs; all proofs are session-local and no subagents were spawned.

### 2026-10-02T22:04:26-05:00 - Access-first P0 override

- Applied Larry's overriding directive from copilot-directive-20261003T0235Z.md across the entire design and decision inbox. Supersedes earlier hard MCP ceilings, typed 512/50 MiB gates, implicit-scope restrictions and unconditional quota refusal. Defaults optimize presentation/acquisition; every supported explicit request remains accessible through hlx with exact next steps.
- Retained triage ordering/previews and 12/16 KiB inline shaping targets, but over-target records/issues/full sets now use verified private disk/evidence references or advancing chunks, not domain errors. Added shared scoped hlx_read_evidence design, explicit file/CLI output, exact full actions on previews and no selector-specific timeline snapshot keys. Exact selectors bypass an omitted failure preset; deliberate filters still apply.
- Explicit large artifacts/members/dumps/archive/non-Logs/successful-leg requests stream with progress without classification or byte unlocking. Unattended thresholds have one --allow-large checkpoint/resume route; actual headroom/retention leases, authentication, integrity and unsafe-path defenses remain. Full acquired mutable observations can be captured with provenance rather than refused until terminal.
- Anonymous native-member and ZIP Range probes returned HTTP 400 "Content-Range is not accepted as a part of the request."; ordinary native control still returns 200. Design preserves completed members and per-file restart where upstream resume is unavailable, validates 206/validators where supported, and provides exact local cache/delivery ranges.
- Inspected concrete existing anti-patterns and added prioritized follow-ups: #157 all-results >10000 skip; attachment 1000/all explicit-max guard; CreateLimitedResults vague higher-top hints; AzDO/Helix search continuation; evidence-plan 200/10/1000 caps; Helix >50 batch rejection; uploaded-file >50 MiB search error/ReadAllLines; page-count ceiling and ordinary cache target; dead-end no-match/binary/snapshot errors. Recommend explicit --test-scope all streams with progress, while diagnostic attachment defaults avoid accidental million-request fan-out.
- Final design and inbox now define exact delivery/recovery fields, paged query cache/checkpoints, one-flag continuation and source-level acceptance matrix. Phasing remains timeline -> build sets -> bytes, with collector-coupled follow-ups called out. No src implementation/tests/release docs, commits/PRs or subagent spawns.
