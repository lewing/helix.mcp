---
session_id: scribe-2026-09-11-issue-130-pre-fix-evidence
timestamp: 2026-09-11T14:20:00.000-05:00
issue: #130
scope: pre-fix evidence preparation and regression coverage
---

# Issue #130 — Pre-Fix Evidence Preparation (SQLite Pool Scope & Artifact Source Share)

**Orchestration Complete**

Three agents delivered pre-fix evidence and regression coverage for the two distinct production defects identified in #130 design review:

- **Ripley** (Backend Dev): Artifact-source FileShare seam introduction (behavior-neutral constant, future-fix-ready)
- **Lambert** (Tester): Pre-fix regression coverage across pool-scope isolation and artifact-source sharing (7 new deterministic facts, Windows discriminators included)
- **Dallas** (Lead): Independent sync merge review, APPROVED with no blocking findings

**Coverage Delivered:**

**Pre-Fix Regression Tests (187 passed, 7 skipped, 0 failed):**
- `SqliteCacheStoreConcurrencyTests`: independent-roots pool-scope discriminator (Windows-only); manual pool-eviction cleanup to expose defect
- `SnapshotExportTests`: concurrent artifact overwrite while exporter holds source handle (Windows defect observable); concurrent eval-mode validator read (positive regression baseline)
- `WindowsOnlyFactAttribute`: new reusable test marker for platform-specific discriminators
- `AzdoEvidenceSurfaceTests`: cleanup manual call removal per architecture

**Seam Strategy:**

Ripley's artifact-source `FileShare` constant lands at current value (`FileShare.Read`), allowing Lambert's Windows discriminator test to compile now and fail pre-fix. The value change to `FileShare.Read | FileShare.Delete` is deferred to a dedicated fix-step commit, keeping diff minimal and isolating the Windows-red-to-green transition to one atomic change.

**Architecture Verified:**

One-PR model with two scoped fixes:
1. `SqliteCacheStore.Dispose()` clearing scoped to the disposing store's connection string (not process-global)
2. `SnapshotExporter.CopyArtifactAsync` source handle share-policy set to `FileShare.Read | FileShare.Delete` (permissive for concurrent eviction on Windows)

No retries, no serialization, no public API changes.

**Remaining Work:**

Production fixes themselves land in follow-up commits, with these pre-fix tests now provably failing on Windows CI until each fix is applied. Full test suite baseline (1981 passed, 8 pre-existing skips, 0 failed) is maintained.

**Status:** APPROVED
**Outcome:** Pre-fix evidence complete and ready to merge. No rework requested.
