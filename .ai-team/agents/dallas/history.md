# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)

## Core Context (summarized through 2026-02-15)

**Architecture reviews produced:** Initial code review (namespace collision, god class, no DI/tests), P0 foundation design (IHelixApiClient, DI, HelixException, CancellationToken), stdio MCP transport (Option B — `hlx mcp` subcommand), US-4 auth design (HELIX_ACCESS_TOKEN env var), multi-auth analysis (deferred), cache design (SQLite-backed, decorator pattern, WAL mode), HTTP/SSE multi-client auth (IHttpContextAccessor + scoped DI).

**Key design patterns established:**
- Decorator pattern for caching (CachingHelixApiClient wrapping IHelixApiClient)
- Console logs for running jobs must never be cached
- Cache TTL matrix: 15s/30s running, 1h/4h completed
- Cache isolation by auth token hash (SHA256) → separate SQLite DBs
- `IHelixTokenAccessor` abstraction: env var for stdio, HttpContext for HTTP
- MCP tools are thin wrappers over HelixService — business logic stays in HelixService
- MCP tool naming: `hlx_{verb}` or `hlx_{noun}` pattern with `_` separators
- MCP descriptions: expose behavioral contracts (what/inputs/outputs), NOT implementation mechanics
- For file scanning: one generic tool + one convenience alias (not per-type tool sprawl)

**MCP API review findings (2026-02-13):**
- No `hlx_list_work_items` — consumers must use hlx_status for navigation (N+1 problem)
- URL resolution boilerplate shared across 5 of 9 tools — tolerable at current scale
- `hlx_status` description should mention `failureCategory` as completeness fix

**Threat model review (2025-07-23):** Approved Ash's STRIDE analysis. All 10 MCP tools, both transports, cache, filesystem covered. Minor gap: TryResolveJobAndWorkItem can't handle `%2F` in work item names (correctness bug, not security).

**Remote search design (2026-02-13):** download-search-delete pattern. No regex (ReDoS risk). TRX parsing requires XXE protection. US-31 (search file), US-32 (TRX parsing) created. Structured console log parsing (US-22 partial) deferred.

**Value-add analysis (2025-07-23):** 12 enhancements cataloged — 5 major (cache, TTL, failure classification, TRX parsing, remote search), 3 significant (URL parsing, file discovery, batch status), 3 moderate, 1 minor.

**UseStructuredContent review (2025-07-24):** APPROVED. Clean migration from Task<string> to typed returns. Wire-compatible (all [JsonPropertyName] camelCase preserved). FileInfo_ trailing underscore acceptable but HelixFileInfo cleaner. hlx_logs correctly excluded. Error handling: McpException for tool errors, ArgumentException for param validation. Skill extracted to `.ai-team/skills/mcp-structured-content/SKILL.md`.

📌 Team update (2026-02-11): MatchesPattern internal static — decided by Lambert
📌 Team update (2026-02-11): Documentation audit — decided by Kane
📌 Team update (2026-02-11): P0 Foundation design review — decided by Dallas
📌 Team update (2026-02-11): Requirements backlog (30 US) — decided by Ash
📌 Team update (2026-02-11): US-17/US-24/US-30/US-29/US-10/US-23/US-21/US-18/US-11 implemented — decided by Ripley
📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry
📌 Team update (2025-02-12): NuGet Trusted Publishing workflow — decided by Ripley
📌 Team update (2026-02-13): HTTP/SSE auth tests (L-HTTP-1–5) — decided by Lambert
📌 Team update (2026-02-13): US-9 script removability — decided by Ash
📌 Team update (2026-02-13): US-6 download E2E — decided by Lambert
📌 Team update (2026-02-13): Requirements audit — audited by Ash
📌 Team update (2026-02-13): P1 security fixes E1+D1 — decided by Ripley
📌 Team update (2026-02-13): Security validation tests — decided by Lambert
📌 Team update (2026-02-13): Remote search design — decided by Dallas
📌 Team update (2026-02-13): Status filter changed — decided by Larry/Ripley
📌 Team update (2026-02-15): Per-invocation temp dirs — decided by Ripley
📌 Team update (2026-02-15): CI version validation — decided by Ripley
📌 Team update (2026-03-01): UseStructuredContent refactor approved — typed return objects with UseStructuredContent=true for all 12 MCP tools (hlx_logs excepted). FileInfo_ naming noted as non-blocking. No breaking wire-format changes. — decided by Dallas
