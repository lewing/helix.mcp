# hlx CLI Reference

`hlx` is the standalone CLI for [helix.mcp](../README.md) — it works without any MCP server or configuration. It provides direct access to Helix and Azure DevOps CI data from the terminal.

> **Investigation path:** use `hlx test-results` only when the work item uploads structured results to Helix; otherwise pivot to `hlx azdo test-runs` + `hlx azdo test-results`, or `hlx search-log` when the useful signal is only in console output. In MCP mode, `helix_ci_guide(repo)` is the repo-specific entry point when that choice varies by repo.

## Installation

```bash
# Install as a global tool
dotnet tool install -g lewing.helix.mcp

# Or run without installing (requires .NET 10)
dnx lewing.helix.mcp <command>
```

After installation, the `hlx` command is available globally.

> When running from a local build, substitute `dotnet run --project src/HelixTool --` for `hlx`.

## Authentication

### Helix

```bash
hlx login              # Opens browser to token page, stores via git credential
hlx login --no-browser # Skip browser (SSH sessions)
hlx auth-status        # Check current auth status
hlx logout             # Remove stored token
```

Or set `HELIX_ACCESS_TOKEN` environment variable for CI/CD.

### Azure DevOps

Set `AZDO_TOKEN` environment variable, or sign in via Azure CLI (`az login`). Public projects work without auth.

## Helix Commands

### `hlx status <jobId> [failed|passed|all]`

Show work item pass/fail summary for a Helix job. Filter is a positional arg (default: `failed`).

```bash
hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009
hlx status 02d8bd09 all
```

Accepts bare GUIDs, short prefixes, or full Helix URLs:

```bash
hlx status https://helix.dot.net/api/jobs/02d8bd09-9400-4e86-8d2b-7a6ca21c5009/details
```

### `hlx logs <jobId> <workItem>`

Download console log to a temp file and print the path.

```bash
hlx logs 02d8bd09 "dotnet-watch.Tests.dll.1"
```

### `hlx files <jobId> <workItem>`

List uploaded files for a work item, grouped by type (binlogs, test results, other).

```bash
hlx files 02d8bd09 "dotnet-watch.Tests.dll.1"
```

### `hlx download <jobId> <workItem> [--pattern PAT]` or `hlx download --url <url>`

Download work item files to a temp directory, or download a file by direct blob storage URL.

```bash
hlx download 02d8bd09 "dotnet-watch.Tests.dll.1" --pattern "*.binlog"
hlx download --url "https://helix.dot.net/..."
```

### `hlx find-files <jobId> [--pattern PAT] [--max-items N]`

Search across work items for files matching a glob pattern.

```bash
hlx find-files 02d8bd09 --pattern "*.binlog"
hlx find-files 02d8bd09 --pattern "*.dmp" --max-items 10
```

### `hlx work-item <jobId> <workItem>`

Detailed work item info: exit code, state, machine, duration, failure category, uploaded files.

```bash
hlx work-item 02d8bd09 "dotnet-watch.Tests.dll.1"
```

### `hlx batch-status <jobId1> <jobId2> ...`

Status for multiple jobs in parallel with aggregate totals.

```bash
hlx batch-status 02d8bd09 a1b2c3d4 e5f6a7b8
```

### `hlx search-log <jobId> <workItem> <pattern> [--file-name NAME] [--context N] [--max-matches N]`

Search a work item's console log or an uploaded file for lines matching a pattern.

```bash
hlx search-log 02d8bd09 "dotnet-watch.Tests.dll.1" "error CS"
hlx search-log 02d8bd09 "dotnet-watch.Tests.dll.1" "FAIL" --file-name "testhost.log" --context 5 --max-matches 20
```

### `hlx test-results <jobId> <workItem> [--file-name NAME] [--include-passed] [--max-results N]`

Parse Helix-hosted structured test result files and display structured results.

```bash
hlx test-results 02d8bd09 "dotnet-watch.Tests.dll.1"
hlx test-results 02d8bd09 "dotnet-watch.Tests.dll.1" --include-passed
```

## AzDO Commands

### `hlx azdo build <buildId>`

Get details of a specific Azure DevOps build.

```bash
hlx azdo build 12345678
hlx azdo build "https://dev.azure.com/dnceng-public/public/_build/results?buildId=12345678"
```

### `hlx azdo builds [--branch B] [--pr-number N] [--definition-id D] [--status S] [--top N] [--min-time ISO8601] [--max-time ISO8601] [--query-order ORDER]`

List recent builds for a project. Defaults to `dnceng-public/public`.

```bash
hlx azdo builds --branch main
hlx azdo builds --pr-number 12345 --top 5
hlx azdo builds --min-time 2026-06-01T00:00:00Z --max-time 2026-06-24T00:00:00Z --query-order finishTimeDescending
```

`--min-time` and `--max-time` filter the time field determined by `--query-order`. For example, `--query-order finishTimeDescending` means both bounds apply to finish time. Default `--query-order` is `queueTimeDescending`.

### `hlx azdo timeline <buildId> [--filter failed|all]`

Show build timeline (stages, jobs, tasks). Default filter: `failed`.

```bash
hlx azdo timeline 12345678
hlx azdo timeline 12345678 --filter all
```

### `hlx azdo log <buildId> <logId> [--tail-lines N] [--full]`

Get log content for a build log entry. Use log IDs from `timeline` output. Default tail: 500 lines; `--full` fetches the complete log.

```bash
hlx azdo log 12345678 42
hlx azdo log 12345678 42 --tail-lines 100
hlx azdo log 12345678 42 --full
```

### Paging and complete collection

The AzDO list commands `changes`, `test-runs`, `test-results`, `artifacts`, and `test-attachments` support deterministic paging for scanners and offline snapshot population.

AzDO continuation-token list paging follows at most 1000 pages per list request and fails closed with `invalid_response` if the provider repeats a continuation token/request URL or exceeds that cap; partial results are not returned as complete.

**Flags:**

- `--limit N` — Maximum rows to return for this page. Defaults are command-specific: `changes` 20, `test-runs` 50, `test-results` 200, `artifacts` 100, and `test-attachments` 100.
- `--top N` — Compatibility alias for `--limit` on commands that previously accepted `--top`; specify only one of `--limit` or `--top`.
- `--offset N` — Zero-based offset into the complete selected list. Default: `0`.
- `--all` — Return the complete selected list and write/read the complete-list cache key. Mutually exclusive with `--offset`, `--limit`, and `--top`.
- `--allow-truncated` — Keep exit code `0` when a bounded page is truncated. Without it, truncated output exits `2` after writing the usable JSON/human output.

**JSON envelope:**

With `--json`, these commands emit:

```json
{
  "ok": true,
  "results": [],
  "returned": 0,
  "total": 0,
  "offset": 0,
  "limit": 100,
  "complete": true,
  "truncated": false,
  "next": null,
  "cache": {
    "key": "azdo:...",
    "completeKey": "azdo:..."
  },
  "note": null
}
```

Envelope fields:

- `ok` — `true` for successful list output. Hard acquisition failures use the error envelope described in [Errors and exit codes](#errors-and-exit-codes).
- `results[]` — The returned page or complete selected list. Scripts migrating from the old JSON array shape should read `.results`.
- `returned` — Number of rows in `results[]`.
- `total` — Total rows in the complete selected list before paging.
- `offset` — Zero-based offset represented by this response (`0` for `--all`).
- `limit` — Requested page size, or `null` for `--all`.
- `complete` — `true` when this response contains the complete selected list.
- `truncated` — `true` when this response is a bounded page rather than the complete selected list.
- `next` — `{ "offset": N, "limit": N }` when another page exists after this response; otherwise `null`.
- `cache.key` — Backing cache key used to serve/replay this list response. List envelopes currently use the complete selected-list cache key here.
- `cache.completeKey` — Cache key for the complete selected list; currently the same value as `cache.key`. In eval mode, capped MCP/list calls can be served from this complete key.
- `note` — Human-readable truncation guidance, present only when `truncated` is `true`.

Exit codes for these list commands:

| Code | Meaning |
|------|---------|
| `0` | The response is complete, or `--allow-truncated` was supplied. |
| `2` | A bounded response was written with `truncated == true` and `--allow-truncated` was not supplied. Fetch `next`, rerun with `--all`, or opt into legacy success semantics with `--allow-truncated`. |
| `1` | Invalid arguments or hard acquisition/command failure. |

Example generated from a live public AzDO run:

```bash
cd src/HelixTool
DOTNET_ROLL_FORWARD=Major dotnet run -- azdo test-runs 1621192 --limit 1 --json
echo exit=$?
```

```json
{
  "ok": true,
  "results": [
    {
      "id": 44915306,
      "name": "build_linux_x64_checked_CLR_R2R_Tests_ios_arm64-xunit",
      "state": "Completed",
      "totalTests": 49,
      "passedTests": 36,
      "unanalyzedTests": 0,
      "failedTests": 0,
      "incompleteTests": 0,
      "notApplicableTests": 13,
      "startedDate": null,
      "completedDate": null,
      "buildConfiguration": null
    }
  ],
  "returned": 1,
  "total": 49,
  "offset": 0,
  "limit": 1,
  "complete": false,
  "truncated": true,
  "next": {
    "offset": 1,
    "limit": 1
  },
  "cache": {
    "key": "azdo:7af1ee30:dnceng-public:public:testruns:v3:1621192:window:0:1",
    "completeKey": "azdo:7af1ee30:dnceng-public:public:testruns:v3:1621192:all"
  },
  "note": "Showing 1 of 49 from azdo test-runs. Re-run with --all or --offset 1 --limit 1."
}
exit=2
```

### `hlx azdo changes <buildId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated]`

List commits/changes associated with a build. Default limit: 20.

```bash
hlx azdo changes 12345678
hlx azdo changes 12345678 --all
```

### `hlx azdo test-runs <buildId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated]`

List test runs for a build (total, passed, failed counts). Default limit: 50.

```bash
hlx azdo test-runs 12345678
hlx azdo test-runs 12345678 --limit 100 --offset 100
```

### `hlx azdo test-results <buildId> <runId> [--limit N|--top N] [--offset N] [--all] [--allow-truncated] [--outcomes OUTCOMES]`

Get test results for a specific test run. Defaults to failed tests (top 200).

```bash
hlx azdo test-results 12345678 98765
hlx azdo test-results 12345678 98765 --all
hlx azdo test-results 12345678 98765 --outcomes "Passed,Failed"
hlx azdo test-results 12345678 98765 --outcomes NotExecuted
```

`--outcomes` accepts a comma-separated list of AzDO test outcome names (e.g. `Failed`, `Passed`, `NotExecuted`). Default: `Failed`.

### `hlx azdo artifacts <buildId> [--pattern PAT] [--limit N|--top N] [--offset N] [--all] [--allow-truncated]`

List build artifacts. Supports glob-style filtering. Default limit: 100 after `--pattern` filtering.

```bash
hlx azdo artifacts 12345678
hlx azdo artifacts 12345678 --pattern "*.binlog"
hlx azdo artifacts 12345678 --pattern "Logs_Build_*" --all
```

### `hlx azdo search-log <buildId> [--log-id N] [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N]`

Search a specific build log, or omit `--log-id` to search ranked build log steps across the build.

```bash
hlx azdo search-log 12345678 --log-id 42 --pattern "error CS"
hlx azdo search-log 12345678 --pattern "FAIL" --max-matches 30
```

### `hlx azdo search-timeline <buildId> <pattern> [--type Stage|Job|Task] [--result failed|all]`

Search timeline records by name or issue pattern.

```bash
hlx azdo search-timeline 12345678 "test"
hlx azdo search-timeline 12345678 "build" --type Task --result all
```

### `hlx azdo evidence plan <buildId> [--job-results RESULTS] [--artifact-pattern PAT] [--artifact-job-prefix PREFIX] [--keep-attempt-prefix] [--match MODE] [--helix-failure-offset N] [--helix-failure-limit N] [--json]`

Plan failed/canceled job → artifact evidence mapping. Returns a bounded plan with candidate artifacts (if any) for each selected job, ranked by attempt number. It never silently chooses: ambiguous matches retain ranked candidates and report the full candidate count. If the build contains arcade queue-monitor jobs, also parses Helix work-item failures from timeline issues into deterministic `helixFailures[]` rows suitable for fetching with Helix tools.

```bash
# Map failed and canceled jobs to evidence artifacts
hlx azdo evidence plan 12345678

# Map only failed jobs using the default auto strategy (source ID, then name fallback)
hlx azdo evidence plan 12345678 --job-results failed

# Map to artifacts matching a specific prefix, using normalized-exact matching.
# AttemptN_ is stripped by default.
hlx azdo evidence plan 12345678 --artifact-pattern "Logs_Build_*" --artifact-job-prefix "Logs_Build_" --match normalized-exact

# Keep the literal AttemptN_ segment (this is a bare presence flag)
hlx azdo evidence plan 12345678 --keep-attempt-prefix

# Page through Helix monitor failures (when present)
hlx azdo evidence plan 12345678 --helix-failure-limit 50 --helix-failure-offset 0

# Output as JSON
hlx azdo evidence plan 12345678 --json
```

**Parameters:**

- `--job-results RESULTS` — Comma-separated timeline result filter. Default: `failed,canceled`. Allowed: `failed`, `canceled`, `abandoned`, `skipped`, `succeededWithIssues`, `succeeded`, `none`. Case-insensitive. All other values yield an error listing the valid options.

- `--artifact-pattern PAT` — Glob pattern to filter artifacts (e.g., `Logs_Build_*`, `*.binlog`). Default: no filter (all artifacts considered).

- `--artifact-job-prefix PREFIX` — Prefix to strip from artifact names before matching to job names (e.g., `Logs_Build_`). Default: no prefix stripping.

- `--keep-attempt-prefix` — Bare presence flag (it takes no value). Keep `Attempt{N}_` in artifact names after removing `--artifact-job-prefix`. By default this segment is stripped (e.g., `Logs_Build_Attempt1_JobName` → `JobName`), parsed into each candidate's `attempt`, and used for ranking. With this flag, the segment remains part of the name used by name-based matching and the candidate `attempt` property is omitted. The flag is omitted by default.

The MCP equivalent keeps its positive `stripAttemptPrefix` boolean, which defaults to `true`. Thus CLI `--keep-attempt-prefix` is equivalent to MCP `stripAttemptPrefix: false`; omitting either option preserves strip-by-default behavior.

- `--match MODE` — Matching strategy. Default: `auto`.
  - `auto` — Join by artifact `source` (GUID) first, fall back to normalized-exact name matching. **Recommended.** Handles retried jobs correctly and has 0% miss rate on real builds.
  - `source-id` — GUID join only; unmapped jobs reported as `missing`. No fallback.
  - `normalized-exact` — Name-only matching (dotnet/runtime PR #132609 parity). Note: 12.7% miss rate on real builds with matrix variants, and 100% ambiguous on retried jobs. Kept for reproduction/audit purposes.
  - `exact` — Ordinal-ignore-case equality after prefix stripping, with no normalization.

- `--helix-failure-offset N` — Offset into parsed Helix monitor failures (for deterministic collectors). Default: `0`. Used with `--helix-failure-limit` to page through `helixFailures[]` when the total exceeds the limit.

- `--helix-failure-limit N` — Maximum parsed Helix monitor failures to return. Default: `200`, max: `1000`. Any page where `helixFailureTotal > helixFailures.length` is a partial response: `complete` is `false`, `truncated` is `true`, `helixFailuresTruncated` is `true`, `incompleteDetails[].code` includes `helix_failures_truncated`, and the CLI exits `2`. This fail-closed rule also applies to later offset pages, including a final page such as `showing 3-3 of 3`, because that single response does not contain every parsed failure. Collectors should treat exit `2` from Helix-failure paging as "fetch/merge remaining pages" or request a `--helix-failure-limit` greater than or equal to `helixFailureTotal` when they need a single complete response. Helix failures are parsed from timeline issues in arcade queue-monitor jobs; returned only when the monitor job is selected by `--job-results` and its timeline issues contain parseable work-item failures. A monitor job with unparseable failures, unresolved Helix job IDs, or partial paging remains incomplete with explicit `incompleteDetails[].code` reasons.

**Exit Codes:**

| Code | Meaning |
|------|---------|
| `0` | Plan produced and `complete == true`: selected artifact jobs are mapped, monitor failures (if any) are parseable, and no evidence-plan output was truncated. A paged response is complete only when the page itself contains all parsed Helix failures. |
| `2` | Plan produced but `complete == false` because of ambiguous/missing artifacts, truncation, partial Helix-failure paging, unparseable monitor output, or unresolved monitor Helix job IDs. **The bounded plan is still written to stdout.** For paging collectors, treat this as a signal to continue fetching/merging pages or retry with a limit at least as large as `helixFailureTotal`. |
| `1` | Hard error: invalid argument, build not found, timeline unavailable, or network error. |

**Output Structure (JSON):**

Without `--json`, the CLI prints a deterministic human-readable plan. With `--json`, it emits the pretty-printed structured response below; MCP always returns this structure.

- `buildId` — The AzDO build ID (numeric).
- `build` — Build provenance: `buildId`, `buildNumber`, `definitionName`, `definitionId`, `status`, `result`, `sourceBranch`, `sourceVersion`, `finishTime`, `webUrl`, `org`, `project`. PR metadata (if applicable): `prNumber`, `prSourceSha`, `prSourceBranch`, `prIsFork`, `prDraft`, `prProviderId`.
- `buildIncomplete` — `true` if the AzDO build is still running (status not `"completed"`).
- `matchStrategy` — Canonical lowercase form of the effective `--match` value (e.g., `"auto"`, `"source-id"`). Mixed-case input is accepted and emitted in lowercase.
- `jobResultsFilter` — The `--job-results` values that were matched.
- `artifactPattern`, `artifactJobPrefix`, `stripAttemptPrefix` — Echo of the effective input options. `stripAttemptPrefix` is `false` when the CLI receives `--keep-attempt-prefix`.
- `entries[]` — One entry per selected job. Each contains:
  - `jobId`, `jobName` — Timeline record GUID and display name.
  - `jobResult` — The job's result value (e.g., `"failed"`, `"canceled"`).
  - `jobOrder` — Timeline record order (if present).
  - `jobAttempt` — Job attempt number from the timeline (if present).
  - `matchedBy` — Which strategy produced this entry's candidates: `"source-id"`, `"normalized-name"` (from `--match normalized-exact`), `"exact"`, or `null` (missing).
  - `status` — `"mapped"` (exactly one candidate), `"ambiguous"` (multiple), or `"missing"` (zero).
  - `candidates[]` — Up to 10 ranked candidate artifacts. Each carries direct fields: `rank` (0-based), `artifactId`, `artifactName`, `source` (job GUID that published it), `attempt` (parsed from `AttemptN_` when attempt-prefix stripping is enabled, as it is by default; omitted with `--keep-attempt-prefix`), `resourceType`, `downloadUrl`, and `sizeBytes`.
  - `candidateTotal` — Total matching candidates before the returned list was bounded.
  - `candidatesTruncated` — `true` when `candidateTotal` exceeds the number returned in `candidates`.
  - `candidateNote` — Human-readable candidate truncation summary, present when `candidatesTruncated` is `true`.
- `helixFailures[]` — Parsed Helix work-item failures from arcade queue-monitor timeline issues (returned only when a monitor job is selected by `--job-results` and parseable failures are found). **Not artifacts.** Each contains:
  - `monitorJobId`, `monitorJobName` — The AzDO queue-monitor job GUID and name that published the failures.
  - `monitorJobResult` — The monitor job's result (e.g., `"failed"`, `"canceled"`).
  - `monitorJobOrder`, `monitorJobAttempt` — Timeline record order and attempt (if present).
  - `monitorTaskId`, `monitorTaskName` — AzDO task GUID and name that ran the monitor (if available).
  - `helixJobId` — Helix job ID (used by the suggested Helix fetch tools).
  - `helixJobName` — Helix job display name (if parsed).
  - `leg`, `queue` — Helix job leg and queue names (if parsed).
  - `workItem` — Helix work-item name (used with `helixJobId` by work-item-scoped fetches).
  - `state` — Work-item state (e.g., `"Finished"`, `"Active"`).
  - `exitCode` — Work-item exit code (if available).
  - `details` — Raw work-item failure details or error message.
  - `sourceFormat` — Parsing source: `"legacy"` (dotnet arcade v5 format), `"monitor-warning"`, or `"monitor-tree"`.
  - `suggestedFetches[]` — Deterministic drilldown fetch intents for scripts. Each contains:
    - `tool` — MCP/CLI tool name. Emitted values are `"helix_work_item"`, `"helix_logs"`, and `"helix_files"`.
    - `helixJobId` — Job ID to pass to the tool.
    - `workItem` — Work-item name (if applicable for this fetch).
    - `purpose` — Human-readable description of why this fetch is suggested.
- `helixFailureOffset`, `helixFailureLimit` — Echo of paging parameters from `--helix-failure-offset` / `--helix-failure-limit`.
- `helixFailureTotal` — Total parsed Helix monitor failures before paging bounds were applied.
- `helixFailuresTruncated` — `true` when `helixFailureTotal > helixFailures.length`. This marks the current response as a partial page even if the offset is on the last row range; it does not necessarily mean another page exists after the current offset.
- `incompleteDetails[]` — Machine-readable completeness diagnostics (present only if `complete == false`). Each contains:
  - `code` — Stable machine-readable reason:
    - `"artifact_ambiguous"` — A selected artifact job matched multiple candidate artifacts, so none was selected.
    - `"artifact_missing"` — A selected artifact job had no matching artifact candidate.
    - `"candidates_truncated"` — A job's candidate artifact list exceeded the per-entry bound and was truncated.
    - `"entries_truncated"` — Selected artifact jobs exceeded the plan entry bound, so some jobs are not represented.
    - `"helix_failures_truncated"` — Parsed Helix monitor failures exceeded the count returned in the current `--helix-failure-offset`/`--helix-failure-limit` page.
    - `"monitor_unparseable"` — A selected Helix-monitor-like job failed but timeline issues contained no parseable Helix work-item failures.
    - `"monitor_unresolved_job_id"` — Failure-shaped monitor timeline entries were found, but their Helix job ID could not be recovered.
  - `message` — Human-readable explanation.
  - `jobId`, `jobName` — Associated job GUID and name (present for job-specific issues).
  - `count`, `total` — When applicable (e.g., for truncation): count returned, total available.
- `complete` — `true` only when selected artifact jobs are complete (`status == "mapped"`) and `incompleteDetails[]` is empty (no monitor parse/unresolved/truncation diagnostics and no partial Helix-failure page).
- `incompleteReasons[]` — Human-readable lines (present only if `complete == false`) explaining ambiguities, gaps, entry truncation, partial Helix-failure pages, or monitor parsing issues. Human output prints `incompleteDetails[]` as bracketed stable codes, e.g. `- [monitor_unparseable] ...`. Helix paging messages use explicit ranges such as `Helix monitor failures truncated: showing 1-1 of 2.`
- `warnings[]` — Non-fatal planning diagnostics in deterministic order, capped at 10. Always present (empty when there are no warnings).
- `warningTotal` — Total warnings before the 10-item bound. Always present.
- `warningsTruncated` — `true` when `warningTotal` exceeds the number returned in `warnings`; otherwise `false`. Always present.
- `truncated` — `true` if either the 200-entry limit, any entry's 10-candidate limit, or the current Helix failure page is partial (`helixFailureTotal > helixFailures.length`).
- `totalEntries` — Total selected artifact jobs (present when artifact entry/candidate planning was truncated; omitted for Helix-only truncation).
- `note` — Present on truncation; summarizes entry truncation, candidate-list truncation, or Helix failure truncation.
- `generatedAt` — ISO 8601 timestamp when the plan was generated.

**Why `auto` is the default:** Testing on real AzDO builds reveals:
- Normalized-name matching (PR #132609) has a **12.7% miss rate** because the AzDO job display name includes a matrix-leg suffix that the artifact omits (for example, job `linux-arm64 release CrossAOT_Mono crossaot` versus artifact `Logs_Build_Attempt1_linux__arm64_release_CrossAOT_Mono`).
- On retried builds, name matching becomes **100% ambiguous** with the default attempt-prefix stripping — both `Attempt1` and `Attempt2` artifacts collapse to the same key and are both returned.
- GUID joining (`artifact.source == job.id`) has a **100% resolution rate** across real builds and correctly handles retries by construction — each artifact carries the exact job GUID that published it.

Use `--match normalized-exact` only if you need to reproduce or audit against the workflow's existing algorithm.

**Example: Full-stack usage**

```bash
BUILD_ID=12345678

# Create a redaction-safe manifest of stable artifact identifiers.
# This projection intentionally does not echo candidate download URLs.
hlx azdo evidence plan "$BUILD_ID" --job-results failed --json | \
  jq '{complete, truncated, artifacts: [.entries[] | select(.status == "mapped") | {jobId, jobName, artifactId: .candidates[0].artifactId, artifactName: .candidates[0].artifactName, source: .candidates[0].source, attempt: .candidates[0].attempt}]}'

# Find ambiguous mappings (manual resolution needed)
hlx azdo evidence plan "$BUILD_ID" --json | \
  jq '.entries[] | select(.status == "ambiguous")'

# Validate completeness before fetching
hlx azdo evidence plan "$BUILD_ID" --json | jq '.complete'

# Convert parsed Helix failures into suggested Helix fetch commands for a script
hlx azdo evidence plan "$BUILD_ID" --json | \
  jq -r '.helixFailures[] | .suggestedFetches[] | "\(.tool) \(.helixJobId) \(.workItem // "")  # \(.purpose)"' | \
  while read cmd; do echo "# $cmd"; done

# Paging collectors: exit 2 means the response is bounded but usable.
# Merge pages until the collected row count reaches helixFailureTotal,
# or rerun once with --helix-failure-limit >= helixFailureTotal.
hlx azdo evidence plan "$BUILD_ID" --helix-failure-limit 1 --json > page.json
status=$?
if [ "$status" -eq 2 ]; then
  jq '{helixFailureOffset, returned: (.helixFailures | length), helixFailureTotal, incompleteDetails}' page.json
fi
```

### `hlx azdo test-attachments <runId> <resultId> [--org ORG] [--project PROJ] [--limit N|--top N] [--offset N] [--all] [--allow-truncated]`

List attachments for a test result (screenshots, logs, dumps). Defaults: `--org dnceng-public --project public --limit 100`.

```bash
hlx azdo test-attachments 98765 1234
hlx azdo test-attachments 98765 1234 --all
```

## Collect Commands

### `hlx collect azdo-build <build-id-or-url> [options]`

Collect deterministic evidence for an Azure DevOps build into the normal hlx cache (or an isolated cache root), optionally export that cache as a replayable snapshot, and write a manifest describing every fetch, skip, recorded failure, and gap.

```bash
hlx collect azdo-build 12345678 --export /tmp/build-12345678-snapshot
hlx collect azdo-build "https://dev.azure.com/dnceng-public/public/_build/results?buildId=12345678" --manifest collect.json --json
```

The command cannot run when `HLX_EVAL_SNAPSHOT` is set or when caching is disabled with `HLX_CACHE_MAX_SIZE_MB=0`. It populates live cache entries first; `--export` then copies those entries into a snapshot and copies the manifest to `manifest/hlx-collect-manifest.json` inside the snapshot.

**Flags and defaults:**

| Flag | Default | Meaning |
|------|---------|---------|
| `--cache-dir <dir>` | `null` | Cache base directory to populate. Omit to use the normal hlx cache root, except with `--export`, which uses a fresh isolated per-run temporary cache to avoid exporting other builds or auth partitions. |
| `--manifest <path>` | `null` | Manifest output path. When omitted, writes `hlx-collect-manifest.json` inside the effective cache directory, not CWD. |
| `--resume` | `false` | Reuse successful entries from the previous manifest when the referenced cache evidence still exists. |
| `--export <snapshot-dir>` | `null` | Destination snapshot directory. Must not already exist. |
| `--json` | `false` | Print the complete manifest JSON to stdout after collection. |
| `--allow-incomplete` | `false` | Return exit `0` only when incompleteness is limited to policy-allowed skips. |
| `--max-concurrency <int>` | `6` | Maximum concurrent resource fetches. Must be greater than `0`. |
| `--retry-count <int>` | `3` | Total attempts per transient acquisition. Must be greater than `0`. |
| `--retry-kinds <csv>` | `rate_limited,timeout,transport_error` | Acquisition error kinds retried by the collector. Valid values are `not_found`, `access_denied`, `rate_limited`, `timeout`, `transport_error`, `invalid_response`, and `not_in_snapshot`. |
| `--retry-initial-delay <duration>` | `2s` | Initial retry delay. Accepts `TimeSpan` values or suffixes such as `2s`, `5m`, or `1h`. |
| `--retry-max-delay <duration>` | `30s` | Maximum computed exponential-backoff delay. Accepts the same duration formats as `--retry-initial-delay`; provider `retryAfterSeconds` / `Retry-After` delays are honored separately up to 1 hour. |
| `--artifact-pattern <glob>` | `*` | Artifact-name glob used by evidence planning. |
| `--artifact-job-prefix <prefix>` | `null` | Prefix stripped from artifact names before matching. |
| `--keep-attempt-prefix` | `false` | Keep `AttemptN_` in artifact names instead of stripping it. |
| `--match <mode>` | `auto` | Evidence match strategy: `auto`, `source-id`, `normalized-exact`, or `exact`. |
| `--job-results <csv>` | `failed,canceled` | Timeline job results targeted by evidence planning. Empty CSV falls back to `failed,canceled`. |
| `--log-scope <failed\|all\|none>` | `failed` | `failed` collects logs for failed/non-succeeded timeline records, records with issues, and monitor records referenced by Helix failures; `all` collects every build log; `none` records a policy skip. |
| `--test-scope <failed\|all\|none>` | `failed` | `failed` collects all test-run summaries and failed results; `all` collects every supported AzDO outcome, subject to the build-wide result guard; `none` records a policy skip. Attachment selection is independent. Supported outcomes include `Unspecified,None,Passed,Failed,Inconclusive,Timeout,Aborted,Blocked,NotExecuted,Warning,Error,NotApplicable,Paused,InProgress,NotImpacted`. |
| `--max-test-results <long>` | `10000` (implicit) | All-result budget checked against the sum of run `totalTests` before result acquisition, including on resume. Above 10,000 estimated results, supply an explicit budget at least as large as the estimate. An insufficient budget records `test_result_limit`, `complete=false`, and exit 2; `--allow-incomplete` may change policy-only exit status to 0. Failed-only collection is unchanged. |
| `--test-attachment-scope <diagnostic\|all\|none>` | `diagnostic` | Select attachment metadata independently of result scope. Diagnostic selects `Failed,Error,Timeout,Aborted,Inconclusive,Blocked,Warning`; other rows are recorded as aggregate policy exclusions. `all` requires an explicit positive `--max-test-attachments`; `none` excludes all attachment metadata. |
| `--max-test-attachments <long>` | `1000` (implicit) | Build-wide cap on selected attachment-list requests. Eligible rows are selected deterministically by run/result ID. Remaining eligible coverage is recorded as `test_attachment_limit`, `complete=false`, and exit 2 by default. Resume/cache reads count toward selected coverage but avoid additional provider calls. |
| `--helix-scope <suggested\|none>` | `suggested` | `suggested` follows Helix `suggestedFetches[]` from the evidence plan; `none` records a policy skip. |
| `--download-helix-files <glob>` | `null` | Downloads matching Helix uploaded files by glob after metadata is listed. Bytes stream through `--max-file-bytes` and cumulative `--max-total-bytes` caps before cache writes; over-cap files are deleted, recorded as skipped with `size_limit` or `total_size_limit`, and never cached. In-cap files are cached and replay offline. Unmatched files are recorded as policy-excluded skips. |
| `--max-file-bytes <long>` | `52428800` | Per-file byte cap for `--download-helix-files`; files over the cap are skipped with `size_limit`. |
| `--max-total-bytes <long>` | `2147483648` | Total byte cap for `--download-helix-files`; files that would exceed the remaining budget are skipped with `total_size_limit`. |
| `--schema` | `false` | Print the `CollectManifest` JSON schema and exit. |

**Default collection policy:**

By default, the collector gathers build metadata, the full timeline, artifact metadata matching `--artifact-pattern`, the build logs list, the evidence plan, all Helix failure pages from that plan, full AzDO logs for failed/non-succeeded records, records with issues, and monitor records referenced by Helix failures, all test-run summaries, failed test results and their attachment metadata, and the evidence-plan Helix `suggestedFetches[]` (`helix_work_item`, full `helix_logs`, and `helix_files` metadata). The manifest also records source build fields, auth/replay metadata, cache root, policy, summary counts, and every attempt.

For large builds, opt into all result rows explicitly, for example `--test-scope all --max-test-results 2000000`. This does **not** select attachment lists for every passed result: the default diagnostic attachment policy remains in effect. The collector reports run counts, estimated/acquired rows, guard decisions, attachment completion/exclusion/limit counts, and five-second elapsed heartbeats during long acquisitions to stderr. `--json` prints only the final manifest to stdout.

By default, it does not download AzDO artifact ZIP/file bytes, binlogs, dumps, arbitrary Helix uploaded-file bytes, parsed TRX/xUnit derived summaries, search-result precomputations, MCP cursor/window variants, or any live fallback during offline replay. Helix uploaded-file metadata can still be collected through `helix_files`; pass `--download-helix-files <glob>` to cache and replay matching uploaded-file bytes offline.

**Manifest schema:**

The manifest is stable, versioned JSON with top-level fields `schemaVersion`, `kind`, `manifestId`, `hlxVersion`, `generatedAt`, `completedAt`, `command`, `source`, `auth`, `policy`, `cache`, `snapshot`, `complete`, `exitCode`, `incompleteDetails`, `summary`, and `attempts`. `kind` is `hlx.collect.azdo-build`; `schemaVersion` is `1`.

Each `attempts[]` entry has `id`, optional `parentId`, `phase`, `required`, `provider`, `operation`, `resource`, optional `cacheKey`, optional `completeCacheKey`, `startedAt`, `finishedAt`, `durationMs`, `attemptCount`, `outcome`, nullable `bytes`, optional `sha256`, optional `paging`, optional `error`, and optional `skip`. `outcome` is one of:

- `ok` — Fetch/read succeeded for the requested policy.
- `cached` — `--resume` reused a previous `ok`/`cached` attempt after verifying the cache key still exists.
- `recorded_failure` — A non-retried provider failure was recorded in negative-cache form and contributes to incompleteness when required.
- `failed` — A retried/transient acquisition exhausted the retry budget or an acquisition failed without a recordable provider error.
- `skipped` — The resource was intentionally not fetched by policy or cap.

`paging`, when present, contains `returned`, nullable `total`, `offset`, nullable `limit`, `complete`, `truncated`, and `next`. `skip.kind` is emitted as `policy_excluded`, `not_selected`, `size_limit`, `total_size_limit`, `test_result_limit`, or `test_attachment_limit`. Test-volume skips include requested counts, effective budgets, and remediation text; attachment exclusions are aggregated per run rather than adding one manifest row for every passed test. `policy.caps` records effective `maxTestResults` and `maxTestAttachments`, `policy.maxTestResultsExplicit` distinguishes consent from the default, and `policy.testAttachmentScope` records selected coverage. These additive fields preserve older manifest readability. `bytes` is `null` for skips. `error` reuses the `AcquisitionError` shape from [Errors and exit codes](#errors-and-exit-codes): `kind`, `provider`, `operation`, `resource`, optional `httpStatus`, optional `retryAfterSeconds`, optional `source`, optional `replayed`, optional `recordedAt`, and `message`.

`summary` contains `attempted`, `ok`, `cached`, `recordedFailure`, `failed`, `skipped`, and `bytes`. `auth.azdo.cachePartition` is the non-secret replay partition (`public` or `cache-xxxxxxxx`); `auth.azdo.replay` is `public` or `snapshot_partition`; `auth.helix.path` is `anonymous`, `environment`, or `stored-credential`. `snapshot.manifestPath` is `manifest/hlx-collect-manifest.json` when `--export` succeeds; the standalone manifest path is the `--manifest` value or `hlx-collect-manifest.json` inside the effective cache directory.

Before writing the final manifest/export result, the collector re-reads every `ok`/`cached` cache entry that has a `cacheKey`. Missing metadata, empty/corrupt raw AzDO log rows, and byte-count mismatches are downgraded to failed cache verification and make the manifest incomplete; missing Helix artifact evidence is reported as `artifact_missing`, while corrupt/size-mismatched evidence is reported as `fetch_failed` with `provider: "cache"`.

Trimmed real manifest example, generated from public build `1621192` with `--log-scope none --test-scope none --helix-scope none`:

```json
{
  "schemaVersion": 1,
  "kind": "hlx.collect.azdo-build",
  "source": {
    "provider": "azdo",
    "org": "dnceng-public",
    "project": "public",
    "buildId": 1621192,
    "definitionName": "runtime",
    "status": "completed",
    "result": "failed"
  },
  "auth": {
    "azdo": {
      "path": "AzureCliCredential",
      "cachePartition": "cache-7af1ee30",
      "replay": "snapshot_partition"
    },
    "helix": {
      "path": "anonymous"
    }
  },
  "policy": {
    "requiredOperations": [
      "azdo_evidence_plan",
      "get_build",
      "get_build_log",
      "get_timeline",
      "helix_suggested_fetches",
      "list_artifacts",
      "list_build_logs",
      "list_test_runs"
    ],
    "maxConcurrency": 6,
    "retry": {
      "maxAttempts": 3,
      "kinds": ["rate_limited", "timeout", "transport_error"],
      "initialDelay": "PT2S",
      "maxDelay": "PT30S"
    },
    "logScope": "none",
    "testScope": "none",
    "helixScope": "none"
  },
  "snapshot": {
    "exported": false,
    "validated": false,
    "validationErrors": [],
    "validationWarnings": []
  },
  "complete": true,
  "exitCode": 0,
  "incompleteDetails": [],
  "summary": {
    "attempted": 5,
    "ok": 5,
    "cached": 0,
    "recordedFailure": 0,
    "failed": 0,
    "skipped": 3,
    "bytes": 1546817
  },
  "attempts": [
    {
      "id": "azdo.artifacts",
      "phase": "azdo_root",
      "required": true,
      "provider": "azdo",
      "operation": "list_artifacts",
      "resource": { "org": "dnceng-public", "project": "public", "buildId": 1621192 },
      "outcome": "ok",
      "bytes": 137998,
      "paging": {
        "returned": 117,
        "total": 117,
        "offset": 0,
        "limit": null,
        "complete": true,
        "truncated": false,
        "next": null
      }
    },
    {
      "id": "azdo.logs",
      "phase": "azdo_logs",
      "required": true,
      "provider": "azdo",
      "operation": "get_build_log",
      "outcome": "skipped",
      "bytes": null,
      "skip": {
        "kind": "policy_excluded",
        "message": "AzDO log collection was disabled by --log-scope none."
      }
    }
  ]
}
```

**Exit codes:**

| Code | Meaning |
|------|---------|
| `0` | Collection completed and `complete == true`, or `--allow-incomplete` was supplied and every incomplete detail is policy-allowed. |
| `1` | Command/setup failure: invalid retry kind or delay, invalid policy values, running in eval mode, or snapshot export/validation failure. |
| `2` | Manifest was written, but required collection is incomplete: required fetch failure, recorded provider failure, unallowed required skip, or incomplete evidence-plan details such as unresolved/truncated Helix failures. |

**Retry behavior:** only `rate_limited`, `timeout`, and `transport_error` are retried by default. `--retry-count` is the total attempt count. `retryAfterSeconds` is honored for rate limits when present, even beyond `--retry-max-delay`, with a 1-hour safety ceiling; otherwise retries use exponential backoff from `--retry-initial-delay` bounded by `--retry-max-delay` with small jitter. Non-retried acquisition kinds such as `not_found`, `access_denied`, and `invalid_response` become `recorded_failure` attempts.

**Resume behavior:** `--resume` reads the existing manifest at the resolved manifest path. Prior `ok`/`cached` attempts are recorded as `cached` only if the referenced cache key still exists, and final cache verification still runs before export/manifest completion. Prior provider failures are reused as `recorded_failure` only when the matching negative-cache entry still exists and its kind is not selected by the current retry policy; otherwise they are refetched/retried. Policy changes are reflected in the newly written manifest.

**Resuming a run that used an implicit isolated cache:** if the original run was `--export` without `--cache-dir`, the collector populated a fresh, per-run temporary cache directory instead of the normal hlx cache root (see `--cache-dir` above), and that temporary directory is not reused automatically on a later invocation. Supplying `--manifest <path>` alone is not enough to resume it: the manifest only tells `--resume` which prior attempts to consider, while cache evidence is read from whatever `--cache-dir` resolves to on the new run (the normal hlx cache root by default). To resume such a run, pass the original `--cache-dir`, which the prior run recorded in the manifest at `command.options.cacheDir` (and in the isolated-cache message printed to stderr: `No --cache-dir given with --export; collecting into an isolated cache directory: <path>`), together with `--resume --manifest <path-to-prior-manifest> --export <new-destination>`:

```bash
# Original run: no --cache-dir, so hlx used an isolated temp cache and reported it on stderr
hlx collect azdo-build 1621192 --export /tmp/snap-v1
# manifest/hlx-collect-manifest.json inside /tmp/snap-v1 records command.options.cacheDir, e.g.
#   /tmp/hlx-collect-cache/<guid>

# Resuming: reuse that recorded cache directory explicitly
hlx collect azdo-build 1621192 \
  --cache-dir /tmp/hlx-collect-cache/<guid> \
  --manifest /tmp/snap-v1/manifest/hlx-collect-manifest.json \
  --resume --export /tmp/snap-v2
```

If the temporary cache directory was already deleted (for example by OS temp-directory cleanup), no cache evidence remains to resume from, and the collector refetches everything as a fresh run. Runs started with an explicit `--cache-dir` do not have this limitation: pass the same `--cache-dir` again to resume.

## Snapshot Commands

### `hlx snapshot export <destination>`

Export the current cache as an offline eval snapshot. The snapshot preserves cache keys and can be replayed in eval mode (see `HLX_EVAL_SNAPSHOT` below). Current exports use snapshot schema v2.

```bash
hlx snapshot export /tmp/my-snapshot
```

The command prints:
- Source cache location
- Destination path
- Auth-scoped replay limitation (see below)
- Final summary with destination, database size, artifact count, and usage instructions

**Usage in eval mode:**

```bash
HLX_EVAL_SNAPSHOT=/tmp/my-snapshot hlx status <jobId>
HLX_EVAL_SNAPSHOT=/tmp/my-snapshot hlx azdo test-results <buildId> <runId>
HLX_EVAL_SNAPSHOT=/tmp/my-snapshot hlx mcp
```

In schema v2 snapshots, deterministic acquisition failures recorded during live population are exported with the cache. Recordable failure kinds are `not_found`, `access_denied`, and `invalid_response`; transient kinds (`rate_limited`, `timeout`, `transport_error`) are not recorded. During offline replay, recorded failures keep their original `kind`, `provider`, `operation`, `resource`, `httpStatus` (when known), and `message`, and add `source: "snapshot"`, `replayed: true`, and `recordedAt`.

Live mode never serves recorded failures as data. A later successful live fetch for the same cache key deletes the recorded failure. A snapshot miss for a key that was never collected is reported separately as `kind: "not_in_snapshot"`, `provider: "cache"`, `source: "snapshot"`.

**AzDO replay partition selection:**

The snapshot preserves AzDO cache keys and replays them without AzDO credentials. Eval mode does not need `AZDO_TOKEN` or `az login`; instead, it selects the non-secret AzDO cache partition embedded in the snapshot:

- `public` for anonymous/public AzDO entries.
- `cache-xxxxxxxx` for entries collected with an authenticated AzDO identity such as `AZDO_TOKEN`, `AzureCliCredential`, or `az` CLI fallback.

Collector exports also record that partition in `manifest/hlx-collect-manifest.json` as `auth.azdo.cachePartition`; authenticated collector snapshots use `auth.azdo.replay: "snapshot_partition"`. Selection follows this precedence:

1. `HLX_EVAL_AZDO_PARTITION=public` or `HLX_EVAL_AZDO_PARTITION=cache-xxxxxxxx`, if set. If the snapshot's cache contains any discovered AzDO partitions and the requested one isn't among them, selection fails.
2. Otherwise, the partition recorded in `manifest/hlx-collect-manifest.json` (`auth.azdo.cachePartition`), if a manifest is present. This is the common case for collector-exported snapshots and needs no environment variable.
3. Otherwise, if the snapshot's `cache.db` contains exactly one AzDO partition, hlx selects it automatically.
4. Otherwise, if it contains more than one AzDO partition with no explicit selector or manifest to disambiguate, hlx fails closed until `HLX_EVAL_AZDO_PARTITION` selects one.
5. Otherwise (no partitions discovered and nothing else matched), hlx defaults to `public`.

`HLX_EVAL_AZDO_PARTITION` is only *required* for case 4: an ambiguous snapshot with multiple AzDO partitions and no recorded manifest selection.

### `hlx snapshot validate <snapshotPath>`

Validate a snapshot directory for use with `HLX_EVAL_SNAPSHOT`. Checks:
- Single-link SQLite database ownership (no hard-link aliases)
- Database integrity and schema version
- Schema v2 acquisition-failure table and indexes
- SQLite sidecar absence (`-wal`, `-shm`, `-journal` files)
- Empty/corrupt NUL-prefixed raw AzDO log metadata rows
- Artifact references and file sizes

```bash
hlx snapshot validate /tmp/my-snapshot
```

Exit codes:
- `0` — Snapshot is VALID
- `1` — Errors found (INVALID)

Output includes:
- Warnings (if any) — informational issues that don't block usage
- Errors (if any) — validation failures
- Metadata entry count
- Artifact entry count
- Acquisition error entry count
- Missing artifact files count

Schema v1 snapshots remain valid, but validation prints a compatibility warning because v1 predates recorded acquisition failures. Missing v1 entries replay as `not_in_snapshot` rather than the original provider failure.

**Intended workflow:** `snapshot export` → `snapshot validate` → offline run with `HLX_EVAL_SNAPSHOT`.

### Scanner workflow

Treat a scanner "bundle" as the existing offline cache snapshot plus the collector manifest, not a separate artifact format. Collect in CI while live credentials and network are available, upload the snapshot, then investigate anywhere without AzDO credentials:

```bash
hlx collect azdo-build "$BUILD_ID_OR_URL" --export /tmp/my-snapshot
# upload /tmp/my-snapshot to the scanner/investigation environment
env -u AZDO_TOKEN HLX_EVAL_SNAPSHOT=/tmp/my-snapshot hlx mcp
```

For `dnx`-based MCP configs, set `HLX_EVAL_SNAPSHOT` in the MCP server environment and use the same command/args as live mode (`dnx --yes lewing.helix.mcp`; MCP mode is the default when no subcommand is given). Offline replay reads only the snapshot cache and selected AzDO partition; it does not require `AZDO_TOKEN` or `az login`.

Read `/tmp/my-snapshot/manifest/hlx-collect-manifest.json` before launching or evaluating the scanner. `complete: true` means all required resources for the selected policy were fetched or policy-skipped. `complete: false` with `exitCode: 2` means the snapshot is usable but has declared gaps; inspect `incompleteDetails[]` and `attempts[]` to decide whether to upload, retry with `--resume`, change policy, or fail the scan.

Offline replay distinguishes provider failures from collector gaps. A provider failure recorded during collection replays with its original `error.kind` plus `source: "snapshot"` and `replayed: true`; retrying offline will return the same recorded failure. A resource that was never collected returns `kind: "not_in_snapshot"`, `provider: "cache"`, which means the manifest/snapshot is missing that key and must be recollected live (or deliberately skipped in policy).

## Utility Commands

| Command | Description |
|---------|-------------|
| `hlx mcp` | Start MCP server over stdio (also the default when no command is given) |
| `hlx cache status` | Show cache size, entry count, oldest/newest entries |
| `hlx cache clear` | Wipe all cached data |
| `hlx llms-txt` | Print CLI documentation for LLM agents |

## Errors and exit codes

Provider acquisition failures use a stable `AcquisitionError` shape across CLI JSON and MCP structured errors. The wire values for `error.kind` are:

| `kind` | Meaning |
|--------|---------|
| `not_found` | The requested provider resource or eval-mode cache entry was not found. |
| `access_denied` | Authentication or authorization failed. |
| `rate_limited` | The provider returned a rate-limit response. |
| `timeout` | The provider operation timed out. |
| `transport_error` | Network transport failed or the provider returned an unclassified non-success HTTP status. |
| `invalid_response` | The provider/cache returned malformed, empty, corrupt, or otherwise unusable data. |
| `not_in_snapshot` | Eval mode could not find the requested cache entry in the offline snapshot. |

`error.provider` is `azdo`, `helix`, or `cache` (eval-mode snapshot misses and corrupt cache entries). Every error has `kind`, `provider`, `operation`, `resource`, and `message`; `httpStatus`, `retryAfterSeconds`, `source`, `replayed`, and `recordedAt` are present only when known. `source: "snapshot"` means the error came from eval-mode snapshot replay. `replayed: true` means the provider failure was observed and recorded during live collection, then replayed offline. The `resource` object contains operation-specific identifiers such as `org`, `project`, `buildId`, `logId`, `jobId`, `workItem`, or cache `key`; URL query strings and fragments are redacted from resource string values.

CLI commands with `--json` wrap hard acquisition failures as:

```json
{
  "ok": false,
  "error": {
    "kind": "not_found",
    "provider": "azdo",
    "operation": "get_build",
    "resource": {
      "org": "dnceng-public",
      "project": "public",
      "buildId": 999999999
    },
    "httpStatus": 404,
    "message": "AzDO get_build not found for org=dnceng-public, project=public, buildId=999999999 (HTTP 404)."
  }
}
```

The example above is from:

```bash
cd src/HelixTool
DOTNET_ROLL_FORWARD=Major dotnet run -- azdo build 999999999 --json
echo exit=$?
# exit=1
```

A recorded provider failure replayed from a schema v2 snapshot keeps its original provider failure shape and adds snapshot replay metadata. This example was captured after populating an anonymous/public cache with `azdo build 1621466 --json` and `azdo log 1621466 999999 --json`, exporting the snapshot, then replaying the same missing log offline:

```json
{
  "ok": false,
  "error": {
    "kind": "not_found",
    "provider": "azdo",
    "operation": "get_build_log",
    "resource": {
      "org": "dnceng-public",
      "project": "public",
      "buildId": 1621466,
      "logId": 999999
    },
    "source": "snapshot",
    "replayed": true,
    "recordedAt": "2026-10-02T18:07:29.635097+00:00",
    "message": "Build log 999999 for build 1621466 returned an empty body, but the log ID was absent from the build log metadata and timeline log references."
  }
}
```

A key that was never collected into the snapshot is different: it is a collector gap and returns `not_in_snapshot` from provider `cache`:

```json
{
  "ok": false,
  "error": {
    "kind": "not_in_snapshot",
    "provider": "cache",
    "operation": "get_build",
    "resource": {
      "org": "dnceng-public",
      "project": "public",
      "buildId": 999999999
    },
    "source": "snapshot",
    "message": "Snapshot does not contain cache entry for get_build."
  }
}
```

MCP tool failures return a normal tool result with `isError: true`, human text in `content[0].text`, and the same machine-readable error nested under `structuredContent.error`:

```json
{
  "isError": true,
  "content": [
    { "type": "text", "text": "AzDO get_build_log not found for org=dnceng-public, project=public, buildId=12345, logId=7 (HTTP 404)." }
  ],
  "structuredContent": {
    "error": {
      "kind": "not_found",
      "provider": "azdo",
      "operation": "get_build_log",
      "resource": {
        "org": "dnceng-public",
        "project": "public",
        "buildId": 12345,
        "logId": 7
      },
      "httpStatus": 404,
      "message": "AzDO get_build_log not found for org=dnceng-public, project=public, buildId=12345, logId=7 (HTTP 404)."
    }
  }
}
```

Callers own retry/skip policy. `rate_limited` can include `retryAfterSeconds`, but `hlx` does not automatically decide whether a caller should retry, skip a resource, or fail a larger collection. For scripts and scanners, treat `not_in_snapshot` as a collection gap: fetch the resource live and export a new snapshot, or record a deliberate skip in your manifest. Treat `replayed: true` as a provider failure observed at collection time, not an offline collection gap.

Exit codes:

| Code | Meaning |
|------|---------|
| `0` | Success. Genuinely empty successful results remain successes: for example, a real empty work-item file list or an empty successful search/list result is not converted into an error. |
| `1` | Hard command or acquisition error, including validation failures, provider errors, auth failures, invalid/corrupt cache entries, malformed provider JSON, and empty provider responses where a JSON object/list was required. |
| `2` | Bounded output was written but is incomplete/truncated. For AzDO list commands, this means the JSON envelope has `truncated == true` and `--allow-truncated` was not supplied. For evidence plans, this means `complete == false` because artifact mapping is ambiguous/missing, output was truncated, monitor data was unparseable/unresolved, or Helix-failure paging returned a partial page. |

When a direct build-log body is empty, hlx validates the logId against the build's logs list and timeline record.log.id; if the logId exists in neither, the operation fails with kind=not_found, provider=azdo, operation=get_build_log. If the logId exists, an empty log is a successful result. Empty full logs are not cached.

## Environment Variables

| Variable | Purpose |
|----------|---------|
| `HELIX_ACCESS_TOKEN` | Helix API token (overrides stored credential) |
| `AZDO_TOKEN` | Azure DevOps PAT (overrides Azure CLI auth) |
| `AZDO_TOKEN_TYPE` | Optional `AZDO_TOKEN` classification override. Use `pat` or `bearer` when live AzDO token auto-detection misclassifies the token. Snapshot replay does not require this variable. |
| `HLX_EVAL_SNAPSHOT` | Path to a snapshot directory (created with `hlx snapshot export`) for offline replay mode. When set, hlx loads the snapshot's cached data instead of making live API calls. Overrides all cache configuration and auth. |
| `HLX_EVAL_AZDO_PARTITION` | Optional eval-mode AzDO cache partition selector. Use `public` or `cache-xxxxxxxx` when a snapshot contains multiple AzDO partitions, or to override automatic single-partition/manifest selection. |
| `HLX_CACHE_MAX_SIZE_MB` | Max cache size in MB (default: 1024, set to `0` to disable). `hlx collect azdo-build` requires caching and rejects `0`. |
| `HLX_DISABLE_FILE_SEARCH` | Set to `true` to disable file content search tools |
| `HLX_API_KEY` | Require API key for HTTP MCP server access |

## Failure Categorization

Failed work items are automatically classified: **Timeout**, **Crash**, **BuildFailure**, **TestFailure**, **InfrastructureError**, **AssertionFailure**, or **Unknown**. The category appears in `status`, `work-item`, and `batch-status` output.

## Finding Helix Job IDs

Helix job IDs appear in Azure DevOps build logs. Look for "Send to Helix" or "Wait for Helix" tasks — the job ID is a GUID in the log output.
