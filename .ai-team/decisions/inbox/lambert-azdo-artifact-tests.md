# Decision: AzDO Artifact/Attachment Test Patterns

**Author:** Lambert (Tester)
**Date:** 2026-03-08
**Status:** Informational

## Context
Added 33 tests for the new `azdo_artifacts` and `azdo_test_attachments` MCP tools.

## Patterns Established
1. **CamelCase JSON assertions** use `GetProperty("name").GetString()` instead of `Assert.NotNull(GetProperty("name"))` to avoid xUnit2002 warnings on `JsonElement` (value type).
2. **TestAttachments top limiting** happens in the service layer via `Take(top)`, not the API client — the AzDO API doesn't support server-side limiting for test result attachments.
3. **Artifact caching** uses `ImmutableTtl` (4h) since artifacts are immutable once published.
4. **Attachment caching** uses `TestTtl` (1h) consistent with other test-scoped data.

## Impact
Total test count: 700 (up from 667).
