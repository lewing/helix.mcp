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

## 2026-09-11 — Startup cache-eviction lifecycle CHANGELOG entry (#129)

**Documentation:** Added single [Unreleased] CHANGELOG entry for #129. Clarified that startup cache maintenance is now tracked and canceled/joined on disposal, and the startup pass no longer removes entries written after the cache was opened. Explicit note: "Maintenance is not always awaited at shutdown" (CLI does not dispose ServiceProvider). No README/cli-reference changes (no public API changes, no command/flag changes). No misleading "always await" claims. Out of scope: broader TTL/LRU documentation (unchanged).

**Decision:** `kane-changelog.md` (documentation status & scope).

**Orchestration log:** `.squad/orchestration-log/2026-09-11-1350-kane-changelog.md`
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

## Learnings

### 2026-08-26: Snapshot Export Auth Replay Wording
- Snapshot export warnings cannot depend on `AuthTokenHash` or `CacheRootHash`: the export command starts in a fresh process before AzDO credential resolution, so both may be null.
- Auth-scoped keys remain unchanged. Eval mode can reproduce environment-keyed partitions with the identical `AZDO_TOKEN` and effective PAT/Bearer classification; matching `AZDO_TOKEN_TYPE` is the reliable classification control.
- Eval's environment-only accessor does not reproduce `AzureCliCredential` or `az` CLI identity partitions. Anonymous/public entries remain usable without credentials.
- User-facing export output should report only the published snapshot destination, artifact count, and database size; online backup makes WAL checkpoint/page and sidecar-copy status claims obsolete.

### 2026-07-20: hlx CLI Skill Discoverability Assessment
**Question:** Do we have something like maestro's `helix-cli` SKILL.md? Is it good? How do people find it when MCP isn't running?

**Answer:** We already have `.github/skills/helix-cli/SKILL.md`. It's richer than maestro's in progressive discovery (4-level ladder), jq examples (real field paths), and workflow patterns (7 numbered). Three gaps in the skill doc: no `dnx` install path, stub Cache section (doesn't say "CLI warms cache for MCP"), and frontmatter doesn't trigger on "MCP fails to start."

**Bigger finding:** The skill is good once found, but **discoverability when MCP is absent** has four weak points:
1. README headline is self-deprecating about the CLI, no "works standalone" callout near the top.
2. MCP Configuration section doesn't mention CLI fallback.
3. SKILL.md frontmatter says "not loaded" not "not configured / fails to start."
4. docs/cli-reference.md doesn't open with "works without MCP."

**Top edits ranked:** (1) README top — add "No MCP? `hlx` works standalone" callout. (2) SKILL.md — add `dnx` install + expand Cache section. (3) README MCP config section — add CLI fallback note. (4) cli-reference.md first line. (5) SKILL.md frontmatter USE FOR phrase.

**Decision filed:** `.squad/decisions/inbox/kane-hlx-cli-skill-discoverability.md`

---

## Final Review F2: Scratch Path Cleanup (Dallas Review 2026-08-20)

**Finding:** Third-party source copy (`upstream-StatelessServerTests.cs`, 642 lines, zero provenance) and test-run logs (`.squad/evidence/`) were about to be committed into the PR, violating compliance/licensing policy and creating scratch clutter.

**Action:** 
- Deleted `.squad/artifacts/` and `.squad/evidence/` directories entirely
- Rewrote two dangling XML-doc references in test classes to cite upstream MCP C# SDK v2.2.0 directly via stable GitHub blob URLs
- Corrected XML-doc misnomer in AzdoMcpTools.cs line 468: "six **paginated** AzDO tools" → "six **capped/truncating** AzDO tools"
- Added `.squad/artifacts/` and `.squad/evidence/` to `.gitignore` alongside existing scratch patterns, preserving durable `.squad/skills/` and histories

**Key Learning:** When citing upstream library sources in XML docs, prefer stable GitHub URLs (blob/tag/path) over local file copies. This avoids vendoring third-party code without provenance and keeps test documentation pointing to authoritative sources.

**Result:** No dangling references, git status clean of scratch paths, .gitignore prevents recurrence.

---

## Snapshot Export/Validate Discoverability (2026-09-11)

**Task:** Close discoverability gap for snapshotting. The feature existed (SnapshotCommands.cs, SnapshotExporter.cs, SnapshotValidator.cs) but had zero documentation.

**Workflow documented:**
- **Export:** `hlx snapshot export <destination>` — exports cache to portable SQLite snapshot with artifact files. Prints auth-scoped replay limitation warning and usage instructions with `HLX_EVAL_SNAPSHOT`.
- **Validate:** `hlx snapshot validate <snapshotPath>` — checks SQLite integrity, schema version, single-link requirement (no hard-link aliases), and artifact references. Exit 0 (valid) or 1 (invalid) with detailed diagnostics.
- **Replay:** Set `HLX_EVAL_SNAPSHOT=/path/to/snapshot hlx <command>` for offline evaluation (no network calls).
- **Auth semantics:** Environment-keyed entries (via AZDO_TOKEN) reproducible with matching token+AZDO_TOKEN_TYPE classification. Anonymous/public entries always reproducible. AzureCliCredential/az CLI-derived partitions not reproducible in eval mode.

**Changes:**
1. **docs/cli-reference.md:** Added "Snapshot Commands" section (two subsections: export, validate) covering parameters, semantics, exit codes, and intended workflow. Moved "Utility Commands" table below. Added `HLX_EVAL_SNAPSHOT` and `AZDO_TOKEN_TYPE` to Environment Variables table with full descriptions.
2. **CHANGELOG.md:** Added entry under [Unreleased] titled "Snapshot export and validation for offline replay mode" with feature summary, use cases, auth-scoped replay semantics, and snapshot layout details.
3. **README.md:** Added "Offline Snapshots" subsection under "Cross-Process Caching" with code example and link to full CLI reference. Maintains concise cross-reference pattern.

**Key design decisions:**
- Exact CLI syntax derived from SnapshotCommands.cs [Command] attributes, not guessed.
- Auth limitation doc mirrors SnapshotCommands.cs export command's Console.Error.WriteLine blocks (the source of truth for user-facing limitation messaging).
- Snapshot layout description from SnapshotValidator.cs/SnapshotExporter.cs checks: cache.db requires single-link ownership (no aliases), artifacts/ optional, sidecars forbidden.
- Exit code table matches implementation: exit 0 = valid, exit 1 = errors.
- AZDO_TOKEN_TYPE added to environment table as it's foundational to auth-scoped key classification in replay mode.
- README callout stays concise and defers detailed reference to docs/cli-reference.md (consistent with investigation-path and cross-reference patterns).

**Completeness:** Feature is now discoverable via CLI reference, searchable in README, and properly versioned in unreleased changelog.

---

## Prior Work Archive

See `.squad/agents/kane/history-archive.md` for detailed work on:
- Project structure, auth chain patterns, tool enumeration
- Folder restructuring analysis (Option A executed in PR #17)
- Cache security review, HelixService refactoring
- Knowledgebase refresh guidance
