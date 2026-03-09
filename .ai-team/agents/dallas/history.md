# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)

## Core Context (summarized through 2026-03-09)

> Older history archived to history-archive.md on 2026-03-09.

**Architecture reviews produced:** Initial code review, P0 foundation design (IHelixApiClient, DI, HelixException, CancellationToken), stdio MCP transport (`hlx mcp` subcommand), US-4 auth (HELIX_ACCESS_TOKEN env var), cache design (SQLite, decorator pattern, WAL), HTTP/SSE multi-client auth (IHttpContextAccessor + scoped DI).

**Key design patterns established:**
- Decorator pattern for caching (CachingHelixApiClient wrapping IHelixApiClient)
- Console logs for running jobs must never be cached
- Cache TTL matrix: 15s/30s running, 1h/4h completed
- Cache isolation by auth token hash (SHA256) → separate SQLite DBs
- `IHelixTokenAccessor` abstraction: env var for stdio, HttpContext for HTTP
- MCP tools are thin wrappers over HelixService — business logic stays in HelixService
- MCP tool naming: `hlx_{verb}`/`hlx_{noun}` with `_` separators; descriptions expose behavioral contracts, not implementation
- File scanning: one generic tool + one convenience alias (not per-type sprawl)

**MCP API review (2026-02-13):** No `hlx_list_work_items` (N+1 via hlx_status). URL resolution boilerplate across 5/9 tools — tolerable. `hlx_status` should mention `failureCategory`.

**Threat model (2025-07-23):** STRIDE approved. All 10 tools, both transports, cache, filesystem covered. Minor: `%2F` in work item names (correctness, not security).

**Remote search (2026-02-13):** download-search-delete. No regex (ReDoS). TRX needs XXE protection. US-31/32 created.

**Value-add (2025-07-23):** 12 enhancements — 5 major, 3 significant, 3 moderate, 1 minor.

**UseStructuredContent (2025-07-24):** APPROVED. Task<string> → typed returns. Wire-compatible. hlx_logs excluded. Skill at `.ai-team/skills/mcp-structured-content/SKILL.md`.

**AzDO MCP tools design (2026-03-07):** MCP tools return AzDO model types directly — no DTO wrapper layer. Wrapper types deferred until reshaping is needed.

**AzDO security review (2026-03-08):** 5 findings — (1) type-validate or `Uri.EscapeDataString` all user inputs in URLs, (2) `BuildUrl` hardcodes `https://dev.azure.com/` (SSRF-proof), (3) singleton token accessor doesn't handle expiry (fails closed — operational gap), (4) `CacheSecurity.SanitizeCacheKeySegment` required for all subsystems, (5) security review convention: 7 focus areas, SEC-{N} IDs. Full details in history-archive.md.

📌 Team updates (2026-02-11 – 2026-03-07 summary): P0 foundation, requirements backlog (30 US), 9 US implemented, PackageId rename, NuGet publishing, HTTP/SSE auth tests, security fixes/tests, remote search design, status filter, UseStructuredContent refactor, Phase 1 auth UX, AzDO pipeline architecture. Decided by: Lambert, Kane, Dallas, Ash, Ripley, Larry.

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
- **AzDO build logs are append-only.** Once a line is written to a build log, it never changes. This is a structural property of the AzDO logging pipeline, not just an observed behavior. Cached log content is always a valid prefix of the current log — only new lines get added at the end.
- **`ICacheStore` deletes expired entries — no stale reads.** `GetMetadataAsync` returns `null` for expired keys, not stale data. Any "keep-but-refresh" pattern must use long TTLs + separate freshness markers, not short TTLs on the content itself.
- **Freshness marker pattern for delta caching.** Two cache keys: content (long TTL) + freshness sentinel (short TTL). When sentinel expires, delta-fetch new data, append to content, reset sentinel. Avoids extending `ICacheStore` for stale-while-revalidate semantics. Applicable to any append-only data source.
- **`CountLines` must account for trailing newlines in AzDO log content.** `string.Split('\n').Length` overcounts by 1 when content ends with `\n` (common for AzDO logs). The correct count is the number of `\n` characters (for newline-terminated content) or `Split` count minus 1 when trailing `\n` exists. This matters for delta-fetch `startLine` computation — an off-by-one causes a missed boundary line on every delta cycle. P0 fix required.
- **Existing tests survive interface changes via `Arg.Any<T>()` for new optional params.** When adding optional parameters to an interface method (like `int? startLine = null`), all existing mock setups need `Arg.Any<int?>()` for the new params. NSubstitute won't match if the arg matchers don't cover the full signature.

📌 Team update (2026-03-07): Test result file discovery consolidated — ParseTrxResultsAsync uses TestResultFilePatterns array, supports TRX + xUnit XML, auto-detection via DetectTestFileFormat. — decided by Ripley
📌 Team update (2026-03-07): CacheStoreFactory uses Lazy<T> wrapping — standard .NET pattern for ConcurrentDictionary.GetOrAdd with side effects. — decided by Ripley
📌 Team update (2026-03-07): AzDO test patterns documented — Lambert identified edge cases in AzdoIdResolver (negative buildIds, TryResolve defaults, thread safety). — documented by Lambert
📌 Team update (2026-03-07): AzDO caching strategy — dynamic TTL (completed 4h, in-progress 15s, timelines never while running). No DTO layer needed for AzDO models. — decided by Ripley
📌 Team update (2026-03-08): AzDO context-limiting defaults — safe output-size defaults added to all AzDO MCP tools, matching Helix patterns. — decided by Ripley
📌 Team update (2026-03-08): AzDO artifact/attachment test patterns — artifact caching ImmutableTtl (4h), attachment caching TestTtl (1h). 700 total tests. — documented by Lambert
📌 Team update (2026-03-08): AzDO docs use subsections within existing README structure — decided by Kane
📌 Team update (2026-03-08): `IsFileSearchDisabled` promoted to public on `HelixService` — needed for MCP tools extraction. — decided by Ripley
📌 Team update (2026-03-08): AzDO search gap analysis — P0 `azdo_search_log` (PR #10). P1 candidates: `azdo_search_timeline`, multi-step log search. — analyzed by Ash

### 2026-03-09: azdo_search_log_across_steps design spec

**Spec:** `.ai-team/decisions/inbox/dallas-azdo-search-across-steps.md`

Complements `azdo_search_log`. Two-phase: metadata → incremental search with early termination. Ranking by failure likelihood (4 buckets). New `GetBuildLogsListAsync` on `IAzdoApiClient`. `NormalizeAndSplit` extracted. Result types in Core. Safety: maxLogs=30, minLines=5, maxMatches=50. ~19 tests.

Key files: IAzdoApiClient.cs, AzdoApiClient.cs, AzdoModels.cs, AzdoService.cs, AzdoMcpTools.cs, Program.cs.

### 2026-03-09: Append-on-expire caching (D-6)

**Spec:** `.ai-team/decisions/inbox/dallas-incremental-log-fetching.md`

Freshness marker pattern: content key (4h) + sentinel (15s). Delta-append via CountLines. Uses `IsBuildCompletedAsync` (Option B). Range requests on stale caches delta-refresh first. 12 new tests (C-10–C-21).

### 2026-03-09: Code review — Incremental log fetching (Phase 1 + Phase 2)

**APPROVED** with P0 follow-up. All spec items D-1–D-6 verified correct. 32 tests passing (A-1..A-5, C-1..C-21, S-1..S-6).

**P0 — CountLines off-by-one:** `Split('\n').Length` overcounts by 1 with trailing `\n`. Fix: subtract 1 when content ends with `\n`. Ripley: fix. Lambert: update C-18, C-19, delta tests.

📌 Team update (2026-03-09): Incremental log fetching Phase 1+2 approved — API range support, dual-key append-on-expire caching, tail optimization. 863/863 tests, 32 new. P0: CountLines off-by-one. — reviewed by Dallas
📌 Team update (2026-03-09): Timeline search result types live in Core — TimelineSearchMatch/TimelineSearchResult in AzdoModels.cs. MCP tools return Core types directly. [JsonIgnore] on Record for flat JSON. — decided by Ripley

📌 Team update (2025-07-18): Perf review identified 17 allocation issues — decided by Ripley

📌 Team update (2026-03-09): Cache format changed to raw: prefix with sentinel detection, SearchConsoleLogAsync decoupled from disk, shared StringHelpers in Core — decided by Ripley
