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
- `cache.key` — Cache key for the exact response (`--all` uses the complete key; windows use window keys where the backing endpoint supports them).
- `cache.completeKey` — Cache key for the complete selected list. In eval mode, capped MCP/list calls can be served from this complete key.
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

**Auth-scoped replay limitation:**

The snapshot preserves all cache keys unchanged. When replayed in eval mode:

- **Environment-keyed entries** (auth via `AZDO_TOKEN`): Reproducible. Set `AZDO_TOKEN` to the same PAT/Entra token and `AZDO_TOKEN_TYPE` to the same classification value for reliable replay.
- **Anonymous/public entries**: Always reproducible without credentials.
- **Azure CLI credential partitions** (`AzureCliCredential` or `az` CLI-derived identity): Not reproducible in eval mode because eval mode has an environment-only token accessor. To replay with `az` CLI auth, first export a snapshot using `AZDO_TOKEN` instead of `az login`.

### `hlx snapshot validate <snapshotPath>`

Validate a snapshot directory for use with `HLX_EVAL_SNAPSHOT`. Checks:
- Single-link SQLite database ownership (no hard-link aliases)
- Database integrity and schema version
- Schema v2 acquisition-failure table and indexes
- SQLite sidecar absence (`-wal`, `-shm`, `-journal` files)
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

Treat a scanner "bundle" as the existing offline cache snapshot, not a separate artifact format:

1. Populate the cache by running the needed CLI commands live. Use complete-list commands and full logs when the scanner needs offline replay without later live calls:

   ```bash
   hlx azdo build "$BUILD_ID" --json
   hlx azdo timeline "$BUILD_ID" --json
   hlx azdo changes "$BUILD_ID" --all --json
   hlx azdo test-runs "$BUILD_ID" --all --json
   hlx azdo artifacts "$BUILD_ID" --all --json
   hlx azdo log "$BUILD_ID" "$LOG_ID" --full --json
   ```

   Add `hlx azdo test-results "$BUILD_ID" "$RUN_ID" --all --json`, `hlx azdo test-attachments "$RUN_ID" "$RESULT_ID" --all --json`, and Helix drilldown commands surfaced by `azdo evidence plan` for any selected runs/results/work items. The capped MCP defaults are unchanged, but in eval mode they can be served offline from the complete-list keys populated by `--all`.
2. Export and validate the snapshot:

   ```bash
   hlx snapshot export /tmp/my-snapshot
   hlx snapshot validate /tmp/my-snapshot
   ```

3. Run the agent offline against the same MCP/CLI surface:

   ```bash
   HLX_EVAL_SNAPSHOT=/tmp/my-snapshot hlx mcp
   ```

   For `dnx`-based MCP configs, set the `HLX_EVAL_SNAPSHOT` environment variable and use the same command/args as live mode (`dnx --yes lewing.helix.mcp`; MCP mode is the default when no subcommand is given).

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
| `AZDO_TOKEN_TYPE` | Classification of the AZDO_TOKEN value. Used to distinguish PAT, JWT, and Entra token types. When replaying snapshots, set to the same value as the export session to ensure auth-scoped key classification is preserved. |
| `HLX_EVAL_SNAPSHOT` | Path to a snapshot directory (created with `hlx snapshot export`) for offline replay mode. When set, hlx loads the snapshot's cached data instead of making live API calls. Overrides all cache configuration and auth. |
| `HLX_CACHE_MAX_SIZE_MB` | Max cache size in MB (default: 1024, set to `0` to disable) |
| `HLX_DISABLE_FILE_SEARCH` | Set to `true` to disable file content search tools |
| `HLX_API_KEY` | Require API key for HTTP MCP server access |

## Failure Categorization

Failed work items are automatically classified: **Timeout**, **Crash**, **BuildFailure**, **TestFailure**, **InfrastructureError**, **AssertionFailure**, or **Unknown**. The category appears in `status`, `work-item`, and `batch-status` output.

## Finding Helix Job IDs

Helix job IDs appear in Azure DevOps build logs. Look for "Send to Helix" or "Wait for Helix" tasks — the job ID is a GUID in the log output.
