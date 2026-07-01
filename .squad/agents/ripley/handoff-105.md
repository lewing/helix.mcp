# Handoff: Issue #105 — HTTP 204 Handling (for Lambert)

**Date:** 2026-06-30
**Author:** Ripley
**Branch:** `fix/105-azdo-204-handling`

---

## Summary of production changes

### `AzdoApiClient.GetAsync<T>` (core helper)
- Early-return `null` for `HttpStatusCode.NoContent` (204), parallel to existing 404.
- After auth/error checks, returns `null` if `Content-Length == 0`.
- If `Content-Length` header is absent, buffers with `ReadAsByteArrayAsync` and
  returns `null` if the body is empty.

### `AzdoApiClient.GetListAsync<T>` (core helper)
- Same treatment: 204 → `[]`, `Content-Length: 0` → `[]`, no-header empty body → `[]`.

### `AzdoService.SearchTimelineAsync`
- Changed the `timeline is null` branch from throwing `InvalidOperationException`
  to returning a `TimelineSearchResult` with a friendly `Note` message.

### `AzdoService.GetHelixJobsViaTimelineAsync`
- Changed the `timeline is null` branch from throwing `InvalidOperationException`
  to returning a `HelixJobsFromBuildResult(buildIdOrUrl, 0, 0, [])` with a
  friendly `Note` message.

### `AzdoMcpTools.Timeline` (`azdo_timeline` tool)
- Changed `return null` when `GetTimelineAsync` returns null to returning a
  `TimelineResponse { Note = "No timeline available..." }`.

---

## Test scenarios needed

### `AzdoApiClientTests` (unit — mock `HttpMessageHandler`)

1. **`GetAsync_204_ReturnsNull`**
   - Handler returns `StatusCode = 204` with no body.
   - Assert: `GetTimelineAsync(...)` returns `null`.

2. **`GetListAsync_204_ReturnsEmpty`**
   - Handler returns `StatusCode = 204` with no body.
   - Assert: result is empty list (`Count == 0`). Use any list-returning method,
     e.g. `ListBuildsAsync(...)`.

3. **`GetAsync_200_EmptyBodyWithContentLength_ReturnsNull`**
   - Handler returns `StatusCode = 200`, `Content-Length: 0`, empty body.
   - Assert: `GetTimelineAsync(...)` returns `null`.

4. **`GetListAsync_200_EmptyBodyWithContentLength_ReturnsEmpty`**
   - Handler returns `StatusCode = 200`, `Content-Length: 0`, empty body.
   - Assert: result is empty list.

5. **`GetAsync_200_EmptyBodyNoContentLength_ReturnsNull`**
   - Handler returns `StatusCode = 200`, no `Content-Length` header, empty body.
   - Assert: `GetTimelineAsync(...)` returns `null`.

6. **`GetListAsync_200_EmptyBodyNoContentLength_ReturnsEmpty`**
   - Handler returns `StatusCode = 200`, no `Content-Length` header, empty body.
   - Assert: result is empty list.

7. **`GetAsync_200_ValidJson_ReturnsDeserialized`** (happy-path regression)
   - Handler returns `StatusCode = 200` with valid timeline JSON.
   - Assert: deserialized `AzdoTimeline` is returned (non-null, records populated).

8. **`GetListAsync_200_ValidJson_ReturnsDeserialized`** (happy-path regression)
   - Handler returns `StatusCode = 200` with valid `AzdoListResponse<AzdoBuild>` JSON.
   - Assert: list is non-empty.

9. **`GetAsync_404_ReturnsNull`** (existing behavior regression)
   - Existing test `GetTimelineAsync_404_ReturnsNull` — verify still passes unchanged.

10. **`GetListAsync_404_ReturnsEmpty`** (existing behavior regression)
    - Add equivalent: handler returns 404, assert empty list.

### `AzdoServiceTests` (unit — mock `IAzdoApiClient`)

11. **`GetTimelineAsync_NullResult_ReturnsNull`**
    - Already exists (`GetTimelineAsync_NullResult_ReturnsNull` in `AzdoServiceTests.cs`).
    - Verify it still passes.

12. **`SearchTimelineAsync_NullTimeline_ReturnsFriendlyNote`**
    - Mock `_mockApi.GetTimelineAsync(...)` returns `null`.
    - Call `_svc.SearchTimelineAsync("1", "pattern")`.
    - Assert: result is non-null `TimelineSearchResult`.
    - Assert: `result.Note` contains "No timeline available".
    - Assert: `result.Matches` is empty, no exception thrown.

13. **`GetHelixJobsViaTimeline_NullTimeline_ReturnsFriendlyNote`**
    - `_helixApi` should be null (use timeline-only constructor) OR mock Helix API
      to return empty so timeline fallback is exercised.
    - Mock `_mockApi.GetTimelineAsync(...)` returns `null`.
    - Assert: result is `HelixJobsFromBuildResult` with `TotalHelixJobs == 0`.
    - Assert: `result.Note` contains "No timeline available".
    - Assert: no exception thrown.

### `AzdoMcpToolsTests` (unit — mock `IAzdoService` / `IAzdoApiClient`)

14. **`Timeline_NullFromService_ReturnsFriendlyResponse`**
    - Mock `_mockApi.GetTimelineAsync(...)` returns `null`.
    - Call `azdo_timeline` tool (via `AzdoMcpTools.Timeline`).
    - Assert: result is non-null `TimelineResponse`.
    - Assert: `result.Note` contains "No timeline available".
    - Assert: `result.Records` is empty, no exception thrown.

15. **`SearchTimeline_NullTimeline_ReturnsFriendlyResult`**
    - Same mock setup (timeline returns null).
    - Call `azdo_search_timeline` tool.
    - Assert: `TimelineSearchResult.Note` contains "No timeline available".
    - Assert: `Matches` is empty, no exception thrown.

---

## Pattern notes for Lambert

- The mock HTTP handler pattern is already well-established in `AzdoApiClientTests.cs`.
  Use `MockHttpHandler` (or the existing test infrastructure — check how
  `GetTimelineAsync_404_ReturnsNull` is set up at line ~408).
- For service-level tests, the mock `IAzdoApiClient` pattern is in `AzdoServiceTests.cs`
  and `AzdoMcpToolsTests.cs`.
- All new test methods should use `[Fact]` (xUnit) consistent with the rest of the suite.
