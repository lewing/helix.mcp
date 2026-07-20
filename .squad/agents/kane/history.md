# Kane — History (Condensed)

## Executive Summary

**Role:** Documentation lead. Maintains README, docs/cli-reference.md, .github/skills/helix-cli/SKILL.md.

**Key Learnings:** MCP tool descriptions explain what/inputs/outputs; repo-specific routing via helix_ci_guide; README leads with value prop, keeps CLI details in docs/cli-reference.md.

---

## Documentation Decisions (Merged)

### 2026-03-10: README Overhaul (PR #18)
**Restructured:** Why (value prop) → Context-Efficient Design → Caching → MCP Tools → Installation → Auth → Security.
**Removed:** Project structure section (use codebase), full CLI reference (→ docs/cli-reference.md), TRX as featured section (→ tools table).
**Result:** 589 → ~270 lines. Conciseness is a feature for evaluators.

### 2026-03-10: Documentation/Tool-Description Synchronization
- **MCP surfaces:** tool descriptions in HelixMcpTools.cs / AzdoMcpTools.cs are the source of truth.
- **CLI surfaces:** llmstxt (Program.cs), --help on commands, docs/cli-reference.md.
- **Discovery routing:** `hlx llms-txt` → `hlx describe <command>` → `<command> --schema` → `<command> --help`.
- **Do NOT document unshipped JSON field shapes** in skill docs; keep hlx search-log CLI text-only, route structured consumers to MCP helix_search.

### 2026-05-08: MCP Annotations & Progress Notifications (PR #47 + #48)
- **AllowedValues** on enum params (22 network tools = OpenWorld true, 3 static = false)
- **Progress notifications** on helix_download, azdo_search_log, helix_find_files
- **Docs action:** Add README section noting which tools emit progress with example formats

---

## Tool & API Context

**Helix tools:** 11 (hlx search-log, hlx parse-trx, hlx logs, hlx files, hlx work-item, hlx status, hlx find-files, hlx download, hlx batch-status, helix_ci_guide, helix_parse_uploaded_trx)

**AzDO tools:** 12 (azdo_build, azdo_builds, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_artifacts, azdo_test_attachments, azdo_search_log, azdo_search_timeline, azdo_search_log_across_steps)

**CLI vs. MCP naming:** MCP uses underscores, CLI uses kebab-case. Example: `azdo_search_log_across_steps` MCP → `hlx azdo search-log-all` CLI.

**AzDO auth:** AZDO_TOKEN (PAT/JWT/Entra) → AzureCliCredential → az CLI → anonymous. Narrow chain with scheme-aware metadata.

**AzDO caching:** SqliteCacheStore with TTL per endpoint (builds 4h completed/15s in-progress, logs 4h, tests 1h).

---

## Structural Conventions

- **Subsection headers:** Use ### Helix Tools / ### AzDO Tools rather than separate top-level sections for scanability.
- **llmstxt raw string:** Flush-left in Program.cs (no indentation inside """ """ block).
- **MCP tool table descriptions:** One line each. Detailed param docs in [Description] attributes, not README.
- **File locations (DO grep before editing):** HelixMcpTools.cs moved to src/HelixTool.Mcp.Tools/ (from src/HelixTool.Core/); AzdoMcpTools.cs same location.
- **Folder restructuring (2026-03-10):** 9 Helix files → Core/Helix/; Cache namespace added; shared utils extracted; Helix/AzDO subfolders in Mcp.Tools and Tests (59 files, 1038 tests pass, PR #17).

---

## Skill Doc Maintenance

**File:** .github/skills/helix-cli/SKILL.md (single-source CLI doc for agents)

**Structure:** Discovery path (hlx describe → --schema → --help), auth/caching guidance, jq workflows, cache behavior.

**Content rules:**
- Treat as living document aligned to shipped CLI state
- Note tool-discovery surfaces (llmstxt primary, llms-txt secondary)
- No unshipped CLI JSON shapes
- Use exact Ordinal root-boundary checks for cache path containment

**History decisions:** Issue #59 Phase 1 learnings merged; discoverability + documentation/tool-description sync remain active.

---

## Prior Work Archive

See `.squad/agents/kane/history-archive.md` for detailed work on:
- Project structure, auth chain patterns, tool enumeration
- Folder restructuring analysis (Option A executed in PR #17)
- Cache security review, HelixService refactoring
- Knowledgebase refresh guidance
