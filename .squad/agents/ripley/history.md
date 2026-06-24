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
- **DateTimeOffset? in cache keys:** Use `.ToString("O", CultureInfo.InvariantCulture)` for stable, round-trip-safe cache key segments. The `{value:O}` format-string shorthand works but the explicit InvariantCulture call is more defensive.
- **Doc coupling between CLI XML and MCP `[Description]`:** When a param's behavior depends on another param (e.g., minTime/maxTime filtered by the time-field implied by queryOrder), document that coupling in BOTH surfaces. The MCP description and the CLI XML `<param>` doc must be kept in sync — users of each surface deserve the same information.

**Commit:** `0101b7d`  
**Tests:** 1332 passed, 2 skipped (0 failed) — 2 new tests added (NullAndWhitespaceQueryOrder_ShareCacheKey, DifferentTimeRanges_DistinctCacheKeys)  
**Branch:** `fix/azdo-param-plumbing`

## 2026-06-24: PR #78 Third Copilot Review — Defense-in-depth normalization at HTTP client + cache key collapse (fix/azdo-param-plumbing)

### Learnings

- **Normalization belongs at EVERY layer that touches the value, not just the entry layer.** The HTTP client, cache key, and entry-point validation are all independent and must each be self-protecting. An internal caller, test, or future path can bypass the CLI/MCP entry layer — the HTTP client and cache layer must still produce correct behavior. (Applied: `AzdoApiClient.ListBuildsAsync` whitespace-normalizes `QueryOrder` independent of service layer.)
- **For cache keys, normalize the canonical default string to null — null and the explicit server default are semantically identical and must share a key.** `null` and `"queueTimeDescending"` produce the exact same AzDO REST call; hashing them differently causes unnecessary cache misses. Collapse with `string.Equals(value, Default, OrdinalIgnoreCase)` → null before hashing. Extract the default as a named constant to avoid magic-string duplication across client and cache layers. (Applied: `AzdoApiClient.DefaultQueryOrder` constant, `CachingAzdoApiClient.HashFilter` collapses explicit "queueTimeDescending" to null.)

**Commit:** `fd11105`  
**Tests:** 1336 passed, 2 skipped (0 failed) — 4 new tests added (WhitespaceQueryOrder_FallsBackToDefault, EmptyQueryOrder_FallsBackToDefault, NullAndExplicitDefaultQueryOrder_ShareCacheKey, NullAndExplicitFailedOutcomes_ShareCacheKey)  
**Branch:** `fix/azdo-param-plumbing`


## 2026-06-24: PR #78 Final Copilot Review — Array mutability, cache key casing, API stability (fix/azdo-param-plumbing)

### Learnings

- **Public arrays are mutable even when the field is `readonly` — always expose `IReadOnlyList<T>`, `FrozenSet<T>`, or similar for validation sets.** `public static readonly string[]` lets callers overwrite elements at runtime, silently corrupting validation. The field reference is frozen; the array contents are not. Use `IReadOnlyList<string>` (or `ImmutableArray<T>`) at the public boundary.
- **`CancellationToken` is always last in .NET convention; new optional params go before it.** Inserting a parameter anywhere but the position immediately before `CancellationToken` shifts indices for positional callers. Verify placement by reading the full signature — the reviewer's concern was valid in principle but the current code was already correct.
- **Cache keys for case-insensitive server values must be lowercased to prevent fragmentation.** AzDO treats `queryOrder` case-insensitively, so `"finishTimeDescending"` and `"FINISHTIMEDESCENDING"` produce identical server responses but different hash inputs without explicit normalization. After the null-collapse step, call `?.ToLowerInvariant()` before hashing.

### Correction to round-3 learning: "normalize at every layer"

The round-3 learning said "Normalization belongs at EVERY layer that touches the value." Rubber-duck review clarified the right principle is:

- **Validate at user/input boundaries** (CLI/MCP) — for useful, early error messages
- **Canonicalize at semantic boundaries** (cache key construction, URL construction) — where the value's meaning is consumed
- **Centralize the canonicalization algorithm** — multiple layers invoke the shared helper; none reimplement it independently
- **"Defense in depth at every layer" leads to algorithm duplication and drift.** The right principle is "canonicalize at boundaries, share the algorithm." Duplicating the normalization expression at HTTP client, cache, and entry-point means three places to update when the rule changes.

**Commit:** `6bb0009`
**Tests:** 1337 passed, 2 skipped (0 failed) — 1 new test added (ListBuildsAsync_DifferentCasingsSameQueryOrder_ShareCacheKey)
**Branch:** `fix/azdo-param-plumbing`
