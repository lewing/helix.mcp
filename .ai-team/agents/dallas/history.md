# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)
- **No tests exist yet** — test project needs to be created

## Summarized History (through 2026-02-12)

**Architecture reviews produced:** Initial code review (namespace collision, god class, no DI/tests), P0 foundation design (IHelixApiClient, DI, HelixException, CancellationToken), stdio MCP transport (Option B — `hlx mcp` subcommand), US-4 auth design (HELIX_ACCESS_TOKEN env var), multi-auth analysis (deferred — OS/MCP client handles it), cache design (SQLite-backed, decorator pattern, ICacheStore interface, WAL mode), HTTP/SSE multi-client auth architecture (IHttpContextAccessor + scoped DI).

**Key design patterns established:**
- Decorator pattern for caching (CachingHelixApiClient wrapping IHelixApiClient)
- Console logs for running jobs must never be cached
- Cache TTL matrix: 15s/30s running, 1h/4h completed
- Cache isolation by auth token hash (SHA256) → separate SQLite DBs
- MCP protocol is session-scoped auth, not per-tool-call
- `IHelixTokenAccessor` abstraction: env var for stdio, HttpContext for HTTP
- MCP SDK: `ScopeRequests=true` (default), `IHttpContextAccessor` works with `PerSessionExecutionContext=false`

**Sessions facilitated:** P0 foundation (2026-02-11), P1 features (2026-02-11), cache implementation (2026-02-12), cache security (2026-02-12)

📌 Team update (2026-02-11): MatchesPattern changed to internal static — decided by Lambert
📌 Team update (2026-02-11): Documentation audit — decided by Kane
📌 Team update (2026-02-11): P0 Foundation design review — decided by Dallas
📌 Team update (2026-02-11): Requirements backlog (30 US) — decided by Ash
📌 Team update (2026-02-11): US-17/US-24/US-30/US-29/US-10/US-23/US-21/US-18/US-11 implemented — decided by Ripley
📌 Team update (2025-02-12): PackageId renamed to lewing.helix.mcp — decided by Ripley/Larry
📌 Team update (2025-02-12): NuGet Trusted Publishing workflow added — decided by Ripley


📌 Team update (2026-02-13): HTTP/SSE auth test suites written by Lambert (L-HTTP-1 through L-HTTP-5) — 45 tests covering token accessors, factories, concurrent cache, and HTTP context token extraction. All 252 tests passing. — decided by Lambert

📌 Team update (2026-02-13): US-9 script removability analysis complete — 100% core API coverage, 3-phase migration plan, Phase 1 can proceed immediately — decided by Ash
📌 Team update (2026-02-13): US-6 download E2E verification complete — 46 tests, all 298 tests pass, all P1s done — decided by Lambert
