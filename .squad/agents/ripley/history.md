# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing
- **Stack:** C# .NET 10, ConsoleAppFramework, ModelContextProtocol, Microsoft.DotNet.Helix.Client
- **Structure:** Four projects — HelixTool.Core (shared library), HelixTool (CLI), HelixTool.Mcp (HTTP MCP server), HelixTool.Mcp.Tools (MCP tool definitions)
- **Key service methods:** GetJobStatusAsync, GetWorkItemFilesAsync, DownloadConsoleLogAsync, GetConsoleLogContentAsync, FindBinlogsAsync, DownloadFilesAsync, GetWorkItemDetailAsync, GetBatchStatusAsync, DownloadFromUrlAsync
- **HelixIdResolver:** Handles bare GUIDs, full Helix URLs, and `TryResolveJobAndWorkItem` for URL-based jobId+workItem extraction
- **MatchesPattern:** Simple glob — `*` matches all, `*.ext` matches suffix, else substring match

## Core Context

- **Implementation layout:** service code lives under `src/HelixTool.Core/Helix/` and `src/HelixTool.Core/AzDO/`; MCP tool definitions live under `src/HelixTool.Mcp.Tools/Helix/` and `src/HelixTool.Mcp.Tools/AzDO/`.
- **Cache/search primitives:** `CachingHelixApiClient`, `CachingAzdoApiClient`, `StringHelpers`, and `TextSearchHelper` are the shared implementation seams; `HLX_CACHE_MAX_SIZE_MB=0` disables caching and `HLX_DISABLE_FILE_SEARCH` disables file-content search.
- **Wire-format conventions:** structured MCP tools use `UseStructuredContent=true`, camelCase JSON/property names remain stable, and descriptions stay behavior-first with repo-specific guidance routed to `helix_ci_guide`.
- **Hot-path log rules:** keep large-log search/tail work span-based, overflow-safe, and semantically aligned with server behavior; when tagging raw cached payloads, prefer collision-proof sentinels over human-readable prefixes.
- **Test/routing defaults:** avoid layer-duplicate or passthrough-only tests, and keep repo-specific workflow guidance in `helix_ci_guide` instead of bloating always-loaded tool descriptions.
- **Auth/runtime:** Helix auth remains env-var based, while AzDO auth now uses the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous with metadata carried by `AzdoCredential`.

## Summarized History (2026-03-08 through 2026-03-13 pre-PR-28) — archived 2026-03-13

Detailed notes for AzDO search/log ranking, MCP error surfacing, CI-knowledge description churn, Option A restructuring, review-fix security hardening, discoverability routing, Helix MCP renames, truncation metadata, and early AzDO auth-chain hardening moved to `history-archive.md`.

- **Durable routing/doc rules:** keep repo-specific CI guidance in `helix_ci_guide`, keep tool descriptions short and behavior-first, and keep scope-accurate MCP names like `helix_search` / `helix_parse_uploaded_trx`.
- **Architecture/security rules:** shared code lives under `HelixTool.Core/{Helix,AzDO,Cache}` and `HelixTool.Mcp.Tools/{Helix,AzDO}`; shared utilities belong in `StringHelpers`; security-sensitive path containment uses normalized full-path + Ordinal root-boundary checks; `HelixService` requires injected `HttpClient`.
- **UX/runtime rules:** capped MCP list/search responses should surface explicit truncation metadata (`LimitedResults<T>`, `LogSearchResult.Truncated`), and AzDO auth stays on the narrow chain `AZDO_TOKEN` → `AzureCliCredential` → az CLI → anonymous with scheme-aware `AzdoCredential` metadata and sanitized error snippets.

## Learnings (remaining AzDO auth threat-model follow-ups)

- **CLI schema generation now respects explicit JSON property names:** `src/HelixTool.Core/CliSchema/SchemaGenerator.cs` uses `JsonPropertyNameAttribute` values when present, so `--schema` matches the actual `System.Text.Json` wire names instead of always reflecting raw CLR property names.
- **Historical CLI JSON casing is per-field, not per-command:** for `status`, `files`, and `work-item`, the old anonymous-object field names in `src/HelixTool/Program.cs` are the wire contract, so typed DTOs should add `[JsonPropertyName]` only on members that were explicitly lowercase/camelCase (`job`, `jobId`, `totalWorkItems`, `failedCount`, `passedCount`, `duration`, `failureCategory`, `binlogs`, `testResults`, `other`, `files`) and leave CLR-default PascalCase members alone.
- **CLI-only JSON DTOs should keep serializer defaults unless the external wire format truly requires overrides:** the private `status`, `files`, and `work-item` DTOs in `src/HelixTool/Program.cs` are meant to preserve the CLI's exact historical output, which may mix CLR-default PascalCase with explicit camelCase field names, so only members that were explicitly renamed in the old anonymous-object payloads should carry `[JsonPropertyName]`.

- **Refreshable fallback credentials now carry expiry metadata:** `AzCliAzdoTokenAccessor` caches Azure CLI and `az`-subprocess credentials with a refresh deadline (`min(expiresOn - 5m, now + 45m)`) instead of pinning the first token for the full process lifetime; 401/403 responses now invalidate that cached fallback so the next request re-resolves auth.
- **Stable cache isolation uses source identity, not token bytes:** `AzdoCredential.CacheIdentity` is derived from the auth path plus stable JWT claims (`tid`/`oid`/`appid`/`sub`) when available, and `AzdoApiClient` stores `CacheOptions.AuthTokenHash` from that stable identity after the first successful authenticated AzDO response. `CachingAzdoApiClient` then prefixes all AzDO cache/state keys with that hash when present.
- **Auth visibility is now first-class metadata:** `IAzdoTokenAccessor.AuthStatusAsync` reports the resolved path, credential source, expiry signal, and warnings without making an AzDO request; `hlx azdo auth-status` prints or serializes that safe metadata for operators.
- **Key file paths:** `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` now owns fallback-token refresh, cache identity derivation, and auth-status reporting. `src/HelixTool.Core/AzDO/AzdoApiClient.cs` now invalidates stale fallback auth on 401/403 and seeds `CacheOptions.AuthTokenHash` after successful auth. `src/HelixTool.Core/AzDO/CachingAzdoApiClient.cs` applies the auth hash to AzDO cache keys, while `src/HelixTool/Program.cs` exposes `azdo auth-status` and the CLI/MCP hosts both pass shared `CacheOptions` into `AzdoApiClient`.
- **PAT cache identities now get a secret-safe fingerprint fallback:** when `AzdoCredential.BuildCacheIdentity` cannot extract JWT claims, it appends the first 8 hex chars of a SHA256 over the token so PAT-backed AzDO contexts stay isolated without persisting the raw secret.
- **AzDO key partitioning is now established before cache reads and kept separate from cache-root partitioning:** `AzdoApiClient` seeds the mutable AzDO auth hash immediately after credential resolution, `CachingAzdoApiClient` also pre-resolves that hash before its first cache lookup, and `CacheOptions` now keeps stable cache-root partitioning in `CacheRootHash` instead of reusing `AuthTokenHash`.
- **Cache-key hygiene and CLI auth-status exits were tightened together:** `CachingAzdoApiClient` sanitizes the auth-hash segment before composing cache/state keys, and `hlx azdo auth-status` now sets a non-zero exit code for anonymous status even on the `--json` path.
- **JWT cache identities now fall back to fingerprints when stable claims are missing:** `AzdoCredential.BuildCacheIdentity` only returns claim-based identities when at least one of `tid`, `oid`, `appid`, or `sub` is present; otherwise JWTs use the same SHA256 suffix path as non-JWT tokens so cache partitioning stays per-principal instead of collapsing to the bare prefix.
- **AzDO token env-var tests are now serialized:** the `AzdoTokenEnv` xUnit collection now has `DisableParallelization = true`, which keeps PATH-mutation tests from racing unrelated tests that spawn external processes.

## Learnings (MCP Timeline Analysis — 2026-07-18)

- **`azdo_timeline` filter='failed' does MCP-layer record filtering but still returns raw `AzdoTimelineRecord` objects with all fields:** The MCP layer (lines 68–122 of `AzdoMcpTools.cs`) fetches the full timeline from the API client (`_client.GetTimelineAsync`), then filters client-side to non-succeeded records plus their parent chain. For large VMR builds (~600+ records), the raw AzdoTimeline JSON can be 400–600KB even after filtering to failures, because each record carries issues arrays, previousAttempts, log references, workerName, etc.
- **`azdo_timeline` filter='failed' does server-side record filtering but still returns raw `AzdoTimelineRecord` objects with all fields:** The MCP layer (lines 68–122 of `AzdoMcpTools.cs`) fetches the full timeline from the API client (`_client.GetTimelineAsync`), then filters client-side to non-succeeded records plus their parent chain. For large VMR builds (~600+ records), the raw AzdoTimeline JSON can be 400–600KB even after filtering to failures, because each record carries issues arrays, previousAttempts, log references, workerName, etc.
- **`azdo_search_timeline` solves the large-output problem by returning only pattern-matched records with a compact DTO:** `SearchTimelineAsync` (lines 182–290 of `AzdoService.cs`) fetches the same full timeline but then applies text search on record names and issue messages, returning `TimelineSearchMatch` objects that include only recordId, name, type, state, result, duration, logId, matchedIssues, and parentName — dramatically smaller than raw records.
- **The ci_guide workflow instructions never mention `azdo_search_timeline`:** All 9 repo profiles in `CiKnowledgeService.cs` recommend `azdo_timeline(buildId, filter='failed')` as the first investigation step, but none mention `azdo_search_timeline` as an alternative. The generic overview also omits it. This is the discoverability gap that caused the gist scenario.
- **Intended AzDO investigation workflow:** `azdo_timeline` (identify failed steps) → `azdo_log` (read specific step log by logId) or `azdo_search_log` (search across logs by pattern). `azdo_search_timeline` is a shortcut that combines timeline retrieval + filtering + text search in one call, ideal when you know what error text you're looking for.
- **MCP tool layer does significant post-processing over raw API:** `AzdoService` orchestrates URL resolution via `AzdoIdResolver`, parallel metadata fetches, ranked log search with early termination, and result shaping — it's not a thin pass-through. The API client (`AzdoApiClient`) handles auth and HTTP; the service layer handles business logic.

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane
- **Added CLI schema discovery for JSON commands:** `src/HelixTool.Core/CliSchema/SchemaGenerator.cs` now builds pretty-printed JSON skeletons from reflected public types, including placeholder scalars, single-item collections, nested object recursion, enum value summaries, and circular/depth protection. `src/HelixTool/Program.cs` now exposes `TryPrintSchema<T>` and wires `--schema` into the 14 CLI query commands that already support `--json`.
- **Preserved existing Helix JSON wire shapes while introducing named schema types:** the `status`, `files`, and `work-item` CLI commands now serialize private DTOs in `src/HelixTool/Program.cs` with `[JsonPropertyName]` attributes so their runtime `--json` payloads stay stable even though schema discovery now reflects named types instead of anonymous objects.
- **`azdo search-log --schema` follows the active JSON mode:** when `--log-id` is supplied, the CLI prints `LogSearchResult`; otherwise it prints `CrossStepSearchResult`, matching the command's two existing JSON response shapes without redesigning the command surface.
- **CLI describe metadata is now generator-driven:** `src/HelixTool.Generators/DescribeGenerator.cs` reads MCP `[Description]` metadata from the referenced `HelixTool.Mcp.Tools` assembly, joins it with CLI `[McpEquivalent]` + `[Command]` metadata from `src/HelixTool/Program.cs`, and emits `HelixTool.Generated.CommandRegistry` so MCP descriptions stay the single source of truth.
- **CLI/MCP parity markers live in Core:** `src/HelixTool.Core/McpEquivalentAttribute.cs` is the shared marker attribute that CLI command methods use to opt into the generated registry without introducing a shared constants file for descriptions.
- **Key file paths:** `src/HelixTool.Generators/HelixTool.Generators.csproj` is the netstandard2.0 analyzer project referenced from `src/HelixTool/HelixTool.csproj`, and `hlx describe` summary/detail rendering now lives in `src/HelixTool/Program.cs`.

## Learnings (Timeline Truncation & Discoverability — 2026-07-18)

- **Partial response pattern for large MCP outputs:** When `azdo_timeline` returns >200 records, the MCP layer now truncates to the first 100 records and includes a `note` field with a ⚠️ message pointing the agent to `azdo_search_timeline`. The `TimelineResponse` record (in `AzdoMcpTools.cs`) wraps the timeline data with `truncated`, `totalRecords`, and `note` fields — using `JsonIgnore(Condition = WhenWritingDefault/WhenWritingNull)` so non-truncated responses stay clean.
- **Larry's preference: partial response > silent drop.** When output is too large, return what fits with a continuation indicator rather than dropping data silently. This applies to any MCP tool that might produce oversized responses.
- **ci_guide now recommends azdo_search_timeline as the preferred first investigation step** for repos that previously recommended `azdo_timeline(buildId, filter='failed')` first. Updated: runtime, sdk, roslyn, efcore, VMR profiles, plus the generic overview and non-Helix summary sections. Profiles that don't start with azdo_timeline (aspnetcore, maui, macios, android) were left as-is since their workflows start differently.
- **Threshold constants live in the MCP tool class:** `MaxTimelineRecords = 200` and `TruncatedTimelineBudget = 100` are private consts in `AzdoMcpTools` — the truncation is a presentation concern, not core business logic.

## Learnings (Auth Error UX — 2025-07-25)

- **MCP tool classes now take token accessors in constructors:** `AzdoMcpTools(AzdoService, IAzdoTokenAccessor)` and `HelixMcpTools(HelixService, IHelixTokenAccessor)` — both are injected by DI; tests pass `Substitute.For<...>()`.
- **`azdo_builds` and `azdo_test_attachments` are now URL-aware:** `TryExtractOrgProjectFromUrl` in `AzdoMcpTools` detects when the `org` param contains `dev.azure.com`, `visualstudio.com`, or `://` and auto-extracts org/project via `AzdoIdResolver.TryResolve`. Other tools already get this for free via their `buildId` param going through `AzdoIdResolver.Resolve`.
- **404 hint pattern for internal builds:** `AppendNotFoundHint` in `AzdoMcpTools` checks if the "not found" error mentions the default org/project (`dnceng-public`/`public`), and if so appends a hint that the build might be internal — directing the agent to pass the full URL or set `org='dnceng'` and `project='internal'`.
- **Two new auth-status MCP tools shipped:** `azdo_auth_status` delegates to `IAzdoTokenAccessor.AuthStatusAsync()` (returns `AzdoAuthStatus` record). `helix_auth_status` checks `IHelixTokenAccessor.GetAccessToken()` and resolves `TokenSource` from `ChainedHelixTokenAccessor` when available, returning a `HelixAuthStatus` record.
- **Key file paths:** `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` (TryExtractOrgProjectFromUrl, AppendNotFoundHint, azdo_auth_status), `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` (helix_auth_status, HelixAuthStatus record).

📌 Team update (2026-05-08): MCP SDK v1.0.0 → v1.3.0 upgrade pending — parallel research (Ash) and inventory (Dallas) complete; recommendation to upgrade to v1.3.0 (low risk, no code changes required); awaiting decision to spawn Ripley for upgrade PR. See `.squad/decisions/inbox/*` and `.squad/log/2026-05-08T20-29-00Z-mcp-sdk-upgrade-research.md` for research details and drift items flagged.

## 2025 — MCP SDK 1.3.0 upgrade + Central Package Management migration
- **Branch:** `squad/mcp-sdk-1.3.0-upgrade`
- **CPM migration pattern (reusable):**
  1. Create `Directory.Packages.props` at repo root with `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`.
  2. Aggregate every `PackageReference Version=` from every csproj into `<PackageVersion Include= Version= />` entries.
  3. Strip `Version=` from each `PackageReference` (keep `Include=` and any `PrivateAssets`/`OutputItemType` attrs).
  4. If any project uses floating ranges (`*`, `*-*`), add `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` — otherwise `NU1011` blocks restore.
  5. CPM surfaces pre-existing `NU1507` (multiple NuGet sources w/o package source mapping) — note as separate cleanup, don't conflate.
- **Version-source pattern (replaces hardcoded `ServerInfo.Version`):**
  ```csharp
  var serverVersion = System.Reflection.Assembly.GetExecutingAssembly()
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
  options.ServerInfo = new() { Name = "hlx", Version = serverVersion };
  ```
  Works in both top-level Program.cs (`HelixTool.Mcp`) and class-based commands (`HelixTool`). HelixTool csproj's `<Version>0.5.4</Version>` flows through automatically; HelixTool.Mcp has no `<Version>` so it gets `1.0.0.0` (assembly default), which is fine for an unpacked aspnetcore host.
- **stdio resources bug:** the `hlx mcp` subcommand registered `WithToolsFromAssembly` but not `WithResourcesFromAssembly` — meaning `ci://profiles` and `ci://profiles/{repo}` resources were silently invisible to stdio clients (Claude Desktop, Cline, etc.) while HTTP clients saw them. Always mirror `WithToolsFromAssembly` and `WithResourcesFromAssembly` calls across hosts; treat divergence as a bug.
- **MCP SDK 1.0.0 → 1.3.0:** zero code changes required for our usage (root-endpoint HTTP transport, no manual `RequestContext` construction). Per Ash's research, the 1.2.0 breaking changes (legacy SSE off-by-default, RequestContext ctor obsolete) don't affect us.

## 2026 — MCP progress notifications (issue #43)
- **Branch:** `squad/mcp-progress-notifications` (off main; PR pending).
- **MCP SDK 1.3.0 server-side progress API — verified surface:**
  - **The pattern is auto-injection.** Any tool method parameter typed
    `IProgress<ProgressNotificationValue>` is bound by the SDK at invocation
    time. If the client included a `_meta.progressToken` in `tools/call`, the
    SDK manufactures a forwarding sink whose `Report` calls turn into
    `notifications/progress` JSON-RPC messages with that token. **If the client
    did NOT include a progress token, the parameter is still injected but
    bound to a no-op sink** — so emitting progress is always safe.
  - The parameter is omitted from the tool's JSON schema (clients don't see it).
  - `ProgressNotificationValue` has init-only `float Progress`, `float? Total`,
    `string? Message`. Use object-initializer syntax.
  - Lower-level escape hatch: `McpSession.NotifyProgressAsync(ProgressToken, ProgressNotificationValue, …)` exists for code outside the auto-injection path
    (e.g. when you have a raw `RequestContext`). We did not need it.
- **Architecture decision:** Service layer (`HelixService`, `AzdoService`) stays
  MCP-free. Added `HelixTool.Core/ProgressUpdate.cs` (record struct
  `(double Current, double? Total, string? Message)`) + `ProgressReporter`
  static helpers. The MCP tool layer owns a tiny `McpProgressAdapter` that
  translates `IProgress<ProgressNotificationValue>` → `IProgress<ProgressUpdate>`.
  Adapter returns `null` when the inner sink is `null` so the no-op fast path
  doesn't even allocate.
- **Granularity rule:** ~5–10 emits per long run. `ProgressReporter.ItemStep(total)`
  returns `max(1, total/10)`. `CopyToWithProgressAsync` emits every 10% of total
  (or every 1 MiB when `Content-Length` is missing) plus a 250ms throttle so a
  small file doesn't spam.
- **Tools instrumented:**
  - `helix_download` → per-file ("Downloaded N of M files: <name>") for the
    work-item path; chunked bytes ("Downloaded 42 of 128 MB") for the URL path.
  - `helix_find_files` → "Scanned N of M work items (K matches)" every ~10%.
  - `azdo_search_log` → "Searched N of M log steps (K matches)" every ~10%.
  - `azdo_log` skipped — confirmed it's a single fetch, no streaming.
- **Smoke test (file-based C# app under throwaway `scratch/` dir, deleted after):**
  Spun up an in-proc HttpListener serving 4 MiB in 256 KiB chunks with 40ms
  delays; called `ProgressReporter.CopyToWithProgressAsync` directly; confirmed
  4 events fired (0%, ~45%, ~89%, 100% — the 250ms throttle suppressed
  intermediate emits, which is correct behavior). Output bytes match input.
- **Backward compat:** All public service signatures got an *optional*
  `IProgress<ProgressUpdate>?` parameter inserted **before** the
  `CancellationToken`. This is a binary-compatible source change for callers
  using named args, but **breaks any caller that passed `cancellationToken`
  positionally as the next argument**. Fixed three internal call sites
  (`FindBinlogsAsync`, two `DownloadFilesAsync` calls inside HelixService,
  one test in `DownloadTests.cs`) by passing `progress: null,` explicitly.
  Lesson: append-at-end is gentler; insert-before-CT only works because we
  control all callers. **Future progress params should still go before CT to
  keep CT visually last.**
- **Working-tree gotcha (one-time):** Mid-task the working tree was switched
  out from under me to `squad/mcp-tool-annotations-and-cleanup` (the parallel
  branch) — likely the parallel agent grabbed the same checkout. My MCP tool
  edits got blown away. Recovered by salvaging diffs to a `.salvage/` dir,
  switching back to `squad/mcp-progress-notifications`, and reapplying. **In
  shared workspaces, `git worktree list` early so each squad member can spawn
  a separate worktree per branch.**
## Learnings — Release process (v0.6.0, 2026-05-08)
- **Version lives in two files only** — bump both:
  - `src/HelixTool/HelixTool.csproj` → `<Version>X.Y.Z</Version>`
  - `src/HelixTool/.mcp/server.json` → both `.version` (top-level) and `.packages[0].version`
- **No `CHANGELOG.md` convention.** Prior releases (v0.5.0 onward) used the release-commit message and the GitHub Release body as the changelog. Don't invent one.
- **Release notes drafts** now live under `.squad/release-notes/vX.Y.Z.md` (introduced this release for use with `gh release create --notes-file`). Reuse this path going forward.
- **Tag format:** `vMAJOR.MINOR.PATCH` (e.g. `v0.6.0`). Tag triggers `.github/workflows/publish.yml` (`on: push: tags: v*`) which validates that the tag version matches both `HelixTool.csproj` `<Version>` and the two `server.json` versions before publishing — any mismatch fails the publish job.
- **Release commit subject style:** `release: vX.Y.Z — <one-line scope>`.
- **Release branch convention:** `squad/release-vX.Y.Z` PR'd into `main`. Tag is created on the merge commit AFTER the PR lands, never pre-tag.
- **Co-author trailer** required on the release commit: `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- **CI workflows of interest:** `ci.yml` (build/test on PR), `publish.yml` (tag → NuGet/MCP registry publish), `squad-release.yml` (squad-side coordination — not inspected this round).

## Learnings — Pagination Standardization Implementation (2026-05-20, commit 1a2e1d0)

**Files changed:**
- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` — wrapped azdo_changes and azdo_test_runs in CreateLimitedResults()
- `src/HelixTool.Mcp.Tools/McpToolResults.cs` — added truncated+note fields to 5 Helix result types
- `src/HelixTool.Core/AzDO/AzdoModels.cs` — added truncated+note fields to 3 AzDO result types (HelixJobsFromBuildResult, TimelineSearchResult, BuildAnalysisResult)
- `src/HelixTool.Core/Helix/HelixService.cs` — added FindFilesResults wrapper record, updated FindFilesAsync to return it with truncation metadata
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` — wired truncation logic for helix_find_files
- `src/HelixTool/Program.cs` — updated CLI find-files command to show truncation warning
- `src/HelixTool.Tests/Helix/WorkItemDetailTests.cs` — fixed test to match new FindFilesResults shape

**Build:** ✅ Succeeded (dotnet build passed clean)  
**Branch:** `squad/pagination-standardize` (created from main)  
**Commit:** `1a2e1d0` — "Standardize pagination across MCP tools (Phase 1+2)"

**Deviations from Dallas's spec:** None — all 2 🔴 tools wrapped, all 8 🟡 bespoke result types updated with truncated+note fields (helix_search and azdo_search_log already had truncation, as noted in spec).

**Pattern learned:** When changing service-layer return types, remember to:
1. Update the record definition (or add new wrapper)
2. Update the service method signature + implementation
3. Update all tool/CLI call sites
4. Update tests that directly call the service
5. Clean build to avoid stale reference errors
