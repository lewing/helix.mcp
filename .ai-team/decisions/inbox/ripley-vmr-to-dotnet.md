# Decision: Rename "vmr" profile key to "dotnet"

**Date:** 2025-07-25
**Author:** Ripley
**Status:** Implemented

## Context

The CI knowledge profile for dotnet/dotnet used `"vmr"` as its dictionary key and `RepoName`. Agents searching for "dotnet" or "dotnet/dotnet" wouldn't find it without a special-case fallback, and the overview listing showed "vmr" which agents don't recognize.

## Decision

- Dictionary key changed from `["vmr"]` to `["dotnet"]`
- `RepoName` changed from `"vmr"` to `"dotnet/dotnet"` (full repo path)
- `DisplayName` kept as `"dotnet/dotnet (VMR)"` — the VMR context is still useful
- Removed the special-case lookup that mapped "dotnet"/"dotnet/dotnet" → vmr — now "dotnet" matches the key directly, and "dotnet/dotnet" resolves via the existing shortName extraction logic (splits on "/" → "dotnet")
- All string literals and doc comments updated from "vmr" to "dotnet" where they referred to the profile key; "VMR" in display text, tips, and stage names was intentionally preserved

## Impact

- `GetProfile("dotnet")` → direct key match (was special-case fallback)
- `GetProfile("dotnet/dotnet")` → shortName extraction match (was special-case fallback)
- `KnownRepos` collection now contains "dotnet" instead of "vmr"
- `GetOverview()` listing shows "dotnet" instead of "vmr"
- `profile.RepoName` is now "dotnet/dotnet" (was "vmr")
- 196 CI knowledge tests pass
