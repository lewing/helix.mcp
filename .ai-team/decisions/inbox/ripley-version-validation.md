# Decision: CI version validation in publish workflow

**Decided by:** Ripley  
**Date:** 2025-07-23  
**Status:** Implemented

## Context

The publish workflow triggers on `v*` tags and extracts the version from the tag. Previously, nothing verified that the csproj and server.json versions matched the tag, and `dotnet pack` used whatever version was in the csproj.

## Decision

1. **Tag is source of truth for package version.** The Pack step now passes `/p:Version=` from the tag, overriding the csproj value.
2. **Validation step before Pack** checks that `HelixTool.csproj <Version>`, `server.json .version`, and `server.json .packages[0].version` all match the tag. Fails the build on mismatch with clear error messages.
3. **Belt-and-suspenders approach:** validation catches mistakes early for developers, `/p:Version=` override ensures the published artifact is always correct.

## Impact

- All team members must update csproj and server.json version fields before tagging a release.
- The workflow will fail fast with actionable error messages if versions are out of sync.
- No CI workflow exists yet (`ci.yml`), so validation is only in the publish workflow.
