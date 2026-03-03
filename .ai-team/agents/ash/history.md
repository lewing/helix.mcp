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

> Summarized 2026-03-03. Full details in history-archive.md.

- All 6 core Helix API functions are 100% replaceable by hlx (~152 lines). Overall Helix coverage: ~85%.
- hlx is migration-ready. Phase 1 yields ~120 net line reduction; Phase 2 adds ~73 more via hlx_search_log.
- Only meaningful gap: US-22 structured test failure extraction (P2).

📌 Team update (2026-02-13): US-6 download E2E verification complete — 46 tests covering DownloadFilesAsync/DownloadFromUrlAsync, all 298 tests pass — decided by Lambert

📌 Team update (2026-02-15): README now documents ci-analysis replacement coverage in Architecture section — caching, HTTP multi-auth, project structure all documented — decided by Kane

### 2025-07-18: Requirements audit — comprehensive P0/P1/P2 completion status

> Summarized 2026-03-03. Full details in history-archive.md.

- 25/30 user stories verified as ✅ Implemented. P0 all done, P1 all 13 done, P2 8/9 done (US-22 partial), P3 none started.
- 7 acceptance criteria left unchecked despite features existing (US-1, US-4, US-11, US-17×2, US-21, US-22).

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash

### 2025-07-23: STRIDE Threat Model

> Summarized 2026-03-03. Full details in history-archive.md.

- HTTP MCP server has no auth middleware (high severity for network). SSRF vector in `DownloadFromUrlAsync`. Path traversal protection thorough. No SQL injection or ReDoS. Token handling sound.
- Output: `.ai-team/analysis/threat-model.md` — 16 findings.

📌 Team update (2026-02-13): P1 security fixes E1+D1 implemented (URL scheme validation, batch size cap) — decided by Ripley
📌 Team update (2026-02-13): Security validation test strategy (18 tests) — decided by Lambert

### 2026-02-13: Security analysis — structured file parsing

> Summarized 2026-03-03. Full details in history-archive.md.

- .NET Core+ XML defaults safe; set explicitly for defense-in-depth. 50 MB file size limit for XML DOM parsing.
- Text search uses simple string matching (no regex). Binlog parsing delegated to external tool.
- TRX files same trust chain as console logs — no new disclosure surface.


📌 Team update (2026-02-13): Status filter changed from bool to enum (failed|passed|all) — decided by Larry/Ripley

📌 Team update (2026-02-15): DownloadFilesAsync temp dirs now per-invocation (helix-{id}-{Guid}) to prevent cross-process races — decided by Ripley
📌 Team update (2026-02-15): CI version validation added to publish workflow — tag is source of truth for package version — decided by Ripley
📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas

### 2026-03-03: Helix authentication UX feasibility analysis

- Helix API uses custom `Authorization: token <TOKEN>` scheme, not Bearer
- Helix tokens are opaque strings generated manually from helix.dot.net/Profile
- Maestro/PCS supports Entra JWT auth but Helix does not
- IHelixTokenAccessor is the extensibility point for new auth methods
- Wrote feasibility analysis → `.ai-team/decisions/inbox/ash-helix-auth-ux.md`
- Recommended Phase 1: `hlx login` + `git credential` storage — high value, low effort
- Entra API auth is blocked on Helix server-side changes — not a client-side fix
- Key architecture question raised: where should `StoredHelixTokenAccessor` live (Core vs CLI-only)

📌 Team update (2026-03-03): HelixTool.Core published as standalone NuGet (lewing.helix.core) — MCP tools extracted to HelixTool.Mcp.Tools, 11 models extracted, version centralized. All 9 work items complete, 373 tests pass. — decided by Dallas, executed by Ripley
📌 Team update (2026-03-03): Phase 1 auth UX approved by Dallas — `hlx login`/`logout`/`auth status`, `git credential` storage, `ChainedHelixTokenAccessor`. 7 work items created for Ripley. Your feasibility analysis was the foundation. — decided by Dallas

### 2026-03-03: HelixTool.Core consumer experience review

**Key findings:**
- 1 blocker: README example uses `summary.FailedItems`/`PassedItems` but actual record properties are `Failed`/`Passed` — example won't compile
- 7 friction items: CLI-specific error messages in library exceptions ("Run 'hlx login'"), missing `nuget.config` guidance for library consumers, `FailureCategory` enum in wrong file, unclear `IHelixTokenAccessor` relationship, undocumented temp file cleanup, no options pattern on `HelixApiClient`, inconsistent exception types for file search disabled
- 7 opportunities: `AddHelixClient()` DI extension, `GetJobInfoAsync` metadata-only method, `HttpStatusCode` on `HelixException`, error handling example in README, model type documentation, `IAsyncDisposable` download wrapper
- API surface is well-designed: 13 methods cover all 5 core consumer scenarios, record types are excellent, XML docs are thorough, error wrapping is consistent
- Auth story is good for typical use (public jobs need no token, env var for CI, constructor param for programmatic)
- Output: `.ai-team/decisions/inbox/ash-consumer-review.md`

📌 Team update (2026-03-03): API review findings — decided by Dallas, Ash
