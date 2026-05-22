# Ash — History (Summarized)

## Current Session (2026-05-22)

### MCP Tool Description Audit
- **Scope:** 25 MCP tools across 3 assemblies (HelixTool.Core, HelixTool.Mcp.Tools, HelixTool)
- **Status:** Complete
- **Finding:** 8 tools flagged for description tightening (~69 words recoverable)

**Flagged tools (by pattern):**
1. **Situational bloat** (3): azdo_timeline, azdo_helix_jobs, helix_status — filter enums duplicated in tool + param descriptions
2. **Schema dump** (2): helix_status, helix_files — lead with schema details instead of action verb
3. **Domain knowledge** (2): azdo_test_results, helix_ci_guide — repo-specific context belongs in knowledge tool
4. **Parameter detail** (1): azdo_builds — defaults belong in param description

**Top offenders:**
- `azdo_timeline` (44 words) → tighten to ~20 words, move filter list to parameter
- `azdo_helix_jobs` (31 words) → same pattern
- `azdo_builds` (30 words) → defer defaults to parameters
- `helix_ci_guide` (26 words) → move repo list to knowledge doc

**Effort:** Ripley should tighten ~1–2 hours (8 tools × ~10 min per tool + validation)

**Next:** Ripley now executing on branch `feat/mcp-description-tightening`

---

## Prior Work (2026-02-13 through 2026-05-21)

### Key Accomplishments
- **Requirements extraction:** 30 user stories from organic investigation (18 from session 72e659c1, 12 from ci-analysis deep-dive)
- **P0 complete:** Testability (US-12), error handling (US-13)
- **Security:** STRIDE threat model, AzDO security review (6 findings, 1 code fix: query injection)
- **Architecture:** Layered CI diagnosis stack documented. hlx replaceable in 85% of ci-analysis workflows
- **Auth UX:** Helix auth feasibility analysis (Phase 1: git credential storage approved)
- **Audits:** XXE DtdProcessing verification, cross-repo test patterns, AzDO test run metadata reliability

### Historical Context
> See `history-archive.md` for detailed notes on:
> - CI repo profile analysis (cross-repo test pattern variance)
> - Helix authentication UX design (2026-03-03)
> - AzDO security review (2026-03-08)
> - MCP exception audit findings (2026-05-21)

Full text preserved in archive. Current history focuses on active/recent work.
