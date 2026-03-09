# Perf Fixes Decisions — Ripley (2025-07-18)

## Decision 1: Cache format change (raw: prefix)

**Context:** CachingAzdoApiClient stored log content via `JsonSerializer.Serialize<string>()`, double-escaping multi-MB strings. Changed to plain text with `raw:` sentinel prefix.

**Decision:** Backward-compatible migration via sentinel detection. `DeserializeLogContent` checks for `raw:` prefix first, falls back to JSON deserialization for legacy entries. No explicit migration step — natural TTL expiry handles transition.

**Risk:** Low. Legacy entries are still readable. New entries are written in the efficient format. Cache key structure is unchanged, so there's no key collision.

**For Dallas to review:** Is the `raw:` prefix approach acceptable long-term, or should we consider a versioned cache format? The prefix relies on log content never starting with `raw:` literally — extremely unlikely for AzDO build logs but worth noting.

## Decision 2: SearchConsoleLogAsync decoupled from disk download

**Context:** `SearchConsoleLogAsync` used `DownloadConsoleLogAsync` (stream→disk) then `File.ReadAllLinesAsync` (disk→memory). Changed to use `GetConsoleLogContentAsync` (stream→memory directly).

**Decision:** Safe to decouple because `DownloadConsoleLogAsync` is only used by the CLI download command and `SearchFileAsync` (which needs disk for binary detection). Search doesn't need disk presence.

**Risk:** None observed — 864/864 tests pass. If a future change adds caching or rate-limiting at the download layer, search would bypass it. Worth noting but not a current concern.

## Decision 3: Shared StringHelpers in Core

**Context:** Both AzdoService and HelixService had identical tail-trimming patterns. Extracted to `HelixTool.Core.StringHelpers` (internal static class).

**Decision:** `internal` visibility is sufficient — only Core code needs it. If CLI or MCP projects need it in the future, promote to `public`.
