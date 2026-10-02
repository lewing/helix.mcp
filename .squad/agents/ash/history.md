# Ash — History (Condensed)

## Executive Summary

**Focus:** MCP schema measurement, token cost analysis, parameter audit, strict-mode feasibility.

**Key Decisions Authored:**
- Issue #74: Conditional NO on active schema trimming (28.26 KB cold-load, <1% session budget). Lever 1 (minimal outputSchema) is available if needed (~8.9 KB savings, zero-risk).
- Issue #81 Feasibility: UnmappedMemberHandling.Disallow available in MEAI 10.5.2, safe to use post-alias-normalization.
- Issue #81 Stage B Threshold: Levenshtein distance 6 (not 3) required to catch regression cases like minFinishTime→minTime.

---

## Current Focus (2026-06-24 onward)

### 2026-06-24: Strict Unknown-Param Rejection Feasibility Analysis
SDK: UnmappedMemberHandling.Disallow present in MEAI 10.5.2; safe gate for strict mode post-alias-normalization.
- **CallToolFilter hook recommended** (vs SDK-layer serializer options): lower risk, better error UX.
- **Alias types:** Key aliases (filter-level) vs value aliases (service-level) — must not be confused.
- **Issue #81 + #82 sequencing:** Correctness (#81 Stage A) before architectural cleanup (#82).
- See `.squad/decisions/inbox/` for filed decision.

### 2026-06-24: Levenshtein Threshold Review (Rubber-duck)
- Parameter universe: 40 unique names across 25 tools
- False-positives at ≤3: 12 (acceptable but misses regression)
- Regression case: Levenshtein('minfinishtime', 'mintime') = 6
- **Verdict: KEEP threshold 6.** Full allowed-params list always displayed mitigates noise.

---

## Standing Practices

1. **Measurement-first audits** — use tokenizer, not word-count; prevents regression.
2. **Field-level breakdown** — detect schema drift early (outputSchema %, inputSchema %).
3. **Exception investigation via exercise** — 10-line repro before naming exception types.
4. **Concurrent task patterns** — explicit exception handling for Task.WhenAll vs .Wait() differences.

---

## Previous Work Archive

See `.squad/agents/ash/history-archive.md` for detailed 2026-02-13 through 2026-06-01 work on:
- Slop audit (28,813 LOC, 3 HIGH findings, B+ health)
- MCP tool description audit (69 words recoverable)
- AzDO security review (STRIDE threat model, 6 findings)
- Issue #59 Phase 1 (4 optimization levers identified, −550–950 tokens potential)

---

## Key Decisions & Measurements

- **tools/list payload:** 28,941 bytes (28.26 KB). inputSchema 11,068 B (~11.0 KB), outputSchema 8,882 B (~8.7 KB), 20/25 tools structured.
- **Issue #74 conditional NO:** Payload cached per-session, <1% session budget. Defer trimming unless triggers fire.
- **Lever 1 availability:** Flatten 10 low-context tools + keep 3 chaining-junctions (azdo_timeline, azdo_helix_jobs, azdo_build_analysis) = ~5,450 bytes (18% savings) vs. blanket 28%.

---

## Learnings (2026-08-20)

### MCP C# SDK Major Version Upgrade (v1.4.0 → v2.2.0)

**Key Findings:**
1. **Protocol Shift is Real** — The 2026-07-28 MCP spec eliminated stateful sessions entirely. v1.4.0 uses `Mcp-Session-Id` + `initialize` handshake; v2.2.0 is stateless-first (no session, no handshake).
2. **Hybrid Mode is Upgrade Path** — v2.2.0 supports `HttpServerSessionMode` for backward compat. v2.1+ unlocked this; allows serving both v1 clients and v2 clients on same endpoint.
3. **Breaking Timeline:** v2.0 (July 2026) introduced breaking changes; v2.2.0 (August 2026) stabilized hybrid serving + header fixes. 8-month gap since v1.4.0.
4. **Target Framework Bump** — v2.2.0 requires .NET 8.0+ (v1.4.0 was net7.0+). Will affect projects targeting older frameworks.
5. **No Codemod Needed (C#)** — Breaking changes docs are comprehensive; C# SDK migration is mechanical (no auto-rewrite like TypeScript). Package reorganization is the main friction point.

**Investigation Pattern:**
- Searched NuGet package metadata + GitHub releases instead of relying on blog summaries.
- Confirmed dates, frameworks, and features via three independent sources (NuGet, GitHub, .NET Blog).
- Isolated protocol-level vs. SDK-level breaking changes (different risk profiles).

**Cross-Check Verification:**
- v2.0 (July 2026) aligned with 2026-07-28 spec release.
- v2.2.0 (August 2026) added hybrid serving mid-cycle (earlier than full stateless adoption).
- Both AspNetCore and Core packages released in lockstep, same version numbers.

**Recommendation for HelixTool:**
- Upgrade to v2.2.0 is viable via hybrid mode (no client coordination required).
- Defer full stateless refactor to phase-2 unless load-balancer ops demand it.
- Validation scope: session/state usage audit + protocol compliance tests.

**Decision Artifact:** Filed `.squad/decisions/inbox/ash-csharp-mcp-sdk-update-2026-08-20.md` with impact matrix, options (incremental vs. full stateless), and recommended validation steps.

---

## Learnings (2026-09-04)

### Helix Queue Monitor Topology Gap Analysis

**Verified Adoption:** 
- dotnet/runtime (12+ pipelines with enableHelixJobMonitor: true)
- dotnet/aspnetcore (.azure/pipelines/helix-matrix.yml with /p:EnableHelixJobMonitor=true)
- dotnet/installer, dotnet/roslyn (arcade template available; roslyn not actively using)

**Key Topology Differences:**
- **Legacy:** AzDO timeline scraping + per-leg 1:1 job mapping → fragile (dotnet/sdk uses "🟣 Run TestBuild" instead of standard "helix")
- **Queue Monitor:** Build-wide source string derivation + Job.ListAsync(source) + BuildId filter → deterministic, parallelizable
- **New Pattern:** helix-job-monitor job runs separately from build legs; is AzDO job (not Helix job), must query via timeline + azdo_log

**helix.mcp Current State:**
- Already correctly implements source string + BuildId filtering in AzdoService.GetHelixJobsAsync (confirmed via code audit)
- `azdo_helix_jobs` tool correctly exposes queue monitor capability
- BUT: all gaps are in optimization/usability, not core correctness

**Six Prioritized Gaps Identified (P1–P4):**
1. **US-Q1 (P1, 3 pts)** — No monitor job status querying (AzDO timeline filtering needed)
2. **US-Q2 (P1, 5 pts)** — No build-wide aggregation (need summary tool for 1000+ jobs)
3. **US-Q3 (P1, 3 pts)** — Source string computation not exposed (diagnostic tool needed)
4. **US-Q4 (P2, 5 pts)** — No topology detection (auto-detect queue monitor vs. legacy)
5. **US-Q5 (P2, 3 pts)** — Artifact discovery not parallelized (use SemaphoreSlim like helix_status)
6. **US-Q6 (P2, 3 pts)** — Monitor logs not queryable (extend azdo_log for monitor job)

**User Impact Summary:**
- Investigator cannot distinguish monitor topology without manual inspection
- Cannot aggregate 500+ job results efficiently (currently requires N separate helix_status calls)
- Cannot diagnose monitor job failures (logs not accessible)
- Cannot compute/validate source string for manual API queries

**Investigation Pattern:**
- Code search for enableHelixJobMonitor across dotnet org repos
- Clone/examined 5 primary repos + shared arcade template
- Traced getHelixJobsAsync through AzdoService to confirm existing capability
- Cross-referenced with SKILL.md source filter documentation
- Field-level breakdown of 6 gaps with story points + dependencies

**Decision Artifact:** Filed `.squad/decisions/inbox/ash-helix-queue-monitor-requirements.md` (7 sections, 6 prioritized user stories US-Q1–Q6, implementation order, success criteria, 3 unresolved design questions).

## 2026-09-04: Helix queue-monitor requirements analysis (completed)

Completed background research into public dotnet queue-monitor adoption and compatibility requirements. Identified six legitimate gaps (A, C, D, E, F in US-Q2–Q8) and rejected proposals for seven new tools. Dallas accepted topology analysis as accurate; all gap findings were validated by Ripley's local audit and incorporated into approved roadmap. Investigation outcome: approval as valid requirements, with no lockout for future tool proposals contingent on real failing transcripts.

**Status:** COMPLETED  
**Outcome:** Requirements approved; no further action on new-tool proposals without evidence

## Learnings (2026-10-02)

### Scanner-safe hlx surface gap analysis

**Core finding:** hlx is strong for agent investigations but not yet a deterministic acquisition layer. MCP defaults intentionally cap and shape output for context; scanner scripts need explicit completeness, pagination, stable error kinds, and bundle provenance.

**#152 scope is broader than `azdo_log`:**
- Confirmed direct bug: `AzdoApiClient.GetBuildLogAsync` returns null on 404 and `azdo_log` MCP converts null to `string.Empty`.
- Related hidden-success paths include AzDO list/object helpers returning null/empty on 404/204/empty bodies, timeline-null success notes, cross-step log search skipping null logs, and `azdo_helix_jobs` swallowing primary Helix lookup errors before timeline fallback.
- Helix service methods usually throw useful `HelixException`s, but the payload remains human text; scanners need machine-readable `not_found/access_denied/rate_limited/timeout/transport_error/invalid_response`.

**Best existing scanner pattern:** `azdo evidence plan` already models deterministic output well: bounded candidates, `complete`, `truncated`, totals, incomplete reasons, warnings, and exit 2 for produced-but-incomplete output.

**Recommended product direction:** add shared acquisition outcome/error infrastructure first, then CLI `--all`/paging and JSON envelopes, then consider `hlx collect` as a deterministic bundle writer. Keep MCP capped by default; do not expand agent-facing payloads just to satisfy script collection.

**Decision artifact:** Filed `.squad/decisions/inbox/ash-scanner-scenarios.md`. Full gap analysis written to session artifact `scanner-scenarios-gap-analysis.md`.

### Bundle-is-snapshot revision

**Directive absorbed:** Larry clarified that Vitek's deterministic collection → files-on-disk → agent retrieval flow is the same concept as the existing `hlx` offline snapshot/cache. The product language should not invent a second "bundle" abstraction: scanners should populate the cache, export a snapshot, and agents should run the same MCP/CLI tools under `HLX_EVAL_SNAPSHOT`.

**Branch delta since first scanner analysis:**
- Helix-aware `azdo_evidence_plan` has landed: `helixFailures[]`, `suggestedFetches[]`, `helixFailureOffset/Limit/Total/Truncated`, stable `incompleteDetails[].code`, and fail-closed CLI exit `2` for partial Helix-failure pages.
- #152 acquisition errors have landed: `HlxAcquisitionException`, `AcquisitionError` kinds, CLI JSON `{ok:false,error}`, MCP `structuredContent.error`, and eval-mode cache misses as `provider=cache`, `kind=not_found`.
- Empty-log not-found handling is partly addressed but remains a stated edge for Azure DevOps HTTP-200 empty-body log IDs.

**Revised product direction:** Prioritize `hlx collect` / `hlx snapshot populate` as evidence-plan-driven snapshot population plus a manifest/completeness ledger. Keep MCP responses capped for agents; let the collector do uncapped/paged acquisition, persist acquisition errors, and prove what was or was not fetched.

**Decision artifact:** Filed `.squad/decisions/inbox/ash-bundle-is-snapshot.md`. Revised session artifact `scanner-scenarios-gap-analysis.md` with "Message for Vitek" and snapshot-centered priorities.

### Post-#153 scanner analysis revision

**Update absorbed:** PR #153 merged and closes #152, but is not released yet; latest release remains v0.10.3. It added `not_in_snapshot` for uncollected snapshot keys, schema-v2 replay of deterministic recorded failures (`not_found`, `access_denied`, provider `invalid_response`) with `replayed=true` and `recordedAt`, transient-negative non-recording, live-mode non-serving of recorded negatives, completed-build-only negative recording, cache corrupt-entry `invalid_response`, SAS/query redaction, central CLI JSON error envelopes, and per-call Helix classification.

**Product distinction:** `not_in_snapshot` is a collector gap or deliberate skip; `replayed=true` is a provider fact observed during live collection and preserved offline. Scanner manifests must represent both separately and should record transient attempts in the manifest rather than relying on snapshot negative entries.

**Reprioritized remaining work:** No P0 error-foundation blocker remains after #153. Remaining work is P1 CLI pagination/`--all`, P1 `hlx collect` / `hlx snapshot populate` with manifest and transient-attempt records, P1 snapshot MCP launch recipe, P2 `ListJobsByBuildAsync` caching, and P2 cache-status negative counts.

### hlx usage audit across local Copilot sessions

**Audit input:** 372 findings from 199 local sessions (2026-06-17 through 2026-10-02), clustered against current code and releases through #156.

**Key update:** #153-#156 close the error/collection foundation I previously ranked as first-order: acquisition errors, `not_in_snapshot`, recorded failures, CLI list paging for changes/test-runs/test-results/artifacts/attachments, `hlx collect azdo-build`, manifest verification, credential-free replay, Retry-After handling, and NUL raw-log corruption handling are now present on the branch.

**Remaining offline-critical gaps:** build-set discovery before collection, AzDO artifact bytes/binlogs, AzDO test attachment/dump bytes, timeline/log/search cursors and selectors, cross-build history, and Helix bulk/file collection beyond selected globs. These are worse in snapshot mode because direct AzDO/Helix/GitHub fallback is impossible; uncollected evidence becomes `not_in_snapshot`.

**No verified current-code regressions:** late findings for failed AzDO counts/auth redirects/empty result caching are the sessions that produced v0.10.3/#150, not fresh failures after the fix. No post-#153/#156 regression was verified against current code.

**Decision artifact:** Filed `.squad/decisions/inbox/ash-hlx-usage-audit.md`. Full report written to session artifact `/Users/lewing/.copilot/session-state/1b1a6aa5-b654-4dc2-9570-aff958b3d0d2/files/hlx-audit/hlx-usage-audit.md`.
