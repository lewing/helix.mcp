# Decision: Status Filter Test Coverage Strategy

**By:** Lambert (Tester)
**Date:** 2026-02-15
**Context:** Status API migration from `includePassed: bool` to `filter: string`

## Decision

The `filter: "passed"` case introduces a new behavioral pattern where `failed` is null (the reverse of `filter: "failed"` nulling `passed`). Tests cover this symmetry explicitly, plus case-insensitivity and invalid input rejection.

Test naming convention follows the pattern `Status_Filter{Value}_{Assertion}` for consistency with the new API shape. The old `Status_AllTrue`/`Status_AllFalse` names are retired.

## Tests Added (5 new + 2 renamed)

| Test | Filter | Validates |
|------|--------|-----------|
| `Status_FilterFailed_PassedIsNull` | "failed" | passed=null (renamed) |
| `Status_FilterAll_PassedIncludesItems` | "all" | passed populated (renamed) |
| `Status_DefaultFilter_ShowsOnlyFailed` | (none) | default = "failed" behavior |
| `Status_FilterPassed_FailedIsNull` | "passed" | failed=null, passed populated |
| `Status_FilterPassed_IncludesPassedItems` | "passed" | item structure verification |
| `Status_FilterCaseInsensitive` | "ALL" | uppercase accepted |
| `Status_InvalidFilter_ThrowsArgumentException` | "invalid" | ArgumentException thrown |

## Impact

Test count: 364 → 369 (net +5). All 15 status tests pass.
