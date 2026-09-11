# Ripley — History

## Executive Summary

**Role:** Backend development, performance optimization, audit and verification work.

**Current Focus:** PR #132 implementation is complete, final internal review is approved, and external review is in progress.

---

## 2026-06-01 Through 2026-08-31: Summary

Completed multiple phases of helix.mcp backend work:
- **June–July:** Caching infrastructure, offline test fixtures, eval-mode PoC for snapshot replay (9,000+ lines investigated; architecture validated but tests blocked on env)
- **July–August:** Helix SDK feasibility, constraint analysis on fixture boundaries, verified decorator pattern for cache+offline isolation
- **August:** Deployed multiple incremental improvements; all PRs merged; tests stable

Key architectural patterns established: Reuse existing decorators (CachingAzdoApiClient, CachingHelixApiClient); mock at interface boundary (IHelixApiClient, not HttpMessageHandler); early-return pattern for eval mode pathways.

---

## Recent Detailed Work

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

## 2026-09-11 — Startup cache-eviction lifecycle production implementation & reaffirmation (#129)

**Production Implementation (R1):** Delivered exact implementation of Dallas's accepted design: async startup maintenance with retained handle (`internal Task StartupMaintenance`), construction-time cutoff pinning, strict disposal ordering (cancel → bounded join with `DisposeJoinTimeout=10s` → narrow exception absorption → CTS disposal → ClearAllPools last), and cancellation checkpoints throughout. Production diff is exactly `SqliteCacheStore.cs` (90 insertions, 10 deletions); no API surface changes, no test files touched, no changes to `ICacheStore`/`ICacheStoreFactory`/`EvalModeServices`/`Program.cs`. Build 0 W/0 E; targeted tests 52/52 pass (pre-Lambert); full suite 1981 pass/8 skip/0 fail. All 9 reject-on-sight conditions pass. Implementation note: `AggregateException.Handle` is the correct narrow-catch shape for Task.Wait timeout/fault semantics (not a broadening).

**Fault-Propagation Reaffirmation:** Verified two .NET Task behaviors empirically (AggregateException wrapping, fault observability through ContinueWith). Confirmed existing Dispose() implementation already satisfies the fault-propagation contract: unexpected exceptions still propagate, Dispose() does not launder non-cancellation faults into silence. Production diff unchanged (same 90 insertions, 10 deletions as R1). Targeted tests 60/60 pass; full suite 1989 pass/8 skip/0 fail. Incidental finding (not mine to fix, Lambert already resolved): SnapshotEvalTestHarness BackupDatabase copies WAL mode header flag; test fix was forcing journal_mode=DELETE on seeded copy before baseline assertion.

**Decisions:** `ripley-startup-cache-eviction-implementation.md` (R1 delivery), `ripley-startup-cache-eviction-fault-reaffirmation.md` (fault-propagation verification).

**Orchestration logs:** `.squad/orchestration-log/2026-09-11-1338-ripley-startup-cache-implementation.md`, `.squad/orchestration-log/2026-09-11-1343-ripley-fault-propagation-reaffirmation.md`

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

## 2026-08-20: Minimal `{"type":"object"}` outputSchema for the six `LimitedResults<T>` tools

Larry directed a revision of the same PR: my B1 fix bought envelope stability by declaring a
full `LimitedResultsSchema<T>` property mirror, and that cost **+3,745 B** of `tools/list` fixed
context across six tools. Replaced all six with one shared, deliberately empty marker —
`public sealed record MinimalObjectSchema;` in `HelixTool.Mcp.Tools` — declared the same
documented way (`OutputSchemaType = typeof(MinimalObjectSchema)`).

**The SDK only asks one question, so only answer that one.** `ShouldWrapValueForLegacyWire`
tests whether the schema's `type` is `object` (or `["object","null"]`). It does not read
`properties`, `required`, or anything else. Every byte beyond `{"type":"object"}` is therefore
paid on every session by every client and buys *nothing* from the SDK — it only buys
documentation, in the one place documentation is most expensive and least enforceable. A
hand-written property mirror of a hand-written converter has no compiler tie either way, so the
"contract" it advertised was maintained by convention only. Deleting it removed both the rent and
the drift surface; the payload shape stays documented in the tool description and in the returned
JSON, which are authoritative.

**Probe the exporter, don't reason about it.** I stood up a throwaway tool host declaring
`OutputSchemaType` against seven candidate types and printed the generated schema for each:

| Marker type | Generated schema | Usable |
|---|---|---|
| empty `record` / empty `class` | `{"type":"object"}` (17 B) | ✅ |
| `JsonObject`, `Dictionary<string,JsonElement>`, `Dictionary<string,object>` | `{"type":"object"}` (17 B) | ✅ |
| `typeof(object)` | `true` (4 B) | ❌ non-object ⇒ legacy wrap |
| `typeof(JsonElement)` | `true` (4 B) | ❌ same |

**The intuitive choice is the wrong one.** `typeof(object)` is what you reach for when you want
"just an object", and it produces the exact schema that reintroduces the bug — 4 bytes that split
the wire contract by protocol version. An empty user-defined type is 13 bytes more expensive and
correct. Cheapest ≠ smallest schema; cheapest is *smallest schema that is still object-typed*.

`McpServerToolCreateOptions.OutputSchema` accepts an explicit `JsonElement` and would give exact
byte control, but it is not reachable from the attribute — using it means abandoning
`WithTools<T>()` attribute registration for hand-built `McpServerTool.Create(...)` calls for six
tools. Rejected: much larger production change for a 0-byte gain. Post-registration schema
mutation rejected outright per the brief.

**The empty type's emptiness is load-bearing, so pin it.** Any member added to
`MinimalObjectSchema` silently inflates all six tools. `LimitedResultsOutputSchemaTests` asserts
(a) exactly six tools are discovered, (b) all six declare `typeof(MinimalObjectSchema)`, (c) each
advertises byte-exact `{"type":"object"}`, (d) the marker has no serializable members. The
version-parameterized wire theory in `StructuredContentReturnTypeTests` now loops all six rather
than sampling `azdo_builds`, since one shared type means a drift in any one is a shared-contract
drift.

**Measured wire, both versions, literal output** (throwaway probe over real Streamable-HTTP,
deleted after):

```
WIRE[2026-07-28] outputSchema={"type":"object"}  structuredContent={"results":[…],"truncated":false}  hasResultWrapper=False
WIRE[2025-06-18] outputSchema={"type":"object"}  structuredContent={"results":[…],"truncated":false}  hasResultWrapper=False
```

Identical at both — which is the point: `BuildLegacyWireProtocolTool` rewrites only non-object
schemas, so schema pass-through *is* the observable proof that no payload wrap happened.

**Byte ledger** (25 tools, `ProtocolTool` + `McpJsonUtilities.DefaultOptions`, UTF-8):

| State | `tools/list` | six schemas | per tool |
|---|---|---|---|
| main (SDK 1.4.0, 6eb3905) | 30,366 | 408 | 68 (`{"type":"object","properties":{"result":true},"required":["result"]}`) |
| pre-revision PR (mirror) | 32,806 | 3,745 | 1319/422/653/476/454/421 |
| revised PR (marker) | **29,163** | **102** | **17** |

Revised is **−1,203 B (−3.96%, ≈−301 tokens) below main** — the first time this PR is net-cheaper
than the baseline it replaces — and −3,643 B (−11.10%, ≈−911 tokens) below my own prior revision.

**main was already wrapping.** Worth recording because it reframes the "breaking change" question:
main's six schemas are `{"type":"object","properties":{"result":true},"required":["result"]}`,
i.e. SDK 1.4.0 wrapped eagerly at creation time and shipped `{"result": …}` `structuredContent` to
*every* client. So this PR does change the payload for existing clients — from uniformly wrapped
to uniformly natural — it just changes it once, consistently, instead of splitting it by version.
That is a real migration note, not a no-op, and it should be stated plainly rather than implied by
a byte table.

**Validation:** build 0 warnings / 0 errors (Debug + Release); targeted 81/81; full suite
1,568 passed / 2 pre-existing skips / 0 failed, identical with `HLX_API_KEY` unset and set; no
`MCP9xxx`. `DOTNET_ROLL_FORWARD=Major` still mandatory on every `dotnet test`. Not committed.

---

## 2026-08-26 — Snapshot eval mode analysis

Validated Larry's hypothesis against actual code. Key findings durable across future work:

- `cache_artifacts.file_path` is stored **relative** to `_artifactsDir` — the DB+artifacts dir is self-contained and portable.
- All `cache_metadata` reads check `expires_at > @now`. At 4h TTL for completed builds, a snapshot older than 4h causes 100% metadata misses → network fallthrough. This is the **primary blocker** for naive snapshot replay.
- The `log-fresh:{buildId}:{logId}` key has 15s TTL — always expired in snapshots → delta-refresh logic fires on every `GetBuildLogAsync` → hits network.
- `ListJobNamesByBuildAsync` is deliberately not cached (uncacheable by design per code comment).
- Auth-partitioned AzDO cache keys (`azdo:{authHash}:{org}:{project}:...`) are portable for public/anonymous snapshots (no hash prefix). Auth'd snapshots need matching identity or key rewrite.
- WAL checkpoint required before snapshot copy (`PRAGMA wal_checkpoint(FULL)`).
- Minimum code change: `EvalMode` flag on `CacheOptions`, TTL bypass in `GetMetadataAsync`/`IsJobCompletedAsync`, `OfflineAzdoApiClient`/`OfflineHelixApiClient` stubs, `ExportSnapshotAsync` on `SqliteCacheStore`, env var activation (`HLX_EVAL_SNAPSHOT`). No schema changes, no new file formats.
- Decision proposal written to `.squad/decisions/inbox/ripley-snapshot-eval.md`.

---

## 2026-09-04 — Helix queue-monitor audit

Read-only audit requested by Larry: what does helix.mcp do worse for repos using arcade's
"Helix Queue Monitor" pattern (a single job watching all Helix jobs for a build, separate from
the legs that submit them)? Validated against public pipelines, not just arcade source. No code
changed. Full proposal in `.squad/decisions/inbox/ripley-helix-queue-monitor.md`.

Durable findings:

- Confirmed live adoption via GitHub code search, not just the arcade template: dotnet/sdk
  (`.vsts-ci.yml`/`.vsts-pr.yml` — its *main* pipelines), dotnet/runtime (45+, mostly stress
  suites), dotnet/aspnetcore, dotnet/roslyn, mirrored into dotnet/dotnet (VMR). This is not a
  niche pattern — it's SDK's primary CI shape today.
- Good news: `HelixJobSource.Compute` in arcade's `JobMonitor` is identical to our own
  `AzdoService.ComputeHelixSource`. Our Helix-side primary path (`Job.ListAsync(source)+BuildId`,
  PR #92) is exactly aligned with how the monitor itself finds jobs — discovery already works.
- Bad news, in priority order:
  1. `HelixApiClient.ListJobNamesByBuildAsync` discards `QueueId`/`Properties` from the same
     `Job.ListAsync` response it already paid for. `System.PhaseName`/`System.JobDisplayName` are
     sitting right there and would restore `ParentJobName` (currently hard-coded `""` in
     `BuildHelixResultFromJobNames`) at zero extra API cost. Cheapest, safest fix — proposed P1.
  2. Timeline-fallback regexes (`FailedWorkItemRegex`) assume the old "has failed" + raw-GUID
     format; the monitor emits `"failed (State)."` + human `DisplayName`, so `FailedWorkItems`
     silently stays empty for monitor repos even when the fallback does run.
  3. Pre-existing bug, now more consequential: when a timeline task `hasIssues` but yields zero
     parseable job GUIDs and `filter="failed"` (the default), `GetHelixJobsViaTimelineAsync` emits
     *no row at all* — build-wide monitor errors like "No Helix jobs were submitted..." vanish
     instead of surfacing.
  4. The monitor has its own internal retry/resubmission loop (`JobMonitorRunner.ExecuteRetryPassAsync`)
     that resubmits work as new Helix jobs sharing the same `BuildId`, disambiguated only by
     `System.JobAttempt`/`System.StageAttempt` properties (arcade dedups via
     `MonitorState.GetLatestHelixJobAttempts`). Our `(source, BuildId)` filter has no attempt
     concept and can double-count/point at superseded job GUIDs. This is the one item with real
     schema/behavior weight — flagged for Dallas, not something to just fix inline.
  5. `CiKnowledgeService.cs` (lines 230/240/787) still tells the LLM that `azdo_helix_jobs` returns
     0 for SDK due to task-name substring miss — that predates PR #92 and needs re-verification
     against a live SDK build before trusting it; flagged for Kane if stale.
- Confirmed dotnet/sdk's actual `.vsts-ci.yml`: a single `HelixJobMonitor` job runs in the `build`
  stage next to (not gated by) the WINDOWS/LINUX/MACOS matrix legs — the exact "separate from
  spawning legs" shape the task described, and likely the root cause behind the existing SDK
  guidance in CiKnowledgeService.
- Left unresolved (non-blocking, noted in proposal): did not verify arcade's
  `TestResultUploadPipeline.cs` test-run naming convention to confirm/correct the `[HelixJob:GUID]`
  guidance already in `CiKnowledgeService.cs` — worth a follow-up if anyone touches that doc.

## 2026-09-04: Helix queue-monitor local implementation audit (completed)

Completed audit of helix.mcp's Helix client integration against arcade's queue-monitor implementation. Identified five concrete defects (regex mismatch, job name collapse, build-wide error drops, metadata discard, stale guidance) and three architectural options. Dallas accepted all defects as genuine; produced corrected P4 dedup algorithm (lineage-leaf rule, not group-by-attempt). Corrected form splits D5a (expose lineage now) and D5b (filter superseded, gated on evidence). All approved items assigned to Ripley ownership.

**Status:** COMPLETED  
**Outcome:** Five defects confirmed; implementation roadmap staged with D1–D6 approved

## 2026-09-04: R4 shim deletion + Source-threading fix (completed)

Executed the final merge gate from `dallas-queue-monitor-design-review.md`: deleted
`ListJobNamesByBuildAsync` from all four sites (`IHelixApiClient.cs`, `HelixApiClient.cs`,
`OfflineHelixApiClient.cs`, `CachingHelixApiClient.cs`) after confirming via Lambert's
migration-gate message and a tree-wide grep that no production or test caller referenced it.
While verifying against §8's targeted test filter, found and fixed a genuine gap (not a test
bug): `GetHelixJobsAsync` computed the Helix `source` string for its primary attempt but
discarded it when falling through to `GetHelixJobsViaTimelineAsync` on 0 results, so the
timeline-fallback result always reported `Source: null`. Threaded `source` through as an
optional parameter so it survives the fallback when it was actually computed, staying null
(and omitted from the wire result) only when genuinely unknown. Also observed intermittent
NSubstitute `ThreadLocalContext` failures in Lambert's `GetHelixJobsOrchestrationTests` before
this fix (nested substitute built inside another substitute's `.Returns()` argument — a test
mock-setup anti-pattern, not production code); flagged to her via
`.squad/decisions/inbox/ripley-r4-shim-deleted.md` rather than touching the test file myself.

**Status:** COMPLETED
**Outcome:** R4 gate satisfied (shim fully removed, zero references remain); `Source` now
correctly populated on both Helix-path and timeline-fallback results; build 0/0, targeted
suite 117/117 ×3, full suite 1943/0/8-skipped. No test files opened or edited.

## 2026-09-11: Startup cache-eviction lifecycle implementation (#129, R1) (completed)

Implemented Dallas's accepted design exactly, in `SqliteCacheStore.cs` only:

- Constructor now retains `_maintenanceCts` (`CancellationTokenSource`) and an `internal Task
  StartupMaintenance` (never null — eval mode assigns `Task.CompletedTask` and starts no
  background work; normal mode captures `DateTimeOffset.UtcNow` at construction and passes it
  into `Task.Run(() => EvictExpiredAsync(asOf, _maintenanceCts.Token))`).
- Split `EvictExpiredAsync` into the unchanged public `(CancellationToken ct = default)` overload
  (still evaluates `UtcNow` at call time) delegating to a new private `(DateTimeOffset asOf,
  CancellationToken ct)` overload used by both the public API and the startup pass. This is what
  pins the startup cutoff and keeps `NormalMode_EvictExpired_RemovesExpiredRows` semantics intact.
- Added `ct.ThrowIfCancellationRequested()` checkpoints before each `DELETE`, before the artifact
  `SELECT`, and gave `DeleteArtifactRows` a `CancellationToken` parameter checked once per loop
  iteration (both call sites — expiry and LRU-cap — updated).
- `Dispose()` rewritten to the exact ordering the brief specifies: `Interlocked.Exchange` on a new
  `_disposed` field for idempotency → `_maintenanceCts.Cancel()` → bounded
  `StartupMaintenance.Wait(DisposeJoinTimeout)` (named constant, 10s) → narrow fault absorption via
  `AggregateException.Handle` allowing only `OperationCanceledException`, `SqliteException`,
  `IOException` (anything else rethrows) → on timeout, a fault-observing `ContinueWith` so a later
  exception can never surface as unobserved → `_maintenanceCts.Dispose()` → `ClearAllPools()` last.
- Confirmed `Task.Wait(TimeSpan)` wraps a canceled/faulted task in `AggregateException` (never
  throws bare `OperationCanceledException`), so `ex.Handle(predicate)` is the correct narrow-catch
  shape here — a plain `catch (OperationCanceledException)` block would silently never fire.

**Validation:** build 0 Warning(s)/0 Error(s) (project + full solution, Release). Targeted
existing suite (`SqliteCacheStore*`, `SnapshotEvalMode*`, `ExpiredSnapshot*`) 52/52 passed before
Lambert's new coverage landed. Full existing suite 1981 passed / 8 pre-existing skips / 0 failed —
i.e. nothing in the pre-existing suite regressed even though it wasn't written against the new
contract yet. Production diff confirmed via `git diff --stat` to be exactly one file,
`SqliteCacheStore.cs` (90 insertions / 10 deletions). Did not open or edit any test file, did not
touch `ICacheStore.cs`/`ICacheStoreFactory.cs`/`Program.cs`/`EvalModeServices.cs`.

**Status:** COMPLETED
**Outcome:** R1 delivered per brief; `StartupMaintenance` is the agreed internal member name for
Lambert's tests to await; all nine reject-on-sight conditions in §5 checked against the diff and
none apply.

## 2026-09-11: Startup cache-eviction lifecycle — reaffirmation validation (#129, R1 follow-up) (completed)

Larry reaffirmed a specific requirement on the already-delivered R1 `Dispose()` implementation:
preserve/propagate unexpected (non-cancellation) worker faults when `StartupMaintenance` completes
within the bounded join, and on an actual timeout, observe eventual faults without broad
swallowing — no public API. Rather than assume my existing implementation already satisfied this,
I verified it empirically with a throwaway scratch console app (outside the repo tree, referencing
nothing production-internal, deleted before finishing — no test file was opened or edited):

- `Task.Wait(TimeSpan)` never throws a bare exception for a faulted or canceled task — it always
  wraps in `AggregateException`, for both the canceled and faulted cases. Confirmed
  `AggregateException.Handle(predicate)` is therefore the correct narrow-catch shape for the
  brief's three-row table (`OperationCanceledException`/`SqliteException`/`IOException`), not a
  broadening: `Handle` rethrows a fresh `AggregateException` containing any inner exception the
  predicate rejects, so an unexpected type (e.g. `InvalidOperationException`) still propagates out
  of `Dispose()` when the pass completes inside the join window.
- Confirmed the timeout-path continuation (`ContinueWith(t => _ = t.Exception, ...,
  OnlyOnFaulted | ExecuteSynchronously)`) only marks the fault "observed" for the
  process-wide unobserved-task-exception check — it does not consume or suppress the exception for
  any other consumer. A second, independent `await` on the *same* task instance after that
  continuation ran still received the original exception unchanged. This is what makes
  `StartupMaintenance` remain usable by Lambert's tests as a true fault-carrying handle even after
  `Dispose()` has already touched it on a timeout path.
- Conclusion: no code change was needed — the `Dispose()` implementation delivered in the initial
  R1 pass already satisfies the reaffirmed contract exactly as stated. Re-ran the full targeted and
  full test suites to reconfirm: 60/60 targeted, 1989 passed / 8 pre-existing skips / 0 failed full
  suite (Lambert's new coverage had landed by this point). Production diff unchanged: exactly
  `SqliteCacheStore.cs`.

**Incidental finding, not mine to fix:** while investigating an intermittently-observed failure in
`SqliteCacheStoreEvalModeTests.EvalMode_OpenEvictDispose_DatabaseUnchanged_No(New)WalOrShmSidecars`,
traced the root cause with the same scratch-app technique (also reproduced against the pre-#129
original code, proving it predates and is unrelated to this fix): the test harness's
`SnapshotEvalTestHarness.CreateStableSnapshotAsync` backs up straight from a live WAL-mode source
into an on-disk destination file via `SqliteConnection.BackupDatabase`, which carries the source's
WAL flag into the destination file's header even though no live `-wal` exists. Opening that file
later — even `Mode=ReadOnly` — makes SQLite (re-)establish WAL machinery and (re)create `-wal`/
`-shm` sidecars, which is exactly what the test's assertions forbid. Production's real
`SnapshotExporter.ExportAsync` avoids this by staging through an in-memory connection and asserting
`PRAGMA journal_mode = 'memory'` before serializing to disk, which strips the WAL flag — the test
harness does not replicate that step. By the time I finished isolating this, Lambert had already
landed a fix on her side and the suite was green; no production change was made or needed for it.

**Status:** COMPLETED
**Outcome:** Reaffirmed fault-handling contract validated as already satisfied by the existing
`Dispose()` implementation; zero further production changes required. Full suite green.

---

## Issue #130 — pre-fix seam: artifact source FileShare constant

**Task:** Introduce a behavior-neutral seam in `SnapshotExporter` ahead of the planned artifact
source share-policy fix, so Lambert can add a Windows discriminator test that compiles now and
fails pre-fix.

**Change:** Added `private const FileShare ArtifactSourceFileShare = FileShare.Read;` next to the
existing exporter constants, and replaced the inline `FileShare.Read` literal at the live-source
`FileStream` open in `CopyArtifactAsync` with this constant. Value is unchanged from current
behavior — this is purely a naming/seam commit, not the `FileShare.Read | FileShare.Delete` fix
itself (that lands in a later step per Dallas's plan).

**Verification:** `dotnet build src/HelixTool.Core/HelixTool.Core.csproj` succeeded, 0
warnings/errors. `git diff` confirms the only changed file is
`src/HelixTool.Core/Cache/SnapshotExporter.cs`, with exactly the constant addition and the single
literal-to-constant substitution — no other production file, test, changelog, package, or public
API touched. Not committed.

**Learning:** When a plan calls for a named constant purely so a not-yet-written test can compile
against it, land the constant with the *current* value first as its own tiny seam, and leave the
value change for the dedicated fix step — keeps the pre-fix/post-fix diff for reviewers minimal
and isolates the Windows-red-to-green transition to one commit.

**Status:** COMPLETED (seam only; behavior fix and tests are out of scope for this task).

---

## Issue #130 — production fix: scoped pool clearing and artifact source FileShare

**Task:** Implement Dallas's approved design exactly, against the pre-fix Windows-red evidence on
commit `8e6cee6`: `SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize`
and `IndependentRoots_DisposeA_WindowsExclusiveOpenOfB_StillFails_BecauseBPoolUntouched` were both
failing on Windows only (1993 passed, 2 failed, 8 skipped); Ubuntu and Squad CI were green.

**Change 1 — `src/HelixTool.Core/Cache/SqliteCacheStore.cs` `Dispose()`:**
Wrapped the cancel → bounded `StartupMaintenance.Wait` → narrow `AggregateException.Handle` →
timeout fault-observation sequence in a `try`, byte-identical to the #129 contract, and moved
`_maintenanceCts.Dispose()` plus pool release into the `finally` so post-join cleanup runs even
when an unexpected worker fault propagates out of the `try`. Replaced the process-global
`SqliteConnection.ClearAllPools()` with a fresh, unopened `SqliteConnection(_connectionString)`
passed to `SqliteConnection.ClearPool(...)` — the connection is never opened or cached, and the
exact `_connectionString` field is reused so the correct pool group binds via the `ConnectionString`
setter. No refcounting, no broadened catches, no suppression of unexpected faults added.

**Change 2 — `src/HelixTool.Core/Cache/SnapshotExporter.cs`:** Changed
`internal const FileShare ArtifactSourceFileShare` from `FileShare.Read` to
`FileShare.Read | FileShare.Delete` and updated its doc comment with the Windows/POSIX rationale
(delete/rename sharing lets a concurrent cache write or eviction proceed while the export keeps
reading the already-opened file identity; `FileShare.Write` deliberately excluded so the
before/after length + SHA-256 integrity checks stay valid). Only the single call site in
`CopyArtifactAsync` is affected; staging destination `FileShare.None`, immutable hash handles,
backup logic, retries, timeouts, and publication were untouched.

**Verification:** `dotnet build` on both `HelixTool.Core.csproj` and `HelixTool.Tests.csproj`
succeeded, 0 warnings/errors. Targeted filter (`SqliteCacheStore*`, `Snapshot*`,
`CacheStoreFactoryTests`, `CacheSecurityTests`, `AzdoEvidence*`): 386 passed, 0 failed, 7 skipped
(this machine is macOS, so the Windows-only discriminator correctly skips via its
`WindowsOnlyFactAttribute` gate). The cross-platform artifact-replacement test
(`SetArtifactAsync_WhileArtifactOpenWithExporterSourceShare_TrulyReplacesContentAndFileSize`) now
passes on this machine, which was the reachable half of the two pre-fix Windows failures. Full
local suite: 1994 passed, 0 failed, 9 skipped, total 2003 — net +4 passed / +1 skipped versus the
1990 passed / 8 skipped Unix baseline in the plan, consistent with the four new regression tests
(C1–C4) added in `8e6cee6`, one of which (C2, Windows-only) skips here.
`rg "ClearAllPools" src` returns zero call sites (comment references only). Diff confined to
exactly the two named production files — confirmed via `git status --short` before and after;
no test, changelog, package, project, or public API file touched. Not committed or pushed per
instruction; left for the reviewer gate and PR finalization step.

**Learning:** The `try { ... } finally { cts.Dispose(); ClearPool(...); }` restructuring is a pure
reordering of *when* cleanup runs relative to control flow, not a change to *what* the #129 steps
do — every line inside the `try` is textually identical to before, which made this an easy diff
for a reviewer to audit against the byte-identical-contract requirement. Constructing an unopened
`SqliteConnection` purely to bind a pool group via its `ConnectionString` setter (never calling
`.Open()`) is a pattern worth remembering: it gets you the exact per-connection-string pool
group without paying for a real connection or risking that the a long-lived cached instance holds
a since-pruned pool group.

**Status:** COMPLETED. Awaiting Dallas's reviewer gate before commit/push per plan step 8.

---

## Issue #130 — scope amendment: `SetArtifactAsync` silent-failure-then-stale-success defect

**Task:** Dallas's post-CI retrospective amended scope: the approved
`SnapshotExporter.ArtifactSourceFileShare = FileShare.Read | FileShare.Delete` stands, but the
Windows-red evidence surfaced a real production correctness bug, not a test bug. On Windows,
`File.Move(temp, dest, overwrite: true)` (via Win32 `MoveFileEx`/`MOVEFILE_REPLACE_EXISTING`) can
still fail with a sharing violation against a destination held open by a concurrent reader — even
one opened with `FileShare.Read | FileShare.Delete` or `FileShare.ReadWrite | FileShare.Delete` —
because `MoveFileEx` requires the destination to be free of incompatible opens for the
content-replace step, share-delete alone is not sufficient. The existing catch
`(IOException or UnauthorizedAccessException)` swallowed exactly that failure, deleted the only
copy of the new bytes (the temp file), and fell through to write a success row into
`cache_artifacts` using the untouched destination's stale size — a silent data-loss/lie-to-the-
database bug, now confirmed in scope for me to fix in
`src/HelixTool.Core/Cache/SqliteCacheStore.cs` only.

**Research before changing anything:** Read `dotnet/runtime`'s `File.cs`, `FileSystem.Windows.cs`,
and `FileSystem.Unix.cs` from the current `main` branch, plus the Win32 `ReplaceFile` MSDN
reference, rather than assuming behavior:
- `File.Move(src, dest, overwrite: true)` on Windows calls `Interop.Kernel32.MoveFile(src, dest,
  overwrite: true)` → `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING`. A sharing violation there
  (`ERROR_SHARING_VIOLATION`) is mapped by `Win32Marshal` to `IOException` — confirming the
  existing catch clause's exception types are exactly right for detecting this failure, they just
  respond to it wrongly.
- `File.Replace(src, dest, null)` on Windows calls `Interop.Kernel32.ReplaceFile`. Its Win32
  contract (`ReplaceFileW` docs) states it explicitly: "This file [the one being replaced] is
  opened with the GENERIC_READ, DELETE, and SYNCHRONIZE access rights. The sharing mode is
  FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE" — i.e. `ReplaceFile` is the primitive
  Win32 built specifically to replace a destination file that another process still has open,
  and it tolerates *both* concurrent-holder share modes named in the brief
  (`Read|Delete` and `ReadWrite|Delete`), because its own required minimum is a strict subset of
  both. It requires the destination to already exist (it opens it), which is guaranteed on this
  fallback path since a genuinely-absent destination is created directly by the already-attempted
  `File.Move` with no exception.
- On Unix, `FileSystem.Unix.cs`'s `MoveFile(overwrite: true)` is a plain `rename(2)` (atomic
  regardless of open handles) and its `ReplaceFile` is rename-based too — so the fallback path is
  Windows-only *in practice* (via the exception, not an explicit OS check), and is safe if ever
  exercised cross-platform.

**Change:** In `SetArtifactAsync`, kept `File.Move(tempPath, fullPath, overwrite: true)` as the
first attempt unchanged (satisfies "destination absent → keep first-write atomic move" and is the
cheaper path when nothing has the destination open). On `IOException`/`UnauthorizedAccessException`
from that attempt, instead of deleting the temp file and silently continuing, now attempt
`File.Replace(tempPath, fullPath, destinationBackupFileName: null)` as the correct
concurrent-replace primitive. If `File.Replace` also throws, the temp file (the only copy of the
new bytes) is deleted on a best-effort basis and the original exception is rethrown via a bare
`throw;` inside the inner `catch` — this exits `SetArtifactAsync` before the file-size read, the
`INSERT OR REPLACE INTO cache_artifacts` write, and the LRU eviction call, so a replacement failure
can never produce a stale/successful DB row. If `File.Replace` succeeds, control falls through to
the existing size-read/DB-write/eviction tail exactly as before, now reading the genuinely-replaced
file. No retry loop, no sleep, no new public member, no test seam, no schema change — the diff is
additive around the single existing catch block.

**Verification:** `dotnet build` on `HelixTool.Core.csproj` and `HelixTool.Tests.csproj`: 0
Warning(s)/0 Error(s) both. This machine's installed runtime is `11.0.0-preview` only while the
projects target `net10.0`, so `dotnet test` needed `DOTNET_ROLL_FORWARD=LatestMajor` in the
environment to launch the test host — an environment quirk, not a project/test change, and no
project file was touched to work around it. Targeted filter (`SqliteCacheStore*`, `Snapshot*`,
`CacheStoreFactoryTests`, `CacheSecurityTests`, `AzdoEvidence*`): **387 passed, 0 failed, 7
skipped, 394 total** (the +1 passed / same skip count versus my prior 386/7 report reflects
Lambert's since-landed `[Theory]` split of the regression test into
`useExporterSourceShare: true/false` cases, covering both the exporter's `Read|Delete` share and a
plain `GetArtifactAsync`-style `ReadWrite|Delete` share against the same fix — both now pass here).
Full local suite: **1995 passed, 0 failed, 9 skipped, 2004 total**. `git diff --stat` confirms
exactly one file changed, `src/HelixTool.Core/Cache/SqliteCacheStore.cs` (23 insertions / 3
deletions) — did not touch `SnapshotExporter.cs`, `CHANGELOG.md`, any test file, or any other
production file (the `CHANGELOG.md` and `SnapshotExportTests.cs` diffs already present in the
working tree before I started are Lambert's/Dallas's own uncommitted work, not mine, and were left
untouched). Not committed or pushed per instruction.

**Learning:** `File.Move(overwrite: true)` and `File.Replace` are not interchangeable "atomic
overwrite" primitives on Windows even though POSIX `rename(2)` makes them behave identically on
Unix — the former requires the destination to be unopened (or opened compatibly) for its
content-replace step, the latter is Win32's purpose-built "swap in a new file while someone still
has the old one open" API and documents its own minimum required share mode directly in the
`ReplaceFileW` reference page. When a bug report says a share-flag fix didn't fully work on
Windows, check whether the *consuming* primitive (not just the *producing* one) actually honors
the new share flags before assuming the two share-policy fixes are the same fix.

**Status:** COMPLETED. Build 0/0, targeted 387/0/7 (394 total), full suite 1995/0/9 (2004 total).
Diff confined to `SqliteCacheStore.cs`. Not committed/pushed per instruction; awaiting reviewer
gate.

---

## Issue #130 — revision: destination-existence dispatch, not exception-driven retry

**Task:** My first pass on the `SetArtifactAsync` fix above was rejected before acceptance. Dallas's
required design is: pick the publication primitive up front from whether the destination already
exists — `File.Move` when absent, `File.Replace` when present — never attempt `Move` first and
fall back to `Replace` only after catching a failure. My initial pass did exactly that rejected
shape (try `Move`, catch, then try `Replace` inside the catch), which is a retry path in substance
even though it only ever retries once, and its nested `catch { ...; throw; }` around the `Replace`
attempt was a bare catch-all — broader than the narrow, explicit exception handling the plan
requires, and it risked reading as "swallow-then-rethrow" rather than a clean propagate.

**Change — same method, same file, still the only one touched:** Replaced the
try/Move/catch/Replace/fallback shape with:
- `var destinationExisted = File.Exists(fullPath);` computed once, up front — matches this file's
  own precedent for this exact check (`DeleteArtifactRows` already guards its delete with
  `if (File.Exists(fullPath)) File.Delete(fullPath);`).
- A single `if (destinationExisted) File.Replace(...); else File.Move(..., overwrite: true);` with
  **no catch around either call** — any exception from the chosen primitive now propagates
  immediately and unmodified, so the method exits before reaching the file-size read, the
  `INSERT OR REPLACE INTO cache_artifacts` write, and the eviction pass; no risk of a stale-success
  row on any failure path, and no place left where the exception type is silently narrowed or
  discarded.
- A `published` flag set only after the chosen call returns without throwing, checked in a
  `finally` block to decide whether `tempPath` needs best-effort cleanup. On the success path
  `published == true`, so the `finally` does nothing (the primitive already consumed/renamed
  `tempPath`; confirmed `File.Delete` is itself idempotent for an already-gone path via both
  `FileSystem.Windows.cs`'s explicit `ERROR_FILE_NOT_FOUND` early-return and
  `FileSystem.Unix.cs`'s `ENOENT` early-return in `dotnet/runtime`, so even calling it redundantly
  would be harmless — but the flag keeps the *intent* explicit rather than relying on that
  incidental idempotency). On any failure path, the `finally` calls `File.Delete(tempPath)` wrapped
  in a narrow `catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)`
  — the same two-type shape `DeleteArtifactRows` already uses for its own cleanup delete — so an
  unexpected cleanup exception type still surfaces as a bug instead of being absorbed, and because
  this is a `finally` (not a `catch` wrapping the primary call), the primary Move/Replace exception
  that is already unwinding through it is never replaced or masked by the cleanup logic; it keeps
  propagating out of `SetArtifactAsync` unchanged.
- Documented in comments the accepted, intentional narrow race this reintroduces versus the
  previous unconditional-`Move` design: a concurrent evictor could delete `fullPath` between the
  `File.Exists` check and the `File.Replace` call, in which case `Replace` fails because it
  requires the destination to exist, and that failure now correctly propagates rather than being
  retried — this is the "reasonable same-key race behavior without retries" the brief explicitly
  called for, not a regression to paper over.

**Verification:** `dotnet build` on `HelixTool.Core.csproj` and `HelixTool.Tests.csproj`: 0
Warning(s)/0 Error(s) both (same `DOTNET_ROLL_FORWARD=LatestMajor` environment note as before to
launch the test host against this machine's `11.0.0-preview`-only runtime — no project file
touched). Targeted filter (`SqliteCacheStore*`, `Snapshot*`, `CacheStoreFactoryTests`,
`CacheSecurityTests`, `AzdoEvidence*`): **387 passed, 0 failed, 7 skipped, 394 total** — identical
counts to the rejected pass, confirming the redesign didn't change observable behavior on this
platform, only the internal shape of the fix. Full local suite: **1995 passed, 0 failed, 9 skipped,
2004 total** — also identical. `git diff --stat` confirms exactly one file changed,
`SqliteCacheStore.cs` (41 insertions / 5 deletions from the pre-fix baseline). Confirmed
byte-for-byte that `Dispose()`'s scoped-pool-cleanup block (`_maintenanceCts.Cancel()` through
`SqliteConnection.ClearPool(poolHandle)`) and the rest of the file are untouched by this revision —
only the body of `SetArtifactAsync`'s publication step changed. `CHANGELOG.md` and
`SnapshotExportTests.cs` remain modified in the working tree from Lambert's/Dallas's own prior
uncommitted work, not touched by me. Not committed or pushed per instruction.

**Learning:** "Catch an exception from primitive A, then try primitive B inside the catch" is a
retry in every way that matters to a reviewer auditing error-handling discipline, even when it's
bounded to exactly one extra attempt and even when the second primitive is the objectively correct
one — the tell is that success or failure of the operation still depends on exception flow instead
of an up-front decision. Computing the branch condition once, before touching the filesystem, and
using a `finally`-with-success-flag for cleanup instead of a `catch`-wrapping-the-primary-call is
the shape that keeps a secondary (cleanup) failure from ever being able to shadow a primary
(operation) failure, structurally, not just by convention.

**Status:** COMPLETED. Build 0/0, targeted 387/0/7 (394 total), full suite 1995/0/9 (2004 total) —
unchanged from the prior pass. Diff confined to `SqliteCacheStore.cs`. Not committed/pushed per
instruction; awaiting reviewer gate.

## 2026-09-11: Production fixes — pool scope, artifact source share, and File.Replace (#130, R2-R3)

Two sequential commits delivered the evidence-driven production fixes:

**R2 (c58340d): Scope disposal + FileShare.Read|Delete**

Scoped `SqliteConnection.ClearAllPools()` in `SqliteCacheStore.Dispose()` to clear only the disposing store's exact connection string, not the process-global pool, fixing cross-root interference. Changed `SnapshotExporter.CopyArtifactAsync` source-file `FileShare` from `FileShare.Read` to `FileShare.Read | FileShare.Delete`, expecting permissive source-share alone would solve Windows file-handle conflicts in concurrent cache eviction/replacement.

Verification: release build 0/0 warnings/errors, targeted 387 passed / 7 skipped, full suite 1995 passed / 9 skipped. Pool-scope discriminator test turned GREEN (confirming scoped clearing works); artifact-replacement test remained RED on Windows (proving original plan's `MoveFileEx` assumption wrong — permissive source-share alone is insufficient).

**R3 (10149cf): File.Replace for existing artifacts**

Root-cause analysis with Lambert: `File.Move(..., overwrite: true)` itself lacks share-conflict awareness on Windows, even when source is opened with `FileShare.Read | FileShare.Delete`. Replaced `File.Move` in `SnapshotExporter.SetArtifactAsync` with platform-aware logic:
- `File.Replace` for existing artifacts (atomic, handles share conflicts on Windows)
- `File.Move` for absent artifacts (simple case)

Surfaces publication failures before metadata-update, per design. Verification: release build 0/0, targeted 387 passed / 7 skipped (artifact-replacement test now GREEN), full suite 1995 passed / 9 skipped. Windows CI, Ubuntu CI, Squad CI all success.

**Learning:** The original plan's `MoveFileEx` intuition was correct in spirit (need atomic/share-aware replacement), but C# standard library `File.Replace` is the idiomatic solution and directly solves the platform-specific share-conflict gap. Test-driven discovery of the hidden defect (silent catch in original implementation masked the failure) validates the regression-coverage-first methodology.

**Status:** COMPLETED — both commits delivered, all tests green, Dallas final verdict APPROVED
