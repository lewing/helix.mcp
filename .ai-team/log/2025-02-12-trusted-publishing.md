# Session: 2025-02-12-trusted-publishing

**Date:** 2025-02-12
**Requested by:** Larry Ewing

## Summary

Ripley created `.github/workflows/publish.yml` for NuGet Trusted Publishing.
- Uses OIDC via `NuGet/login@v1` — no long-lived API keys
- Triggers on `v*` tag push
- Packs the project, creates a GitHub Release with nupkg attached, pushes to nuget.org
- Pattern adapted from baronfel/mcp-binlog-tool

## Decisions

- Adopted NuGet Trusted Publishing (OIDC) over traditional API key secrets
- Workflow mirrors CI's .NET 10 preview SDK setup for consistency
- Changelog support intentionally deferred
