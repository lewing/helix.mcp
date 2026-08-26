# Decision: ModelContextProtocol C# SDK 1.4.0 → 2.2.0 Migration — APPROVED

**Date:** 2026-08-20  
**Status:** ✅ **APPROVED** — Ready for PR creation  
**Final verdict by:** Dallas (Lead Architect)  
**Agents:** Ash (analysis), Ripley (implementation + B1–B3 fix), Lambert (T1–T4 + F1/F3), Kane (F2)

---

## Executive Summary

The ModelContextProtocol C# SDK is upgrading from v1.4.0 to v2.2.0 (released 2026-08-13). This is a major version migration requiring dependency bump + one-line session-mode configuration. All blocking findings (F1, F2, F3) are resolved. Tests (T1–T4) implemented and passing. Migration is low-risk; no tool refactoring required.

---

## Timeline & Key Decisions

### Phase 1 Approved (Dallas Architecture Review, Final Review, Re-Review)

**Approved changes:**
1. `Directory.Packages.props`: bump `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` to 2.2.0 (locked in sync)
2. `src/HelixTool.Mcp/Program.cs`: add explicit `SessionMode = HttpServerSessionMode.Stateless` in `.WithHttpTransport()` options
3. `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs`: add `LimitedResultsSchema<T>` record + `OutputSchemaType` annotations on six tools (B1 fix by Ripley)
4. New test files (5 total, T1–T4 + F1/F3/G7 gates): T1 blocking; T2/T3/T4 supporting gates
5. Cleanup: `.gitignore` additions, XML-doc URL rewrites (F2 by Kane), auth-scoped test isolation (F3 by Lambert)

**Rejected (NOT in phase 1):**
- Ash's Option A (hybrid session mode): explicitly rejected — stateless is already the correct mode
- Phase-2 stateless refactor: no stateful tool code exists; close this thread

---

## Key Findings & Resolutions

### F1 — Session-mode test hermetic defect (RESOLVED by Lambert)

**Problem:** `HttpTransportSessionModeTests.cs` was non-deterministic: failed when `HLX_API_KEY` env var was set.

**Root cause:** Test used `WebApplicationFactory<Program>`, which boots the real `ApiKeyMiddleware` reading the ambient environment variable at pipeline-build time. No `X-Api-Key` header sent → 401 auth rejection before transport even runs.

**Fix:** `AttachApiKeyIfConfigured()` helper reads the *same* `HLX_API_KEY` variable through *same* production constant `ApiKeyMiddleware.EnvVarName`, applies *same* predicate, attaches header when needed. `HlxApiKeyEnvCollection` serializes xUnit test execution to eliminate cross-class parallelism races. Both worlds (key set/unset) now green.

**Verification:** Full suite 1,570 tests (1,568 passed, 2 pre-existing skips / 0 failed) — identical in both env worlds.

### F2 — Scratch artifacts & licensing (RESOLVED by Kane)

**Problem:** `.squad/artifacts/upstream-StatelessServerTests.cs` (642-line third-party copy with no license header) and `.squad/evidence/b2-{red,green}.txt` (generated test logs) were about to be committed.

**Fix:**
- Deleted both directories entirely
- Added `.squad/artifacts/` and `.squad/evidence/` to `.gitignore`
- Rewrote two test file XML-doc references from local paths to stable upstream GitHub URLs (`https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/tests/ModelContextProtocol.AspNetCore.Tests/StatelessServerTests.cs`)
- Kept `.squad/skills/mcp-sdk-major-upgrade/SKILL.md` (first-party, durable knowledge)
- Corrected `AzdoMcpTools.cs` XML-doc misnomer: "paginated" → "capped/truncating"

**Verification:** `git status` confirms no untracked paths under deleted dirs; grep confirms no dangling references.

### F3 / G7 — HTTP auth + per-request token/cache isolation (RESOLVED by Lambert)

**Problem:** No gate verified that auth gating + per-request scoping survive sessionless HTTP mode.

**Fix:** New `ApiKeyScopedRequestIsolationTests.cs` using real `WebApplicationFactory<Program>` (not mocked host). Four facts:
1. Missing `X-Api-Key` header → 401
2. Incorrect key → 401
3. Correct key → proceeds
4. Two consecutive requests with different `Authorization: Bearer` tokens resolve independent tokens + cache partitions (verified via recording factories for `IHelixApiClientFactory`/`ICacheStoreFactory`)

**Verification:** Identical 4-pass results with `HLX_API_KEY` unset and set (ambient).

---

## Validation Gates (All Passing)

| Gate | Owner | Status | Notes |
|------|-------|--------|-------|
| **G1** Clean build | Ripley | ✅ | 0 warnings, 0 errors |
| **G2** Full suite | Lambert | ✅ | 1,568 passed / 2 pre-existing skipped / 0 failed (1,570 total) |
| **G3** Progress over stateless HTTP (T1, blocking) | Lambert | ✅ | Progress notifications survive SSE stream end-to-end |
| **G4** tools/list wire parity | Lambert | ✅ | 30,366 bytes (v1.4.0) → 29,163 bytes (v2.2.0); delta explained (six minimal schemas 408→102 bytes [−306], removed task-support metadata [−897], −1,203 bytes total, −3.96%, approximately −301 tokens) |
| **G5** GET/DELETE contract | Lambert | ✅ | Asserts 405 on stateless endpoint; API key auth gates tested |
| **G6** Stdio smoke | Ripley | ✅ | hlx mcp over stdio; tools/list + live call succeed |
| **G7** HTTP smoke (F3 gate) | Lambert | ✅ | Two-request auth + token/cache scoping verified |

---

## Test Summary (T1–T4)

### T1 — Progress notifications over stateless HTTP (BLOCKING)
- Hosts real `HelixMcpTools` over `TestHost` with `SessionMode.Stateless`
- Real `McpClient` over `HttpClientTransport`; passes `progressToken` in request meta
- Asserts ≥1 `notifications/progress` arrives, with values matching `ProgressUpdate` → `ProgressNotificationValue` adapter mapping
- **Result:** ✅ PASS

### T2 — Structured-content return-type guard
- Real `McpServerTool` schema generation over tool discovery
- Discovery across structured tool classes (≥20 methods in `AzdoMcpTools`, `HelixMcpTools`, `CiKnowledgeTool` with `UseStructuredContent = true`)
- Six `LimitedResults<T>` tools pinned to exactly `{"type":"object"}` schema
- Current and down-level wire assertions prove natural unwrapped `structuredContent` behavior
- Anti-vacuity coverage prevents future silent regression
- **Result:** ✅ PASS

### T3 — Session mode effective-value regression guard
- Effective-value regression guard: asserts `HttpServerTransportOptions.SessionMode` resolves to `HttpServerSessionMode.Stateless`
- Catches changes to `Stateful`/`StatefulForInitializeClients` and future default changes
- Cannot distinguish deletion while SDK 2.2 itself defaults to `Stateless`, hence explicit source configuration remains the reviewability rule
- **Result:** ✅ PASS (both test constructs + mutation-verified on Program.cs line flip)

### T4 — GET/DELETE return 405 under stateless
- Contract test on MCP endpoint asserting GET / DELETE yield `HttpStatusCode.MethodNotAllowed` (405)
- Documents endpoint surface change for readers
- **Result:** ✅ PASS

---

## Migration Plan (Dallas)

### Why Stateless is Correct
- Our entire server is already per-request-scoped (token accessor, cache store, API clients all `AddScoped`)
- We hold no cross-request server state
- We issue no server→client requests (progress is request-bound, injected parameter)
- Stateless mode removes session affinity requirement for load-balanced deployments
- SDK 2.x flipped the default from stateful → stateless; we are explicitly declaring the intention

### Rollback Plan
Revert `Directory.Packages.props` to **v1.4.1** (2026-07-09, the 1.x servicing release) + revert `Program.cs` hunk. Two-file revert; all other changes are independent.

### Effort Estimate
- Ripley: 0.5–1 day (dependency bump + smoke tests G6/G7)
- Lambert: 0.5 day (test gates T1–T4, G2–G5)
- Kane: 0.25 day (artifact cleanup F2)
- Total: ~1.25 days

---

## Key Learnings

1. **Session mode is the critical change.** SDK 2.x flipped the default; without explicit source declaration, reviewers cannot see the intent and default-change regressions are invisible.
2. **Hybrid mode adds legacy session debt unnecessarily.** Stateless is already our architecture; adopting hybrid would re-introduce session affinity for load-balanced deployments, on day one of a deprecated API.
3. **Wire-format claims require evidence.** Dallas's initial concern about "silent breaking changes to 6 tools" was real in schema structure but not in wire payloads to legacy clients. B1 fix is still correct; frame it as schema clarity improvement, not legacy regression repair.
4. **Auth + scoping under stateless requires proof.** T1 (progress) and F3 (per-request token/cache isolation) are the blocking facts that justify the mode choice; do not assume existing unit tests catch them.

---
---
date: 2026-07-28
author: ripley
status: decided
---

# Decision: Add `workItem` parameter to `helix_find_files`

## Problem

A calling model passed `workItem` to `helix_find_files` and received a hard schema-rejection error from strict-mode parameter validation:

> Unknown parameter 'workItem' for tool 'helix_find_files'. Did you mean: maxItems?

All sibling tools (`helix_files`, `helix_logs`, `helix_search`, `helix_download`, `helix_work_item`, `helix_parse_uploaded_trx`) accept `workItem`. `helix_find_files` was the sole exception.

## Decision

Add `workItem` as an optional parameter to `helix_find_files`. When supplied:
- Skip `ListWorkItemsAsync` (which pages through all work items in the job)
- Call `ListWorkItemFilesAsync` directly on the named work item
- Return a `FindFilesResults` with `TotalWorkItems = 1`, `Truncated = false`

This is equivalent to `helix_files` + client-side glob filter, implemented at the service layer for proper error handling.

## Rationale

- Consistency: models learn the jobId/workItem pattern from one sibling and apply it to all. Breaking consistency is a usability defect, not a feature.
- Performance: single-work-item fast path avoids listing all work items in the job.
- No breaking change: `workItem` is optional with `null` default; existing callers unaffected.

## Scope

- `src/HelixTool.Core/Helix/HelixService.cs` — `FindFilesAsync` signature + single-item fast path
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` — MCP tool: added `workItem`, URL extraction, updated description
- `FindBinlogsAsync` internal caller updated to use named `cancellationToken:` arg

## Other inconsistencies checked

No other schema inconsistencies found. `helix_status` and `helix_batch_status` are intentionally job-scoped and do not need `workItem`.

---
date: 2026-07-28T16:18:09-05:00
author: lambert
status: proposed
---

# Decision: Schema Consistency Tests for Work-Item-Scoped Helix Tools

## Problem

A hard schema-rejection error (`Unknown parameter 'workItem' for tool 'helix_find_files'`) reached production because there was no automated gate catching when a new tool in the Helix family omitted a parameter that all its siblings had. The parameter validation layer did exactly what it should — it rejected the unknown param — but the gap in the schema itself was never caught before ship.

The existing `McpServerToolParameters_HaveDiscoverableDescriptions` test in `McpToolDescriptionTests.cs` guards descriptions but not cross-sibling parameter consistency.

## Decision: Explicit Enumeration Test in McpToolDescriptionTests

Add a `[Theory]` test (`WorkItemScopedHelixTools_HaveOptionalWorkItemParameter`) that enumerates every Helix MCP tool expected to have an optional `workItem` parameter:

```
helix_logs, helix_files, helix_download, helix_find_files,
helix_work_item, helix_search, helix_parse_uploaded_trx
```

The test uses reflection to assert each tool has a `workItem` parameter that is optional (nullable string with null default).

## Rationale

- **Explicit enumeration > heuristic derivation.** The description-based approach (look for "work item" in the description) doesn't work reliably: `helix_find_files` says "Search work items" (plural), while the tools that had it say "for a Helix work item" (singular). The explicit list is the ground truth.
- **Fails loudly on regression.** If a new tool is added to this family without `workItem`, the test fails with a message that names the tool and its actual parameter list.
- **Self-documents the invariant.** The test and its inline comments are the canonical answer to "which Helix tools need workItem?"

## Alternatives Rejected

- **Heuristic over description text:** Unreliable (see above).
- **Single `[Fact]` over all tools:** No — `[Theory]` is better so each tool gets its own pass/fail row in the test runner.

## Consequences

- Any future Helix tool in the work-item-scoped family must be added to the `[InlineData]` list, or the test will error with "tool not found."
- The test is green today (Ripley's fix already landed) and must stay green.

## Companion Tests Added

- `HelixMcpToolsTests.FindFiles_WithWorkItem_ScansOnlyNamedWorkItem` — behavioral: when `workItem` is provided, only that item is queried; `ListWorkItemsAsync` is not called.
- `HelixMcpToolsTests.FindFiles_WithWorkItem_DoesNotReturnOtherWorkItems` — behavioral: results contain only the requested work item.
- Two pre-existing tests fixed to use named args (`pattern: "*.trx"`, `pattern: "*"`) after Ripley's parameter ordering change.

# Review: helix_find_files workItem parameter addition

**Date:** 2026-07-28T16:18:09-05:00
**Reviewer:** Dallas (Lead)
**Artifacts:** Ripley (implementation), Lambert (tests)
**Verdict:** ✅ APPROVE

---

## 1. Parameter Ordering — Acceptable

**MCP tool level (`FindFiles`):** `workItem` is placed after `jobId` and before `pattern`. This matches the sibling convention exactly — `Download`, `Logs`, `Files`, `Search`, and `ParseUploadedTrx` all place `workItem` as the second parameter after `jobId`. MCP parameters bind by name (JSON schema), not positionally, so the wire protocol is unaffected. The two test callsites updated to named arguments (`pattern: "*.trx"`) are the only C# callers outside tests. No package is published; this is not external API surface.

**Service level (`FindFilesAsync`):** `workItem` is appended after `progress` and before `cancellationToken` — the lowest-disruption position. The only internal caller change is `FindBinlogsAsync` switching `cancellationToken` from positional to named, which Ripley handled correctly. `Program.cs` passes 3 positional args and is unaffected.

No source-compat concern. Ordering follows established convention.

## 2. Fast Path Correctness — Sound

- **Glob matching:** Fast path uses the same `MatchesPattern(f.Name, pattern)` as the scan path. ✓
- **Error handling:** A nonexistent work item will hit `ListWorkItemFilesAsync` → HTTP 404 → caught by the existing `catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)` block → surfaces as `HelixException`. Same behavior as the scan path hitting a missing job. ✓
- **`maxItems` interaction:** Silently ignored when `workItem` is set. The tool description says "instead of scanning up to maxItems work items," which is clear. The MCP tool sets `ScannedItems = 1` in the response, so callers see the actual scope. Acceptable.
- **`Truncated` flag:** Fast path returns `Truncated: false`, `TotalWorkItems: 1` — correct since there is no truncation when targeting a single item. ✓

## 3. Tool Description — Clear and Consistent

The updated description adds: "When workItem is supplied, scopes the search to that single work item (equivalent to helix_files + glob filter) instead of scanning up to maxItems work items."

The `workItem` parameter description — "Helix work item name; optional only when jobId is a full Helix work item URL" — is character-for-character identical to sibling tools (`helix_logs`, `helix_files`, `helix_search`, etc.). ✓

The URL-extraction fallback (`HelixIdResolver.TryResolveJobAndWorkItem`) matches the pattern in all sibling tools. ✓

## 4. Test Quality — Adequate with Caveats

**Schema consistency test (`WorkItemScopedHelixTools_HaveOptionalWorkItemParameter`):**
Uses `[InlineData]` with a hardcoded list of 7 tool names. This means a future work-item-scoped tool added without `workItem` will NOT be caught unless someone remembers to add it to the list. However, auto-detection of "work-item-scoped" tools is non-trivial (no attribute or marker distinguishes them), so a hardcoded list is a pragmatic choice. The `GetMcpToolMethods()` discovery mechanism is sound for the tools it covers.

**Behavioral tests:** Correct in their assertions — they verify `ListWorkItemsAsync` is NOT called and only the named work item is scanned. However, both tests use reflection-based invocation unnecessarily complex for this scenario. Named arguments (`FindFiles(ValidJobId, workItem: "target-wi", pattern: "*.trx")`) would be simpler and more readable.

**Stale comments:** Test doc-comments say "RED until Ripley adds the optional workItem parameter" — but the parameter already exists. These are harmless but noisy.

## Non-Blocking Follow-Ups

1. **Lambert:** Simplify the two behavioral tests to use direct named-argument calls instead of reflection. The reflection was needed when writing tests anticipatorily (before the param existed), but now that it's landed, direct calls are clearer.
2. **Lambert:** Remove or update the "RED until Ripley's fix" comments — the fix has landed.
3. **Future consideration:** If a new work-item-scoped tool is added, the `[Theory]` list in `McpToolDescriptionTests` must be updated manually. Consider adding a code comment near the `[InlineData]` block reminding future authors to extend the list.

# PR #117 Reviewer Fixes — Decisions & Patterns

**Date:** 2026-07-28  
**Author:** Ripley  
**Branch:** lewing-fix-find-files-workitem-param  
**Commit:** 6d95624

---

## Decision: Use `IsNullOrWhiteSpace` as the canonical "absent" predicate for optional string tool params

**Context:** `HelixMcpTools.FindFiles` used `IsNullOrEmpty`; `HelixService.FindFilesAsync` used `IsNullOrWhiteSpace`. A whitespace-only `workItem` was treated differently by each layer — the MCP layer skipped URL extraction while the service treated it as absent and ran a full scan. The `scannedItems` metadata then reported `1` (MCP path said "scoped") while the service returned results for all items.

**Decision:** Standardize on `IsNullOrWhiteSpace` at every layer boundary that guards an optional user-supplied string. A whitespace-only name is never a valid work item identifier.

**Scope:** All Helix MCP tool methods that check `workItem` before URL extraction or branching logic.

---

## Decision: Use `GetWorkItemFilesAsync` (not `_api.ListWorkItemFilesAsync`) in single-item fast paths

**Context:** `FindFilesAsync`'s single-item fast path called `_api.ListWorkItemFilesAsync` directly. The outer catch blocks map HTTP 404 → "Job 'X' not found", so a missing work item produced the wrong error message.

**Decision:** When a method-level sibling (`GetWorkItemFilesAsync`) already encapsulates the right error context for a resource, call the sibling instead of the raw API client. This keeps error messages accurate without duplicating catch logic.

**Scope:** Any future fast-path additions in `HelixService` that operate on a single work item within a method otherwise scoped to jobs.

---

## Pattern: Schema consistency guard is discovery-based, not enumeration-based

**Context:** The skill I authored described a `[Theory]`/`[InlineData]` pattern for the schema-consistency test. Lambert implemented a `[Fact]` with reflection-based discovery (`HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped`) that automatically catches new tools and requires explicit opt-out for job-scoped tools.

**Preferred pattern:** Discovery-based `[Fact]` with an `intentionallyJobScopedTools` exclusion set. New work-item-scoped tools require zero test changes. New job-scoped tools require one line in the exclusion set with a justification comment.

**Recommendation to Dallas:** Consider applying this same discovery-based guard pattern to other cross-cutting invariants (e.g., all tools with `jobId` must have a `Description` attribute, all tools using `CancellationToken` must not expose it in the MCP schema).
# Decision: Reflection contract tests must assert parameter shape, not just presence

**Date:** 2026-07-28  
**Author:** Lambert  
**Context:** PR #117 — helix_find_files workItem parameter, follow-up review comment

## Decision

When a reflection-based guard asserts that a parameter exists, it must also assert the **full behavioral shape** of that parameter:

1. **Name** — parameter named as expected
2. **Type** — `ParameterType == typeof(T)` for the expected CLR type
3. **Optionality and default** — `HasDefaultValue && DefaultValue is null` for an optional-with-null-default pattern

Asserting only the name allows wrong-type or required parameters to slip through while still violating the MCP schema contract.

## Rationale

The original guard in `HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped` only checked `p.Name == "workItem"`. A future author declaring `string workItem` (required), `int workItem`, or `string workItem = "default"` would pass the guard while producing an inconsistent MCP schema — exactly the bug class PR #117 was preventing.

## Applied in

`src/HelixTool.Tests/McpToolDescriptionTests.cs` — `HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped`, committed on branch `lewing-fix-find-files-workitem-param`.

## Generalization

Any schema-consistency guard that uses reflection to check a parameter convention should validate the complete contract: name + type + optionality. This applies equally to future guards for other cross-tool conventions (e.g., `filter`, `top`, `org`).

---

