---
name: "mcp-threat-modeling"
description: "STRIDE threat modeling patterns specific to MCP servers"
domain: "security-analysis"
confidence: "low"
source: "earned"
---

## Context
MCP (Model Context Protocol) servers have a distinctive threat model compared to typical APIs. They are invoked by AI agents which may be prompt-injected, run in two transport modes (stdio and HTTP) with very different trust boundaries, and often handle CI/infrastructure data that may contain secrets.

## Patterns

### MCP-Specific Trust Boundaries
Always identify these trust boundaries for any MCP server:
1. **MCP Client → Server** — Stdio is process-local (trusted). HTTP is network-accessible (untrusted). Treat these as fundamentally different security postures.
2. **AI Agent → MCP Tool Parameters** — Parameters come from an AI agent that may be prompt-injected. Treat all MCP tool inputs as untrusted, even in stdio mode.
3. **Server → External APIs** — Outbound API calls use credentials. SSRF risk if the server fetches arbitrary URLs based on agent input.
4. **Server → Local Filesystem** — Downloads and caches write to disk. Path components derived from external data require sanitization.

### Stdio vs HTTP Security Divergence
Stdio MCP servers inherit the host process's security context — authentication is implicit (the user who launched the process). HTTP MCP servers are network services that need explicit authentication. A common mistake is building a tool as stdio-first, then adding HTTP transport without adding auth middleware.

### AI Agent as Untrusted Input Source
MCP tool parameters originate from an AI agent, not directly from a human. An agent can be prompt-injected to:
- Pass malicious URLs to download tools (SSRF)
- Request unbounded batch operations (DoS)
- Construct path-traversal payloads in file/work item names
Always validate and sanitize MCP tool inputs as if they were user input from the internet.

### Cache Security for MCP Servers
MCP stdio servers are often ephemeral processes. Cross-process caches (SQLite, file-based) persist data beyond session lifetime. Key concerns:
- Auth context isolation — different tokens should get separate cache namespaces
- Cached data may contain secrets (CI logs, build artifacts)
- Cache location should be user-profile-scoped (not world-readable)

## Anti-Patterns
- **Assuming stdio means trusted inputs** — The AI agent's parameters are still untrusted due to prompt injection risk.
- **Adding HTTP transport without auth** — Stdio-first tools that grow HTTP support often forget to add authentication middleware.
- **Using regex for user-facing pattern matching** — ReDoS risk. Prefer simple string operations (`Contains`, `EndsWith`) for glob-like matching.
- **Unbounded batch/fan-out operations** — AI agents can request very large batch sizes. Always cap parallel operations.
