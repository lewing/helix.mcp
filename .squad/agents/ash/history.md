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
