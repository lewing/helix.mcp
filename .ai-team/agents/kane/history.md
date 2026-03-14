# Kane — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
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
