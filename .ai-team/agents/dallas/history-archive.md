# Dallas — History Archive
> Archived on 2026-03-09. See history.md for current context.

## 2026-03-07: Decision — AzdoMcpTools returns model types directly
If we later need to reshape AzDO output differently from the API models, we'd add wrapper types then. For now, direct return is simpler and correct.

## 2026-03-08: AzDO Security Review — Learnings
- **Query parameter injection is easy to miss.** `prNumber` was not escaped or validated as an integer while `branch` and `statusFilter` were properly escaped with `Uri.EscapeDataString`. Enforce convention: all user-provided string values interpolated into URLs must be either type-validated (e.g., `int.TryParse` for numeric IDs) or `Uri.EscapeDataString`-escaped. No exceptions.
- **`BuildUrl` hardcoding `https://dev.azure.com/` is the right SSRF mitigation.** Combined with `Uri.EscapeDataString` on org/project, this makes SSRF structurally impossible regardless of input. This pattern should be preserved — never allow user input to influence the URL base/authority.
- **Singleton `AzCliAzdoTokenAccessor` with `_resolved` flag doesn't handle token expiry.** For long-running MCP servers, az CLI tokens (~1h lifetime) will expire. Not a security bug (fails closed with 401), but an operational gap. Future work: track JWT expiry or use `AZDO_TOKEN` with external rotation.
- **`CacheSecurity.SanitizeCacheKeySegment` is the established pattern for cache key hygiene.** AzDO caching correctly reuses it. Any new cacheable subsystem must use it too.
- **Security review convention established:** For new API integrations, review all 7 focus areas (command injection, SSRF, token leakage, input validation, cache isolation, HTTP security, pattern consistency). Use SEC-{N} IDs with severity levels.
