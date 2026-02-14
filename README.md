# helix.mcp — Helix Test Infrastructure CLI & MCP Server

A CLI tool and MCP server for investigating [.NET Helix](https://helix.dot.net) test results. Designed for diagnosing CI failures in dotnet repos (runtime, sdk, aspnetcore, etc.).

Built with [Squad](https://github.com/bradygaster/squad) — [meet the squad](.ai-team/SQUAD.md).

## Architecture

The project is split into three layers:

- **HelixTool.Core** — Shared library containing `HelixService`, `IHelixApiClient`, and model types. All Helix API logic lives here.
- **HelixTool** — CLI tool built with [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework). Serves both human-readable terminal commands and a stdio MCP server (`hlx mcp`). MCP is also the default mode when no subcommand is given.
- **HelixTool.Mcp** — Standalone MCP HTTP server built with [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk). Returns structured JSON for LLM agents over HTTP.

Both the CLI and MCP server depend on Core but not on each other.

> **ci-analysis replacement:** hlx provides 100% coverage of the Helix API surface used by the `ci-analysis` skill's ~150 lines of PowerShell, with structured caching, failure categorization, and MCP tool support on top.

## Installation

### Run with dnx (no install needed)

`dnx` (new in .NET 10) auto-downloads and runs NuGet tool packages — no install step required:

```bash
dnx lewing.helix.mcp
```

This is the recommended approach for MCP server configuration (see below). No explicit `mcp` subcommand is needed — MCP mode is the default when no command is specified.

### Install as Global Tool

```bash
dotnet tool install -g lewing.helix.mcp
```

For repo-local installation via a [tool manifest](https://learn.microsoft.com/dotnet/core/tools/local-tools-how-to-use):

```bash
dotnet new tool-manifest   # if .config/dotnet-tools.json doesn't exist
dotnet tool install --local lewing.helix.mcp
```

### Install from Local Build

```bash
dotnet pack src/HelixTool
dotnet tool install -g --add-source src/HelixTool/nupkg lewing.helix.mcp
```

After installation, `hlx` is available globally.

### Build from Source

```bash
# Prerequisites: .NET 10 SDK
git clone https://github.com/lewing/helix.mcp.git
cd helix.mcp
dotnet build
```

> **NuGet feed requirement:** The `Microsoft.DotNet.Helix.Client` SDK package is published to the
> [dotnet-eng](https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json)
> Azure Artifacts feed. The included `nuget.config` references this feed. If you see restore errors
> for `Microsoft.DotNet.Helix.Client`, ensure the feed is accessible.

## Quick Start

### CLI

After [installing](#installation) `lewing.helix.mcp` as a global or local tool, the `hlx` command is available:

```bash
# Check a Helix job (shows failed work items by default)
hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009

# Show all work items including passed
hlx status 02d8bd09 all

# Download console log for a failed work item
hlx logs 02d8bd09 "dotnet-watch.Tests.dll.1"

# List uploaded files (binlogs, test results, etc.)
hlx files 02d8bd09 "dotnet-watch.Tests.dll.1"

# Download binlogs from a work item
hlx download 02d8bd09 "dotnet-watch.Tests.dll.1" --pattern "*.binlog"

# Scan work items to find which ones have binlogs
hlx find-binlogs 02d8bd09

# Search work items for any file type
hlx find-files 02d8bd09 --pattern "*.trx"

# Download a file by direct URL (from hlx files output)
hlx download-url "https://helix..."

# Show detailed info about a specific work item
hlx work-item 02d8bd09 "dotnet-watch.Tests.dll.1"

# Check status of multiple jobs at once
hlx batch-status 02d8bd09 a1b2c3d4

# Search console log for error patterns
hlx search-log 02d8bd09 "dotnet-watch.Tests.dll.1" "error CS"

# Search an uploaded file for a pattern
hlx search-file 02d8bd09 "dotnet-watch.Tests.dll.1" "testhost.log" "error"

# Parse TRX test results from a work item
hlx test-results 02d8bd09 "dotnet-watch.Tests.dll.1"
```

Accepts bare GUIDs or full Helix URLs:
```bash
hlx status https://helix.dot.net/api/jobs/02d8bd09-9400-4e86-8d2b-7a6ca21c5009/details
```

> If running from a local build instead of the installed tool, substitute `dotnet run --project src/HelixTool --` for `hlx`.

### MCP Server

**Stdio (recommended for local use)** — launched automatically by MCP clients:

```bash
hlx mcp
```

**HTTP (for remote/shared servers)**:

```bash
# Start the HTTP MCP server (default port 5000)
dotnet run --project src/HelixTool.Mcp

# Or on a specific port
dotnet run --project src/HelixTool.Mcp --urls http://localhost:3001
```

## MCP Configuration

Add the following to your MCP client config. The `--yes` flag ensures `dnx` doesn't prompt for confirmation:

```json
{
  "servers": {
    "hlx": {
      "type": "stdio",
      "command": "dnx",
      "args": ["lewing.helix.mcp", "--yes"]
    }
  }
}
```

> If you've installed `lewing.helix.mcp` as a global tool, you can use `"command": "hlx"` with `"args": []` instead of `dnx`.

### Config file locations

| Client | Config file | Top-level key |
|--------|------------|---------------|
| **VS Code / GitHub Copilot** | `.vscode/mcp.json` | `servers` |
| **Claude Desktop** (macOS) | `~/Library/Application Support/Claude/claude_desktop_config.json` | `mcpServers` |
| **Claude Desktop** (Windows) | `%APPDATA%\Claude\claude_desktop_config.json` | `mcpServers` |
| **Claude Code / Cursor** | `.cursor/mcp.json` | `mcpServers` |

> **Note:** VS Code uses the `servers` key (shown above). Claude Desktop, Claude Code, and Cursor use `mcpServers` instead — the rest of the JSON is identical.

### HTTP alternative (for remote/shared servers)

```json
{
  "servers": {
    "hlx": {
      "type": "http",
      "url": "http://localhost:3001"
    }
  }
}
```

## MCP Tools

| Tool | Description |
|------|-------------|
| `hlx_status` | Get work item pass/fail summary for a Helix job. Accepts a `filter` parameter: `failed` (default), `passed`, or `all`. Returns structured JSON with job metadata, failed items (with exit codes, state, duration, machine, failure category), and passed count. |
| `hlx_logs` | Get console log content for a work item. Returns the log text directly (last N lines if `tail` specified, default 500). |
| `hlx_files` | List uploaded files for a work item, grouped by type. Returns binlogs, testResults, and other files with names and URIs. |
| `hlx_download` | Download files from a work item to a temp directory. Supports glob patterns (e.g., `*.binlog`). Returns local file paths. |
| `hlx_download_url` | Download a file by direct blob storage URL (e.g., from `hlx_files` output). Returns the local file path. |
| `hlx_find_files` | Search work items in a job for files matching a glob pattern (`*.binlog`, `*.trx`, `*.dmp`, etc.). Returns work item names and matching file URIs. |
| `hlx_find_binlogs` | Scan work items in a job to find which ones contain binlog files. Shortcut for `hlx_find_files` with `*.binlog` pattern. |
| `hlx_work_item` | Get detailed info about a specific work item: exit code, state, machine, duration, failure category, console log URL, and uploaded files. |
| `hlx_batch_status` | Get status for multiple Helix jobs at once (max 50). Accepts an array of job IDs/URLs. Returns per-job summaries, overall totals, and failure breakdown by category. |
| `hlx_search_log` | Search a work item's console log for lines matching a pattern. Returns matching lines with context. Supports `contextLines` and `maxMatches` parameters. |
| `hlx_search_file` | Search an uploaded file's content for lines matching a pattern — without downloading it. Supports context lines and max match limits. Disabled when `HLX_DISABLE_FILE_SEARCH=true`. |
| `hlx_test_results` | Parse TRX test result files from a work item. Returns structured test results: names, outcomes, durations, and error messages/stack traces for failures. Auto-discovers `.trx` files or filter to a specific one. Disabled when `HLX_DISABLE_FILE_SEARCH=true`. |

## CLI Commands

| Command | Description |
|---------|-------------|
| `hlx status <jobId> [failed\|passed\|all]` | Work item summary. Filter is a positional arg (default: `failed`). |
| `hlx logs <jobId> <workItem>` | Download console log to a temp file and print the path. |
| `hlx files <jobId> <workItem>` | List uploaded files for a work item. |
| `hlx download <jobId> <workItem> [--pattern PAT]` | Download work item files. Glob pattern (default: `*`). |
| `hlx download-url <url>` | Download a file by direct blob storage URL. |
| `hlx find-files <jobId> [--pattern PAT] [--max-items N]` | Search work items for files matching a glob pattern. |
| `hlx find-binlogs <jobId> [--max-items N]` | Shortcut for `find-files --pattern "*.binlog"`. |
| `hlx work-item <jobId> <workItem>` | Detailed work item info (exit code, state, machine, files). |
| `hlx batch-status <jobId1> <jobId2> ...` | Status for multiple jobs in parallel. |
| `hlx search-log <jobId> <workItem> <pattern> [--context N] [--max-matches N]` | Search console log for a pattern. |
| `hlx search-file <jobId> <workItem> <fileName> <pattern> [--context N] [--max-matches N]` | Search an uploaded file for a pattern. |
| `hlx test-results <jobId> <workItem> [--file-name NAME] [--include-passed] [--max-results N]` | Parse TRX test results from a work item. |
| `hlx cache status` | Show cache size, entry count, oldest/newest entries. |
| `hlx cache clear` | Wipe all cached data (all auth contexts). |
| `hlx mcp` | Start MCP server over stdio. Also the default when no command is given. |

## Failure Categorization

Failed work items are automatically classified into one of: **Timeout**, **Crash**, **BuildFailure**, **TestFailure**, **InfrastructureError**, **AssertionFailure**, or **Unknown**. The category appears in `status`, `work-item`, and `batch-status` output, and is available as `failureCategory` in JSON and MCP tool responses.

## Project Structure

```
src/
├── HelixTool/              # CLI tool + stdio MCP server
│   └── Program.cs           # Console commands via ConsoleAppFramework + MCP server
├── HelixTool.Core/         # Shared library — Helix API logic + MCP tool definitions
│   ├── HelixService.cs     # Core operations (status, logs, files, download)
│   ├── HelixMcpTools.cs    # MCP tool definitions ([McpServerToolType])
│   ├── HelixIdResolver.cs  # GUID and URL parsing
│   ├── IHelixApiClient.cs  # Helix API abstraction
│   ├── HelixApiClient.cs   # Helix API implementation
│   ├── IHelixApiClientFactory.cs  # Per-request client creation (HTTP multi-auth)
│   ├── IHelixTokenAccessor.cs     # Token resolution abstraction
│   ├── HelixException.cs   # Typed exceptions
│   └── Cache/              # SQLite-backed response caching
│       ├── SqliteCacheStore.cs       # Cache storage implementation
│       ├── CachingHelixApiClient.cs  # Transparent caching wrapper
│       ├── CacheSecurity.cs          # Path traversal protection
│       ├── CacheOptions.cs           # TTL, size, auth isolation config
│       └── ICacheStore.cs            # Cache store abstraction
├── HelixTool.Mcp/          # MCP HTTP server
│   ├── Program.cs                         # ASP.NET Core + ModelContextProtocol
│   └── HttpContextHelixTokenAccessor.cs   # Per-request token from Authorization header
└── HelixTool.Tests/        # Unit tests (369 tests)
```

## Authentication

No authentication is needed for public Helix jobs (dotnet open-source CI). For internal/private jobs, set the `HELIX_ACCESS_TOKEN` environment variable:

```bash
# Get your token from https://helix.dot.net → Profile → Access Tokens
export HELIX_ACCESS_TOKEN=your-token-here

# Or for a single command
HELIX_ACCESS_TOKEN=your-token hlx status <jobId>
```

For MCP clients, pass the token in the server config:

```json
{
  "servers": {
    "hlx": {
      "type": "stdio",
      "command": "dnx",
      "args": ["lewing.helix.mcp", "--yes"],
      "env": {
        "HELIX_ACCESS_TOKEN": "your-token-here"
      }
    }
  }
}
```

### HTTP MCP server (per-request auth)

The HTTP MCP server (`HelixTool.Mcp`) supports per-request authentication via the `Authorization` header:

```
Authorization: Bearer <token>
Authorization: token <token>
```

Each authenticated client gets isolated cache storage. If no header is present, the server falls back to the `HELIX_ACCESS_TOKEN` environment variable. This enables shared/remote MCP server deployments where multiple users connect with different credentials.

**API key auth:** Set `HLX_API_KEY` to require an `X-Api-Key` header on every request. When set, requests without a valid key receive `401 Unauthorized`. This is independent of Helix token auth — it gates access to the server itself.

If a job requires authentication and no token is set, hlx will show an actionable error message.

## Caching

Helix API responses are automatically cached to a local SQLite database — no configuration needed.

| Setting | Default | Env var |
|---------|---------|---------|
| Max cache size | 1 GB | `HLX_CACHE_MAX_SIZE_MB` (set to `0` to disable) |
| Cache location (Windows) | `%LOCALAPPDATA%\hlx\` | — |
| Cache location (Linux/macOS) | `$XDG_CACHE_HOME/hlx/` | — |
| Artifact expiry | 7 days without access | — |

**TTL policy:** Running jobs use short TTLs (15–30s). Completed jobs cache for 1–4h. Console logs are never cached while a job is still running.

**Auth isolation:** Each unique token gets its own cache directory (`cache-{hash}`). Unauthenticated requests use `public/`. The HTTP MCP server isolates per-request tokens automatically.

**CLI commands:**

```bash
hlx cache status   # Show cache size, entry count, oldest/newest entries
hlx cache clear    # Wipe all cached data (all auth contexts)
```

## Security

- **Safe XML parsing:** TRX files are parsed with `DtdProcessing.Prohibit`, `XmlResolver = null`, and a 50 MB character limit to prevent XXE and billion-laughs attacks.
- **Path traversal protection:** All cache paths and download file names are sanitized via `CacheSecurity` — directory separators are replaced and `..` sequences are stripped. Resolved paths are validated to stay within their designated root.
- **URL scheme validation:** `hlx_download_url` only accepts HTTP/HTTPS URLs; other schemes are rejected.
- **File search toggle:** Set `HLX_DISABLE_FILE_SEARCH=true` to disable `hlx_search_file`, `hlx_search_log`, and `hlx_test_results`. Useful for locked-down deployments where file content inspection is not desired.
- **Input validation:** Job IDs are resolved through `HelixIdResolver` (GUIDs and URLs). Batch operations are capped at 50 jobs per request. File search is limited to 50 MB files.

### Cached data

The SQLite cache (`%LOCALAPPDATA%\hlx\` on Windows, `$XDG_CACHE_HOME/hlx/` on Linux/macOS) stores Helix API responses and downloaded artifacts on disk. Cached data includes job metadata, work item details, console logs, and uploaded files like binlogs and TRX results. Console logs and test output from CI runs may inadvertently contain secrets such as connection strings or tokens — treat the cache directory as potentially sensitive.

**What is NOT cached:** Authentication tokens are never written to the cache. The `HELIX_ACCESS_TOKEN` is used only for API requests and to derive an 8-character SHA256 hash for cache directory isolation (`cache-{hash}/`). The hash is not reversible to the original token.

**Access control:** The cache lives in the current user's profile directory, protected by OS-level file permissions. Each unique Helix token gets its own isolated cache directory; unauthenticated requests use a separate `public/` directory. No cross-token data leakage is possible. On shared machines or when switching between security contexts, run `hlx cache clear` to wipe all cached data. Cached metadata expires via TTL (15s–4h depending on job state), and artifact files expire after 7 days without access.

## Requirements

- .NET 10 SDK

## Known Issues

- **File listing uses `ListFiles` endpoint** — hlx uses the `ListFilesAsync` endpoint for work item
  file listing, which correctly returns file URIs for files in subdirectories and files with unicode
  characters. This avoids the known bug in the `Details` endpoint where file URIs are broken
  ([dotnet/dnceng#6072](https://github.com/dotnet/dnceng/issues/6072)).

## How to find Helix job IDs

Helix job IDs appear in Azure DevOps build logs. Look for tasks like "Send to Helix" or "Wait for Helix" — the job ID is a GUID in the log output. You can also use the [`azp`](https://github.com/AzurePipelinesTool/AzurePipelinesTool) CLI to find them.

## License

MIT
