# Ash — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Architecture context:** hlx is part of a layered CI diagnosis stack: azp (AzDO pipeline discovery) → hlx (Helix test investigation) → binlog MCP (structured diagnosis) → thin script (PR correlation, known issues, JSON summary)
- **Origin session:** 72e659c1 — contains the architecture proposal, API discovery notes, and migration path from monolithic ci-analysis script to layered tool approach
- **Key user workflow:** User investigates CI failures by: discovering failed builds (azp) → finding Helix jobs → checking work item status → downloading logs/binlogs → structured diagnosis

## Learnings

### 2025-07-18: Requirements extraction from session 72e659c1

**Requirements patterns discovered:**
- The session evolved organically from architecture exploration → tool building → integration planning. Requirements were scattered across plan.md TODOs, architecture doc open questions, checkpoint next-steps, and implicit workflow observations. Formalizing them required reading everything — no single source had the full picture.
- Dallas's architecture review (in decisions.md) surfaced 12+ code quality issues. These are requirements too — they represent the gap between "it works" and "it's maintainable." I classified testability (US-12) and error handling (US-13) as P0 because Dallas is right: you can't safely add features without tests.
- The caching design (Dallas's two decisions) is the most thoroughly spec'd feature that hasn't been built yet. It has a full TTL matrix, eviction policy, and edge case analysis. Unusual for a backlog item to arrive this detailed — it reflects real pain from repeated API calls during debugging sessions.

**User workflow insights:**
- Larry's investigation pattern is a strict pipeline: AzDO build → Helix job → work items → logs/binlogs → structured diagnosis. Each step feeds into the next. The layered architecture (azp → hlx → binlog MCP → thin script) maps directly onto this pipeline.
- The "logs stay out of context" principle is a hard requirement, not a preference. LLM agents can't function if you dump a 1.7MB console log into their context window. CLI saves to files, MCP returns tail-limited content. Both are correct but for different consumers.
- Positional args (US-1) seems minor but it's a workflow friction issue. When you're iterating through jobs, typing `--job-id` every time adds cognitive load. The user called this out explicitly.

**Gap analysis findings:**
- 18 user stories extracted total. 6 features are built (with 8 known gaps in those implementations). 12 features are unbuilt.
- The biggest structural gap is testability — zero tests exist, zero DI, zero mocking capability. This blocks safe iteration on everything else.
- The second biggest gap is error handling — the tool produces raw .NET stack traces on any API error. For a CLI tool used during stressful CI debugging, this is unacceptable UX.
- Script removability analysis (US-9) is important strategically — it quantifies the ROI of hlx. ~300+ lines of PowerShell become redundant if hlx works, but nobody has done the precise mapping yet.

### 2025-07-18: ci-analysis skill deep-dive (primary consumer analysis)

**Source materials:** SKILL.md, Get-CIStatus.ps1 (2262 lines), 7 reference documents (helix-artifacts.md, delegation-patterns.md, manual-investigation.md, build-progression-analysis.md, binlog-comparison.md, azdo-helix-reference.md, azure-cli.md).

📌 Team update (2026-02-11): P0 Foundation design decisions D1–D10 merged — IHelixApiClient, constructor injection, HelixException, CancellationToken, input validation. Ripley and Lambert assigned implementation and testing. — decided by Dallas

📌 Session 2026-02-11-p0-implementation: US-12 (DI/testability) and US-13 (error handling) are DONE. 38 tests pass. P0 complete — P1 work can proceed.

📌 Session 2026-02-11-p1-features: US-1 (positional args) and US-20 (rich status output) implemented by Ripley. Kane completed docs fixes (llmstxt, README, XML doc comments). 38/38 tests pass. US-1 and US-20 can be marked DONE in requirements.md.

📌 Team update (2026-02-11): US-17 namespace cleanup complete — `HelixTool.Core` and `HelixTool.Mcp` namespaces now distinct. New files must use correct namespace. — decided by Ripley
📌 Team update (2026-02-11): US-24 (download by URL) and US-30 (structured agent-friendly JSON) implemented. `hlx_files` grouped output is a breaking change. Tests: 74. — decided by Ripley

**How hlx fits into ci-analysis's workflow:**
- ci-analysis is a 2262-line PowerShell script that orchestrates CI failure investigation across AzDO and Helix. It has THREE modes: PR analysis, Build ID analysis, and direct Helix job query.
- The Helix job query mode (lines 1580-1713) is essentially what hlx replaces. Six functions (`Get-HelixJobDetails`, `Get-HelixWorkItems`, `Get-HelixWorkItemFiles`, `Get-HelixWorkItemDetails`, `Get-HelixConsoleLog`, `Find-WorkItemsWithBinlogs`) map directly to hlx commands.
- hlx is invoked at Layer 2a of the diagnosis stack. ci-analysis would call hlx MCP tools from subagents doing parallel work item investigation.

**Key integration insights:**
1. **hlx already handles the ListFiles bug correctly** — ci-analysis has an explicit workaround for dnceng#6072 (broken URIs for subdirectory/unicode files). hlx uses `ListFilesAsync` which avoids this. This is a selling point for adoption but needs documentation (US-28).
2. **Status output is too thin** — hlx returns pass/fail counts, but ci-analysis needs per-work-item state, exit code, duration, machine name, and console log URLs. The Details endpoint is already called; the data just isn't surfaced (US-20, US-25).
3. **Job metadata is missing** — ci-analysis shows QueueId and Source for context. hlx doesn't expose these (US-19).
4. **Failure categorization is a pain point** — ci-analysis spends ~10 lines classifying failures by exit code and log patterns (crash vs timeout vs test-failure vs infrastructure). This is reusable logic that belongs in hlx (US-21).
5. **Delegation patterns drive MCP design** — ci-analysis's subagent patterns (references/delegation-patterns.md) show agents launching parallel tasks that each call hlx MCP tools. Key implication: MCP tools should return self-contained JSON (no context assumptions) and accept flexible input (URLs or component params).

**Script removability update (US-9):**
After reading the full script, I can now quantify what hlx replaces more precisely:
- Lines 1301-1453 (Helix API functions): ~152 lines → fully replaced by hlx
- Lines 1459-1547 (Format-TestFailure, log parsing): ~88 lines → replaced by hlx_test_failures (US-22) once built
- Lines 759-907 (Extract-HelixUrls, Extract-HelixLogUrls): ~148 lines → partially replaced; URL extraction is AzDO log parsing, not Helix
- Total replaceable: ~240-400 lines depending on how much log parsing hlx absorbs

**What stays in ci-analysis (NOT hlx's responsibility):**
- PR discovery via `gh pr checks` (lines 341-417)
- AzDO timeline parsing (lines 628-720)
- Build Analysis known issues (lines 419-492)
- PR change correlation (lines 494-626)
- Known issue search via GitHub + MihuBot (lines 913-1186)
- `[CI_ANALYSIS_SUMMARY]` JSON construction (lines 2196-2254) — this is the agent-facing output format
- Error categorization at the AzDO level (build errors vs Helix test failures)

**12 new user stories added (US-19 through US-30):**
- 6 at P1 (US-19, US-20, US-24, US-25, US-28, US-29, US-30) — these are blocking ci-analysis adoption of hlx
- 3 at P2 (US-21, US-22, US-23) — improve the agent experience but not blockers
- 2 at P3 (US-26, US-27) — nice-to-have utilities

**Priority re-evaluation:**
- US-9 (script removability) is now partially done — the mapping above IS the removability analysis. Remaining work: formalize the function→command mapping into a table.
- US-20 (per-work-item detail in status) should be P1 not P2 — it's the single biggest gap preventing ci-analysis from using hlx. ci-analysis fetches details for every work item individually; hlx should do this in its status command.
- US-30 (structured MCP output) is P1 — agents can't effectively use hlx if the MCP responses are just raw data.


📌 Team update (2026-02-11): US-10 (GetWorkItemDetailAsync) and US-23 (GetBatchStatusAsync) implemented — new CLI commands work-item and batch-status, MCP tools hlx_work_item and hlx_batch_status added. — decided by Ripley



📌 Team update (2026-02-11): US-21 failure categorization implemented — FailureCategory enum + ClassifyFailure heuristic classifier added to HelixService. WorkItemResult/WorkItemDetail records expanded. — decided by Ripley


📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry


📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — publish via git tag v*

