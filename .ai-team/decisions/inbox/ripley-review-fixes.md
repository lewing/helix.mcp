### 2026-03-10: Security boundaries and download transports must be explicit
**By:** Ripley
**What:** `CacheSecurity.ValidatePathWithinRoot` now treats path containment as an exact, case-sensitive boundary check after full-path normalization and root-boundary trimming. `HelixService` no longer creates a fallback `HttpClient`; every caller must provide one, and the constructor null-guards both dependencies.
**Why:** Ignore-case prefix checks can let a case-variant sibling path look like it is under the trusted root on case-sensitive filesystems. Requiring injected `HttpClient` instances keeps timeout/handler configuration centralized in DI and avoids hidden transport creation that bypasses host configuration.
