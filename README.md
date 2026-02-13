# helix.mcp — Helix Test Infrastructure CLI & MCP Server

A CLI tool and MCP server for investigating [.NET Helix](https://helix.dot.net) test results. Designed for diagnosing CI failures in dotnet repos (runtime, sdk, aspnetcore, etc.).

## Architecture

The project is split into three layers:

- **HelixTool.Core** — Shared library containing `HelixService`, `IHelixApiClient`, and model types. All Helix API logic lives here.
- **HelixTool** — CLI tool built with [ConsoleAppFramework](https://github.com/Cysharp/ConsoleAppFramework). Serves both human-readable terminal commands and a stdio MCP server (`hlx mcp`). MCP is also the default mode when no subcommand is given.
- **HelixTool.Mcp** — Standalone MCP HTTP server built with [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk). Returns structured JSON for LLM agents over HTTP.

Both the CLI and MCP server depend on Core but not on each other.

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
# Check a Helix job (shows failed work items)
hlx status 02d8bd09-9400-4e86-8d2b-7a6ca21c5009

# Show all work items including passed
hlx status 02d8bd09 --all

# Download console log for a failed work item
hlx logs 02d8bd09 "dotnet-watch.Tests.dll.1"

# List uploaded files (binlogs, test results, etc.)
hlx files 02d8bd09 "dotnet-watch.Tests.dll.1"

# Download binlogs from a work item
hlx download 02d8bd09 "dotnet-watch.Tests.dll.1" "*.binlog"

# Scan work items to find which ones have binlogs
hlx find-binlogs 02d8bd09

# Download a file by direct URL (from hlx files output)
hlx download-url "https://helix..."

# Show detailed info about a specific work item
hlx work-item 02d8bd09 "dotnet-watch.Tests.dll.1"

# Check status of multiple jobs at once
hlx batch-status 02d8bd09 a1b2c3d4

# Search console log for error patterns
hlx search-log 02d8bd09 "dotnet-watch.Tests.dll.1" "error CS"
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
| `hlx_status` | Get work item pass/fail summary for a Helix job. Returns structured JSON with job metadata, failed items (with exit codes), and passed count. |
| `hlx_logs` | Get console log content for a work item. Returns the last N lines (default 500). |
| `hlx_files` | List uploaded files for a work item. Returns file names, URIs, and tags (binlog, test-results). |
| `hlx_download` | Download files from a work item to a temp directory. Supports glob patterns (e.g., `*.binlog`). |
| `hlx_download_url` | Download a file by direct blob storage URL (from `hlx_files` output). |
| `hlx_find_binlogs` | Scan work items in a job to find which ones contain binlog files. |
| `hlx_work_item` | Get detailed info about a specific work item: exit code, state, machine, duration, console log URL, and uploaded files. |
| `hlx_batch_status` | Query status for multiple Helix jobs in parallel. Returns per-job summary and overall totals. |
| `hlx_search_log` | Search a work item's console log for error patterns. Supports context lines and max match limits. |

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
│   └── HelixException.cs   # Typed exceptions
├── HelixTool.Mcp/          # MCP HTTP server
│   └── Program.cs           # ASP.NET Core + ModelContextProtocol
└── HelixTool.Tests/        # Unit tests
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

If a job requires authentication and no token is set, hlx will show an actionable error message.

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
