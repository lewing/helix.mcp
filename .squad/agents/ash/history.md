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
