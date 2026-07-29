# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, test architecture.

**Focus (2026-06-24 through 2026-07-20):** Strict-mode implementation (PR #83–87), CCA cycles, test patterns, anticipated schema-reduction validation work.

---

## 2026-07-20: Tiered outputSchema Recommendation — PEER REVIEW

**Context:** Dallas refined "flatten all" → tiered (FLATTEN 10 / KEEP 3 / LEAVE 12).

**Anticipated Lambert work (pending user approval):**
- **Integration test:** Confirm tools/list shrinks ~5,450 bytes after tiered implementation
- **StructuredContent validation:** Verify responses still emit StructuredContent despite flattened schema
- **Test file patterns:** Reuse existing MCP_* or StructuredContent_* tests

**See also:** .squad/decisions/decisions.md (Dallas decision, merged from inbox 2026-07-20).

---

## 2026-06-24: Strict-Mode Implementation (PR #83–87)

### PR #83 Review — Issue #81 Stage A
**Blocking bug found:** Missing TypeInfoResolver in Program.cs → first-request crash (InvalidOperationException on read-only JsonSerializerOptions).
- SDK sets TypeInfoResolver auto when null, but only before MakeReadOnly
- If MakeReadOnly called first, auto-assign fails on read-only instance
- **Fix:** Add `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` to both Program.cs files

**Non-blocking:** Alias removal, result→resultFilter, arguments.Remove(), existing bindings all correct.

**Tests:** 8 tests cover 7 scenarios + 2 alias-collision regressions.

### PR #87 — CCA Follow-Up Cleanup (#83–#85)
**Real bugs fixed (by Lambert under lockout):**
1. **Alias-removal hole** (McpServerOptionsExtensions.cs:75): Used `continue` on canonical present → alias key never removed. Fix: always remove alias key, skip only canonical-value promotion.
2. **Missing newline** (line 199): Single-unknown path had no trailing `\n` → concatenated "Did you mean: X?Allowed parameters...". Fix: `sb.AppendLine()`.

**Tests added:** 2 new alias-collision regression tests; message-format tests updated with `\n`-transition assertions. (1450 → 1452 passed; 2 skipped).

**CCA cycle pattern:** CCA finds bug → Ripley (author) locked out → Lambert fixes + tests under lockout → Larry reviews CCA second pass → Larry merges.

---

## 2026-06-01 through 2026-06-24: Param Plumbing & Strict-Mode Architecture

### PR #75 — Numeric Alias Coercion (Gap Fix)
**Finding:** Numeric `build_id` values (JSON numbers) fail binding to string parameter `buildIdOrUrl`.
**Fix:** Implement `CoerceToStringElement()` in CallToolFilter; validate upstream value kinds.
**Lesson:** When binding alias parameters, consider jsonElement.ValueKind early. Test all upstream kinds, not just expected types.

### MCP 1.4.0 Bump Safety
Decompiled Microsoft.Extensions.AI.Abstractions 10.5.2 (shared by MCP 1.3.0 and 1.4.0):
- UnmappedMemberHandling.Disallow check gates on `!HasCustomParameterBinding`
- Our tools (all plain value params, no DI) → HasCustomParameterBinding = false → check WOULD run
- No changes to CallToolFilter API, McpException shape, ProtocolTool.InputSchema structure, or alias-normalization paths
- **Bump to 1.4.0 is safe.** Zero migration work required.

---

## Test Architecture Patterns (Reusable)

### `[Theory] + [InlineData]` Contract Test Pattern
Per-param coverage with high test count, low LOC:

**URL construction:**
```csharp
[Theory]
[InlineData("main", "branchName=main")]
[InlineData("refs/heads/main", "branchName=refs%2Fheads%2Fmain")]
public async Task ListBuildsAsync_Branch_AppearsInUrl(string branch, string expectedPart) { }
```

**Cache key discrimination:**
```csharp
[Theory]
[InlineData("main", "develop")]
public async Task ListBuildsAsync_DifferentBranch_DistinctCacheKeys(string b1, string b2) { }
```

### Redundant-Test Removal Heuristic

Test is redundant iff:
1. Tests only a normalization RULE (not the layer's behavior), AND
2. Same rule is now covered by a direct unit test of the shared normalizer

**Safe to remove:** Normalization unit tests if centralizer has own coverage.
**Must keep:** Tests that verify URL construction, cache TTL, cache hit/miss behavior (layer tests, not rule tests).

**Practical rule:** Keep if test would fail after correct normalizer but broken call site. Remove only if would pass by testing normalizer alone.

---

## Prior Work Archive

See `.squad/agents/lambert/history-archive.md` for:
- PR #66–#78 exception handling, parameter standardization, caching patterns
- Cache normalization, exit codes, doc coupling learnings
- Array safety (use IReadOnlyList/FrozenSet, not readonly string[])
- SQLite test flakiness pre-existing issues
- Extensive test patterns and code review feedback cycles

---

## 2026-07-28: helix_find_files workItem schema consistency tests

### Context

User reported hard schema-rejection error: `helix_find_files` was missing `workItem` while all 6 sibling work-item tools had it. Ripley was concurrently implementing the fix.

### Learnings

**Test infrastructure:**
- `McpToolDescriptionTests.cs` is the home for MCP schema/contract tests. It already had `McpServerToolParameters_HaveDiscoverableDescriptions` using reflection over `HelixMcpTools`, `AzdoMcpTools`, `CiKnowledgeTool`. Adding a `[Theory]` with `[InlineData]` for each expected tool is the right extension point for schema consistency tests.
- The existing test class exposes `GetMcpToolMethods()` and `GetToolName()` as private statics — add new tests to the same class to reuse them without changing visibility.
- `HelixMcpToolsTests.cs` is the home for per-tool behavioral tests. Setup pattern: `IHelixApiClient` mock via NSubstitute, `HelixService` + `HelixMcpTools` wired together.

**Reflection-based behavioral tests for anticipated parameters:**
- When testing behavior that depends on a parameter not yet in the codebase, use `method.GetParameters().FirstOrDefault(p => p.Name == "X")` as a guard inside the test. If the parameter is absent, the test fails early with a clear message. If present, the test proceeds with reflection-based invocation using a name-based arg selector (`p.Name switch { ... }`) — this is position-independent and survives Ripley inserting the param at any slot.
- `Task<T>` return types can be cast directly from `method.Invoke(...)` if you know the concrete generic type.

**Parameter ordering hazard:**
- When a new optional parameter is inserted before existing optional parameters in a method, callers using positional args silently bind to the wrong slot (or fail to compile). Two pre-existing tests (`FindFiles_ReturnsValidJsonWithScanResults`, `FindFiles_WildcardPattern_ReturnsAllFiles`) broke this way — `"*.trx"` bound to the new `workItem` slot instead of `pattern`. Fix: use named args (`pattern: "*.trx"`) for all optional params after the first.

**`ScannedItems` adjustment:**
- Ripley's implementation sets `ScannedItems = string.IsNullOrEmpty(workItem) ? maxItems : 1` when workItem is provided. Tests that assert `ScannedItems == 50` remain valid when no workItem is given.

---

## 2026-07-28 — PR #117 guard hardening (workItem shape validation)

### Learnings

**Reflection-based contract tests must assert parameter SHAPE, not just presence.**
Checking `p.Name == "workItem"` only proves the parameter exists; it does not prevent a future tool from declaring `string workItem` (required), `int workItem`, or `string workItem = "default"`, all of which would pass the old guard while still breaking callers. The correct assertion is all three:
1. `p.Name == "workItem"` — parameter exists
2. `p.ParameterType == typeof(string)` — correct CLR type
3. `p.HasDefaultValue && p.DefaultValue is null` — optional with a null default

Each distinct failure path should carry a self-contained message naming the tool and quoting the required declaration (`string? workItem = null`) so the author has an actionable fix without reading the test body.

---

## Known Patterns & Conventions

- **Validation layers:** Validate at user boundary (CLI/MCP) → canonicalize at semantic boundary (cache key, URL) → share algorithm across layers
- **Silent param drop detection:** Audit tool method signature vs REST API capabilities; missing params + missing URL plumbing produce identical symptom
- **Cache key normalization:** Always normalize null/whitespace/defaults to identical representations before hashing
- **External PR reviews:** Clear feedback → merge promptly → file follow-ups ourselves
- **CCA follow-up cycle:** Expects author lockout; fixer can be different agent; ensure fix closes entire bug class, not just test case
- **Schema consistency guard:** Use `[Theory] + [InlineData(toolName)]` in `McpToolDescriptionTests` to explicitly enumerate the set of tools that must share a parameter; fails loudly when a new tool is added without it

## 2026-07-28 — helix_find_files workItem parameter test coverage
Added schema-consistency test (WorkItemScopedHelixTools_HaveOptionalWorkItemParameter) covering 7 work-item-scoped Helix tools. Added 2 behavioral tests for workItem fast path. Fixed 2 pre-existing tests after parameter ordering change. Full suite: 1506 passed. Approved by Dallas; assigned non-blocking cleanup (simplify reflection-based tests, remove stale comments, harden schema test).

## 2026-07-28: PR #117 Review Round — Guard Hardening (lewing-fix-find-files-workitem-param)

### Task
Route final review comment on helix_find_files workItem parameter — hardening schema-consistency guard assertions.

### Fix Applied
**Hardened HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped** reflection guard:
- Added parameter type assertion (`typeof(string)`)
- Added optionality assertion (`HasDefaultValue && DefaultValue is null`)
- Each with distinct, actionable failure message

Prevents future regressions from wrong-type or required parameters slipping through while still violating MCP contract.

**Commit:** 445abcb  
**Test outcome:** 1500/0 failed / 2 skipped  
**Branch:** lewing-fix-find-files-workitem-param

### Lesson: Skill Extraction Timing
**TEAM LESSON (cross-agent):** Skill was extracted mid-session and captured the INTENDED design. Subsequent discovery-based implementation (Lambert) replaced the referenced method with superior pattern, leaving skill pointing at code that never existed. Consider deferring skill extraction until after review completion to capture actual shipped behavior.

---
