# AzDO Authentication Chain Threat Model

**Date:** 2026-03-13  
**Author:** Dallas  
**Scope:** Azure DevOps authentication chain introduced in PR #19 (`AZDO_TOKEN` → `AzureCliCredential` → `az` CLI subprocess → anonymous) for both CLI and MCP server usage.  
**Methodology:** STRIDE, with emphasis on MCP trust boundaries, token handling, and long-running server behavior.

---

## Executive Summary

The auth chain is directionally sound: it prefers explicit credentials, hard-locks outbound API traffic to `https://dev.azure.com/`, never writes tokens into AzDO cache payloads, redacts `AzdoCredential.ToString()`, and avoids shell injection in the `az` fallback by using `UseShellExecute = false` with fixed arguments.

The main residual risks are architectural rather than cryptographic:

1. **HTTP MCP clients act through a shared server-side AzDO identity.** AzDO auth is process-wide, not per-request.
2. **Bearer tokens are cached as strings for process lifetime.** That retains secrets in memory and prevents refresh after expiry.
3. **Failure modes are intentionally quiet.** Silent fallback to anonymous improves resiliency for public repos but obscures auth outages.
4. **`DisplayToken` / implicit string conversion was a sharp edge.** The implicit string conversion has now been removed. ✅ **Addressed in this PR.**
5. **AzDO cache isolation and AzDO auth are not tightly coupled in CLI/stdio paths.** Tokens stay in memory only, but authenticated AzDO responses can still outlive the process in shared cache directories.

---

## System Overview

### Authentication chain

1. **`AZDO_TOKEN` environment variable**
   - Read on every request.
   - Treated as **Bearer** when it "looks like" a JWT (exactly two dots).
   - Otherwise encoded as **Basic** for PAT auth.
2. **`AzureCliCredential`**
   - Uses the local `az login` session through `Azure.Identity`.
3. **`az account get-access-token` subprocess**
   - Fixed command and resource ID.
4. **Anonymous**
   - No `Authorization` header.
   - Works only for public Azure DevOps data.

### Security-positive properties already present

- `AzdoApiClient.BuildUrl()` hardcodes `https://dev.azure.com/...`.
- `Authorization` is sent only via headers, never in URLs.
- `AzdoCredential.ToString()` redacts the token.
- PATs are converted to `Basic` only at the wire boundary.
- The `az` subprocess uses `UseShellExecute = false` and hardcoded arguments.
- Tests cover token leakage in common 401/403/500 paths.

---

## Trust Boundary Diagram

```text
[Human user / AI agent / MCP client]
        |
        |  TB1: tool input, MCP transport, process environment
        v
[hlx CLI or MCP server process]
        |
        |-- stdio mode: local same-user boundary
        |-- HTTP mode: remote client boundary (+ optional X-Api-Key gate)
        |
        |  TB2: server-side AzDO auth chain
        |   +--> [AZDO_TOKEN env var]
        |   +--> [AzureCliCredential / local Azure CLI session]
        |   +--> [az account get-access-token subprocess via PATH]
        |   \--> [anonymous]
        |
        |  TB3: process memory + cache layer
        |   +--> [AzdoCredential.Token / DisplayToken in memory]
        |   \--> [SQLite/file cache keyed independently of AzDO credential]
        v
[AzdoApiClient]
        |
        |  TB4: outbound HTTPS request with Basic/Bearer header
        v
[Azure DevOps REST API - dev.azure.com]
```

### Trust boundary notes

- In **stdio**, the MCP client and server usually share the same OS user, so the main trust issue is prompt-injected tool input rather than remote network callers.
- In **HTTP MCP mode**, the caller is an AI agent or remote client crossing a real network boundary. The AzDO credential used for outbound requests is still the **server's** credential, not the caller's credential.
- The `az` fallback introduces a local boundary to the **workstation PATH and Azure CLI installation**, not just to Azure DevOps.

---

## Credential Data Flows

### 1. `AZDO_TOKEN` environment variable

```text
Launcher / shell / MCP config env
    -> Environment.GetEnvironmentVariable("AZDO_TOKEN")
    -> LooksLikeJwt()? Bearer : EncodePatForBasic()? Basic
    -> AzdoCredential(Token, Scheme, Source, DisplayToken)
    -> Authorization header on HTTPS request to dev.azure.com
```

**Observations**
- Best for explicit, deterministic server auth.
- Re-read every call, so rotation is visible without restarting the process.
- Raw token still exists in `DisplayToken` and in process environment.

### 2. `AzureCliCredential`

```text
Local az login session / Azure.Identity cache
    -> AzureCliCredential.GetTokenAsync(scope)
    -> Bearer token string copied into AzdoCredential
    -> cached in _cachedAzureIdentityCredential and _cachedCredential
    -> Authorization header on HTTPS request to dev.azure.com
```

**Observations**
- Uses the developer's existing `az login` context.
- Current implementation caches the **token string**, not the credential provider result, so refresh is bypassed after first resolution.

### 3. `az` CLI subprocess fallback

```text
Process.Start("az", "account get-access-token ...")
    -> stdout capture
    -> Trim()
    -> Bearer token copied into AzdoCredential
    -> cached in _cachedCredential
    -> Authorization header on HTTPS request to dev.azure.com
```

**Observations**
- No shell metacharacter interpretation in this code path.
- The trust boundary shifts to PATH resolution, local `az` binary integrity, and Azure CLI's own transport/security behavior.

### 4. Anonymous

```text
No env token
    -> AzureCliCredential unavailable/failed
    -> az CLI unavailable/failed
    -> null credential
    -> no Authorization header
    -> public AzDO endpoints only
```

**Observations**
- Safe for public data.
- Dangerous from a usability/security-signaling standpoint because it can hide failed authenticated resolution.

---

## Attack Surface Summary

| Surface | Entry point | What is exposed | Current posture |
|---|---|---|---|
| Process environment | `AZDO_TOKEN`, PATH, startup context | Identity selection, token source, `az` binary selection | High-trust ambient authority |
| Azure Identity | `AzureCliCredential` | Local Azure CLI/Entra login session | Narrow credential choice, but token refresh is bypassed after first fetch |
| `az` subprocess | `Process.Start("az", ...)` | Local PATH / CLI integrity / stdout token handling | Shell-safe, but PATH-trust-sensitive |
| Outbound HTTP | `AzdoApiClient` | Bearer or Basic header to AzDO | HTTPS-only, fixed host |
| Error propagation | `HttpRequestException` messages to CLI/MCP client | Auth source names, truncated server error bodies | Common token leaks covered; upstream-body leaks still possible |
| Process memory | `_cachedCredential`, `DisplayToken`, HTTP headers | Raw token lifetime | In-memory only, but retained for process lifetime |
| Cache/storage coupling | `CachingAzdoApiClient` + cache root selection | AzDO response persistence after auth | Tokens not cached in payloads; response isolation is weaker than the README suggests in CLI/stdio paths |
| MCP transport | stdio or HTTP | Who can cause server-side AzDO requests | Strongest risk in HTTP/shared-server deployments |

---

## STRIDE Threats

## Spoofing

### T-1
- **Category:** Spoofing
- **Component:** HTTP MCP server + singleton `IAzdoTokenAccessor`
- **Threat:** A remote MCP client can make requests that Azure DevOps sees as the **server's** AzDO identity, not the caller's identity. In HTTP mode, the effective AzDO principal is shared across all clients.
- **Severity:** High
- **Current mitigation:** Stdio mode is the default; HTTP mode can be gated with `HLX_API_KEY` / `X-Api-Key`; the chain itself is explicit and narrow.
- **Residual risk:** Any client that can invoke MCP tools against the server can pivot through whatever AzDO access the server process has (private orgs, internal projects, broader-than-expected tenancy access).
- **Recommendation:** Treat AzDO auth as explicitly **server-scoped** in docs and deployment guidance. For shared HTTP deployments, prefer per-request AzDO delegation or a separate server identity with tightly limited AzDO permissions.

### T-2
- **Category:** Spoofing
- **Component:** `AZDO_TOKEN` env var and ambient workstation auth context
- **Threat:** Whoever controls the process environment or launch context can choose the AzDO identity by setting `AZDO_TOKEN`, altering the Azure CLI login state, or launching the process under a different user/session.
- **Severity:** High
- **Current mitigation:** `AZDO_TOKEN` is intentionally highest-priority and is re-read on every call; there is no hidden credential probe such as `DefaultAzureCredential`.
- **Residual risk:** CI pipelines, shell profiles, IDE extensions, or wrapper processes can silently override the intended identity. The tool has no explicit identity confirmation step before use.
- **Recommendation:** Surface the resolved auth source and principal in a lightweight `auth status` path for AzDO, and document that shared MCP servers must control environment injection as a deployment boundary.

## Tampering

### T-3
- **Category:** Tampering
- **Component:** `az` CLI subprocess fallback
- **Threat:** The fallback resolves `az` via PATH. A malicious or replaced `az` binary can return attacker-controlled output, exfiltrate tokens, or force a chosen identity.
- **Severity:** Medium
- **Current mitigation:** `UseShellExecute = false`, fixed filename (`az`), fixed arguments, and no user-controlled command construction eliminate shell injection in this code.
- **Residual risk:** PATH hijack, compromised workstation installs, or trojaned developer tooling remain in scope. The code trusts whatever executable the OS resolves as `az`.
- **Recommendation:** Prefer `AzureCliCredential` over the subprocess when available, and consider logging a one-time warning when falling back to PATH-based `az`. For hardened deployments, resolve an expected absolute path or document PATH integrity as a prerequisite.

### T-4
- **Category:** Tampering
- **Component:** `LooksLikeJwt()` heuristic
- **Threat:** Token type is inferred from a simple "exactly two dots" heuristic. A malformed token can be classified as Bearer, and an edge-case PAT containing two dots can be classified as JWT-like input.
- **Severity:** Medium
- **Current mitigation:** Misclassified tokens still fail closed at Azure DevOps; PATs sent as Basic are encoded before use.
- **Residual risk:** The caller has no way to force the intended auth scheme. Misclassification becomes an integrity problem for auth state because the request is constructed incorrectly before it ever reaches AzDO.
- **Recommendation:** Add an explicit override (`AZDO_TOKEN_TYPE=pat|bearer`) or a stricter heuristic/validation path. At minimum, document the heuristic and its failure mode. ✅ **Addressed in this PR.** `AZDO_TOKEN_TYPE` override support has been implemented.

## Repudiation

### T-5
- **Category:** Repudiation
- **Component:** Fallback resolution (`AzureCliCredential`/`az`/anonymous)
- **Threat:** Credential-resolution failures are intentionally quiet. `CredentialUnavailableException`, `AuthenticationFailedException`, non-zero `az` exit codes, and most subprocess exceptions all collapse into later anonymous behavior with no durable audit trail.
- **Severity:** Medium
- **Current mitigation:** 401/403 messages include the final auth source used (`Current auth: ...`) when Azure DevOps rejects the request.
- **Residual risk:** For public endpoints, callers may never know that authenticated resolution failed. For private endpoints, the user sees the end result but not which fallback step failed or why.
- **Recommendation:** Emit structured debug/trace events for auth-resolution attempts and failures (without tokens). Keep the user-facing behavior quiet by default, but make post-incident reconstruction possible.

## Information Disclosure

### T-6
- **Category:** Information Disclosure
- **Component:** `AzdoApiClient.ThrowOnUnexpectedError()`
- **Threat:** Unexpected-response handling reflects up to 500 characters of the server response body directly into the thrown exception message. If Azure DevOps, a proxy, or a future upstream layer echoes sensitive material, that text is exposed to CLI output and MCP clients.
- **Severity:** Medium
- **Current mitigation:** The body is truncated to 500 characters; common 401/403 paths do not include the token; tests verify the client's own error text does not echo configured tokens.
- **Residual risk:** Truncation is not redaction. Response bodies can still leak secrets, org names, internal routing details, or reflected header fragments.
- **Recommendation:** Apply secret-pattern redaction to error-body snippets before surfacing them, or downgrade raw-body inclusion behind a debug flag. ✅ **Addressed in this PR.** Error-body redaction has been implemented.

### T-7
- **Category:** Information Disclosure
- **Component:** `AzdoCredential.DisplayToken` and implicit `string` conversions
- **Threat:** `AzdoCredential` converts implicitly to the raw display token. Any future string interpolation, logging, exception construction, or telemetry that treats the credential as a string can leak the plaintext token.
- **Severity:** High
- **Current mitigation:** `ToString()` is redacted, current tests cover `ToString()` safety indirectly, and existing call sites use `credential.Token`/`credential.Source` deliberately.
- **Residual risk:** The dangerous path is still in the public API surface. The risk is highest for future maintenance, debugging helpers, and external consumers of `AzdoCredential`.
- **Recommendation:** Remove or obsolete the implicit `string` conversion if compatibility allows. If it must remain, add loud XML-doc warnings and targeted tests proving no production path relies on it for logging or messaging. ✅ **Addressed in this PR.** The implicit string conversion has been removed.

### T-8
- **Category:** Information Disclosure
- **Component:** `_cachedCredential`, `_cachedAzureIdentityCredential`, `DisplayToken`
- **Threat:** Tokens are stored only in memory, but for the **entire process lifetime** after fallback resolution. A long-lived MCP server keeps bearer tokens resident even after the user signs out elsewhere.
- **Severity:** Medium
- **Current mitigation:** Tokens are not intentionally written to disk by the auth chain itself; env-var credentials are not cached by the accessor; cache values do not store `Authorization` headers.
- **Residual risk:** Memory inspection, crash dumps, verbose diagnostics, or accidental serialization elsewhere can expose long-lived token material. The risk is amplified by storing both wire token and display token.
- **Recommendation:** Cache metadata needed to re-acquire tokens rather than caching the raw token string when possible, and clear cached fallback credentials on expiry/failure. Document crash-dump sensitivity for long-running MCP servers.

### T-9
- **Category:** Information Disclosure
- **Component:** `CachingAzdoApiClient` + cache root selection in CLI/stdio composition roots
- **Threat:** The auth chain itself keeps tokens in memory only, but authenticated **AzDO responses** can still persist on disk in cache locations that are not keyed by AzDO credential material. In CLI/stdio startup, `CacheOptions.AuthTokenHash` is initialized to null, while `CachingAzdoApiClient` cache keys contain only org/project/suffix.
- **Severity:** High
- **Current mitigation:** Cache payloads tested in this area do not contain bearer tokens or `Authorization` headers; HTTP mode can scope caches by the incoming Helix token hash; cache keys sanitize org/project values.
- **Residual risk:** On a shared machine or across separate stdio processes, private AzDO data fetched with one AzDO identity may be replayed from the shared `public/` cache to another process. This is a data-disclosure risk even though the token itself is never cached.
- **Recommendation:** Key AzDO caches by AzDO auth context (or disable authenticated AzDO caching until that isolation exists), and align README claims with actual behavior.

### T-10
- **Category:** Information Disclosure
- **Component:** Transport security assumptions (`AzdoApiClient` + Azure CLI/Azure.Identity)
- **Threat:** Token confidentiality depends on default TLS behavior in `HttpClient`, Azure.Identity, Azure CLI, and the OS trust store. The code does not add pinning or explicit proxy detection.
- **Severity:** Low
- **Current mitigation:** All AzDO requests are forced to `https://dev.azure.com`; credentials are sent in headers, not query strings; there is no custom insecure `HttpClientHandler` here.
- **Residual risk:** Enterprise TLS interception, compromised trust stores, or malicious local proxies remain outside the app's direct control.
- **Recommendation:** No immediate code change required. Document the reliance on OS/browser/Azure CLI trust roots and keep transport handling on the standard stacks.

## Denial of Service

### T-11
- **Category:** Denial of Service
- **Component:** Process-lifetime caching of fallback credentials
- **Threat:** `AzureCliCredential` and `az`-derived bearer tokens are cached as strings and never refreshed. Once they expire, long-running MCP servers continue sending stale credentials until restarted.
- **Severity:** High
- **Current mitigation:** None in the current implementation beyond env-var precedence (which bypasses fallback caching if `AZDO_TOKEN` is set).
- **Residual risk:** The most convenient developer auth modes degrade into repeated 401/403 failures after token expiry. This is particularly painful in always-on MCP servers.
- **Recommendation:** Track token expiry and refresh proactively, or cache the credential provider path rather than the token string. At minimum, invalidate cached fallback credentials after the first auth failure.

### T-12
- **Category:** Denial of Service
- **Component:** Anonymous fallback behavior
- **Threat:** If Azure Identity or `az` resolution fails, the tool silently downgrades to anonymous mode. Public data continues to work, but private calls fail later, and callers may not realize they lost authenticated access.
- **Severity:** Medium
- **Current mitigation:** 401/403 messages are actionable and show the final auth source; anonymous mode is intentional for public repos.
- **Residual risk:** Users and AI agents can make decisions on incomplete public data while believing they are still authenticated. This is an availability and correctness problem, not just a UX quirk.
- **Recommendation:** Expose auth state explicitly to callers and consider a strict mode for MCP/CLI that refuses anonymous fallback after an authenticated path was expected.

### T-13
- **Category:** Denial of Service
- **Component:** PAT/JWT auto-detection
- **Threat:** A legitimate token that is misclassified by `LooksLikeJwt()` causes every request to be built with the wrong scheme and fail until the environment is changed or the process restarts into a different auth path.
- **Severity:** Medium
- **Current mitigation:** Azure DevOps rejects the bad request rather than accepting a confused credential.
- **Residual risk:** The failure can be persistent and hard to diagnose, especially for AI agents that only see later 401s.
- **Recommendation:** Same as T-4: add an explicit token-type override and improve auth-failure messages to mention possible misclassification.

## Elevation of Privilege

### T-14
- **Category:** Elevation of Privilege
- **Component:** MCP server trust boundary
- **Threat:** An AI agent with tool access can leverage the server's AzDO credential breadth, which may exceed the principal intended for that agent or client. This is especially relevant when the same server services multiple agents, repos, or tenants.
- **Severity:** High
- **Current mitigation:** Optional API-key gating for HTTP; stdio inherits local-user boundaries; the auth chain does not broaden itself beyond the chosen env/Azure CLI/`az` identity.
- **Residual risk:** The server becomes a privilege concentrator. A prompt-injected or over-entitled client can query private AzDO resources simply because the server can.
- **Recommendation:** Run shared MCP servers under least-privilege AzDO identities, segment servers by trust zone, and prefer per-request or per-tenant AzDO delegation where practical.

---

## Recommended Actions (Priority Order)

1. **Fix token refresh for non-env credentials.** This is the highest-value reliability/security improvement for long-lived MCP servers.
2. **Make AzDO auth scope explicit in HTTP mode.** Either support per-request AzDO auth or document clearly that AzDO is server-scoped.
3. ✅ **Done in this PR:** Removed the implicit `string` conversion to eliminate this token-leak footgun.
4. **Add explicit auth-state visibility.** Surface what path succeeded, what failed, and whether the process is currently anonymous.
5. **Align cache isolation with AzDO auth context.** Otherwise cached private AzDO data can outlive the process in a weaker trust zone than the live credential.
6. ✅ **Done in this PR:** Added an explicit `AZDO_TOKEN_TYPE` override to avoid scheme confusion.
7. ✅ **Done in this PR:** Redact unexpected error bodies before surfacing them to MCP clients.

---

## Merge Gate Assessment

**Recommended disposition:** **Approve PR #19 only with follow-up security work tracked.**

The chain is a reasonable design for developer ergonomics and public-repo usability, and the immediate implementation avoids the worst classes of token leakage and shell injection. The follow-up work should focus on **refresh semantics, auth-state observability, shared-server privilege boundaries, and cache/auth coupling**, rather than on replacing the overall credential order.
