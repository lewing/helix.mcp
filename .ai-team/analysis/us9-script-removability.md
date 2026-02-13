# US-9: Script Removability Analysis — What hlx Replaces

> **Author:** Ash (Product Analyst)
> **Date:** 2025-07-18
> **Status:** Complete
> **Cross-refs:** US-19–US-30, requirements.md §E

---

## 1. Function-by-Function Mapping

This section maps each Helix API function in ci-analysis (Get-CIStatus.ps1, lines 1301–1453) to the corresponding hlx command and MCP tool.

### 1a. Fully Replaced Functions

| ci-analysis Function | Script Lines | hlx CLI Command | hlx MCP Tool | Status | Notes |
|---|---|---|---|---|---|
| `Get-HelixJobDetails` | 1301–1330 | `hlx status <jobId>` | `hlx_status` | ✅ **REPLACED** | hlx returns Name, QueueId, Creator, Source, Created, Finished — all fields ci-analysis uses (US-19 DONE). |
| `Get-HelixWorkItems` | 1331–1354 | `hlx status <jobId>` | `hlx_status` | ✅ **REPLACED** | Work item listing is embedded in the status response. Per-item state, exit code, duration, machine, consoleLogUrl, failureCategory all surfaced (US-20 DONE). |
| `Get-HelixWorkItemFiles` | 1365–1377 | `hlx files <jobId> <workItem>` | `hlx_files` | ✅ **REPLACED** | Uses `ListFilesAsync` — correctly avoids dnceng#6072 bug. Output grouped by type (binlogs, testResults, other) per US-30. |
| `Get-HelixWorkItemDetails` | 1379–1414 | `hlx work-item <jobId> <workItem>` | `hlx_work_item` | ✅ **REPLACED** | Returns exit code, state, machine, duration, files, consoleLogUrl, failureCategory. Single API call replaces ci-analysis's per-item detail fetch (US-10 DONE). |
| `Get-HelixConsoleLog` | 1416–1453 | `hlx logs <jobId> <workItem>` | `hlx_logs` | ✅ **REPLACED** | CLI saves to temp file (print path). MCP returns content with `tail` support (default 500 lines). Pattern: "logs stay out of context" (US-8). |
| `Find-WorkItemsWithBinlogs` | 1455–1457 | `hlx find-binlogs <jobId>` | `hlx_find_binlogs` | ✅ **REPLACED** | Scans work items, returns binlog names + URIs. `maxItems` cap (default 30). |

### 1b. Partially Replaced Functions

| ci-analysis Capability | Script Lines | hlx Equivalent | Status | Gap |
|---|---|---|---|---|
| Exit code categorization | 2026–2036 | `FailureCategory` enum + `ClassifyFailure()` in `HelixService` | ✅ **REPLACED** | hlx categorizes: Timeout, Crash, BuildFailure, TestFailure, InfrastructureError, AssertionFailure, Unknown. Included in `hlx_status` and `hlx_work_item` output (US-21 DONE). |
| Console log URL construction | 1624 | `ConsoleLogUrl` field on `WorkItemResult` | ✅ **REPLACED** | hlx constructs `https://helix.dot.net/api/2019-06-17/jobs/{id}/workitems/{name}/console` per work item (US-25 DONE). |
| `Format-TestFailure` (log parsing) | 1459–1547 | `hlx search-log <jobId> <workItem> <pattern>` / `hlx_search_log` | ⚠️ **PARTIAL** | hlx has generic pattern search with context lines. Does NOT have structured test failure extraction (xUnit `[FAIL]`, NUnit, MSTest parsing). See gap G2. |
| Direct URL download | various | `hlx download-url <url>` / `hlx_download_url` | ✅ **REPLACED** | Blob storage URIs from `hlx_files` can be downloaded directly (US-24 DONE). |
| Multi-job status | delegation patterns | `hlx batch-status <id1> <id2> ...` / `hlx_batch_status` | ✅ **REPLACED** | Parallel fetch with SemaphoreSlim(5), failure breakdown (US-23 DONE). |
| URL/GUID input flexibility | various | `HelixIdResolver` + `TryResolveJobAndWorkItem` | ✅ **REPLACED** | MCP tools accept full Helix URLs or bare GUIDs. Work item name extracted from URL when possible (US-29 DONE). |

### 1c. Not Replaced (Stays in ci-analysis)

| ci-analysis Capability | Script Lines | Why hlx Does NOT Replace | Layer |
|---|---|---|---|
| PR discovery via `gh pr checks` | 341–417 | AzDO/GitHub scope — not Helix | Layer 1 (azp) |
| AzDO timeline JSON parsing | 628–720 | AzDO build timeline, not Helix | Layer 1 (azp) |
| AzDO REST API log fetching | 759–907 | AzDO build logs, not Helix data | Layer 1 (azp) |
| Extract-HelixUrls from AzDO logs | 759–907 | AzDO log parsing to find Helix job IDs — cross-layer bridging | Layer 1→2 bridge |
| Build Analysis known issues | 419–492 | Domain-specific database of known build failures | Layer 3 (thin script) |
| PR change correlation | 494–626 | Cross-referencing PR changes with failure patterns | Layer 3 (thin script) |
| Known issue search (GitHub + MihuBot) | 913–1186 | External API integration, not Helix | Layer 3 (thin script) |
| Error categorization at AzDO level | various | Build errors vs Helix test failures — AzDO scope | Layer 3 (thin script) |
| `[CI_ANALYSIS_SUMMARY]` JSON construction | 2196–2254 | Agent-facing summary format — orchestration concern | Layer 3 (thin script) |

---

## 2. Gap Analysis — What ci-analysis Does That hlx Does NOT Yet Cover

### G1: Structured Test Failure Extraction (US-22 — P2, NOT BUILT)

**What ci-analysis does:** `Format-TestFailure` (lines 1459–1547) parses console logs to extract structured test failure information — `[FAIL]` lines (xUnit), NUnit format, MSTest format, exit codes, timeout messages.

**What hlx has:** `hlx_search_log` provides generic substring search with context lines. It can find `[FAIL]` lines but doesn't parse them into `{ test: "Namespace.Class.Method", message: "..." }` structure.

**Gap impact:** Medium. Agents can use `hlx_search_log` with pattern `"[FAIL]"` or `"error"` as a workaround, but they must parse the results themselves. A dedicated `hlx_test_failures` tool would eliminate this parsing burden.

**Blocking migration?** No — ci-analysis can call `hlx_search_log` and post-process. But it's a quality-of-life gap.

### G2: Helix Job ID Extraction from AzDO Build Logs (US-26 — P3, NOT BUILT)

**What ci-analysis does:** `Extract-HelixUrls` (lines 759–907) parses AzDO build logs to find Helix job GUIDs, bridging from Layer 1 (build discovery) to Layer 2 (Helix investigation).

**What hlx has:** `HelixIdResolver` parses Helix URLs/GUIDs from user input, but there's no `hlx_extract_jobs` tool that scans arbitrary text for job GUIDs.

**Gap impact:** Low for hlx directly. This is a cross-layer bridging concern. In the layered architecture, `azp` handles AzDO log discovery and `hlx` handles Helix-native operations. The bridge could live in either tool or in the thin script.

**Blocking migration?** No — ci-analysis already has this logic and can keep it.

### G3: Work Item Environment Variable Extraction (US-27 — P3, NOT BUILT)

**What ci-analysis does:** The manual-investigation reference doc describes extracting `DOTNET_*`, `COMPlus_*` environment variables from console logs for local reproduction.

**What hlx has:** `hlx_search_log` with pattern `"DOTNET_"` can find the lines, but there's no structured key-value extraction.

**Gap impact:** Low. A convenience feature for manual investigation. The workaround (search for `DOTNET_` and parse manually) is adequate.

**Blocking migration?** No.

### G4: TRX Test Results Parsing (US-14 — P3, NOT BUILT)

**What ci-analysis does:** Not directly — but the workflow implies TRX parsing for structured test results.

**What hlx has:** `hlx files` identifies `.trx` files (tagged `[test-results]`) and `hlx download` retrieves them. No TRX XML parsing.

**Gap impact:** Low. TRX parsing is a separate concern that could live in hlx or in a dedicated tool.

**Blocking migration?** No.

### G5: Retry/Correlation for Flaky Tests (US-16 — P3, NOT BUILT)

**What ci-analysis does:** Delegation Pattern 4 (baseline comparison) compares test results across jobs to identify flaky vs consistent failures.

**What hlx has:** `hlx batch-status` queries multiple jobs in parallel. But there's no `hlx find-work-item <name> --jobs <id1,id2>` to track a specific work item across jobs.

**Gap impact:** Low. Agents can use `hlx_batch_status` and filter results by work item name in their own logic.

**Blocking migration?** No.

---

## 3. Coverage Score

### Methodology

The ci-analysis script (Get-CIStatus.ps1) contains approximately **152 lines** of Helix API wrapper functions (lines 1301–1453). These are the core functions that hlx was designed to replace.

Beyond the core API wrappers, additional Helix-adjacent code includes:
- Format-TestFailure / log parsing: ~88 lines (1459–1547)
- URL construction and result formatting: ~50 lines (scattered)
- Exit code categorization: ~10 lines (2026–2036)

### Coverage Calculation

| Scope | Lines in ci-analysis | Replaced by hlx | Coverage |
|---|---|---|---|
| **Core Helix API functions** (Get-HelixJobDetails, Get-HelixWorkItems, Get-HelixWorkItemFiles, Get-HelixWorkItemDetails, Get-HelixConsoleLog, Find-WorkItemsWithBinlogs) | ~152 lines | ✅ All 6 functions fully replaced | **100%** |
| **Exit code categorization** | ~10 lines | ✅ `FailureCategory` enum + `ClassifyFailure()` | **100%** |
| **Console log URL construction** | ~5 lines | ✅ `ConsoleLogUrl` field on every work item | **100%** |
| **Format-TestFailure (log parsing)** | ~88 lines | ⚠️ `hlx_search_log` covers generic search; structured extraction missing | **~40%** |
| **URL construction & result formatting** | ~50 lines | ✅ Structured JSON output, grouped file listing | **90%** |

### Summary Score

| Metric | Value |
|---|---|
| **Core Helix API lines replaceable** | **152 / 152 = 100%** |
| **Extended Helix-adjacent lines replaceable** | **~217 / ~305 = ~71%** |
| **Overall Helix-related code coverage** | **~85%** |
| **Functions fully replaced** | **6 / 6 core + 4 / 5 adjacent = 10 / 11** |
| **Only gap for full replacement** | Structured test failure extraction (US-22) |

The ~88 lines of `Format-TestFailure` are the single remaining gap. Everything else — all 6 core API wrappers, exit code categorization, URL construction, multi-job batch queries, direct URL download, URL/GUID input flexibility — is fully implemented in hlx.

---

## 4. What Stays in ci-analysis

Even after hlx fully replaces the Helix API layer, ci-analysis retains significant responsibilities:

### 4a. MUST KEEP — Core ci-analysis Responsibilities

| Responsibility | Lines | Rationale |
|---|---|---|
| **PR discovery** (`gh pr checks`) | 341–417 | GitHub/AzDO scope. Will migrate to `azp` tool. |
| **AzDO timeline parsing** | 628–720 | Build timeline JSON. Will migrate to `azp` tool. |
| **AzDO REST API log fetching** | 759–907 | Build logs + Helix URL extraction. AzDO scope. |
| **Known issues database** | 419–492 | Domain-specific matching against known CI failures. Thin script responsibility. |
| **PR change correlation** | 494–626 | Cross-referencing file changes with failure areas. Thin script responsibility. |
| **Known issue search** (GitHub + MihuBot) | 913–1186 | External API integration. Thin script responsibility. |
| **AzDO-level error categorization** | various | Build errors vs test failures at the AzDO level. Thin script responsibility. |
| **`[CI_ANALYSIS_SUMMARY]` JSON output** | 2196–2254 | Agent-facing summary format. Orchestration concern — thin script. |

### 4b. Architecture Alignment

Per the layered architecture (US-7, decisions.md):

```
Layer 1 (Discovery):     azp — AzDO pipeline/build/log operations  
Layer 2a (Helix):         hlx — ALL Helix API operations ← FULLY COVERED
Layer 2b (Diagnosis):     binlog MCP — structured build log analysis
Layer 3 (Correlation):    thin script — PR correlation, known issues, JSON summary
```

ci-analysis's **Layer 2a responsibilities** (Helix API calls) are now **100% replaceable** by hlx. The script should:

1. **Remove** all 6 `Get-Helix*` / `Find-*` functions (~152 lines)
2. **Replace** with calls to hlx MCP tools (`hlx_status`, `hlx_files`, `hlx_logs`, `hlx_work_item`, `hlx_find_binlogs`, `hlx_download_url`, `hlx_batch_status`, `hlx_search_log`)
3. **Keep** all Layer 1 (AzDO) and Layer 3 (correlation/known issues) logic

---

## 5. Migration Recommendation

### Phase 1: Immediate Migration (No blockers — can proceed now)

Replace the 6 core Helix API functions with hlx MCP tool calls:

| ci-analysis Function | Replace With | Effort |
|---|---|---|
| `Get-HelixJobDetails` | `hlx_status` (read `.job` from response) | Trivial |
| `Get-HelixWorkItems` | `hlx_status` (read `.failed` + `.passed` arrays) | Trivial |
| `Get-HelixWorkItemDetails` | `hlx_work_item` | Trivial |
| `Get-HelixWorkItemFiles` | `hlx_files` | Trivial |
| `Get-HelixConsoleLog` | `hlx_logs` | Trivial |
| `Find-WorkItemsWithBinlogs` | `hlx_find_binlogs` | Trivial |

**Lines removed:** ~152
**Lines added:** ~30 (MCP tool invocations)
**Net reduction:** ~120 lines

All data that ci-analysis currently extracts from these functions (queue, source, creator, exit code, state, duration, machine, consoleLogUrl, failureCategory) is available in the hlx MCP tool responses.

### Phase 2: Extended Migration (Requires hlx_search_log awareness)

Replace `Format-TestFailure` log parsing with `hlx_search_log`:

| ci-analysis Capability | Replace With | Notes |
|---|---|---|
| `[FAIL]` line extraction | `hlx_search_log` with pattern `"[FAIL]"` | Returns lines + context |
| Timeout detection | Already handled by `failureCategory: "Timeout"` in `hlx_status` | No action needed |
| Exit code classification | Already handled by `FailureCategory` enum | No action needed |

**Lines removed:** ~88
**Lines added:** ~15 (search invocations + light post-processing)
**Net reduction:** ~73 lines

### Phase 3: Optional Enhancements (P3 items — promote if blocking)

| User Story | Current Priority | Promotion Case | Recommendation |
|---|---|---|---|
| **US-22** (Structured test failure extraction) | P2 | Would eliminate the last 40% gap in Format-TestFailure coverage. If ci-analysis agents need structured `{ test, message }` JSON without post-processing. | **Promote to P1 if** ci-analysis subagents consistently need structured failure data and `hlx_search_log` is insufficient. |
| **US-14** (TRX parsing) | P3 | Would add structured test results directly from .trx files. | **Keep at P3.** TRX files can be downloaded and parsed by other tools. Not a migration blocker. |
| **US-16** (Flaky test correlation) | P3 | Would help delegation Pattern 4 (baseline comparison). | **Keep at P3.** `hlx_batch_status` provides adequate workaround. |
| **US-26** (Job ID extraction from text) | P3 | Would bridge Layer 1→2 gap. | **Keep at P3.** This is a cross-layer concern; ci-analysis already handles it. |
| **US-27** (Env var extraction) | P3 | Manual investigation convenience. | **Keep at P3.** `hlx_search_log` with pattern `"DOTNET_"` works. |

### Migration Summary

| Phase | Lines Removed | Lines Added | Net Reduction | Blockers |
|---|---|---|---|---|
| Phase 1 (Core API) | ~152 | ~30 | **~120 lines** | None |
| Phase 2 (Log parsing) | ~88 | ~15 | **~73 lines** | None (hlx_search_log sufficient) |
| Phase 3 (Enhancements) | ~0 | ~0 | Quality improvements | P3 user stories |
| **Total** | **~240** | **~45** | **~195 lines** | **None** |

### Key Finding

**hlx is migration-ready.** All 6 core Helix API functions are fully replaceable today. The `hlx_status` response now includes everything ci-analysis fetches: job metadata (queue, source, creator), per-work-item details (state, exit code, duration, machine), console log URLs, and failure categorization. No user stories need to be promoted to unblock Phase 1 migration.

The only meaningful gap is structured test failure extraction (US-22, P2), which affects ~88 lines of `Format-TestFailure`. This is mitigated by `hlx_search_log` as a workaround and is not a migration blocker.
