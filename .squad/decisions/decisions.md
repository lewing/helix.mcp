# Decisions

**Last updated:** 2026-07-20T21:08:57Z
**Merge cycle:** 2026-07-20T21:08:57Z (Scribe archival + inbox merge)

---

## Active Decisions

### 2026-07-20T16:05:00-05:00: outputSchema Keep vs. Flatten — Per-Tool Assessment
**By:** Dallas (Lead)
**Status:** Recommendation (refines prior "flatten everything" call)

**Summary:** Refined assessment after user challenge on schema value: Flatten 10 tools (helix_status, azdo_build, helix_parse_uploaded_trx, azdo_search_timeline, helix_batch_status, helix_work_item, helix_search, helix_find_files, helix_files, helix_download) to `{"type":"object"}` for ~5,450 bytes (18% savings). Keep 3 chaining-junction tools (azdo_timeline, azdo_helix_jobs, azdo_build_analysis) with full schema + optional micro-enrichment (~40-50 bytes per tool on log.id and HelixJobId descriptions). Leave 12 low-value/already-minimal tools unchanged.

**Bucket 1 (KEEP):** azdo_timeline (1,123 bytes) — critical for log.id extraction to chain to azdo_log; azdo_helix_jobs (550 bytes) — bridge from AzDO to Helix; azdo_build_analysis (539 bytes) — discriminated known/unmatched failures structure.

**Bucket 2 (FLATTEN):** 10 tools, 5,541 bytes. Field names self-descriptive; no downstream extraction ambiguity.

**Bucket 3 (NO-OP):** 12 tools already minimal or degenerate (68-byte LimitedResults wrappers, string-only returns).

**Implementation:** (1) Flatten 10 tools (single PR), (2) optionally enrich 2 field descriptions, (3) leave 68-byte cluster alone.

**Measurement:** Current tools/list = 30,056 bytes authoritative (inputSchema 12,104; outputSchema 8,961; 20/25 structured). Blanket flatten would save 28% (~8,550 bytes); tiered saves 18% (~5,450 bytes) while retaining extraction-critical schema.

---

### 2026-05-30T11:48:09-05:00: User directive — wire format changes for tool names
**By:** Larry (via Copilot)
**What:** General rule: don't worry too much about wire format changes (tool renames, alias additions) because we encourage agents to make semantic connections, not memorize tool names. Renaming `helix_status` → `helix_workitems` and similar discoverability fixes are fine; the cost is low.
**Why:** Discoverability of the right tool matters more than backward-compatible tool names. Validation: cross-check by searching dotnet org for hard-coded `hlx-*` / `helix_*` tool name references — if few/none, the rule is holding.

---

## Archive

See `archive/` for dated snapshots.

