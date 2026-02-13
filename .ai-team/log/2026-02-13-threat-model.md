# Session: 2026-02-13-threat-model

**Requested by:** Larry Ewing

## Summary

Ash produced a STRIDE threat model for lewing.helix.mcp, identifying 16 threats across all 6 STRIDE categories. Dallas reviewed and approved the threat model with one minor amendment.

## Key Findings

| Priority | Severity | Finding |
|----------|----------|---------|
| P0 | High | HTTP MCP server lacks authentication middleware (S1/I4) |
| P1 | Medium | SSRF via `hlx_download_url` accepting arbitrary URLs (E1) |
| P1 | Medium | Unbounded batch size in `GetBatchStatusAsync` (D1) |

## Positive Patterns Noted

- Consistent path traversal protection (`SanitizePathSegment` + `ValidatePathWithinRoot` at all filesystem write sites)
- Parameterized SQL throughout `SqliteCacheStore`
- Token isolation (per-session scoping, `ComputeTokenHash` for cache partitioning)

## Actions

- Ash produced STRIDE threat model → `.ai-team/analysis/threat-model.md`
- Dallas reviewed, verified 15+ line-number references against source — all correct
- Dallas approved with one amendment: S1 recommendation should be "Add auth middleware for non-localhost deployment" (localhost is already the default)
- Dallas noted E1 (URL scheme validation) as a one-liner hardening task for Ripley
- Dallas noted D1 (batch size limit) as a guard for Ripley
- T2 (domain allowlist for download-url) deferred — too restrictive for diagnostic tool

## Artifacts

- `.ai-team/analysis/threat-model.md` — Full STRIDE threat model
- `.ai-team/decisions/inbox/ash-threat-model.md` — Decision record (merged)
- `.ai-team/decisions/inbox/dallas-threat-model-review.md` — Review record (merged)
