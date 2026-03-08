# 2026-03-08: AzDO Integration Security Review

**Requested by:** Larry Ewing

## Session Summary

Dallas performed an architectural security review of the AzDO integration code (8 files). Found 6 findings:
- **1 Medium** — SEC-1: `prNumber` query parameter injection in `AzdoApiClient.ListBuildsAsync` (unescaped string interpolation allows query injection into AzDO API calls)
- **3 Low** — SEC-2: HttpClient without IHttpClientFactory (socket exhaustion risk), SEC-3: unbounded log response size, SEC-4: no configurable timeout on AzDO HttpClient
- **2 Info** — SEC-5: `AzCliAzdoTokenAccessor._resolved` not thread-safe, SEC-6: az CLI token never refreshed after initial resolution

**Verdict:** Safe to merge after fixing SEC-1 (`int.TryParse` validation on `prNumber`).

**Areas reviewed with no issues:** Command injection, SSRF (BuildUrl + AzdoIdResolver), token leakage, cache isolation, TLS enforcement, input validation (except prNumber), consistency with existing Helix patterns.

## Testing

Lambert wrote 63 new security-focused tests (667 total, all passing). Tests cover:
- Malicious URL inputs (embedded credentials, non-AzDO hosts, path traversal, query injection)
- SSRF prevention (host assertion, URL base hardcoding)
- Command injection safety (no shell execute, env var passthrough)
- Cache isolation (org/project key separation, poisoning resistance)
- Token leakage (DoesNotContain on all error paths)
- End-to-end rejection (bad input rejected before API call)

## Action Items

- Ripley: Fix SEC-1 — add `int.TryParse` validation on `prNumber` in `AzdoApiClient.cs`
- SEC-2/3/4: Follow-up improvements (non-blocking)
- SEC-5/6: Document as known limitations
