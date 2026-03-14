# Helix CLI Reference

Reference material for the `helix-cli` skill. Use this file when you need the full command map, exact JSON field names, or a repeatable CI investigation flow.

## Full Command Table

### Helix and utility commands

| CLI command | `--json` | MCP tool mapping | Notes |
|---|---:|---|---|
| `hlx auth-status` | No | — | Checks the active Helix auth source and validates the token with a live API probe. |
| `hlx login` | No | — | Opens/stores a Helix token via git credential helpers. |
| `hlx logout` | No | — | Removes the stored Helix token. |
| `hlx status <jobId> [failed\|passed\|all]` | Yes | `helix_status` | Positional filter; accepts bare GUIDs or Helix URLs. |
| `hlx logs <jobId> <workItem>` | No | `helix_logs` | Downloads the console log to a temp file and prints the path. |
| `hlx files <jobId> <workItem>` | No | `helix_files` | Lists uploaded files for one work item. |
| `hlx download <jobId> <workItem> [--pattern P]` | No | `helix_download` | Downloads matching uploaded files to temp storage. |
| `hlx download --url URL` | No | `helix_download` | Direct blob download path. |
| `hlx find-files <jobId> [--pattern P] [--max-items N]` | No | `helix_find_files` | Scans work items for matching uploaded files. |
| `hlx work-item <jobId> <workItem>` | Yes | `helix_work_item` | Detailed work-item metadata plus uploaded file list. |
| `hlx batch-status <jobId1> <jobId2> ...` | No | `helix_batch_status` | Parallel summary across multiple Helix jobs. |
| `hlx search-log <jobId> <workItem> <pattern> [--file-name NAME] [--context N] [--max-matches N]` | No* | `helix_search` | *CLI currently prints text output only; see structured fields below for parity with core/MCP results. |
| `hlx parse-uploaded-trx <jobId> <workItem> [--file-name NAME] [--include-passed] [--max-results N]` | No | `helix_parse_uploaded_trx` | Parses Helix-hosted TRX/xUnit results. |
| `hlx llms-txt` | No | — | Best CLI discovery surface for LLM agents. |
| `hlx cache status` | No | — | Shows cache info for the current auth context. |
| `hlx cache clear` | No | — | Clears cache data across all auth contexts. |
| `hlx mcp` | No | — | Starts the stdio MCP server. |

### Azure DevOps commands

| CLI command | `--json` | MCP tool mapping | Notes |
|---|---:|---|---|
| `hlx azdo auth-status` | Yes | — | Shows the resolved AzDO auth path without making an AzDO API request. |
| `hlx azdo build <buildId>` | Yes | `azdo_build` | Accepts numeric build IDs or AzDO build URLs. |
| `hlx azdo builds [--org ORG] [--project PROJ] [--top N] [--branch B] [--pr-number N] [--definition-id N] [--status S]` | Yes | `azdo_builds` | Defaults to `dnceng-public/public`. |
| `hlx azdo timeline <buildId> [--filter failed\|all]` | Yes | `azdo_timeline` | Returns timeline records with nested `log.id` references. |
| `hlx azdo log <buildId> <logId> [--tail-lines N]` | No | `azdo_log` | Reads raw log text for one log entry. |
| `hlx azdo search-log <buildId> [--log-id N] [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N]` | Yes | `azdo_search_log` | `--log-id` = one log; omit it for ranked cross-step search. |
| `hlx azdo search-timeline <buildId> <pattern> [--type Stage\|Job\|Task] [--result failed\|all]` | Yes | `azdo_search_timeline` | Searches record names and issue messages. |
| `hlx azdo changes <buildId> [--top N]` | Yes | `azdo_changes` | Build-associated commits/changes. |
| `hlx azdo test-runs <buildId> [--top N]` | Yes | `azdo_test_runs` | Run summaries with total/passed/failed counts. |
| `hlx azdo test-results <buildId> <runId> [--top N]` | Yes | `azdo_test_results` | Individual test results; defaults to failed tests. |
| `hlx azdo artifacts <buildId> [--pattern P] [--top N]` | Yes | `azdo_artifacts` | Build artifacts with optional filtering. |
| `hlx azdo test-attachments <runId> <resultId> [--org ORG] [--project PROJ] [--top N]` | Yes | `azdo_test_attachments` | Attachments for one test result. |

## Key JSON Response Fields

These are the field names that matter most when piping `hlx` output into `jq`. The schemas below are derived from the current CLI serialization code and core result models; sample values are illustrative.

### 1. `hlx status <jobId> all --json`

CLI shape:

```json
{
  "job": {
    "jobId": "02d8bd09-9400-4e86-8d2b-7a6ca21c5009",
    "name": "runtime",
    "queueId": "ubuntu.2204.amd64.open",
    "creator": "dotnet-maestro[bot]",
    "source": "https://github.com/dotnet/runtime",
    "created": "2026-03-14T00:00:00Z",
    "finished": "2026-03-14T00:15:00Z"
  },
  "totalWorkItems": 12,
  "failedCount": 2,
  "passedCount": 10,
  "failed": [
    {
      "name": "System.Net.Tests",
      "exitCode": 1,
      "state": "Failed",
      "machineName": "fv-az123-456",
      "duration": "00:02:34.5678900",
      "consoleLogUrl": "https://helix.dot.net/api/2019-06-17/jobs/.../console",
      "failureCategory": "TestFailure"
    }
  ],
  "passed": [
    {
      "name": "System.IO.Tests",
      "exitCode": 0,
      "state": "Passed",
      "machineName": "fv-az123-789",
      "duration": "00:01:12.3456789",
      "consoleLogUrl": "https://helix.dot.net/api/2019-06-17/jobs/.../console",
      "failureCategory": null
    }
  ]
}
```

High-value selectors:
- `.job.jobId`, `.job.name`, `.job.queueId`
- `.failedCount`, `.passedCount`
- `.failed[].name`, `.failed[].failureCategory`, `.failed[].consoleLogUrl`
- `.passed[].name`

Notes:
- The positional filter changes which arrays are present. Use `all` when you want both `failed` and `passed`.
- `duration` is a raw `TimeSpan` string in CLI JSON, not the shortened human-readable text rendering.

### 2. `hlx azdo builds --json --top 3`

CLI shape:

```json
[
  {
    "id": 12345678,
    "buildNumber": "20260314.4",
    "status": "completed",
    "result": "failed",
    "definition": {
      "id": 123,
      "name": "runtime"
    },
    "sourceBranch": "refs/pull/118282/merge",
    "sourceVersion": "abc123def456",
    "queueTime": "2026-03-14T00:00:00Z",
    "startTime": "2026-03-14T00:01:00Z",
    "finishTime": "2026-03-14T00:12:00Z",
    "requestedFor": {
      "displayName": "dotnet-maestro[bot]"
    },
    "triggerInfo": {
      "ci.message": "Merge pull request 118282 from ...",
      "pr.number": "118282"
    },
    "url": "https://dev.azure.com/.../_apis/build/Builds/12345678"
  }
]
```

High-value selectors:
- `.[].id`, `.[].result`, `.[].status`
- `.[].definition.name`, `.[].definition.id`
- `.[].sourceBranch`, `.[].sourceVersion`
- `.[].requestedFor.displayName`
- `.[].triggerInfo["pr.number"]`

Notes:
- CLI returns a bare array here. MCP wraps similar list results in a `results`/`truncated` envelope, but the CLI does not.

### 3. `hlx azdo timeline <buildId> --json --filter failed`

CLI shape:

```json
{
  "id": "timeline-guid",
  "records": [
    {
      "id": "record-guid",
      "parentId": "parent-record-guid",
      "type": "Task",
      "name": "Run tests",
      "state": "completed",
      "result": "failed",
      "startTime": "2026-03-14T00:04:00Z",
      "finishTime": "2026-03-14T00:06:00Z",
      "log": {
        "id": 42,
        "url": "https://dev.azure.com/.../logs/42"
      },
      "order": 7,
      "issues": [
        {
          "type": "error",
          "message": "Tests failed",
          "category": "General"
        }
      ],
      "workerName": "Azure Pipelines 12",
      "previousAttempts": [
        {
          "id": "attempt-guid",
          "timelineId": "timeline-guid",
          "attempt": 1
        }
      ]
    }
  ]
}
```

High-value selectors:
- `.records[].type`, `.records[].name`, `.records[].result`
- `.records[].log.id`
- `.records[].issues[].message`
- `.records[].workerName`

Notes:
- `--filter failed` prunes the record set before JSON serialization; parent records are retained so the failing task still has context.

### 4. `hlx search-log <jobId> <workItem> error ...` structured fields

The CLI currently renders Helix search results as text only, so there is no shipped `--json` flag today. The underlying structured shape used by the core/MCP path is still useful when reasoning about field names:

```json
{
  "workItem": "System.Net.Tests",
  "fileName": "testhost.log",
  "pattern": "error",
  "totalLines": 1234,
  "matchCount": 5,
  "truncated": false,
  "matches": [
    {
      "lineNumber": 87,
      "line": "error CS1234: Something bad happened",
      "context": [
        "line before",
        "error CS1234: Something bad happened",
        "line after"
      ]
    }
  ]
}
```

High-value selectors if/when you encounter the structured form:
- `.workItem`, `.fileName`, `.pattern`
- `.totalLines`, `.matchCount`, `.truncated`
- `.matches[].lineNumber`, `.matches[].line`
- `.matches[].context[]`

Notes:
- `fileName` is populated when searching an uploaded file and omitted/null for console log search.
- Until CLI JSON parity exists, use the text output directly or switch to the MCP `helix_search` tool when structured matches are required.

### 5. `hlx azdo search-log <buildId> --pattern error --json --max-matches 5`

#### Cross-step mode (no `--log-id`)

```json
{
  "build": "12345678",
  "pattern": "error",
  "totalLogsInBuild": 18,
  "logsSearched": 4,
  "logsSkipped": 2,
  "totalMatchCount": 5,
  "stoppedEarly": true,
  "steps": [
    {
      "logId": 42,
      "stepName": "Run tests",
      "stepType": "Task",
      "stepResult": "failed",
      "parentName": "Linux x64 Debug",
      "lineCount": 1300,
      "matchCount": 3,
      "matches": [
        {
          "lineNumber": 87,
          "line": "error CS1234: Something bad happened",
          "context": [
            "line before",
            "error CS1234: Something bad happened",
            "line after"
          ]
        }
      ]
    }
  ]
}
```

High-value selectors:
- `.totalLogsInBuild`, `.logsSearched`, `.logsSkipped`
- `.totalMatchCount`, `.stoppedEarly`
- `.steps[].logId`, `.steps[].stepName`, `.steps[].stepResult`
- `.steps[].matches[].lineNumber`, `.steps[].matches[].line`

#### Single-log mode (`--log-id N`)

```json
{
  "workItem": "log:42",
  "matches": [
    {
      "lineNumber": 87,
      "line": "error CS1234: Something bad happened",
      "context": [
        "line before",
        "error CS1234: Something bad happened",
        "line after"
      ]
    }
  ],
  "totalLines": 1300,
  "truncated": false
}
```

Notes:
- The single-log form is the same `LogSearchResult` shape used by core text search helpers.
- The cross-step form is the high-value one for build triage because it includes step metadata and ranking summaries.

## Investigation Workflows

### Build failed → find the failing step → search logs → inspect test results

1. Find the build you care about:

   ```bash
   hlx azdo builds --pr-number 118282 --top 5 --json
   ```

2. Pull the failing timeline view and extract log IDs:

   ```bash
   hlx azdo timeline "$BUILD" --filter failed --json \
     | jq -r '.records[] | select(.log != null) | [.type, .name, (.log.id | tostring)] | @tsv'
   ```

3. Search ranked logs when you do not know the exact step yet:

   ```bash
   hlx azdo search-log "$BUILD" --pattern error --max-matches 20 --json
   ```

4. Read one interesting log in full context:

   ```bash
   hlx azdo log "$BUILD" "$LOG_ID" --tail-lines 300
   ```

5. Prefer structured test results before more log scraping:

   ```bash
   hlx azdo test-runs "$BUILD" --json
   hlx azdo test-results "$BUILD" "$RUN_ID" --json
   ```

### Helix job failed → inspect work items → pull artifacts or test results

1. Get the job summary:

   ```bash
   hlx status "$JOB" all --json
   ```

2. Drill into the failing work item:

   ```bash
   hlx work-item "$JOB" "$WORK_ITEM" --json
   ```

3. Enumerate or download useful files:

   ```bash
   hlx files "$JOB" "$WORK_ITEM"
   hlx download "$JOB" "$WORK_ITEM" --pattern "*.binlog"
   ```

4. If Helix uploaded structured results, parse them before searching text:

   ```bash
   hlx parse-uploaded-trx "$JOB" "$WORK_ITEM"
   ```

5. Otherwise search the console log or an uploaded text file:

   ```bash
   hlx search-log "$JOB" "$WORK_ITEM" "error"
   hlx search-log "$JOB" "$WORK_ITEM" "FAIL" --file-name testhost.log --context 5
   ```

## Repo-Specific Guidance

There is no `hlx ci-guide` CLI command yet.

Use this routing instead:
- CLI-only: `hlx llms-txt`
- MCP available: `helix_ci_guide(repo)` and `ci://profiles`, `ci://profiles/{repo}`

Practical rule of thumb:
- Use `hlx parse-uploaded-trx` only when the work item actually uploaded structured results to Helix.
- Otherwise prefer `hlx azdo test-runs` + `hlx azdo test-results` for structured test data.
- Fall back to `hlx search-log` / `hlx azdo search-log` when the useful signal only exists in console or step logs.
