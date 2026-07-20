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
