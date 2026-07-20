## 2026-06-24: AzDO Param Plumbing — Three Bugs Fixed (fix/azdo-param-plumbing)

### Learnings

**AzDO REST query param names for time range:**
- `minTime` and `maxTime` (ISO 8601 round-trip format, URL-escaped)
- The time field filtered is **determined by queryOrder**, not by minTime/maxTime param names
  (e.g., `queryOrder=finishTimeDescending` → AzDO interprets minTime/maxTime against finish time)
- Valid queryOrder values: `queueTimeAscending`, `queueTimeDescending`, `startTimeAscending`, `startTimeDescending`, `finishTimeAscending`, `finishTimeDescending`

**Class of bug (silent param drop):**
- MCP param binding silently drops unknown args if not present in the tool method signature
- Missing param + missing URL plumbing both produce identical symptom: filter is ignored
- Audit: compare tool method signature with underlying REST API capabilities to catch gaps early

**Three bugs fixed and locations:**
1. `azdo_builds` — `minTime`/`maxTime`/`queryOrder` were absent from `AzdoBuildFilter`, not forwarded to AzDO URL, not exposed on MCP tool or CLI command
   - Files: `AzdoModels.cs`, `AzdoApiClient.cs` (`ListBuildsAsync`), `AzdoService.cs`, `CachingAzdoApiClient.cs`, `AzdoMcpTools.cs`, `Program.cs`
2. `azdo_test_attachments` — `top` param accepted but never forwarded to REST URL (`$top=` missing from `GetTestAttachmentsAsync`)
   - File: `AzdoApiClient.cs` (`GetTestAttachmentsAsync`)
3. `azdo_test_results` — `outcomes` filter hardcoded to `Failed` with no way for caller to override; passing `Passed,Failed` etc. was impossible
   - Files: `IAzdoApiClient.cs`, `AzdoApiClient.cs`, `CachingAzdoApiClient.cs`, `AzdoService.cs`, `AzdoMcpTools.cs`, `Program.cs`

**Pattern applied:**
- `NormalizeQueryOrder` + `IsValidQueryOrder` + `GetInvalidQueryOrderMessage` mirrors existing `NormalizeFilter`/`IsValidFilter` pattern
- `AllowedValues` on MCP tool param + server-side validator + `McpException` on invalid = defense in depth
- Cache key includes new discriminating params (outcomes, QueryOrder, MinTime, MaxTime) to avoid stale cache hits

**Commits:** `fefd0dc` (builds), `a2615df` (attachments top), `cbb35c5` (outcomes)  
**Tests:** 1326 passed, 2 skipped (0 failed) — 14 new tests added  
**Branch:** `fix/azdo-param-plumbing`

## 2026-06-24: PR #78 Copilot Reviewer Feedback — Whitespace normalization (fix/azdo-param-plumbing)

### Learnings

- **Optional string params with server-side defaults:** Always use `IsNullOrWhiteSpace` + `Trim()`, not `IsNullOrEmpty`. Empty or whitespace from a caller should fall back to the default, not produce malformed URLs (`outcomes=%20%20%20`) or distinct cache keys for semantically-identical requests.
- **Both CLI and MCP entry points must validate:** For tools with both CLI and MCP surfaces, normalize and validate at BOTH entry points using the shared helper (e.g., `AzdoService.NormalizeQueryOrder` / `IsValidQueryOrder`). Don't rely on one path to protect the other — a CLI user calling `--query-order " "` hits AzDO with a bad value if only the MCP path validates.
- **Cache key normalization:** In `CachingAzdoApiClient`, normalize once at the top of the method and use the normalized value for both the cache key and the inner-client call. Raw caller input (null vs "" vs "   ") must not produce distinct cache entries for semantically-identical requests.

**Commit:** `aa7dbe8` (whitespace normalization — queryOrder CLI, outcomes trim, caching outcomes)  
**Tests:** 1330 passed, 2 skipped (0 failed) — 4 new tests added  
**Branch:** `fix/azdo-param-plumbing`

## 2026-06-24: PR #78 Second Copilot Review — Cache normalization, exit codes, doc coupling (fix/azdo-param-plumbing)

### Learnings

- **Cache key normalization isn't just for outcomes — any optional param with a server-side default needs the same null-vs-default treatment in the cache layer.** Explicit `"queueTimeDescending"` and `null` are semantically identical (the server applies the same default), but produce different hash strings if you embed the raw value. Always normalize to `null` before hashing when the server would treat them as equivalent.
- **CLI commands MUST set non-zero exit code on invalid input or scripts can't detect failure.** `Environment.ExitCode = 1` before returning is the pattern used throughout this codebase for user input errors. Silent success-on-bad-input (`return` with exit 0) masks failures in CI pipelines and shell scripts.

---

## Summary (archived 17 detailed entries)

**Focus:** PR #78 (AzDO param plumbing & whitespace handling), Issue #81-82 (strict-mode parameter rejection), Issue #91-105 (SDK bumps, container image, HTTP 204 handling).

### Key Architectural Patterns Established

1. **Defense-in-depth for optional params:** Validate at user boundary (CLI/MCP) → Canonicalize at semantic boundary (cache key, URL) → Share algorithm across layers. Do NOT duplicate normalization logic.
2. **Silent param drop detection:** Audit tool method signature vs. REST API capabilities; missing params + missing URL plumbing produce identical symptom (filter ignored).
3. **Cache key stability:** Normalize null/whitespace/default values to identical representations before hashing. `null` and explicit `"queueTimeDescending"` are semantically identical and must share a cache key.
4. **Array safety:** Public validation sets must be `IReadOnlyList<T>` or `FrozenSet<T>`, not `readonly string[]` (readonly doesn't prevent element mutation).
5. **Alias correctness:** When renaming legacy params to canonical names, remove alias key from dict after promotion; without removal, strict-mode rejects the orphaned alias.
6. **Did-you-mean filter:** Levenshtein distance 6 (not 3) needed to catch hallucinated compound names like `minFinishTime` → `minTime` (distance 6).

### Recent Work Summary

- **PR #78:** Fixed 3 AzDO bugs (minTime/maxTime/queryOrder missing; outcomes hardcoded; top param ignored). 14 new tests added.
- **Issue #81 Stage A:** Added `result` → `resultFilter` alias; enabled `UnmappedMemberHandling.Disallow`; removed alias key after promotion.
- **Issue #81 Stage B:** Designed unknown-param filter with `RuntimeHelpers.GetUninitializedObject` schema extraction; Levenshtein threshold 6 validated for PR #78 regression.
- **Issue #82:** Centralized AzDO filter normalization pattern.
- **v0.8.0:** Released with strict-mode safety net + did-you-mean UX.
- **Issue #91+:** SDK bumps, WorkItemSummary fast-path, container image hardening, HTTP 204 handling.
- **2026-07-20:** Measured MCP schema token cost empirically (32.7 KB, ~8,175 tokens). Ground truth for Issue #74 reduction lever analysis.

### High-Value Test Files

- `src/HelixTool.Tests/Mcp/McpServerOptionsExtensionsTests.cs` — alias, strict-mode, unknown-param tests
- `src/HelixTool.Tests/AzDO/AzdoServiceNormalizationTests.cs` — param normalization, cache key stability
- `src/HelixTool.Tests/AzDO/PaginationContractTests.cs` — pagination spec validation (333 LOC, 13/13 passing)

### Current Focus

- **Decision Gate:** Awaiting user go/no-go on MCP schema Lever 1 (minimal outputSchema, ~8.9 KB / 31% savings).
- **Dependencies:** Dallas recommendation + Ripley half-day implementation + Lambert integration test if approved.
- **No blockers:** Measurement complete, recommendations documented in `decisions.md`.

---

## 2026-07-20: MCP Schema Token-Cost Measurement (READ-ONLY — no code changes)
