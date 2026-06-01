# Decision: helix.mcp outbound traffic identifier

**Date:** 2026-05-29T20:12:39-05:00  
**Author:** Ripley  
**Status:** Proposed implementation

## Context

arcade-services request logs could not distinguish helix.mcp traffic from other Helix SDK consumers because this tool sent no product-specific identifier. AzDO traffic also only added auth headers.

## Decision

Add one shared `HelixToolUserAgent` helper in `HelixTool.Core` and apply it to every outbound HTTP surface owned by this repo:

- named `AzDO` `HttpClient`
- named `HelixDownload` `HttpClient`
- Helix SDK calls via `HelixApiOptions.AddPolicy(...)`

The identifier is `User-Agent: helix.mcp/{version}` plus `X-Helix-Mcp-Tool: helix.mcp`.

## Consequences

arcade-services can filter logs by either standard User-Agent product or the explicit tool header. The Helix SDK path is covered because `HelixApiOptions` exposes an Azure.Core pipeline policy hook.

# Decision: v0.7.6 Release Shipped ✅

**Date:** 2026-05-29T20:34:23-05:00  
**Released by:** Ripley  
**Status:** Complete

## Summary

v0.7.6 of lewing/helix.mcp shipped successfully to NuGet and GitHub Releases.

### Changes Shipped

1. **PR #73** (akoeplinger): User-Agent identifier + X-Helix-Mcp-Tool custom header
   - Adds `User-Agent: helix.mcp/{version}` header to all outbound AzDO and Helix downloads
   - Adds `X-Helix-Mcp-Tool: helix.mcp` custom header for arcade-services identification
   - Helix SDK calls use `HelixApiOptions.AddPolicy()` to inject headers via per-call pipeline policy
   - Lets arcade-services distinguish hlx traffic for observability

2. **PR #71** (backport of #70): IsCompleted bucketing in GetWorkItemDetailAsync
   - Applies completion-signal pattern to single-item detail path
   - `details.ExitCode.HasValue` determines completion (not `FailureCategory`)
   - Prevents waiting/in-progress work items from being miscounted as failed
   - Mirrors pattern from PR #66 (work-item summary exit code)

### Release Artifacts

- **GitHub Release:** https://github.com/lewing/helix.mcp/releases/tag/v0.7.6
- **NuGet Package:** https://www.nuget.org/packages/lewing.helix.mcp/0.7.6
- **NuGet Asset (attached):** `lewing.helix.mcp.0.7.6.nupkg` (19.7 MB, SHA256: 35641bdd452b295b49e2c87733db5b781f337841bb16675fb50e269367b9967e)

### Build & Test Verification

- Build: 0 errors, 0 warnings (9.70s)
- Tests: 1300 passed, 0 failed, 2 skipped (3s)
- All CI gates green

### Release Commits

| Commit | Message |
|--------|---------|
| 0bc0095 | release: v0.7.6 (version bump) |
| 815c497 | squad: Ripley v0.7.6 release notes |
| v0.7.6  | tag pushed to origin |

### Publish Workflow

- **Run:** https://github.com/lewing/helix.mcp/actions/runs/26670863077
- **Status:** ✅ Completed (46s)
- **Steps:** All green (Pack, Create Release, NuGet login, Push to NuGet)

### Distribution Status

- ✅ GitHub Release created with asset
- ✅ NuGet package pushed successfully
- ✅ Package visible at https://www.nuget.org/packages/lewing.helix.mcp/0.7.6
- ✅ Tool CLI can be updated via `dotnet tool update -g lewing.helix.mcp`

---

**Next steps:** Users can install v0.7.6 via:
```bash
dotnet tool install -g lewing.helix.mcp@0.7.6
# or update existing installation
dotnet tool update -g lewing.helix.mcp
```


---

# MCP Schema Token Cost Measurement — Decision Input

**Date**: 2026-06-01  
**Analyst**: Ash  
**Decision Required By**: Next squad planning session  
**Related Issue**: https://github.com/lewing/helix.mcp/issues/74

---

## Executive Summary

helix.mcp ships **25 MCP tools with an estimated schema footprint of 15.83 KB per `tools/list` call**. GitHub's recent study identified MCP tool schemas as the #1 token inefficiency in agentic workflows, with typical 40-tool servers adding 10–15 KB per turn and 8–12 KB savings achievable through pruning.

We are **in the impact zone** (15.83 KB is at the upper end of GitHub's measured range). However, **actual savings depend on agent workflow patterns**—if `tools/list` is cached per-session (not called per-turn), token impact is minimal.

---

## Measurements

### Absolute Cost
- **Total Tools**: 25
- **Total Schema Bytes**: 16,212 bytes (15.83 KB)
- **Per-Tool Average**: 648.5 bytes
- **Breakdown**:
  - AzDO Tools (14): 8.84 KB
  - Helix Tools (11): 6.99 KB
  - Knowledge Tools (1): 0.39 KB

### Cost Distribution
| Rank | Tool | Bytes | Key Driver |
|------|------|-------|-----------|
| 1 | helix_search | 1,113 | 6 params + 338 char param descriptions |
| 2 | azdo_search_log | 1,089 | 7 params + 256 char param descriptions |
| 3 | azdo_builds | 1,051 | 7 params + 277 char param descriptions |
| 4 | helix_download | 923 | 5 params + 286 char param descriptions |
| 5 | azdo_search_timeline | 917 | 4 params + 284 char param descriptions |
| ... | ... | ... | ... |
| 21 | azdo_build | 456 | 1 param (lean baseline) |
| 22 | helix_ci_guide | 403 | 1 param |
| 23 | azdo_build_analysis | 386 | 1 param |
| 24 | helix_auth_status | 225 | 0 params |
| 25 | azdo_auth_status | 219 | 0 params |

### Cost Driver Analysis
1. **Parameter count dominates**: 7-param tools (azdo_search_log, azdo_builds) cost 560–640 bytes from parameters alone. Adding one parameter ≈ +80 bytes estimated.
2. **Parameter descriptions**: 200–338 chars per verbose tool. Compound effect: 6–7 verbose tools = 1.5–2 KB overhead.
3. **Tool descriptions**: Secondary (87–183 chars; 30–200 bytes each). Could reduce average from 120 → 60 chars for −1.5 KB if all descriptions are migrated elsewhere.

---

## Trimming Potential

### Conservative Scenario (−1 KB)
- Tighten tool descriptions (120 → 80 chars): −600 bytes
- Minor parameter description consolidation: −400 bytes
- **Risk**: LOW
- **Effort**: 3–4 hours

### Moderate Scenario (−2 KB)
- Tool descriptions: −1 KB (120 → 50 chars; requires moving context to UI/docs)
- Parameter consolidation (e.g., standardize filter descriptions): −1 KB
- **Risk**: MEDIUM (may harm discoverability; PR #57 work on description tightening established the baseline)
- **Effort**: 6–8 hours

### Aggressive Scenario (−3 KB)
- Tool descriptions: −1 KB
- Parameter consolidation: −1.5 KB
- Parameter count reduction (e.g., consolidate optional filters on azdo_search_log): −0.5 KB
- **Risk**: HIGH (breaks API contracts; v0.7.x agents expect 7 params on azdo_builds/azdo_search_log)
- **Effort**: 12–16 hours (requires versioning or dual-support)

---

## Decision Points

### 1. Measurement Validation
**Question**: Should we measure actual token cost in live workflows before deciding?

**Rationale**: GitHub's study showed 0% savings in workflows where context was dominated by other content (not schema). Our 15.83 KB is meaningful, but savings only accrue if:
- Agent calls `tools/list` per-workflow-step (not once per session)
- Schema isn't already overshadowed by prompt/context/response content

**Recommendation**: 
- **YES (preferred)**: Instrument live agent runs (via ci-analysis or other high-traffic agents) to measure schema's actual % of total token cost
- **MAYBE**: Accept assumption that schema is non-negligible and proceed with conservative trimming (−1 KB)
- **NO**: Defer; treat as future optimization if token budget becomes tight

### 2. Trim Scope (if proceeding)
**Question**: How much risk are we willing to accept?

**Recommendation**:
- **Conservative (−1 KB)**: Tighten descriptions only; no API changes. Aligns with PR #57 work. Ship in v0.7.8.
- **Moderate (−2 KB)**: Consolidate parameter descriptions (e.g., shared filter enum docs); ship v0.7.9 after validation.
- **Aggressive (−3 KB)**: Requires major version bump and dual-schema support; defer to v0.8.

### 3. Discoverability vs. Trimming
**Question**: Does schema trimming conflict with ongoing work on discoverability (e.g., helix_status → helix_workitems rename)?

**Recommendation**:
- Ongoing work (PR context) scopes v0.7.7 for `helix_status` → `helix_workitems` rename + alias
- Schema trimming (if approved) is orthogonal; can happen in v0.7.8 post-rename
- **Don't postpone discoverability work for schema optimization**

---

## Recommendation

**Primary path (Ash → Dallas)**: 
1. Measure schema's actual token % in live ci-analysis/helix-investigation workflows (1–2 hour instrumentation task; can be done ad-hoc by Dallas or Ripley)
2. If real cost is ≤ 5% of total turn tokens, **defer trimming**; cost is acceptable noise
3. If real cost is > 5%, **approve conservative trim** (−1 KB) for v0.7.8

**Fallback path** (if live measurement is low-value):
- **Conservative trim only** (−1 KB via description tightening): Ship v0.7.8 with Ripley
- Leaves moderate/aggressive trimming for future if budget pressure increases

**Out of scope**:
- Parameter count reduction (high API risk; defer to v0.8)
- Lazy tool scoping (future enhancement; not urgent)

---

## Appendix: Methodology & Caveats

### Estimation Method
Estimated bytes = `len(name) + 20 + len(title) + 20 + len(description) + 30 + (param_count * 80) + len(param_descriptions) + 20`

Where:
- 20–30 byte JSON wrapper overhead per field (JSON key, quotes, commas)
- 80 bytes per parameter (type, name, required, description boilerplate)

**Caveat**: Actual `tools/list` response from SDK may vary ±10%. True size should be measured via `curl` against running server if high precision is needed.

### Tools Analyzed
- **AzDO**: 14 tools (azdo_build, azdo_builds, azdo_timeline, azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_artifacts, azdo_search_log, azdo_search_timeline, azdo_test_attachments, azdo_helix_jobs, azdo_build_analysis, azdo_auth_status)
- **Helix**: 11 tools (helix_status, helix_logs, helix_files, helix_download, helix_find_files, helix_work_item, helix_search, helix_parse_uploaded_trx, helix_batch_status, helix_auth_status)
- **Knowledge**: 1 tool (helix_ci_guide)

### Not Measured
- OutputSchema (response type definitions). MCP SDK may ship these separately from `tools/list`; if they're bundled, actual overhead is higher.
- Runtime schema caching behavior (how often agents request `tools/list`).

---

## Next Actions

**Immediate** (Ash):
- [ ] File tracking issue (DONE: #74)
- [ ] Append to squad history (DONE)

**Short-term** (Squad decision):
- [ ] Decide: Measure live cost OR proceed with conservative trim?
- [ ] If measure: Assign to Dallas or Ripley (1–2 hours)
- [ ] If trim: Assign to Ripley for v0.7.8 planning

**Follow-up** (Ripley, if approved):
- [ ] Implement conservative trim (−1 KB)
- [ ] Validate via testing (PR #XX)
- [ ] Update changelog

---

**Prepared by**: Ash (Analyst)  
**Status**: Ready for squad review  
**Decision owner**: Dallas (PM/Architecture)

---

# Issue #74 Schema Cost — Ground-Truth Analysis

**Filed by**: Ash (Product Analyst)  
**Date**: 2026-06-01T13:54:11.609-05:00  
**Decision owner**: Dallas  
**Status**: Awaiting go/no-go

---

## What Was Measured

The issue's 16,212-byte estimate used a heuristic: `name + title + description + (params × 80) + param_descriptions + JSON wrapper`. The "× 80" per-param multiplier is the flaw — actual per-param JSON overhead is ~42–50 bytes.

Real measurements extracted directly from source:

| Metric | Value |
|---|---|
| Total tools | 25 (confirmed) |
| Tool description chars | 3,181 |
| Parameter count | 39 total |
| Param description chars | 1,866 |
| All text chars (names + titles + descs) | 5,927 |
| **Compact JSON — inputSchema only** | **~11,317 bytes (~11.0 KB)** |
| Issue's estimate | 16,212 bytes |
| Overstatement | ~30–40% |

**Source files measured**:
- `src/HelixTool.Mcp.Tools/Helix/HelixMcpTools.cs` (10 tools)
- `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` (14 tools)
- `src/HelixTool.Mcp.Tools/CiKnowledgeTool.cs` (1 tool)

---

## Critical Gap the Issue Missed

20 of 25 tools have `UseStructuredContent = true` → the SDK emits an `outputSchema` field for each in the `tools/list` response. The issue explicitly did NOT measure this. If outputSchema is significant (structured result types like `AzdoBuild`, `HelixWorkItem`, etc.), the real `tools/list` payload could be **15–25 KB** — meaning the issue's 16.2 KB number might accidentally be close to reality, just via the wrong path.

**The right measurement**: Use `McpServerTool.Create(...)` per tool, serialize each `ProtocolTool` with `McpJsonUtilities.DefaultOptions`, count UTF-8 bytes (per the existing `.squad/skills/mcp-wire-format-trim/SKILL.md`). This is a 2-hour task for Ripley.

---

## Corrected Top 5 Fattest Tools (inputSchema only)

| Rank | Tool | Est. JSON (bytes) | Change from Issue |
|---|---|---|---|
| 1 | helix_search | 861 | Was 1,113 (overstated) |
| 2 | azdo_search_log | 823 | Was 1,089 (overstated) |
| 3 | helix_parse_uploaded_trx | 691 | Not in issue's top-5 |
| 4 | helix_download | 594 | Was 923 (overstated) |
| 5 | helix_logs | 522 | Not in issue's top-5 |

Issue's #3 (`azdo_builds`, 1,051 bytes) actually ranks #7 at 508 bytes. Issue's #5 (`azdo_search_timeline`, 917 bytes) actually ranks #9 at 469 bytes.

---

## Recommendation (for Dallas)

**Do NOT trim before measuring the real `tools/list` payload** (including outputSchema). The GitHub study's key caveat applies directly: schema pruning gives 0% improvement when context is dominated by other content — measurement matters before action.

**Decision criteria**:

| Scenario | Verdict |
|---|---|
| Real `tools/list` > 15 KB **AND** called per-turn (not cached) | **YES — pursue trimming** |
| Real `tools/list` 11–15 KB, tools/list called per-turn | **MAYBE — conservative desc trim only, <1 KB savings** |
| tools/list cached per-session OR context dominated by other payloads | **NO** |

**If YES, trimming priority order** (lowest risk → highest risk):
1. **Description tightening** (no API contract impact) — reuse existing mcp-wire-format-trim skill; potential −0.5–1 KB
2. **Default-annotation audit** — `OpenWorld=true` and other SDK-default annotations are removable noise (per skill Pattern 1); potential −0.1–0.2 KB  
3. **outputSchema opt-out for trivial tools** (Pattern 2 in skill) — only for small primitive-result tools; medium risk
4. **Parameter consolidation** in `azdo_search_log`/`azdo_builds` — **BREAKS v0.7.x API contracts → Dallas decision required before any work begins**

---

## Concrete Next Step

Assign to Ripley: run the `McpServerTool.Create` + `ProtocolTool` serialization measurement (`.squad/skills/mcp-wire-format-trim/SKILL.md` "Measurement" section). Report back total byte count including outputSchema before any trim work is authorized. Estimated effort: 1–2 hours.

---

# Issue #74 — Ground-Truth `tools/list` Byte Measurement ✅

**Date:** 2026-06-01T14:02:01.195-05:00  
**Author:** Ripley  
**Status:** Measurement complete — awaiting Dallas go/no-go

---

## TL;DR

The real `tools/list` JSON payload is **28,941 bytes (28.26 KB)**, not the 16,212-byte estimate in issue #74, and well above Ash's 15–25 KB range. outputSchema alone contributes 8,882 bytes across 20 tools. The issue estimate was wrong for two independent reasons: the per-param heuristic overstated inputSchema by ~30%, AND it ignored outputSchema entirely.

---

## Serialization Path Used

`McpServerTool.Create(MethodInfo, object, null)` → `mcpTool.ProtocolTool` → `JsonSerializer.Serialize(proto, McpJsonUtilities.DefaultOptions)` (compact JSON, UTF-8 byte count). This is the canonical wire path documented in `.squad/skills/mcp-wire-format-trim/SKILL.md`.

Instance requirement: `RuntimeHelpers.GetUninitializedObject(type)` was used to create a schema-only shell for each tool class (no constructor runs, no DI services needed). The test lives in `src/HelixTool.Tests/McpToolsListPayloadTests.cs`.

---

## Measured Numbers

| Metric | Bytes | KB |
|---|---|---|
| **tools/list full payload** | **28,941** | **28.26** |
| inputSchema total | 11,068 | 10.81 |
| outputSchema total | 8,882 | 8.67 |
| input + output total | 19,950 | 19.48 |
| Remaining (names, desc, annotations, wrappers) | 8,991 | 8.78 |

- Total tools: **25**
- Structured (have outputSchema): **20 / 25**

---

## Per-Tool Breakdown (fattest first)

| Rank | Tool | Total | Input | Output | Desc | HasOutput |
|---|---|---|---|---|---|---|
| 1 | azdo_timeline | 2099 | 578 | 1123 | 169 | yes |
| 2 | azdo_search_log | 2027 | 844 | 800 | 146 | yes |
| 3 | azdo_search_timeline | 1929 | 900 | 608 | 183 | yes |
| 4 | helix_parse_uploaded_trx | 1703 | 651 | 656 | 133 | yes |
| 5 | helix_status | 1692 | 370 | 1001 | 99 | yes |
| 6 | helix_search | 1650 | 819 | 418 | 163 | yes |
| 7 | azdo_build | 1531 | 226 | 929 | 152 | yes |
| 8 | azdo_helix_jobs | 1425 | 498 | 550 | 142 | yes |
| 9 | azdo_builds | 1305 | 920 | 68 | 98 | yes |
| 10 | helix_find_files | 1185 | 418 | 398 | 129 | yes |
| 11 | helix_batch_status | 1143 | 207 | 569 | 127 | yes |
| 12 | helix_work_item | 1128 | 344 | 428 | 117 | yes |
| 13 | azdo_build_analysis | 1103 | 226 | 539 | 87 | yes |
| 14 | helix_files | 1058 | 344 | 362 | 121 | yes |
| 15 | azdo_test_attachments | 1048 | 592 | 68 | 147 | yes |
| 16 | helix_download | 1035 | 586 | 93 | 106 | yes |
| 17 | azdo_artifacts | 838 | 412 | 68 | 126 | yes |
| 18 | azdo_test_results | 806 | 417 | 68 | 92 | yes |
| 19 | helix_logs | 806 | 467 | 0 | 127 | no |
| 20 | azdo_log | 727 | 403 | 0 | 126 | no |
| 21 | azdo_test_runs | 788 | 306 | 68 | 185 | yes |
| 22 | azdo_changes | 708 | 306 | 68 | 108 | yes |
| 23 | helix_ci_guide | 479 | 168 | 0 | 108 | no |
| 24 | azdo_auth_status | 362 | 33 | 0 | 97 | no |
| 25 | helix_auth_status | 330 | 33 | 0 | 101 | no |

---

## Reconciliation Against Prior Estimates

| Source | Estimate | Reality | Verdict |
|---|---|---|---|
| Issue #74 heuristic | 16,212 bytes | 28,941 bytes | **Understated by 44%** (two independent errors) |
| Ash static estimate (inputSchema only) | 11,317 bytes | 11,068 bytes | ✅ Accurate (within 2%) |
| Ash range (with outputSchema) | 15,000–25,000 bytes | 28,941 bytes | **Range top was too low by ~16%** |

The issue's 16,212-byte number was wrong because:
1. Its `params × 80` heuristic overstated inputSchema by ~30% (actual is ~42–50 bytes/param, not 80)
2. It completely excluded outputSchema (8,882 bytes for 20 structured tools)

These two errors partially cancelled: overcount on inputSchema + undercount (zero) on outputSchema = net understatement vs. reality.

---

## Decision Criteria (from decisions.md)

Per Ash's framework:

| Scenario | Threshold | Result |
|---|---|---|
| Real > 15 KB AND called per-turn | YES — pursue trimming | **28.26 KB — in scope** |
| 11–15 KB, called per-turn | MAYBE — conservative only | (below actual) |
| Cached per-session OR dominated by other payloads | NO | (unknown; requires live measurement) |

**Measurement verdict: the payload is large enough that trimming is justified IF tools/list is called per-turn.** Dallas still needs to decide whether to measure live caching behavior or proceed directly to conservative trim.

---

## Key Trim Opportunity (not implemented — Dallas decision required)

Top 5 outputSchema contributors (high-value trim targets via Pattern 2 in skill):

| Tool | Output bytes | Note |
|---|---|---|
| azdo_timeline | 1,123 | Largest single outputSchema |
| helix_status | 1,001 | StatusResult type is broad |
| azdo_build | 929 | Build result DTO is wide |
| azdo_search_log | 800 | Structured log result |
| helix_parse_uploaded_trx | 656 | TRX parse output schema |

Dropping outputSchema on these 5 alone (Pattern 2: return `CallToolResult` directly, keep StructuredContent) would save ~4.5 KB. Combined with description tightening (-0.5 KB), a moderate trim could bring total from 28.26 KB → ~23 KB.

---

## Test Artifact

`src/HelixTool.Tests/McpToolsListPayloadTests.cs` — kept as a regression guard. If total payload grows beyond 32 KB, the test will catch it via the sanity assertions.

---

**Prepared by:** Ripley  
**Decision owner:** Dallas

---

# Issue #74 — Schema Trim Verdict

**Date:** 2026-06-01T14:12:04.001-05:00  
**Author:** Dallas (Lead)  
**Status:** DECIDED  
**Related:** Issue #74, decisions.md (Ash analysis + Ripley measurement)

---

## Verdict: CONDITIONAL NO — Do not pursue active trimming now

At 28.26 KB, the `tools/list` payload is non-trivial. But 28 KB is a **cold-load cost**, not a per-turn cost. MCP `tools/list` is called once at session initialization and cached by every major MCP client (GitHub Copilot, Claude Desktop, Cursor, VS Code). No evidence exists that any consumer re-fetches it per-turn.

**28 KB amortized over a session that processes hundreds of KB of build logs, test output, and timeline data is noise.** The GitHub study's own caveat applies directly: schema pruning yields 0% benefit when context is dominated by other content. Our tools routinely return 50–500 KB of build/test data per invocation. The schema is <1% of a typical session's token budget.

**The one fact that flips this decision:** If we discover or a consumer reports that `tools/list` is re-fetched per-turn (not cached), this becomes an immediate YES for the outputSchema lever (8.9 KB, 31% of payload). File a revisit trigger on issue #74.

---

## Lever Ranking (value-vs-risk, best to worst)

### Lever 1: Selective outputSchema removal — HOLD (best future lever)
- **Savings:** 4.5–8.9 KB (16–31% of payload)
- **Risk:** LOW-MEDIUM. Pattern 2 from the SKILL.md preserves StructuredContent in tool-call responses while dropping the schema from `tools/list`. No wire-format change for consumers.
- **Why HOLD:** This is the biggest single lever and the right first move IF we ever need to trim. But today it's solving a problem we don't have. The 5 fattest outputSchemas (azdo_timeline 1,123 B, helix_status 1,001 B, azdo_build 929 B, azdo_search_log 800 B, helix_parse_uploaded_trx 656 B) are the candidates.
- **Back-compat note:** v0.7.x consumers that parse `tools/list` outputSchema for type hints would lose that metadata. No known consumer does this today; outputSchema is informational in MCP spec.

### Lever 2: Description tightening — OPPORTUNISTIC ONLY
- **Savings:** ~0.5–1 KB
- **Risk:** LOW
- **Verdict:** Do this incidentally during normal tool work (renames, new tools), not as a dedicated project. PR #57 already established the baseline. Not worth a standalone PR for 0.5 KB.

### Lever 3: Lazy/scoped tool loading — DEFER TO v0.9+
- **Savings:** Up to 50% if a session only needs Helix OR AzDO tools
- **Risk:** MEDIUM (requires MCP server-side filtering, client negotiation)
- **Verdict:** Architecturally interesting but premature. Our tool count (25) is modest. This becomes valuable at 50+ tools or if we add new tool families. Track as a v0.9 architecture item, not a trimming response.

### Lever 4: Parameter consolidation — REJECTED
- **Savings:** ~0.5 KB
- **Risk:** HIGH — breaks v0.7.x API contracts on azdo_search_log, azdo_builds
- **Verdict:** Not worth it. The savings are tiny relative to the breaking change. Never pursue this for trim reasons alone.

---

## What NOT to do

1. **Do not assign Ripley to any trim implementation work.** There is no approved trim scope.
2. **Do not measure live caching behavior.** Ash recommended this as a gate; I'm overruling it. Every major MCP client caches `tools/list`. Spending 1–2 hours instrumenting something we already know the answer to is waste.
3. **Do not defer other work (renames, new tools) waiting for a trim decision.** This decision is: proceed normally.

---

## Revisit Triggers

This decision flips to YES if ANY of:
1. A consumer (GitHub Copilot, Claude, etc.) is confirmed to re-fetch `tools/list` per-turn
2. Tool count grows past 40 (payload would exceed ~45 KB)
3. An MCP spec change makes outputSchema mandatory or cached differently
4. Token budget pressure is reported by a real user/agent workflow

---

## Assignments (if revisit triggers fire)

| Role | Task |
|------|------|
| Ripley | Implement Pattern 2 (selective outputSchema removal) on top 5 tools |
| Lambert | Regression test: verify tool-call StructuredContent unchanged after outputSchema removal |
| Kane | Update issue #74 with this decision; document outputSchema opt-out pattern |

---

## Issue #74 Comment (for Kane to post)

> **Lead Decision (Dallas, 2026-06-01):** CONDITIONAL NO on schema trimming.
>
> Ground-truth measurement: `tools/list` = 28,941 bytes (28.26 KB). outputSchema is 8.9 KB (31%) — the biggest surprise lever. However, `tools/list` is a cold-load cost cached per-session, not per-turn. At <1% of a typical session's token budget (our tools return 50–500 KB of data per call), trimming solves a problem we don't have today.
>
> **Revisit if:** any consumer re-fetches `tools/list` per-turn, or tool count exceeds 40.
>
> Best available lever when needed: selective outputSchema removal via Pattern 2 (SKILL.md), saving 4.5–8.9 KB with no wire-format breaking change. See `.squad/decisions/inbox/dallas-issue74-trim-verdict.md` for full rationale.

---

**Decision is final unless a revisit trigger fires.**

---

# Proposal: Normalize AzDO `buildIdOrUrl` aliases at MCP call binding

**Date:** 2026-06-01  
**Author:** Ripley  
**Status:** Proposed for Larry/Dallas approval  
**Recommendation:** Add one inbound MCP argument-alias `CallToolFilter` that maps `build_id`, `buildId`, and `buildUrl` to canonical `buildIdOrUrl` before SDK binding. Ship with v0.7.7 if that train is still open.

## Why this is the recommended path

The failure is a wire-format compatibility problem, not an AzDO service parsing problem. Agents supplied a valid build URL/ID under intuitive keys, but the SDK binder rejected the call before `AzdoMcpTools` or `AzdoService` ran.

Option **(b) CallToolFilter normalization** is the best fit:

- **Fixes the actual failure point:** the filter sees `CallToolRequestParams.Arguments` before tool invocation/binding.
- **No schema bloat:** unlike explicit `buildId` / `buildUrl` parameters, aliases stay silent and do not add bytes to `tools/list`.
- **Maintainable:** one central alias table can cover future parameter renames or intuitive spellings.
- **Aligned with team directive:** keeps canonical implementation names while tolerating wire-format drift.

## Confirmed surface

Every `[McpServerTool]` in `src/HelixTool.Mcp.Tools/AzDO/AzdoMcpTools.cs` that takes `buildIdOrUrl`:

| Tool | Method line | Parameter line |
|---|---:|---:|
| `azdo_build` | 27 | 30 |
| `azdo_timeline` | 69 | 72 |
| `azdo_log` | 146 | 149 |
| `azdo_changes` | 161 | 164 |
| `azdo_test_runs` | 173 | 176 |
| `azdo_test_results` | 185 | 188 |
| `azdo_artifacts` | 198 | 201 |
| `azdo_search_log` | 211 | 214 |
| `azdo_search_timeline` | 261 | 264 |
| `azdo_helix_jobs` | 292 | 295 |
| `azdo_build_analysis` | 304 | 307 |

This is broad enough that fixing one method body would be the wrong granularity.

## Existing alias pattern check

The previous AzDO filter work lives in `src/HelixTool.Core/AzDO/AzdoService.cs:14-69`:

```csharp
private static readonly Dictionary<string, string> s_filterAliases = new(StringComparer.OrdinalIgnoreCase)
{
    ["inProgress"] = "running",
    ["in-progress"] = "running",
    ["active"] = "running",
    ["notStarted"] = "pending",
    ["not-started"] = "pending"
};

public static string NormalizeFilter(string filter)
{
    ArgumentNullException.ThrowIfNull(filter);
    return s_filterAliases.TryGetValue(filter, out var canonical) ? canonical : filter;
}
```

That pattern works for **values** after binding has succeeded. It cannot fix `buildUrl` or `build_id`, because those are **argument keys**. When the required key `buildIdOrUrl` is absent, the method body never executes.

A local binder probe against `Microsoft.Extensions.AI.AIFunctionFactory` showed:

- `{ "buildIdOrUrl": "123" }` binds successfully.
- `{ "build_id": "123" }` fails with `ArgumentException`, `ParamName == "arguments"`, missing required `buildIdOrUrl`.
- Unknown extras such as `org`, `project`, or `result` are ignored when required args are present.

## MCP SDK binding findings

ModelContextProtocol 1.3.0 does not expose method/parameter alias metadata in the reflection path used here.

Reflection-confirmed public `McpServerToolAttribute` properties:

```text
Name, Title, Destructive, Idempotent, OpenWorld, ReadOnly,
UseStructuredContent, OutputSchemaType, IconSource, TaskSupport
```

Reflection-confirmed `McpServerToolCreateOptions` has tool-level options (`Name`, `Description`, `Title`, schema/options/metadata), but no per-parameter alias map.

The relevant hook does exist at request-filter level:

- `McpServerOptions.Filters.Request.CallToolFilters` already exists in this repo.
- `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` already installs `AddBindingErrorFilter()` in both startup paths.
- `CallToolRequestParams.Arguments` is mutable, so aliases can be normalized before `next(request, ct)` invokes the tool.

Illustrative implementation shape:

```csharp
private static readonly Dictionary<string, string> s_argumentAliases = new(StringComparer.OrdinalIgnoreCase)
{
    ["build_id"] = "buildIdOrUrl",
    ["buildId"] = "buildIdOrUrl",
    ["buildUrl"] = "buildIdOrUrl",
};

private static void NormalizeArgumentAliases(CallToolRequestParams? parameters)
{
    var args = parameters?.Arguments;
    if (args is null || args.ContainsKey("buildIdOrUrl"))
        return;

    foreach (var (alias, canonical) in s_argumentAliases)
    {
        if (args.TryGetValue(alias, out var value) && !args.ContainsKey(canonical))
        {
            args[canonical] = value;
            return;
        }
    }
}
```

This can either augment the existing binding-error filter before `next(...)`, or become a separate filter registered before the error wrapper. I prefer a named helper in `McpServerOptionsExtensions` so stdio and HTTP paths remain one-line registration points.

## Options considered

### (a) Rename/keep one canonical parameter

Already true: `buildIdOrUrl` accepts both numeric IDs and URLs. The telemetry shows agents do not reliably intuit that exact key, so this does not solve the wire failure.

### (b) Normalize inbound argument dictionaries before binding — recommended

Best balance of compatibility, token cost, and maintainability. The schema stays canonical and compact, while the runtime tolerates intuitive aliases.

### (c) Add explicit optional parameters (`buildId`, `buildUrl`) and coalesce

Not recommended. It would work only after broad method signature changes across 11 tools, adds redundant schema bytes to every affected tool, and creates precedence/validation questions in each method. It also trains callers toward multiple names instead of keeping one canonical schema.

## Release scope recommendation

Ship this in **v0.7.7** alongside the discoverability fixes if there is still room. It is a compatibility/discoverability bugfix caused by real consumer telemetry, and option (b) does not worsen the v0.7.8 schema-trim problem.

Do not wait for v0.7.8 unless v0.7.7 is already frozen. Schema trimming should remain focused on measured `tools/list` reduction; this alias filter is runtime behavior with near-zero schema impact.

## Test expectations for Lambert

Recommended coverage:

1. Filter maps `build_id` to `buildIdOrUrl` when canonical is absent.
2. Filter maps `buildId` and `buildUrl` likewise.
3. Canonical `buildIdOrUrl` wins if both canonical and alias are supplied.
4. Existing binding-error filter still reports missing required params when no canonical or alias exists.
5. An end-to-end MCP tool call for at least `azdo_build_analysis` and `azdo_search_timeline` reaches the service/mock with the normalized value.

## Open questions / risks

- Should alias normalization be global for all tools or scoped to AzDO tool names? I recommend global because `buildIdOrUrl` is currently AzDO-only and the alias table is canonical-key based.
- Should `result` also alias to `resultFilter` for `azdo_search_timeline`? The observed call included `result: 'failed'`, but unknown extras are ignored and the default is already `failed`. Treat as a separate alias decision if telemetry shows non-default `result` values.
- Future SDK versions might add native alias support. If that happens, this filter can be retired or reduced to a compatibility shim.
