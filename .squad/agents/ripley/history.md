## [Archived] Older learnings (2026-05-21 to 2026-05-22)

– Learnings — v0.7.2 implementation notes (2026-05-21)
– Learnings — v0.7.2 release flow (2026-05-21 Ripley mechanical release)

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

## Learnings — DTO consolidation refactor 2026-05-22

- Safe consolidation pattern here was **centralize DTO definitions into `src/HelixTool.Mcp.Tools/McpToolResults.cs`, but keep distinct CLI vs MCP types when wire formats differ**. The CLI `--json` contracts still rely on a mixed PascalCase/camelCase shape, so direct reuse of MCP DTOs would have changed output.
- The low-risk move was to add public CLI DTOs in the shared results file and alias them back into `Program.cs`, then delete the nested `Program.cs` copies. That removed the parallel definitions without changing command logic.
- Wire-compat verification worked best as a two-layer check: full `dotnet test --nologo --no-build` for Lambert's existing JSON tests, plus explicit `--schema` spot-checks on `status`, `files`, and `work-item` to confirm property casing stayed exactly where expected.
- The surprising detail was that the "duplicate" classes were only structurally close, not identical: MCP status includes `helixUrl` and camelCase attributes, while CLI status intentionally omits that field and leaves several properties PascalCase.
See **history-archive.md** for detailed notes on:
- Exception patterns and safe/unsafe async patterns
- SDK upgrade decisions (MCP 1.0.0 → 1.3.0)
- MCP progress notifications & auto-injection patterns
- Parallel squad work with git worktrees
- dnceng feed format and Helix.Client version schemes
- Pagination standardization (Phase 1+2)
- RollForward policy configuration
- Earlier release flows and test suite baselines

## Learnings — Issue #59 quick wins 2026-05-22

- **Verified SDK 1.3.0 defaults from source/reflection, not guesswork:** `OpenWorld=true`, `ReadOnly=false`, `Idempotent=false`, `Destructive=true`, and `UseStructuredContent=false` are the `McpServerToolAttribute` defaults. That means `OpenWorld=true` is redundant noise, but `Destructive=false` is **not** — removing it would silently change semantics.
- **Structured output without outputSchema:** for tiny MCP responses where clients still benefit from `structuredContent`, the low-risk suppression pattern is `CallToolResult` + manual `Content`/`StructuredContent` population. Dropping `UseStructuredContent=true` removes `tools/list` `outputSchema`, while returning `CallToolResult` directly preserves the actual tool-call wire payload.
- **Issue #59 Phase 2A application:** `azdo_auth_status` and `helix_auth_status` were the safe wins. The already-primitive string tools (`azdo_log`, `helix_logs`, `helix_ci_guide`) already emitted no `outputSchema`, and one-property wrappers like `helix_download` would have needed a wire-shape change to trim further.
- **Measurement methodology:** the redirected stdio transport in this preview SDK did not emit capturable `stdout` payloads in this shell, so I measured the exact `ProtocolTool` JSON generated by `McpServerTool.Create(...)` in a file-based app against the built assembly. That matched the same SDK tool metadata path used by `hlx mcp` and was stable enough for before/after byte comparisons.
- **Measured win:** tool-list result JSON dropped from `27038` bytes → `26167` bytes. Breakdown: annotations cleanup `-462` bytes; auth-status outputSchema suppression `-409` bytes; total `-871` bytes (~200+ tokenizer tokens, rough).

## Learnings — Issue #61 Bug B exception centralization 2026-05-25

- Centralization pattern: MCP service calls now go through `McpExceptionHandler.RunServiceCallAsync` / `RunServiceCall`, a shared helper in `src/HelixTool.Mcp.Tools` that preserves deliberate `McpException`s and converts service-layer failures into actionable `McpException`s.
- Structured surfacing: the helper unwraps `AggregateException` from `Task.WhenAll`, catches `TaskCanceledException` / `OperationCanceledException`, wraps known exceptions as `Failed to {action}: {message}`, and uses `Unexpected error during {action}: {message}` as the fallback so MCP SDK 1.3.0 returns `isError` with text instead of a silent null.
- The old `catch when (...)` filters missed `AggregateException` and cancellation from AzDO/Helix `Task.WhenAll` paths. Covered production sites audited on 2026-05-25: AzDO `SearchBuildLogAcrossStepsAsync` and `GetBuildAnalysisAsync`; Helix `GetJobStatusAsync`, work-item detail/files fan-out, and batch status fan-out.
- AzDO not-found auth hints remain domain-specific via the helper's special-message callback; Helix extends the known-exception list with `HelixException` and `RestApiException`.
## Learnings — Issue #61 Bug A param standardization 2026-05-25

- Renamed MCP AzDO build-identifier parameters to `buildIdOrUrl` anywhere the primary input accepts either a numeric AzDO build ID or a full build URL: `azdo_build`, `azdo_timeline`, `azdo_log`, `azdo_changes`, `azdo_test_runs`, `azdo_test_results`, `azdo_artifacts`, `azdo_helix_jobs`, and `azdo_build_analysis`. Existing `azdo_search_log` and `azdo_search_timeline` were already correct.
- Wire compatibility choice: hard rename, no alias. This follows Dallas's clean schema guidance; no known downstream consumer requiring the old `buildId` MCP JSON key was found, and aliases would preserve parameter drift.
- Manual repro used local `hlx mcp` stdio: `tools/list` exposed `buildIdOrUrl` for `azdo_build` and `azdo_build_analysis`; `azdo_build` succeeded with a full dnceng-public URL using `buildIdOrUrl`; old `buildId` returned an explicit MCP `isError` response instead of silent null.
- Captured reusable pattern in `.squad/skills/mcp-param-rename/SKILL.md`: audit the full tool family, make an explicit hard-rename-vs-alias decision, and verify schema/new-call/old-call behavior manually.

---

## 2026-05-25: Issue #61 — Two PRs Merged (Bug A + Bug B)

**Session:** Issue #61 Silent MCP failures fix  
**Status:** Implementation Complete; both PRs merged ✅  
**Scope:** PR #62 (parameter standardization) + PR #64 (exception centralization)

### PR #62 — Standardize `buildIdOrUrl` Parameter (Bug A)

- Renamed `buildId` → `buildIdOrUrl` across 9 AzDO tools
- Low blast radius (parameter schema only)
- CI green; no regressions
- MERGED ✅

### PR #64 — Centralize MCP Exception Handling (Bug B)

- Extracted `McpExceptionHandler.WrapServiceException` helper
- Replaced 16 repetitive catch-when blocks
- Added TaskCanceledException and OperationCanceledException to known types
- CI green; no regressions
- MERGED ✅

### Key Calibration Learning

**Name an exception by exercising it, not by guessing from source-read.**

Ash's investigation identified the right fix (centralize exceptions) but incorrectly named the uncaught exception as "AggregateException from Task.WhenAll." In reality, `await Task.WhenAll` unwraps to the inner exception; only `.Wait()` throws AggregateException. The actual uncaught types were TaskCanceledException and OperationCanceledException (now fixed in PR #64).

**Better practice:**
1. Write 10-line repro
2. Run it: `catch (Exception ex) { Console.WriteLine(ex.GetType()); }`
3. Only then name the type in narrative

**This is critical for Task.WhenAll, Task.WhenAny, ConfigureAwait** — await machinery has non-obvious unwrapping behavior.

**Net impact:** Narrative correction (cosmetic); zero production risk.

### Issue #61 Closed — 3 PRs Merged

- PR #62 (yours): Parameter standardization ✅
- PR #64 (yours): Exception centralization ✅
- PR #63 (Lambert): Exception coverage audit + tests ✅

Both bugs fixed. Follow-up issue #65 tracks schema test, flatten exceptions, unskip tests, rolling coverage tests, preserve calibration lesson.


## Learnings — Issue #67 CallToolFilters middleware 2026-05-28

- SDK API confirmed on ModelContextProtocol 1.3.0: `McpServerOptions.Filters.Request.CallToolFilters` exists, and `CallToolFilters` can be appended inside the existing `.AddMcpServer(options => ...)` startup configuration. The companion builder API is `WithRequestFilters(...).AddCallToolFilter(...)`, but this change used the direct options path from Dallas's policy.
- The filter converts SDK parameter-binding `ArgumentException`s into `McpException` before the MCP server's generic formatter hides details. It covers binding failures before tool method bodies run; it does not replace `McpExceptionHandler` for runtime/service exceptions inside tool bodies.
- Double-wrap discipline: the implementation catches `ArgumentException` only when `ex.ParamName == "arguments"`, matching the SDK binder's parameter name from the #67 repro. That avoids relabeling ordinary tool-body `ArgumentException`s as parameter-binding errors while still surfacing the missing `jobId` failure.

## Learnings — PR #69 review-feedback iteration 2026-05-28

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
