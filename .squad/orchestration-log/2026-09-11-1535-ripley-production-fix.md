---
timestamp: 2026-09-11T15:35:00.000-05:00
agent: Ripley
role: Backend Developer
issue: "#130"
phase: production implementation
commits:
  - c58340d: "scoped ClearPool + FileShare.Read|Delete"
  - 10149cf: "File.Replace vs Move for artifact replacement"
---

# Ripley — Production Fix Implementation (#130)

**Role:** Backend Developer, production implementation

**Assignment:** Implement two distinct fixes for pool-scope isolation and artifact-source sharing

**Delivery:**

**Commit c58340d (Scope + Share Policy):**
- Scoped `SqliteConnection.ClearAllPools()` to the disposing store's exact connection string in `SqliteCacheStore.Dispose()`
- Changed `SnapshotExporter.CopyArtifactAsync` source handle from `FileShare.Read` to `FileShare.Read | FileShare.Delete` (production fix for Windows share-policy defect)
- Result: pool-scope test turned green; artifact-replacement test remained red (proving original plan's MoveFileEx assumption wrong)

**Commit 10149cf (File.Replace Implementation):**
- Replaced `File.Move(..., overwrite: true)` in `SnapshotExporter.SetArtifactAsync` with platform-aware logic:
  - `File.Replace` for existing artifacts (atomic, handles share conflicts on Windows)
  - `File.Move` for absent artifacts (simple case)
- Surfaces publication failures before metadata update to database
- Test covers `ReadWrite|Delete` holders + `FileShare.Read|Delete` holder concurrent open

**Validation:**
- Release build: 0 Warning(s), 0 Error(s)
- Local targeted: 387 passed, 7 skipped, 0 failed
- Local full suite: 1995 passed, 9 skipped, 0 failed
- Windows CI: success
- Ubuntu CI: success

**Status:** COMPLETED
**Outcome:** Both production fixes delivered; scope amendment validated via test turning red-to-green
