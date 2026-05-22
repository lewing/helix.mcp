# Ash — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
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

📌 Team update (2026-03-13): Scribe merged decision inbox items covering `dotnet` as the VMR profile key, `helix_search`/`helix_parse_uploaded_trx` naming, tighter MCP descriptions, and explicit truncation metadata (`truncated`, `LimitedResults<T>`). README/docs now also call out `ci://profiles` resources and idempotent annotations.
📌 Team update (2026-03-13): MCP-facing Helix names/descriptions should stay scope-accurate and low-context: use `helix_parse_uploaded_trx`, `helix_search`, and keep repo-specific routing in `helix_ci_guide`. — decided by Ripley

📌 Team update (2026-03-13): PR #28 merged the remaining AzDO auth quick wins — fallback Azure CLI/`az` credentials now refresh on deadline/401, cache isolation keys off stable auth-source identity instead of raw token bytes, and `hlx azdo auth-status` exposes safe auth-path metadata. — decided by Ripley

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane

### 2026-05-15: C# MCP SDK v1.0.0 → v1.3.0 upgrade feasibility analysis

- Research scope: latest releases of official C# MCP SDK (github.com/modelcontextprotocol/csharp-sdk), NuGet packages (ModelContextProtocol, ModelContextProtocol.AspNetCore)
- Current version: 1.0.0 (released 2026-02-25)
- Latest version: 1.3.0 (released 2026-05-08)
- Intermediate versions: v1.1.0 (2026-03-06), v1.2.0 (2026-03-27)
- Gap: 3 minor versions + 2 breaking changes (v1.2.0 SSE/RequestContext deprecations)

**Key findings:**
- v1.1.0: Client completion APIs, handler auto-discovery, message handler cleanup fix — low impact (we don't use these features)
- v1.2.0 breaking changes: SSE disabled by default (non-issue — we use root endpoint already); RequestContext deprecation (non-issue — we don't instantiate it directly)
- v1.3.0: Public `ClientTransportClosedException` for structured transport closure info; fixes for stateless HTTP transport; CORS/allowed-hosts documentation; process crash fix for Stderr testing
- Our usage: HTTP server transport with tool registration via attributes, root endpoint mapping, scoped DI for token accessors — exactly compatible with all v1.x releases
- Upgrade risk: **Minimal** — no code changes needed, test suite should pass unchanged
- Recommendation: **Upgrade to v1.3.0** (P1 priority) for reliability (structured exceptions), security (CORS docs), future-proofing (resource streaming fixes), and stability (2 months production validation)
- Effort: ~15 min (edit 3 .csproj files, run tests)
- Outcome document: `.squad/decisions/inbox/ash-mcp-sdk-upgrade-research.md`

### 2026-05-15: MCP SDK 1.1.0–1.3.0 Feature Adoption Evaluation

- Requested by Larry Ewing: "Are there new features we should *consider adopting*, beyond the safety case for version bump?"
- Research scope: Feature set in C# MCP SDK v1.1.0, v1.2.0, v1.3.0 and broader MCP spec versions they introduce
- Features evaluated: AllowedValuesAttribute auto-completion, OutputSchema independence, Progress notifications, WithMeta annotation, Resource subscriptions, Roots, Sampling, Elicitation, Structured logging, Tool annotations, Client completion details
- Current tool inventory: 27 tools (14 AzDO, 13 Helix), all ReadOnly=true, Idempotent=true, string-based enum params

**Recommendations matrix:**
- ✅ ADOPT (P1 High): Progress notifications for long-running tools (`helix_download`, `azdo_search_log`, `helix_find_files`) — clients can track 30–120 second operations; prevents timeout false positives
- ✅ ADOPT (P2 Medium): AllowedValuesAttribute on enum-like params (`org`, `project`, `filter`, `recordType`) — clients auto-discover valid values; type safety + better UX; effort ~30 min
- ✅ ADOPT (P2 Medium): Client completion details (v1.1.0) — structured exception info when host crashes; improves debuggability; wait until pain point surfaces
- 🤔 MAYBE (P3 Low): WithMeta annotation, Tool auth hints, Structured logging — marginal ROI or require additional design
- ❌ SKIP: Elicitation, Sampling, Resources, Roots — architectural mismatch (no LLM reasoning, no filesystem access, no resources primitive)

**Effort estimate (post-upgrade to v1.3.0):**
- Phase 1 (AllowedValuesAttribute): 30 min
- Phase 2 (Progress notifications): 2–3 hours
- Phase 3 (Client completion details): 1 hour (if needed)

- Outcome document: `.squad/decisions/inbox/ash-mcp-sdk-adoptable-features.md`

### 2026-05-21: MCP Exception Audit — Exception Hygiene Analysis

- Audited all 27 MCP tool methods in src/HelixTool.Mcp.Tools/ for exception handling patterns
- Classification: 26 tools pre-wrapped (posture A), 1 tool raw-throw (posture B), 0 implicit, 0 swallowed, 0 mixed
- **Single issue found:** helix_ci_guide (CiKnowledgeTool.cs:11–20) calls CiKnowledgeService.GetGuide() without try/catch wrapper; raw exceptions bubble to JSON-RPC layer
- **Fix complexity:** Trivial (add try/catch, wrap in McpException — 3 lines)
- **Pattern excellence:** All other 26 tools follow BinlogMcp's McpException pattern correctly: broad catch for service-layer exceptions, context-specific catch for semantic errors (e.g., "not found"), pre-call parameter validation, config-based guards
- **Key patterns observed:**
  - Broad catch pattern: `catch (Exception ex) when (ex is HttpRequestException or HelixException or ...)`
  - Context-specific: `catch (InvalidOperationException ex) when (ex.Message.Contains("not found", ...))`
  - Pre-call validation: Parameter checks before service calls
  - Config guards: StringHelpers.IsFileSearchDisabled checks
- **Open questions:** (1) Should CiKnowledgeService validate repo names before lookup or rely on wrapper? (2) Are auth-status methods (helix_auth_status, azdo_auth_status) truly safe-for-no-wrapping? (Both synchronous, no I/O. Yes, safe.) (3) Future: structured error codes vs message-only?
- **Audit methodology extracted** → reusable process for auditing any MCP tool set by posture, user visibility, and fix complexity
- **Deliverable:** Comprehensive audit report in .squad/decisions/inbox/ash-mcp-exception-audit.md

### 2026-05-22: MCP Tool Description Audit — Token Bloat & Context Drift

- **Audit scope:** All 25 McpServerTool descriptions across src/HelixTool.Mcp.Tools/ (AzDO, Helix, CiKnowledgeTool)
- **Rubric source:** mcp-server-design skill at `~/source/blazor-playground/copilot-skills/plugins/skill-trainer/skills/mcp-server-design/` (SKILL.md + tool-description-patterns.md + tool-naming-conventions.md)
- **Key rubric findings:** Tool descriptions are **always loaded** into every session context (like skill frontmatter); compact budget ~20 words per tool is the proven standard; helix.mcp previously tightened 17 descriptions from ~60 to ~20 words, removing ~550 words of always-loaded context

**Audit results:**
- Total tools: 25 (14 AzDO, 11 Helix/CiKnowledgeTool)
- Total description words: 528 (avg 21.1 words/tool)
- Flagged tools: 8 (32% of inventory)
- Clean tools: 17 (68%)

**Anti-pattern breakdown (8 hits across 7 patterns):**
- **bloat** (5 tools): azdo_timeline (44 words — 2.2× target), azdo_helix_jobs (31), azdo_builds (30), azdo_build_analysis (30), helix_ci_guide (26)
- **situational-bloat** (3): azdo_timeline, azdo_helix_jobs, helix_status — all enumerate filter options in tool description when they belong in parameter description
- **schema-dump** (2): helix_status, helix_files — describe return value structure ("Returns failed items with...", "Returns binlogs, testResults...") violating purpose-first principle
- **domain-knowledge** (2): azdo_test_results (mentions aspnetcore, sdk, roslyn, efcore), helix_ci_guide (mentions macios/android) — knowledge that changes over time shouldn't bloat descriptions
- **parameter-detail** (1): azdo_builds — default org/project (dnceng-public/public) belong in parameter description, not tool summary

**Specific offenders & tightening directions:**
1. `azdo_timeline` (44 words) — Situational bloat: filter list duplicated in BOTH tool description AND parameter description (line 85). Direction: Move filter enumeration entirely to parameter; tool description should be 1-2 sentences on purpose.
2. `azdo_helix_jobs` (31 words) — Situational bloat: Same pattern. Direction: "Extract Helix job IDs from a build. Bridges AzDO→Helix gap." (~10 words); defer filter options to param.
3. `azdo_builds` (30 words) — Parameter detail embedded. Direction: "List recent builds for an AzDO project. Filter by PR, branch, definition, or status." (~12 words); dnceng-public/public default goes to param description.
4. `azdo_build_analysis` (30 words) — Acceptable content but verbose. Direction: "Extract Build Analysis known issue matches from a build." (~9 words); "matched/unmatched" detail belongs in param or knowledge tool.
5. `helix_ci_guide` (26 words) — This IS a knowledge tool; describing its content bloats its own description. Direction: "Get repo-specific CI guidance: tool selection, failure patterns, pipeline details." (~11 words); move repo list + macios/android auth note to inline examples or knowledge doc.
6. `azdo_test_results` (24 words) — Domain knowledge embedded. Direction: "Test results for a specific run. Defaults to failed tests only." (~11 words); defer repo-specific context to `helix_ci_guide`; remove aspnetcore/sdk/roslyn/efcore list.
7. `helix_status` (23 words) — Schema dump + situational bloat. Direction: "Get pass/fail summary for a Helix job." (~8 words); "exit codes, state, duration, machine" are visible in response; filter options belong to param.
8. `helix_files` (21 words) — Schema dump. Direction: "List uploaded files for a Helix work item, grouped by type." (~11 words); "binlogs, testResults" are schema details.

**Estimated context savings:**
- If 8 flagged tools rewritten to ~20 words: ~69 words removed
- This aligns with 2026-02-13 precedent: 17-tool tightening removed ~550 words (~32 words per tool)
- Current state: descriptions have drifted back up (avg 21.1 words, 5 over 25-word threshold)

**Key findings:**
- **Filter enumeration duplication:** azdo_timeline and azdo_helix_jobs duplicate their filter lists between tool description and parameter description. One source of truth should be parameter-level.
- **Schema leakage:** Descriptions that start "Returns X with Y, Z fields" should start with the action verb ("List", "Get", "Extract")
- **Domain knowledge migration:** Repo-specific failure signatures, tool sequences, and authentication gotchas belong in helix_ci_guide knowledge tool, not in individual tool descriptions
- **Knowledge tool self-reference:** helix_ci_guide describing its own content (listing 9 repos, mentioning macios auth) is ironic — it should point to itself or defer

**Audit methodology:** Extracted all [McpServerTool] attributes with regex, counted words, classified 7 anti-pattern types from skill rubric (bloat, no-verb-lead, schema-dump, situational-bloat, domain-knowledge, parameter-detail, missing-routing-signal). Created reusable Python analysis script for future audits.

- **Recommendation:** Ripley should tighten 8 flagged descriptions (priority: azdo_timeline, azdo_helix_jobs first — most duplication). Effort ~1–2 hours total. Follow tool-description-patterns.md: lead with verb, 1–2 sentences, defer filters to params, move domain knowledge to helix_ci_guide. This would align with 2026-02-13 precedent and remove ~69 words of always-loaded context.


---

*Archived on 2026-05-22T18:44:50Z. See current history.md for recent entries.*
