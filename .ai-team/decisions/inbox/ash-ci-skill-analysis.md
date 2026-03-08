### 2026-03-08: CI-analysis skill usage patterns and AzDO search recommendations

**By:** Ash

**What:** Deep analysis of how the ci-analysis skill (in `lewing/agent-plugins` and `blazor-playground/copilot-skills`) uses AzDO and Helix tools in practice, with updated recommendations for AzDO search/filter tools based on real usage patterns.

**Why:** The prior AzDO search gap analysis (ash-azdo-search-gaps.md) was based on API surface review. This analysis examines how agents *actually* use these tools during CI failure investigation, revealing specific pain points and new tool ideas that weren't visible from the API alone.

---

## 1. How the CI-Analysis Skill Currently Works

### Tool Call Flow (happy path)

1. **Step 0 — Gather PR context**: GitHub MCP (`pull_request_read`, `list_commits`, `get_file_contents`) to classify PR type (code/flow/backport/merge), read labels, check existing comments
2. **Step 1 — Run Get-CIStatus.ps1**: PowerShell script that queries AzDO timeline, extracts Helix job IDs from build logs, fetches Helix console logs, and produces a `[CI_ANALYSIS_SUMMARY]` JSON
3. **Step 1b — Supplement with MCP**: When script output is insufficient, use `ado-dnceng-public` MCP tools (`get_builds`, `get_build_log`, `get_build_log_by_id`) for additional data
4. **Step 2 — Analyze**: Cross-reference `failedJobDetails` with `knownIssues`, correlate with PR changes, check build progression for multi-commit PRs
5. **Step 3 — Deep dive (if needed)**: Helix MCP tools (`hlx_search_log`, `hlx_search_file`, `hlx_status`, `hlx_test_results`, `hlx_logs`) for individual work item investigation
6. **Step 4 — Binlog analysis (if needed)**: `hlx_download` + `mcp-binlog-tool` for MSBuild-level diagnosis

### Key Design Decisions

- **Script does heavy lifting**: The PowerShell script handles AzDO auth, timeline parsing, Helix URL extraction, and error categorization. Agents are instructed to parse its output first, not re-query.
- **MCP tools as supplements, not primaries for initial scan**: The script can access AzDO and Helix via REST APIs independently. MCP tools fill gaps the script can't cover.
- **Helix search is remote-first**: `hlx_search_log` and `hlx_search_file` are preferred over `hlx_logs` (full download). This was a trained behavior — agents initially defaulted to download-first until explicit "prefer remote search" guidance was added.

### Service Access Stack

| Service | Primary | Fallback |
|---------|---------|----------|
| AzDO builds/timeline | `ado-dnceng-public-*` MCP tools | Script via `Invoke-RestMethod` |
| AzDO build logs | `ado-dnceng-public-pipelines_get_build_log_by_id` | Script via `Invoke-RestMethod` |
| Helix job status | `hlx_status` | Script / `curl` |
| Helix work item errors | `hlx_search_log` (remote search) | `hlx_logs` (full download) |
| Helix artifacts | `hlx_search_file` / `hlx_files` | `hlx_download` |
| GitHub | GitHub MCP tools | `gh` CLI |

---

## 2. Specific Pain Points Where Search/Filter Capabilities Would Help

### Pain Point 1: AzDO Build Log Crawling (P0 — validates prior analysis)

**Evidence (training log session b563ef92):** Agent spent 7+ tool calls crawling AzDO build logs after the script had already provided the error. The agent guessed log IDs by file size instead of searching for error content. Even after training improvements (Change 1), the script's `Get-BuildLog` function still fetches *entire* log content (`Invoke-RestMethod` on the full log endpoint) and then does client-side pattern matching with `Select-String`.

**What happens today:**
- Script fetches full build log via REST API (no size limit, no server-side search)
- Script has `Extract-BuildErrors` function with ~11 regex patterns for common errors (CS/MSB/NU errors, linker errors, etc.)
- Agents sometimes need to look at logs the script didn't fetch (e.g., checkout logs for build progression, step logs for package restore errors)
- When agents use `ado-dnceng-public-pipelines_get_build_log_by_id`, they get raw text — may be 10K+ lines with no way to search

**What `azdo_search_log` would enable:**
- Agent asks "search log 5 of build 1283986 for 'Merge' pattern" → gets 3 matching lines with context (vs downloading 650+ lines of git output)
- Agent asks "search log 565 for 'error'" → gets error lines immediately (vs downloading entire step log)
- The delegation pattern for build progression (Pattern 5 in delegation-patterns.md) explicitly needs to search checkout logs for a merge line around line 500-650 — a search tool would replace the workaround of fetching with `startLine` guessing

### Pain Point 2: Helix Console Log Search Already Works — Parity Gap (P0)

**Evidence (training log Session 3):** After SkillResearcher found that `hlx_search_log` wasn't mentioned in the skill, training added it and achieved 5/5 compliance across all models. Agents now reliably use `hlx_search_log(pattern="error", contextLines=3)` as the first step for Helix investigation.

**The parity gap:** Helix has `hlx_search_log` (pattern search with context lines) and `hlx_search_file` (search uploaded artifacts). AzDO has neither. When the agent needs to search an AzDO build log, it must:
1. Fetch the entire log with `get_build_log_by_id`
2. Pipe through `Select-String` in PowerShell (burns context window with full log content)
3. Or use the script's `Extract-BuildErrors` (only works for patterns the script knows about)

An `azdo_search_log` tool matching `hlx_search_log`'s interface would let agents use the same mental model for both services.

### Pain Point 3: Build Log ID Discovery (P1 — NEW finding)

**Evidence:** The `manual-investigation.md` reference and `build-progression-analysis.md` both mention hardcoded log IDs (`logId: 5` for checkout). The script discovers log IDs by traversing the timeline to find Helix tasks. But when agents need to search a *specific* step's log (e.g., the "Restore" step, or "Send to Helix"), they have no way to find its log ID without fetching the full timeline and scanning for matching record names.

**What would help:** The existing `ado-dnceng-public-pipelines_get_build_log` tool already returns a list of all logs with metadata. But a filter parameter (e.g., `nameFilter` on the timeline/records endpoint) would let agents find the right log ID without processing the full timeline.

### Pain Point 4: AzDO Test Results Name Filtering (P1 — validates prior analysis)

**Evidence:** The skill's `sql-tracking.md` reference has agents creating `failed_jobs` tables to track individual failures. The script extracts `failedJobDetails` from AzDO test runs, but when agents need to search for a specific test name across multiple builds (e.g., for build progression analysis), they must iterate through each build's test results.

**What would help:** A `testNameFilter` on the test results MCP tool. The `build_failures` SQL table pattern (build-progression-analysis.md) queries `SELECT test_name, COUNT(DISTINCT build_id) as fail_count` — this currently requires fetching all failures from each build and inserting into SQL. A server-side name filter would reduce round trips.

### Pain Point 5: Delegation Context Budget (P1 — NEW finding)

**Evidence (training log, delegation patterns):** The skill defines 5 delegation patterns for subagents. Pattern 1 (scanning console logs) and Pattern 4 (parallel artifact extraction) both involve subagents that need to search through content. Subagents run in separate context windows — they can't share the script's output. Each subagent independently fetches logs/artifacts.

**Impact:** When the main agent delegates "search 5 work items for [FAIL] lines" to a subagent, that subagent currently must either:
- Use `hlx_search_log` for each work item (efficient — remote search)
- Fetch full logs with `hlx_logs` for each (context-expensive)

For AzDO build logs, there's no search equivalent. A subagent delegated to "extract target HEAD from build checkout log" must fetch the entire log.

---

## 3. Updated Recommendations for AzDO Search Tools

### P0: `azdo_search_log` — Search within AzDO build step logs

**Priority elevated from prior analysis.** Real skill usage confirms this is the #1 gap.

**Interface (matching `hlx_search_log` for consistency):**
```
azdo_search_log(
    buildId: int,         // AzDO build ID
    logId: int,           // Log ID (from build log list or timeline)
    pattern: string,      // Search pattern (case-insensitive)
    contextLines: int = 2,  // Lines before/after each match
    maxMatches: int = 50    // Limit results
)
```

**Implementation notes:**
- Reuse `SearchLines()` from `HelixService` (already identified in prior analysis)
- AzDO REST API has no server-side search — must fetch log content and search client-side
- Could use `startLine`/`endLine` on `get_build_log_by_id` to paginate large logs, but search is still client-side
- Consider caching fetched logs to avoid re-downloading on repeated searches

**Use cases from real skill patterns:**
1. Build progression: search checkout log for `"HEAD is now at"` merge line
2. Error diagnosis: search step log for `"error"` pattern when script didn't capture it
3. Package restore: search restore log for `"NU1102"` or `"Unable to find package"`
4. Helix job discovery: search "Send to Helix" log for `"Sent Helix Job"` GUIDs

### P1: `azdo_search_timeline` — Search timeline records by name

**New recommendation** (not in prior analysis).

**Interface:**
```
azdo_search_timeline(
    buildId: int,
    nameFilter: string,     // Substring match on record name
    typeFilter: string = null,  // "Job", "Task", "Stage"
    resultFilter: string = null // "failed", "succeeded", "canceled"
)
```

**Use cases:**
1. Finding the log ID for a specific step (e.g., "Checkout", "Restore", "Send to Helix")
2. Filtering to just failed jobs (already partially done — `hlx_status` does this for Helix)
3. Finding Helix-related tasks within a specific job

**Implementation notes:**
- The AzDO timeline API returns all records. Client-side filter is fine.
- This replaces the pattern of fetching full timeline → PowerShell `Where-Object` filtering

### P1: Test results name filter (unchanged from prior analysis)

Add `testNameFilter` parameter to the existing `azdo_test_results` tool.

### P2: Timeline name filter (merged into P1 `azdo_search_timeline` above)

---

## 4. NEW Tool Ideas from Studying the Skill

### NEW P1: `azdo_search_log_across_steps` — Multi-step log search

**Evidence:** The script's `Extract-HelixUrls` function normalizes content across line breaks to find Helix URLs. The build-progression pattern searches multiple steps' logs for different patterns. Currently, agents must know which log ID to search.

**Interface:**
```
azdo_search_log_across_steps(
    buildId: int,
    pattern: string,
    stepNameFilter: string = null,  // Only search steps matching this name
    maxMatches: int = 20
)
```

This would search across all steps in a build (or filtered steps) for a pattern — like `hlx_find_files` scans multiple work items. High value for "find which step contains this error" scenarios.

### NEW P2: `azdo_build_summary` — Structured build failure summary

**Evidence:** The script produces `[CI_ANALYSIS_SUMMARY]` JSON with structured failure data. When MCP tools are the primary access method (no script), agents must piece together build status from timeline + test results + log snippets manually.

**Interface:**
```
azdo_build_summary(
    buildId: int,
    includeHelixJobs: bool = false  // Extract Helix job IDs from logs
)
```

Returns: `{ result, failedJobs: [{name, result, logId, errorSnippet}], canceledJobs: [...] }`

This would replicate the script's core value proposition in a single MCP tool call.

### NEW P2: `azdo_log_error_extract` — Pre-built error extraction

**Evidence:** The script's `Extract-BuildErrors` function has 11 carefully crafted regex patterns for .NET build errors. This domain knowledge (CS errors, MSB errors, NU errors, linker errors, AzDO annotations) could be baked into a tool.

**Interface:**
```
azdo_log_error_extract(
    buildId: int,
    logId: int,
    contextLines: int = 5
)
```

Returns only matching error lines with context — no need for the agent to know patterns. This is a specialized version of `azdo_search_log` with built-in .NET error knowledge.

---

## 5. Cross-Cutting Observations

### The "Remote Search First" Pattern is Proven

Training Session 3 proved that when agents have search tools (`hlx_search_log`), they prefer them over download (`hlx_logs`) — 5/5 across all 3 model families after guidance. This validates the entire `azdo_search_log` approach: agents WILL use search tools if they exist, and it dramatically reduces context consumption.

### 🚨 Blockquote Rules Drive Universal Compliance

The training log validates that format matters more than content depth. Every `🚨` rule in SKILL.md achieved 3/3 model compliance. When we add `azdo_search_log`, the skill should get a `🚨` rule: "Prefer `azdo_search_log` over `get_build_log_by_id` for finding specific content in build logs."

### Script vs MCP Tools Tension

The skill relies on a PowerShell script (Get-CIStatus.ps1, ~2000 lines) for initial data gathering, with MCP tools as supplements. As MCP tools gain search/filter capabilities, the script becomes less necessary. The long-term trajectory is: script handles orchestration logic (multi-step workflow), MCP tools handle individual data access. `azdo_build_summary` (P2) would be a step toward making the script optional for simple investigations.

### `SearchLines()` Extraction is the Key Implementation Step

The prior analysis identified that `SearchLines()` in `HelixService` should be extracted to a shared utility. This analysis confirms the need: both `azdo_search_log` and `azdo_search_log_across_steps` would use the same search logic. Extract to `TextSearchHelper.SearchLines()` in Core, then both Helix and AzDO tools can use it.

---

## Priority Summary (Updated)

| Priority | Tool | Status | Evidence |
|----------|------|--------|----------|
| **P0** | `azdo_search_log` | Confirmed from real usage | Training log session b563ef92, delegation Pattern 5, manual-investigation.md |
| **P1** | `azdo_search_timeline` | NEW — from skill analysis | Timeline traversal in multiple reference docs |
| **P1** | `azdo_search_log_across_steps` | NEW — from delegation patterns | Multi-step search needed for Helix URL discovery |
| **P1** | Test results name filter | Confirmed from real usage | build-progression-analysis.md, sql-tracking.md |
| **P2** | `azdo_build_summary` | NEW — script replacement path | Get-CIStatus.ps1 core function replicated as MCP tool |
| **P2** | `azdo_log_error_extract` | NEW — domain-specific search | Script's `Extract-BuildErrors` patterns |
