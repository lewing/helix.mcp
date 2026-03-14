# Ripley — History

## Project Learnings (from import)
- **Project:** hlx — Helix Test Infrastructure CLI & MCP Server
- **User:** Larry Ewing (lewing@microsoft.com)
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

- **Refreshable fallback credentials now carry expiry metadata:** `AzCliAzdoTokenAccessor` caches Azure CLI and `az`-subprocess credentials with a refresh deadline (`min(expiresOn - 5m, now + 45m)`) instead of pinning the first token for the full process lifetime; 401/403 responses now invalidate that cached fallback so the next request re-resolves auth.
- **Stable cache isolation uses source identity, not token bytes:** `AzdoCredential.CacheIdentity` is derived from the auth path plus stable JWT claims (`tid`/`oid`/`appid`/`sub`) when available, and `AzdoApiClient` stores `CacheOptions.AuthTokenHash` from that stable identity after the first successful authenticated AzDO response. `CachingAzdoApiClient` then prefixes all AzDO cache/state keys with that hash when present.
- **Auth visibility is now first-class metadata:** `IAzdoTokenAccessor.AuthStatusAsync` reports the resolved path, credential source, expiry signal, and warnings without making an AzDO request; `hlx azdo auth-status` prints or serializes that safe metadata for operators.
- **Key file paths:** `src/HelixTool.Core/AzDO/IAzdoTokenAccessor.cs` now owns fallback-token refresh, cache identity derivation, and auth-status reporting. `src/HelixTool.Core/AzDO/AzdoApiClient.cs` now invalidates stale fallback auth on 401/403 and seeds `CacheOptions.AuthTokenHash` after successful auth. `src/HelixTool.Core/AzDO/CachingAzdoApiClient.cs` applies the auth hash to AzDO cache keys, while `src/HelixTool/Program.cs` exposes `azdo auth-status` and the CLI/MCP hosts both pass shared `CacheOptions` into `AzdoApiClient`.
- **PAT cache identities now get a secret-safe fingerprint fallback:** when `AzdoCredential.BuildCacheIdentity` cannot extract JWT claims, it appends the first 8 hex chars of a SHA256 over the token so PAT-backed AzDO contexts stay isolated without persisting the raw secret.
- **AzDO key partitioning is now established before cache reads and kept separate from cache-root partitioning:** `AzdoApiClient` seeds the mutable AzDO auth hash immediately after credential resolution, `CachingAzdoApiClient` also pre-resolves that hash before its first cache lookup, and `CacheOptions` now keeps stable cache-root partitioning in `CacheRootHash` instead of reusing `AuthTokenHash`.
- **Cache-key hygiene and CLI auth-status exits were tightened together:** `CachingAzdoApiClient` sanitizes the auth-hash segment before composing cache/state keys, and `hlx azdo auth-status` now sets a non-zero exit code for anonymous status even on the `--json` path.
- **JWT cache identities now fall back to fingerprints when stable claims are missing:** `AzdoCredential.BuildCacheIdentity` only returns claim-based identities when at least one of `tid`, `oid`, `appid`, or `sub` is present; otherwise JWTs use the same SHA256 suffix path as non-JWT tokens so cache partitioning stays per-principal instead of collapsing to the bare prefix.
- **AzDO token env-var tests are now serialized:** the `AzdoTokenEnv` xUnit collection now has `DisableParallelization = true`, which keeps PATH-mutation tests from racing unrelated tests that spawn external processes.

📌 Team update (2026-03-14): helix-cli skill docs must reflect shipped CLI behavior: use `hlx llms-txt` for CLI discovery, note no `hlx ci-guide` command yet, and keep `hlx search-log` CLI docs text-only. — decided by Kane
- **Added CLI schema discovery for JSON commands:** `src/HelixTool.Core/CliSchema/SchemaGenerator.cs` now builds pretty-printed JSON skeletons from reflected public types, including placeholder scalars, single-item collections, nested object recursion, enum value summaries, and circular/depth protection. `src/HelixTool/Program.cs` now exposes `TryPrintSchema<T>` and wires `--schema` into the 14 CLI query commands that already support `--json`.
- **Preserved existing Helix JSON wire shapes while introducing named schema types:** the `status`, `files`, and `work-item` CLI commands now serialize private DTOs in `src/HelixTool/Program.cs` with `[JsonPropertyName]` attributes so their runtime `--json` payloads stay stable even though schema discovery now reflects named types instead of anonymous objects.
- **`azdo search-log --schema` follows the active JSON mode:** when `--log-id` is supplied, the CLI prints `LogSearchResult`; otherwise it prints `CrossStepSearchResult`, matching the command's two existing JSON response shapes without redesigning the command surface.
- **CLI describe metadata is now generator-driven:** `src/HelixTool.Generators/DescribeGenerator.cs` reads MCP `[Description]` metadata from the referenced `HelixTool.Mcp.Tools` assembly, joins it with CLI `[McpEquivalent]` + `[Command]` metadata from `src/HelixTool/Program.cs`, and emits `HelixTool.Generated.CommandRegistry` so MCP descriptions stay the single source of truth.
- **CLI/MCP parity markers live in Core:** `src/HelixTool.Core/McpEquivalentAttribute.cs` is the shared marker attribute that CLI command methods use to opt into the generated registry without introducing a shared constants file for descriptions.
- **Key file paths:** `src/HelixTool.Generators/HelixTool.Generators.csproj` is the netstandard2.0 analyzer project referenced from `src/HelixTool/HelixTool.csproj`, and `hlx describe` summary/detail rendering now lives in `src/HelixTool/Program.cs`.
