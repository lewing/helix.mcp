# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, and test architecture.

**Current Focus:** Issue #149 regression coverage is complete and committed with Ripley's fix as `b47deae` on branch `lewing-fix-test-results-failures-hidden`. Full pre-condense detail was archived to `history-archive.md` on 2026-10-01T00:45:00Z.

## Durable Test Patterns

- Prefer deterministic observable end-state over sleeps, retries, or race-outcome assertions. If a background task is part of the contract, expose/await an internal completion handle rather than adding public hooks.
- Public APIs can mask low-level behavior under test; inspect SQLite rows directly when API TTL filters would hide whether startup cleanup actually deleted data.
- Disk-to-disk SQLite backup can copy WAL journal-mode headers; force `journal_mode=DELETE` when a clean snapshot fixture is required.
- Regression-first methodology is valuable for platform defects: write a Windows-only discriminator that fails pre-fix and turns green post-fix, with `[WindowsOnlyFact]` reporting a real skip on non-Windows platforms.
- Do not use test seams or global mutable hooks when existing reachable paths can produce the required failure deterministically.
- Keep assertion data in one fixture source when integration and unit tests share a scenario.

## Recent Completed Work

### 2026-09-11 — Startup cache eviction lifecycle tests (#129)

Added deterministic coverage for `SqliteCacheStore.StartupMaintenance`, disposal joining, eval-mode no-op behavior, stale comment cleanup, and non-cancellation worker-fault propagation. Full suite reached 1990 passed / 8 skipped after repeated validation.

### 2026-09-11 — Pool scope and artifact replacement coverage (#130)

Added pre-fix and post-fix regression coverage for cross-root SQLite pool interference and Windows artifact replacement. The artifact test proved permissive source share alone was insufficient and drove the accepted `File.Replace` fix. Final validation: targeted 387 passed / 7 skipped; full suite 1995 passed / 9 skipped; CI green.

### 2026-09-30 — AzDO test-results silent-empty regressions (#149)

Added `src/HelixTool.Tests/AzDO/TestResultsSilentEmptyTests.cs` with 15 focused tests: real AzDO run JSON maps `unanalyzedTests` to `FailedTests`; 203/text-html/302 sign-in responses assert auth guidance across list and non-list paths; test-run result 404s assert explicit not-found/deleted errors; caching wrappers do not cache auth exceptions; and request URLs prove `$top` never exceeds 10,000. Updated stale 404 expectation to the new contract. Final validation: new class 15/15 passed; AzDO namespace 1009 passed / 2 skipped; full suite 2016 passed / 9 skipped.

## Environment Note

Local runtime may be .NET 11 preview only while projects target `net10.0`; use `DOTNET_ROLL_FORWARD=Major` for builds/tests when needed.

## Learnings

### 2026-10-02T11:29:27-05:00 — Helix evidence-plan monitor coverage

- Live public timelines are compact enough to distill into deterministic unit fixtures: dotnet/runtime build 1621192 is monitor-only with a `Monitor Helix Jobs` warning for `System.Diagnostics.Process.Tests`; build 1621133 combines the same monitor shape with a canceled `osx-arm64 Debug Libraries_CheckedCoreCLR` leg and a real `Logs_Build_Attempt1_osx__arm64_Debug_Libraries_CheckedCoreCLR` artifact.
- Human CLI output needs explicit `incompleteDetails[].code` rendering, not just human `incompleteReasons`; otherwise deterministic collector failures like `monitor_unparseable` and `helix_failures_truncated` are invisible in non-JSON mode even when JSON is machine-readable.
- Parser tests should cover direct GUID extraction, console fallback scoping, tree-line parsing, and warning/tree dedupe separately from service tests; that isolates production parsing bugs from evidence-plan wiring bugs.

## 2026-10-02T12:05:00Z — Session handoff: Test findings + #152 phasing

Cross-agent context from Scribe:

**For Lambert (Testing):**
- Your evidence-plan Helix tests are high quality (255 targeted / 2 failed); the 2 failures are implementation gaps, not test bugs
- Missing stable reason codes in human CLI output: Dallas's design requires them for deterministic collectors (`[monitor_unparseable]`, `[helix_failures_truncated]`)
- Ripley's implementation calculated codes but did not emit them in human formatter
- Recommendation: flag both test failures as "design-implementation gap" for Ripley to fix before merge
- Dallas approved acquisition error contract (#152) in parallel; your error test patterns will scale to MCP + CLI JSON envelopes for acquisition failures
- Phasing: error contract first, then scanner pagination/completeness, then bundle writer

**Parallel work:**
- Ash's scanner analysis aligns with Dallas's #152 phasing recommendation
- All decisions merged and ready for Larry's review
