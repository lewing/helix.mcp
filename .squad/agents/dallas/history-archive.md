# Dallas — History Archive
> Archived on 2026-03-09. See history.md for current context.

## 2026-03-07: Decision — AzdoMcpTools returns model types directly
If we later need to reshape AzDO output differently from the API models, we'd add wrapper types then. For now, direct return is simpler and correct.

## 2026-03-08: AzDO Security Review — Learnings
- **Query parameter injection is easy to miss.** `prNumber` was not escaped or validated as an integer while `branch` and `statusFilter` were properly escaped with `Uri.EscapeDataString`. Enforce convention: all user-provided string values interpolated into URLs must be either type-validated (e.g., `int.TryParse` for numeric IDs) or `Uri.EscapeDataString`-escaped. No exceptions.
- **`BuildUrl` hardcoding `https://dev.azure.com/` is the right SSRF mitigation.** Combined with `Uri.EscapeDataString` on org/project, this makes SSRF structurally impossible regardless of input. This pattern should be preserved — never allow user input to influence the URL base/authority.
- **Singleton `AzCliAzdoTokenAccessor` with `_resolved` flag doesn't handle token expiry.** For long-running MCP servers, az CLI tokens (~1h lifetime) will expire. Not a security bug (fails closed with 401), but an operational gap. Future work: track JWT expiry or use `AZDO_TOKEN` with external rotation.
- **`CacheSecurity.SanitizeCacheKeySegment` is the established pattern for cache key hygiene.** AzDO caching correctly reuses it. Any new cacheable subsystem must use it too.
- **Security review convention established:** For new API integrations, review all 7 focus areas (command injection, SSRF, token leakage, input validation, cache isolation, HTTP security, pattern consistency). Use SEC-{N} IDs with severity levels.

## 2026-03-13: Archived from history.md during summarization

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

📌 Team updates (2026-03-09): Incremental log (PR #13), perf review, cache raw: prefix. — Ripley

### 2025-07-24: Test Quality Review — Tautological Test Audit

Reviewed 776 tests. ~40 problematic (5%), concentrated not systemic. Deleted ~17 tests (~350 lines), zero coverage loss. Key rules: no layer duplication, ≤1 passthrough smoke test, interface compliance tests are redundant. Gold standard patterns: AzdoSecurityTests, AzdoIdResolverTests, TextSearchHelperTests, CachingAzdoApiClientTests, AzdoServiceTailTests.

📌 Team update (2026-03-10): CiKnowledgeService expanded to 9 repos with full profiles. 5 tool descriptions updated. — Ripley

- **HelixTool.Core has asymmetric organization.** AzDO code lives in a clean `AzDO/` subfolder with `HelixTool.Core.AzDO` namespace. Helix-specific code (8 files, ~1,700 lines) is scattered at the project root alongside shared utilities (5 files, ~800 lines). CachingHelixApiClient is in `Cache/` but is Helix-specific.
- **Cache/ folder uses `HelixTool.Core` namespace, not `HelixTool.Core.Cache`.** All 7 cache files lack a sub-namespace, unlike AzDO which correctly uses `HelixTool.Core.AzDO`. This makes cache types indistinguishable from Helix types by namespace alone.
- **AzdoService depends on HelixService for shared utility methods.** `AzdoService.cs` calls `HelixService.MatchesPattern()` and `HelixService.IsFileSearchDisabled` — these are genuinely shared utilities stranded on a domain-specific class. Must extract before any structural reorganization.
- **HelixTool.Mcp.Tools has flat structure mixing domains.** HelixMcpTools.cs (483 lines) and AzdoMcpTools.cs (307 lines) sit side-by-side with no folder separation.
- **Program.cs (CLI) is 1,513 lines** — largest file in the repo, contains all Helix + AzDO commands in one file.
- **Option A (folder-level reorg) recommended over project splitting at current scale (~22K lines, ~80 files, ~770 tests, 1 team).** Create `Helix/` subfolders mirroring existing `AzDO/` subfolders. Decision spec: `.ai-team/decisions/inbox/dallas-helix-azdo-restructure.md`

## 2026-03-13: Archived durable learnings from history.md

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
# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
- **Stack:** C# .NET 10, ConsoleAppFramework, Spectre.Console, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Three projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server)
- **Key files:** HelixService.cs (core ops), HelixIdResolver.cs (GUID/URL parsing), HelixMcpTools.cs (MCP tool definitions), Program.cs (CLI commands)

## Core Context

- **Architecture boundaries:** `src/HelixTool.Core/Helix/`, `src/HelixTool.Core/AzDO/`, `src/HelixTool.Core/Cache/`, and `src/HelixTool.Mcp.Tools/{Helix,AzDO}/` are the stable domain folders; composition roots remain `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs`.
- **Design defaults:** keep business logic in services, keep MCP tools thin, use decorators for caching, and prefer behavioral contracts in tool descriptions over implementation details.
- **Caching/auth:** running console logs are never cached, cache isolation is by auth-token hash, stdio remains the primary transport, and HTTP/SSE auth is a scoped per-request design rather than a CLI concern.
- **Protocol/runtime rules:** Helix auth remains opaque `Authorization: token` only, AzDO calls must continue accepting org/project per request, and in-progress AzDO timelines/logs should be treated as live append-only data rather than long-lived cache snapshots.
- **Cache/testing patterns:** append-only log refresh uses long-lived content plus short-lived freshness sentinels, `ICacheStore` never serves expired entries, and interface signature changes require updated `Arg.Any<T>()` coverage for every optional parameter in existing NSubstitute setups.
- **AzDO direction:** model types can be returned directly from MCP tools, `helix_ci_guide` owns repo-specific routing, and the current AzDO auth chain is `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous.

## Learnings

**Archive refresh (2026-03-13):** Detailed `azdo_search_log_across_steps`, append-on-expire caching, tautological-test-audit, and Helix/AzDO restructuring-analysis notes moved to `history-archive.md`. Keep the durable rules: ranked incremental log search, freshness-sentinel append caching, pruning layer-duplicate/passthrough tests, and preferring the low-risk folder-level split over premature project sprawl.


📌 Team updates (2026-03-07 – 2026-03-08 summary): Test result file discovery consolidated (Ripley). CacheStoreFactory Lazy<T> pattern (Ripley). AzDO edge cases documented (Lambert). Dynamic TTL caching strategy (Ripley). Context-limiting defaults for AzDO MCP tools (Ripley). AzDO artifact/attachment test patterns — 700 total tests (Lambert). AzDO docs subsections (Kane). IsFileSearchDisabled promoted to public (Ripley). AzDO search gap analysis — P0 azdo_search_log (Ash).

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

📌 Team updates (2026-03-09): Incremental log (PR #13), perf review, cache raw: prefix. — Ripley

📌 Team update (2026-03-10): CiKnowledgeService expanded to 9 repos with full profiles. 5 tool descriptions updated. — Ripley

- **HelixTool.Core has asymmetric organization.** AzDO code lives in a clean `AzDO/` subfolder with `HelixTool.Core.AzDO` namespace. Helix-specific code (8 files, ~1,700 lines) is scattered at the project root alongside shared utilities (5 files, ~800 lines). CachingHelixApiClient is in `Cache/` but is Helix-specific.
- **Cache/ folder uses `HelixTool.Core` namespace, not `HelixTool.Core.Cache`.** All 7 cache files lack a sub-namespace, unlike AzDO which correctly uses `HelixTool.Core.AzDO`. This makes cache types indistinguishable from Helix types by namespace alone.
- **AzdoService depends on HelixService for shared utility methods.** `AzdoService.cs` calls `HelixService.MatchesPattern()` and `HelixService.IsFileSearchDisabled` — these are genuinely shared utilities stranded on a domain-specific class. Must extract before any structural reorganization.
- **HelixTool.Mcp.Tools has flat structure mixing domains.** HelixMcpTools.cs (483 lines) and AzdoMcpTools.cs (307 lines) sit side-by-side with no folder separation.
- **Program.cs (CLI) is 1,513 lines** — largest file in the repo, contains all Helix + AzDO commands in one file.
- **Option A (folder-level reorg) recommended over project splitting at current scale (~22K lines, ~80 files, ~770 tests, 1 team).** Create `Helix/` subfolders mirroring existing `AzDO/` subfolders. Decision spec: `.ai-team/decisions/inbox/dallas-helix-azdo-restructure.md`

📌 Team update (2026-03-10): Option A folder restructuring executed — 9 Helix files moved to Core/Helix/, Cache namespace added, shared utils extracted from HelixService, Helix/AzDO subfolders in Mcp.Tools and Tests. 59 files, 1038 tests pass, zero behavioral changes. PR #17. — decided by Dallas (analysis), Ripley (execution)

📌 Team update (2026-03-10): Review-fix decisions merged — README now leads with value prop, shared caching, and context reduction; cache path containment uses exact Ordinal root-boundary checks; and HelixService requires an injected HttpClient with no implicit fallback. Validation confirmed current CLI/MCP DI sites already comply and focused plus full-suite coverage exists. — decided by Kane, Lambert, Ripley

📌 Team update (2026-03-10): Knowledgebase refresh guidance merged — treat the knowledgebase as a living document aligned to current file state, not a static snapshot; earlier README/cache-security/HelixService review findings are resolved knowledge, and only residual follow-up should stay active (discoverability plus documentation/tool-description synchronization). — requested by Larry Ewing, refreshed by Ash

📌 Team update (2026-03-10): Discoverability routing decisions merged — keep the current tool surface, route repo-specific workflow selection through `helix_ci_guide(repo)`, treat `helix_test_results` as structured Helix-hosted parsing rather than a universal first step, and keep `helix_search_log`/docs/help guidance synchronized across surfaces. — decided by Dallas, Kane, Ripley

📌 Team update (2026-03-13): Scribe merged decision inbox items covering `dotnet` as the VMR profile key, `helix_search`/`helix_parse_uploaded_trx` naming, tighter MCP descriptions, and explicit truncation metadata (`truncated`, `LimitedResults<T>`). README/docs now also call out `ci://profiles` resources and idempotent annotations.
- **AzDO auth code and auth decisions are currently out of sync.** `decisions.md` and Dallas history record Azure Identity / `AzureDeveloperCliCredential` as the intended direction for AzDO, but the live implementation in `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` still explicitly avoids `Azure.Identity` and uses `AZDO_TOKEN` → `az account get-access-token` → anonymous because of WSL libsecret/D-Bus failures. Any future auth work must treat this as an intentional divergence to resolve, not a missing cleanup.
- **`HelixTool.Core` already carries Azure SDK plumbing transitively.** `Microsoft.DotNet.Helix.Client` already brings in `Azure.Core`, so adding `Azure.Identity` would not introduce the first Azure package; the incremental cost is the identity stack (Azure.Identity + MSAL packages and a few abstractions), not the Azure SDK foundation.
- **If AzDO adopts Azure.Identity, use an explicit narrow chain, not `DefaultAzureCredential`.** The recommended order is `AZDO_TOKEN` env var → targeted Azure.Identity credential(s) for cached developer auth → existing `az` CLI subprocess fallback → anonymous. This avoids `DefaultAzureCredential`'s broad probe surface and preserves the known-good WSL fallback path.
- **AzDO auth touchpoints are concentrated in four files.** `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` contains both the interface and current az CLI implementation, `src/HelixTool.Core/AzDO/AzdoApiClient.cs` conditionally adds the Bearer header, and both `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` register the accessor as a singleton in the composition roots.

📌 Team update (2026-03-13): AzDO auth is now the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous, with scheme-aware `AzdoCredential` metadata and `DisplayToken` kept separate from the wire token. — decided by Dallas, Ripley

📌 Team update (2026-03-13): MCP-facing Helix names/descriptions should stay scope-accurate and low-context: use `helix_parse_uploaded_trx`, `helix_search`, and keep repo-specific routing in `helix_ci_guide`. — decided by Ripley

- **AzDO auth is server-scoped in HTTP MCP mode.** `IAzdoTokenAccessor` is registered as a singleton in `HelixTool.Mcp/Program.cs`, so remote MCP clients act through the server's shared AzDO identity even when Helix auth/cache isolation is per-request. Future security reviews should treat shared HTTP deployments as a privilege-concentration boundary.
- **Caching raw Azure tokens blocks refresh and extends memory residency.** The current accessor caches `AzdoCredential` strings, not credential-provider state, so `AzureCliCredential`/`az` bearer tokens persist for process lifetime and cannot refresh after expiry without explicit invalidation. Long-running MCP servers need expiry-aware refresh or strict guidance to use externally rotated `AZDO_TOKEN`.
- **AzDO cache isolation is weaker than the README currently implies.** `CachingAzdoApiClient` keys entries by org/project/suffix, while CLI/stdio composition roots initialize `CacheOptions.AuthTokenHash = null`; authenticated AzDO responses can therefore persist in the shared `public/` cache even though the auth chain itself never writes tokens to disk. Any future cache/auth work should key AzDO cache state by AzDO auth context or narrow the documentation claim.
- **`DisplayToken` remains the main token-leak footgun.** `ToString()` is redacted and current error paths are careful, but the implicit `AzdoCredential -> string` conversion still yields the raw display token. Preserve the current guarded call-site pattern and prefer removing or heavily warning this conversion if compatibility allows.

📌 Team update (2026-03-13): PR #28 merged the remaining AzDO auth quick wins — fallback Azure CLI/`az` credentials now refresh on deadline/401, cache isolation keys off stable auth-source identity instead of raw token bytes, and `hlx azdo auth-status` exposes safe auth-path metadata. — decided by Ripley

📌 Team update (2026-03-13): Cache roots now stay stable via `CacheRootHash` while mutable `AuthTokenHash` partitions AzDO entries, and AzDO auth hashes are seeded before cached AzDO reads. — decided by Ripley

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane

📌 Team update (2026-03-16): MCP timeline truncation + CI guide improvements complete — `azdo_timeline` now implements partial response pattern (200-record threshold, 100 returned + truncation metadata); 5 core repo profiles in `CiKnowledgeService` updated to recommend `azdo_search_timeline` as first investigation step for large builds; cross-references added in tool descriptions. Wire format change from `AzdoTimeline?` to `TimelineResponse?` — review for CLI-side compatibility and threshold configurability. Build clean, 1127 tests pass. — implemented by Ripley

## 2026-XX-XX: MCP SDK usage inventory (for Larry, parallel with Ash's SDK research)
- Both `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` are pinned to **1.0.0** (no Directory.Packages.props; versions live in csproj).
- Two transports in use: `WithStdioServerTransport()` in `HelixTool/Program.cs` (CLI `mcp` subcommand) and `WithHttpTransport()` + `MapMcp()` in `HelixTool.Mcp/Program.cs` (ASP.NET Core HTTP server).
- Tool registration is fully attribute-based (`[McpServerToolType]` + `[McpServerTool]`) discovered via `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`. ~28 tools across 3 classes (Helix=11, AzDO=15, CiKnowledgeTool=1, +1 resource type with 2 resources).
- Resources: only registered in HTTP server (`WithResourcesFromAssembly`), not in stdio CLI server — divergence worth flagging if SDK upgrade changes resource semantics.
- No prompts. No source generators against MCP SDK (DescribeGenerator inspects `[McpServerTool]` syntax for CLI/MCP cross-reference doc but doesn't depend on SDK runtime types).
- SDK extension surface is shallow: `McpException` for tool-surface errors, `UseStructuredContent = true` on most tools for auto-generated output schemas, `[Description]` for params, `IHttpContextAccessor`-based per-request token plumbing (HTTP server), custom `ApiKeyMiddleware` runs before `MapMcp()`.
- Version-pinning rationale (decisions.md): chose 1.0.0 specifically for `UseStructuredContent` schema generation and `McpException` surfacing semantics. No "do not upgrade" guidance recorded.
- `.mcp/server.json` (packaged with the dotnet tool) ships separately and is version-validated against csproj/tag at release.

📌 Team update (2026-05-08): MCP SDK usage inventory for v1.0.0 → v1.3.0 upgrade assessment — ModelContextProtocol 1.0.0 + ModelContextProtocol.AspNetCore 1.0.0 across 3 csprojs; attribute-based tool registration (~27 tools); identified drift: hardcoded ServerInfo.Version, stdio host missing WithResourcesFromAssembly, no Directory.Packages.props; parallel Ash research recommends v1.3.0 upgrade (low risk, no code changes required).

📌 Team update (2026-05-08): MCP SDK 1.3.0 upgrade — Ripley shipped Central Package Management migration across 6 csprojs (new Directory.Packages.props), MCP SDK 1.0.0 → 1.3.0 (zero source changes), stdio host resource visibility fix (WithResourcesFromAssembly), ServerInfo.Version de-hardcoding (AssemblyInformationalVersionAttribute pattern). Build validates (0 errors, 6 NU1507 warnings pre-existing). Branch `squad/mcp-sdk-1.3.0-upgrade` ready for Lambert testing.


📌 Team update (2026-05-08): Parallel squad work — worktree recommendation

**Context:** Two Ripley branches (`squad/mcp-tool-annotations-and-cleanup` and `squad/mcp-progress-notifications`) ran in parallel in the same working tree, causing a checkout race mid-task.

**Recommendation:** Future parallel squad work should use separate `git worktree`s. Each agent gets isolated working state, eliminating checkout races entirely. Worktrees are lightweight (shared git dir, isolated working dirs). Recovery: `git worktree add /path/to/worktree-N branch-name` before spawning agents; cleanup with `git worktree remove` after.

**Owner:** Dallas (CI/coordination) — recommend baking into squad orchestration checklist for future parallel sprints.

📌 Team update (2026-05-21): Pagination Phase 1+2 implemented and tested — wrapped `azdo_changes` and `azdo_test_runs` in `LimitedResults<T>`; added `truncated`/`note` fields to 8 bespoke result types; 13 contract tests (333 LOC) verify truncation behavior. Full suite: 1180/1180 passing. Closes Dallas's pagination audit spec. — implemented by Ripley, tested by Lambert

**2026-05-21 10:33Z:** Ash audit complete. 3 decisions pending for your review: service-layer validation, azdo_auth_status sync vs async, structured error codes. See decisions.md.

**2026-05-21 17:58Z:** Completed design proposal for surfacing WorkItemSummary.ExitCode + ConsoleOutputUri. Filed to `.squad/decisions/inbox/dallas-surface-workitem-fields.md`. Key call: optimize GetJobStatusAsync (skip detail fetch for passed items), defer ConsoleOutputUri streaming.

## Learnings — Slop audit triage 2026-05-22

- **Structural duplication > boilerplate repetition for triage priority.** Ash's audit found 16 catch-throw handlers (high count) and 6 DTO classes (schema duplication, low count). Verdict: FIX the DTOs (functional duplication, maintainability hazard); DEFER the handlers (control-flow refactoring creates risk of silent behavior change). Boilerplate extracted without formal exception-path coverage can break silently.
- **Exception handler extraction requires test coverage before refactoring.** The catch blocks are a control boundary; refactoring them into a shared helper without exercising all 16 paths (pass-through behavior, exception propagation, stack trace preservation) risks silently changing exception semantics. Safer to defer extraction until we have explicit exception test coverage.
- **Style drift (attribute placement, naming) is not worth a refactor PR.** [JsonPropertyName] placement inconsistency (inline vs above-line) and usage (some with attributes, some relying on defaults) is low-priority. Bundle standardization into the next structural refactor rather than spinning a dedicated PR. Functional correctness (schema match) > consistency (attribute placement style).
- **Intentional API versioning (service models vs MCP results) is not slop.** The separation between AzdoModels.cs (API-level) and McpToolResults.cs (tool-level) is a deliberate stability boundary. Schema versions evolve independently at that boundary; this is good architecture, not tech debt. Flag false positives like this to reject without guilt.
- **Ripley sequencing after a metadata pass: avoid churn.** PR #57 just landed (description tightening). Adding structural refactors immediately after metadata PRs creates thrashing. Sequence the DTO consolidation as a separate, lower-priority PR. Do not bundle multiple refactors into a single spike unless they directly conflict.

---

- **Adapter pattern depth:** The SDK-to-interface adapter layer is shallow (3 classes: HelixApiClient adapters, CachingHelixApiClient DTOs). Adding fields is mechanical: interface → adapter → cache DTO. The cache DTO must match the interface for JSON round-trip fidelity.
- **IWorkItemSummary is intentionally thin:** Only `Name` was exposed. ExitCode lived exclusively on `IWorkItemDetails`. This forced an N+1 fetch pattern in `GetJobStatusAsync` that the new SDK fields can eliminate.
- **Nullability as version signal:** When extending interfaces with SDK-backed fields from beta packages, always make new properties nullable. Null means "server didn't provide it" — safe fallback to existing code paths.
- **Brady values:** Bullet-heavy proposals, concrete before/after call counts, explicit file lists for handoff, and clear rollout recommendations (patch vs minor). Keep it tight.
- **AzDO timeline has two orthogonal axes:** `state` (pending/inProgress/completed) is lifecycle; `result` (succeeded/failed/canceled/skipped/abandoned/succeededWithIssues) is outcome, only meaningful when state=completed. `issues` is a third orthogonal dimension. Never conflate these in a single filter without clearly documenting the mapping.
- **MCP filter design: prefer named presets over orthogonal params for LLM consumers.** Presets encode valid combinations and avoid invalid cross-product states. Shape A (richer enum) beats Shape B (two params) when the axes have dependent validity.
- **Two naming conventions coexist in MCP tools:** Pass-through params (values sent to AzDO API) use AzDO-verbatim casing (`inProgress`, `Stage`). Preset params (values interpreted by our logic) use friendly lowercase (`failed`, `running`). Don't mix conventions within a param type. Use silent aliases (NormalizeFilter) to bridge when an LLM carries a pass-through name into a preset param.
- **Key files for AzDO filter logic:** MCP layer: `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` (3 tools). Service layer: `src/HelixTool.Core/AzDO/AzdoService.cs` (SearchTimelineAsync, GetHelixJobsAsync). Model: `src/HelixTool.Core/AzDO/AzdoModels.cs` (AzdoTimelineRecord has State, Result, Issues).
- **MCP description audit cadence (2026-05-22):** Approved PR #57 (3c4728c) — second description-tightening pass, ~3 months after the 2026-02-13 original. 8 of 25 tools had drifted back above the rubric threshold. Pattern: audit quarterly, or after any batch of new tools lands. Key rubric rules: lead with verb, ≤25 words, push filter enums and defaults to param-level `[Description]`, keep cross-references (e.g. `azdo_log`, `azdo_search_timeline`) as the one exception. Domain knowledge (repo lists, org names like `devdiv`) belongs in `CiKnowledgeService` response content, not always-loaded tool metadata.
- **Test strategy for description metadata:** Pin routing-intent phrases (e.g. "Repo-specific CI guidance") in description-string tests, but assert domain-specific discoverability details (e.g. `devdiv`) against `CiKnowledgeService` response content where they actually live. Avoids fragile coupling between test assertions and metadata that should stay compact.


# Summary (archived 1 older sections)

See history-archive.md for complete history.
- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## 2026-05-22 — Slop Audit Triage Complete, PR #58 Merged

**Status:** Triaged Ash findings → Ripley implementation → PR #58 merged to main

Recommended DTO consolidation PR (Finding #1 verdict: FIX) successfully implemented and merged. Deferred exception handler extraction (Finding #2) to Q3 2026 pending exception test coverage improvement. Rejected findings #3–6 per architectural and risk analysis.

Slop audit pattern established. Ready for future audits.

## Learnings — Issue #59 Triage (2026-05-22)

- **Measurement-first audits beat word-counting for metadata work.** PR #57's headline claimed 29→14 and 45→20 word reductions, suggesting large agent-visible savings. Actual `tools/list` token-count audit (gpt-4o tokenizer) showed −164 tokens (−2.0%) across the release range. The real wins came from PR #51's `LimitedResults<T>` wrapper (zero description changes, −257 tokens) and PR #57's text trims (−110), but PR #56's filter-preset schema additions reclaimed +113. Description word-count metrics are insufficient; always measure the full JSON footprint.

- **Tokenizer-measurement methodology is reusable and should be standing practice.** The measurement snippet (bash + Python gpt-4o tokenizer) is repeatable and gives conclusive evidence. Overhead is ~30 minutes (snapshot before, land PR, snapshot after, diff). Adopted as checklist item for future description PRs: measure, record delta in PR body, store methodology in `.squad/skills/mcp-description-rubric.md` Appendix.

- **Schema growth from one PR can cancel text trims from another.** PR #56's filter presets added inputSchema properties; PR #57's text trims were contemporaneous. The two effects netted to a small total savings. Lesson: audit in measurement-first order, not PR order. Track all tools' token contributions across a release range, not just the tools targeted by any single PR.

- **Generalization patterns (like `LimitedResults<T>`) are high-leverage opportunities.** PR #51's wrapper migration was unplanned discovery: two tools got −164 and −93 tokens with zero description changes. Lewing identified three candidate tools for the same treatment. Worth a sub-question (Ripley audit) before committing: does the pattern generalize safely? Expected upside if it does: −150–250 additional tokens (3× the entire PR #57 word-trim win).

- **Defer diffuse follow-ups; do targeted ones immediately.** Issue #59 proposed three follow-ups: (1) Trim `azdo_search_timeline` params (+69 from PR #56) = targeted, measurable, ACCEPT. (2) "Top-5 tool 20% trim pass" = broad, unscoped, no evidence = DEFER pending Ash's investigation audit in 2–3 weeks. (3) `LimitedResults<T>` extension = targeted pattern, unknown safety = ACCEPT Phase 1 (Ripley audit), conditional on Phase 1 result.

- **Evidence-first decision sequencing avoids orphaned work.** Lewing provided per-tool token deltas with full release-range context. This made it possible to confidently accept or defer each follow-up without speculation. For Follow-up #2 (top-5 audit), the issue provided the math but no field audit. Decision: defer to investigation spike, then convert to a follow-up issue with per-tool targets. This prevents Ripley from guessing which tools to trim.

**Decision document:** `.squad/decisions/inbox/dallas-issue59-triage-2026-05-22.md`

---

### Retriage — New Structural Data (2026-05-22, 14:50Z)

blazor-playground/copilot-skills measurement agent provided field-level breakdown: **outputSchema 37% (3,032 tokens), inputSchema 33.5% (2,742 tokens), annotations 9.7% (791 tokens), description 7.2% (588 tokens)**. This inverts priority: PR #57 attacked the smallest contributor. Original triage was undersized; revised verdicts below.

**Key revelation:** PR #51's `LimitedResults<T>` win was outputSchema shrinkage by type simplification, not a one-off. Top-10 tools carry 58% of cost; many are list-returning. Pattern likely generalizes to −300–600 tokens (Phase 1 audit identifies candidates). Combined with a DTO-trim pass on top-10 tools (outputSchema + inputSchema reduction), ceiling jumps from original −80–300 to −940–2,080 tokens (11–25% of total cost).

**Revised verdicts:**
- **Follow-up 1 (param trim):** ACCEPT (elevated scope). Not just descriptions—inputSchema enum descriptions are the lever. Filter-preset enum on azdo_timeline alone = 88 tokens. Estimate: −40–80 tokens across affected tools.
- **Follow-up 2 (LimitedResults<T>):** ACCEPT (now headline lever). Expand to full top-10 outputSchema audit. Phase 1: Ripley identifies LimitedResults<T> + other simplification candidates. Phase 2: implement. Estimate: −300–600 tokens.
- **NEW Follow-up 4 (outputSchema DTO-trim on top-10):** **ACCEPT, highest priority.** Tighten DTO shapes, trim per-property descriptions, adopt $ref for shared schemas. Author ceiling: −500–1,500 tokens (50% of outputSchema cost, 10× PR #57 win). Phase 1: Ripley audits top-10, identifies targets. Phase 2: implement. Risk: DTO shape changes affect downstream; mitigate via measurement + wire-format compatibility.
- **NEW Follow-up 5 (annotations audit):** ACCEPT. Redundant annotations (e.g., openWorldHint=true matching SDK defaults) waste bytes. Estimate: −50–300 tokens. Quick 3–5 hour audit + cleanup.
- **NEW Follow-up 6 (drop primitive outputSchema):** ACCEPT. Tools returning primitives (e.g., helix_auth_status → boolean) don't need auto-generated schema. Estimate: −50–100 tokens. Zero risk. 2 hours.
- **Deferred (original broad top-5 audit):** Superseded. Refined follow-ups 4, 5, 6 provide field-level leverage points + sequenced Phase 1 audits.

**Execution plan:** Ripley runs Phase 1 audits on Follow-ups 2, 4, 5, 6 in parallel (weeks 1–2). Phase 2 implementation on highest-ROI targets next (Follow-up 4, then Follow-up 2). Follow-up 1 lands after Follow-up 2 Phase 1. Total scope: 20–32 hours over 3 weeks. Expected recovery: −940–2,080 tokens.

**Lesson learned:** Field-level structural breakdown is essential for prioritization. Metrics-first (word counts, boilerplate counts) miss the actual cost drivers. Next MCP audit should report token distribution by field. Dallas will update the standing rubric to mandate field-breakdown measurement.

**Retriage document:** `.squad/decisions/inbox/dallas-issue59-retriage-2026-05-22.md`

---


# Recent History (2026-05-25+)

## Learnings — Issue #61 Bug B Re-triage (2026-05-25)

### Deferral Calibration: When "Wait for Evidence" Becomes "Fix Now"

- **The May 22 deferral was defensible at the time, but not revisited.** Finding #2 (16 identical catch-throw blocks) had high count but no documented user impact. Correctly decided: "Don't refactor control boundaries without test evidence." That principle is sound. HOWEVER, the deferral should have come with a **revisit trigger**, not a hard "Q3 2026" date.

- **Low-probability-but-systemic findings need revisit triggers, not blanket deferrals.** The pattern:
  1. Finding: "16 repetitive catch blocks (boilerplate + control boundary)"
  2. Deferral: "Wait for exception test coverage before refactoring"
  3. ❌ WRONG: Set hard date (Q3 2026) and move on
  4. ✅ RIGHT: Set revisit trigger ("If exception goes uncaught in production → promote immediately")

- **The cost of the deferral was higher than estimated.** User-visible silent failures (`success=False, result=None` with no error message) appeared in production on 2026-05-25 — three days after deferral. The boilerplate pattern itself was masking exception handling gaps (AggregateException, TaskCanceledException not caught). Had we had a weekly revisit trigger, we'd have promoted Finding #2 to "fix now" on 2026-05-25 instead of discovering it reactively when Ash investigated a live incident.

- **Test coverage is not a blocker; it's a validation gate.** My May 22 call was "don't refactor without test coverage." The correct interpretation is: centralize now (fixes production bug), validate with tests afterward (Lambert writes exception-path tests in weeks 2–3). The user-visible bug provides sufficient justification to proceed. This converts the deferral gate from a precondition ("before you start") to a validation gate ("after you ship").

- **Control boundaries deserve special triage attention.** Exception handling, parameter validation, and task coordination are control boundaries where small oversights cause silent failures. Boilerplate at control boundaries can mask gaps that manifest only under load or concurrency. Heuristic: if a refactoring target is at a control boundary (exception handler, router, cache validator), flag it as "lower priority unless user impact emerges," not "safe to defer indefinitely."

- **Deferred Finding #2 should have been tracked in a revisit backlog.** The `.squad/decisions.md` summary said "deferred to Q3 2026," but there was no tracking of the precondition ("if exception goes uncaught") that would trigger an earlier revisit. Future: maintain a revisit-trigger index in `.squad/constraint-tracking.md` or similar, so that findings with emerging evidence (user reports, incident logs) are automatically elevated.

**Recommendation for future deferrals:** Every deferred finding gets a **revisit trigger** (event or time-based), not a hard date. Example:
- Deferral: "Found #2 (catch-throw boilerplate). Defer until we have exception test coverage or user reports silent failures."
- Revisit trigger: "Time: Q3 2026 OR Event: any user report of `success=False, result=None` with no error message"
- Status tracking: Add to `.squad/constraint-tracking.md` with owner (Dallas reviews Q3 2026 or upon trigger)

**Retriage document:** `.squad/decisions/inbox/dallas-issue61-bugb-retriage-2026-05-25.md`

## Learnings — Issue #61 merge gate 2026-05-25

- **`await Task.WhenAll` does NOT throw `AggregateException`.** Verified via C# repro: `await` unwraps to the first inner exception (e.g., `HttpRequestException`). Only `.Wait()` / `.Result` throws `AggregateException`. The `Task.Exception` property IS an `AggregateException` (for inspection), but `await` strips it. This means Ash's narrative ("AggregateException from Task.WhenAll is uncaught") was wrong — but the fix (centralized catch-all handler) was correct regardless.

- **Copilot reviewer caught what human review missed.** The `AggregateException` framing error propagated through Ash → Dallas retriage → Lambert tests → Ripley handler without anyone exercising the actual failure path. Copilot's review on #63/#64 correctly identified the semantic mismatch. Lesson: automated reviewers complement human review precisely for "obvious if you check, invisible if you don't" issues.

- **The real uncaught exception family was `TaskCanceledException` / `OperationCanceledException`.** The original catch clauses (`when (ex is InvalidOperationException or HttpRequestException or ArgumentException)`) already caught the exceptions `await Task.WhenAll` would unwrap. The production silent failure in session 9de92b14 was primarily Bug A (parameter name mismatch → MCP SDK binding failure before method entry).

- **Name the exception by exercising it, not by guessing from source-read.** Future root-cause analyses must write a 10-line repro exercising the failure path before naming the exception type. `Task.WhenAll` → `AggregateException` is the `.Wait()` mental model, not the `await` model. A repro would have caught this in 5 minutes.

- **Defensive dead code in handlers is acceptable when harmless.** PR #64's `AggregateException` unwrap in `McpExceptionHandler` is dead code under normal `await` usage, but it's harmless defensive code that guards against future `.Wait()` callers. Approved without change; follow-up #65 filed for flatten-vs-first-inner improvement.

- **Merge-conflict resolution is a Lead responsibility when sequencing matters.** PR #62 (param rename) and #64 (exception centralization) both touched `AzdoMcpTools.cs`. Merging #62 first (correct order) created conflicts in #64. Resolved by taking #64's centralized handler pattern and applying #62's `buildIdOrUrl` rename. Build + 1292 tests pass.

**Decision document:** `.squad/decisions/inbox/dallas-issue61-merge-gate-2026-05-25.md`
**Follow-up issue:** #65 (schema tests, flatten AggregateException, unskip Lambert tests, calibration process)

---

## 2026-05-25: Issue #61 — Two Decision Gates + Merge Gate Review

**Session:** Issue #61 Silent MCP failures (Bug A + Bug B)  
**Status:** Issue Complete; all 3 PRs merged ✅  
**Role:** Decision maker (Bug B re-triage) + Merge gate reviewer

### Bug B Re-triage Decision (May 25)

The May 22 deferral of Finding #2 (boilerplate, Q3 2026) is **no longer valid.** Ash's investigation revealed production-impact evidence: silent failures in live session caused by uncaught exception types (AggregateException, TaskCanceledException).

**VERDICT: PROMOTE Finding #2 to FIX NOW** — 3–4h Ripley work to centralize exception handling.

**Why the May 22 deferral was defensible then; why it's no longer valid now:**
- **Then:** No documented user-visible impact; extraction risk without test evidence
- **Now:** User-visible bug in production; test evidence provided by Ash

**Reasoning:**
- User-visible bug justifies proceeding (not hypothetical)
- Centralization is lower-risk than leaving scattered
- Test coverage is parallel, non-blocking (Lambert runs audit in parallel)
- Timeline predictable (3–4h effort)

### Merge Gate Review (Final Decision)

Reviewed all three PRs (Ripley ×2, Lambert ×1) and verified technical claims.

**Technical finding:** Verified via C# repro that `await Task.WhenAll(t1, t2)` unwraps to the **first inner exception**, NOT AggregateException. Only `.Wait()` throws AggregateException. This corrects Ash's narrative but validates her fix (centralized catch-all still correct).

**Per-PR Verdicts:**
- PR #62 (Ripley, parameter standardization): APPROVE & MERGE ✅
- PR #63 (Lambert, exception coverage): APPROVE WITH FOLLOW-UP ✅
- PR #64 (Ripley, exception centralization): APPROVE & MERGE ✅

**Merge order:** #62 → #64 → #63 (all executed successfully)

### Key Calibration Learning

**Name an exception by exercising it, not by guessing from source-read.**

Ash's investigation correctly identified the gap and the right fix, but incorrectly named "AggregateException from Task.WhenAll" as the uncaught exception. In reality, `await Task.WhenAll` unwraps to the inner exception; only `.Wait()` throws AggregateException. The actual uncaught types were TaskCanceledException and OperationCanceledException.

**Better practice:**
1. Write 10-line repro that forces failure
2. Observe: `catch (Exception ex) { Console.WriteLine(ex.GetType()); }`
3. Only then name the type in narrative

**This is especially critical for Task.WhenAll, Task.WhenAny, ConfigureAwait** — await machinery has non-obvious unwrapping behavior.

**Net impact:** Narrative error (cosmetic); zero production risk. Fix still correct (catch-all pattern catches everything). This lesson should be preserved for future exception investigations.

### Issue #61 Closed — 3 PRs Merged

- PR #62: Standardize `buildIdOrUrl` parameter (Bug A) ✅
- PR #64: Centralize MCP exception handling (Bug B) ✅
- PR #63: Exception coverage audit + tests (baseline) ✅

Both bugs fixed. Follow-up issue #65 filed for: schema test, flatten exceptions, unskip tests, rolling coverage tests, preserve calibration lesson.

### Deferral Calibration Reflection

**What you got right on May 22:**
- Recognized control-flow refactoring is risky without test evidence
- Correctly identified precondition ("better exception test coverage")
- Decision logic was sound

**What you missed:**
- Should have set a **revisit trigger** (e.g., "if any user report of silent exception behavior → promote immediately")
- Didn't weight "low-probability but high-impact" findings heavily enough
- The boilerplate pattern itself masks gaps that surface sooner than Q3

**Calibration for future:** Low-count but high-risk structural findings get a **REVISIT TRIGGER**, not blanket deferral. Revisit trigger: "If any exception goes uncaught in production → promote immediately."

**Cost of deferral was higher than estimated:** Not wrong, but should have checked for production evidence weekly. Ripley/Lambert can help with lightweight weekly checks on deferred findings.

## Learnings — PR #66 external contributor review 2026-05-28

- **Bug pattern: null ExitCode → sentinel -1 → mis-bucketed as failure.** Helix `/details` returns `ExitCode == null` for Waiting/Running/Unscheduled work items. The code `details.ExitCode ?? -1` coerced null to -1, then `results.Where(r => r.ExitCode != 0)` classified -1 as failed. Fix: derive `IsCompleted = details.ExitCode.HasValue`, three-way bucket (InProgress / Failed / Passed). Null-coercion of nullable ints to sentinels is a recurring hazard when the sentinel value (-1) falls in the domain of valid failure codes.

- **Additive wire-format discipline must be verified, not trusted.** PR claimed "additive" — verified by confirming: (a) new fields use `init` properties with default values (int → 0, nullable list → null with `JsonIgnore(WhenWritingNull)`), (b) existing field names/types/positions unchanged, (c) no tests assert absence of new fields. For MCP DTOs, "additive" means: new fields only, no renames, no type changes, nullable or defaulted so old consumers see no difference.

- **External contributor reviews differ from internal team PRs.** (a) Be especially clear and specific in review feedback — can't easily ping for follow-ups. (b) Merge first when the fix is correct — don't make external contributors wait behind internal PRs that don't conflict. (c) Production evidence in the PR body (specific AzDO build IDs, Helix job IDs, queue names) made verification efficient. (d) File follow-up issues ourselves rather than requesting them from external contributors.

- **Second code path with same bug pattern (`GetWorkItemDetailAsync` line 563) not addressed by PR.** Deliberately scoped to the aggregation path only — correct prioritization since the detail view is informational. Filed as follow-up. Pattern: when fixing a bug in one code path, grep for the same pattern in other paths and explicitly note what's in/out of scope.


## 2026-05-28: PR #66 Review & Issue #67 Policy Decision

**PR #66 Review:** Approved akoeplinger's external contribution fixing Helix waiting work items counted as failed. Identified follow-up on GetWorkItemDetailAsync line 563 ExitCode pattern. Coordinated merge sequencing with PR #68/69.

**Issue #67 Policy Decision:** Reviewed Ash's silent MCP failure investigation. Decided on CallToolFilters middleware as central solution (ArgumentException → McpException, ~10 LOC, all 25 tools). Sequenced v0.7.5 release with CallToolFilters as primary item, schema audit parallel. Deferred per-tool validation prologues (only for combo-rules, narrow scope).

**Deliverables:** PR #66 review document + McpException policy decision document (merged into decisions.md 2026-05-28)


---

## Archived from history.md on 2026-10-01T00:45:00Z

# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, and strict-mode architecture.

**Current Focus:** PR #132 implementation is complete, final internal review is approved, and external review is in progress.

---

## 2026-06-01 Through 2026-08-31: Summary

Completed five major decision cycles:

1. **Parameter Plumbing (June):** Fixed three azdo_* parameter bugs (minTime/maxTime/queryOrder missing, top not forwarded, outcomes hardcoded). PR #78 merged; 14 new tests; all 1337 tests pass.

2. **Numeric Alias Coercion (June):** Implemented CoerceToStringElement in CallToolFilter to handle numeric build_id values. Gap fixed; Ripley + Lambert execution.

3. **Parameter Alias Layer (June):** Established canonical parameter names to reduce agent confusion (buildIdOrUrl, not build_id/buildUrl).

4. **Tiered outputSchema Reduction (July):** Refined "flatten all" → targeted approach (FLATTEN 10 / KEEP 3 / LEAVE 12). Net savings ~5,450 bytes (18% reduction). Dallas decision filed; work approved pending implementation.

5. **Strict-Mode Architecture (July):** Triaged Issue #81/#82 sequencing; designed CallToolFilter layer for unknown-param rejection. Correctness prioritized before cleanup.

All decisions archived to decisions.md. No lockouts issued; steady progress on parameter safety and schema reduction.

---

## Recent Detailed Work


## Learnings

### 2026-07-28: helix_find_files workItem review

**Finding:** When adding an optional parameter to an MCP tool, parameter position matters for C# test callers but NOT for MCP wire callers (JSON binds by name). The sibling convention (jobId → workItem → pattern → maxItems) is the right ordering for MCP tools even if it shifts positional args in tests — tests should use named arguments.

**Finding:** Anticipatory tests written via reflection (to compile before the implementation) become technical debt once the implementation lands — they should be simplified to direct named-argument calls. Flag for Lambert.

**Finding:** Hardcoded `[InlineData]` lists for schema-consistency `[Theory]` tests are pragmatic when no attribute/marker distinguishes the target tool class, but carry a maintenance burden. A code comment near the list is the minimum viable guard.

**Decision:** APPROVED Ripley's implementation + Lambert's tests. Non-blocking: simplify reflection-based tests to named-arg calls, clean up stale "RED until" comments.

## 2026-07-28 — helix_find_files workItem parameter review
Reviewed Ripley's FindFilesAsync + MCP tool implementation and Lambert's test suite. Verified parameter ordering consistency, fast-path correctness, error handling, and tool description. APPROVED. No source-compat concerns. Assigned Lambert non-blocking follow-up tasks: simplify tests, remove stale comments, harden schema test.

---

## 2026-09-11 — Startup cache-eviction lifecycle architecture review & merge approval (#129)

**Ceremony:** Dual-gate: pre-implementation design review (read-only, sync) and independent merge reviewer (sync).

**Design Review:** Identified and specified the correct boundary for fixing issue #129 (untracked startup maintenance task). The issue bundled three defects: (1) untracked handle, (2) unordered disposal, (3) late-binding cutoff (the Windows CI failure root cause). Designed a full solution with construction-time cutoff pinning, internal completion handle, strict disposal ordering (cancel → bounded join → fault observation → CTS disposal → pool clear), and deterministic test contract (no timing, no race-outcome assertions, only observable end-state).

Delivered specifications to three agents:
- **Ripley:** One-file production change (SqliteCacheStore.cs), exact lifecycle per spec
- **Lambert:** Deterministic coverage across 8 new tests, stale-comment cleanup in 2 existing files
- **Kane:** Single [Unreleased] CHANGELOG entry

**Merge Review:** Verified production implementation (90 insertions, one file), fault-propagation reaffirmation (no code change, correct semantics), test coverage (8 facts, zero banned patterns), and CHANGELOG accuracy. APPROVED with no blocking findings. Recorded three non-blocking observations for future reference (cleanup tail skipping on faults, lazy token reading, timeout pool semantics).

**Decisions:** `dallas-startup-cache-eviction-lifecycle.md` (design, §2.1–§2.6 with reject-on-sight gate), `ripley-startup-cache-eviction-implementation.md` (R1 delivery report), `ripley-startup-cache-eviction-fault-reaffirmation.md` (reaffirmed fault contract), `lambert-startup-cache-eviction-tests.md` (coverage report), `kane-changelog.md` (documentation status).

**Orchestration logs:** `.squad/orchestration-log/2026-09-11-0000-dallas-startup-cache-lifecycle-review.md`, `.squad/orchestration-log/2026-09-11-1400-dallas-reviewer-verdict.md`

## 2026-08-20 — MCP C# SDK 1.4.0 → 2.2.0 migration decision

**Verdict:** APPROVED phase-1 (package bump + one explicit config line). REJECTED Ash's
hybrid-session-mode recommendation. Mandated explicit `SessionMode` + 4 new tests.
Decision: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md`.

### Learnings

**Verify agent-reported release metadata against the registry, always.** Ash and Ripley
disagreed on every release date. Ripley was right; Ash dated 2.2.0 to "today" and 1.4.0 to
"early 2026" (actual: 2026-08-13 and 2026-06-04). One `curl` to the NuGet registration
index settled it in seconds. **Both** reports also missed 1.4.1 entirely — the servicing
release on the old major, which is the correct rollback target. Analysts reliably enumerate
the *new* major's releases and skip servicing releases on the *old* one.

**A changed default is a silent diff — force it into source.** `WithHttpTransport()` with no
options changed from stateful to stateless across the major bump with zero textual change at
the call site. The most consequential effect of an upgrade should never be invisible in code
review. Declaring `SessionMode = Stateless` explicitly costs one line and buys: a reviewable
diff, immunity to a future default flip, operator-readable intent, and a place to hang the
"here's the escape hatch" comment. Pair it with an assertion test so deleting the line fails.

**Convenience-proxy properties are a migration trap.** Ripley's fallback snippet
(`Stateless = false`) is a bool proxy over the real `SessionMode` enum, and `false` maps to
`Stateful` — which *refuses* new-revision clients with -32022. The value a maintainer would
actually want (`StatefulForInitializeClients`) is unreachable through the bool. When an SDK
adds an enum alongside a legacy bool, adopt the enum immediately; the bool cannot express
the state space and encodes a wrong answer for the most likely future need.

**Reject backward-compat modes that protect capabilities you don't use.** Hybrid mode's sole
function is handing real sessions to down-level clients. Sessions exist for `Mcp-Session-Id`
affinity, standalone GET SSE, and server→client requests — we use none. Adopting it would
have reintroduced session affinity plus already-`[Obsolete]` idle-session tracking on day
one, and load-balancer affinity is a one-way ratchet operationally. Test for a backward-compat
mode: name the specific capability it preserves, then check we actually use that capability.

**"No impact" without evidence is the thing to gate on.** Both reports independently declared
progress notifications unaffected — neither demonstrated it. That was the *only* place our
design touched what stateless mode removes (unsolicited server→client messages). Reading
`TokenProgress` confirmed progress is request-scoped and should ride the POST response stream,
but agreement between two agents who both reasoned from release notes is not evidence. When
every report converges on "no impact" for the one genuinely adjacent risk, that convergence
is the signal to demand a test, not to relax.

**Spec breakage ≠ SDK breakage.** Ash framed the `initialize` handshake as "gone" because the
2026-07-28 spec removed it. The SDK retains it as the down-level fallback path (probe
`server/discover`, fall back to `initialize`). Reading the spec and reading the SDK's
compat shims are different jobs; overstating this would have justified defensive work we
did not need.

**Cheap void-checks first.** Ash's "MODERATE: must run on .NET 8.0+" collapsed against one
grep — every project is `net10.0`. Grep the repo for the claimed constraint before spending
any reasoning on a reported risk.

**Reflection-based invariant guards beat one-time verification.** #1568 changes wire format
only for non-object structured returns. We have none *today*. A reflection test asserting
every `UseStructuredContent = true` tool returns an object type converts a point-in-time
audit into a standing guard — the cheapest way to stop a future scalar-returning tool from
silently changing the wire format.

---

## 2026-08-20T15:47:37.675-05:00: Review — MCP SDK 2.2.0 migration + T1–T4 (REJECTED)

Reviewed Ripley's phase-1 implementation and Lambert's T1–T4/G4 against my accepted decision.
Production diff, G1, G6, T1, T4 approved. **T2, T3, G4 rejected; G7 found never executed.**
Verdict: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-review.md`.

**I was wrong in my own decision doc, and the error propagated into the test that was supposed
to catch it.** My §1 accepted Ash's and Ripley's shared conclusion that #1568 could not affect
us "because all structured tools return objects." That reasoning is invalid:

> **#1568's trigger is *schema* object-ness, not *CLR* object-ness.** A CLR class carrying a
> custom `[JsonConverter]` is opaque to `System.Text.Json`'s schema exporter, which emits the
> permissive schema `true`. SDK 1.4.0 classified such a type as a **non-object** and wrapped it
> in `{"result": …}`. 2.2.0 does not. The CLR type never changed; the envelope did.

Six tools returning `LimitedResults<T>` (custom converter factory) silently lost their
`{"result": …}` envelope — 24% of our tool surface, user-visible, shipping unremarked.

**How I caught it, and the generalizable move: reconcile a reported delta arithmetically
before accepting its narrative.** Lambert's G4 numbers were correct and the explanation —
"SDK 2.2.0 generates a more compact schema representation… a favorable improvement" — was
plausible. What broke it open was that `outputSchema` dropped to **4 bytes**. Four bytes cannot
describe an object; it is `true`. From there: `{"type":"object","properties":{"result":true},"required":["result"]}`
is **exactly 68 bytes**, and `6 × (68−4) = 384` matched the reported delta exactly. Three
independent numbers agreeing turned a hypothesis into proof without running the old SDK.
**Suspiciously small magnitudes are structural evidence.** A schema that shrinks by 94% has not
been compacted, it has been emptied.

**A green test suite is only evidence about what it asserts.** "1526 passed, 0 failed" was
offered as reassurance. `grep -rl "structuredContent" src/HelixTool.Tests/` returned exactly one
file — the new T2. No test in the repo had *ever* asserted the structuredContent wire shape, so
the suite was structurally incapable of detecting this. **Before crediting a green suite on a
specific risk, grep for a test that mentions it.** One grep, and "1526 passed" stops being an
argument.

**My own §6 T2 specified the wrong assertion — and a wrong guard is worse than none.** I asked
for a reflection test that the CLR return type is non-scalar. Lambert implemented exactly that,
correctly. `LimitedResults<T>` is a sealed class, so it *passed* — the guard green-lit the live
regression it was commissioned to prevent, and that false confidence is why the G4 delta got
rationalized instead of investigated. Correction to the prior entry in this file: an invariant
guard must assert on **the artifact that actually goes on the wire** (the generated
`ProtocolTool.OutputSchema`), not on a proxy for it (the CLR type). Acceptance criterion for
such a guard: **it must fail on the current tree.** A guard that passes on first write has not
been shown to guard anything.

**Reconstruction tests: reject them when the thing under test is that a specific line exists.**
Lambert's T3 rebuilt `Program.cs`'s registration in test code (top-level statements block
`WebApplicationFactory<Program>`) and escalated rather than adding a seam unilaterally — exactly
right. But the reconstruction asserts that ASP.NET Core's options binding round-trips a value:
Microsoft's job, cannot fail for any reason we care about, and stays green if the production
line is deleted. **Config-intent pins are worthless unless driven through the real composition
root.** Ruled for `public partial class Program;` — one behavior-neutral line, and it *also*
unblocks a real G7 against real auth middleware and real `AddScoped` lifetimes. When weighing a
small production seam, count every gate it unlocks, not just the one that prompted the ask.

**A test that goes red for the right event but points at the wrong culprit is a liability.**
Lambert's supplementary `SdkDefault_SessionMode_IsNotSomethingThisAppShouldRelyOn` asserts the
*SDK's* default equals Stateless. If a future SDK flips that default — the exact scenario the
pin exists for — this test fails while blaming the SDK, and the real regression stays green. It
trains the reader to distrust the wrong thing. **Rationale belongs in a comment; assertions
belong on things we control.**

**Gates handed off between agents get dropped in the seam.** G7 was Ripley's; Ripley deferred it
to "Lambert's TestHost coverage," which was never in Lambert's scope. Both reports read as
complete; nobody ran it. And the fixture that appeared to cover it registered `HelixService` as
`AddSingleton` where production uses `AddScoped` — **inverting the exact lifetime G7 exists to
prove.** Two lessons: assign each gate to exactly one owner and require that owner to post
evidence or explicitly escalate; and when a fixture is offered as covering a gate, diff its DI
registrations against production before crediting it.

**What held up.** T1 was excellent and retired the migration's highest risk: real `McpClient`
over real `HttpClientTransport` on `TestServer`, real tool and adapter chain, substitution
pushed down to `IHelixApiClient`, and a `TaskCompletionSource` gate proving the notification
crossed the per-request SSE stream *while the call was in flight* rather than merely landing
eventually — without that gate the assertion would have passed vacuously. Ripley's G6 substituted
a scripted raw JSON-RPC-over-stdio harness for the GUI client I specified and picked a
"No API call made" tool for determinism — better than what I asked for. **Accept substituted
evidence that clears the bar by a better method; the gate's purpose outranks its prescribed form.**

## 2026-08-20T17:15:37.128-05:00 — Final self-review of the MCP SDK 2.2.0 migration (REJECTED again: F1, F2, F3)

**A reported-green suite is only green in the reporter's environment.** Ripley's B1–B3 report
said "1,554 passed / 0 failed," and it was true — on a machine with no API key configured. I
re-ran it myself and got **Failed: 1**: the new `WebApplicationFactory<Program>` test boots the
real host, the real host reads the ambient `HLX_API_KEY`, and the test sends no header, so
initialize returns 401 before it ever reaches the transport. Deterministic in both directions,
not flaky. **Re-run the suite yourself in your own environment before crediting a green
claim** — and when a report omits the environment, that omission is the finding.

**The seam I mandated is the seam that broke.** I ruled Option B (boot the real `Program`) over
Ripley's proposal precisely to make T3 non-tautological, and it worked — T3 now detects a real
config mutation. But booting the real composition root inherits the real app's **environment
coupling**, and I did not anticipate that when I ruled. When a review mandates testing against
production wiring, the same ruling must specify how the test isolates production's ambient
inputs. Own the consequence of your own ruling.

**Scratch evidence is not gitignored by default.** `git check-ignore` proved `.squad/artifacts/`
and `.squad/evidence/` were **not** covered — including a 642-line verbatim copy of upstream SDK
test source with zero license or attribution, one `git add -A` from shipping into a product PR.
**Run `git check-ignore` over every untracked file before calling a diff clean;** reading the
file list is not the same as knowing what will be committed. And two production XML docs cited
that scratch path, so deleting it silently dangles references — cross-references into scratch
directories are a defect at the moment they are written.

**A guard scoped by a hardcoded list has a silent expiry date.** The rewritten structured-content
guard enumerates three tool classes by name; a fourth `[McpServerToolType]` would be silently
unguarded. That is structurally the *same* defect — a guard not covering what it claims — as the
tautology it replaced. When approving a replacement guard, check that the fix changed the failure
mode and not just the assertion.

**Verify a subordinate's severity claim, not just his mechanism — and accept it when he is
right.** Ripley inverted my §1 framing: I had called the legacy envelope a live regression, when
in fact doing nothing preserved legacy clients exactly and *the fix* is what changes them. He was
correct, and I accepted it in full. The point of re-review is that it can correct the Lead. But
verify the mechanism independently anyway: I generated the schema by reflection and measured
1,319 bytes for `azdo_builds`, matching his table, before crediting the number.

**A reassigned gate is still a dropped gate.** G7 has now been dropped by two owners across two
reviews (Ripley deferred, Lambert did not deliver). Reassignment alone does not create
accountability. Third assignment carries a single named owner, an explicit "post evidence or
escalate — silence is not delivery" clause, and no further reassignment. I also bound it to the
*same* fixture as the F1 fix, because splitting one fixture across two agents is exactly the
seam it fell through the first time.

**Byte-budget deltas should be decomposed, not narrated.** Lambert explained a +2,440-byte
`tools/list` growth as "more compact schema representation"; Ripley decomposed it exactly —
−897 (23 async tools × 39 bytes of dropped `execution.taskSupport`, with the two synchronous
tools unchanged as the control) + 3,337 (six real schemas), zero residual — which also disproved
the compaction narrative outright, since the 14 unaffected tools were byte-identical. **Demand
arithmetic that closes to zero; a plausible story about a number is not an account of it.**

---

## 2026-08-20 — Re-review of F1/F2/F3 → APPROVED (MCP SDK 2.2.0 migration cleared for PR)

**Verdict:** APPROVED. All three blocking findings resolved. Commit/PR may proceed; only the
PR-description gate remains (writing task, no code impact).
Decision: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-rereview.md`.

### Learnings

**Mutation-test the gate before you accept it green — especially a gate that has been dropped
before.** G7 had been dropped by two owners across two reviews. A passing run proves the test
runs, not that it *discriminates*. I mutated production four ways and rebuilt each time:
`AddScoped<IHelixApiClient>`→`AddSingleton` (RED), `ComputeTokenHash(token)`→a constant (RED at
the partition assertion), removing `UseApiKeyAuthIfConfigured()` (RED on both 401 facts), and
`SessionMode`→`Stateful` (RED on both T3 facts). Only after that did the evidence mean anything.
The constant-hash mutation was the decisive one: it proved the cache-partition assertion is bound
to the real production computation rather than to a value the test itself supplies. Neither
implementer had demonstrated their gate *could* fail — that demonstration is the reviewer's job
when the gate is load-bearing. Do it with a SHA-256 checksum of the original file and verify
byte-identity on restore, so the diligence cannot corrupt the tree.

**"Test doubles must preserve the lifetimes under test" is checked by reading the production
registrations, not by reading the doubles.** My instinct was that `AddSingleton` doubles would
invalidate a per-request scoping proof — that instinct was right for `StatelessMcpTestHost` and
*wrong* here. `IHelixApiClientFactory` and `ICacheStoreFactory` are **already singletons in
production**, so singleton doubles introduce zero lifetime inversion, and every scoped
registration in the chain runs untouched. More than that: singleton factories are the *only*
correct observation point, because a singleton's recording queue accumulates across requests,
which is precisely what makes cross-request comparison possible. **A singleton test double is
suspicious only when it replaces a scoped production registration.** Compare the two lists
side by side before concluding.

**Specify the property, not the implementation — subordinates can beat your fix.** For F1 I
offered two remedies: attach the header, or clear the variable. Lambert took neither verbatim and
chose better — read the *same* env var through the *same* production constant, apply the *same*
non-empty predicate the middleware uses, and attach the header only when the real host would
require it. Result: both worlds stay under test rather than one being suppressed, and because
both sides reference `ApiKeyMiddleware.EnvVarName`/`HeaderName` rather than literals, a rename
cannot silently desync them. Had I mandated "clear the variable," I would have shipped a strictly
weaker gate.

**A test-ordering assumption should be asserted, not commented.** The G7 class shares one fixture
across four facts; three never dispatch a tool call. Rather than documenting that, the test
asserts `Count == 2` exactly — which converts the assumption into a tripwire that also catches
request retries and duplicate DI resolutions. Mutation M1 confirmed it fires. Prefer an assertion
that encodes the invariant over a comment that describes it.

**Gitignore *and* delete; either alone regresses.** F2 was closed by removing the vendored
upstream source and adding the paths to `.gitignore`. Deletion alone regresses the moment anyone
recreates the directory; ignoring alone leaves the licensing exposure on disk and in any archive.
Also: when a doc citation must point at third-party source, pin it to a **tag** (`blob/v2.2.0/…`),
never `main` — and actually fetch the URL. I did; it returned 200.

**Don't promote a pre-existing wart to a blocker on re-review.** I found eight source files citing
`.squad/decisions/inbox/` paths in XML docs — a gitignored directory, so those references dangle
for anyone reading the PR. Real inconsistency (the tracked precedent cites *tracked* files), but
the same pattern sits in T1/T2/T4 artifacts I had already approved without flagging. Blocking on
it now would be scope creep and would punish the revision authors for my earlier omission.
Recorded as MINOR with a concrete remedy. **A re-review is bounded by the findings that caused
the rejection; new observations get filed, not escalated.**

**Re-run the headline numbers yourself even when the report looks solid.** Lambert reported
1558/2/0/1560 in both env worlds. I reproduced it exactly — targeted (16/16) and full suite, both
worlds, before and after mutation testing. Cheap, and it converts a claim into a fact. It also
caught a small transcription error in Kane's report (files listed as `M` that are actually `??`),
harmless here but the kind of thing that misleads a later reader.

---

## 2026-08-20T18:00:34.354-05:00 — Review of Ripley's fixed-context revision → APPROVED (minimal `{"type":"object"}` outputSchema)

**Verdict:** APPROVED, no blocking code findings. Every claim reproduced independently.
Decision: `.squad/decisions/inbox/dallas-minimal-schema-review.md`. Only outstanding gate is the
PR #123 body, which I found materially inaccurate and for which I supplied exact replacement text.

### Learnings

**`git checkout --` is not a mutation-restore tool when the work under review is uncommitted.**
Mid-mutation I reverted `AzdoMcpTools.cs` with `git checkout --` and silently destroyed Ripley's
undelivered changes — the file went back to `HEAD`, not to the state I was reviewing. The only
reason I caught it is that I had taken SHA-256 checksums *before* mutating and compared after every
restore; the hash came back `a6d9d2d0…` instead of `61db2141…`. I rebuilt the six substitutions and
the record deletion from the diff I had captured in context and re-verified byte identity. **On an
uncommitted diff, restore by exact reverse string replacement, never by `git checkout`** — and treat
the pre-mutation checksum as mandatory, not as diligence theatre. It is the difference between a
recoverable slip and destroying a subordinate's unpushed work.

**A constant remainder is a stronger "nothing else changed" proof than reading the diff.** Rather
than eyeballing that only six attribute lines moved, I subtracted the six `LimitedResults<T>`
schemas from the all-tools `outputSchema` total in each of the three states: 8,961−408, 12,298−3,745,
8,655−102 — **8,553 every time**. One arithmetic identity certifies that all 14 other structured
tools are byte-identical across a *major SDK boundary*, which no amount of diff-reading does.
Prefer an invariant that must hold over an enumeration that must be complete.

**Verify the baseline's behaviour from the old SDK's source, not from the new one's model.** The PR's
compatibility note claimed the fix only moves pre-`2026-07-28` clients. I fetched
`AIFunctionMcpServerTool.cs` at tag **v1.4.0** and found `CreateStructuredResponse(object?)` — no
protocol-version parameter at all, wrapping decided once at creation time. `main` therefore shipped
`{"result": …}` to *every* client, so the change is universal, not down-level-only. The note wasn't
merely imprecise; it understated the blast radius, and it did so because it described the
*intermediate* PR state (2.2.0 without the fix) as if it were the merge target. **When a compat note
describes "before," pin which commit "before" means and read that commit's SDK.**

**Reproduce the numbers by re-running the repo's own harness in throwaway worktrees.** Detached
`git worktree add` at `6eb3905` and `9f007f19` let me run the existing
`McpToolsListPayloadTests.ToolsListPayload_ReportActualBytes` unmodified in each and get
30,366 / 32,806 / 29,163 — all three matching Ripley exactly, including the six per-tool sizes in
declaration order. Cheaper and far less error-prone than writing a probe, and it uses the same
measurement definition the branch is being judged by. Remove the worktrees in the same session;
`git worktree list` is the check that you did.

**Demand that the delta decompose, then check the decomposition at tool granularity too.**
−1,203 = −306 (schemas) + −897 (dropped `execution.taskSupport`), zero residual — and
`azdo_builds` alone: 2,103 → 2,013 = −51 − 39. A total that closes to zero can still hide two
compensating errors; a per-tool row that also closes to zero cannot.

**Mutation-test the *documented trap*, not just the guard.** The skill Ripley wrote warns that
`typeof(object)` exports as `true` and reintroduces the version split. I mutated one tool to
`typeof(object)` and watched the advertised schema become `true` at `2026-07-28` and the 68-byte
`{"result":true}` placeholder at `2025-06-18` — the split rendered visible in the schema itself. That
single mutation simultaneously proved the guard discriminates *and* that the skill's claim is true.
When a subordinate's write-up asserts a trap, the mutation that proves the guard should be the trap.

**"Weakened validation" must be judged against the merge target, not against the rejected draft.**
My instinct was that dropping a property mirror weakens the contract. It does — relative to the
*pre-revision PR*, which never shipped. Relative to `main`, whose schema was
`{"properties":{"result":true}}` (the permissive any-schema), descriptiveness is a **wash**, and
self-consistency actually improves because `main` advertised `required:["result"]` for a payload the
revision no longer emits. The C# SDK never validated `structuredContent` against `outputSchema` in
either version, so nothing enforceable was lost. **Ask "what does this PR change for a consumer of
`main`" before calling anything a regression.**

**Explicit `OutputSchema` short-circuits the return-schema path — the marker is more robust than
claimed.** SDK 2.2.0's `CreateOutputSchema` returns `toolCreateOptions.OutputSchema` *before*
consulting `function.ReturnJsonSchema`, so a future `[return: Description]` or `<returns>` doc
cannot inflate these six tools' 17 bytes. Reading two functions past the one under discussion turned
a maintenance worry into a documented guarantee. Read the caller and the fallback, not just the
predicate.

---

## 2026-08-26 — Snapshot export successor hardening design

Facilitated the required pre-work design review for the standalone successor to merged PR #125.
Inspected current `origin/main` and all five unresolved review threads. Approved a surgical
contract in `.squad/decisions/inbox/dallas-snapshot-hardening-design.md`.

The decisive architecture choices are:

- SQLite online backup (`SqliteConnection.BackupDatabase`) is the database consistency boundary;
  export never checkpoints or copies the live DB/WAL/SHM file set.
- Artifact selection comes from the backed-up `cache_artifacts` rows, not recursive live-directory
  enumeration.
- The complete temporary snapshot must pass schema, `integrity_check`, no-sidecar, containment,
  existence, and size checks before same-parent atomic rename.
- Destination comparison uses physical canonical paths, component-by-component link/junction
  resolution, Windows case-insensitive boundaries, and non-Windows ordinal boundaries. Resolution
  ambiguity fails closed before temp creation.
- The auth warning is unconditional. Identical `AZDO_TOKEN` plus identical effective
  `AZDO_TOKEN_TYPE` can replay environment-keyed entries through the merged environment-only eval
  accessor; Azure CLI identities remain unreproducible.
- Traversal errors invalidate a snapshot but never increment `MissingArtifactFiles`.

Ownership is non-overlapping: Ripley owns Core implementation, Kane owns `SnapshotCommands.cs` and
the PR narrative, and Lambert owns `SnapshotExportTests.cs`. Cache options, composition roots,
SQLite store behavior, project files, fixture format, key normalization, record mode, and Vally APIs
are frozen.

---

## 2026-08-26 — Snapshot export hardening independent review

Issued an overall **REJECT** while accepting Ripley's Core implementation and Kane's CLI wording.
The blocking defect is confined to Lambert's concurrency test artifact: its checkpoint readiness
counter advances for any checkpoint result row, including a zero-page attempt that can occur before
the first writer commit, and its baseline metadata check proves only row count rather than the
seeded keys and JSON values.

Targeted tests passed 29/29, the stress test passed twelve repeated runs, and the full suite passed
1,661 with two pre-existing skips. Those green runs do not repair a missing proof invariant.
Lambert is locked out of the next `SnapshotExportTests.cs` revision. Because every other rostered
specialist is barred by charter from writing tests, I explicitly requested escalation to a new
.NET concurrency/filesystem test specialist.

---

## 2026-08-26 — Parker snapshot stress revision re-review

**Verdict:** **REJECT.** Parker fixed both original proof gaps: checkpoint readiness now follows a
committed write and requires positive checkpoint progress, and every exported snapshot checks the
three exact baseline keys and JSON values.

Repeated execution exposed a new shutdown race in the same checkpoint loop. Two separate campaigns
both failed on attempt 24 because SQLite returned a WAL-page count of minus one after no WAL was
currently present, and the test treated that non-progress response as an assertion failure. The
targeted snapshot selection passed 40 tests, but the stress test passed only 46 of 48 repeated
attempts before the two failures stopped their campaigns.

Parker is now locked out of this artifact, Lambert remains locked out, and I requested another new
independent .NET and SQLite concurrency test specialist. The revision gate remains closed, so the
pull request is not ready for final suite or CI.

---

## 2026-08-26 — Bishop snapshot stress revision final re-review

**Verdict:** **APPROVE.** Bishop's narrow change handles only the exact no-current-WAL result after
checkpoint readiness as non-progress. It preserves the committed-write ordering and requires
positive checkpoint progress before readiness. The same response before readiness, malformed rows,
and inconsistent values still fail.

The cancellation, finite busy handling, bounded final wait, and background exception propagation
are unchanged. All 29 tests in `SnapshotExportTests.cs` passed with
`DOTNET_ROLL_FORWARD=Major`, followed by 48 consecutive passes of the writer/checkpointer stress
test.

The revision gate is cleared. The complete pull request is ready for final full-suite and Ubuntu
and Windows CI validation.

---

## 2026-08-26 — PR #127 review-thread triage

**Verdict:** **REJECT.** The unresolved database-link finding and the related artifact-directory
finding are both valid blockers. `SnapshotValidator` resolves `cache.db` and `artifacts/` but never
requires either resolved path to remain a strict child of the resolved snapshot root. It can
therefore validate an external database, an external artifact tree, or an `artifacts/` alias back
to the snapshot root.

The duplicate layout assertion is valid but minor. The orchestration log also records the wrong
decisions-file path in two places; that is not a runtime blocker, but the log is rejected as an
inaccurate record. The 28 focused exporter/validator tests pass, confirming that current coverage
does not exercise these aliases.

Ripley is locked out of the rejected validator revision. Lambert and Parker remain ineligible for
the test artifact, and Bishop owns the current rejected version, so a new independent test owner
is required. I requested a new .NET filesystem-security implementer and a separate cross-platform
filesystem test specialist. Scribe is locked out of the rejected log revision; Kane may make the
two factual path corrections. Detailed acceptance criteria are in
`.squad/decisions/inbox/dallas-pr127-review-triage.md`.

---

## 2026-08-26 — PR #127 boundary revision recheck

**Verdict:** **APPROVE.** Brett's validator now rejects a resolved database or existing artifacts
directory unless it is a strict physical child of the snapshot root, at the required points before
sidecar/database or row/file inspection. The comparison is separator-aware, ignores case only on
Windows, rejects equality, and preserves the missing-artifacts warning.

Burke's three focused regressions exercise an external database alias, a populated external
artifacts alias, and an artifacts alias to the snapshot root on every platform, with Windows
junctions for directories, safe alias cleanup, and focused boundary assertions. The duplicate
layout assertion is gone. Kane corrected both decisions-file references.

All 43 focused snapshot tests passed with `DOTNET_ROLL_FORWARD=Major`. The review gate is cleared,
and PR #127 is ready for the full suite and Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 second-review triage

**Verdict:** **REJECT.** The fresh macOS containment finding is valid and blocking. Exporter
boundaries use ordinal comparison outside Windows, but this worktree's macOS volume resolves
case-only spellings to the same directory. A new child below a case-only source spelling can
therefore bypass containment. The current case-only test returns without assertions on macOS and
passes vacuously.

The suppressed test findings are also valid. The hardening rewrite removed exporter rejection
coverage for missing source, database, destination parent, schema versions, and required tables,
and removed validator coverage for missing layout, wrong schema, and missing tables. The new
integrity check has no corrupt-database regression. The current-focus record is stale: the
1,661-test local suite and refreshed Ubuntu, Windows, and Squad checks completed successfully at
`dcc755a5`, although this rejection now requires them to run again after revision.

Ripley is locked out of the exporter revision. Lambert, Parker, Bishop, and Burke are locked out of
the next `SnapshotExportTests.cs` revision, which must have one newly recruited independent .NET
filesystem and SQLite test owner. Kane may correct the current-focus record; Scribe is locked out.
The exact revision and acceptance gates are recorded in
`.squad/decisions/inbox/dallas-pr127-second-review-triage.md`.

---

## 2026-08-26 — PR #127 second-review recheck

**Verdict:** **APPROVE.** Frost applied the required conservative macOS/Windows ignore-case
boundary rule while preserving ordinal Linux/other behavior, separator boundaries, pre-creation
containment, and the destination-parent recheck. Hudson restored every named exporter and validator
negative case, replaced the vacuous macOS test with an actual-filesystem branch, and added
deterministic non-throwing corruption coverage. Ten repeated corruption runs passed. No global
SQLite pool clear or unrelated weakening appeared.

Kane's focus record accurately captures the prior completed gates, the reopened review, the named
revisions, and the required reruns. All 43 focused `SnapshotExportTests` cases passed with
`DOTNET_ROLL_FORWARD=Major`, with no skips or failures. The revision gate is cleared; PR #127 is
ready for the full local suite and fresh Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 Ubuntu CI triage

**Verdict:** **REJECT** Hudson's current test revision; Frost's production revision remains
accepted. Ubuntu's case-sensitive success path preserved the source `cache.db` and artifact bytes
exactly but SQLite's read-only WAL opens materialized `cache.db-shm` and an empty `cache.db-wal`.
Those SQLite-managed sidecars may legitimately appear or disappear and are not source corruption.

The independent replacement must compare exact persistent source payload bytes and logical database
state while excluding only the two root SQLite sidecars on the success path. It must not weaken the
full-tree source checks on pre-database-open rejection paths or their shared helper. No production
change is warranted. Hudson is locked out from revision and advice; prior test-owner lockouts remain,
so the Coordinator must recruit a new independent .NET/SQLite filesystem test owner. Windows was
canceled during restore and must run again with the full suite and Ubuntu after Dallas re-review.

---

## 2026-08-26 — PR #127 Ubuntu CI recheck

**Verdict:** **APPROVE.** Vasquez's local success-path helper excludes only root regular-file
fingerprints for SQLite's WAL/SHM lifecycle while retaining exact database, artifact, other-file,
directory, and link comparison. Integrity, schema/user versions, the complete schema, and every
fixture table column are compared before and after export.

The case-insensitive rejection branch and shared strict helper are unchanged, and both platform
branches remain substantive. With `DOTNET_ROLL_FORWARD=Major`, the case-only test passed its build
run plus 10 repeated no-build runs, and all 54 focused snapshot tests passed with no skips. Frost's
production exporter remains accepted and frozen; the local gate is cleared for the full suite and
fresh Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 WAL readiness CI triage

**Verdict:** **REJECT** the stress helper at `9a7fd86`; Frost's production exporter remains accepted
and frozen. Ubuntu exposed the startup form of the WAL lifecycle race Bishop handled only after
readiness: a committed-write signal does not guarantee that a separately and lazily opened
checkpointer connection immediately has a current WAL, so exact `(-1,-1)` is legitimate
non-progress before readiness too.

The replacement must establish and hold an explicit WAL writer/anchor, assert WAL mode on both
worker connections, sequence worker initialization before a known committed write, and retry exact
`(-1,-1)` under a bounded timeout without completing readiness. Readiness still requires a
post-commit, non-busy PASSIVE result with positive WAL and checkpointed page counts.

Hicks is assigned as the new independent .NET/SQLite concurrency test specialist. Lambert, Parker,
Bishop, Burke, Hudson, and Vasquez are locked out from both revision and advice. Exact gates are in
`.squad/decisions/inbox/dallas-pr127-wal-readiness-ci-triage.md`; fresh Ubuntu and Windows CI remain
mandatory.

---

## 2026-08-26 — PR #127 WAL readiness CI recheck

**Verdict:** **APPROVE** Hicks's test-only WAL-readiness revision for the full-suite and fresh-CI
gate. The unpooled anchor now spans initialization through cancellation and worker join; WAL mode,
autocheckpointing, the zero-page baseline, four ordering gates, and checkpointer attachment are
explicit. The strict state machine treats exact `(-1,-1)` as retryable non-progress before and after
readiness, rejects invalid rows, and permits readiness only for positive post-commit progress.

All 53 tests in `SnapshotExportTests.cs` passed, followed by 100 isolated repetitions of the real
writer/checkpointer stress test, with zero failures or skips under `DOTNET_ROLL_FORWARD=Major`.
Existing export invariants are unchanged and no production file changed. Frost's exporter remains
accepted and frozen; the local gate is cleared for the full suite and fresh Ubuntu/Windows CI.

---

## 2026-09-04 — Helix queue monitor: combined review and roadmap

**Verdict:** **ACCEPT WITH CORRECTIONS** on Ash's requirements analysis and Ripley's backend
audit. Four fixes land now (D1–D4), two items defer (D5b, D6), six are rejected. Recorded in
`.squad/decisions/inbox/dallas-helix-queue-monitor-roadmap.md`.

The reframing that drove every decision: queue monitor did not create a capability gap, it
exposed a **projection gap**. Reflecting over `Microsoft.DotNet.Helix.Client`
`11.0.0-beta.26325.102` showed `JobSummary` — the type `Job.ListAsync` already returns —
carries `QueueId`, `Properties`, `Created`, `Finished`, `InitialWorkItemCount`, and
`FailureReason`. `HelixApiClient.ListJobNamesByBuildAsync` discards all of it via
`.Select(j => j.Name)`. Restoring that projection satisfies Ripley P1, most of Ash US-Q2, the
real need behind US-Q3, and the seed of US-Q7 at **zero additional HTTP cost**. Every accepted
item is a fix; every rejected item is a new feature. That split was not imposed, it fell out.

**Material correction — Ripley P4 is wrong as specified.** Ripley proposed grouping by
`(logical name | PhaseName, QueueId)` and keeping max `System.JobAttempt`, attributing it to
arcade's `GetLatestHelixJobAttempts`. Arcade (`MonitorState.cs:593-601`) actually uses a
**lineage-leaf rule**: superseded iff another job's `PreviousHelixJobName` points at it;
attempt numbers only order, never select. Ripley's key would silently *delete* legitimate
concurrent jobs — arcade's own `LogicalJobName` docs say one AzDO job can submit several Helix
jobs to the same queue. Undercounting is a worse failure than the overcounting it targeted.
Ripley cited the right primitive in §1 and failed to carry it into the recommendation, so the
correction is mechanical and Ripley keeps ownership; no lockout.

**Sequencing principle worth reusing:** annotate before filtering. D5a exposes `Superseded`
as a free additive field and changes no counts; D5b (actually filtering) stays unapproved
until D5a surfaces a real build proving duplicates occur. Changing a user-visible count on an
unproven hypothesis is precisely what Ripley was right to escalate, and the answer was "prove
it with the cheap version first," not "yes" or "no."

**Rejection pattern in Ash's proposal:** four of six opportunities proposed new MCP tools for
capabilities the server already composes. US-Q1 and US-Q6 are fully served by
`azdo_timeline`/`azdo_search_timeline`/`azdo_search_log` — the last already accepts a null
`logId` and scans all ranked steps. US-Q4 is a heuristic wrapper whose `confidence` field we
cannot calibrate, and D1 removes the reason to branch on topology at all. US-Q3's need was
real but met by an additive `Source` field, not a tool. Also caught two factual slips: the
find-files default is 30 not 50, and "<5s for 500+ jobs" conflates submission-level
aggregation (free) with result-level pass/fail (N calls, unachievable). No lockout on Ash —
requirements analysis is supposed to surface candidates review rejects — but reviving US-Q1,
US-Q4, or US-Q6 now requires a concrete failing investigation transcript.

Verified true against local code: the monitor's `failed ({State}).` format can never match
`FailedWorkItemRegex` (`AzdoService.cs:767`); `ParentJobName` collapses to `HelixJobMonitor`
(`:957-959`); build-wide prose errors vanish under `filter="failed"` (`:963` guard);
`CiKnowledgeService.cs:229/787` still describes the fallback as the whole tool, which is wrong
independent of dotnet/sdk behavior — so D4 does not block on a live build.

Compatibility rule I want held: **no behavior may branch on "monitor detected."** D1 helps both
topologies because the same submitter stamps the same properties either way, and D5's leaf rule
is a provable no-op on legacy. If an implementer needs a topology flag, the design went wrong.

No production code or tests written. The reflection probe was deleted and the worktree is clean.

## 2026-09-04: Helix queue-monitor design review and roadmap adjudication (completed)

Completed architectural review of Ash's requirements and Ripley's audit. Verified all claims against local code (HelixApiClient projection bug) and arcade source (lineage-leaf dedup rule). Adjudicated parallel proposals into ranked roadmap: six items approved (D1–D6: four fixes, one enhancement, one doc), two items deferred to later gate (D5b), seven new-tool proposals rejected. Key finding: existing tools compose to same capability; JobSummary metadata restoration is mechanical fix with zero additional HTTP cost.

**Status:** COMPLETED  
**Outcome:** Roadmap verdict ACCEPT WITH CORRECTIONS; D1–D6 ready for implementation planning; D5b gated on D5a evidence

## Ownership Assignment

- **Ripley:** D1, D2, D3, D5a, D5b, D6 (primary owner of queue-monitor fixes)
- **Kane:** D4 (documentation correction)

## 2026-09-04: Pre-work Design Review — queue-monitor compatibility slice 1 (completed)

Ran the read-only Design Review ceremony for the first implementation slice. Brief recorded at
`.squad/decisions/inbox/dallas-queue-monitor-design-review.md`. No production file or test
touched; worktree clean, build 0/0 before and after.

**Scope decided:** D1, D2, D3 (+ new D3b), D4, D5a. D6 pushed to slice 2 (unrelated subsystem,
drags `ProgressOverStatelessHttpTests` in). D5b still gated on D5a evidence.

**Two accepted premises corrected on primary-source evidence** (fetched `dotnet/arcade@main`
verbatim rather than trusting the prior summary):
- D3's "detect by task name `Monitor Helix Jobs`" is **struck**. `helix-job-monitor.yml` names
  both the Job and the Task `Monitor Helix Jobs`, so the existing `Name.Contains("helix")`
  predicate already finds them — and a name gate is precisely the "branch on monitor detected"
  my own compatibility rule #1 forbids. Parsers apply unconditionally.
- D3's "extract the GUID from the console URL" is **demoted to fallback**. `HelixJobInfo.cs:149`
  puts the GUID in `DisplayName` (`"{label} - {queue} ({guid})"`, or bare `{guid}`), and
  `MonitorState.cs:656` can emit the literal `"no console link available"`. DisplayName first.

**Found a second monitor format nobody had named** (`StatusReporter.cs:334-354`): an aggregated
`Failed work item information:` tree, emitted as `LogError`, carrying `// DO NOT CHANGE THIS
LINE - it's matched by Build Analysis`. Added as D3b — same function, same test file, zero new
surface, and it is the format arcade has explicitly pinned. Leaving the most stable signal
unparsed would have been the worse call.

**Reframed what D3 actually fixes.** The GUID is usually already recovered today (the console
URL matches `HelixJobIdRegex`). What is lost is the **work-item → job association**, because
`FailedWorkItemRegex` requires the literal `has failed`. Naming the defect precisely changed
the tests I demanded.

**Rulings worth remembering:**
- D2's open question ("reuse `FailedWorkItems`, or add a field?") — *this review was the review
  the decision deferred to*. Verdict: reusing it is semantically wrong, because clients feed
  that list to `helix_search(jobId, workItem)`; raw AzDO prose there produces a predictable
  misuse. Added bounded `Messages` (≤20 × ≤500 chars, only on empty-GUID rows).
- Added `Strategy` beyond the accepted decision, and justified it as **correctness, not
  convenience**: D1 gives `Result` two vocabularies across the two paths, so having introduced
  the ambiguity we are obliged to ship the discriminator.
- **Declined to narrow D1's `Result`** even though the new `State` field makes it redundant on
  the Helix path. Re-litigating an accepted criterion for tidiness, after Lambert holds it,
  costs more than one redundant field. Guard rail instead: `Result` is never a pass/fail verdict
  there, and `Note` must say so.
- "Running with errors" pinned as `State=="running" && Result=="unknown" && (TaskErrorCount>0 ||
  TaskWarningCount>0)`. Counts deliberately unchanged (rule #4) — the obligation that creates is
  **disclosure in `Note`**, not silence.
- Counts named `TaskErrorCount`/`TaskWarningCount` on purpose: N rows from one task all carry
  the same value, and the prefix is what stops someone summing them.

**Process lesson — the shim removed the only real ordering conflict.** Renaming
`ListJobNamesByBuildAsync` would red the test project the moment R1 landed, forcing either
Ripley into tests or Lambert into a wait. Keeping it as an undecorated delegating shim (not
`[Obsolete]` — that breaks the 0-warning gate that is itself a merge criterion) buys full
three-way parallelism for the price of one mandatory deletion step, R4. I made R4 a merge gate
so the shim cannot quietly become permanent.

**Two new standing compatibility rules** (now #5 and #6): no new *positional* record parameters
on wire types, because `azdo_helix_jobs` generates its output schema from the record and
positional params generate as required; and Newtonsoft stops at `HelixApiClient` — no
`JObject`/`JToken` may cross `IHelixApiClient`.

**Deliberately left broken:** the `filter="all"` wart at `AzdoService.cs:918` (issue-free helix
tasks skipped even under `all`). Real, pre-existing, and fixing it moves `TotalHelixJobs` without
evidence. Recorded in the brief so it is a known wart rather than a future rediscovery.

**Did not convene Ripley/Lambert/Kane as subagents.** Everything was answerable from local source
plus arcade; they would have re-read the same files. Recorded the omission as deliberate.

**Status:** COMPLETED
**Outcome:** Design accepted; R1/L1/K1 may begin. Eight named reject-on-sight conditions recorded
for my own merge review.

## 2026-09-08: v0.10.0 release-preparation commit (completed)

Performed mechanical release prep per `.squad/skills/release-cut/SKILL.md`, staying on
worktree branch `lewing-release-v0-10-0` (task instructed not to switch/push/tag). Confirmed
the only three authoritative version surfaces are `src/HelixTool/HelixTool.csproj`
`<Version>`, and `src/HelixTool/.mcp/server.json` top-level `version` + `packages[0].version`
— `publish.yml`'s `validate-version` step checks exactly these three against the pushed tag.
No other release-version surface exists in the repo (grepped for `0.9.1` outside
`.squad/release-notes/` and `CHANGELOG.md`).

Bumped all three 0.9.1 → 0.10.0. Promoted CHANGELOG's `[Unreleased]` to
`## [v0.10.0] — 2026-09-08` (dated from CURRENT_DATETIME, not release-notes convention which
would need a separate `.squad/release-notes/v0.10.0.md` — that file was not requested by this
task and was intentionally not created, since the task scope was CHANGELOG promotion only) and
added a fresh empty `[Unreleased]` above it, matching the pattern the v0.9.1 commit (6eb3905)
and v0.9.0 gap-fix established.

Validation: `dotnet build -c Release --no-incremental` (0 Warning(s), 0 Error(s));
`DOTNET_ROLL_FORWARD=Major dotnet test -c Release --no-build` (1954 passed, 8 skipped
pre-existing, 0 failed, matches prior session's noted local-runtime-11-vs-target-10 mismatch
workaround); `dotnet pack src/HelixTool -c Release -o src/HelixTool/nupkg
/p:Version=0.10.0` produced `lewing.helix.mcp.0.10.0.nupkg`. Unzipped and inspected: nuspec
`<version>0.10.0</version>`, bundled `.mcp/server.json` also at 0.10.0, `DotnetToolSettings.xml`
present, `packageTypes` includes both `DotnetTool` and `McpServer`. Deleted the nupkg output
directory after inspection — not part of the release-prep commit.

Diff reviewed before commit: exactly 3 files, 5 insertions/3 deletions — the two version bumps
plus the two-line CHANGELOG heading insertion. No unreleased-content text was altered, no
unrelated worktree changes swept in.

Committed as `00c18d2` on `lewing-release-v0-10-0`. Did NOT tag, push, or open a PR — per this
task's explicit instruction, those happen only after this commit is reviewed and merged to
main by Larry. Post-merge command is unchanged from the skill: tag the merged main-tip commit
with `git tag -a v0.10.0 -m "Release v0.10.0"` then `git push origin v0.10.0`, which triggers
`publish.yml` (validates versions, packs, creates GitHub Release, pushes to NuGet).

**Status:** COMPLETED
**Outcome:** Release-prep commit `00c18d2` ready for review/merge; no tag pushed.

## 2026-09-11: Pre-implementation design review — startup cache-eviction lifecycle (#129) (completed)

Ran the read-only design review for #129. Brief at
`.squad/decisions/inbox/dallas-startup-cache-eviction-lifecycle.md`. No production or test file
touched; Release build 0 Warning(s)/0 Error(s) before and after.

**Reframed the defect, which changed the fix.** The issue title says "track and await", but
`_ = Task.Run(() => EvictExpiredAsync())` bundles three defects, and tracking only addresses two.
The third — the pass computes `DateTimeOffset.UtcNow` *when it eventually runs*, not when the
store was opened — is the one that actually reddened Windows CI on PR #128. A tracked task makes
that race observable; it does not remove it. So the decision requires the startup pass to pin its
cutoff at construction time, making "startup maintenance deleted a row written after
construction" structurally impossible on every platform. The public `EvictExpiredAsync(ct)`
contract is explicitly left evaluating `UtcNow` at call time, so no existing caller or test moves.

**Durable pattern — pinned as-of timestamp for startup sweeps.** Any maintenance pass scheduled
at construction should capture its cutoff at construction, not at execution. Otherwise the
scheduler decides what data is eligible, and no test delay can close that. Generalizes beyond
this cache.

**Refused to add a test seam, and said why in the brief.** The tempting fix for "prove
cancellation is honored mid-pass" is a static hook — but a process-global mutable hook forces
suite serialization, which the issue explicitly forbids. Ruled instead that determinism comes
from `await store.StartupMaintenance` (internal, riding the existing
`InternalsVisibleTo HelixTool.Tests` on HelixTool.Core) plus observable end-state: after
`Dispose()` returns, the task is completed and a fresh connection to the database opens and
writes immediately. Added an escalation rule so Lambert brings any un-expressible invariant to me
rather than inventing surface.

**Banned outcome-of-race assertions by name.** "Ran to completion" vs. "was canceled" after a
construct-then-dispose is genuinely scheduling-dependent — asserting it is the *same* class of
mistake as the original failing test. Listed as a reject-on-sight condition, because it is the
kind of test that passes locally and fails on one CI OS.

**Made "minimal public impact" mechanically checkable:** the production diff is exactly one file,
`SqliteCacheStore.cs`. If `ICacheStore`, `ICacheStoreFactory`, `EvalModeServices`, or `Program.cs`
needs to move, the design is wrong and Ripley stops. A criterion someone can verify with
`git diff --stat` beats a criterion they have to interpret.

**Narrow-catch table instead of a prose rule.** The body catches nothing but cancellation
(detected via `IsCancellationRequested`, per the cancellation-vs-timeout skill, not token
identity); `Dispose` absorbs exactly `OperationCanceledException`, `SqliteException`,
`IOException`, each with a named reason, and lets everything else propagate. Enumerating the three
is enforceable in review; "avoid broad swallowing" is not.

**Declined to overclaim in the CHANGELOG.** The CLI never disposes its `ServiceProvider`
(`Program.cs:113`), so at process exit the pass is abandoned by the OS rather than joined. Told
Kane the guarantee is "no untracked work", not "always joined at shutdown", and recorded the
limitation in the brief so the next reader does not have to rediscover it.

**Put two stale test comments in scope.** `SnapshotEvalModeTests.cs:53-60` and the rationale
header of `ExpiredSnapshot.cs` both document the fire-and-forget task as current behavior — they
exist only because of this bug. The workarounds themselves stay (the backup retry still guards
real cross-process WAL contention), but comments describing a fixed bug as live are the same
defect class I rejected in the last documentation cycle.

**Did not convene subagents.** Everything was answerable from the cache subsystem plus the two
test files that already document the bug; Ripley and Lambert would have re-read the same code.
Recording the omission as deliberate, same as the 2026-09-04 review.

**Status:** COMPLETED
**Outcome:** Design accepted. R1 (Ripley, `SqliteCacheStore.cs` only), L1 (Lambert, four test
files), K1 (Kane, CHANGELOG `[Unreleased]` only) may begin. Nine reject-on-sight conditions
recorded for my merge review.

## 2026-09-11: Pre-fix evidence and regression coverage review — pool scope & artifact source (#130) (completed)

Conducted sync merge-gate review of Ripley's artifact-source FileShare seam and Lambert's pre-fix regression coverage. No production or test file touched; Release build maintained 0 Warning(s)/0 Error(s).

**Ripley's Seam (Artifact-Source FileShare Constant):**
- Added `private const FileShare ArtifactSourceFileShare = FileShare.Read;` in `SnapshotExporter.cs`
- Replaced inline `FileShare.Read` literal with constant reference — behavior-neutral naming seam
- Value unchanged; defers `FileShare.Read | FileShare.Delete` fix to dedicated fix-step commit
- Allows Lambert's Windows discriminator test to compile now and fail pre-fix

**Lambert's Pre-Fix Regression Coverage (7 new facts):**
- `SqliteCacheStoreConcurrencyTests`: independent-roots pool-scope discriminator (Windows-only defect observable)
- `SnapshotExportTests`: concurrent artifact overwrite while exporter holds source handle (Windows share-policy defect observable); concurrent eval-mode validator read (positive regression baseline)
- `WindowsOnlyFactAttribute`: reusable platform-specific test marker
- `AzdoEvidenceSurfaceTests`: manual pool-clear cleanup call removal
- Validation: 187 passed, 7 skipped (Windows-only facts on Unix), 0 failed; full suite baseline maintained (1981 passed, 8 skipped, 0 failed)

**Architecture Verified:**
- One-PR design: scoped `ClearPool` fix + permissive source-share fix
- No retries, no serialization; pure deterministic isolation via scope boundary
- Both defects now observable in pre-fix tests on Windows CI; production fixes will make tests green

**Rework Requested:** None

**Status:** COMPLETED
**Outcome:** APPROVED with no blocking findings. Seam and pre-fix tests complete and ready to merge. Production fixes themselves land in follow-up commits.

## 2026-09-11: Evidence-driven scope amendment retrospective and final merge verdict (#130)

Monitored Ripley's two-commit production fix and Lambert's regression-test validation. Approved evidence-driven scope amendment and issued final APPROVED verdict with no blocking findings.

**Scope Amendment Retrospective:**

Initial plan called for `MoveFileEx` atomic-move on Windows to solve artifact-replacement file-handle conflicts. R2 (c58340d) changed `SnapshotExporter` source-share to `FileShare.Read | FileShare.Delete`, expecting that alone to solve concurrent-write failures.

Lambert's artifact-replacement test immediately proved this assumption wrong on Windows — test RED post-c58340d. Root-cause analysis identified `File.Move(..., overwrite: true)` itself lacks share-conflict awareness on Windows, even when source is opened with permissive flags.

R3 (10149cf) used `File.Replace` for existing artifacts (atomic, share-aware) and `File.Move` for absent artifacts. This turned Lambert's failing test GREEN, proving the scope amendment correct.

**Verification Summary:**
- Pool-scope fix: discriminator test RED → GREEN (c58340d)
- Source-share fix: eval-mode baseline GREEN (c58340d)
- Artifact-replacement fix: test RED → GREEN (10149cf)
- All platforms: Ubuntu CI ✓, Windows CI ✓, Squad CI ✓
- Test counts: 387 targeted pass (including Windows-only facts), 1995 full-suite pass, 9 pre-existing skips, 0 failures
- Production scope verified: exactly `SqliteCacheStore.cs` + `SnapshotExporter.cs`, no ICacheStore/public API changes

**Key Learning:** Evidence-driven scope amendment validated via test outcomes (red-to-green transitions) rather than assumption about platform semantics. Regression-test-first methodology correctly exposed the hidden `File.Move` defect that implementation-first would have shipped.

**Final Verdict:** APPROVED — no blockers, ready for merge. PR #140 to be marked ready for merge once this bookkeeping commit is pushed (per orchestration protocol).

**Status:** COMPLETED
