### 2026-02-12: Multi-auth support — defer, current design is sufficient

**By:** Dallas
**Requested by:** Larry Ewing

**What:** Analyzed whether hlx should support multiple simultaneous Helix auth tokens (e.g., public + internal, different orgs). Recommendation: **do not implement multi-auth**. The current single-token-per-process model is correct for our execution context.

**Why:**

## Architectural Analysis

### 1. Use cases examined

The plausible scenarios for multi-auth are:
- **Public vs internal jobs** in the same session — a user debugging CI failures across both public OSS and internal proprietary queues.
- **Different orgs/instances** — a user who works across multiple Helix deployments.
- **Different permission levels** — a read-only token for automated scanning, a write token for manual operations.

These are real scenarios, but they're **rare** in practice. The typical hlx user is debugging one CI pipeline at a time. When they switch contexts, they switch environment variables.

### 2. Current constraints make multi-auth unnecessary

**MCP stdio model:** The MCP server runs as `hlx mcp` — a fresh stdio process spawned by the MCP client (VS Code, Claude Desktop, Copilot CLI). The MCP client configuration already specifies env vars per server entry:

```json
{
  "mcpServers": {
    "hlx-public": {
      "command": "hlx",
      "args": ["mcp"],
      "env": {}
    },
    "hlx-internal": {
      "command": "hlx",
      "args": ["mcp"],
      "env": { "HELIX_ACCESS_TOKEN": "secret-token" }
    }
  }
}
```

The user can already configure **two separate MCP server entries** with different tokens. Each gets its own process, its own DI container, its own cache context (the SHA256-based isolation in `CacheOptions` handles this perfectly). This is the MCP-native way to handle multi-auth. We don't need to reinvent it.

**CLI model:** For CLI usage, users can trivially do:
```
HELIX_ACCESS_TOKEN=abc hlx status <internal-job>
hlx status <public-job>
```

Or use shell aliases / direnv / .env files. The operating system already has the right abstraction for per-invocation environment configuration.

### 3. Design options rejected

| Option | Verdict | Reason |
|--------|---------|--------|
| **A: Named connections** (`--connection internal`) | Reject | Requires a connection registry (config file or env var naming scheme). Adds complexity to every command signature. Solves a problem users don't have. |
| **B: Per-request MCP auth passthrough** | Reject | MCP protocol does support auth at the transport level, but it's session-scoped, not per-tool-call. Would require breaking the current clean DI model where `IHelixApiClient` is a singleton. |
| **C: Multiple env vars** (`HELIX_ACCESS_TOKEN_INTERNAL`) | Reject | Ad-hoc naming convention, no discoverability, pollutes the env var namespace. Which one wins when both are set? |
| **D: Config file with profiles** | Reject | Adds file I/O, a config schema, a config file location convention, and a `hlx config` command. Massive complexity for marginal value. |
| **E: Do nothing** | ✅ Accept | The MCP client config already solves the MCP case. The shell environment already solves the CLI case. |

### 4. MCP protocol considerations

The MCP specification (as of the version we're using with `ModelContextProtocol` v0.8.0-preview.1) treats auth as session-level, not per-tool-call. There's no standard mechanism for an MCP client to pass a different bearer token per `tools/call` request. The auth context is established when the transport connects. This means multi-auth within a single MCP session would require a custom, non-standard extension — which would break interop with standard MCP clients.

### 5. Cache implications

The cache is already correctly isolated per token via `CacheOptions.AuthTokenHash` → separate SQLite DB + artifact directory. Two MCP server instances with different tokens get completely independent caches. No changes needed.

### 6. What we SHOULD do instead (if anything)

If users report friction switching between authenticated and unauthenticated access, the right move is:

1. **Better error messages** — when a 401/403 occurs, suggest both the env var AND the MCP multi-server-entry pattern in the error message. Today we only mention the env var.
2. **`hlx auth check`** — a diagnostic command that reports whether a token is set, whether it's valid (test API call), and what access level it provides. Zero-cost, high-value for debugging auth issues.

Neither of these requires any architectural changes.

### Decision

**Do not implement multi-auth.** The current single-token-per-process model is the correct abstraction for both execution contexts (CLI and MCP). Multi-auth is already achievable through existing OS and MCP client mechanisms. Adding it to hlx would introduce complexity with no corresponding user value.

Re-evaluate if: (a) Helix deploys multiple independent instances that users need to query in a single debugging session, or (b) MCP protocol adds per-tool-call auth, or (c) multiple users report the env var switching workflow as painful.
