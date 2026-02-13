### Lambert: HttpContextHelixTokenAccessor test suite written (L-HTTP-5)

**By:** Lambert
**Date:** 2026-02-12

**What:** Created 17 tests in `src/HelixTool.Tests/HttpContextHelixTokenAccessorTests.cs` for the HTTP-specific token accessor that Ripley is implementing in HelixTool.Mcp.

**Tests cover:**
1. Bearer token extraction (`Authorization: Bearer mytoken123`)
2. `token` format extraction (`Authorization: token mytoken456`)
3. No auth header with env var set → returns env var value
4. No auth header, no env var → returns null
5. No HttpContext, no env var → returns null
6. Empty Authorization header with env var → falls back to env var
7. Empty Authorization header, no env var → returns null
8. Case insensitivity: `bearer`, `BEARER`, `Bearer`, `beArEr` all work
9. Case insensitivity: `TOKEN`, `Token` work
10. Extra spaces around token → trimmed
11. Tabs around token → trimmed
12. Malformed: `Bearer` with no token value → null or empty
13. Malformed: `Bearer   ` (only spaces) → null or empty
14. Unknown scheme (`Basic ...`) → falls back to env var
15. Interface compliance (implements IHelixTokenAccessor)

**Csproj change:** Added `<ProjectReference Include="..\HelixTool.Mcp\HelixTool.Mcp.csproj" />` to HelixTool.Tests.csproj.

**Status:** Tests will compile once Ripley implements `HttpContextHelixTokenAccessor` in HelixTool.Mcp. Currently 1 expected compile error (type not found). All other test files build clean.

**For Ripley:** The tests expect `HttpContextHelixTokenAccessor` to:
- Accept `IHttpContextAccessor` in constructor
- Implement `IHelixTokenAccessor.GetAccessToken()`
- Parse `Bearer {token}` and `token {token}` formats (case-insensitive)
- Trim whitespace from extracted tokens
- Fall back to `HELIX_ACCESS_TOKEN` env var when no recognized auth header present
- Return null when no auth source is available
