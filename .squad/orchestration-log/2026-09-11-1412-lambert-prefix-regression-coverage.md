---
timestamp: 2026-09-11T14:12:00.000-05:00
agent: Lambert
role: Test Engineer
issue: #130
phase: pre-fix regression coverage
scope: deterministic pool-scope and artifact-source tests
---

# Lambert — Pre-Fix Regression Coverage for #130

**Role:** Test Engineer, pre-fix regression coverage

**Assignment:** Land deterministic regression coverage before either production fix lands, so CI proves the defects first and proves the fixes second in the same PR. Production code was read but not touched.

**Tests Added (7 new facts across 3 authorized files):**

**SqliteCacheStoreConcurrencyTests.cs:**
- `IndependentRoots_DisposeA_BStaysUsable_ARootDeletesCleanly` — two Guid-unique roots, warms both, disposes A, proves B remains fully read/write-usable, then asserts A's own root directory deletes cleanly with unswallowed exceptions.
- `IndependentRoots_DisposeA_WindowsExclusiveOpenOfB_StillFails_BecauseBPoolUntouched` (`[WindowsOnlyFact]`) — the discriminator: warms B (idle pooled native handle), disposes independent A, asserts exclusive `FileShare.None` open of B's `cache.db` still throws `IOException`. Pre-fix, A's global `ClearAllPools()` also evicts B's unrelated pool. Explicitly framed as a handle/share-policy invariant, not a temporal-race assertion.
- Removed `SqliteConnection.ClearAllPools()` finally-block call from `StartupMaintenance_NonCancellationWorkerFault_PropagatesThroughAwaitAndDispose` per assignment.

**SnapshotExportTests.cs:**
- `SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize` — opens artifact with exporter's `ArtifactSourceFileShare` constant, calls `SetArtifactAsync` for same key with different content while handle remains open; asserts replacement. Expected RED on Windows while constant is `FileShare.Read`.
- `Export_WhileNormalArtifactReadStreamRemainsOpen_SucceedsWithExactBytesSizeAndHash` — keeps live `GetArtifactAsync` stream open across full `SnapshotExporter.ExportAsync`; verifies exact bytes, size, and SHA-256 hash. OS-agnostic positive concurrency regression.
- `Validate_PublishedSnapshot_WhileEvalModeStoreRemainsOpen_SucceedsWithNoWritesOrSidecars` — real exported snapshot, opened by genuine eval-mode `SqliteCacheStore` across `SnapshotValidator.ValidateAsync`; asserts validation succeeds and on-disk layout stays `{cache.db, artifacts}`.

**WindowsOnlyFactAttribute.cs:**
- New shared file — generic Windows-only `FactAttribute` following established pattern. Skip set in constructor when `!OperatingSystem.IsWindows()`.

**AzdoEvidenceSurfaceTests.cs:**
- Removed redundant `SqliteConnection.ClearAllPools()` call from `CliEvidencePlan_KeepAttemptPrefixFlag_ReachesSerializedPlan`'s `finally` block per assignment.

**Validation (Unix machine):**
- Targeted filter: 187 passed, 7 skipped (Windows-only facts on Unix), 0 failed
- Full suite baseline maintained: 1981 passed, 8 pre-existing skips, 0 failed
- No production code changed

**Status:** COMPLETED
**Outcome:** Pre-fix regression coverage delivered; defects now testably observable before fixes land.
