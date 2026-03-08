# Decision Proposal: AzDO Search/Filter Capabilities

**Author:** Ash (Product Analyst)
**Date:** 2025-07-24
**Status:** Proposed
**Requested by:** Larry Ewing

## Context

The Helix MCP tools include `helix_search_log` and `helix_search_file` — tools that let agents search within large text artifacts (console logs, uploaded files) rather than consuming the entire content into their context window. This pattern exists specifically to limit context window consumption by returning only matching lines with surrounding context.

The AzDO tools currently lack this pattern. This gap analysis identifies where similar search capabilities would provide the most value for AzDO tools.

## Gap Analysis

### P0: `azdo_search_log` — Search within AzDO build logs

**Current state:** `azdo_log` returns raw text with a `tailLines=500` default. If the relevant error is outside the last 500 lines, the agent must either increase `tailLines` (consuming more context) or miss the error entirely. There's no way to search for a specific pattern within a build log.

**Impact:** Build logs are the single largest text payloads in the AzDO tool surface. A dotnet/runtime build task log can easily exceed 10,000 lines. Agents investigating failures often need to find specific error codes, exception types, or test names buried somewhere in the log.

**Proposed tool:** `azdo_search_log` — direct analog of `helix_search_log`
- Parameters: `buildId`, `logId`, `pattern` (text, case-insensitive), `contextLines` (default: 2), `maxMatches` (default: 50)
- Implementation: Download full log content, apply `SearchLines()` (shared from HelixService or extracted to a utility), return matches with context
- Context savings estimate: **90-95%** for typical "find the error in this log" workflows

**API constraint:** AzDO REST API returns build logs as plain text — no server-side search exists. Must be client-side, same as Helix implementation.

### P1: `azdo_test_results` text filter parameter

**Current state:** `azdo_test_results` already filters to `outcomes=Failed` server-side and caps at `top=200`. However, each failed test result includes `errorMessage` and `stackTrace` fields that can be large. With 50+ failures, total payload is substantial.

**Impact:** When an agent knows the test name pattern it's looking for (e.g., "HttpClient" or "Serialization"), it currently must consume all 200 results to find the relevant ones.

**Proposed enhancement:** Add optional `testNameFilter` parameter to `azdo_test_results`
- Client-side filtering on `testCaseTitle` and `automatedTestName` fields (case-insensitive contains)
- Also consider an `outcomeFilter` parameter (currently hardcoded to "Failed" — could allow "Passed", "NotExecuted", "All")
- Context savings estimate: **50-80%** when agents search for specific test patterns

### P2: `azdo_timeline` name search

**Current state:** `azdo_timeline` already filters to failed records by default. But large builds (dotnet/runtime) can have hundreds of failed timeline records across stages/jobs/tasks.

**Impact:** Agents sometimes need to find a specific job by name (e.g., "coreclr_tests" or "libraries_tests"). Currently must scan the full filtered timeline.

**Proposed enhancement:** Add optional `nameFilter` parameter to `azdo_timeline`
- Case-insensitive substring match on record name
- Include parent chain for context (same as current filter logic)
- Context savings estimate: **30-60%** for targeted job investigation

### No action needed

- **`azdo_builds`** — Already well-filtered (branch, PR, definition, status, top). Returns structured metadata, not raw text.
- **`azdo_artifacts`** — Already has glob pattern matching and top limit.
- **`azdo_changes`** — Small structured payloads with top limit.
- **`azdo_test_runs`** — Structured summaries, reasonable size.
- **`azdo_test_attachments`** — Metadata only, no content.

## Implementation Notes

1. The `SearchLines()` method in `HelixService` (line 599) should be extracted to a shared utility (e.g., `TextSearchHelper` in Core) so both Helix and AzDO search tools can use it. This avoids code duplication.

2. The `azdo_search_log` implementation in `AzdoService` would:
   - Call `GetBuildLogAsync()` (existing) to get full content (remove tailLines)
   - Apply `TextSearchHelper.SearchLines()` to find matches
   - Return the same `LogSearchResult` / `SearchMatch` structure used by Helix

3. The `IsFileSearchDisabled` config flag should apply to AzDO search tools too — same security posture.

4. All new search tools should respect the `UseStructuredContent` pattern.

## Decision Needed From Dallas

1. **Approve/prioritize** the three proposed enhancements
2. **Architecture decision:** Should `SearchLines` be extracted to a shared utility, or should AzDO have its own implementation? (Recommending shared utility in Core)
3. **Naming convention:** Should we match `helix_search_log` → `azdo_search_log`, or use a different pattern?
