---
timestamp: 2026-09-11T14:07:00.000-05:00
agent: Ripley
role: Backend Developer
issue: #130
phase: seam introduction
task: artifact source FileShare constant
---

# Ripley — Artifact Source FileShare Seam (#130)

**Role:** Backend Developer, pre-fix seam introduction

**Assignment:** Introduce a behavior-neutral seam in `SnapshotExporter` ahead of the planned artifact source share-policy fix, so Lambert can add a Windows discriminator test that compiles now and fails pre-fix.

**Delivery:**

Added `private const FileShare ArtifactSourceFileShare = FileShare.Read;` next to existing exporter constants in `src/HelixTool.Core/Cache/SnapshotExporter.cs`, and replaced the inline `FileShare.Read` literal at the live-source `FileStream` open in `CopyArtifactAsync` with this constant. Value is unchanged from current behavior — this is purely a naming/seam commit, not the `FileShare.Read | FileShare.Delete` fix itself.

**Verification:**
- `dotnet build src/HelixTool.Core/HelixTool.Core.csproj`: 0 warnings/0 errors
- `git diff` confirms only changed file is `src/HelixTool.Core/Cache/SnapshotExporter.cs`
- Exact changes: constant addition + single literal-to-constant substitution
- No production file, test, changelog, package, or public API touched

**Learning:** When a plan calls for a named constant purely so a not-yet-written test can compile against it, land the constant with the *current* value first as its own tiny seam, and leave the value change for the dedicated fix step — keeps the pre-fix/post-fix diff for reviewers minimal and isolates the Windows-red-to-green transition to one commit.

**Status:** COMPLETED
**Outcome:** Seam only; behavior fix and tests are out of scope for this task.
