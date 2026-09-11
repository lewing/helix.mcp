---
timestamp: 2026-09-11T15:40:00.000-05:00
agent: Lambert
role: Test Engineer
issue: "#130"
phase: full regression coverage
scope: "7 pre-fix facts + final share-policy validation"
---

# Lambert — Final Regression Coverage (#130)

**Role:** Test Engineer, full pre-fix and post-fix regression coverage

**Assignment:** Land deterministic regression coverage proving both defects pre-fix; validate production fixes post-fix with focus on share-policy concurrent-open scenarios

**Coverage Delivered:**

**Pre-Fix Regression Tests (7 facts):**
- Pool-scope discriminator: `IndependentRoots_DisposeA_WindowsExclusiveOpenOfB_StillFails_BecauseBPoolUntouched` — RED pre-fix on Windows (as expected)
- Artifact-source share policy: `SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize` — RED pre-fix on Windows due to sharing violation
- Concurrent eval-mode validator: `Validate_PublishedSnapshot_WhileEvalModeStoreRemainsOpen_SucceedsWithNoWritesOrSidecars`
- Positive regression: `Export_WhileNormalArtifactReadStreamRemainsOpen_SucceedsWithExactBytesSizeAndHash`

**Post-Fix Validation:**
- Windows discriminator now GREEN (pool-scope fix validates)
- Artifact-replacement test still RED — scope amendment proved necessary
- File.Replace + FileShare.Read|Delete final fix turns artifact-replacement test GREEN
- All 387 targeted tests now passing (including Windows-only facts on Windows CI)

**Full Suite Status:**
- Local: 1995 passed, 9 skipped, 0 failed
- Windows CI: success
- Ubuntu CI: success

**Learning:** Silent catch mechanism in original `SetArtifactAsync` masked the sharing violation on Windows until File.Replace logic was explicitly added — test design correctly exposed the hidden defect.

**Status:** COMPLETED
**Outcome:** Full pre-fix and post-fix regression coverage delivered; both production fixes validated green across all platforms
