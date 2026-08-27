# Decisions

**Last updated:** 2026-08-26T19:04:54Z
**Merge cycle:** 2026-08-26T19:04:54Z (Scribe PR #127 second-review cycle merge)

---

## Active Decisions

### 2026-08-26T19:04:54-05:00: PR #127 Snapshot Export Hardening — Second Review & Approval Gate

**By:** Dallas (Lead)  
**Status:** APPROVED — Independent revision gate cleared

#### Triage (Second Review): REJECTED at 2026-08-26T12:00-05:00

All four findings are production or test-gate blockers:

1. **Production boundary blocker:** macOS case-insensitive filesystem allows case-only spelling aliasing; `SnapshotExporter` incorrectly treats `cache` and `CACHE` as unrelated on the default case-insensitive volume. This breaks containment when source is at `cache` and destination below `CACHE`.

2. **Exporter regression coverage blocker:** Hardening rewrite removed rejection scenarios for missing source root, missing `cache.db`, missing destination parent, schema zero, unsupported schema, and missing table. Existing artifact and alias tests do not cover those contracts.

3. **Validator regression coverage blocker:** Test suite no longer proves rejection of missing snapshot directory, missing `cache.db`, wrong schema, missing table, or database corruption (new integrity check).

4. **Current-focus record blocker:** Scribe-authored `.squad/identity/now.md` incorrectly reported full suite and CI as pending when 1,661 local tests and refreshed checks had passed.

#### Assignments

- **Frost** (filesystem-security specialist; Ripley locked): Fix macOS case containment while preserving separator-aware checks and case-sensitive filesystem behavior.
- **Hudson** (independent test specialist; Lambert/Parker/Bishop/Burke locked): Restore all exporter/validator rejection scenarios and add deterministic corruption coverage; ensure case-only test runs meaningfully on macOS.
- **Kane** (approved focus author): Correct current-focus record to acknowledge prior completion while reopening gate.

#### Recheck (Second Review): APPROVED at 2026-08-26T18:40-05:00

**Frost's exporter revision:** Boundary equality and descendant checks ignore case on Windows and macOS; remain ordinal on Linux. Destination-parent identity recheck uses same rule. Initial containment completes before temp directory or database creation. Distinct case-only siblings remain permitted on ordinal platforms.

**Hudson's exporter + validator revisions:** Case-only regression no longer skips macOS; detects whether alias is true (rejects with unchanged source and publication state) or distinct (exports successfully, preserves source). Exporter rejection scenarios verify focused diagnostics and residue; applicable cases verify source integrity. Missing-parent case proves no parent created. Validator covers missing snapshot, database, wrong schema, missing table, deterministic corruption (integrity check returns non-OK, validation returns invalid result without throwing, reports diagnostic). Corruption passed ten additional repetitions. Existing boundary, sidecar, traversal, missing-file, and size coverage remains intact.

**Kane's focus record:** Accurately acknowledges prior 1,661-test local gate and refreshed checks, reopened review gate, assigned revisions, and required fresh full-suite and Ubuntu/Windows CI runs.

**Gate Status:** All 43 focused `SnapshotExportTests` passed with no skips or failures (DOTNET_ROLL_FORWARD=Major). Independent revision gate cleared. PR #127 ready for full local suite and fresh Ubuntu/Windows CI validation.

#### File Ownership (Locked)

**Frost:** `SnapshotExporter.cs` (macOS case containment fix)  
**Hudson:** `SnapshotExportTests.cs`, `SnapshotValidator.cs` (negative path and corruption coverage)  
**Kane:** `.squad/identity/now.md` (approved focus record — mechanical preservation only)

---

### 2026-08-26T19:04:54-05:00: macOS Snapshot Export Boundary Containment Policy

**By:** Frost  
**Status:** APPROVED — In effect

Snapshot-export security boundaries use ordinal ignore-case comparison on Windows and macOS, and ordinal comparison on Linux and other platforms. macOS is intentionally conservative: case-sensitive volume may reject distinct case-only sibling, but case-only alias on default case-insensitive filesystem cannot bypass source containment. Equality, separator-bounded descendant checks, and destination-parent identity rechecks share these semantics.

---

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

### 2026-08-26T18:29:40.518-05:00: macOS Snapshot Export Comparison Policy
**By:** Kane
**Status:** APPROVED — Supersedes prior macOS policy

Supersedes the 2026-08-26 macOS policy. Conservative destination deny-list checks use ordinal ignore-case on Windows/macOS and ordinal elsewhere. Positive artifact-containment, validator-containment, and destination-parent identity proofs use ordinal ignore-case only on Windows and ordinal on macOS/Linux. Thus macOS deny-list checks remain conservative, while case-only distinct paths do not prove containment or identity on case-sensitive volumes.

---

## Archive

See `archive/` for dated snapshots.

