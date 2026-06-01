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
