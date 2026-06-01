## Learnings — Summary (archived earlier entries)

See history-archive.md for:
- MCP SDK 1.0.0 → 1.3.0 upgrade analysis
- Pagination standardization (Phase 1+2)
- DTO consolidation patterns
- Issue #59 quick wins (SDK defaults, structured output)
- Issue #61 Bug A+B (param rename, exception centralization)
- RollForward policy, release flows, earlier baselines

## Learnings — AzDO timeline filter presets (2026-05-22T13:03:40-05:00)

- Shared filter logic now lives on `AzdoService` as `public static` helpers because `HelixTool.Mcp.Tools` needs the exact same normalization/validation/predicate flow and `HelixTool.Core` only exposes internals to tests today.
- The clean MCP pattern is: keep `AllowedValues` canonical, run `NormalizeFilter(...)` before validation, and accept silent aliases (`inProgress`, `in-progress`, `active`, `notStarted`, `not-started`) without advertising them in schema.
- `azdo_helix_jobs` must relax its old issues-only gate for `running` / `pending` / `incomplete`; otherwise active Helix submission tasks disappear before issue text exposes a GUID. Returning `HelixJobId = ""` preserves the existing record shape while surfacing those state-based matches.
- Branch: `feat/azdo-timeline-filter-presets`. PR: #56.

## Team Update (2026-05-22)

**Lambert's PR #56 merged.** 97 unit tests for AzDO timeline filter presets (`running`, `pending`, `incomplete`, `issues`) and aliases (`inProgress`, `notStarted`, `in-progress`, `active`) now passing in main. Ripley's description tightening work on feat/mcp-description-tightening can proceed independently; no rebase required.

## Learnings — MCP description tightening pass (2026-05-22)

- For `[McpServerTool]` descriptions, follow the `mcp-server-design` rubric in `.squad/skills/mcp-filter-api-design/SKILL.md`: lead with a verb, stay around 20 words or less, and push defaults/filter enumerations down into parameter descriptions.
- Schema dumps and repo-specific/domain guidance belong in response content (`CiKnowledgeService` overview/profile text), not in always-loaded tool description metadata.
- The second-pass audit on 2026-05-22 showed description drift had already crept back in roughly three months after the prior tightening pass, so periodic re-audits are warranted.

## Team Update (2026-05-22 completion)

**PR #57 merged to main at 3c4728c.** Ripley completed description tightening on 8 tools (229 → 93 words, 136 recovered). Lambert fixed assertion coupling by routing devdiv knowledge verification to CiKnowledgeService response content. Dallas reviewed, approved, and merged; flagged two follow-ups: (a) establish quarterly description audit cadence, (b) restore azdo_builds→azdo_search_timeline cross-reference in future pass. Baseline decision recorded in decisions.md with full audit counts and pattern guidance for next drift check.

- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## Learnings — Issue #67 CallToolFilters middleware (2026-05-28)

- SDK API confirmed on ModelContextProtocol 1.3.0: `McpServerOptions.Filters.Request.CallToolFilters` exists, and `CallToolFilters` can be appended inside the existing `.AddMcpServer(options => ...)` startup configuration. The companion builder API is `WithRequestFilters(...).AddCallToolFilter(...)`, but this change used the direct options path from Dallas's policy.
- The filter converts SDK parameter-binding `ArgumentException`s into `McpException` before the MCP server's generic formatter hides details. It covers binding failures before tool method bodies run; it does not replace `McpExceptionHandler` for runtime/service exceptions inside tool bodies.
- Double-wrap discipline: the implementation catches `ArgumentException` only when `ex.ParamName == "arguments"`, matching the SDK binder's parameter name from the #67 repro. That avoids relabeling ordinary tool-body `ArgumentException`s as parameter-binding errors while still surfacing the missing `jobId` failure.

## Learnings — PR #69 review-feedback iteration (2026-05-28)

- Helper extraction pattern: cross-cutting MCP request filters now live as extension methods on `McpServerOptions` in `src/HelixTool.Mcp.Tools`, so stdio and HTTP startup paths each keep a one-line `options.AddBindingErrorFilter()` call.
- Binder param-name rationale: the SDK binding failure is identified by `ArgumentException.ParamName == "arguments"`; `Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.InvokeCoreAsync` constructs that exception when binder validation fails, and Ash verified the exact param name in stderr during the 2026-05-28 investigation.
- Filter middleware test pattern: instantiate `McpServerOptions`, call the extension, grab the single `CallToolFilters` delegate, wrap a fake `McpRequestHandler<CallToolRequestParams, CallToolResult>`, and invoke it with a minimal `RequestContext<CallToolRequestParams>` using an NSubstitute `McpServer` and `JsonRpcRequest`.

## 2026-05-28: PR #69 — CallToolFilters Middleware for MCP Parameter Binding Errors

- Implemented AddBindingErrorFilter() in new src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs
- Integrated filter registration in both src/HelixTool/Program.cs and src/HelixTool.Mcp/Program.cs
- Added 2 unit tests for filter behavior (ArgumentException detection, McpException conversion, message preservation)
- Resolves Issue #67 Class A silent failures (all 25 tools automatically protected)
- PR #69 shipped and merged; v0.7.5 release candidate pending

**Next steps:** Per-tool validation prologues deferred pending filter feedback (only for combo-rules, narrow scope)

## Learnings — v0.7.5 release flow (2026-05-28 Ripley mechanical release)

**Release execution summary:**
- Synced main branch: commit 5c7852e (via `git pull --ff-only`).
- Bumped three version stamps: `src/HelixTool/HelixTool.csproj` (line 12) + `src/HelixTool/.mcp/server.json` (top-level "version" + packages[0].version) — all 0.7.4 → 0.7.5.
- Build: 0 errors, 0 warnings (9.52s).
- Tests: 1298 passed, 0 failed, 2 skipped (3s).
- Release commit: c801bb5 (`release: v0.7.5`).
- Tag: `v0.7.5` pushed to origin.
- Publish workflow: triggered on tag push, run 26599303495, status in_progress.
- Workflow URL: https://github.com/lewing/helix.mcp/actions/runs/26599303495

**Shipped PRs in v0.7.5:**
- PR #66 (akoeplinger): fix(helix): waiting work items must not be counted as failed — adds IsCompleted/InProgress bucketing.
- PR #68 (Lambert): audit: MCP tool required-param schema clarity (#67 supporting work) — improves [Description] attributes, adds reflection coverage test.
- PR #69 (Ripley): fix: surface MCP parameter-binding errors via CallToolFilters (#67) — adds AddBindingErrorFilter() middleware to surface previously-stripped binding error messages.

**Issue closed:** #67 (by PRs #68/#69 in combination).

## Learnings — Issue #70 GetWorkItemDetail IsCompleted bucketing (2026-05-29)

- Applied PR #66's Helix work-item completion pattern to the single-item detail path: `details.ExitCode.HasValue` is the completion signal, `-1` remains only a sentinel, and `FailureCategory` is assigned only for completed non-zero exits.
- `WorkItemDetail` now mirrors `WorkItemResult` with `bool IsCompleted = true` for compatibility, and both CLI JSON/human output plus MCP `helix_work_item` structured content expose completion state.
- Added focused coverage for a waiting work item so incomplete details return `IsCompleted=false`, `ExitCode=-1`, and no failure category.

## Learnings — PR #71 wire-compat result-wrapper defaults (2026-05-29)

- When adding new bool fields to Helix DTOs for wire compatibility, mirror the non-breaking default on every serialized wrapper too: source DTOs (`WorkItemDetail`, `WorkItemResult`) plus CLI/MCP result wrappers (`CliWorkItemJsonResult`, `WorkItemToolResult`). Missing the wrapper default makes older JSON payloads deserialize absent fields as `false` and flips completed work items to incomplete.

## Learnings — User-Agent identifier (2026-05-29T20:12:39-05:00)

- Outbound tool-owned `HttpClient` traffic now uses `HelixToolUserAgent.Apply(HttpClient)` to add `User-Agent: helix.mcp/{version}` plus `X-Helix-Mcp-Tool: helix.mcp` on the AzDO and Helix download named clients.
- The Helix SDK exposes an `Azure.Core.ClientOptions.AddPolicy(...)` hook through `HelixApiOptions`, so SDK calls can carry the same UA and tool header via a per-call pipeline policy instead of relying on the SDK default UA alone.

## Learnings — v0.7.6 release flow (2026-05-29 Ripley mechanical release)

**Release execution summary:**
- Synced main branch: commit 0ee744d (via `git pull --ff-only`).
- Bumped three version stamps: `src/HelixTool/HelixTool.csproj` (line 12) + `src/HelixTool/.mcp/server.json` (top-level "version" + packages[0].version) — all 0.7.5 → 0.7.6.
- Build: 0 errors, 0 warnings (9.70s).
- Tests: 1300 passed, 0 failed, 2 skipped (3s).
- Release commit: 0bc0095 (`release: v0.7.6`).
- Tag: `v0.7.6` pushed to origin.

**Shipped PRs in v0.7.6:**
- PR #73 (akoeplinger): User-Agent identifier + X-Helix-Mcp-Tool custom header on AzDO HttpClient, Helix download HttpClient, and Helix SDK pipeline (via HelixApiOptions.AddPolicy) — lets arcade-services distinguish hlx traffic.
- PR #71 (backport of #70): Apply IsCompleted bucketing to GetWorkItemDetailAsync so waiting/in-progress work items aren't miscounted as failed.

## Status — v0.7.6 release shipped (2026-05-29T20:34:23-05:00)

Release v0.7.6 shipped successfully to NuGet and GitHub Releases. Decision merged to `.squad/decisions.md`. Orchestration logged. Cross-agent update by Scribe.

## 2026-05-30T11:48:09-05:00: Tool rename freedom validated (helix_status → helix_workitems)

**Validation scope:** Cross-check dotnet org for hard-coded tool name references (cypher research).
**Finding:** Zero code-level pinning of `helix_*` tool names. Semantic connections only.
**Decision:** Rename `helix_status` → `helix_workitems` is safe. No alias needed.
**PR scope:** Tiny discoverability rename. Expected landing in next cycle.

## 2026-06-01T12:37:55-05:00: MCP Schema Trimming — v0.7.8 candidate (decision pending)

**Input from Ash (Analyst):** GitHub agentic token-efficiency study identified MCP tool schemas as #1 inefficiency. helix.mcp's 25-tool schema footprint is 15.83 KB (upper end of measured range).

**Measurement:** Top 5 cost drivers (helix_search, azdo_search_log, azdo_builds, helix_download, azdo_search_timeline) account for 60% of schema cost. Conservative trim opportunity: −1 KB (tool description tightening only; no API changes).

**Decision timeline:** Squad PM (Dallas) to decide on measure-in-live-workflows vs. proceed-with-conservative-trim. If approved, conservative trim is planned for v0.7.8 post-helix_status→helix_workitems rename (v0.7.7 candidate).

**Tracking:** GitHub issue #74. Full analysis in .squad/decisions/ash-mcp-schema-measurement-2026-06-01.md.

**Status:** Awaiting squad decision. No action for Ripley until approval.

## 2026-06-01T14:02:01-05:00: Issue #74 Ground-Truth tools/list Measurement — COMPLETE

**Serialization path:** `McpServerTool.Create(MethodInfo, RuntimeHelpers.GetUninitializedObject(type), null)` → `mcpTool.ProtocolTool` → `JsonSerializer.Serialize(proto, McpJsonUtilities.DefaultOptions)` (compact JSON, UTF-8 byte count). This is the canonical wire path from `.squad/skills/mcp-wire-format-trim/SKILL.md`.

**Key files:**
- Measurement test: `src/HelixTool.Tests/McpToolsListPayloadTests.cs`
- Results inbox: `.squad/decisions/inbox/ripley-issue74-toolslist-bytes.md`

**Real numbers (measured, not estimated):**

| Metric | Bytes | KB |
|---|---|---|
| tools/list full payload | **28,941** | **28.26** |
| inputSchema only | 11,068 | 10.81 |
| outputSchema only | 8,882 | 8.67 |
| input + output combined | 19,950 | 19.48 |

- 25 tools total, 20/25 have outputSchema (`UseStructuredContent=true`)
- Ash's inputSchema-only static estimate was accurate (11,317 vs. 11,068, within 2%)
- Issue #74 heuristic was wrong: 16,212 bytes (understated by 44% once outputSchema counted)
- Ash's range estimate of 15–25 KB was itself understated — reality is 28.26 KB

**Fattest tools (full JSON):** azdo_timeline (2,099), azdo_search_log (2,027), azdo_search_timeline (1,929), helix_parse_uploaded_trx (1,703), helix_status (1,692)

**outputSchema top contributors:** azdo_timeline (1,123), helix_status (1,001), azdo_build (929) — biggest trim opportunity via Pattern 2 (return CallToolResult directly, drop advertised outputSchema)

**Test count:** 1301 passed, 0 failed after adding measurement test (was 1300).

## 2026-06-01T19:01:23Z: Issue #74 Ground-Truth Schema Measurement — Incoming Task (Ripley)

**From Ash (Analyst) via Scribe:**
Ash's analysis revealed critical gap: issue #74 excluded `outputSchema` (20/25 tools use `UseStructuredContent=true`). Real `tools/list` payload likely 15–25 KB, not the measured 11.3 KB inputSchema alone.

**Incoming task for Ripley:** Run ground-truth `tools/list` measurement using `McpServerTool.Create()` + `ProtocolTool` serialization per `.squad/skills/mcp-wire-format-trim/SKILL.md` "Measurement" section. Report total byte count (including outputSchema) before any trim work is authorized. Estimated effort: 1–2 hours.

**Decision depends on this:** If real > 15 KB AND called per-turn (not cached), Dallas may approve conservative trim (−1 KB). Otherwise, defer.

## 2026-06-01: AzDO buildIdOrUrl alias investigation

- Surface confirmed: 11 AzDO MCP tools in `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` require `buildIdOrUrl`: `azdo_build`, `azdo_timeline`, `azdo_log`, `azdo_changes`, `azdo_test_runs`, `azdo_test_results`, `azdo_artifacts`, `azdo_search_log`, `azdo_search_timeline`, `azdo_helix_jobs`, and `azdo_build_analysis`.
- `AzdoService.NormalizeFilter(...)` is a value-normalizing helper after binding. It cannot fix `buildUrl` / `build_id` because the MCP/AI binder rejects missing required parameter names before tool method bodies run.
- ModelContextProtocol 1.3.0 exposes no method/parameter alias property on `McpServerToolAttribute` or `McpServerToolCreateOptions`; binding is keyed by the method parameter name. `CallToolRequestParams.Arguments` is mutable, and existing `CallToolFilters` run before the SDK invokes/binds the tool, so an inbound-argument normalization filter is the right hook.
- Recommendation proposed: add one generic CallToolFilter mapping aliases such as `build_id`, `buildId`, and `buildUrl` to canonical `buildIdOrUrl` when the canonical key is absent. This keeps schema bytes flat, aligns with wire-format flexibility, covers future aliases centrally, and should ride v0.7.7 as a compatibility/discoverability bugfix rather than wait for v0.7.8 schema-trim work.

## 2026-06-01T19:14:56Z: Dallas Verdict on Issue #74 (CONDITIONAL NO)

**Status:** Finalized and merged into decisions.md by Scribe.

**Key decision:** CONDITIONAL NO on active schema trimming. At 28.26 KB cold-load cached per-session (not per-turn), the payload is <1% of typical session token budget. Trimming solves a problem we don't have today.

**No trim implementation assigned.** Ripley's measurement validates Ash's framework; buildIdOrUrl alias proposal (option b) approved separately as v0.7.7 compatibility fix (no trim scope).

**Revisit triggers:** (1) consumer re-fetches per-turn, (2) tool count >40, (3) token budget pressure from real workflows.

**Best available lever when needed:** Pattern 2 (selective outputSchema removal via SKILL.md), saving 4.5–8.9 KB (targets: azdo_timeline 1,123 B, helix_status 1,001 B, azdo_build 929 B, azdo_search_log 800 B, helix_parse_uploaded_trx 656 B) with no breaking change.

Measurement complete. Awaiting buildIdOrUrl implementation scheduling for v0.7.7.
