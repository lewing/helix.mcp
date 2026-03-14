# 2026-03-14 PR #29 review round 3

- **Requested by:** Larry Ewing
- Ripley fixed 5 CCA review comments on PR #29:
  - `CacheStoreFactory` keyed by `GetEffectiveCacheRoot()` only
  - Auth hash now updates on credential identity change
  - `TrySetAuthTokenHash` allows updates when the value differs
  - `TryParseExpiration` normalized to UTC
  - Removed redundant test assertion
- Build and tests passing; pushed as commit `66ad2d5`
- All 5 review threads replied to
