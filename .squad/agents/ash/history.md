# Ash — History (Summarized)

## Current Work (2026-05-28 through 2026-06-01)

### 2026-05-28: Silent MCP Failures Investigation — Issue #67

- Analyzed parameter binding failures in session b11893eb
- Root cause: Microsoft.Extensions.AI.AIFunctionFactory parameter marshalling failures before tool method invocation
- Generated investigation document; fed into Dallas's CallToolFilters middleware policy decision (resolved in PR #69)

### 2026-06-01: MCP Schema Measurement & Token Cost Analysis

**Status**: Complete. Issue filed: https://github.com/lewing/helix.mcp/issues/74

**Measurements** (25 tools):
- **Total schema**: 16,212 bytes (15.83 KB per `tools/list`)
- **AzDO tools (14)**: 8.84 KB
- **Helix tools (11)**: 6.99 KB
- **Knowledge tools (1)**: 0.39 KB

**Top 5 schema cost drivers**:
1. helix_search (1,113 bytes; 6 params + descriptions)
2. azdo_search_log (1,089 bytes; 7 params + descriptions)
3. azdo_builds (1,051 bytes; 7 params + descriptions)
4. helix_download (923 bytes; 5 params + descriptions)
5. azdo_search_timeline (917 bytes; 4 params + descriptions)

**Key finding**: Parameter count and descriptions dominate cost (7-param tools = 560–640 bytes). Conservative trim potential: −1 KB (descriptions only). Aggressive: −3 KB (with API risk).

**Decision pending**: Measure real schema cost in live workflows vs. proceed with conservative trim for v0.7.8.

**Input document**: .squad/decisions/ash-mcp-schema-measurement-2026-06-01.md

---

## Prior Work Summary (2026-02-13 through 2026-05-27)

### Key Audits & Investigations
- **Issue #59 Phase 1 (2026-05-22):** outputSchema + inputSchema deep-dive on top-10 tools. Identified 4 optimization levers (filter enum consolidation, redundant fields, nested structure depth, LimitedResults<T> maturity). Est. recovery: −550–950 tokens (6.7–11.6% of total MCP cost).

- **Slop Audit (2026-05-22):** 28,813 LOC analysis. Found 3 HIGH-severity items (result DTO duplication, catch-throw boilerplate, JSON attribute inconsistency), 1 MEDIUM (Program.cs size), 1 LOW (unused imports). Codebase health: B+. Duplication rate: 0.3%.

- **MCP Tool Description Audit (2026-05-22):** 8 tools flagged for tightening (~69 words recoverable). Ripley executed tightening (136 words recovered, PR #57 merged).

- **Issue #61 (2026-05-25):** Silent MCP failures investigation. Discovered parameter naming inconsistency (buildId vs buildIdOrUrl) and uncaught exception gaps. Ripley fixed both in PR #62 + #64. Calibration learning: Name exceptions by exercising 10-line repro, not source-read.

- **AzDO Security Review (2026-03-08):** STRIDE threat model, 6 findings (1 code fix: query injection), XXE DtdProcessing verification.

- **Requirements Extraction (2026-02-13):** 30 user stories. P0 complete (testability US-12, error handling US-13).

See `history-archive.md` for full details on older work.

---

## Standing Practices

1. **Measurement-first audits** (use gpt-4o tokenizer, not word-count). Prevents regressions like PR #56 growth canceling PR #57 savings.
2. **Field-level breakdown** (outputSchema %, inputSchema %, annotations %) enables early detection of schema drift.
3. **Exception investigation exercise** — reproduce failures in 10-line code before naming exception types.
4. **Concurrent task patterns** — always wrap Task.WhenAll with explicit exception handling (AggregateException vs. TaskCanceledException behavior differs by await vs. .Wait()).
