## Learnings — Summary (archived earlier entries)

See history-archive.md for:
- MCP SDK 1.0.0 → 1.3.0 upgrade analysis
- Pagination standardization (Phase 1+2)
- DTO consolidation patterns
- Issue #59 quick wins (SDK defaults, structured output)
- Issue #61 Bug A+B (param rename, exception centralization)
- RollForward policy, release flows, earlier baselines
- AzDO timeline filter presets (PR #56)
- Description tightening (PR #57)
- v0.7.3 release flow summary

## Current Work (2026-05-28 through 2026-06-01)

### Learnings — Issue #67 CallToolFilters middleware (2026-05-28)

- SDK API confirmed on ModelContextProtocol 1.3.0: `McpServerOptions.Filters.Request.CallToolFilters` exists, and `CallToolFilters` can be appended inside the existing `.AddMcpServer(options => ...)` startup configuration.
- Filter converts SDK parameter-binding `ArgumentException`s into `McpException` before the MCP server's generic formatter hides details. Double-wrap discipline: catches `ArgumentException` only when `ex.ParamName == "arguments"` (matching SDK binder's exact param name from #67 repro).
- Helper extraction pattern: cross-cutting MCP request filters live as extension methods on `McpServerOptions` in `src/HelixTool.Mcp.Tools`, so stdio and HTTP startup paths each keep a one-line `options.AddBindingErrorFilter()` call.

### PR #69: CallToolFilters Middleware (2026-05-28)

- Implemented AddBindingErrorFilter() in new src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs
- Integrated filter registration in both src/HelixTool/Program.cs and src/HelixTool.Mcp/Program.cs
- Added 2 unit tests for filter behavior
- Resolves Issue #67 Class A silent failures (all 25 tools automatically protected)
- **v0.7.5 shipped** (PR #69 merged)

### v0.7.5 Release Flow (2026-05-28 mechanical release)

- Synced main: commit 5c7852e. Bumped version 0.7.4 → 0.7.5 (3 locations: .csproj, server.json top-level + packages[0].version).
- Build: 0 errors, 0 warnings. Tests: 1298 passed, 0 failed, 2 skipped.
- Release commit: c801bb5. Tag: v0.7.5 pushed to origin. Publish workflow: run 26599303495.
- **Shipped PRs:** PR #66 (akoeplinger): IsCompleted bucketing. PR #68 (Lambert): required-param schema clarity audit. PR #69 (Ripley): CallToolFilters middleware.

### Issue #70 & PR #71: GetWorkItemDetail IsCompleted Bucketing (2026-05-29)

- Applied PR #66's Helix work-item completion pattern to single-item detail path: `details.ExitCode.HasValue` is completion signal.
- Added focused coverage for waiting work items (IsCompleted=false, ExitCode=-1, no failure category).
- When adding new bool fields to Helix DTOs, mirror non-breaking default on every serialized wrapper (source DTOs + CLI/MCP result wrappers). Missing wrapper default causes older JSON payloads to deserialize absent fields as `false` and flip completed items to incomplete.

### User-Agent Identifier & v0.7.6 Release (2026-05-29)

- Outbound tool-owned `HttpClient` traffic: `HelixToolUserAgent.Apply(HttpClient)` adds `User-Agent: helix.mcp/{version}` + `X-Helix-Mcp-Tool: helix.mcp` on AzDO and Helix download named clients.
- Helix SDK exposes `Azure.Core.ClientOptions.AddPolicy(...)` hook through `HelixApiOptions`, so SDK calls carry same UA/tool header via per-call pipeline policy.
- **v0.7.6 shipped:** Release commit 0bc0095 (0.7.5 → 0.7.6). Build: 0 errors. Tests: 1300 passed.
- **Shipped PRs:** PR #73 (User-Agent + X-Helix-Mcp-Tool header). PR #71 (IsCompleted backport).

### Tool Rename Validation (2026-05-30)

- Cross-checked dotnet org for hard-coded tool name references: zero code-level pinning of `helix_*` tool names.
- **Verdict:** Rename `helix_status` → `helix_workitems` is safe. No alias needed. Expected landing in next cycle.

### Issue #74: MCP Schema Trimming Decision (2026-06-01)

**Ground-truth measurement (completed by Ripley 2026-06-01T14:02:01Z):**
- Serialization: `McpServerTool.Create()` → `ProtocolTool` → `JsonSerializer.Serialize()` (canonical wire path).
- **Real `tools/list` payload: 28,941 bytes (28.26 KB)** — 44% larger than issue's 16,212-byte heuristic estimate.
  - inputSchema: 11,068 bytes (10.81 KB) — matches Ash's estimate within 2% ✅
  - outputSchema: 8,882 bytes (8.67 KB) — critical discovery (20/25 tools have UseStructuredContent=true)
- Ash's measurement framework validated. Issue #74 closed with Dallas verdict.
- **Measurement test:** src/HelixTool.Tests/McpToolsListPayloadTests.cs (added as regression guard, 1301 tests pass).
- Top outputSchema contributors (trim candidates): azdo_timeline (1,123 B), helix_status (1,001 B), azdo_build (929 B).

**Dallas Verdict (2026-06-01T14:12:04.001-05:00):** CONDITIONAL NO on active trimming. Cold-load payload cached per-session, not per-turn. At <1% of session token budget, solves problem we don't have. Revisit if: consumer re-fetches per-turn, tool count >40, or token budget pressure reported. Best lever when needed: Pattern 2 (selective outputSchema removal, 4.5–8.9 KB, no breaking change). No trim implementation assigned.

### AzDO buildIdOrUrl Alias Proposal (2026-06-01)

**Surface confirmed:** 11 AzDO MCP tools require `buildIdOrUrl`: azdo_build, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_artifacts, azdo_search_log, azdo_search_timeline, azdo_helix_jobs, azdo_build_analysis.

**Issue:** `AzdoService.NormalizeFilter(...)` works for values after binding. Cannot fix `buildUrl` / `build_id` keys because MCP/AI binder rejects missing required parameter names before tool method bodies run.

**Solution:** Add one generic CallToolFilter mapping aliases (`build_id`, `buildId`, `buildUrl`) to canonical `buildIdOrUrl` when canonical key is absent. Keeps schema bytes flat. No wire-format breaking change. Recommended for **v0.7.7** as compatibility/discoverability bugfix (separate from schema-trim work).

---

**Status:** Issue #74 finalized (CONDITIONAL NO). buildIdOrUrl alias approved as separate v0.7.7 task. Awaiting Larry/Dallas approval on v0.7.7 scheduling.
## 2026-06-01: Implemented AzDO `buildIdOrUrl` MCP argument aliases

- Implemented Dallas-approved alias normalization inside the existing `AddBindingErrorFilter` in `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs`, so normalization runs before SDK parameter binding and before binding errors are wrapped.
- Added case-insensitive aliases `build_id`, `buildId`, and `buildUrl` for canonical `buildIdOrUrl`. Conflict semantics: an existing canonical key wins; if multiple aliases are present without canonical, insertion order decides (`build_id` > `buildId` > `buildUrl`).
- Added optional `ILogger?` support plus fallback logger resolution from request services. When an alias fires, the filter logs Debug: `Argument alias resolved: '{Alias}' → '{Canonical}' for tool '{ToolName}'`.
- Build validation: `dotnet build --nologo` completed with 0 warnings and 0 errors.

Lambert handoff line table:

| File | Lines | Notes |
|---|---:|---|
| `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` | 1-4 | Added logging/DI/protocol imports. |
| `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` | 15-21 | Alias table and precedence comment. |
| `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` | 23-38 | Alias normalization folded into `AddBindingErrorFilter`. |
| `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` | 43-91 | Logger resolution and case-insensitive argument-key helpers. |
| `.squad/decisions/inbox/ripley-buildidorurl-impl-handoff-2026-06-01.md` | 1-29 | Lambert test handoff. |

## 2026-06-01: Completion — buildIdOrUrl alias implementation approved and tested

**Dallas verdict:** APPROVE WITH CHANGES (4 directives, all implemented)
**Lambert delivery:** 11 tests added, all 7 test cases covered. Full suite: 1312 pass, 2 pre-existing skip.
**Status:** Implementation merged to decisions.md. Orchestration logs created. Ready for team commit.

## 2026-06-01: Numeric `JsonElement` alias binding fix

- Copilot review caught a critical real-world gap: copying `build_id: 2989057` as a numeric `JsonElement` into canonical `buildIdOrUrl` still leaves the SDK binder unable to bind the value to the string parameter. Alias filters for string parameters must normalize both the key and the JSON value kind.
- Updated `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` so alias values are assigned through `CoerceToStringElement(...)`: string values are preserved; non-string JSON values use `GetRawText()` serialized as a JSON string element. This makes numeric telemetry bind as `"2989057"`.
- Replaced the alias `Dictionary` with an ordered tuple array because alias precedence is documented behavior (`build_id` > `buildId` > `buildUrl`) and should not rely on dictionary enumeration semantics.
- Validation: `dotnet build --nologo` completed with 0 warnings / 0 errors; `dotnet test --nologo --no-build` completed with 1312 passed, 2 skipped, 0 failed.

