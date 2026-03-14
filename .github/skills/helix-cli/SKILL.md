---
name: helix-cli
description: >
  Investigate .NET CI failures using the hlx CLI tool via bash.
  USE FOR: checking Helix job status, searching build logs, downloading
  test results, AzDO build timeline analysis — when MCP tools aren't
  loaded or when scripting with JSON output and jq.
  DO NOT USE FOR: tasks where Helix/AzDO MCP tools are already available
  in context (prefer ci-analysis skill when MCP server is loaded).
  INVOKES: bash (hlx CLI commands with --json output).
---

# Helix CLI

Investigate Helix and Azure DevOps CI failures with the `hlx` CLI instead of loading the Helix/AzDO MCP tools into context. Use it when you want the same backend data with less context tax, more shell composability, and progressive discovery through `--help` plus `hlx llms-txt`.

See the full command and JSON field reference in [references/helix-cli-reference.md](references/helix-cli-reference.md).

## When to Use This Skill

Use this skill when:
- Helix/AzDO MCP tools are not loaded, but `hlx` is installed or available via `dotnet run`.
- You want to script investigations with shell pipelines, `--json`, and optionally `jq`.
- You need to move quickly through a build → timeline → logs → tests workflow without paying the context cost of loading many tools.
- You are working in a plain terminal, CI job, or one-off bash session where CLI output is easier than MCP round-trips.

Prefer MCP tools or the `ci-analysis` skill when those tools are already loaded in context. The CLI is the context-efficient fallback, not the primary path when MCP is ready.

## Installation

```bash
dotnet tool install -g lewing.helix.mcp
```

Local repo fallback:

```bash
dotnet run --project src/HelixTool -- <command>
```

Examples in this skill use `hlx`; replace it with the local `dotnet run` form if needed.

## Authentication

`hlx` has a three-tier auth story depending on the data source:

### Helix

Use any of these:

```bash
export HELIX_ACCESS_TOKEN=...
hlx auth-status
```

or:

```bash
hlx login
hlx auth-status
hlx logout
```

- `HELIX_ACCESS_TOKEN` is the explicit env-var path.
- `hlx login` stores a token via git credential helpers.
- Public Helix jobs need no auth.

### Azure DevOps

Resolution order is:
1. `AZDO_TOKEN`
2. Azure CLI credential / `az login`
3. `az account get-access-token`
4. anonymous access for public projects such as `dnceng-public/public`

Useful check:

```bash
hlx azdo auth-status --json
```

## Progressive Discovery

Discover capabilities in three steps instead of loading every tool description up front:

```bash
hlx --help
hlx llms-txt
hlx azdo search-log --help
hlx azdo test-results --help
```

Recommended discovery order:
1. `hlx --help` for the top-level command list.
2. `hlx llms-txt` for the compact LLM-oriented docs surface, including auth, caching, and investigation routing.
3. `<specific command> --help` for flags and positional arguments.

Important: there is not yet a `hlx ci-guide` CLI command. For CLI-only discovery, use `hlx llms-txt`. If MCP is available, repo-specific routing still lives in `helix_ci_guide(repo)` and `ci://profiles` resources.

## Common Patterns

### 1. Find the recent build for a PR

```bash
hlx azdo builds --pr-number 118282 --top 5 --json
```

Use this to get build IDs before drilling into timeline, logs, or test runs.

### 2. Show only the failing parts of a build timeline

```bash
hlx azdo timeline 12345678 --filter failed --json
```

This is the quickest way to surface failed jobs/tasks and the `log.id` values needed for targeted log reads.

### 3. Search ranked AzDO logs without knowing the failing step yet

```bash
hlx azdo search-log 12345678 --pattern error --max-matches 20 --json
```

When `--log-id` is omitted, `hlx` searches ranked build logs across the build and stops early once `--max-matches` is satisfied.

### 4. Drill into one specific AzDO log step

```bash
hlx azdo log 12345678 42 --tail-lines 300
```

Use this after `timeline` or `search-log` identifies a promising `log.id`.

### 5. Pivot from build failure to Helix job details

```bash
hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009 all --json
hlx work-item 02d8bd09-9400-4e86-8d2b-7a6ca21c5009 "System.Net.Tests" --json
hlx files 02d8bd09-9400-4e86-8d2b-7a6ca21c5009 "System.Net.Tests"
```

Use this when the failure signal lives in Helix work items rather than AzDO build orchestration.

### 6. Pull structured test results before scraping logs

```bash
hlx azdo test-runs 12345678 --json
hlx azdo test-results 12345678 987654 --json
```

If the work item uploaded test results to Helix instead, use:

```bash
hlx parse-uploaded-trx 02d8bd09-9400-4e86-8d2b-7a6ca21c5009 "System.Net.Tests"
```

### 7. Check auth before assuming the service is broken

```bash
hlx auth-status
hlx azdo auth-status --json
```

Do this early when a public build works for others but fails locally.

## Chaining with jq

Examples below assume `jq` is available; if it is not, the plain CLI output is still useful.

### Failed Helix work items with categories and log URLs

```bash
hlx status "$JOB" all --json \
  | jq -r '.failed[] | [.name, .failureCategory, .consoleLogUrl] | @tsv'
```

### Recent failed builds for a PR

```bash
hlx azdo builds --pr-number 118282 --top 20 --json \
  | jq -r '.[] | select(.result == "failed") | [.id, .definition.name, .sourceBranch, (.triggerInfo["pr.number"] // "-")] | @tsv'
```

### Failed timeline records with log IDs

```bash
hlx azdo timeline "$BUILD" --filter failed --json \
  | jq -r '.records[] | select(.log != null) | [.type, .name, (.log.id | tostring)] | @tsv'
```

### Ranked AzDO log hits by step

```bash
hlx azdo search-log "$BUILD" --pattern error --max-matches 20 --json \
  | jq -r '.steps[] | [.logId, .stepName, .stepResult, (.matchCount | tostring)] | @tsv'
```

### Failed tests in a specific test run

```bash
hlx azdo test-results "$BUILD" "$RUN" --json \
  | jq -r '.[] | [.id, .testCaseTitle, .outcome, .automatedTestName] | @tsv'
```

Notes:
- `hlx azdo builds --json` returns a bare array, not an MCP-style wrapper.
- `hlx status ... all --json` is the easiest way to guarantee both `failed` and `passed` arrays are present.
- `hlx search-log` for Helix is currently text-oriented in the CLI; see the reference doc for the underlying structured field shape.

## Cache Behavior

`hlx` uses a shared SQLite-backed cache across CLI and stdio MCP usage.

Key points:
- Public and authenticated traffic use separate cache roots.
- `hlx cache status` reports the current auth context.
- `hlx cache clear` clears all auth contexts, not just the current one.
- Default max cache size is 1 GB (`HLX_CACHE_MAX_SIZE_MB=0` disables it).
- Helix TTLs are short for running jobs and longer for completed jobs.
- AzDO caches completed builds longer than in-progress builds; immutable logs cache longer than changing build state.

Quick cache checks:

```bash
hlx cache status
hlx cache clear
```
