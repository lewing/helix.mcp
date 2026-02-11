# hlx — Requirements Document

> Extracted from session 72e659c1 by Ash (Product Analyst), 2025-07-18
> Source materials: plan.md, architecture-idea-binlog-first.md, checkpoints 007/008, cross-session notes, current codebase

---

## A. Implemented Features

The MVP is functional. Six commands ship across CLI and MCP surfaces, backed by a shared Core library.

| Feature | CLI | MCP | Notes |
|---------|-----|-----|-------|
| Job status summary (pass/fail/exit codes) | `status` | `hlx_status` | Parallel detail fetch with SemaphoreSlim(10). Works. |
| Console log download (to temp file) | `logs` | `hlx_logs` | CLI saves file, MCP returns content with tail support. |
| Work item file listing (with binlog/trx tags) | `files` | `hlx_files` | Uses ListFiles endpoint (avoids broken Details URIs). |
| Artifact download (with pattern filter) | `download` | `hlx_download` | ⚠️ Not yet tested end-to-end per plan.md. |
| Binlog scanning across work items | `find-binlogs` | `hlx_find_binlogs` | Sequential scanning, capped at maxItems. |
| LLM agent documentation | `llmstxt` | — | ⚠️ Whitespace bug from raw string literal (identified by Kane). |
| Helix URL + GUID input parsing | All commands | All tools | `HelixIdResolver` — clean, tested, pure function. |

### Implementation Gaps (planned ≠ built)

1. **Positional arguments**: Plan says `hlx status <guid>`, code requires `--job-id`. ConsoleAppFramework's `[Argument]` attribute not yet applied.
2. **`download` untested**: Plan.md explicitly marks it `[ ] download command not yet tested end-to-end`.
3. **`llmstxt` indentation bug**: Raw string literal produces leading whitespace in output (Kane finding).
4. **Console output uses raw `Console.ForegroundColor`**: Spectre.Console is a dependency but unused for rendering (Dallas finding).
5. **Namespace collision**: All three projects share `namespace HelixTool` — confusing, fragile (Dallas finding).
6. **No DI, no testability**: `HelixApi` is `new()`'d directly in three places, zero injection (Dallas P0 finding).
7. **No error handling**: Zero try/catch anywhere — raw exceptions propagate as stack traces (Dallas P0 finding).
8. **No cancellation support**: No `CancellationToken` on any async method.

---

## B. Planned but Not Yet Implemented

These are explicitly listed in plan.md's TODO section or session checkpoint next-steps.

### US-1: Positional Argument Support
**Priority: P1** · **Owner: Ripley**

As a CI investigator, I want to type `hlx status <guid>` without `--job-id` so that my workflow is faster and matches how I think about Helix jobs.

Acceptance criteria:
- [ ] `hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009` works (bare GUID as positional arg)
- [ ] `hlx status https://helix.dot.net/api/jobs/02d8bd09.../details` works (URL as positional arg)
- [ ] `--job-id` still works as a named flag for backwards compat
- [ ] All commands (`logs`, `files`, `download`, `find-binlogs`) accept positional job ID
- [ ] `workItem` is positional in `logs`, `files`, `download`

### US-2: Duration Display in Status Output
**Priority: P1** · **Owner: Ripley**

As a CI investigator, I want to see how long each failed work item took so that I can distinguish slow tests from fast-failing infrastructure issues.

Acceptance criteria:
- [ ] `status` output shows duration for each failed work item
- [ ] Duration is human-readable (e.g., "2m 34s", not raw milliseconds)
- [ ] Duration data comes from the Details endpoint (already fetched, not yet surfaced)

### US-3: Machine Name in Status Output
**Priority: P2** · **Owner: Ripley**

As a CI investigator, I want to see which machine ran a failed work item so that I can identify infra-specific failures (bad machine, disk full, etc.).

Acceptance criteria:
- [ ] `status` output includes machine name for failed work items
- [ ] Machine info comes from the Details endpoint
- [ ] MCP `hlx_status` response includes machine name in the JSON

### US-4: Authentication for Internal Org Builds
**Priority: P2** · **Owner: Ripley**

As a dotnet team member, I want to investigate internal/private Helix jobs so that I can diagnose failures in internal CI pipelines.

Acceptance criteria:
- [ ] Supports token-based auth for non-public Helix API
- [ ] Auth mechanism is consistent with azp's approach (e.g., `azd auth login`)
- [ ] Works for both CLI and MCP server modes
- [ ] Public jobs continue to work without auth
- [ ] Error message when auth is needed but not configured

### US-5: Package as dotnet Tool
**Priority: P1** · **Owner: Ripley**

As a developer, I want to install hlx via `dotnet tool install` so that I don't have to clone the repo to use it.

Acceptance criteria:
- [ ] `dotnet tool install -g hlx` works (or equivalent package name)
- [ ] Published to a NuGet feed (public or dotnet-eng)
- [ ] Tool command name is `hlx` (already configured in csproj PackAsTool)
- [ ] README has install instructions
- [ ] Version is set correctly in csproj

### US-6: Verify Download Command End-to-End
**Priority: P1** · **Owner: Lambert**

As a developer, I want confidence that `hlx download` works on real Helix jobs so that users don't hit silent failures.

Acceptance criteria:
- [ ] Manually tested against a real Helix job with downloadable files
- [ ] Pattern matching works: `*.binlog`, `*.trx`, `*`, specific filename
- [ ] Downloaded files are valid (can be loaded by binlog MCP, opened in VS)
- [ ] Error case: no matching files → clear message (already implemented)
- [ ] Edge case: files in subdirectories (path separator handling)

---

## C. Architectural Requirements

These come from the architecture proposal in `architecture-idea-binlog-first.md` — the vision for how hlx fits into the larger CI diagnosis ecosystem.

### US-7: Layered CI Diagnosis Architecture (azp → hlx → binlog MCP → thin script)
**Priority: P0** · **Owner: Dallas (architecture), Ripley (implementation)**

As a CI investigator using agent-assisted workflows, I want composable CLI tools that each own one layer of the diagnosis stack so that agents can orchestrate them without a monolithic 2200-line script.

Acceptance criteria:
- [ ] Layer 1 (Discovery): `azp status -d 3` provides structured build tree with failed tasks and log IDs
- [ ] Layer 2a (Helix): `hlx` provides status, logs, files, download, binlog scanning
- [ ] Layer 2b (Diagnosis): binlog MCP provides structured errors/warnings via `get_diagnostics`
- [ ] Layer 3 (Correlation): Thin script handles PR correlation, known issues, error categories, JSON summary
- [ ] Each layer is independently useful — no layer requires another to function
- [ ] Migration is incremental per the 5-phase plan

### US-8: "Logs Stay Out of Context" Principle
**Priority: P0** · **Owner: Ripley**

As an LLM agent, I want logs and artifacts saved to temp files (not returned as stdout) so that large content doesn't consume my context window.

Acceptance criteria:
- [ ] CLI `logs` command saves to temp file and prints only the path (already implemented ✅)
- [ ] CLI `download` command saves to temp dir and prints only paths (already implemented ✅)
- [ ] MCP `hlx_logs` returns content with tail support (500 lines default — already implemented ✅)
- [ ] MCP tools never return unbounded content — all have configurable limits
- [ ] Agent skill docs explain when to use CLI (file-based) vs MCP (content-based) modes

### US-9: Script Removability — What hlx Replaces
**Priority: P1** · **Owner: Ash (analysis), Ripley (implementation)**

As a maintainer of ci-analysis, I want to quantify which script sections become redundant with hlx so that we can execute the migration plan.

Acceptance criteria:
- [ ] Helix API functions (~150 lines at lines 1299-1453 in Get-CIStatus.ps1) identified as replaceable
- [ ] Mapping document: each script function → corresponding hlx command
- [ ] Timeline JSON parsing (~lines 600-900) identified as replaceable by `azp status`
- [ ] REST API log fetching (~lines 900-1000) identified as replaceable by `azp logs`
- [ ] What stays in the script is clearly defined: PR correlation, known issues, error categories, JSON summary

---

## D. Discovered Requirements

These weren't explicitly listed as TODOs but are strongly implied by the session discussion, user workflow patterns, or architectural context.

### US-10: Work Item Detail Command
**Priority: P2** · **Owner: Ripley**

As a CI investigator, I want to get detailed information about a specific work item (state, exit code, duration, machine, logs URI) so that I can triage without having to run multiple commands.

Acceptance criteria:
- [ ] New command: `hlx detail <jobId> <workItem>` (or `hlx work-item`)
- [ ] Returns: state, exit code, duration, machine name, console log URI, file count
- [ ] Available as both CLI command and MCP tool
- [ ] Useful when you already know the work item name and want everything about it

### US-11: --json Output Flag for CLI
**Priority: P2** · **Owner: Ripley**

As a power user, I want `hlx status --json` so that I can pipe output to `jq` or other tools.

Acceptance criteria:
- [ ] `--json` flag on all commands that produce structured output (`status`, `files`, `find-binlogs`)
- [ ] JSON format matches MCP tool output (reuse same serialization)
- [ ] Default output remains human-readable with colors
- [ ] JSON output goes to stdout, progress messages stay on stderr (already partially done — `Console.Error.Write` for progress)

### US-12: Dependency Injection and Testability
**Priority: P0** · **Owner: Ripley (implementation), Lambert (tests)**

As a developer, I want HelixService to accept injected dependencies so that I can write unit tests without hitting the real Helix API.

Acceptance criteria:
- [ ] `HelixApi` (or a thin wrapper interface) injected via constructor into `HelixService`
- [ ] `HelixService` registered in DI for both CLI and MCP hosts
- [ ] MCP tools use constructor injection, not static state
- [ ] At least one test exists that uses a mock/fake `HelixApi`
- [ ] `HelixIdResolver` remains a static utility (it's pure, already testable)

### US-13: Error Handling and Input Validation
**Priority: P0** · **Owner: Ripley**

As a user, I want clear error messages instead of .NET stack traces when something goes wrong so that I can fix the problem (wrong job ID, network error, auth needed).

Acceptance criteria:
- [ ] API calls wrapped in try/catch at service level
- [ ] `HttpRequestException` → "Helix API unreachable" or "Job not found" (with HTTP status)
- [ ] Invalid GUID/URL input → clear "Invalid job ID" message (not silent pass-through)
- [ ] `id[..8]` in file paths guarded against short IDs
- [ ] `workItem` validated as non-empty
- [ ] `CancellationToken` added to all async methods

### US-14: TRX Test Results Parsing
**Priority: P3** · **Owner: Ripley**

As a CI investigator, I want to see which tests failed/passed from a TRX file without downloading it and opening Visual Studio so that I can quickly identify the specific failing test methods.

Acceptance criteria:
- [ ] New command: `hlx test-results <jobId> <workItem>` (or `--format` flag on `files`)
- [ ] Parses `.trx` XML for test name, outcome, duration, error message
- [ ] Shows failed tests first with error snippets
- [ ] Available as MCP tool for agent consumption

### US-15: API Response Caching
**Priority: P2** · **Owner: Ripley**

As a user running multiple hlx commands in a debugging session, I want repeated queries to be fast so that I'm not waiting for the same API calls over and over.

Acceptance criteria:
- [ ] In-memory cache for job metadata (4h TTL for completed, 15s for running)
- [ ] Disk cache for downloaded artifacts (7-day expiry, 500MB cap)
- [ ] Console logs bypass cache while job is running (they're append-only streams)
- [ ] `cache clear` command for manual wipe
- [ ] Automatic LRU eviction when disk cache exceeds size limit
- [ ] Cache design follows Dallas's revised TTL matrix decision

### US-16: Retry/Correlation Support for Flaky Test Investigation
**Priority: P3** · **Owner: Ripley**

As a CI investigator diagnosing flaky tests, I want to find the same work item across multiple Helix jobs so that I can see if a failure is consistent or intermittent.

Acceptance criteria:
- [ ] New command: `hlx find-work-item <name> --jobs <id1,id2,...>` (or similar)
- [ ] Shows pass/fail/exit code for the named work item across multiple jobs
- [ ] Useful for answering "does this test always fail or just sometimes?"

### US-17: Namespace and Code Organization Cleanup
**Priority: P1** · **Owner: Ripley**

As a developer, I want distinct namespaces per project and extracted model types so that the codebase is maintainable as it grows.

Acceptance criteria:
- [ ] `HelixTool.Core` uses `namespace HelixTool.Core`
- [ ] `HelixTool.Mcp` uses `namespace HelixTool.Mcp`
- [ ] `WorkItemResult`, `JobSummary`, `FileEntry`, `BinlogResult` extracted to `Models.cs` or `Models/` folder
- [ ] Empty `Display/` folder either used (for Spectre.Console rendering) or removed
- [ ] `JsonSerializerOptions` hoisted to a `static readonly` field (currently allocated 4x in MCP tools)

### US-18: Spectre.Console Integration or Removal
**Priority: P2** · **Owner: Ripley**

As a CLI user, I want polished terminal output (tables, progress bars, proper color support) or for the unused dependency to be removed so that the project isn't carrying dead weight.

Acceptance criteria:
- [ ] Either: Replace `Console.ForegroundColor` with Spectre.Console markup throughout CLI
- [ ] Or: Remove Spectre.Console dependency and keep raw console output
- [ ] If Spectre.Console is used: status output uses a table, find-binlogs shows a progress spinner

---

## E. Requirements from ci-analysis Skill (Primary Consumer)

> Extracted by Ash from the dotnet/runtime ci-analysis skill (SKILL.md, Get-CIStatus.ps1, and all reference docs), 2025-07-18.
> The ci-analysis skill is the PRIMARY CONSUMER of hlx. These requirements reflect what ci-analysis needs from hlx that hlx doesn't yet provide.

### Key insight: ci-analysis reimplements what hlx should own

The ci-analysis script contains ~150 lines of Helix API wrapper functions (lines 1301-1453) that duplicate hlx's core purpose. These functions (`Get-HelixJobDetails`, `Get-HelixWorkItems`, `Get-HelixWorkItemFiles`, `Get-HelixWorkItemDetails`, `Get-HelixConsoleLog`, `Find-WorkItemsWithBinlogs`) are the exact responsibilities hlx was built to replace. But ci-analysis can't adopt hlx yet because hlx is missing several capabilities that the script has.

### US-19: Job Metadata Endpoint (Queue, Source, Creator)
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** Partially overlaps US-10 (work item detail). ci-analysis calls `Get-HelixJobDetails` to show `QueueId` and `Source` (lines 1586-1590). hlx's `status` command fetches job data but doesn't surface these fields.

As a ci-analysis agent, I want hlx to return Helix job metadata (queue ID, source, creator, type, build) so that I can classify the job's context without a separate API call.

Acceptance criteria:
- [ ] `hlx status` output includes: QueueId, Source, Creator, Type, Build fields
- [ ] MCP `hlx_status` JSON response includes job metadata block
- [ ] Metadata is returned alongside the work item summary (single API round-trip to consumer)

### US-20: Work Item State Filtering in Status Output
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** Extends US-2 (duration), US-3 (machine). ci-analysis fetches individual work item details to determine state and exit code because the list API only returns "Finished" (script line 1653-1664).

As a ci-analysis agent, I want hlx status to return state, exit code, duration, and machine for each work item so that I don't need to make N additional API calls to classify failures.

Acceptance criteria:
- [ ] `hlx status` returns per-work-item: Name, State, ExitCode, Duration, MachineName
- [ ] Failed items are clearly distinguished from passed items
- [ ] This data is already fetched via `Details` endpoint in hlx — it just needs to be surfaced in the output
- [ ] MCP response includes structured work item objects, not just pass/fail counts

### US-21: Failure Categorization Support
**Priority: P2** · **Owner: Ripley**
**Cross-ref:** New capability. ci-analysis categorizes failures into: `test-failure`, `build-error`, `test-timeout`, `crash` (exit codes 139/134), `tests-passed-reporter-failed`, `unclassified` (lines 2026-2036). hlx could perform basic categorization from exit codes and log content patterns.

As a ci-analysis agent, I want hlx to provide basic failure categorization so that I can classify errors without parsing raw console logs myself.

Acceptance criteria:
- [ ] Exit code 0 → "passed"
- [ ] Exit code 139 or 134 → "crash"
- [ ] Exit code non-zero + log contains "Timed Out (timeout" → "test-timeout"
- [ ] Exit code non-zero + log contains "Traceback" + log contains "Failures: 0" → "tests-passed-reporter-failed"
- [ ] Default non-zero → "test-failure"
- [ ] Category field included in MCP `hlx_status` per-work-item response
- [ ] CLI shows category tag next to each failed work item

### US-22: Console Log Content Search / Pattern Extraction
**Priority: P2** · **Owner: Ripley**
**Cross-ref:** Extends US-8 (logs stay out of context). ci-analysis's `Format-TestFailure` function (line 1459) parses console logs to extract failure patterns — `[FAIL]` lines, xUnit format, exit codes, timeouts. The delegation pattern docs (Pattern 1) describe scanning multiple logs for test failures. hlx should offer pattern-filtered log content, not just raw tailing.

As a ci-analysis agent, I want hlx to extract structured failure information from console logs so that subagents can gather test failures without dumping full logs into context.

Acceptance criteria:
- [ ] New MCP tool: `hlx_test_failures` — returns parsed test failure names from console log
- [ ] Supports xUnit `[FAIL]` format, NUnit format, MSTest format
- [ ] Returns JSON: `{ "failures": [{ "test": "Namespace.Class.Method", "message": "..." }] }`
- [ ] Falls back to last N lines if no pattern matches (like ci-analysis does)
- [ ] CLI equivalent: `hlx failures <jobId> <workItem>`

### US-23: Multi-Job Batch Status Query
**Priority: P2** · **Owner: Ripley**
**Cross-ref:** New capability. ci-analysis's delegation patterns (Pattern 1, Pattern 4) show agents querying multiple Helix jobs in parallel. Currently each requires a separate `hlx status` invocation. A batch query would reduce MCP round-trips.

As a ci-analysis agent orchestrating subagents, I want to query status for multiple Helix jobs in a single call so that parallel investigation doesn't require N separate MCP tool invocations.

Acceptance criteria:
- [ ] MCP tool `hlx_status` accepts a list of job IDs (comma-separated or array)
- [ ] Returns results for all jobs in a single response
- [ ] Individual job failures don't block results for other jobs (partial results OK)
- [ ] CLI: `hlx status <jobId1> <jobId2> <jobId3>`

### US-24: Artifact Download by Direct URL
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** Extends US-6 (download verification). The binlog-comparison reference doc shows a workflow where ci-analysis extracts a binlog URI from `hlx files` output, then needs to download it. Currently `hlx download` requires jobId + workItem + pattern. Direct URL download would be more convenient for the common "I found a binlog URI, download it" pattern.

As a ci-analysis agent, I want to download a Helix artifact by direct blob storage URL so that I don't have to reverse-engineer the jobId/workItem/pattern from a URL I already have.

Acceptance criteria:
- [ ] `hlx download <url>` works when given a direct blob storage URI
- [ ] File is saved to temp directory, path is returned
- [ ] Works for both CLI and MCP
- [ ] Integrates with existing download path (temp file management, caching)

### US-25: Work Item Console Log URL in Status Output
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** New. ci-analysis constructs console log URLs as `https://helix.dot.net/api/2019-06-17/jobs/$HelixJob/workitems/$WorkItem/console` (line 1624). If hlx's status included these URLs, ci-analysis wouldn't need to construct them.

As a ci-analysis agent, I want hlx status to include the console log URL for each failed work item so that I can immediately fetch or display log links without URL construction.

Acceptance criteria:
- [ ] `hlx status` output includes console log URL for each work item
- [ ] MCP `hlx_status` response includes `consoleLogUrl` per work item
- [ ] URLs use the correct API version path

### US-26: Helix Job ID Extraction from Build Logs
**Priority: P3** · **Owner: Ripley**
**Cross-ref:** New. The binlog-comparison doc describes finding Helix job IDs inside AzDO build logs (via `SendToHelix.binlog`). This is a cross-layer operation (AzDO → Helix) that currently requires manual searching. Future integration with azp could make this seamless, but hlx could expose a "parse Helix job IDs from text" utility.

As a ci-analysis agent, I want to extract Helix job GUIDs from arbitrary text (build logs, binlog output) so that I can bridge from AzDO build artifacts to Helix job queries.

Acceptance criteria:
- [ ] Utility function that extracts Helix job GUIDs from text input
- [ ] Handles both standalone GUIDs and full Helix URLs
- [ ] Available as MCP tool: `hlx_extract_jobs` with text input
- [ ] Returns array of unique job IDs found

### US-27: Work Item Environment Variables
**Priority: P3** · **Owner: Ripley**
**Cross-ref:** New. manual-investigation.md shows extracting DOTNET_* environment variables from console logs (e.g., `DOTNET_JitStress=1`, `DOTNET_GCStress=0xC`) — these are critical for reproducing failures locally.

As a CI investigator, I want hlx to extract environment variable settings from a work item's console log so that I can reproduce failures locally with the correct configuration.

Acceptance criteria:
- [ ] New command/tool: `hlx env <jobId> <workItem>`
- [ ] Parses console log for `DOTNET_*`, `COMPlus_*` environment variable lines
- [ ] Returns structured key-value pairs
- [ ] MCP tool returns JSON: `{ "variables": { "DOTNET_JitStress": "1", ... } }`

### US-28: ListFiles Endpoint Workaround (Already Correct — Document It)
**Priority: P1** · **Owner: Kane (docs)**
**Cross-ref:** US-9 (script removability). ci-analysis explicitly works around the broken Details URI bug (dotnet/dnceng#6072) by using the `ListFiles` endpoint instead (script lines 1331-1354, 1365-1377). hlx already uses `ListFilesAsync` correctly — this is a win that should be documented as a differentiator.

As a developer evaluating hlx vs raw API calls, I want documentation explaining that hlx handles the known dnceng#6072 bug automatically so that I trust hlx over manual API calls for file listing.

Acceptance criteria:
- [ ] llmstxt / README mentions the ListFiles workaround
- [ ] Explains that `hlx files` always works for subdirectory files and unicode filenames
- [ ] References the upstream bug for context

### US-29: MCP Tool Input Flexibility (URL or Components)
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** Extends US-1 (positional args). ci-analysis constructs Helix API URLs inline (e.g., line 1624). MCP consumers may pass either a full Helix URL or a jobId+workItem pair. hlx's `HelixIdResolver` handles URLs→GUIDs, but MCP tools currently require separate `jobId` and `workItem` parameters.

As an MCP consumer, I want hlx MCP tools to accept either a full Helix URL or separate jobId/workItem parameters so that I can pass whichever form I already have.

Acceptance criteria:
- [ ] MCP tools accept `helixUrl` as an alternative to `jobId` + `workItem`
- [ ] `HelixIdResolver` extended to extract work item names from URLs (not just job IDs)
- [ ] Both forms documented in tool descriptions
- [ ] Error message when neither form is provided

### US-30: Structured JSON Output from MCP (Agent-Friendly Format)
**Priority: P1** · **Owner: Ripley**
**Cross-ref:** Extends US-11 (--json flag). ci-analysis emits a `[CI_ANALYSIS_SUMMARY]` JSON block with a specific structure (lines 2196-2254) designed for LLM agent consumption. hlx's MCP tools should emit similarly structured, agent-optimized JSON — not just raw API responses.

As a ci-analysis agent, I want hlx MCP tool responses to be structured for agent consumption (categorized, summarized, actionable) so that I can reason over them without additional parsing.

Acceptance criteria:
- [ ] `hlx_status` returns: `{ jobId, queue, source, summary: { total, passed, failed }, failedItems: [...], passedItems: [...] }`
- [ ] `hlx_files` returns files grouped by type: `{ binlogs: [...], testResults: [...], logs: [...], other: [...] }`
- [ ] `hlx_find_binlogs` includes download URIs in the response (already does this)
- [ ] All MCP responses include a `helixUrl` field for the portal link

---

## Priority Summary

| Priority | Stories | Theme |
|----------|---------|-------|
| **P0** | US-7, US-8, US-12, US-13 | Foundation — architecture, testability, error handling |
| **P1** | US-1, US-2, US-5, US-6, US-9, US-17, US-19, US-20, US-24, US-25, US-28, US-29, US-30 | Usability + ci-analysis integration — metadata, URLs, agent-friendly output |
| **P2** | US-3, US-4, US-10, US-11, US-15, US-18, US-21, US-22, US-23 | Enhancement — categorization, log parsing, batch queries |
| **P3** | US-14, US-16, US-26, US-27 | Future — TRX parsing, flaky tests, env vars, job ID extraction |

### Ownership Map

| Team Member | Stories |
|-------------|---------|
| **Ripley** (implementation) | US-1–5, US-7–8, US-10–16, US-19–27, US-29, US-30 |
| **Lambert** (testing) | US-6, US-12 (test side) |
| **Dallas** (architecture) | US-7 (design review), US-15 (cache design — already decided) |
| **Ash** (analysis) | US-9 (script removability mapping) |
| **Kane** (docs) | US-28 (ListFiles workaround docs), README updates after US-5, llmstxt fix |

---

## Workflow Insights

The user's investigation pattern, as observed across the session:

```
1. Get a failing PR or build URL (from GitHub/AzDO)
2. azp status → identify which jobs/tasks failed, get log IDs
3. azp logs → download build logs, grep for Helix job IDs
4. hlx status → see which Helix work items failed
5. hlx logs → download console log, grep for errors
6. hlx files → find binlogs and test results
7. hlx download *.binlog → get binlogs locally
8. binlog MCP get_diagnostics → structured errors without regex
9. Thin script → PR correlation, known issues, JSON summary
```

This pipeline is the core use case. Every feature should make one of these steps faster or more reliable. The P0 items (testability, error handling) are foundational — everything else is harder to do safely without tests, as Dallas correctly noted.

### ci-analysis Integration Points (from skill analysis)

The ci-analysis skill would invoke hlx at these specific points in its workflow:

```
ci-analysis Step              What hlx replaces                          hlx tool
─────────────────────────────────────────────────────────────────────────────────
Get-HelixJobDetails           Job metadata (queue, source)               hlx_status (US-19)
Get-HelixWorkItems            Work item listing                          hlx_status (US-20)
Get-HelixWorkItemDetails      Per-item state/exit/duration/machine       hlx_status (US-20)
Get-HelixWorkItemFiles        File listing with ListFiles workaround     hlx_files (already works)
Get-HelixConsoleLog           Console log fetch                          hlx_logs (already works)
Find-WorkItemsWithBinlogs     Binlog scanning across items               hlx_find_binlogs (already works)
Format-TestFailure            Failure pattern extraction from logs        hlx_test_failures (US-22)
Exit code categorization      crash/timeout/test-failure classification   hlx_status (US-21)
URL construction              Console log URL per work item               hlx_status (US-25)
```

### Delegation patterns hlx should support

ci-analysis uses subagents for mechanical work. hlx MCP tools are ideal delegation targets:

1. **Parallel log scanning** (Pattern 1): Launch subagent per work item → `hlx_test_failures` → collect JSON failures
2. **Binlog comparison** (Pattern 4): `hlx_files` to find binlogs → `hlx_download` to get them → binlog MCP to analyze
3. **Canceled job recovery**: `hlx_status` on a Helix job from a canceled AzDO build → check if tests actually passed
4. **Baseline comparison**: `hlx_status` on target branch Helix job → compare with PR's Helix job
