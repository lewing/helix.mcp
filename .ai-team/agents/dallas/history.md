# Dallas — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
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
