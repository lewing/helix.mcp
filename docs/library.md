# Using HelixTool.Core as a Library

`HelixTool.Core` is available as a standalone NuGet package (`lewing.helix.core`) for programmatic access to the Helix API. Use it to build custom CI dashboards, failure analysis tools, or integrate Helix data into your own applications.

## Installation

```bash
dotnet add package lewing.helix.core
```

### NuGet Feed Requirement

`lewing.helix.core` depends on `Microsoft.DotNet.Helix.Client`, which is published to the [dotnet-eng](https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json) Azure Artifacts feed. You **must** add this feed to your project's `nuget.config`:

```xml
<configuration>
  <packageSources>
    <add key="dotnet-eng" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

If you don't have a `nuget.config`, create one in your project or solution root:

```bash
dotnet new nugetconfig
```

Then add the `dotnet-eng` source as shown above.

## Basic Setup

```csharp
using HelixTool.Core;

// Create an API client (no token needed for public jobs)
var apiClient = new HelixApiClient();

// Create the service
var helix = new HelixService(apiClient);
```

## Quick Example

```csharp
// Get job status with failure categorization
var summary = await helix.GetJobStatusAsync("02d8bd09-9400-4e86-8d2b-7a6ca21c5009");

Console.WriteLine($"Job: {summary.JobName} — {summary.FailedItems.Count} failed, {summary.PassedItems.Count} passed");

foreach (var item in summary.FailedItems)
    Console.WriteLine($"  ✗ {item.Name} ({item.FailureCategory}) exit={item.ExitCode}");

// Search a work item's console log
var results = await helix.SearchConsoleLogAsync("02d8bd09", "MyTest.dll.1", "error CS");
foreach (var match in results.Matches)
    Console.WriteLine($"  Line {match.LineNumber}: {match.Text}");

// Parse TRX test results
var trx = await helix.ParseTrxResultsAsync("02d8bd09", "MyTest.dll.1");
foreach (var result in trx)
    Console.WriteLine($"  {result.TestName}: {result.Outcome}");
```

## Authentication

**No token needed for public dotnet CI jobs** — this is the most common case. Just create a `HelixApiClient()` with no arguments and you're good to go.

For private or internal jobs, provide a token:

```csharp
// Pass token directly
var apiClient = new HelixApiClient("your-token");
```

**`HELIX_ACCESS_TOKEN` environment variable** — set this in CI/CD pipelines. Read it at client construction:

```csharp
var token = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
var apiClient = new HelixApiClient(token);
```

**`IHelixTokenAccessor`** — for advanced scenarios like web apps that need per-request auth (e.g., different tokens per user). Most consumers won't need this.

## Error Handling

All Helix API errors are thrown as `HelixException`:

```csharp
try
{
    var summary = await helix.GetJobStatusAsync(jobId);
}
catch (HelixException ex)
{
    Console.WriteLine($"Helix API error: {ex.Message}");
}
```

## DI Registration

For applications using `Microsoft.Extensions.DependencyInjection`:

```csharp
services.AddSingleton<IHelixApiClient>(sp =>
    new HelixApiClient(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
services.AddSingleton<HelixService>();
```

## Temp File Cleanup

`DownloadConsoleLogAsync` and `DownloadFilesAsync` write downloaded artifacts to temporary directories. Each call uses its own isolated temp directory. **Callers are responsible for cleaning up these directories** when the files are no longer needed:

```csharp
var logPath = await helix.DownloadConsoleLogAsync("02d8bd09", "MyTest.dll.1");
try
{
    // Use the downloaded log file...
    var content = await File.ReadAllTextAsync(logPath);
}
finally
{
    // Clean up the temp file
    if (File.Exists(logPath))
        File.Delete(logPath);
}
```

## Key Types

| Type | Description |
|------|-------------|
| `HelixService` | Main entry point for all Helix operations. Wraps `IHelixApiClient` with higher-level methods. |
| `HelixApiClient` | HTTP client for the Helix REST API. Pass an optional token for authenticated access. |
| `JobSummary` | Result of `GetJobStatusAsync` — contains job metadata, `FailedItems`, and `PassedItems`. |
| `WorkItemResult` | Individual work item in a job summary — name, exit code, state, failure category. |
| `WorkItemDetail` | Detailed info for a single work item — includes files, console log URL, duration. |
| `TrxTestResult` | A single parsed test result from a TRX file — test name, outcome, duration, error message. |
| `TrxParseResult` | Collection of parsed TRX results from a work item. |
| `FailureCategory` | Enum classifying failures: `Timeout`, `Crash`, `BuildFailure`, `TestFailure`, `InfrastructureError`, `AssertionFailure`, `Unknown`. |
| `HelixException` | Thrown for all Helix API errors. |

## See Also

- [README](../README.md) — CLI usage, MCP server setup, and installation
- [MCP Tools](../README.md#mcp-tools) — full MCP tool reference
