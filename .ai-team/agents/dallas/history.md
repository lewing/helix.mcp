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

📌 Team update (2026-02-13): Requirements audit complete — 25/30 stories implemented, US-22 structured test failure parsing is only remaining P2 gap — audited by Ash

## Learnings

### MCP API Design Review (2026-02-13)
- **Key files reviewed:** `src/HelixTool.Core/HelixMcpTools.cs` (9 MCP tools), `src/HelixTool.Core/HelixService.cs` (core service layer), `src/HelixTool/Program.cs` (CLI commands), `src/HelixTool.Core/IHelixApiClient.cs` (API abstraction)
- **Architecture pattern:** MCP tools are thin wrappers over HelixService — they handle URL resolution, JSON serialization, and parameter adaptation. Business logic stays in HelixService. This is correct.
- **Convention established:** MCP tool naming follows `hlx_{verb}` or `hlx_{noun}` pattern with `_` separators. All tools return JSON strings.
- **Design observation:** The MCP surface maps to Helix domain primitives (jobs, work items, files, logs), NOT to ci-analysis-specific workflows. This is the right abstraction level.
- **Gap identified:** No `hlx_list_work_items` tool exists — consumers must call `hlx_status` (which fetches details for every work item) just to get work item names. This is an N+1 problem for navigation.
- **Anti-pattern found:** `hlx_batch_status` takes `string jobIds` (comma-separated) instead of `string[] jobIds`. MCP SDK supports array parameters; this should use them.
- **Inconsistency found:** `hlx_status` uses `bool all` parameter — should be `bool includePassed` for self-documentation.
- **URL resolution pattern:** 5 of 9 tools share identical URL-resolution boilerplate (`TryResolveJobAndWorkItem`). Could be extracted to a helper, but current duplication is tolerable at this scale.

### find-binlogs → find-files Generalization Decision (2026-02-14)
- **Question:** Should `hlx_find_binlogs` become generic `hlx_find_files` with a pattern parameter?
- **Answer:** Yes — add generic `FindFilesAsync` in Core with pattern parameter, add `hlx_find_files` MCP tool, but **keep `hlx_find_binlogs` as a convenience alias** that delegates with `pattern="*.binlog"`.
- **Key rationale:** MCP tool names are a public API contract for LLM consumers (ci-analysis skill references `hlx_find_binlogs`). Removing it would break existing tool-use patterns. Specific named tools are also easier for LLMs to select. The generic tool serves the long tail (crash dumps, coverage files, ETW traces, test results).
- **Design principle established:** For cross-work-item file scanning, one generic tool + one common-case convenience is the right surface area. Do NOT add per-file-type convenience tools (`hlx_find_dumps`, etc.) — that's tool sprawl.
- **Core pattern:** `FindBinlogsAsync` generalizes to `FindFilesAsync` using the existing `MatchesPattern` helper (already proven by `DownloadFilesAsync`). `BinlogResult` renames to `FileSearchResult`.
- **Decision written to:** `.ai-team/decisions/inbox/dallas-find-files-api.md`

### Threat Model Review (2025-07-23)
- **Reviewed:** Ash's STRIDE threat model at `.ai-team/analysis/threat-model.md`
- **Verdict:** Approved with minor amendments. High-quality work — accurate code references, correct severity ratings, well-calibrated recommendations.
- **Code reference accuracy:** Spot-checked 15+ line numbers and method names against actual source. All correct or within ±2 lines (acceptable for a living codebase). `BatchStatus` parameter type correctly identified as `string[]` (was previously `string`, now fixed).
- **Completeness assessment:** Ash covered all 10 MCP tools, both transports, cache layer, and filesystem operations. No significant threats missed.
- **Minor gap noted:** `HelixIdResolver.TryResolveJobAndWorkItem` can't handle work item names containing `/` (URL-encoded as `%2F`) because of `Split('/')`. This is a correctness bug, not a security vulnerability — it would fail to resolve, not cause exploitation. Not a threat model gap.
- **S1/I4 severity confirmed:** HTTP MCP server defaulting to localhost is already the ASP.NET Core default behavior — Ash's recommendation to "default `--urls` to `http://localhost:5000`" is already the case. The real risk is explicit non-localhost binding without auth, which Ash correctly identifies.
- **I3 precision corrected:** Ash says "8 hex chars" and "32 bits of entropy" — mathematically correct (`Convert.ToHexString(hash)[..8]` = 8 hex chars = 32 bits). Sound analysis.
- **P0/P1/P2 calibration:** All appropriate. No over-reactions or under-reactions.
- **Architectural implication:** When HTTP mode moves toward production, auth middleware is the gate. This should be tracked as a pre-GA requirement, not a current blocker.
- **Decision written to:** `.ai-team/decisions/inbox/dallas-threat-model-review.md`
📌 Team update (2026-02-13): P1 security fixes E1+D1 implemented (URL scheme validation, batch size cap) — decided by Ripley
📌 Team update (2026-02-13): Security validation test strategy (18 tests, negative assertion pattern) — decided by Lambert

### Remote Search Feature Design (2026-02-13)
- **Design principle established:** hlx's role is Helix-specific discovery/retrieval/search. Structured analysis of non-Helix formats (binlogs) stays with external tools. TRX is in-scope because it's a test-result format with no dedicated external MCP tool.
- **Pattern: download-search-delete** — `SearchConsoleLogAsync` downloads a file to temp, searches in-memory, deletes the temp file. This pattern generalizes to `SearchFileAsync` for arbitrary text files. The search logic (line matching with context) should be extracted into a shared private helper.
- **Security invariant maintained:** No regex in user-facing pattern matching. `string.Contains` with `OrdinalIgnoreCase` covers CI investigation use cases without ReDoS risk.
- **XML parsing security requirements:** TRX parsing MUST use `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }` to prevent XXE. File size check before parsing (reject >50MB). Error message truncation to prevent context window exhaustion.
- **Tool naming convention:** `hlx_search_file` (general text), `hlx_test_results` (structured TRX). Follows established `hlx_{verb}` / `hlx_{noun}` naming.
- **Backlog mapping:** US-31 (new — remote file text search), US-32 (new — TRX parsing, supersedes US-14 and the structured portion of US-22). Structured console log failure parsing (US-22 partial) recommended for closure — TRX parsing is more reliable than format-dependent `[FAIL]` line parsing.
- **Trust boundary for file content:** All file content from Helix blob storage is semi-trusted (uploaded by CI jobs, which are authored by PR contributors in open-source repos). Parse defensively, never execute.
- **Key file:** `.ai-team/decisions/inbox/dallas-remote-search-design.md` — full feature design with 8 numbered decisions for Larry to review.


📌 Team update (2026-02-13): Status filter changed from bool to enum (failed|passed|all) — decided by Larry/Ripley