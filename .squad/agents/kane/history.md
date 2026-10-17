# Kane — History (Condensed)

## Executive Summary

**Role:** Documentation lead. Maintains README, docs/cli-reference.md, .github/skills/helix-cli/SKILL.md.

**Current Focus:** v0.11.0 release cycle: helix-aware evidence plan docs, CLI paging docs, collector docs, post-merge doc fixes. All v0.11.0 documentation updates shipped.

**Key Learnings:** MCP tool descriptions explain what/inputs/outputs; repo-specific routing via helix_ci_guide; README leads with value prop, keeps CLI details in docs/cli-reference.md. For public error contracts and collector/snapshot docs, verify JSON property names, enum wire strings, provider values, exit codes, flag names against exact commit source/tests plus real CLI runs. Never infer or guess.

---

## Durable Documentation Principles

- **MCP surfaces:** tool descriptions in HelixMcpTools.cs / AzdoMcpTools.cs are the source of truth.
- **CLI surfaces:** llmstxt (Program.cs), --help on commands, docs/cli-reference.md.
- **Discovery routing:** `hlx llms-txt` → `hlx describe <command>` → `<command> --schema` → `<command> --help`.
- **Do NOT document unshipped JSON field shapes** in skill docs; keep hlx search-log CLI text-only, route structured consumers to MCP helix_search.
- **For screenshots/architecture:** Prefer stable GitHub blob URLs over local file copies to avoid vendoring third-party code without provenance.
- **Subsection headers:** Use ### Helix Tools / ### AzDO Tools rather than separate top-level sections for scanability.
- **llmstxt raw string:** Flush-left in Program.cs (no indentation inside """ """ block).

## Tool & API Context

**Helix tools:** 11 (search-log, parse-trx, logs, files, work-item, status, find-files, download, batch-status, helix_ci_guide, helix_parse_uploaded_trx)

**AzDO tools:** 12 (build, builds, timeline, log, changes, test-runs, test-results, artifacts, test-attachments, search-log, search-timeline, search-log-across-steps)

**CLI vs. MCP naming:** MCP uses underscores, CLI uses kebab-case. Example: `azdo_search_log_across_steps` MCP → `hlx azdo search-log-all` CLI.

**AzDO auth:** AZDO_TOKEN (PAT/JWT/Entra) → AzureCliCredential → az CLI → anonymous. Narrow chain with scheme-aware metadata.

**AzDO caching:** SqliteCacheStore with TTL per endpoint (builds 4h completed/15s in-progress, logs 4h, tests 1h).

---

## Learnings (Summary)

**Snapshot export auth replay wording:** Snapshot export warnings cannot depend on `AuthTokenHash` or `CacheRootHash` (null at fresh process before credential resolution). Auth-scoped keys unchanged. Eval can reproduce environment-keyed partitions with identical `AZDO_TOKEN` and effective PAT; matching `AZDO_TOKEN_TYPE` is reliable classification control. Eval's environment-only accessor doesn't reproduce `AzureCliCredential` or `az` CLI identity. Anonymous/public entries usable without credentials. Export output should report only destination, artifact count, database size.

**Helix CLI discoverability:** CLI works standalone without MCP. Top gaps: README self-deprecating about CLI, MCP config section doesn't mention CLI fallback, SKILL.md frontmatter says "not loaded" not "fails to start", cli-reference doesn't open with "works without MCP". Top edits: (1) README top — "No MCP? `hlx` works standalone". (2) SKILL.md — add `dnx` install + expand Cache section. (3) README MCP section — add CLI fallback. (4) cli-reference first line. (5) SKILL.md frontmatter USE FOR.

**Collector docs verification:** For collector docs, verify `CollectCommands`, `CollectPolicy`, `CollectManifest`, `AzdoBuildCollector`. Design doc may be ahead of implementation. Re-check after implementation lands. Current source: streams `--download-helix-files` to temp, enforces `--max-file-bytes`/`--max-total-bytes`, records `size_limit`/`total_size_limit`, caches in-cap files for offline replay, records AzDO `auth.azdo.cachePartition` with replay mode `snapshot_partition`. Eval replay selects non-secret partition without credentials; document `HLX_EVAL_AZDO_PARTITION` for multi-partition snapshots.

**Cache-loss incident docs:** For cache-loss incidents, verify both source and regression tests before writing release guidance. Shipped fix: encodes metadata containing NUL with `hlx:nul-base64\n`, preserves raw AzDO logs cached with `\0raw\n`, treats empty/corrupt raw-log snapshot rows as `cache/invalid_response`, `hlx snapshot validate` rejects those rows. For collector docs, distinguish missing artifact evidence (`artifact_missing`) from corrupt/size-mismatched cache evidence (`fetch_failed` with `provider=cache`).

**Dallas R3 doc-accuracy gate fixes:** Doc claim matching older mental model can still be wrong. `EvalSnapshotAzdoPartitionSelector.Select` has five-step precedence (explicit env var → manifest `auth.azdo.cachePartition` → single discovered → ambiguous-partition refusal → `public` default), not just "env var or fail closed". Previously shipped CHANGELOG/cli-reference skipped manifest step, implied `HLX_EVAL_AZDO_PARTITION` unconditionally required. Implicit `--export` isolated cache directory is real separate temp root (command.options.cacheDir in manifest, printed to stderr) not rediscovered by `--resume --manifest <path>` alone — new `--cache-dir` must be passed explicitly. Use concrete before/after example, not just assertion. When writing CHANGELOG for fixes still flagged REJECT elsewhere (e.g. R1 evidence-eviction, R2 Helix job-discovery), phrase neutrally, scope narrowly to only accepted sub-behavior — don't let adjacent fix's wording bleed into still-broken part.

## Recent Sessions (Most Recent ~5)

### 2026-09-11 — Startup cache-eviction lifecycle CHANGELOG entry (#129)

Added single [Unreleased] CHANGELOG entry. Clarified startup cache maintenance now tracked and canceled/joined on disposal; startup pass no longer removes entries written after cache opened. Explicit note: "Maintenance is not always awaited at shutdown" (CLI doesn't dispose ServiceProvider). No README/cli-reference changes (no public API/command/flag changes). No misleading "always await" claims.

### 2026-10-02 — Helix-aware evidence plan documentation

Updated docs/cli-reference.md `hlx azdo evidence plan` section: added `--helix-failure-offset`/`--helix-failure-limit` parameters, expanded Output Structure with full `helixFailures[]` documentation (field meanings, paging semantics, seven `incompleteDetails[].code` values), added exit code clarification (exit 2 when incomplete), jq example converting `helixFailures[]` into helix fetch command templates. Updated README.md MCP Tools table to note Helix monitor support + paging. Added CHANGELOG.md [Unreleased] entry "Helix-aware evidence plan — arcade queue-monitor parsing" with parsing behavior, paging support, machine-readable codes, deterministic drilldown.

### 2026-10-02 — CLI paging docs source-verification lesson

For flags/fields/exit codes, verify from implementation: `src/HelixTool/Program.cs` owns CLI flag names/validation/--top alias/exit 2; `HlxListEnvelope.cs` owns field names; `AzdoService.CreateEnvelope` owns `complete`/`truncated`/`next`/`cache`/`note` semantics; `CachingAzdoApiClient` owns complete-key eval replay; `AzdoMcpTools` confirms MCP defaults stay capped. Generate live public example, include real exit code for scanner-visible JSON changes.

### 2026-10-02 — `hlx collect azdo-build` docs final gate

All user-facing surfaces now document Helix integration and collector workflow. Docs match shipped code (field names from AzdoEvidenceModels.cs/AzdoMcpTools.cs/Program.cs verified). Paging semantics tied to DefaultHelixFailureLimit and MaxHelixFailureLimit constants. Backward compatibility explicitly noted. No stale text found in CiKnowledgeService.cs (grep found no matches); no documentation debt identified in src/.

### 2026-10-02 — Dallas R3 doc-accuracy gate fixes (final)

Fixed two remaining Markdown issues per Dallas final release approval gate. Corrected docs/cli-reference.md manifest path example: base `/tmp/hlx-collect-cache/<guid>`, `GetEffectiveCacheRoot` appends `public`, `ResolveManifestPath` writes there. Final example: `<guid>/public/hlx-collect-manifest.json`. Confirmed final breaking/security/data-loss/features/fixes release notes with required-evidence retention, atomic optional headroom/resume accounting, classified SDK discovery fallback. v0.11.0 shipped with all documentation corrected and verified.

