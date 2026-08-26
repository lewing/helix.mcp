# Decisions

**Last updated:** 2026-08-26T17:16:50Z
**Merge cycle:** 2026-08-26T17:16:50Z (Scribe snapshot-hardening batch merge)

---

## Active Decisions

### 2026-08-26T12:00:00-05:00: Snapshot Export Hardening Design & Review Gate

**By:** Dallas (Lead)  
**Status:** Approved — Complete implementation gate cleared

#### Design Summary

Successor PR to PR #125 will keep existing snapshot layout (cache.db + artifacts/) and replace checkpoint-plus-file-copy with SQLite's online backup API.

**Database Backup:** Use `SqliteConnection.BackupDatabase()` with unpooled source/destination connections, finite busy timeout, cancellation checks around the synchronous backup call, and mandatory `PRAGMA journal_mode=DELETE`. Exporter must not checkpoint the live database, copy sidecars, or clear connection pools.

**Artifact Selection:** Query the backed-up `cache.db` for artifact rows; copy only distinct referenced files. Reject empty/rooted paths, resolve lexically, detect traversal/escaping, open with denial of replacement on Windows, verify copied size, and omit orphan files.

**Destination Boundary:** Normalize and canonicalize source and destination paths, walk all components for symlinks/junctions (bounded, fail-closed), check destination is not equal to or below source root or artifacts root, and apply Windows case-insensitive comparison. Containment checks complete before temp directory creation.

**Validation:** Run `SnapshotValidator` after artifact copying. Check for WAL/SHM sidecars, run `PRAGMA integrity_check`, verify schema, validate all artifact rows exist with correct sizes, and perform exact missing-file accounting (traversal errors do not increment count). Temporary snapshot must be valid before final `Directory.Move`.

**Auth Warning:** Unconditional warning stating environment-keyed entries are replayable when eval uses identical `AZDO_TOKEN` and token classification; Azure CLI identities remain unreproducible.

#### Test Plan

- **Concurrency stress:** Writer task commits stress metadata; checkpointer runs `PRAGMA wal_checkpoint(PASSIVE)`. Prove at least one committed write and one actual checkpoint (non-zero WAL/checkpointed page counts). Export several snapshots during both loops; verify integrity_check, transactional consistency, exact seeded metadata, validator success, no sidecars. All tests use bounded timeouts.
- **Containment matrix:** Destination = source root, destination = artifacts/, child of source root/artifacts/, equivalent `.`/`..` paths, case-only alias (rejected on Windows, distinct on Ubuntu), symlinked/junctioned parent, source via link/junction, dangling/cyclic link (all fail-closed). For rejections, assert destination absent, no temp sibling, source unchanged.
- **Artifact tests:** Only backed-up rows copied (orphans omitted), missing file fails export cleanly, size mismatch fails, empty artifacts create artifacts/ directory, success leaves no temp sibling, failure leaves no temp sibling, pre-existing destination untouched, final snapshots contain only cache.db + artifacts/ + no sidecars.
- **Validator accounting:** Traversal-only invalid (MissingArtifactFiles == 0), mixed traversal + one missing file (both errors present, count == 1), present files (count == 0).
- **Auth output:** Run with null hashes; warning must appear with AZDO_TOKEN, AZDO_TOKEN_TYPE, environment-key replay, Azure CLI limitation. No checkpoint/sidecar-copy wording.
- **Commands:** `DOTNET_ROLL_FORWARD=Major` for targeted and full suite; GitHub Actions on Ubuntu and Windows.

#### Acceptance Verdicts (2026-08-26)

**Ripley (SnapshotExporter + SnapshotValidator): ACCEPTED**
- Uses SQLite online-backup API with unpooled connections, finite busy timeout, cancellation checks, no live DB/sidecar copy
- Connections/streams disposed before final rename; artifacts from backed-up DB; physical path resolution fails closed; size checks; temp validation before publication; cleanup preserves existing destination
- Validator performs integrity check, schema, sidecar, containment, existence, size, exact missing-file accounting; no global pool clears

**Kane (SnapshotCommands): ACCEPTED**
- Warning unconditional; describes unchanged auth-scoped keys, environment-only replay, AZDO_TOKEN_TYPE role, Azure CLI limitation, anonymous replay
- Obsolete checkpoint/sidecar-copy claims removed

**Lambert (SnapshotExportTests): REJECTED**
- Checkpoint readiness gate did not prove an actual checkpoint occurred; must read WAL/checkpointed page counts and verify positive
- Seeded non-stress invariant was only row-count assertion; must verify exact keys and JSON values
- Escalation required: recruit independent .NET concurrency/filesystem test specialist (not Lambert, Ripley, Kane, or Dallas)

**Parker (revised SnapshotExportTests): REJECTED**
- Corrected checkpoint gate and seeded invariant checks, but checkpoint loop flaky on shutdown
- When writer closes and no WAL present, SQLite reports -1 for WAL/checkpointed page counts
- Test threw instead of handling as non-progress or coordinating shutdown
- 46 of 48 stress runs passed; 2 failed at "Unexpected WAL page count: -1"
- Parker also locked out; escalation required

**Bishop (final SnapshotExportTests): APPROVED**
- Narrow revision accepts SQLite's exact no-current-WAL response (-1 WAL, -1 checkpointed pages, not busy) only after readiness already completed
- Treats response as non-progress without teardown race
- Proof gates intact: writer signal follows commit; readiness requires successful checkpoint with positive page counts; no-WAL response before readiness still fails
- All negative/inconsistent combinations still fail
- Cancellation/teardown bounded by finite SQLite busy handling and 10-second background task wait
- All 29 targeted tests passed; 48 consecutive stress-test repetitions passed

**Gate Status:** Complete. Core artifacts (Ripley) frozen. CLI wording (Kane) finalized. Test revision (Bishop) cleared for full suite and Ubuntu/Windows CI validation.

#### File Ownership (Frozen)

**Ripley:** `SnapshotExporter.cs`, `SnapshotValidator.cs`, `SnapshotValidationResult.cs` (unchanged)  
**Kane:** `SnapshotCommands.cs`  
**Bishop:** `SnapshotExportTests.cs` (final revision; no further changes)  

Frozen files with no change expected: `SqliteCacheStore.cs`, `CacheOptions.cs`, `Program.cs`, project files, Directory.Packages.props

---

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

