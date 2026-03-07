# Decision: AzdoService Method Signatures

**Author:** Ripley  
**Date:** 2026-03-07  
**Status:** Implemented

## Context
AzdoService is the business logic layer between MCP tools and IAzdoApiClient.

## Decisions

1. **All `buildIdOrUrl` parameters resolve via `AzdoIdResolver.Resolve()`** — ArgumentException propagates naturally on bad input.

2. **`GetBuildSummaryAsync` returns `AzdoBuildSummary`** — a flattened record with computed `Duration` and `WebUrl`. Added to `AzdoModels.cs`.

3. **`GetTestResultsAsync(buildIdOrUrl, runId)`** — uses buildIdOrUrl to resolve org/project since runId is org/project-scoped. BuildId is discarded.

4. **`GetBuildLogAsync` has `int? tailLines` parameter** — service handles tail slicing so MCP tools don't have to.

5. **`ListBuildsAsync(org, project, filter)`** — takes raw org/project (no URL resolution) since callers may not have a build URL yet.

6. **No exception wrapping** — unlike HelixService→HelixException, AzdoService lets HttpRequestException propagate for now. Will add AzdoException when MCP tools need it.

## For Team
- **Lambert:** 7 new methods to test. Focus on: URL resolution integration, tailLines edge cases (null, 0, negative, exact length, exceeding length), null build from GetBuildAsync.
- **Kane:** Method signatures are stable — can begin MCP tool descriptions.
