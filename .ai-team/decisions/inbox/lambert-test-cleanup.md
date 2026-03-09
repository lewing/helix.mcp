### 2025-07-18: Proactive test files should be reviewed for duplication when production tests land
**By:** Lambert
**What:** When a proactive test file (written before production code) overlaps with a later "real" test file, the proactive tests should be pruned during the PR that adds the real tests — not left to accumulate as duplicates.
**Why:** AzdoCliCommandTests.cs contained 19 near-duplicate tests of AzdoServiceTests because it was written speculatively and never reconciled. Catching this at PR review time avoids test bloat and the false sense of coverage depth that duplicates create.
