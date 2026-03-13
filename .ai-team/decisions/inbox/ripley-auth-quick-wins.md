### 2026-03-13: AzDO auth quick wins tighten credential handling
**By:** Ripley
**What:** Removed the implicit `AzdoCredential` → `string` conversion, added `AZDO_TOKEN_TYPE=pat|bearer` as an explicit override ahead of the JWT heuristic, and redact token-like material from unexpected AzDO error-response snippets before surfacing them.
**Why:** The threat model flagged three low-effort leaks/ambiguities: accidental stringification of credentials, dotted PATs being misread as bearer tokens, and server/proxy error echoes exposing secrets in CLI or MCP-visible exceptions.
