# Ash — History (Summarized)

## Current Work (2026-05-28 through 2026-06-01)

### 2026-05-28: Silent MCP Failures Investigation — Issue #67

- Analyzed parameter binding failures in session b11893eb
- Root cause: Microsoft.Extensions.AI.AIFunctionFactory parameter marshalling failures before tool method invocation
- Generated investigation document; fed into Dallas's CallToolFilters middleware policy decision (resolved in PR #69)

### 2026-06-01: MCP Schema Measurement & Token Cost Analysis

**Status**: Complete. Issue filed: https://github.com/lewing/helix.mcp/issues/74

**Measurements** (25 tools):
- **Total schema**: 16,212 bytes (15.83 KB per `tools/list`)
- **AzDO tools (14)**: 8.84 KB
- **Helix tools (11)**: 6.99 KB
- **Knowledge tools (1)**: 0.39 KB

**Top 5 schema cost drivers**:
1. helix_search (1,113 bytes; 6 params + descriptions)
2. azdo_search_log (1,089 bytes; 7 params + descriptions)
3. azdo_builds (1,051 bytes; 7 params + descriptions)
4. helix_download (923 bytes; 5 params + descriptions)
5. azdo_search_timeline (917 bytes; 4 params + descriptions)

**Key finding**: Parameter count and descriptions dominate cost (7-param tools = 560–640 bytes). Conservative trim potential: −1 KB (descriptions only). Aggressive: −3 KB (with API risk).

**Decision pending**: Measure real schema cost in live workflows vs. proceed with conservative trim for v0.7.8.

**Input document**: .squad/decisions/ash-mcp-schema-measurement-2026-06-01.md

---

## Prior Work Summary (2026-02-13 through 2026-05-27)

### Key Audits & Investigations
- **Issue #59 Phase 1 (2026-05-22):** outputSchema + inputSchema deep-dive on top-10 tools. Identified 4 optimization levers (filter enum consolidation, redundant fields, nested structure depth, LimitedResults<T> maturity). Est. recovery: −550–950 tokens (6.7–11.6% of total MCP cost).

- **Slop Audit (2026-05-22):** 28,813 LOC analysis. Found 3 HIGH-severity items (result DTO duplication, catch-throw boilerplate, JSON attribute inconsistency), 1 MEDIUM (Program.cs size), 1 LOW (unused imports). Codebase health: B+. Duplication rate: 0.3%.

- **MCP Tool Description Audit (2026-05-22):** 8 tools flagged for tightening (~69 words recoverable). Ripley executed tightening (136 words recovered, PR #57 merged).

- **Issue #61 (2026-05-25):** Silent MCP failures investigation. Discovered parameter naming inconsistency (buildId vs buildIdOrUrl) and uncaught exception gaps. Ripley fixed both in PR #62 + #64. Calibration learning: Name exceptions by exercising 10-line repro, not source-read.

- **AzDO Security Review (2026-03-08):** STRIDE threat model, 6 findings (1 code fix: query injection), XXE DtdProcessing verification.

- **Requirements Extraction (2026-02-13):** 30 user stories. P0 complete (testability US-12, error handling US-13).

See `history-archive.md` for full details on older work.

---

## Standing Practices

1. **Measurement-first audits** (use gpt-4o tokenizer, not word-count). Prevents regressions like PR #56 growth canceling PR #57 savings.
2. **Field-level breakdown** (outputSchema %, inputSchema %, annotations %) enables early detection of schema drift.
3. **Exception investigation exercise** — reproduce failures in 10-line code before naming exception types.
4. **Concurrent task patterns** — always wrap Task.WhenAll with explicit exception handling (AggregateException vs. TaskCanceledException behavior differs by await vs. .Wait()).

---

### 2026-06-01: Issue #74 Schema Cost — Ground-Truth Analysis

**Status**: Complete. Grounding analysis of issue #74 estimates against real source measurements.

**Key file paths**:
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` — 10 Helix MCP tools (McpServerTool + Description attributes)
- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` — 14 AzDO MCP tools
- `src/HelixTool.Mcp.Tools/CiKnowledgeTool.cs` — 1 CI Knowledge tool (helix_ci_guide)
- `.squad/skills/mcp-wire-format-trim/SKILL.md` — existing skill covering measurement approach (McpServerTool.Create + ProtocolTool serialization)

**Real measurements (from source)**:
- Total tools: 25 (confirmed; issue said 25 ✓)
- Total tool description chars: 3,181
- Total param count: 39 (not the 25×6 the heuristic implied)
- Total param description chars: 1,866
- Grand total description text (tool + param descs): 5,047 chars
- All text (names + titles + descs): 5,927 chars
- **Compact JSON estimate (inputSchema only)**: ~11,317 bytes (~11.0 KB)
- With realistic param name correction: ~11,473 bytes (~11.2 KB)
- **Issue's estimate: 16,212 bytes — OVERSTATED by ~30–40%** (heuristic used 80 bytes/param; reality is ~42–50)
- **Critical gap**: Issue excluded outputSchema entirely. 20/25 tools have `UseStructuredContent=true` → real `tools/list` payload could be 15–20+ KB once outputSchema is included. This means the issue's number could accidentally be closer to reality for the wrong reason.

**Corrected Top 5 Fattest Tools (inputSchema, compact JSON)**:
1. helix_search — 861 bytes (issue had 1,113 — overstated)
2. azdo_search_log — 823 bytes (issue had 1,089 — overstated)
3. helix_parse_uploaded_trx — 691 bytes (not in issue's top-5; rises due to 4 params + 131-char desc)
4. helix_download — 594 bytes (issue had 923 — overstated)
5. helix_logs — 522 bytes (not in issue's top-5; azdo_builds displaced)

**Recommendation** (for Dallas go/no-go):
- At ~11 KB inputSchema only, we're at the LOW end of the GitHub study's impact zone.
- 20/25 tools with outputSchema push the real total higher — measure first via `McpServerTool.Create + ProtocolTool` serialization (see mcp-wire-format-trim skill).
- YES to trimming if real total > 15 KB AND tools/list is per-turn (not cached per-session).
- NO if tools/list is cached per-session or agent context is dominated by other content.
- Risk flag: parameter consolidation in azdo_search_log/azdo_builds would break v0.7.x API contracts → Dallas decision required.
- Concrete next step before any trim: run the measurement from the skill to get ground-truth `tools/list` bytes.

**Decision filed**: `.squad/decisions/inbox/ash-issue74-schema-cost.md`

---

### 2026-06-01: Issue #74 Ground-Truth Measurement (Ripley)

**Status**: Complete (measured by Ripley). Key finding: Ash's inputSchema estimate (11,317 bytes) was accurate within 2%; outputSchema adds 8,882 bytes. Total `tools/list` payload: **28,941 bytes (28.26 KB)**, validating that the payload is in-scope for trimming per the decision criteria (>15 KB if per-turn).

**Ripley's measurement**:
- Full payload: 28,941 bytes (28.26 KB) — 44% larger than issue's heuristic estimate of 16,212 bytes
- inputSchema: 11,068 bytes (10.81 KB) — matches Ash's estimate within 2% ✅
- outputSchema: 8,882 bytes (8.67 KB) — critical discovery; Ash's analysis identified this as the missing piece
- 20/25 tools have `UseStructuredContent=true` (output schemas present)
- Top 5 outputSchema contributors identified: azdo_timeline (1,123 B), helix_status (1,001 B), azdo_build (929 B), azdo_search_log (800 B), helix_parse_uploaded_trx (656 B)

**Serialization path verified**: McpServerTool.Create → ProtocolTool → JsonSerializer.Serialize (canonical wire path per mcp-wire-format-trim skill).

**Test artifact**: `src/HelixTool.Tests/McpToolsListPayloadTests.cs` added as regression guard (triggers if payload > 32 KB).

**Next**: Dallas decision on go/no-go for trimming. Measurement validates Ash's framework; no further analysis needed from Ash until trim work begins.

---

### 2026-06-01: Dallas Verdict on Issue #74 (CONDITIONAL NO)

**Status**: Finalized and merged into decisions.md.

**Key decision**: CONDITIONAL NO on active schema trimming. At 28.26 KB cold-load cached per-session (not per-turn), the payload is <1% of typical session token budget. Trimming solves a problem we don't have today.

**Revisit triggers**: (1) consumer re-fetches per-turn, (2) tool count >40, (3) token budget pressure from real workflows.

**Best available lever when needed**: Pattern 2 (selective outputSchema removal via SKILL.md), saving 4.5–8.9 KB with no breaking change.

Ash's measurement framework validated. Issue #74 closed with Conditional No unless trigger fires.

---

### 2026-06-24: Strict Unknown-Param Rejection Feasibility Analysis

**Status**: Complete. Report delivered to Larry.

**SDK behavior confirmed** (ModelContextProtocol 1.3.0):
- `AIFunctionMcpServerTool.InvokeAsync` copies the caller's arg dict to `AIFunctionArguments` and hands it to `AIFunction.InvokeAsync` (AIFunctionFactory reflection binder). Unknown keys are **silently discarded** — no exception, no log. Confirmed by csharp-sdk Issue #1508 and SDK source (`AIFunctionMcpServerTool.cs`).
- There is **no built-in strict mode** in the SDK. `additionalProperties: false` in the generated JSON Schema is advisory-only (for clients), not enforced at dispatch.
- Parameter names ARE accessible at runtime via `tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()`, enabling a CallToolFilter to build the canonical set without per-tool preamble.

**Two distinct alias types in the codebase** — must not be confused:
1. **Key aliases** (`build_id → buildIdOrUrl`): renamed in `McpServerOptionsExtensions.NormalizeArgumentAliases()` at the CallToolFilter level, BEFORE SDK binding. These must stay in the alias registry for strict-mode to work correctly.
2. **Value aliases** (`inProgress → running`): normalized inside `AzdoService.NormalizeFilter()`, called from tool methods AFTER binding. These are invisible to the strict check (no key mismatch).

**Recommended hook**: Extend the existing `AddBindingErrorFilter` CallToolFilter. After `NormalizeArgumentAliases`, extract canonical param names from `ProtocolTool.InputSchema.properties`, diff against the normalized arg dict, throw `McpException` for unknowns with Levenshtein "did you mean" hint. Tool lookup requires passing the `McpServerOptions.ToolCollection` (or tool-lookup delegate) into the filter.

**Ripley's `fix/azdo-param-plumbing` branch** adds `minTime`, `maxTime`, `queryOrder` to `azdo_builds` — exactly the params callers invent variants of. Strict-mode PR should land **after** this branch merges, not before. Otherwise callers passing `minTime` before the param exists would get a confusing rejection on the correct name.

---

### 2026-06-24 Update: MCP 1.4.0 and UnmappedMemberHandling Investigation

**Status**: Complete. Report updated with follow-up findings.

**Key finding — `UnmappedMemberHandling.Disallow` IS in MEAI 10.5.2 (already available in MCP 1.3.0):**
- Decompiled `Microsoft.Extensions.AI.Abstractions 10.5.2` confirms the strict check is present in `ReflectionAIFunction.InvokeCoreAsync`. Both MCP 1.3.0 and 1.4.0 depend on the same MEAI 10.5.2, so this is NOT a 1.4.0 feature — it was there all along.

**How it works (gating condition):**
- The check fires IFF: `JsonSerializerOptions.UnmappedMemberHandling == Disallow` AND `!HasCustomParameterBinding`
- `HasCustomParameterBinding = true` for any tool where any parameter has a non-null `BindParameter` callback from `ConfigureParameterBinding`. For our tools (all plain value params, no DI injections), `HasCustomParameterBinding = false` → check WOULD run.
- The error thrown is `ArgumentException(paramName: "arguments", message: "The arguments dictionary contains an unexpected key 'X'...")` — which our existing binding-error filter catches (it matches on `ex.ParamName == "arguments"`) and wraps as a `McpException`.

**Alias normalization order (confirmed safe):**
- Our `NormalizeArgumentAliases()` runs at the CallToolFilter level, mutating `Arguments` before `next()` calls `InvokeAsync`. By the time `InvokeCoreAsync` runs its strict check, `build_id` has already been renamed to `buildIdOrUrl` and won't be flagged. Order is correct.

**What this approach does NOT give us:**
- "Did you mean" hint (the message just names the unknown key, no edit-distance suggestion)
- Full allowed-param list in the error message
- Control per-tool (it's all-or-nothing via the serializer options)
- Safety when any tool later gains a DI param (HasCustomParameterBinding would silently disable the check for that tool)

**How to set it (if we wanted to use it):**
- Configure `McpServerToolCreateOptions.SerializerOptions` with `UnmappedMemberHandling = Disallow` when registering tools. This plumbs through via `AIFunctionFactoryOptions.SerializerOptions` to the descriptor. OR mutate a custom `JsonSerializerOptions` instance before it's frozen.
- This is more invasive than the CallToolFilter approach and provides weaker error UX.

**MCP 1.4.0 diff — bump safety:**
- Changes: SSO/auth (IdentityAssertionGrantProvider), InheritEnvironmentVariables on stdio client, session DELETE hardening (user-auth check). Zero changes to `CallToolFilter` API, `McpException` shape, `McpServerTool.Create`, `ProtocolTool.InputSchema` structure, or alias normalization paths.
- `AddBindingErrorFilter`, `NormalizeArgumentAliases`, and test pattern in `McpServerOptionsExtensionsTests.cs` all unaffected.
- **Bump to 1.4.0 is safe.** No migration work required.

### 2026-06-24: MCP Tool Param Surface Audit — PR #78 Kick-off

- Audited 25 MCP tools against underlying REST/SDK capabilities
- Found three parameter plumbing bugs: azdo_builds (minTime/maxTime/queryOrder), azdo_test_attachments (top not forwarded), azdo_test_results (outcomes hardcoded)
- All three shipped in PR #78 after four Copilot review rounds
- Also led Feasibility study on strict unknown-parameter rejection (issues #81–#82)

**Related:** Session log: `.squad/log/2026-06-24-pr78-azdo-param-plumbing-and-followups.md`
**Follow-up:** Issue #82 (architectural cleanup: centralize AzDO filter normalization)

## Rubber-duck: Issue #81 Stage B Levenshtein Threshold (2026-06-24)

**Status**: Complete. Verdict: KEEP threshold 6 (Ripley's choice).

**Findings**:
- Parameter universe: 40 unique MCP tool parameter names across 25 tools
- False-positive candidate pairs at threshold ≤3: 12 (conservative, but breaks regression test)
- False-positive candidate pairs at threshold ≤6: 173 (noisy, but catches minFinishTime→minTime)
- Regression case: Levenshtein('minfinishtime', 'mintime') = 6 (exactly at threshold)
- Threshold 3 misses the regression case; threshold 6 catches it

**Key insight**: The full allowed-params list ALWAYS appears in the error, so false-positive suggestions are harmless—callers can read the list and ignore wrong hints. The regression test requirement (minFinishTime→minTime) overrides the spec's ≤3 constraint.

**Recommendation**: Merge Ripley's implementation. No code changes. Update PR description to clarify spec contradiction: "Spec said ≤3, but regression test requires 6. Threshold 6 is correct; false-positives are mitigated by always-present allowed-params list."

**Output**: Decision filed at .squad/decisions/inbox/ash-pr-stage-b-threshold-review.md

---

## Session Acknowledgment (2026-06-24)

**Thank you** for both the Issue #81 feasibility report and the Stage B threshold review. Both insights directly shaped this session's outcomes:
- Feasibility report validated that strict-mode could land via `UnmappedMemberHandling.Disallow` + a CallToolFilter approach
- Threshold review prevented a false-negative (threshold 3 would miss the regression test requirement)
- Both #81 findings fed Ripley's design, and both #82 contract test matrix and the centralized normalizer pattern reflect the audit lessons learned in your prior work on param plumbing discovery

This session's three merged PRs (83, 84, 85) owe much to the groundwork. Well done.
