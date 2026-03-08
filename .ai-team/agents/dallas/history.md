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
📌 Team update (2026-03-03): Phase 1 auth UX architecture approved — `hlx login`/`logout`/`auth status` commands, `git credential` storage (Option A), `ChainedHelixTokenAccessor` with env var > stored > null precedence. 7 work items for Ripley. ICredentialStore + GitCredentialStore in Core, commands in CLI. No new NuGet deps required. — decided by Dallas

📌 Team update (2026-03-07): AzDO pipeline architecture design — produced comprehensive design document for adding Azure DevOps pipeline wrapping to helix.mcp. Key decisions: add as `AzDO/` folder in HelixTool.Core (not separate project), use HttpClient + System.Text.Json (not TFS SDK — avoids 40+ transitive deps), use Azure.Identity for auth (AzureDeveloperCliCredential), 7 MCP tools with `azdo_` prefix, reuse existing SqliteCacheStore with `azdo:` key prefix, no CLI commands Phase 1. Document at `.ai-team/decisions/inbox/dallas-azdo-architecture.md`. — decided by Dallas, awaiting Larry's review

## Learnings

- **Helix auth is opaque tokens only.** The Helix API uses `Authorization: token <TOKEN>` with server-generated opaque strings. No Entra/JWT/OAuth possible until the Helix service team adds support server-side. This is a hard constraint — don't revisit.
- **AzDO auth uses Azure Identity (Entra ID).** The scope is `499b84ac-1321-427f-aa17-267ca6975798/.default`. `AzureDeveloperCliCredential` (via `azd auth login`) is the primary credential for CLI tools. `Azure.Identity` handles token caching/refresh internally.
- **AzDO REST API is stable at v7.0.** The ci-analysis script uses `api-version=7.0` for all endpoints. The 7 endpoints we need (build, builds, timeline, log, changes, test runs, test results) are well-documented and unlikely to change.
- **Microsoft.TeamFoundationServer.Client SDK is too heavy for our use case.** It pulls 40+ transitive deps including Newtonsoft.Json and a CVE-affected System.Data.SqlClient. HttpClient + System.Text.Json is sufficient for 7 REST endpoints.
- **AzDO builds span two orgs: dnceng-public (PR builds) and dnceng (internal).** The API client must accept org/project as per-call parameters, not constructor-level config.
- **Timeline for in-progress builds must never be cached.** The timeline changes as jobs complete — the ci-analysis script explicitly skips cache writes for in-progress builds.
- **`git credential` is the right storage abstraction for CLI tools targeting .NET developers.** Zero new deps, cross-platform, delegates keychain management to the user's existing git credential helper. Same pattern `darc` uses.
- **IHelixTokenAccessor.GetAccessToken() is synchronous.** Changing to async would be a cross-cutting change affecting all 3 projects. For Phase 1, sync-over-async (.GetAwaiter().GetResult()) on the git credential call is acceptable — it runs once at startup and completes in <100ms.
- **Token resolution precedence: env var > stored credential.** Env var must win for backward compat and CI/CD override semantics. Never prompt during DI container setup.
- **HelixService.cs has 7 identical error message strings** for 401 handling. These should be extracted to a constant when updating the message text.

📌 Team update (2026-03-07): Test result file discovery consolidated — ParseTrxResultsAsync uses TestResultFilePatterns array, supports TRX + xUnit XML, auto-detection via DetectTestFileFormat. — decided by Ripley
📌 Team update (2026-03-07): CacheStoreFactory uses Lazy<T> wrapping — standard .NET pattern for ConcurrentDictionary.GetOrAdd with side effects. — decided by Ripley
📌 Team update (2026-03-07): AzDO test patterns documented — Lambert identified edge cases in AzdoIdResolver (negative buildIds, TryResolve defaults, thread safety). — documented by Lambert

📌 Team update (2026-03-07): AzDO caching strategy — dynamic TTL (completed 4h, in-progress 15s, timelines never while running). No DTO layer needed for AzDO models. — decided by Ripley

### 2026-03-07: Decision — AzdoMcpTools returns model types directly
If we later need to reshape AzDO output differently from the API models, we'd add wrapper types then. For now, direct return is simpler and correct.

### 2026-03-08: AzDO Security Review — Learnings
- **Query parameter injection is easy to miss.** `prNumber` was not escaped or validated as an integer while `branch` and `statusFilter` were properly escaped with `Uri.EscapeDataString`. Enforce convention: all user-provided string values interpolated into URLs must be either type-validated (e.g., `int.TryParse` for numeric IDs) or `Uri.EscapeDataString`-escaped. No exceptions.
- **`BuildUrl` hardcoding `https://dev.azure.com/` is the right SSRF mitigation.** Combined with `Uri.EscapeDataString` on org/project, this makes SSRF structurally impossible regardless of input. This pattern should be preserved — never allow user input to influence the URL base/authority.
- **Singleton `AzCliAzdoTokenAccessor` with `_resolved` flag doesn't handle token expiry.** For long-running MCP servers, az CLI tokens (~1h lifetime) will expire. Not a security bug (fails closed with 401), but an operational gap. Future work: track JWT expiry or use `AZDO_TOKEN` with external rotation.
- **`CacheSecurity.SanitizeCacheKeySegment` is the established pattern for cache key hygiene.** AzDO caching correctly reuses it. Any new cacheable subsystem must use it too.
- **Security review convention established:** For new API integrations, review all 7 focus areas (command injection, SSRF, token leakage, input validation, cache isolation, HTTP security, pattern consistency). Use SEC-{N} IDs with severity levels.

📌 Team update (2026-03-08): AzDO context-limiting defaults — safe output-size defaults added to all AzDO MCP tools, matching Helix patterns. Defaults in method signatures, cache keys include limit params. — decided by Ripley

📌 Team update (2026-03-08): AzDO artifact/attachment test patterns — artifact caching ImmutableTtl (4h), attachment caching TestTtl (1h). Service-layer limiting for attachments. 700 total tests. — documented by Lambert

📌 Team update (2026-03-08): AzDO docs use subsections within existing README structure — new API domains should follow this pattern (### under ## MCP Tools, ## Authentication, ## Caching). llmstxt updated with AzDO tools subsection. — decided by Kane

📌 Team update (2026-03-08): `IsFileSearchDisabled` promoted from internal to public on `HelixService` — needed for MCP tools extraction to separate assembly. Consistent with existing public statics `MatchesPattern` and `IsTestResultFile`. — decided by Ripley
