# Session Log: 2026-06-24 — PR #78 AzDO Param Plumbing + Follow-up PRs & Issues

**Session Date:** 2026-06-24  
**Agents Spawned:** 9 (ash, ripley, ash-feasibility ×2, ripley ×4, duck)  
**PRs Shipped:** #78, #79, #80  
**Issues Filed:** #81, #82  
**Status:** ✅ Complete

## Executive Summary

User report about `minFinishTime`/`maxFinishTime` being silently ignored on AzDO builds triggered a comprehensive audit. Ash discovered three end-to-end parameter plumbing bugs. Ripley implemented all three in PR #78 with four review rounds addressing whitespace normalization, cache key canonicalization, and API stability. MCP and SQLite dependency upgrades shipped in parallel. A rubber-duck review clarified architectural principles. Two follow-up issues filed for future work.

## The User Report

Unknown user (via Copilot or live session) reported that `minFinishTime` and `maxFinishTime` parameters on the `azdo_builds` tool were not filtering builds as expected. Investigation revealed these parameters were silently ignored.

## Audit (Ash, ~20:34 UTC)

Ash conducted a comprehensive audit comparing MCP tool parameter signatures against underlying REST API capabilities. Found **three distinct bugs**:

1. **azdo_builds**: `minTime`, `maxTime`, `queryOrder` not exposed in MCP tool or CLI (REST API supports them)
2. **azdo_test_attachments**: `top` parameter accepted but never forwarded to REST URL
3. **azdo_test_results**: `outcomes` filter hardcoded to `Failed` (REST API supports other values)

All three are silent failures: parameters either don't exist or don't reach the server. Caller sees no error, just filtered results.

## PR #78 Implementation (Ripley, ~20:37–21:52 UTC)

Ripley implemented all three bugfixes in a single PR with four Copilot review rounds:

### Round 1: Whitespace Normalization
- Applied `IsNullOrWhiteSpace()` + `Trim()` for optional params with server-side defaults
- Prevents `outcomes = "   "` from reaching the REST API
- Cache keys normalized to avoid fragmentation

### Round 2: Cache Key Semantics
- `null` and explicit server default must hash identically
- Extracted `AzdoApiClient.DefaultQueryOrder` constant
- CLI error handling: set `Environment.ExitCode = 1` on invalid input

### Round 3: Boundary Normalization
- HTTP client layer must self-protect (independent of entry-point validation)
- Centralized canonicalization algorithm (one source of truth)

### Round 4: Public API Surface
- Changed `public static readonly string[]` to `IReadOnlyList<string>` (prevents mutations)
- Lowercased queryOrder in cache keys (AzDO treats it case-insensitively)
- Verified IAzdoApiClient stability (backward-compatible)

**Test Coverage:** 14 new tests added. All 1337 tests pass.

## Architectural Insight (Duck Rubber-Duck Review)

Duck's critique flagged a principle clarification: "normalize at EVERY layer" (round 3 advice) leads to algorithm duplication and drift. **Correct principle:**

- Validate at input boundaries (CLI/MCP) — early, useful errors
- Canonicalize at semantic boundaries (cache, URL) — where values are consumed
- Centralize the algorithm — prevent drift

This insight is not a flaw in PR #78 but a larger architectural pattern. Follow-up issue #82 filed to unify `NormalizeFilter`, `NormalizeQueryOrder`, `NormalizeOutcomes` under a single extensible contract.

## Parallel Work: MCP 1.4.0 Upgrade (PR #79)

Ash-Feasibility verified that MCP 1.4.0 is safe to upgrade (UnmappedMemberHandling.Disallow available in both 1.3.0 and 1.4.0). PR #79 shipped with no issues. Enables future strict parameter validation (issue #81).

## Parallel Work: SQLite CVE-2025-6965 (PR #80)

Pinned SQLitePCLRaw 3.x to address CVE-2025-6965. Eliminated 10 NU1903 warnings (NuGet packages with outdated dependencies).

## Follow-up Issues Filed

### Issue #81: Strict Unknown-Parameter Rejection
- **Stage A (immediate):** Apply UnmappedMemberHandling.Disallow as catch-all
- **Stage B (future):** CallToolFilter with "did you mean?" suggestions

### Issue #82: AzDO Filter Normalization Centralization
- Unify `NormalizeFilter` + `NormalizeQueryOrder` + `NormalizeOutcomes`
- Stable cache-key serialization contract
- Contract-test pattern (verify all filters follow the same rules)

## Metrics

| Metric | Value |
|--------|-------|
| Orchestration log entries written | 9 |
| PRs shipped this session | 3 (#78, #79, #80) |
| Issues filed | 2 (#81, #82) |
| Test cases added (PR #78) | 14 |
| Tests passing (final) | 1337 |
| Copilot review rounds (PR #78) | 4 |
| Bugs fixed | 3 (minTime/maxTime/queryOrder, top forwarding, outcomes filter) |

## Timeline

- ~20:34 UTC: Ash audit begins
- ~20:37 UTC: Ripley starts implementation
- ~20:43 UTC: Ash-feasibility study on strict param validation
- ~20:57 UTC: Ash-feasibility turn 2 (MCP 1.4.0 verification)
- ~21:02 UTC: Ripley round 1 review (whitespace)
- ~21:25 UTC: Ripley round 2 review (cache semantics)
- ~21:41 UTC: Ripley round 3 review (boundary normalization)
- ~21:46 UTC: Duck architectural review
- ~21:52 UTC: Ripley round 4 review (final API stability)

## Status

✅ All work complete. PR #78 merged. PRs #79 and #80 shipped. Follow-up issues filed and prioritized.
