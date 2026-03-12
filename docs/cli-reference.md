# hlx CLI Reference

`hlx` is the command-line interface for [helix.mcp](../README.md). It provides direct access to Helix and Azure DevOps CI data from the terminal.

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

### `hlx azdo builds [--branch B] [--pr N] [--definition-id D] [--status S] [--top N]`

List recent builds for a project. Defaults to `dnceng-public/public`.

```bash
hlx azdo builds --branch main
hlx azdo builds --pr 12345 --top 5
```

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

### `hlx azdo test-results <buildId> <runId> [--top N]`

Get test results for a specific test run. Defaults to failed tests (top 200).

```bash
hlx azdo test-results 12345678 98765
```

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
