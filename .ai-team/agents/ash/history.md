# Ash — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Architecture context:** hlx is part of a layered CI diagnosis stack: azp (AzDO pipeline discovery) → hlx (Helix test investigation) → binlog MCP (structured diagnosis) → thin script (PR correlation, known issues, JSON summary)
- **Origin session:** 72e659c1 — contains the architecture proposal, API discovery notes, and migration path from monolithic ci-analysis script to layered tool approach
- **Key user workflow:** User investigates CI failures by: discovering failed builds (azp) → finding Helix jobs → checking work item status → downloading logs/binlogs → structured diagnosis

## Core Context

> Summarized from older entries on 2026-02-13. Full text in history-archive.md.

- **Requirements extraction (session 72e659c1):** 18 user stories extracted from organic session. Requirements scattered across plan.md, architecture docs, checkpoint notes. Testability (US-12) and error handling (US-13) classified P0. Caching design was the most detailed unbuilt spec.
- **ci-analysis deep-dive:** ci-analysis is a 2262-line PowerShell script. hlx replaces its Helix job query mode (~152 lines, 6 functions). hlx sits at Layer 2a of the diagnosis stack. 12 new user stories (US-19–US-30) added from this analysis. Total replaceable: ~240-400 lines.
- **Key milestones:** P0 complete (US-12, US-13). All P1s done. US-17 namespaces fixed. US-24/US-30 implemented. US-10/US-23 implemented. US-21 failure categorization done. PackageId renamed to lewing.helix.mcp. NuGet Trusted Publishing workflow added.

## Learnings

### 2025-07-18: US-9 Script Removability Analysis Complete

**Key findings:**
- All 6 core Helix API functions in ci-analysis (Get-HelixJobDetails, Get-HelixWorkItems, Get-HelixWorkItemFiles, Get-HelixWorkItemDetails, Get-HelixConsoleLog, Find-WorkItemsWithBinlogs) are 100% replaceable by hlx today. ~152 lines of PowerShell can be deleted.
- Extended Helix-adjacent code (log parsing, URL construction, categorization) is ~71% covered (~217/305 lines). The only meaningful gap is structured test failure extraction (US-22, P2).
- Overall Helix-related coverage: ~85%. 10 of 11 functions fully replaced.
- No user stories need promotion to unblock Phase 1 migration. hlx is migration-ready NOW.
- Phase 1 migration (core API replacement) yields ~120 net line reduction with zero blockers.
- Phase 2 migration (log parsing) yields additional ~73 line reduction using hlx_search_log as workaround for US-22.
- Total potential reduction: ~195 lines from ci-analysis.

**Coverage gaps identified:**
- G1: Structured test failure extraction (US-22, P2) — `Format-TestFailure` parses xUnit/NUnit/MSTest output into structured JSON. hlx_search_log provides generic search but not structured parsing. ~88 lines partially covered (~40%).
- G2: Job ID extraction from AzDO logs (US-26, P3) — cross-layer bridge, stays in ci-analysis. Not hlx's responsibility.
- G3: Env var extraction (US-27, P3) — convenience feature, workaround exists via hlx_search_log.
- G4: TRX parsing (US-14, P3) — separate concern, not a migration blocker.
- G5: Flaky test correlation (US-16, P3) — hlx_batch_status provides adequate workaround.

**Migration priorities:**
1. Phase 1 (NOW): Replace 6 core Helix API functions → ~120 line net reduction, zero blockers
2. Phase 2 (NEXT): Replace Format-TestFailure with hlx_search_log → ~73 additional line reduction
3. Phase 3 (LATER): Promote US-22 to P1 only if ci-analysis agents consistently need structured failure JSON


📌 Team update (2026-02-13): US-6 download E2E verification complete — 46 tests covering DownloadFilesAsync/DownloadFromUrlAsync, all 298 tests pass — decided by Lambert

📌 Team update (2026-02-15): README now documents ci-analysis replacement coverage in Architecture section — caching, HTTP multi-auth, project structure all documented — decided by Kane

### 2025-07-18: Requirements audit — comprehensive P0/P1/P2 completion status

**Verified and updated in requirements.md:**
- Marked 25 of 30 user stories as ✅ Implemented after verifying against actual source code
- P0: US-8 (logs out of context), US-12 (DI), US-13 (error handling) — all done. US-7 (layered architecture) is partial (only hlx layer built; other layers are external projects).
- P1: All 13 stories done — US-1, US-2, US-5, US-6, US-9, US-17, US-19, US-20, US-24, US-25, US-28, US-29, US-30.
- P2: 8 of 9 done — US-3, US-4, US-10, US-11, US-15, US-18, US-21, US-23. US-22 is partially implemented (generic `search-log` exists, but structured `hlx_test_failures` is not built).
- P3: None started — US-14, US-16, US-26, US-27.
- Replaced the "Implementation Gaps" section with "Resolved Gaps" (8 original gaps all fixed) and a shorter "Remaining Implementation Gaps" section (5 minor items).
- Updated feature table to reflect 15 capabilities across CLI/MCP.

**Acceptance criteria NOT met despite feature existing:**
- US-1: `--job-id` backwards compat criterion left unchecked — ConsoleAppFramework `[Argument]` replaced named flags entirely.
- US-4: `azd auth login` consistency criterion left unchecked — hlx uses env var / HTTP header, not azd-based auth.
- US-11: `--json` on all structured commands left unchecked — only `status`, `files`, `work-item` have it; `find-binlogs` and `batch-status` do not.
- US-17: Models extraction to `Models/` folder left unchecked — records are still nested in HelixService.
- US-17: Display/ folder cleanup left unchecked — status unknown.
- US-21: Log-based categorization criteria left unchecked — uses exit code + state heuristics, not log content parsing.
- US-22: All 5 original criteria unchecked — the structured `hlx_test_failures` tool was not built. Instead, a generic `hlx_search_log` was implemented, which covers the use case differently.

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash