## 2026-06-24: AzDO Param Plumbing — Three Bugs Fixed (fix/azdo-param-plumbing)

### Learnings

**AzDO REST query param names for time range:**
- `minTime` and `maxTime` (ISO 8601 round-trip format, URL-escaped)
- The time field filtered is **determined by queryOrder**, not by minTime/maxTime param names
  (e.g., `queryOrder=finishTimeDescending` → AzDO interprets minTime/maxTime against finish time)
- Valid queryOrder values: `queueTimeAscending`, `queueTimeDescending`, `startTimeAscending`, `startTimeDescending`, `finishTimeAscending`, `finishTimeDescending`

**Class of bug (silent param drop):**
- MCP param binding silently drops unknown args if not present in the tool method signature
- Missing param + missing URL plumbing both produce identical symptom: filter is ignored
- Audit: compare tool method signature with underlying REST API capabilities to catch gaps early

**Three bugs fixed and locations:**
1. `azdo_builds` — `minTime`/`maxTime`/`queryOrder` were absent from `AzdoBuildFilter`, not forwarded to AzDO URL, not exposed on MCP tool or CLI command
   - Files: `AzdoModels.cs`, `AzdoApiClient.cs` (`ListBuildsAsync`), `AzdoService.cs`, `CachingAzdoApiClient.cs`, `AzdoMcpTools.cs`, `Program.cs`
2. `azdo_test_attachments` — `top` param accepted but never forwarded to REST URL (`$top=` missing from `GetTestAttachmentsAsync`)
   - File: `AzdoApiClient.cs` (`GetTestAttachmentsAsync`)
3. `azdo_test_results` — `outcomes` filter hardcoded to `Failed` with no way for caller to override; passing `Passed,Failed` etc. was impossible
   - Files: `IAzdoApiClient.cs`, `AzdoApiClient.cs`, `CachingAzdoApiClient.cs`, `AzdoService.cs`, `AzdoMcpTools.cs`, `Program.cs`

**Pattern applied:**
- `NormalizeQueryOrder` + `IsValidQueryOrder` + `GetInvalidQueryOrderMessage` mirrors existing `NormalizeFilter`/`IsValidFilter` pattern
- `AllowedValues` on MCP tool param + server-side validator + `McpException` on invalid = defense in depth
- Cache key includes new discriminating params (outcomes, QueryOrder, MinTime, MaxTime) to avoid stale cache hits

**Commits:** `fefd0dc` (builds), `a2615df` (attachments top), `cbb35c5` (outcomes)  
**Tests:** 1326 passed, 2 skipped (0 failed) — 14 new tests added  
**Branch:** `fix/azdo-param-plumbing`

## 2026-06-24: PR #78 Copilot Reviewer Feedback — Whitespace normalization (fix/azdo-param-plumbing)

### Learnings

- **Optional string params with server-side defaults:** Always use `IsNullOrWhiteSpace` + `Trim()`, not `IsNullOrEmpty`. Empty or whitespace from a caller should fall back to the default, not produce malformed URLs (`outcomes=%20%20%20`) or distinct cache keys for semantically-identical requests.
- **Both CLI and MCP entry points must validate:** For tools with both CLI and MCP surfaces, normalize and validate at BOTH entry points using the shared helper (e.g., `AzdoService.NormalizeQueryOrder` / `IsValidQueryOrder`). Don't rely on one path to protect the other — a CLI user calling `--query-order " "` hits AzDO with a bad value if only the MCP path validates.
- **Cache key normalization:** In `CachingAzdoApiClient`, normalize once at the top of the method and use the normalized value for both the cache key and the inner-client call. Raw caller input (null vs "" vs "   ") must not produce distinct cache entries for semantically-identical requests.

**Commit:** `aa7dbe8` (whitespace normalization — queryOrder CLI, outcomes trim, caching outcomes)  
**Tests:** 1330 passed, 2 skipped (0 failed) — 4 new tests added  
**Branch:** `fix/azdo-param-plumbing`

## 2026-06-24: PR #78 Second Copilot Review — Cache normalization, exit codes, doc coupling (fix/azdo-param-plumbing)

### Learnings

- **Cache key normalization isn't just for outcomes — any optional param with a server-side default needs the same null-vs-default treatment in the cache layer.** Explicit `"queueTimeDescending"` and `null` are semantically identical (the server applies the same default), but produce different hash strings if you embed the raw value. Always normalize to `null` before hashing when the server would treat them as equivalent.
- **CLI commands MUST set non-zero exit code on invalid input or scripts can't detect failure.** `Environment.ExitCode = 1` before returning is the pattern used throughout this codebase for user input errors. Silent success-on-bad-input (`return` with exit 0) masks failures in CI pipelines and shell scripts.

---

## Summary (archived 17 detailed entries)

**Focus:** PR #78 (AzDO param plumbing & whitespace handling), Issue #81-82 (strict-mode parameter rejection), Issue #91-105 (SDK bumps, container image, HTTP 204 handling).

### Key Architectural Patterns Established

1. **Defense-in-depth for optional params:** Validate at user boundary (CLI/MCP) → Canonicalize at semantic boundary (cache key, URL) → Share algorithm across layers. Do NOT duplicate normalization logic.
2. **Silent param drop detection:** Audit tool method signature vs. REST API capabilities; missing params + missing URL plumbing produce identical symptom (filter ignored).
3. **Cache key stability:** Normalize null/whitespace/default values to identical representations before hashing. `null` and explicit `"queueTimeDescending"` are semantically identical and must share a cache key.
4. **Array safety:** Public validation sets must be `IReadOnlyList<T>` or `FrozenSet<T>`, not `readonly string[]` (readonly doesn't prevent element mutation).
5. **Alias correctness:** When renaming legacy params to canonical names, remove alias key from dict after promotion; without removal, strict-mode rejects the orphaned alias.
6. **Did-you-mean filter:** Levenshtein distance 6 (not 3) needed to catch hallucinated compound names like `minFinishTime` → `minTime` (distance 6).

### Recent Work Summary

- **PR #78:** Fixed 3 AzDO bugs (minTime/maxTime/queryOrder missing; outcomes hardcoded; top param ignored). 14 new tests added.
- **Issue #81 Stage A:** Added `result` → `resultFilter` alias; enabled `UnmappedMemberHandling.Disallow`; removed alias key after promotion.
- **Issue #81 Stage B:** Designed unknown-param filter with `RuntimeHelpers.GetUninitializedObject` schema extraction; Levenshtein threshold 6 validated for PR #78 regression.
- **Issue #82:** Centralized AzDO filter normalization pattern.
- **v0.8.0:** Released with strict-mode safety net + did-you-mean UX.
- **Issue #91+:** SDK bumps, WorkItemSummary fast-path, container image hardening, HTTP 204 handling.
- **2026-07-20:** Measured MCP schema token cost empirically (32.7 KB, ~8,175 tokens). Ground truth for Issue #74 reduction lever analysis.

### High-Value Test Files

- `src/HelixTool.Tests/Mcp/McpServerOptionsExtensionsTests.cs` — alias, strict-mode, unknown-param tests
- `src/HelixTool.Tests/AzDO/AzdoServiceNormalizationTests.cs` — param normalization, cache key stability
- `src/HelixTool.Tests/AzDO/PaginationContractTests.cs` — pagination spec validation (333 LOC, 13/13 passing)

### Current Focus

- **Decision Gate:** Awaiting user go/no-go on MCP schema Lever 1 (minimal outputSchema, ~8.9 KB / 31% savings).
- **Dependencies:** Dallas recommendation + Ripley half-day implementation + Lambert integration test if approved.
- **No blockers:** Measurement complete, recommendations documented in `decisions.md`.

---

## 2026-07-20: MCP Schema Token-Cost Measurement (READ-ONLY — no code changes)

---

## 2026-07-20: Dallas Tiered outputSchema Recommendation — NEXT IMPLEMENTER (Ripley)

**Status:** Refined recommendation ready for go/no-go.

**Prior:** Blanket flatten all 20 structured tools' outputSchema to `{"type":"object"}` for ~31% savings.

**Refined Decision:** TIERED approach (see `.squad/decisions/decisions.md`):
- **FLATTEN 10**: helix_status, azdo_build, helix_parse_uploaded_trx, azdo_search_timeline, helix_batch_status, helix_work_item, helix_search, helix_find_files, helix_files, helix_download → `{"type":"object"}`
- **KEEP 3**: azdo_timeline (log.id extraction), azdo_helix_jobs (HelixJobId extraction), azdo_build_analysis (known/unmatched discrimination)
- **LEAVE 12**: Already minimal or degenerate (68-byte LimitedResults wrappers, string-only tools)

**Net Savings:** ~5,450 bytes (18% vs. 28% blanket). Retains extraction-critical guidance on 3 chaining-junction tools.

**Measurement Baseline:** Current tools/list = 30,056 bytes authoritative (inputSchema 12,104; outputSchema 8,961; 20/25 structured).

**Implementation Order:**
1. Single PR: flatten 10 tools
2. Follow-up (optional): micro-enrich azdo_timeline.log.id and azdo_helix_jobs.HelixJobId (descriptions)
3. Leave 68-byte cluster unchanged

**Handoff:** See `.squad/log/2026-07-20T21:08:57Z-schema-keep-vs-flatten.md` for full context and `.squad/orchestration-log/2026-07-20T21:08:57Z-dallas.md` for lead notes.

## 2026-07-20: Dallas Progressive Disclosure Analysis — Config-Profiles Concept (Future Backlog)

**Decision:** Dynamic progressive disclosure NOT recommended. Static config-profiles model identified as practical alternative if further size reduction needed beyond flatten-10/keep-3.

**Concept:** Operator-selectable `--tools-profile` flag at startup:
- `minimal` (8 tools, CORE only)
- `azdo-only` (14 tools, CORE + WORKFLOW-GATED for AzDO)
- `full` (25 tools, current)

**Why later:** flatten-10/keep-3 (5.5 KB savings) is higher ROI and orthogonal. Profiles compose as second-stage lever if token pressure persists.

**Reference:** `.squad/decisions/decisions.md` entry "Progressive Disclosure for tools/list".

---

## 2026-07-28: helix_find_files workItem consistency fix

### Summary
Added `workItem` optional parameter to `helix_find_files` / `FindFilesAsync` to match sibling tools (`helix_files`, `helix_logs`, `helix_search`, etc.). A calling model passed `workItem` to `helix_find_files` and got a hard schema-rejection error (strict-mode did-you-mean → "Did you mean: maxItems?").

### Learnings

**Tool schema layout (Helix tools):**
- All work-item-scoped Helix tools (`helix_files`, `helix_logs`, `helix_search`, `helix_download`, `helix_work_item`, `helix_parse_uploaded_trx`) accept `workItem` as an optional second parameter after `jobId`.
- The URL extraction pattern (`HelixIdResolver.TryResolveJobAndWorkItem`) is replicated identically in each tool method before the service call.
- `helix_find_files` was the sole exception — it scans multiple work items by design, but callers naturally try `workItem` by analogy with siblings. The fix adds a fast-path: when `workItem` is supplied, skip `ListWorkItemsAsync` and call `ListWorkItemFilesAsync` directly on that one item.

**Service-layer signature change:**
- Added `string? workItem = null` as a new optional param between `progress` and `cancellationToken` in `FindFilesAsync`.
- `FindBinlogsAsync` (the only other internal caller) needed `cancellationToken: cancellationToken` (named arg) to avoid it landing on the new `workItem` slot — a compile-time catch, not a runtime one.

**Other schema inconsistencies found:**
- None in the Helix tool family. `helix_status` and `helix_batch_status` are intentionally job-scoped and do not need `workItem`. All other tools are now consistent.

**Files changed:**
- `src/HelixTool.Core/Helix/HelixService.cs` — `FindFilesAsync` + `FindBinlogsAsync` fix
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` — `FindFiles` MCP tool: added `workItem` param, URL extraction, updated description

## 2026-07-28 — helix_find_files workItem parameter
Implemented optional `workItem` parameter on FindFilesAsync service method and helix_find_files MCP tool. Fast path skips ListWorkItemsAsync when work item is named. Approved by Dallas. Shipped clean (0 errors/0 warnings).

## 2026-07-28: PR #117 Copilot reviewer fixes — predicate consistency, error context, changelog, skill

### Summary
Addressed four bugs/deficiencies raised by the Copilot PR reviewer on PR #117 (helix_find_files workItem param).

### Learnings

**Predicate mismatch is a layer-boundary bug class:**
The MCP layer used `IsNullOrEmpty` while the service used `IsNullOrWhiteSpace`. A caller passing `workItem: " "` (whitespace) would be treated as a scoped item by the URL-extraction guard (no extraction attempt) but as an absent item by the service (triggers multi-item scan). The `scannedItems` metadata in the response then said `1` while the service actually scanned all items. Rule: **pick one predicate at the semantic entry point and use it identically at every boundary that guards the same value in the same layer chain.** `IsNullOrWhiteSpace` is almost always correct for user-supplied optional strings; a whitespace-only name is never meaningful.

**Fast-path calls that bypass method-level error handlers inherit wrong error messages:**
`FindFilesAsync` wraps its inner `_api.ListWorkItemFilesAsync` calls with job-level 404 handlers ("Job 'X' not found"). The single-item fast path called `_api.ListWorkItemFilesAsync` directly, so a missing work item was reported as a missing job. Fix: call `GetWorkItemFilesAsync` (which has its own work-item-scoped 404 handler) so the error reads "Work item 'X' in job 'Y' not found." **When a method has a specialized sibling with the right error context, prefer the sibling over calling the raw API client.**

**Release notes must be scoped precisely:**
"Now all Helix tools that accept `jobId` also accept `workItem`" was false — `helix_status` and `helix_batch_status` accept `jobId` but are intentionally job-scoped. Qualified to "all work-item-scoped Helix tools." Lesson: changelog claims about a whole family must be verified against every member.

**Skill docs must describe the test that was actually built:**
I described a `[Theory]`/`[InlineData]` pattern; Lambert built a superior `[Fact]` with reflection-based discovery and an `intentionallyJobScopedTools` exclusion set. Always update authored skill docs immediately when implementation diverges — stale skills mislead the next contributor.

**Files changed:** `HelixMcpTools.cs`, `HelixService.cs`, `CHANGELOG.md`, `.squad/skills/mcp-sibling-schema-consistency/SKILL.md`
**Tests:** 1500 passed, 2 skipped (0 failed) — commit `6d95624`

## 2026-07-28: PR #117 Review Round — Comment Routing (lewing-fix-find-files-workitem-param)

### Task
Route 4 of 5 review comments on helix_find_files workItem parameter change.

### Fixes Applied
1. **Whitespace predicate unification:** `IsNullOrWhiteSpace` across MCP + service layers for optional strings
2. **Error context isolation:** `FindFilesAsync` fast path now calls `GetWorkItemFilesAsync` (not raw API) to preserve error context
3. **CHANGELOG correction:** Removed false claim about helix_status/helix_batch_status param surface
4. **SKILL.md update:** Replaced stale [Theory]/[InlineData] reference with shipped discovery-based [Fact]

**Commits:** 6d95624  
**Test outcome:** 1500/0 failed / 2 skipped  
**Branch:** lewing-fix-find-files-workitem-param

### Lesson: Skill Extraction Timing
**TEAM LESSON (cross-agent):** Skill was extracted mid-session and captured the INTENDED design. Subsequent discovery-based implementation (Lambert) replaced the referenced method with superior pattern, leaving skill pointing at code that never existed. Consider deferring skill extraction until after review completion to capture actual shipped behavior.

---

## 2026-08-20: C# MCP SDK Audit — v1.4.0 → v2.2.0 Migration Surface Map

### Summary
Conducted complete audit of ModelContextProtocol SDK usage across the helix.mcp repo to assess compatibility with the upcoming v2.0.0 major release (breaking changes) and stable v2.2.0 release. Goal was to identify compile/runtime risks, breaking changes affecting this repo, and a concrete implementation plan WITHOUT making code changes—this is purely a pre-update audit for later handoff.

### Key Findings

**Migration Complexity:** MEDIUM  
**Estimated Implementation:** 0.5–1 day (Ripley) + 0.5 day (Lambert for test validation)  

**Breaking Changes in v2.0.0 (10 major):**
1. **HTTP transport now stateless by default** — `HttpServerTransportOptions.Stateless` defaults to `true` (was `false`). No unsolicited server-to-client requests. Repo architecture already stateless; this is PREFERRED. No code changes needed.
2. **Non-object structured-content returns emit raw value** — e.g., `42` instead of `{ "result": 42 }`. **NOT BREAKING for this repo** — all tools return objects or plain strings; no wire-format change.
3. **Tool inputSchema now required on deserialization** — Protects against malformed payloads. **NOT BREAKING** — repo generates canonical schema.
4. **Deprecation warnings:** Tasks, Roots, Sampling, Logging APIs. **NOT BREAKING** — repo doesn't use these.
5. **Tasks API moved to extension package.** **NOT APPLICABLE** — repo doesn't use Tasks.
6. **OAuth validation strengthened.** **NOT BREAKING** — repo is server (MCP provider), not OAuth client.
7. **SSE exception propagation.** LOW RISK — error messages improve; no code changes.
8–10. **Minor changes:** PKCE validation, Legacy session cleanup, etc. **NOT AFFECTING.**

**Files Requiring NO Code Changes But Verification:**
- `Program.cs` — Verify stateless mode is desired behavior (it is). No changes needed.
- `McpServerOptionsExtensions.cs` — Custom filter chain API is stable. Test only.
- `AzdoMcpTools.cs`, `HelixMcpTools.cs` — Structured-content wrapping unchanged (all return objects). Test schema generation only.
- `McpProgressAdapter.cs` — ProgressNotificationValue API is stable. No changes.
- `CiKnowledgeResource.cs` — Resource API unchanged. No changes.

**Package Dependency Change:**
- `Directory.Packages.props`: Bump `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` from 1.4.0 → 2.2.0 (keep in sync).

**Test Coverage Impact:**
- 5 core test files require re-validation: `McpServerOptionsExtensionsTests.cs`, `AzdoMcpToolsTests.cs`, `HelixMcpToolsTests.cs`, `McpToolsListPayloadTests.cs`, `McpBindingErrorFilterTests.cs`.
- 1500+ existing tests to re-run (no test infrastructure changes expected).
- Strategy: `dotnet build` → `dotnet test` → manual schema inspection → smoke test with real client.

### Learnings

1. **Non-breaking server migration for simple use cases:** v1.4.0 → v2.0.0 is dramatic in scope but **non-breaking for servers that use only basic tool/resource patterns** (no Tasks, Roots, Sampling, custom session handling). This repo is a textbook example.

2. **Stateless servers are future-proof:** The stateless default in v2.0.0 aligns with modern cloud-native architecture (per-request scoping, no session state). Repo's design assumed this from the start; no defensive coding needed.

3. **Schema stability for object returns:** Structured-content wrapping changes only affect non-object types. Repo's all-objects-and-strings pattern means wire format is stable despite dramatic SDK changes.

4. **Filter chain resilience:** Custom implementations using `options.Filters.Request.CallToolFilters` are unaffected by breaking changes. Filter API is a stable extension point.

5. **Progress notifications are decoupled from core SDK changes:** The `ProgressNotificationValue` shape and adapter pattern remain unchanged. Existing progress instrumentation needs no updates.

6. **Protocol negotiation is transparent:** v2.0.0 adds `/server/discover` probe; `MapMcp()` handles this automatically. No explicit route registration required.

7. **Dependency lockstep is critical:** `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` MUST always be the same version. Minor version mismatch causes runtime errors.

### Recommendations

- **Test with real clients:** After migration, smoke-test with Claude Desktop and mcp-inspector to catch wire-format regressions.
- **Enable deprecation-as-error in CI:** Set `-Werror` for MCP SDK deprecation warnings so future deprecations are caught early.
- **Document stateless assumptions:** If repo later needs stateful server behavior (session tracking, unsolicited push), document why and set `Stateless = false` explicitly.

### Artifacts

**Decision document:** `.squad/decisions/inbox/ripley-csharp-mcp-sdk-update.md`  
**Migration stages:**
1. Update `Directory.Packages.props` (1–2 hours)
2. Compile + test (2–3 hours)
3. Verify + CI (1 hour)

**Ready for implementation handoff to Stage 1.**

---

## 2026-08-20: MCP SDK 1.4.0 → 2.2.0 Phase-1 Implementation

Implemented Dallas's accepted phase-1 plan (`.squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md`), instructions §9.

**Changes (2 files, surgical):**
- `Directory.Packages.props`: `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` 1.4.0 → 2.2.0, lockstep, no other package touched.
- `src/HelixTool.Mcp/Program.cs`: added `using ModelContextProtocol.AspNetCore;`; replaced bare `.WithHttpTransport()` with `.WithHttpTransport(options => { options.SessionMode = HttpServerSessionMode.Stateless; })` plus a 5-line comment (trimmed from Dallas's draft to fit this repo's comment density) explaining the flipped default, why our per-request-scoped DI needs no sessions, and naming the correct escape hatch (`StatefulForInitializeClients`, not `Stateless = false`).
- `src/HelixTool/Program.cs` (stdio host), tool classes, filters, progress adapter, resources: untouched, as scoped.

**Build:** `dotnet restore HelixTool.slnx` clean. `dotnet build HelixTool.slnx --no-restore`: **Build succeeded, 0 Warning(s), 0 Error(s).** No `MCP9xxx` diagnostic anywhere in the output — confirms Dallas's §1.5 stability matrix (no Roots/Sampling/Logging/Tasks/OAuth-client usage) held in practice, not just on paper. Only noise was the expected `NETSDK1057` preview-SDK notice (net10.0/dotnet 11 preview), unrelated to this change.

**Stdio smoke (G6):** No MCP Inspector/VS Code available in this environment, so I drove the stdio transport directly with a small Python harness (newline-delimited JSON-RPC over stdin/stdout to `dotnet HelixTool.dll mcp`) rather than fake the client-based gate. Sequence: `initialize` → response includes `serverInfo.version` with the build's informational version, confirming the 2.2.0 SDK is actually loaded → `notifications/initialized` → `tools/list` → returned real tool schemas (`helix_work_item`, ...) → `tools/call` on `azdo_auth_status` (chosen because its description states "No API call made," so the result is deterministic without live credentials) → got back a well-formed `structuredContent` result. All four legs succeeded; this satisfies "starts and responds" plus Dallas's fuller G6 bar (tools/list + a live tool call). HTTP smoke (G7) intentionally left for Lambert's TestHost coverage per task scope.

**No new decision needed** — the migration executed exactly as Dallas specified with zero surprises (no MCP9xxx, no compile break, no missing API). Nothing to escalate.

**Skill update:** Added §9 to `.squad/skills/mcp-sdk-major-upgrade/SKILL.md` — a reusable pattern for stdio smoke-testing an MCP server without a GUI client (raw JSON-RPC-over-stdio Python harness), plus the tip to pick a tool call whose description guarantees no live network/credential dependency for a deterministic gate.

---

## 2026-08-20: B1–B3 revision — SDK 2.2.0 structured-content wire fix

Dallas rejected Lambert's T2/T3 and the G4 explanation and assigned me B1–B3
(`.squad/decisions/inbox/dallas-csharp-mcp-sdk-review.md` §5). Full write-up:
`.squad/decisions/inbox/ripley-csharp-mcp-sdk-wire-fix.md`.

**The lesson that generalizes: verify the reviewer's severity claim, not just his mechanism.**
Dallas was right that the six `LimitedResults<T>` tools lost their `outputSchema`, and right
about why (custom `JsonConverter` ⇒ opaque to STJ's schema exporter ⇒ permissive `true` schema
⇒ SDK's `ShouldWrapValueForLegacyWire` classifies it as scalar-ish). He was wrong that a
"silent breaking wire change" was shipping. I stood up a real MCP server with a real `McpClient`
pinned to three protocol versions and measured: every pre-`2026-07-28` client was still getting
byte-identical 1.4.0 output. The 68→4 byte "regression" was measured on
`ProtocolTool.OutputSchema` — a property whose *meaning changed between SDK majors* (1.4.0
stored the already-wrapped legacy schema; 2.2.0 stores the natural one and defers wrapping to
the wire-emission sites, gated on `SupportsNaturalOutputSchemas(negotiatedVersion)`). It was
never a wire measurement in 2.2.0. The real defects were information-free schemas for new
clients plus one tool serving *two different `structuredContent` shapes* by client version. Fix
was still right; the framing was not, and I said so rather than shipping under a claim a
reviewer could disprove in ten minutes.

**Corollary I had to own: my fix introduces the only real legacy-facing change in the whole
migration.** Doing nothing would have preserved pre-`2026-07-28` clients exactly. Escalated
that inversion to Dallas explicitly (§6 of the findings) instead of burying it — it's a
convergence onto the shape `content[0].text`, the converter, and every existing test already
use, but it is still a change and he reasoned from the opposite premise.

**Smallest correct fix beat the cleanest fix.** The tempting option was deleting
`IReadOnlyList<T>` + the converter to make `LimitedResults<T>` a plain POCO — drift-free by
construction, −45 lines. Rejected: the list ergonomics are an explicitly-tested feature and the
brief forbade DTO refactoring. Shipped 8 lines instead: a schema-only mirror record +
`OutputSchemaType` on six attributes. **When a fix relies on a hand-maintained mirror of another
type, the mirror needs its own drift guard** — added `LimitedResultsSchemaContractTests` pinning
the record to the converter's actual `Write` output in both directions, since nothing in the
compiler ties them.

**Write the failing test against the broken tree first, and keep the receipts.** T2 pre-fix:
9 failed / 36 passed, naming all six tools individually plus the version-split case
(`("2025-06-18")` red, `("2026-07-28")` green — the divergence caught directly). Post-fix 45/45.
Saved to `.squad/evidence/`. Without that ordering I'd have had a green test and no proof it
could ever go red — which is exactly how Lambert's CLR-shape T2 green-lit the six broken tools:
it asserted the return type is non-scalar, which is *true* of `LimitedResults<T>`. The SDK never
looks at the CLR type.

**Mutation-test the guard, then report its real limit.** My first T3 behavioral test only
asserted `IsSuccessStatusCode` — it passed under `SessionMode = Stateful`, i.e. proved nothing.
Strengthened it to assert absence of the `Mcp-Session-Id` response header (the actual stateless
signature) and both tests then went red under `Stateful`. But deleting the production line
outright is *undetectable*, because SDK 2.2.0's own default is also `Stateless`. I documented
that in the test's doc comment rather than claim a stronger red→green than the code supports.
Overclaiming coverage is worse than a documented gap.

**"Explain the delta" means to the byte.** Lambert's G4 said 2.2.0 "generates a more compact
schema representation." False twice over: the 14 unaffected structured tools are byte-identical
across versions, and his own −1,281 was two independent effects merged into one wrong story. I
found the missing −897 by byte-diffing raw serialized `ProtocolTool` JSON between a
`git worktree add --detach` of the 1.4.0 commit and this tree: 23 of 25 tools each lost exactly
39 bytes = `,"execution":{"taskSupport":"optional"}`, which 1.4.0 emitted on every
`Task`-returning tool and 2.2.0 omits. The two exempt tools are the only two *synchronous* ones.
Final: 30,366 → 32,806 (+2,440) = +3,337 (six real schemas) − 897 (dropped `execution`), zero
residual. **When a byte-count delta doesn't decompose cleanly, diff the raw serialized objects
per item — don't reach for a plausible narrative.**

**Environment:** `DOTNET_ROLL_FORWARD=Major` is mandatory for every `dotnet test` here (only
`Microsoft.NETCore.App 11.0.0-preview.6.26351.102` installed, test host targets `net10.0`);
without it the run aborts with a runtime-not-found error that looks like a test failure.
`-l "console;verbosity=detailed"` needed to see `ITestOutputHelper` output.

**Validation:** targeted 68/68; build 0 warnings / 0 errors; full suite 1,556 total, 1,554
passed, 2 pre-existing skips, 0 failed; no `MCP9xxx`. Not committed.
