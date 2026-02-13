# Decision: Path Traversal Hardening for Cache and Download Paths

**Date:** 2026-02-13
**Author:** Ripley (Backend Dev)
**Requested by:** Larry Ewing
**Status:** Implemented

## Problem

The cache stores artifacts on disk using paths derived from user-supplied inputs (job IDs, work item names, file names from Helix API responses). A malicious or crafted input containing `..`, `/`, `\`, or other path traversal characters could escape the cache directory or temp directory, enabling arbitrary file read/write.

Attack vectors:
1. Cache key → file path in `SqliteCacheStore.SetArtifactAsync` / `GetArtifactAsync`
2. File names from Helix API used in `HelixService.DownloadFilesAsync` / `DownloadFromUrlAsync`
3. Work item names from Helix API used in cache keys and console log download paths
4. File names from `ListFiles` API responses used in download paths

## Solution

### New: `Cache/CacheSecurity.cs`

Static helper class with three methods:
- `ValidatePathWithinRoot(path, root)` — canonical path traversal defense. Resolves both to full paths, checks the resolved path starts with the resolved root + directory separator. Throws `ArgumentException` on violation.
- `SanitizePathSegment(segment)` — strips `..` and replaces `/` and `\` with `_` in individual path segments.
- `SanitizeCacheKeySegment(value)` — same sanitization for cache key components (jobId, workItemName, fileName embedded in `:` delimited keys).

### Changes to `SqliteCacheStore`

- `GetArtifactAsync`: Added `ValidatePathWithinRoot(fullPath, _artifactsDir)` before reading.
- `SetArtifactAsync`: Sanitize jobId prefix and cacheKey via `SanitizePathSegment`, then validate full path within artifacts dir.
- `DeleteArtifactRows`: Validate before deleting artifact files.

### Changes to `CachingHelixApiClient`

All 6 cache key construction sites now wrap user inputs with `SanitizeCacheKeySegment()`:
- `GetJobDetailsAsync` — jobId
- `ListWorkItemsAsync` — jobId
- `GetWorkItemDetailsAsync` — jobId, workItemName
- `ListWorkItemFilesAsync` — jobId, workItemName
- `GetConsoleLogAsync` — jobId, workItemName
- `GetFileAsync` — jobId, workItemName, fileName

### Changes to `HelixService`

- `DownloadFilesAsync`: Use `Path.GetFileName()` + `SanitizePathSegment()` on file names from API, then validate output path within outDir.
- `DownloadFromUrlAsync`: Use `Path.GetFileName()` + `SanitizePathSegment()` on URI-derived file name, then validate path within temp dir.
- `DownloadConsoleLogAsync`: Use `SanitizePathSegment()` on work item name, validate path within temp dir.

## Defense-in-depth approach

Two layers:
1. **Sanitization** (proactive): Strip traversal characters before they reach path construction.
2. **Validation** (reactive): After `Path.Combine`, verify `Path.GetFullPath` stays within root. This catches anything sanitization missed.

## Test impact

All 182 existing tests pass. No test changes needed — the sanitization is transparent for well-formed inputs.
