# Kane — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **README.md** exists with Quick Start, MCP Tools table, Project Structure, Requirements
- **llmstxt command** exists in Program.cs — prints CLI documentation for LLM agents
- **MCP tool descriptions** are in HelixMcpTools.cs via [Description] attributes
- **XML doc comments** exist on CLI commands and HelixService class

## Core Context

- **Documentation sources:** `README.md`, the `llmstxt`/help text in `src/HelixTool/Program.cs`, MCP `[Description]` attributes in `src/HelixTool.Mcp.Tools/`, and `docs/cli-reference.md` are the maintained user-facing docs surfaces.
- **Doc structure conventions:** README leads with value proposition, caching, MCP tools, install/config, auth, and security; detailed CLI syntax lives in `docs/cli-reference.md`, not in the README.
- **MCP wording rules:** descriptions explain what the tool does, accepted inputs, and returned outputs; repo-specific workflow routing belongs in `helix_ci_guide`, and canonical MCP config examples use one shared block plus a path table.
- **Current naming/details:** package = `lewing.helix.mcp`, repo = `helix.mcp`, CLI command = `hlx`, `dnx` configs need `--yes`, and docs should surface MCP resources like `ci://profiles` alongside idempotent annotations where relevant.

## Learnings

- AzDO tools are MCP-only (no CLI subcommands) — README documents them only in the MCP Tools section, not in CLI Commands
- AzDO auth chain pattern: env var → az CLI → anonymous — different from Helix which uses env var → git credential → error
- AzDO caching uses the same SqliteCacheStore but with distinct TTL rules per endpoint type (builds 4h completed/15s in-progress, logs 4h, tests 1h)
- AzdoIdResolver accepts both dev.azure.com and visualstudio.com URL formats, plus plain integer build IDs
- When adding a new API domain (AzDO alongside Helix), use subsections (### Helix Tools / ### AzDO Tools) rather than separate top-level sections to keep the README scannable
- llmstxt raw string literal in Program.cs must stay flush-left — no indentation inside the `"""..."""` block
- AzDO tools total 9 (not 7 as originally noted in earlier decision): azdo_build, azdo_builds, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_artifacts, azdo_test_attachments
- Key AzDO source files: `src/HelixTool.Core/AzDO/AzdoMcpTools.cs` (tool definitions), `AzdoService.cs` (core logic), `AzdoIdResolver.cs` (URL/ID parsing), `CachingAzdoApiClient.cs` (cache wrapper)
- AzDO MCP tool source moved from `src/HelixTool.Core/AzDO/AzdoMcpTools.cs` to `src/HelixTool.Mcp.Tools/AzdoMcpTools.cs` — must grep for actual location before editing
- AzDO tools total is now 12 (was 9): added azdo_search_log, azdo_search_timeline, azdo_search_log_across_steps. Helix tools = 11. Grand total = 23.
- `azdo_search_log_across_steps` MCP name maps to CLI command `hlx azdo search-log-all` — naming convention: MCP uses underscores, CLI uses kebab-case, and "across_steps" was shortened to "all" for CLI brevity
- Incremental log fetching is documented in Caching section (TTL policy — AzDO paragraph), not as a standalone section — consistent with keeping caching details in one place
- `azdo_search_log_across_steps` is gated by `HLX_DISABLE_FILE_SEARCH` same as other search tools — added to Security section's file search toggle list

📌 Team update (2026-03-08): `IsFileSearchDisabled` promoted from internal to public on `HelixService` — needed for MCP tools extraction to separate assembly. Consistent with existing public statics `MatchesPattern` and `IsTestResultFile`. — decided by Ripley

📌 Team update (2026-03-09): CI profile analysis — 14 recommendations for MCP tool descriptions. Tool descriptions in HelixMcpTools.cs and AzdoMcpTools.cs will change. README and llmstxt may need updates once descriptions are implemented. — decided by Ash

📌 Team update (2026-03-10): CiKnowledgeService expanded to 9 repos with 9 new CiRepoProfile properties. MCP tool descriptions now embed repo-specific CI knowledge. 171 new tests added. — decided by Ripley

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

📌 Team update (2026-03-10): README overhaul — restructured around value proposition, caching, and context reduction. Removed project structure section, moved CLI reference to docs/cli-reference.md, de-emphasized TRX support, consolidated auth. PR #18. — documented by Kane

- README structure: Why → Context-Efficient Design → Cross-Process Caching → MCP Tools → Installation → MCP Config → Auth → Security. This order leads with value prop for evaluators.
- CLI reference lives at docs/cli-reference.md — README links to it but doesn't include full command listings. Keep CLI details there, MCP tool tables in README.
- "How hlx Enhances the Helix API" section was removed — its content overlapped heavily with the Why section and the context-efficiency table. Avoid duplicate storytelling.
- TRX support is one row in the Helix tools table, not a featured section. It's a feature, not a differentiator.
- MCP tool table descriptions should be short (one line). Detailed parameter docs belong in [Description] attributes on the actual tools, not README.
- README went from 589 → ~270 lines. Conciseness is a feature for a README — readers are evaluating, not studying.
- Discoverability guidance should stay as a short routing note, not a mini-manual: `helix_ci_guide(repo)` when repo expectations vary, `helix_test_results` for Helix-hosted structured results, then AzDO structured results or `helix_search_log` as the fallback path.
- `docs/cli-reference.md` only needs a brief consistency note for that investigation path; the full repo-specific guidance belongs in MCP surfaces, not expanded CLI command docs.

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash

📌 Team update (2026-03-10): Discoverability routing decisions merged — keep the current tool surface, route repo-specific workflow selection through `helix_ci_guide(repo)`, treat `helix_test_results` as structured Helix-hosted parsing rather than a universal first step, and keep `helix_search_log`/docs/help guidance synchronized across surfaces. — decided by Dallas, Kane, Ripley

📌 Team update (2026-03-13): Scribe merged decision inbox items covering `dotnet` as the VMR profile key, `helix_search`/`helix_parse_uploaded_trx` naming, tighter MCP descriptions, and explicit truncation metadata (`truncated`, `LimitedResults<T>`). README/docs now also call out `ci://profiles` resources and idempotent annotations.
- AzDO auth resolution is now `AZDO_TOKEN` (PATs auto-Basic, JWT/Entra tokens auto-Bearer) → `AzureCliCredential` → `az account get-access-token` → anonymous; README should show the concrete 401 remediation example for private orgs.
- README Authentication now sits immediately after MCP resources/tool discovery, so access requirements are visible before install/config details.

📌 Team update (2026-03-13): AzDO auth is now the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous, with scheme-aware `AzdoCredential` metadata and `DisplayToken` kept separate from the wire token. — decided by Dallas, Ripley

📌 Team update (2026-03-13): MCP-facing Helix names/descriptions should stay scope-accurate and low-context: use `helix_parse_uploaded_trx`, `helix_search`, and keep repo-specific routing in `helix_ci_guide`. — decided by Ripley

📌 Team update (2026-03-13): README/docs should expose MCP resources (`ci://profiles`, `ci://profiles/{repo}`) and treat idempotent annotations as a context-efficiency design point. — decided by Lambert

📌 Team update (2026-03-13): PR #28 merged the remaining AzDO auth quick wins — fallback Azure CLI/`az` credentials now refresh on deadline/401, cache isolation keys off stable auth-source identity instead of raw token bytes, and `hlx azdo auth-status` exposes safe auth-path metadata for docs/threat-model follow-up. — decided by Ripley
- `.github/skills/helix-cli/SKILL.md` now mirrors the maestro-cli skill structure: frontmatter, CLI-vs-MCP routing, auth/discovery guidance, jq workflows, and cache behavior for using `hlx` via bash.
- The helix-cli skill treats `hlx llms-txt` as the CLI discovery surface and references MCP-only `helix_ci_guide(repo)` / `ci://profiles` as secondary routing because there is no shipped `hlx ci-guide` command yet.
- A companion reference doc was drafted during PR #30, then removed in review. The final skill keeps jq-critical details inline in `SKILL.md`, including that `hlx azdo builds --json` returns a bare array and `hlx search-log` remains text-only in the CLI.

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane
- PR #30 review feedback finalized the skill as a single-source doc: rely on `hlx llms-txt`, command help, and inline jq field hints instead of a separate static reference file.
- Do not document unshipped CLI JSON field shapes in the skill. For Helix `hlx search-log`, keep the CLI docs text-only and route structured consumers to MCP `helix_search`.
- `hlx <command> --schema` should be tracked as a product issue rather than backfilled with a long static reference doc.

📌 Team update (2026-03-14): `hlx azdo search-log --schema` must mirror the active JSON payload: `LogSearchResult` with `--log-id`, `CrossStepSearchResult` otherwise. — decided by Ripley
- `.github/skills/helix-cli/SKILL.md` should use `hlx describe` as the agent-first discovery path: top-level routing, then `hlx describe <command>`, then `<command> --schema`, then `<command> --help`.
- `hlx llms-txt` still exists, but in the helix-cli skill it should be a secondary note rather than the recommended discovery chain.
- Keep the helix-cli skill narrowly scoped to shipped CLI behavior; preserve the existing jq workflows, auth guidance, and cache section unless the shipped surface changed.
- Key doc file: `.github/skills/helix-cli/SKILL.md` is the single maintained skill doc for agent-facing `hlx` CLI usage and discovery guidance.
- Key agent log: `.squad/agents/kane/history.md` records durable documentation decisions, shipped-surface notes, and file-location reminders for future Kane tasks.

📌 Team update (2026-05-08): MCP SDK 1.3.0 upgrade — Central Package Management adopted (Directory.Packages.props), MCP SDK 1.0.0 → 1.3.0 (no source changes required). Note for docs review: ServerInfo.Version now dynamically reads AssemblyInformationalVersionAttribute; CLI/stdio host inherits Version from HelixTool.csproj (0.5.4), HTTP host reports 1.0.0.0. Documentation may need to note source of server version string in future reference sections.


📌 Team update (2026-05-08): MCP annotations and progress notifications merged

**Context:** Ripley completed two PRs merging MCP SDK 1.3.0 follow-ups:
1. **PR #47** — `[AllowedValues]` on enum params, `OpenWorld` annotations (22 network tools = true, 3 static tools = false), `<packageSourceMapping>` for NU1507
2. **PR #48** — Progress notifications on `helix_download`, `azdo_search_log`, `helix_find_files` via SDK 1.3.0 auto-injected `IProgress<T>`

**Action for docs:** Once PRs merge:
- Add README section under MCP server docs noting which tools emit progress and example message formats ("Downloaded 42 of 128 MB", "Searched 12 of 50 log steps")
- Update tool descriptions in HelixMcpTools.cs / AzdoMcpTools.cs if `[AllowedValues]` options or progress behavior needs clarification
- Consider mentioning `[AllowedValues]` + `OpenWorld=false` annotations in security/capability section if docs cover MCP best practices

**Status:** PRs open, awaiting Dallas code review.
- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

### 2026-06-24: PR #78 AzDO Param Plumbing Shipped

- Three parameter plumbing bugs fixed in unified PR (minTime/maxTime/queryOrder, top forwarding, outcomes filter)
- Four review rounds addressing whitespace, cache semantics, boundary normalization, and API stability
- 14 new tests; all 1337 tests pass

**Related:** Session log: `.squad/log/2026-06-24-pr78-azdo-param-plumbing-and-followups.md`
**Follow-up:** Issue #82 (architectural cleanup: centralize AzDO filter normalization)

📌 Team update (2026-06-24): PR #85 + Issue #82 merged — Ripley implemented centralized AzDO filter normalization (4 sub-changes consolidated to 1 PR), Lambert added 91 comprehensive tests (44 normalizer + 42 contract + 5 stability). **USER-VISIBLE CHANGE:** `queryOrder` parameter now sends lowercase values in REST URLs (`finishtimedescending` instead of `finishTimeDescending`). AzDO treats this as case-insensitive, so behavior is identical, but any documentation or downstream assertions on exact query-order casing should be reviewed. — decided by Ripley, tested by Lambert

### 2026-06-24: v0.8.0 doc audit + catch-up PR #89

**Audit findings:**

- README install snippet is version-agnostic — fine.
- No code blocks pin "0.7.6" or any specific version — fine.
- No MCP config examples implying silent-drop tolerance — fine.
- `docs/threat-model-azdo-auth.md` unaffected by v0.8.0 changes — fine.
- `azdo_test_attachments` CLI reference already showed `--top N` — no change needed.

**What needed updating (all addressed in PR #89):**

1. `CHANGELOG.md` — did not exist; created with v0.8.0 + v0.7.6 entries.
2. README AzDO tools table: `azdo_builds` description was missing `minTime`/`maxTime`/`queryOrder` (PR #78 plumbing fix).
3. README AzDO tools table: `azdo_test_results` description didn't mention `outcomes` param (PR #78 plumbing fix).
4. README: no mention of strict param rejection or aliases — added parameter validation callout below AzDO tools table.
5. `docs/cli-reference.md`: `hlx azdo builds` was missing `--min-time`, `--max-time`, `--query-order` flags (PR #78).
6. `docs/cli-reference.md`: `hlx azdo test-results` was missing `--outcomes` flag (PR #78).

**Pre-existing issue noted but NOT fixed (out of v0.8.0 scope):**

- README Helix Tools section header says "(9)" but there are 11 Helix tools in the code (helix_auth_status + helix_ci_guide not in the table). Predates v0.8.0.
- README AzDO Tools section header says "(11)" but there are 14 AzDO tools in the code (azdo_helix_jobs, azdo_build_analysis, azdo_auth_status not in the table). Predates v0.8.0.
- Logged both as follow-up in `.squad/agents/kane/inbox/kane-v0.8.0-audit.md`.

**PR:** #89 (`docs/v0.8.0-catchup`). Do not merge — Larry presses the button.

### 2026-06-24: CI Guide v0.8.0 Audit-Driven Update — PR #90

**Trigger:** Ash completed a live empirical audit of `helix_ci_guide` against v0.8.0 tool behavior (3 repos: runtime, aspnetcore, sdk). Report: `.squad/decisions/inbox/ash-ci-guide-audit-v0.8.0.md`.

**Changes applied to `CiKnowledgeService.cs`:**
1. **Artifact pattern** — generalized `Logs_Build_*` to note repo-specific naming (runtime vs aspnetcore patterns, verified by Ash's azdo_artifacts calls).
2. **SDK KnownGotchas** — added `azdo_helix_jobs` returns 0 caveat (emoji task name not recognized; use azdo_test_runs for [HelixJob:GUID] instead).
3. **SDK RecommendedInvestigationOrder step 4** — replaced bare `helix_status(jobId)` with explicit GUID extraction path since `azdo_helix_jobs` doesn't work for SDK.
4. **Global azdo_helix_jobs caveat** — added to Test Results Tool Selection with generic wording covering any repo with non-standard task names.
5. **outcomes filter** — added Key Insights note on `outcomes='NotExecuted,Failed'` for surfacing platform-conditional skips.
6. **azdo_builds entry point** — added "Finding Builds to Investigate" section to general guide with definitionId/prNumber/minTime/maxTime/branch examples.

**Verified accurate, left unchanged:** `azdo_test_attachments` claim (confirmed empty across all 3 repos tested).

**Deferred:** aspnetcore helix_parse_uploaded_trx direct verification (no Helix-reaching build available in audit), unverified repo profiles (roslyn/efcore/dotnet/maui/macios/android), failedTests=0 masking real Failed outcomes (edge case untested).

**Key pattern to reuse:** **Verify-before-document.** Never add guide claims that haven't been confirmed by actual tool calls. When Ash's tool budget ran out, we stopped — no speculative additions. This discipline keeps the guide trustworthy.

**Build/test:** 0 warnings, 1450/1452 pass (2 pre-existing skips unrelated to CI guide).

**PR:** #90 (`docs/ci-guide-v0.8.0-audit`). Do not merge — Larry presses the button.
