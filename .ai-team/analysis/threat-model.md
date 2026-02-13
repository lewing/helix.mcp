# STRIDE Threat Model — lewing.helix.mcp

**Date:** 2025-07-23
**Author:** Ash (Product Analyst)
**Scope:** lewing.helix.mcp v0.1.x — CLI tool + MCP server for Helix CI test infrastructure
**Methodology:** STRIDE per-component

---

## System Overview

lewing.helix.mcp is a .NET 10 tool that queries the Microsoft Helix test infrastructure API and exposes results to AI agents via the Model Context Protocol. It runs in two modes:

1. **Stdio MCP** (`hlx mcp`) — single-process, launched by MCP clients (VS Code, Copilot CLI). Default mode.
2. **HTTP MCP** (`HelixTool.Mcp`) — ASP.NET Core server, shared/remote deployments with per-request auth.

### Data Flow Diagram

```
┌─────────────┐   stdio/HTTP    ┌─────────────┐   HTTPS    ┌──────────────┐
│  MCP Client │ ◄────────────► │   hlx        │ ─────────► │ Helix API    │
│ (Copilot,   │                │ (CLI/MCP)    │            │ helix.dot.net│
│  VS Code)   │                │              │            └──────────────┘
└─────────────┘                │              │
                               │  ┌─────────┐ │   HTTPS    ┌──────────────┐
                               │  │ SQLite   │ │ ─────────► │ Azure Blob   │
                               │  │ Cache    │ │            │ Storage      │
                               │  └─────────┘ │            │ (artifacts)  │
                               └──────────────┘            └──────────────┘
```

### Trust Boundaries

| Boundary | Description |
|----------|-------------|
| **TB1: MCP Client → hlx** | AI agent sends tool calls. In stdio mode, trusted (same machine, same user). In HTTP mode, untrusted (network-accessible). |
| **TB2: hlx → Helix API** | Outbound HTTPS to helix.dot.net. Helix API responses are semi-trusted (Microsoft internal infra, but content includes user-submitted data like work item names). |
| **TB3: hlx → Azure Blob Storage** | Direct URL downloads via `DownloadFromUrlAsync`. URLs come from Helix API responses. Arbitrary HTTP endpoints. |
| **TB4: hlx → Local Filesystem** | Cache writes to `%LOCALAPPDATA%\hlx\`, temp files to `%TEMP%`. Path components derived from untrusted input (work item names, file names). |
| **TB5: Environment → hlx** | `HELIX_ACCESS_TOKEN` env var, `HLX_CACHE_MAX_SIZE_MB`. Process-level trust. |

### Attack Surface

| Interface | Exposure | Authentication |
|-----------|----------|---------------|
| Stdio MCP (`hlx mcp`) | Local process only | None (inherits user context) |
| HTTP MCP (`HelixTool.Mcp`) | Network (configurable port) | Optional `Authorization: Bearer/token` header |
| CLI commands | Local terminal | None (inherits user context) |
| SQLite cache | Local filesystem | File permissions only |
| Temp file downloads | Local filesystem | File permissions only |

### Sensitive Data Inventory

| Data | Location | Sensitivity |
|------|----------|-------------|
| `HELIX_ACCESS_TOKEN` | Env var, passed to Helix SDK | High — grants access to internal CI data |
| Auth token hash | Cache directory name (`cache-{hash}`) | Low — SHA256 truncated to 8 hex chars, one-way |
| Console log content | Temp files, cache | Medium — may contain secrets accidentally logged by CI |
| Work item metadata | SQLite cache | Low — CI job names, exit codes, machines |
| Artifact files (binlogs, TRX) | Cache + temp dirs | Medium — build artifacts may contain source paths, env vars |
| Helix API responses | SQLite `json_value` column | Low-Medium — job metadata, machine names |

---

## STRIDE Analysis

### 1. Spoofing

#### S1: HTTP MCP server has no mandatory authentication
- **Component:** `HelixTool.Mcp/Program.cs` — `app.MapMcp()` with no auth middleware
- **Code evidence:** The HTTP server registers `HttpContextHelixTokenAccessor` which reads `Authorization` headers, but this is used for *Helix API auth delegation*, not for authenticating MCP clients. There is no `[Authorize]`, no auth middleware, no API key check. Any network-reachable client can invoke MCP tools.
- **Severity:** **High** (HTTP mode) / N/A (stdio mode)
- **Current mitigation:** Stdio mode (default) is process-local and inherits OS-level access control. HTTP mode is opt-in and documented as "for remote/shared servers."
- **Recommendation:** If HTTP mode is deployed on a network, add authentication middleware (API key, JWT, or mutual TLS). At minimum, document that HTTP mode MUST be behind a reverse proxy with auth, or bind to localhost only. Consider defaulting `--urls` to `http://localhost:5000` and requiring explicit opt-in for network binding.

#### S2: Helix API token passed via environment variable
- **Component:** `Program.cs` line 12, `Commands.Mcp()` line 441 — `Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")`
- **Code evidence:** Token is read once at startup and stored in `EnvironmentHelixTokenAccessor._token` for the lifetime of the process.
- **Severity:** **Low**
- **Current mitigation:** Environment variables are the standard mechanism for passing secrets to CLI tools and MCP servers. The MCP client config spec (`env` field) is designed for this. Token is never logged or serialized.
- **Recommendation:** No action needed. This follows the established pattern (cf. `GITHUB_TOKEN`, `AZURE_DEVOPS_PAT`). The token is never written to disk (only its SHA256 hash appears in cache paths).

---

### 2. Tampering

#### T1: Cache poisoning via SQLite database
- **Component:** `SqliteCacheStore.cs` — cached JSON responses served to subsequent requests
- **Code evidence:** Cache keys are constructed from sanitized job IDs and work item names (`CacheSecurity.SanitizeCacheKeySegment`). Cached data is trusted on read without re-validation.
- **Severity:** **Medium**
- **Current mitigation:** 
  - SQLite database is in `%LOCALAPPDATA%\hlx\` with user-level file permissions.
  - Auth context isolation: different tokens get different cache directories (`cache-{hash}`).
  - Cache entries have TTLs (15s–4h) limiting staleness window.
- **Recommendation:** The main risk is a local attacker modifying the SQLite database to inject false API responses. Since the attacker would need local file access (at which point they could tamper with the tool binary itself), this is acceptable risk. No action needed beyond documenting that cache integrity depends on filesystem permissions.

#### T2: Downloaded artifact tampering
- **Component:** `HelixService.DownloadFromUrlAsync` (line 381), `DownloadFilesAsync` (line 319)
- **Code evidence:** Files are downloaded via HTTPS and written to temp/cache directories. No checksum verification. `DownloadFromUrlAsync` accepts arbitrary URLs — the URL is taken from Helix API responses (file links), but the `hlx_download_url` MCP tool and `download-url` CLI command accept user-provided URLs directly.
- **Severity:** **Medium**
- **Current mitigation:**
  - Helix API file links are HTTPS (TLS protects in transit).
  - `CacheSecurity.SanitizePathSegment` prevents path traversal in filenames.
  - `CacheSecurity.ValidatePathWithinRoot` validates output paths stay within allowed directories.
- **Recommendation:** The `hlx_download_url` tool accepts arbitrary URLs from the MCP client (i.e., the AI agent). An agent could be prompt-injected to download from a malicious URL. Consider: (1) adding an allowlist of domains for direct URL downloads (e.g., `helix.dot.net`, `*.blob.core.windows.net`), or (2) documenting that `hlx_download_url` should only be used with URLs obtained from `hlx_files` output.

#### T3: Path traversal via work item names or file names
- **Component:** `HelixService.cs` lines 173-178 (DownloadConsoleLogAsync), lines 333-348 (DownloadFilesAsync), `SqliteCacheStore.cs` lines 186-190 (SetArtifactAsync)
- **Code evidence:** Work item names and file names come from the Helix API and are user-submitted data (Helix job creators choose work item names). These are used in file path construction.
- **Severity:** **Low** (mitigated)
- **Current mitigation:** ✅ **Well-mitigated.** Every path construction site uses:
  - `CacheSecurity.SanitizePathSegment()` — replaces `/`, `\`, `..` with underscores
  - `CacheSecurity.ValidatePathWithinRoot()` — checks resolved path is within allowed root
  - Both are applied consistently across `DownloadConsoleLogAsync`, `DownloadFilesAsync`, `DownloadFromUrlAsync`, and `SetArtifactAsync`.
- **Recommendation:** No action needed. Path traversal protection is thorough and consistently applied. This is one of the strongest security patterns in the codebase.

---

### 3. Repudiation

#### R1: No audit logging of MCP tool invocations
- **Component:** `HelixMcpTools.cs` — all tool methods
- **Code evidence:** MCP tool calls are not logged. In stdio mode, the MCP SDK logs to stderr at `LogLevel.Trace` (`Program.cs` line 439), but there's no structured audit trail of what jobs were queried, what files were downloaded, or what search patterns were used.
- **Severity:** **Low**
- **Current mitigation:** Stdio mode — the MCP client (VS Code, Copilot) maintains its own conversation history. HTTP mode — ASP.NET Core has request logging if enabled.
- **Recommendation:** For HTTP deployments, consider adding structured logging of tool invocations (tool name, jobId, timestamp). This helps with post-incident analysis if a token is compromised. Not urgent for stdio-only usage.

---

### 4. Information Disclosure

#### I1: Console logs may contain secrets
- **Component:** `HelixService.GetConsoleLogContentAsync` (line 210), `SearchConsoleLogAsync` (line 562)
- **Code evidence:** Console logs are CI build/test output. They are returned in full to the MCP client and cached locally. CI logs frequently contain accidentally leaked secrets (connection strings, tokens, passwords printed by build scripts).
- **Severity:** **Medium**
- **Current mitigation:** 
  - Helix API access control (token required for internal jobs) is the primary gate.
  - Cached logs have 1h TTL for completed jobs, never cached for running jobs.
  - The tool returns log content verbatim — no secret scanning.
- **Recommendation:** This is a known risk inherent to any CI log viewer. Secret scanning would add latency and complexity disproportionate to the benefit, since the Helix API itself serves the same content. Document that operators should treat cached log files (`%LOCALAPPDATA%\hlx\artifacts\`) as potentially containing secrets and ensure appropriate file permissions. No code change needed.

#### I2: Cache persists sensitive data to disk after session ends
- **Component:** `SqliteCacheStore.cs` — `cache.db` and `artifacts/` directory
- **Code evidence:** Cached API responses (job metadata, work item details) and artifact files (console logs, binlogs) persist on disk indefinitely (up to 7-day artifact expiry, 4-hour metadata TTL). The cache directory `%LOCALAPPDATA%\hlx\cache-{hash}\` survives process exit.
- **Severity:** **Low**
- **Current mitigation:**
  - Cache is in user-profile directory (OS file permissions).
  - Auth context isolation prevents cross-token data leakage.
  - `hlx cache clear` wipes all contexts.
  - Metadata has TTL-based expiry; artifacts expire after 7 days.
- **Recommendation:** Consider adding a note to documentation about clearing cache when switching between security contexts or on shared machines. The current `hlx cache clear` command is sufficient.

#### I3: Token hash in directory name is not a secret leak
- **Component:** `CacheOptions.ComputeTokenHash` (line 54) — `SHA256.HashData()[..8]`
- **Code evidence:** The first 8 hex characters of SHA256(token) are used in the cache directory name. This is a one-way hash with 32 bits of entropy — not reversible to the token.
- **Severity:** **Low** (informational)
- **Current mitigation:** SHA256 is preimage-resistant. 8 hex chars provide sufficient uniqueness for cache isolation without leaking the token.
- **Recommendation:** No action needed. This is a sound design.

#### I4: HTTP MCP server exposes data to any network client (see S1)
- **Component:** Same as S1.
- **Severity:** **High** (if deployed on a network without auth)
- **Current mitigation:** Same as S1.
- **Recommendation:** Same as S1. The primary fix is adding authentication to the HTTP server.

---

### 5. Denial of Service

#### D1: Unbounded parallel API calls in batch operations
- **Component:** `HelixService.GetBatchStatusAsync` (line 486) — `SemaphoreSlim(5)`, `GetJobStatusAsync` (line 49) — `SemaphoreSlim(10)`
- **Code evidence:** `GetBatchStatusAsync` accepts an unbounded list of job IDs. Each job triggers `GetJobStatusAsync`, which itself fans out up to N work item detail requests. A batch of 100 jobs with 100 work items each = 10,000 Helix API calls.
- **Severity:** **Medium** (self-inflicted resource exhaustion)
- **Current mitigation:**
  - SemaphoreSlim throttling (5 concurrent jobs, 10 concurrent work item details per job).
  - Caching reduces repeated calls.
  - `FindFilesAsync` has a `maxItems` parameter (default 30) limiting scan scope.
- **Recommendation:** Consider adding a maximum batch size limit (e.g., 50 jobs) in `GetBatchStatusAsync` to prevent accidental resource exhaustion. The MCP tool `hlx_batch_status` accepts `string[] jobIds` without a cap. An AI agent could be instructed to query hundreds of jobs.

#### D2: Large console log downloads consume memory
- **Component:** `GetConsoleLogContentAsync` (line 218) — `reader.ReadToEndAsync()`, `SearchConsoleLogAsync` (line 574) — `File.ReadAllLinesAsync()`
- **Code evidence:** Console logs are read entirely into memory. Helix console logs can be 10-100+ MB for long-running test suites. `GetConsoleLogContentAsync` reads the entire stream, then optionally truncates to `tailLines`.
- **Severity:** **Low**
- **Current mitigation:** `hlx_logs` defaults to `tail=500` lines, but the full log is still downloaded and read into memory before truncation.
- **Recommendation:** Consider streaming the log (read line by line from the tail) for very large logs. Not urgent — current usage patterns involve logs in the 1-10 MB range. The cache layer helps avoid repeated downloads.

#### D3: Cache can fill disk to 1 GB (configurable)
- **Component:** `CacheOptions.MaxSizeBytes` default 1 GB, `SqliteCacheStore.EvictLruIfOverCapAsync`
- **Severity:** **Low**
- **Current mitigation:** Max size is enforced by LRU eviction. Configurable via `HLX_CACHE_MAX_SIZE_MB`. Can be disabled with `0`.
- **Recommendation:** No action needed. 1 GB default is reasonable for a CI tool.

---

### 6. Elevation of Privilege

#### E1: `DownloadFromUrlAsync` fetches from arbitrary URLs
- **Component:** `HelixService.DownloadFromUrlAsync` (line 381), `HelixMcpTools.DownloadUrl` (line 163)
- **Code evidence:** The `hlx_download_url` MCP tool takes a `string url` parameter and downloads from it using a static `HttpClient` with no URL validation. An AI agent could be prompt-injected to download from `file:///etc/passwd` (though `HttpClient` typically rejects non-HTTP schemes) or from internal network endpoints (SSRF).
- **Severity:** **Medium**
- **Current mitigation:**
  - `HttpClient.GetAsync` rejects `file://` scheme by default.
  - Downloaded content is written to a temp file — no execution.
  - `CacheSecurity.SanitizePathSegment` and `ValidatePathWithinRoot` prevent path traversal in the output path.
- **Recommendation:**
  1. Validate the URL scheme is `https` (or `http`) before fetching.
  2. Consider restricting to known Helix blob storage domains to prevent SSRF against internal networks.
  3. The `static HttpClient s_httpClient = new()` in `HelixService` (line 374) is unauthenticated — it doesn't carry the Helix token, so SSRF requests won't leak credentials. This is actually good.

#### E2: MCP tool inputs are not validated for injection
- **Component:** `HelixMcpTools.cs` — all tool methods accept string parameters from the AI agent
- **Code evidence:** Parameters like `jobId`, `workItem`, `pattern` flow through `HelixIdResolver.ResolveJobId` (which validates GUID format or URL structure) and `ArgumentException.ThrowIfNullOrWhiteSpace`. The `pattern` parameter in `MatchesPattern` does not use regex — it's a simple `Contains`/`EndsWith` check, so there's no regex injection risk.
- **Severity:** **Low** (mitigated)
- **Current mitigation:**
  - `ResolveJobId` validates input is a GUID or well-formed URL.
  - SQLite cache uses parameterized queries (no SQL injection).
  - `MatchesPattern` is not regex-based (no ReDoS risk).
  - `SearchConsoleLogAsync` uses `string.Contains` for pattern matching (no regex).
- **Recommendation:** No action needed. Input validation is appropriate for the threat level.

#### E3: No process isolation between MCP server and host
- **Component:** `hlx mcp` runs in the same process as the calling terminal
- **Severity:** **Low**
- **Current mitigation:** This is inherent to the stdio MCP transport model. The MCP server runs with the same permissions as the user who launched it. This is by design — the MCP specification expects stdio servers to inherit the host process's security context.
- **Recommendation:** No action needed. Stdio MCP servers are designed to run in the user's security context.

---

## Summary of Findings

| ID | Category | Threat | Severity | Status |
|----|----------|--------|----------|--------|
| S1 | Spoofing | HTTP MCP server has no auth | **High** | ⚠️ Open — needs auth for network deployments |
| S2 | Spoofing | Token via env var | Low | ✅ Acceptable — standard pattern |
| T1 | Tampering | Cache poisoning via SQLite | Medium | ✅ Acceptable — local filesystem trust |
| T2 | Tampering | Arbitrary URL download | Medium | ⚠️ Consider domain allowlist |
| T3 | Tampering | Path traversal | Low | ✅ Well-mitigated — `CacheSecurity` |
| R1 | Repudiation | No audit logging | Low | ℹ️ Consider for HTTP mode |
| I1 | Info Disclosure | Secrets in console logs | Medium | ✅ Acceptable — inherent to CI tools |
| I2 | Info Disclosure | Cache persists after session | Low | ✅ Acceptable — TTL + clear command |
| I3 | Info Disclosure | Token hash in dir name | Low | ✅ Sound design |
| I4 | Info Disclosure | HTTP server exposes data | High | ⚠️ Same root cause as S1 |
| D1 | DoS | Unbounded batch size | Medium | ⚠️ Consider max batch limit |
| D2 | DoS | Large log memory usage | Low | ℹ️ Consider streaming for edge cases |
| D3 | DoS | Cache disk usage | Low | ✅ Configurable + eviction |
| E1 | EoP | SSRF via download-url | Medium | ⚠️ Validate scheme, consider domain allowlist |
| E2 | EoP | MCP input injection | Low | ✅ Mitigated — no regex, parameterized SQL |
| E3 | EoP | No process isolation | Low | ✅ By design — stdio MCP model |

## Priority Recommendations

### P0 — Address before deploying HTTP mode on a network
1. **S1/I4: Add authentication to HTTP MCP server.** At minimum, require an API key via middleware. Alternatively, default to localhost binding and require explicit opt-in for network exposure.

### P1 — Security hardening
2. **E1: Validate URL scheme in `DownloadFromUrlAsync`.** Reject non-HTTP(S) schemes. Consider a domain allowlist for known Helix storage endpoints.
3. **T2: Document that `hlx_download_url` accepts arbitrary URLs.** If domain restriction is too limiting, at least warn operators.
4. **D1: Add max batch size limit.** Cap `GetBatchStatusAsync` at 50 jobs. Return a clear error if exceeded.

### P2 — Defense in depth
5. **R1: Add structured logging for HTTP mode** tool invocations.
6. **I1/I2: Document cache security expectations** — treat `%LOCALAPPDATA%\hlx\` as potentially containing CI secrets.

## Security Patterns Observed (Positive)

The codebase demonstrates several good security practices:

1. **Consistent path traversal protection** — `CacheSecurity.SanitizePathSegment` + `ValidatePathWithinRoot` applied at every filesystem write site. No gaps found.
2. **Parameterized SQL** — All SQLite queries use `cmd.Parameters.AddWithValue`, no string interpolation into SQL.
3. **Token isolation** — Different auth tokens get completely separate cache directories. No cross-token data leakage.
4. **No token logging/serialization** — `HELIX_ACCESS_TOKEN` is read once, passed to the SDK, never logged or written to disk.
5. **Write-then-rename pattern** — `SqliteCacheStore.SetArtifactAsync` uses atomic file writes to avoid corruption.
6. **WAL mode for SQLite** — Enables safe concurrent access across multiple `hlx mcp` processes.
7. **Static HttpClient for URL downloads** — Unauthenticated, so SSRF won't leak the Helix token.
8. **No regex in user-facing pattern matching** — `MatchesPattern` uses simple string operations, eliminating ReDoS risk.
