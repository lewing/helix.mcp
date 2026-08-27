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

### `hlx azdo log <buildId> <logId> [--tail-lines N]`

Get log content for a build log entry. Use log IDs from `timeline` output. Default tail: 500 lines.

```bash
hlx azdo log 12345678 42
hlx azdo log 12345678 42 --tail-lines 100
```

### `hlx azdo changes <buildId> [--top N]`

List commits/changes associated with a build.

```bash
hlx azdo changes 12345678
```

### `hlx azdo test-runs <buildId> [--top N]`

List test runs for a build (total, passed, failed counts).

```bash
hlx azdo test-runs 12345678
```

### `hlx azdo test-results <buildId> <runId> [--top N] [--outcomes OUTCOMES]`

Get test results for a specific test run. Defaults to failed tests (top 200).

```bash
hlx azdo test-results 12345678 98765
hlx azdo test-results 12345678 98765 --outcomes "Passed,Failed"
hlx azdo test-results 12345678 98765 --outcomes NotExecuted
```

`--outcomes` accepts a comma-separated list of AzDO test outcome names (e.g. `Failed`, `Passed`, `NotExecuted`). Default: `Failed`.

### `hlx azdo artifacts <buildId> [--pattern PAT] [--top N]`

List build artifacts. Supports glob-style filtering.

```bash
hlx azdo artifacts 12345678
hlx azdo artifacts 12345678 --pattern "*.binlog"
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

### `hlx azdo evidence plan <buildId> [--job-results RESULTS] [--artifact-pattern PAT] [--artifact-job-prefix PREFIX] [--keep-attempt-prefix] [--match MODE] [--json]`

Plan failed/canceled job → artifact evidence mapping. Returns a bounded plan with candidate artifacts (if any) for each selected job, ranked by attempt number. It never silently chooses: ambiguous matches retain ranked candidates and report the full candidate count.

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

**Exit Codes:**

| Code | Meaning |
|------|---------|
| `0` | Plan produced and all selected jobs have exactly one mapped artifact (`complete == true`). |
| `2` | Plan produced but contains ambiguous, missing, or truncated results (`complete == false`). **The bounded plan is still written to stdout.** This is informational, not a hard error. |
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
- `complete` — `true` only if all entries have `status: "mapped"` and no output was truncated.
- `incompleteReasons[]` — Human-readable lines (present only if `complete == false`) explaining ambiguities, gaps, or entry truncation.
- `warnings[]` — Non-fatal planning diagnostics in deterministic order, capped at 10. Always present (empty when there are no warnings).
- `warningTotal` — Total warnings before the 10-item bound. Always present.
- `warningsTruncated` — `true` when `warningTotal` exceeds the number returned in `warnings`; otherwise `false`. Always present.
- `truncated` — `true` if either the 200-entry limit or any entry's 10-candidate limit was exceeded.
- `totalEntries` — Total selected jobs (present whenever `truncated` is `true`, whether entry or candidate overflow caused it).
- `note` — Present on truncation; summarizes entry truncation, candidate-list truncation, or both.
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
```

### `hlx azdo test-attachments <runId> <resultId> [--top N]`

List attachments for a test result (screenshots, logs, dumps).

```bash
hlx azdo test-attachments 98765 1234
```

## Utility Commands

| Command | Description |
|---------|-------------|
| `hlx mcp` | Start MCP server over stdio (also the default when no command is given) |
| `hlx cache status` | Show cache size, entry count, oldest/newest entries |
| `hlx cache clear` | Wipe all cached data |
| `hlx llms-txt` | Print CLI documentation for LLM agents |

## Environment Variables

| Variable | Purpose |
|----------|---------|
| `HELIX_ACCESS_TOKEN` | Helix API token (overrides stored credential) |
| `AZDO_TOKEN` | Azure DevOps PAT (overrides Azure CLI auth) |
| `HLX_CACHE_MAX_SIZE_MB` | Max cache size in MB (default: 1024, set to `0` to disable) |
| `HLX_DISABLE_FILE_SEARCH` | Set to `true` to disable file content search tools |
| `HLX_API_KEY` | Require API key for HTTP MCP server access |

## Failure Categorization

Failed work items are automatically classified: **Timeout**, **Crash**, **BuildFailure**, **TestFailure**, **InfrastructureError**, **AssertionFailure**, or **Unknown**. The category appears in `status`, `work-item`, and `batch-status` output.

## Finding Helix Job IDs

Helix job IDs appear in Azure DevOps build logs. Look for "Send to Helix" or "Wait for Helix" tasks — the job ID is a GUID in the log output.
