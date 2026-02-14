### 2025-07-25: Cache security expectations documented in README

**By:** Kane
**What:** Added a "Cached data" subsection under Security in README.md. Documents what gets cached (API responses + artifacts), where it lives (SQLite on disk in user profile directory), that auth tokens are never cached (only hash prefix for directory isolation), and recommends `hlx cache clear` for shared machines or security context switches. Addresses threat model items I1 (information disclosure via cached data) and I2 (cache persists after session).
**Why:** The threat model (`.ai-team/analysis/threat-model.md`) explicitly recommended documenting cache security expectations. Users need to know the cache directory may contain sensitive CI data (console logs with accidental secrets) and understand the auth isolation model. This closes the documentation gap for I1/I2 without requiring code changes.
