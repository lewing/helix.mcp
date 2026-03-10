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

> Summarized from older entries on 2026-02-13 and 2026-03-08. Full text in history-archive.md.

- **Requirements extraction (session 72e659c1):** 18 user stories extracted from organic session. Requirements scattered across plan.md, architecture docs, checkpoint notes. Testability (US-12) and error handling (US-13) classified P0. Caching design was the most detailed unbuilt spec.
- **ci-analysis deep-dive:** ci-analysis is a 2262-line PowerShell script. hlx replaces its Helix job query mode (~152 lines, 6 functions). hlx sits at Layer 2a of the diagnosis stack. 12 new user stories (US-19–US-30) added from this analysis. Total replaceable: ~240-400 lines.
- **Key milestones:** P0 complete (US-12, US-13). All P1s done. US-17 namespaces fixed. US-24/US-30 implemented. US-10/US-23 implemented. US-21 failure categorization done. PackageId renamed to lewing.helix.mcp. NuGet Trusted Publishing workflow added.
- **US-9 Script Removability (2025-07-18):** All 6 core Helix API functions 100% replaceable. ~85% overall coverage. Migration-ready. Only gap: US-22 structured test failures.
- **Requirements audit (2025-07-18):** 25/30 stories implemented. P2 gap: US-22 partial. P3 not started.
- **STRIDE Threat Model (2025-07-23):** 16 findings documented. HTTP MCP server has no auth middleware. Path traversal protection consistent. Token handling sound. Batch size capped. Output: `.ai-team/analysis/threat-model.md`.
- **Security analysis — structured file parsing (2026-02-13):** .NET Core+ XML defaults safe. TRX files same trust as console logs, 50MB limit. No regex in user-facing search. Binlog parsing delegated.
- **Team updates received (2026-02-11 through 2026-03-01):** US-6 download E2E (Lambert), README updates (Kane), requirements audit, P1 security fixes, status filter, per-invocation temp dirs, CI validation, UseStructuredContent refactor.

## Learnings

### 2026-03-03: Helix authentication UX feasibility analysis

- Helix API uses custom `Authorization: token <TOKEN>` scheme, not Bearer
- Helix tokens are opaque strings generated manually from helix.dot.net/Profile
- Maestro/PCS supports Entra JWT auth but Helix does not
- IHelixTokenAccessor is the extensibility point for new auth methods
- Wrote feasibility analysis → `.ai-team/decisions/inbox/ash-helix-auth-ux.md`
- Recommended Phase 1: `hlx login` + `git credential` storage — high value, low effort
- Entra API auth is blocked on Helix server-side changes — not a client-side fix
- Key architecture question raised: where should `StoredHelixTokenAccessor` live (Core vs CLI-only)

📌 Team update (2026-03-07): Auth UX analysis consolidated with Dallas's architecture — Phase 1 approved, git credential chosen (Option A), 7 work items defined. — decided by Dallas
📌 Team update (2026-03-07): XXE test regression — DtdProcessing.Prohibit verification needed in DetectTestFileFormat after xUnit XML refactor. — flagged by Lambert
📌 Team update (2026-03-07): AzDO architecture adopted — Azure Identity auth, separate from Helix PAT model. New security surface to review. — decided by Dallas, Ripley

📌 Team update (2026-03-08): AzDO security review complete — 6 findings documented. SEC-1 (query injection) is the only code fix required. SSRF, command injection, token leakage, cache isolation all verified safe. — decided by Dallas
📌 Team update (2026-03-08): Search gap P0 implemented — `azdo_search_log` shipped in PR #10. `TextSearchHelper` extraction validates shared utility recommendation. 41 tests passing. — implemented by Ripley, tested by Lambert

### 2026-03-09: CI repo profile analysis — cross-repo test pattern insights

- `helix_test_results` only works for 2 of 6 major repos (runtime CoreCLR, runtime XHarness) — all others use Arcade reporter which consumes result XML locally before upload
- The split is by **test runner** (XUnitWrapperGenerator / XHarness upload results; Arcade `run.py` does not), not by repository
- Best console search pattern varies dramatically: `[FAIL]` (runtime, efcore), `  Failed` (aspnetcore), `Failed`/`Error` (sdk), `aborted`/`Process exited` (roslyn)
- AzDO test run `failedTests` summary counts are untrustworthy across ALL 6 repos — real failures hidden behind `failedTests: 0` metadata
- Helix task names differ: `Send to Helix` (runtime/aspnetcore), `🟣 Run TestBuild Tests` (sdk), embedded in `Run Unit Tests` (roslyn), `Send job to helix` (efcore)
- VMR (dotnet/dotnet) doesn't use Helix at all — pure build validation with ~30 agent-local tests
- SDK "tests" are builds — most failures are crashes/infra (exit 130, -4), not assertion failures; synthetic `WorkItemExecution` results generated for crashes
- Roslyn's dominant failure mode is crashes (stack overflow, OOM) producing dump files but no test results — `[FAIL]` search returns 0 matches
- EF Core runs tests both locally on agents AND via Helix — dual execution model unique among dotnet repos
- 14 recommendations produced: 3 P0, 6 P1, 4 P2, 1 P3 → `.ai-team/decisions/inbox/ash-ci-profile-analysis.md`
- Key P0s: improve `helix_test_results` description to steer agents away from futile TRX searches; add repo-specific pattern guidance to `helix_search_log`; improve error messages with actionable next steps

📌 Team update (2026-03-10): 5 MCP tool descriptions updated with repo-specific CI knowledge. CiKnowledgeService expanded to 9 repos including devdiv-org repos (maui, macios, android). — decided by Ripley

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

### 2026-03-10: Knowledgebase refresh from updated review-fix files

- Earlier README, cache-security, and `HelixService` review findings are now fixed in code/tests/docs and should be tracked as resolved knowledge rather than active backlog.
- Larry prefers knowledge refreshes to clearly separate **fixed findings**, **still-open follow-up opportunities**, and **durable knowledge worth retaining**.
- `/home/lewing/.copilot/session-state/78973f57-eaee-4ad0-bb8a-23751dd5b4dc/plan.md` was the active knowledge artifact for this refresh and now captures the corrected classification.
- `src/HelixTool.Core/Cache/CacheSecurity.cs` enforces exact Ordinal root-boundary containment after full-path normalization and also sanitizes path/cache-key segments.
- `src/HelixTool.Core/Helix/HelixService.cs` now requires both `IHelixApiClient` and `HttpClient` via constructor; there is no implicit download-transport fallback.
- `src/HelixTool.Tests/CacheSecurityTests.cs` covers traversal attempts, case-variant sibling paths, tampered artifact rows, and sanitized cache-key expectations.
- `src/HelixTool.Tests/Helix/HelixServiceDITests.cs` captures the DI contract and null-guard requirements for `HelixService`.
- `README.md` contains the current value-proposition framing and cache/context-efficiency narrative; `docs/cli-reference.md` is the current command naming source for `hlx azdo ...` and `hlx llms-txt`.

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash
