# Decisions

## 2026-03-16T21:04:47Z: User Directive — Partial Response Pattern

**By:** Larry Ewing (via Copilot)

When truncating large MCP tool outputs, use the partial response pattern (return what fits with a continuation indicator) rather than just dropping data.

---

## 2026-03-16: MCP Timeline Tool Analysis — Discoverability & Output Size

**By:** Ripley  
**Date:** 2026-07-18  
**Context:** Larry's gist showing a CI analysis session where `azdo_timeline` returned 447KB and 601KB responses, forcing the agent to save to temp files and parse with Python scripts.

### Findings

#### 1. `azdo_timeline` filter='failed' — limited output reduction for VMR builds

The `filter` parameter (default `"failed"`) works at the MCP tool layer (`AzdoMcpTools.cs:93–121`). It filters to non-succeeded records plus their parent chain, which does reduce record count. However, it returns full `AzdoTimelineRecord` objects with all fields (issues arrays, previousAttempts, log references, workerName, etc.). For VMR builds with hundreds of records and many partially-succeeded verticals, the filtered output can still be 400–600KB — too large for LLM context windows.

#### 2. `azdo_search_timeline` — solves the problem but isn't discoverable

`azdo_search_timeline` (`AzdoService.cs:182–290`) fetches the same raw timeline but returns compact `TimelineSearchMatch` DTOs: only recordId, name, type, state, result, duration, logId, matchedIssues, parentName. This is an order-of-magnitude smaller. The agent in the gist would have gotten far better results calling `azdo_search_timeline(buildId, "error")` or `azdo_search_timeline(buildId, "fail")` instead of `azdo_timeline`.

#### 3. Other tools that could have helped

- `azdo_search_log` — searches across all build step logs with ranked prioritization (failed steps first). The agent could have gone directly from build ID to error text without ever touching the timeline.
- `azdo_log` — fetches a specific step log by logId (with tail support), but requires knowing the logId from timeline first.
- The optimal workflow: `azdo_search_timeline` to find which steps failed → `azdo_search_log` for error details → `azdo_log` if full context needed.

#### 4. Discoverability gap — ci_guide never mentions `azdo_search_timeline`

All 9 repo profiles in `CiKnowledgeService.cs` recommend:
```
azdo_timeline(buildId, filter='failed') → identify failed jobs
```
None mention `azdo_search_timeline` anywhere. The generic overview section also omits it. The MCP tool description itself is accurate ("Search build timeline record names and issue messages for a pattern") but an LLM following the ci_guide workflow will never see it.

#### 5. MCP layer does real work, not just pass-through

`AzdoService` handles URL resolution, parallel metadata fetches, ranked log search with early termination, and result shaping. The API client layer (`AzdoApiClient`) is thin HTTP + auth. The service layer is where business logic lives.

### Recommended Actions

1. **Update ci_guide investigation workflows** to mention `azdo_search_timeline` as the preferred first step for large builds (especially VMR), or at least as an alternative to `azdo_timeline` when you know what pattern to search for.
2. **Consider adding an `azdo_timeline` output budget** — e.g., if the filtered result would exceed N records or N bytes, automatically summarize or truncate with a note pointing to `azdo_search_timeline`.
3. **Cross-reference in tool descriptions** — `azdo_timeline`'s description could say "For large builds, consider azdo_search_timeline to search by pattern instead."

### Files Examined

- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` — MCP tool definitions (all 11 AzDO tools)
- `src/HelixTool.Core/AzDO/AzdoService.cs` — service layer with SearchTimelineAsync, SearchBuildLogAcrossStepsAsync
- `src/HelixTool.Core/AzDO/AzdoModels.cs` — all AzDO DTOs including TimelineSearchMatch, TimelineSearchResult
- `src/HelixTool.Core/AzDO/AzdoApiClient.cs` — raw API client (thin HTTP layer)
- `src/HelixTool.Core/CiKnowledgeService.cs` — ci_guide content for all 9 repos

---

## 2026-03-16: Timeline Truncation Implementation — Partial Response Pattern

**By:** Ripley  
**Date:** 2026-07-18  
**Status:** Implemented, needs review

### Decision

When `azdo_timeline` returns more than 200 records, the MCP tool layer now returns a **partial response**: the first 100 records plus truncation metadata (`truncated: true`, `totalRecords: N`, `note: "⚠️ ..."`) pointing the agent to `azdo_search_timeline` as a better alternative.

This implements Larry's directive: "When truncating large MCP tool outputs, use the partial response pattern (return what fits with a continuation indicator) rather than just dropping data."

### What Changed

1. **`AzdoMcpTools.Timeline()`** — return type changed from `AzdoTimeline?` to `TimelineResponse?`. New `TimelineResponse` record adds `truncated`, `totalRecords`, and `note` fields (omitted from JSON when not set). Threshold: 200 records; budget: 100.
2. **`azdo_timeline` description** — now mentions `azdo_search_timeline` as an alternative for large builds (~35 words, within repo convention).
3. **`CiKnowledgeService.cs`** — 5 repo profiles (runtime, sdk, roslyn, efcore, VMR) updated to recommend `azdo_search_timeline` as the first investigation step. Generic overview sections also updated. Profiles with non-timeline-first workflows (aspnetcore, maui, macios, android) left unchanged.

### Trade-offs

- **Wire format change:** `TimelineResponse` replaces `AzdoTimeline` as the return type. Non-truncated responses are wire-compatible (same `id` + `records` structure). Truncated responses add three new fields. Downstream consumers (CLI, tests) may need updates if they depend on the exact type.
- **Fixed thresholds:** 200/100 are hardcoded consts. Could be made configurable later if needed, but keeping it simple for now.
- **Partial data ordering:** Takes the first N records by their position in the API response (timeline order). This preserves the hierarchical structure (stages appear before their child jobs/tasks) but may cut off later stages.

### For Dallas

- Review whether the wire format change from `AzdoTimeline?` to `TimelineResponse?` needs any CLI-side updates.
- Consider whether the 200/100 thresholds are appropriate, or if they should be configurable.
