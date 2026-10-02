---
session: evidence-plan-helix-and-scanner-gaps
date: 2026-10-02T12:05:00Z
participants: Dallas, Ripley, Lambert, Kane, Ash
outcomes: 3 approved/for-review, 1 proposed, 5 inbox merged
---

# Session log: Evidence plan Helix and scanner gaps

## Summary

Five-agent session completed evidence-plan Helix monitor awareness implementation (commit 28beceb, 2047 tests pass), acquired #152 discovery decisions, and positioned scanner work behind error-contract phasing.

## Decisions merged to decisions.md (inbox → main)

1. **Helix-aware evidence plan** (Dallas, for-review)
   - Parsed arcade monitor failures as `helixFailures[]` timeline extension
   - Stable `incompleteDetails[].code`, `suggestedFetches[]`, paging fields
   - Design artifact: evidence-plan-helix-design.md

2. **Acquisition error contract** (Dallas, proposed)
   - Core `HlxAcquisitionException` with `AcquisitionError` record
   - Stable kind strings: `not_found`, `access_denied`, `rate_limited`, `timeout`, `transport_error`, `invalid_response`
   - MCP filter + CLI JSON envelope for #152
   - Design artifact: acquisition-errors-design.md

3. **Scanner scenarios analysis** (Ash, proposed)
   - Deterministic collection requirements: stable errors, JSON completeness, exit codes
   - Phasing: acquisition errors → CLI pagination → `hlx collect` bundle writer
   - Analysis artifact: scanner-scenarios-gap-analysis.md (gist)

4. **Test status: Helix evidence plan** (Lambert, for-review)
   - 255 targeted / 2 failed; 2045 full suite / 2 failed / 9 skipped
   - Gap: missing stable reason codes in human CLI output (2 tests)
   - Design requires these codes; Ripley's implementation omitted them

5. **Documentation scope note** (Ripley, resolved)
   - Core + XML comments + MCP descriptions only
   - README/user-facing docs deferred per charter

## Outstanding

- Lambert's 2 failing tests signal design-implementation gap (reason codes in human output)
- Dallas decision requires codes; Ripley's delivery omitted them
- Resolution needed before merge: add codes to human output or revise design doc

## Cross-references

- Issue #152 (acquisition errors + scanner)
- Commit 28beceb (evidence-plan implementation)
- Files/evidence-plan-helix-design.md
- Files/acquisition-errors-design.md
- Files/scanner-scenarios-gap-analysis.md
