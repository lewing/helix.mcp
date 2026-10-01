# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, strict-mode architecture, and merge-gate reviews.

**Current Focus:** Recent merge-gate work centered on startup cache eviction (#129) and pool/artifact replacement (#130). Full pre-condense detail was archived to `history-archive.md` on 2026-10-01T00:45:00Z.

## Durable Decision Principles

- Reframe issue titles into root-cause defect bundles before assigning implementation; tracking a task is not enough if its cutoff/time semantics remain wrong.
- Make minimal-public-impact requirements mechanically checkable with exact file boundaries.
- Prefer narrow enumerated exception handling over prose rules like "avoid broad swallowing."
- Ban race-outcome assertions; tests should prove deterministic end-state and observable contracts.
- Treat changed SDK defaults as silent diffs: force important defaults into source and guard with tests.
- Verify agent-reported release metadata and claimed platform constraints against primary sources or repo greps.
- Evidence-driven scope amendments are valid when red/green tests disprove an architectural assumption.

## Recent Decision Cycles

### 2026-09-08 — v0.10.0 release prep

Prepared release commit `00c18d2` on `lewing-release-v0-10-0`; bumped the three authoritative version surfaces and promoted CHANGELOG. Build, test, and pack validation succeeded. No tag/push was performed.

### 2026-09-11 — Startup cache eviction lifecycle (#129)

Accepted a design with construction-time cutoff pinning, retained internal `StartupMaintenance`, cancel → bounded join → fault observation disposal, and deterministic tests with no public hooks or race-outcome assertions. Approved Ripley/Lambert/Kane results after full validation.

### 2026-09-11 — Pool scope and artifact replacement (#130)

Approved pre-fix regression coverage, then accepted an evidence-driven scope amendment when Windows tests proved `File.Move(overwrite: true)` insufficient despite permissive source share. Final accepted fix: scoped SQLite pool cleanup plus `File.Replace` for existing artifacts and `File.Move` for absent artifacts. Validation: targeted and full suites green; Ubuntu, Windows, and Squad CI success.

## Standing Architecture Context

- Stable folders: `src/HelixTool.Core/{Helix,AzDO,Cache}/` and `src/HelixTool.Mcp.Tools/{Helix,AzDO}/`.
- Keep business logic in services, MCP tools thin, and caching/offline behavior behind decorators.
- AzDO/Helix API clients should maintain strict input validation, cache isolation by auth context, and explicit auth failure surfacing.
