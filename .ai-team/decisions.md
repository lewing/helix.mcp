# Decisions

> Shared team decisions — the single source of truth for architectural and process choices.
### 2025-07-18: Architecture Review — hlx project improvement proposal

**By:** Dallas
**Requested by:** Larry Ewing

---

## 1. Architecture — Core/CLI/MCP Split

**Verdict:** The three-project split is sound in principle. Clean separation of concerns. But there are structural problems underneath:

### 1a. Namespace collision

All three projects use `namespace HelixTool`. The Core library, the CLI, and the MCP server all live in the same namespace. This is confusing and fragile — it means you can't tell from a `using HelixTool;` statement which assembly a type comes from. The MCP project even sets `<RootNamespace>HelixTool</RootNamespace>` explicitly to force this.

**Recommendation:** Use distinct namespaces:
- `HelixTool.Core` → `namespace HelixTool.Core`
- `HelixTool` (CLI) → `namespace HelixTool.Cli` (or keep `HelixTool` since it's the leaf app)
- `HelixTool.Mcp` → `namespace HelixTool.Mcp`

### 1b. HelixService is a god class

`HelixService` contains all Helix API operations in a single class: job status, work item files, log download, file download, binlog scanning. At 143 lines it's manageable today, but this is the class that grows unbounded as features are added.

**Recommendation:** Consider splitting by domain concern:
- `HelixJobService` — job status/summary
- `HelixArtifactService` — files, downloads, binlogs
- `HelixLogService` — console log retrieval

Not urgent at current size. Flag for when the next 2-3 features land.

### 1c. Record types defined inline

`WorkItemResult`, `JobSummary`, `FileEntry`, `BinlogResult` are all nested records inside `HelixService`. They're return types used by both CLI and MCP consumers. They belong in their own file(s) as top-level types in the Core project.

**Recommendation:** Extract to `Models.cs` or individual files in a `Models/` folder.

### 1d. Empty `Display/` folder

The CLI project has an empty `Display/` folder. Either use it (for Spectre.Console rendering logic — which would be a good idea, the CLI's `Program.cs` has raw `Console.ForegroundColor` calls despite having Spectre.Console as a dependency) or delete it.

**Recommendation:** Move CLI rendering into `Display/` classes using Spectre.Console, or remove the folder and the Spectre.Console dependency until it's needed.

---

## 2. Testability

**Verdict: This is the biggest problem.** The code is essentially untestable as written.

### 2a. `HelixApi` is `new()`'d directly — no DI, no abstraction

```csharp
// HelixService.cs
private readonly HelixApi _api = new();

// HelixMcpTools.cs
private static readonly HelixService _svc = new();

// Program.cs (CLI)
private readonly HelixService _svc = new();
```

Three separate instantiation points, zero injection. `HelixApi` is a concrete type from `Microsoft.DotNet.Helix.Client` — there is no interface to mock.

**Recommendation:**
1. Define an `IHelixApi` interface (or a thin wrapper interface) in Core that mirrors the `HelixApi` surface area actually used (Job.DetailsAsync, WorkItem.ListAsync, etc.)
2. Inject it into `HelixService` via constructor
3. Register `HelixService` in DI in both CLI and MCP hosts
4. For tests, provide a fake/mock implementation

Alternatively, if the Helix client SDK exposes interfaces for its sub-clients (`IJob`, `IWorkItem`), wrap only the parts we use.

### 2b. `HelixMcpTools` uses static state

```csharp
private static readonly HelixService _svc = new();
```

Static fields prevent injection. MCP tools should receive `HelixService` via constructor or method injection. The `ModelContextProtocol` SDK supports DI — the MCP tool class can take constructor parameters if registered properly.

### 2c. `HelixIdResolver` is fine

Static utility class with pure functions. Easily testable as-is. Good.

### 2d. File I/O in service layer

`DownloadConsoleLogAsync` and `DownloadFilesAsync` write directly to the filesystem (`File.Create`, `Directory.CreateDirectory`). This makes them hard to test without actual disk I/O.

**Recommendation:** Either:
- Accept a `Stream` or output path as a parameter (push the decision up)
- Abstract file I/O behind an interface (overkill for this project)
- Accept this as a pragmatic trade-off and test these methods via integration tests only

I'd go with option 3 for now.

---

## 3. Error Handling

**Verdict: Essentially non-existent.** Happy path only. No try/catch anywhere in the codebase.

### 3a. No exception handling in HelixService

If `_api.Job.DetailsAsync(id)` throws (404, network error, invalid GUID), the raw exception propagates to the CLI/MCP consumer. The user sees a .NET stack trace.

**Recommendation:**
- Wrap API calls in try/catch at the service level
- Define a `HelixException` or use result types (`Result<T, Error>`) for expected failures
- At minimum: catch `HttpRequestException` and provide a human-readable message ("Job not found", "Helix API unreachable")

### 3b. No validation on inputs

- `ResolveJobId` returns the raw input if it can't parse it — it doesn't tell the caller it failed
- No validation that `workItem` is non-empty
- `id[..8]` in file paths will throw `ArgumentOutOfRangeException` if the ID is too short

**Recommendation:**
- `ResolveJobId` should return `string?` or throw `ArgumentException` if the input is neither a GUID nor a parseable URL. Silent pass-through is dangerous.
- Add `ArgumentException.ThrowIfNullOrWhiteSpace()` guards on public methods.

### 3c. No cancellation support

No methods accept `CancellationToken`. Long-running operations (scanning 30 work items, downloading files) can't be cancelled.

**Recommendation:** Add `CancellationToken cancellationToken = default` to all async methods.

---

## 4. Code Quality

### 4a. Naming is good

Method names (`GetJobStatusAsync`, `FindBinlogsAsync`), record names (`JobSummary`, `FileEntry`), command names (`status`, `logs`, `files`) — all clear and consistent. No complaints.

### 4b. JsonSerializerOptions allocated repeatedly

```csharp
// Appears 4 times in HelixMcpTools.cs
new JsonSerializerOptions { WriteIndented = true }
```

**Recommendation:** Hoist to a `private static readonly` field.

### 4c. Console output in CLI is raw

The CLI uses `Console.ForegroundColor`/`Console.ResetColor` everywhere despite having `Spectre.Console` as a dependency. Either use Spectre properly (it handles color, tables, progress bars) or remove the dependency.

### 4d. `SemaphoreSlim` in `GetJobStatusAsync` is good

Throttling concurrent work item detail requests to 10 is smart. Well done. But the semaphore isn't disposed — should be in a `using` or wrapped.

### 4e. `MatchesPattern` is limited

Only handles `*`, `*.ext`, and substring match. Not a real glob implementation. This is fine for current use but the parameter is documented as "glob pattern" which overpromises.

**Recommendation:** Either rename the parameter documentation to "file extension or substring filter" or use `Microsoft.Extensions.FileSystemGlobbing` for real glob support.

---

## 5. Missing Features / Capabilities

### 5a. No `--output` / `--format` flag

The CLI always writes to stdout with ANSI colors. There's no `--json` flag for machine-readable output. The MCP side returns JSON, but the CLI doesn't.

**Recommendation:** Add `--json` flag that reuses the same serialization logic as MCP tools. Useful for piping into `jq` or other tools.

### 5b. No work item detail command

You can get a job summary and list files, but there's no command to get details about a specific work item (state, exit code, logs URI, duration, machine info).

### 5c. No queue/machine info

Helix jobs run on specific queues and machines. When diagnosing infra issues (not test failures), you need to know which machine ran a work item. This data is available from the API.

### 5d. No retry/correlation support

When investigating flaky tests, you often need to find the same test across multiple Helix jobs. A "find this work item across recent jobs" capability would be very useful.

### 5e. No test results parsing

`.trx` files are tagged in `FileEntry` but never parsed. For a diagnostic tool, being able to show which tests failed/passed from the TRX file (without downloading and opening it in Visual Studio) would be high value.

### 5f. No authentication support

README says "No authentication needed for public Helix jobs" — but internal/private jobs do need auth. The tool should support token-based auth for those scenarios eventually.

### 5g. No caching

Every invocation hits the Helix API fresh. Job details don't change once a job completes. A simple disk cache (keyed by job ID + completion status) would speed up repeated queries significantly.

---

## Priority Ranking

| Priority | Item | Effort |
|----------|------|--------|
| **P0** | Testability — inject HelixApi, add DI | Medium |
| **P0** | Error handling — catch API errors, validate inputs | Medium |
| **P1** | Namespace cleanup | Small |
| **P1** | Extract model records to own files | Small |
| **P1** | CancellationToken support | Small |
| **P1** | Remove static state from MCP tools | Small |
| **P2** | Use Spectre.Console properly or remove it | Small |
| **P2** | `--json` output flag for CLI | Small |
| **P2** | Work item detail command | Medium |
| **P3** | TRX parsing | Large |
| **P3** | Caching | Medium |
| **P3** | Auth support | Medium |

---

**Decision:** This proposal is ready for discussion. No changes should be made until Larry confirms priorities. The P0 items (testability + error handling) should be tackled first as they're foundational — everything else is harder to do safely without tests.

### 2025-07-17: Documentation improvement proposal *(superseded by 2026-02-12 implementation)*

**By:** Kane
**What:** Audit identified 15 documentation improvements needed: llmstxt missing MCP tools, public records missing XML doc comments, README missing install instructions, no LICENSE file, llmstxt indentation bug.
**Status:** Items 1–4 implemented in session 2026-02-11-p1-features. Remaining gaps: HelixIdResolver XML docs, HelixMcpTools class doc, LICENSE file, `dotnet tool install` instructions.

### 2025-07-14: MatchesPattern exposed via InternalsVisibleTo
**By:** Lambert
**What:** Changed `HelixService.MatchesPattern` from `private static` to `internal static` and added `<InternalsVisibleTo Include="HelixTool.Tests" />` to `HelixTool.Core.csproj` to enable direct unit testing.
**Why:** Cleanest approach for testing private logic — no reflection, no test helpers, no public API surface change. The method stays invisible to external consumers while being testable. If anyone adds more internal methods to Core, they're automatically testable too.

### 2025-07-14: Caching Strategy for Helix API Responses and Artifacts *(superseded by 2026-02-12 Cache Implementation)*

**By:** Dallas
**Status:** ⛔ Superseded — see "2026-02-12: Refined Cache Requirements" and "2026-02-12: Cache Implementation Design Review" below.

### 2025-07-18: Revised Cache TTL Policy *(superseded by 2026-02-12 Cache Implementation)*

**By:** Dallas
**Status:** ⛔ Superseded — TTL matrix carried forward into the 2026-02-12 refined requirements. Key change: storage moved from in-memory to SQLite-backed (cross-process), max size bumped from 500MB to 1GB, XDG-compliant cache location.

### 2025-07-18: Requirements backlog formalized — 18 user stories extracted from session 72e659c1
**By:** Ash
**What:** Created `.ai-team/requirements.md` with 18 user stories (US-1 through US-18), categorized into Implemented, Planned, Architectural, and Discovered requirements. Prioritized P0–P3 with ownership assignments. P0 items are: layered architecture (US-7), logs-out-of-context principle (US-8), DI/testability (US-12), and error handling (US-13).
**Why:** The session contained requirements scattered across plan.md, architecture docs, checkpoint notes, and implicit workflow observations. No single source had the full picture. The team needs a single backlog to prioritize from, and the P0 items (testability + error handling) must land before any feature work — Dallas identified this correctly and I'm reinforcing it as the requirements owner. Ripley should not pick up P1/P2 stories until US-12 and US-13 are done.

### 2026-02-11: P0 Foundation Design Review — IHelixApiClient, DI, HelixException, CancellationToken (D1–D10)

**By:** Dallas
**Date:** 2026-02-11
**Context:** Design review for US-12 (DI/testability) and US-13 (error handling)

**Decisions:**
- **D1:** `IHelixApiClient` interface in `HelixTool.Core` — 6 methods mirroring actual SDK usage, all with `CancellationToken`
- **D2:** `HelixApiClient` wraps `new HelixApi()` — single instantiation point
- **D3:** `HelixService` constructor-injects `IHelixApiClient`, no default constructor
- **D4:** CLI constructs `HelixService(new HelixApiClient())` manually
- **D5:** MCP `HelixMcpTools` becomes non-static with constructor injection; `IHelixApiClient` and `HelixService` as singletons
- **D6:** `HelixException` — single exception type; catches `HttpRequestException` and `TaskCanceledException` only
- **D7:** Input validation — `ArgumentException.ThrowIfNullOrWhiteSpace` guards; `ResolveJobId` throws on invalid input
- **D8:** `CancellationToken cancellationToken = default` on all async methods, threaded through all awaits
- **D9:** `IHelixApiClient` is the only mock boundary; Lambert uses NSubstitute
- **D10:** `JsonSerializerOptions` hoisted to static field in `HelixMcpTools`

**Key risks:** MCP SDK may not support instance tool methods; Helix SDK return types may be concrete (may need DTOs).

### 2026-02-11: P0 Implementation — Runtime Decisions

**By:** Ripley
**Date:** 2026-02-11
**Context:** Discovered during P0 implementation (US-12, US-13)

- **TaskCanceledException timeout detection:** Use `cancellationToken.IsCancellationRequested` (true = real cancellation, false = HTTP timeout), NOT `ex.CancellationToken == cancellationToken`. The equality check fails when both tokens are `CancellationToken.None` (default parameter).
- **Helix SDK model types are concrete:** `JobDetails`, `WorkItemSummary`, `WorkItemDetails`, `UploadedFile` have no interfaces. Solved with projection interfaces (`IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile`) in `IHelixApiClient.cs` and private adapter classes in `HelixApiClient.cs`.
- **ConsoleAppFramework DI:** CLI uses `ConsoleApp.ServiceProvider = services.BuildServiceProvider()` before `ConsoleApp.Create()`. CAF v5 supports DI natively via this static property pattern.

### 2026-02-11: P0 Test Infrastructure Decisions

**By:** Lambert
**Date:** 2026-02-11
**Context:** Test creation for P0 foundation

- **NSubstitute 5.* chosen as mocking framework** per Dallas's D9 recommendation. Simpler API than Moq (no `Setup(...).Returns(...)` ceremony, no `.Object` property). `.ThrowsAsync()` from `NSubstitute.ExceptionExtensions` maps cleanly to D6 error handling contract.
- **HelixIdResolver invalid-input tests updated for D7 breaking change:** Replaced 5 pass-through tests with `ArgumentException` throw assertions. Happy-path GUID/URL extraction tests unchanged.
- **Proactive parallel test writing validated:** 19 tests written against design spec before implementation existed. All compiled and passed once Ripley's code landed.

### 2026-02-11: US-1 & US-20 Implementation — Positional Args + Rich Status Output

**By:** Ripley
**Date:** 2025-07-18

- **US-1 (Positional Arguments):** Applied `[Argument]` attribute to `jobId` on all 5 commands and `workItem` on logs/files/download. Named `--job-id` flag still works. Updated `llmstxt` to reflect positional syntax.
- **US-20 (Rich Status Output):** Expanded `IWorkItemDetails` with `State`, `MachineName`, `Started`, `Finished`. `WorkItemResult` now includes `State`, `MachineName`, `Duration`. CLI shows `[FAIL] Name (exit code 1, 2m 34s, machine: helix-win-01)`. MCP JSON includes per-work-item `state`, `machineName`, `duration`.
- **FormatDuration duplication:** Helper is duplicated between CLI and MCP. Acceptable for two consumers; extract to Core if a third appears.
- **Test impact:** Updated mock setup for new `IWorkItemDetails` fields. 38/38 tests pass.

### 2026-02-12: Documentation fixes — llmstxt, README, XML doc comments

**By:** Kane
**Date:** 2026-02-12
**Requested by:** Larry Ewing

- **llmstxt indentation fix** — Raw string literal now uses `var text = """...""";` pattern with proper indentation stripping.
- **MCP tools added to llmstxt** — All five MCP tools documented with JSON return shapes, parameters, and CLI-vs-MCP guidance.
- **README.md updated** — Added Architecture section, Installation section (build-from-source + NuGet feed), Known Issues section (dnceng#6072 workaround, US-28).
- **XML doc comments** — Added to `IHelixApiClient` (all 6 methods), `HelixApiClient`, `HelixException`, `HelixService` (constructor, all public methods, all 4 public record types).
- **Decision for team:** The llmstxt content and README now serve as the authoritative docs for the public API surface. When new commands or MCP tools are added, both must be updated together.
- **Remaining gaps:** `HelixIdResolver` XML docs, `HelixMcpTools` class-level doc comment, LICENSE file, `dotnet tool install` instructions.

### 2026-02-12: US-4 Authentication Design — HELIX_ACCESS_TOKEN env var, optional token constructor

**By:** Dallas
**Date:** 2026-02-12
**Requested by:** Larry Ewing

- **D-AUTH-1:** Token source is `HELIX_ACCESS_TOKEN` environment variable. Matches arcade naming convention. No config file, no secrets on disk.
- **D-AUTH-2:** `HelixApiClient` constructor accepts optional `string? accessToken`. Null/empty → anonymous (zero breakage). Non-empty → authenticated via `HelixApiTokenCredential`.
- **D-AUTH-3:** All 3 DI registrations (CLI, MCP stdio, MCP HTTP) read env var at startup with `Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")`.
- **D-AUTH-4:** `IHelixApiClient` interface unchanged. Auth is a transport concern inside `HelixApiClient`. No test impact.
- **D-AUTH-5:** `HelixService` catches 401/403 with actionable error: "Set HELIX_ACCESS_TOKEN environment variable to access internal builds."
- **D-AUTH-6:** MCP client config example: `{ "command": "hlx", "args": ["mcp"], "env": { "HELIX_ACCESS_TOKEN": "<token>" } }`
- **D-AUTH-7:** No token acquisition built into tool. No `hlx auth login`. Token is opaque string from env var. ~35 lines of code, no new dependencies.

### 2026-02-12: Stdio MCP Transport — `hlx mcp` subcommand (Option B approved)

**By:** Dallas
**Date:** 2026-02-12
**Requested by:** Larry Ewing

- **Decision:** Support stdio MCP via `hlx mcp` subcommand in CLI binary. Not in separate HelixTool.Mcp project.
- **Why:** All primary consumers (Copilot CLI, Claude Desktop, VS Code, ci-analysis skill) use stdio. Single binary install aligns with US-5.
- **Packages:** CLI adds `ModelContextProtocol` (base, ~100KB) + `Microsoft.Extensions.Hosting`. No ASP.NET Core for stdio.
- **HelixTool.Mcp kept:** HTTP transport remains for remote/multi-client scenarios.
- **Logging:** All output to stderr via `LogToStandardErrorThreshold = LogLevel.Trace`.
- **MCP client config:** `{ "command": "hlx", "args": ["mcp"] }`
- **Risks:** ConsoleAppFramework + Host may conflict; `mcp` command bypasses CAF and runs Host directly. Tool assembly scanning needs explicit assembly reference if tools are in Core.

### 2026-02-12: Stdio MCP Implementation — Runtime decisions

**By:** Ripley
**Date:** 2026-02-12
**Implements:** Dallas's stdio MCP design

- `HelixMcpTools.cs` copied to CLI project (same namespace, discovered by `WithToolsFromAssembly()`).
- `mcp` command creates its own DI container via `Host.CreateApplicationBuilder()` — does not reuse CLI's `ServiceCollection`.
- HelixTool.Mcp unchanged (HTTP transport stays).
- Duplication accepted; extract to Core if maintenance burden grows.
- Build and 55/55 tests pass.

### 2026-02-12: MCP Tools Test Strategy

**By:** Lambert
**Date:** 2025-07-18

- Tests reference HelixTool.Mcp via `ProjectReference`. `HelixMcpTools` lives in `namespace HelixTool`.
- `FormatDuration` tested indirectly through Status output (6 branches via arranged timestamps).
- MCP tool methods return JSON strings; tests parse with `JsonDocument` and assert structure/values.
- Download error path returns `{error: "..."}` JSON rather than throwing.
- **Impact:** If `HelixMcpTools` is removed from HelixTool.Mcp, test ProjectReference must change.

### 2026-02-12: US-5 + US-25 Implementation — dotnet tool packaging + ConsoleLogUrl

**By:** Ripley
**Date:** 2026-02-12
**Requested by:** Larry Ewing

- **US-5 (dotnet tool):** Added `<Version>0.1.0</Version>` to HelixTool.csproj. Updated `<Description>` and `<Authors>`. `PackAsTool`, `ToolCommandName`, `PackageId` already existed.
- **US-25 (ConsoleLogUrl):** `WorkItemResult` record gained 6th positional parameter `string ConsoleLogUrl`. URL: `https://helix.dot.net/api/2019-06-17/jobs/{id}/workitems/{name}/console`. CLI shows URL below failed items. MCP JSON includes `consoleLogUrl` for all items.
- Both `HelixMcpTools.cs` copies (HelixTool + HelixTool.Mcp) updated. Must be kept in sync.

### US-17: Namespace Cleanup — Project-correct namespaces

**By:** Ripley
**Date:** 2026-02-12
**Requested by:** Larry Ewing

**What:** Changed all production code namespaces to match their project names:
- `HelixTool.Core` (5 files): `namespace HelixTool;` → `namespace HelixTool.Core;`
- `HelixTool.Mcp` (1 file): `namespace HelixTool;` → `namespace HelixTool.Mcp;`
- `HelixTool` CLI (2 files): kept as `namespace HelixTool;` (correct for the leaf app)

**Why:** All three projects previously used `namespace HelixTool;`, making it impossible to tell which assembly a type came from. With distinct namespaces, `using` directives now make assembly provenance explicit.

**Changes:**
- Removed `<RootNamespace>HelixTool</RootNamespace>` from `HelixTool.Mcp.csproj`
- Added `using HelixTool.Core;` to all consumers and `using HelixTool.Mcp;` to test files referencing MCP tools

**Impact:** No behavioral changes — pure mechanical refactoring. All 74 tests pass. New files in Core/Mcp should use `namespace HelixTool.Core;` / `namespace HelixTool.Mcp;`.

### US-24 + US-30 Implementation — Download by URL + Structured Agent-Friendly JSON

**By:** Ripley
**Date:** 2026-02-12

**US-30 (Structured agent-friendly JSON):**
- `hlx_files` changed from flat array to grouped object `{ binlogs, testResults, other }` (breaking change).
- `hlx_status` job object now includes `jobId` (resolved GUID) and `helixUrl`.
- `JobSummary` record gained `JobId` as first positional parameter (breaking change).

**US-24 (Download by direct URL):**
- Static `HttpClient` in HelixService for direct URL downloads (separate from `IHelixApiClient`).
- `DownloadFromUrlAsync` not mockable through existing test boundary — uses raw HTTP, not Helix SDK.
- Filename extraction via `Uri.Segments[^1]` with `Uri.UnescapeDataString`.

### US-29: MCP Input Flexibility — URL parsing for jobId + workItem

**By:** Ripley
**Date:** 2026-02-12
**Requested by:** Larry Ewing

**What:** MCP tools (`hlx_logs`, `hlx_files`, `hlx_download`) now accept full Helix work item URLs in the `jobId` parameter, automatically extracting both job ID and work item name. The `workItem` parameter is now optional when the URL contains both values.

**Changes:**
- `HelixIdResolver.TryResolveJobAndWorkItem(string input, out string jobId, out string? workItem)` — new method in Core. Parses Helix URLs to extract GUID after `jobs/` and work item name after `workitems/`. URL-decodes work item names. Skips known trailing segments (`console`, `files`, `details`).
- `hlx_logs`, `hlx_files`, `hlx_download` in both `HelixMcpTools.cs` copies — `workItem` changed from required `string` to optional `string?`. URL resolution logic runs when `workItem` is empty. Returns structured JSON error if `workItem` still missing.
- `hlx_status`, `hlx_find_binlogs` — unchanged (no `workItem` parameter).

**Design decisions:**
- **Try-pattern over exceptions:** `TryResolveJobAndWorkItem` returns bool + out params rather than throwing. The existing `ResolveJobId` (which throws) is unchanged.
- **Known trailing segments list:** `["console", "files", "details"]` — hardcoded array.
- **Resolution only when workItem is empty:** Explicit parameters always win.
- **Both HelixMcpTools copies updated in sync.**

**Impact:** Non-breaking. 81/81 tests pass.

### US-18 + US-11: Remove Spectre.Console + Add --json CLI flag

**By:** Ripley
**Date:** 2026-02-12
**Requested by:** Larry Ewing

**US-18 (Remove unused Spectre.Console):**
- Removed `Spectre.Console` v0.54.1-alpha.0.31 from `HelixTool.csproj`. The package was never imported or used in any `.cs` file — CLI output uses raw `Console.ForegroundColor`/`Console.ResetColor`.
- Deleted empty `Commands/` and `Display/` directories from `src/HelixTool/`.
- If Spectre.Console rendering is desired in the future, re-add the dependency and create proper display classes.

**US-11 (--json flag on status and files commands):**
- `status` command: Added `bool json = false` parameter. When `--json`, serializes `JobSummary` to JSON matching MCP tool structure (`job`, `totalWorkItems`, `failedCount`, `passedCount`, `failed`, `passed`). Passed items included only when `--all --json` used together.
- `files` command: Added `bool json = false` parameter. When `--json`, serializes file list to grouped JSON (`binlogs`, `testResults`, `other`) matching MCP's US-30 structure.
- Shared `JsonSerializerOptions` via `private static readonly` field on `Commands` class (per D10 convention from `HelixMcpTools`).
- `using System.Text.Json;` added to `Program.cs`.
- JSON output goes to stdout; progress messages (`Fetching job details...`) still go to stderr.

**Impact:** Non-breaking. All 81 tests pass. No new dependencies added. One dependency removed.


### 2026-02-12: US-10 (Work Item Detail) and US-23 (Batch Status) Implementation


**By:** Ripley
**Date:** 2026-02-12
**Status:** Implemented

**US-10 (Work Item Detail):** New `GetWorkItemDetailAsync` method in `HelixService` returns detailed info about a single work item — exit code, state, machine, duration, console log URL, and file list with type tags. Design: parallel fetch via `Task.WhenAll`, `WorkItemDetail` nested record, CLI `hlx work-item <jobId> <workItem> [--json]`, MCP `hlx_work_item` with US-29 URL resolution, standard error handling.

**US-23 (Batch Status):** New `GetBatchStatusAsync` method queries status for multiple jobs in parallel with `SemaphoreSlim(5)` throttling. Returns `BatchJobSummary` with per-job results and aggregate totals. Design: simple error propagation (exceptions bubble up), CLI one-line-per-job summary, MCP accepts comma-separated job IDs.

## Files Changed
- `src/HelixTool.Core/HelixService.cs` — `WorkItemDetail`, `GetWorkItemDetailAsync`, `BatchJobSummary`, `GetBatchStatusAsync`
- `src/HelixTool/Program.cs` — `work-item` and `batch-status` commands
- `src/HelixTool/HelixMcpTools.cs` — `hlx_work_item` and `hlx_batch_status` tools
- `src/HelixTool.Mcp/HelixMcpTools.cs` — same tools for HTTP MCP server


### 2026-02-12: US-21 Failure Categorization


**By:** Ripley
**Date:** 2026-02-12

## Decision

Added `FailureCategory` enum and `ClassifyFailure` heuristic classifier to `HelixService`. Each failed work item is now tagged with a category (Timeout, Crash, BuildFailure, TestFailure, InfrastructureError, or Unknown) based on exit code, state string, and work item name.

## Key Design Choices

1. **Enum in Core namespace, not nested in HelixService** — `FailureCategory` is a top-level enum in `HelixTool.Core` namespace since it's used across CLI, MCP, and potentially tests. Follows the same pattern discussion from D1c about extracting types.

2. **Nullable on record** — `FailureCategory?` is null for passed items, populated only for failures. This avoids confusing "Unknown" on passing items.

3. **Static classifier method** — `ClassifyFailure` is `public static` on `HelixService` for easy testing and reuse. No instance state needed.

4. **Heuristic priority** — State-based checks (Timeout) take priority over exit-code-based checks. Special case: exit code -1 with no state → Unknown (common for "not yet reported" items). Exit code 1 defaults to TestFailure when no name signal available, since test failures are the most common exit-1 case in Helix.

5. **Both HelixMcpTools.cs copies updated** — Identical changes to both CLI and Mcp project copies. `failureBreakdown` dict added to batch-status JSON output in both.

## Files Modified

- `src/HelixTool.Core/HelixService.cs` — enum, classifier, WorkItemResult (7 params), WorkItemDetail (8 params)
- `src/HelixTool/Program.cs` — CLI output for status, work-item, batch-status
- `src/HelixTool/HelixMcpTools.cs` — MCP JSON for hlx_status, hlx_work_item, hlx_batch_status
- `src/HelixTool.Mcp/HelixMcpTools.cs` — same MCP changes

## Test Impact

No test modifications needed — all 100 existing tests pass. The new `FailureCategory?` parameter is transparently constructed inside `GetJobStatusAsync`/`GetWorkItemDetailAsync`. Lambert should add tests for `ClassifyFailure` covering all heuristic branches.

---

### 2026-02-12: US-22: Console Log Search / Pattern Extraction

**By:** Ripley
**Date:** 2026-02-12
**Requested by:** Larry Ewing

## Decision

Implemented `SearchConsoleLogAsync` in HelixService to search console log content for patterns. Added CLI command `search-log` and MCP tool `hlx_search_log` in both HelixMcpTools.cs files.

## Design Choices

1. **Reuses DownloadConsoleLogAsync**: Rather than adding a new streaming search path, the method downloads the log to a temp file (existing infrastructure), reads all lines, searches in memory, then cleans up. Simple and correct for typical log sizes.

2. **LogMatch record with optional Context**: `LogMatch(int LineNumber, string Line, List<string>? Context = null)` — Context is the full window of lines (before + match + after) when contextLines > 0. This avoids the CLI/MCP needing to re-read the file for context rendering.

3. **Temp file cleanup**: The downloaded log file is deleted in a `finally` block after search completes, since it's only needed transiently.

4. **No error handler duplication**: SearchConsoleLogAsync delegates to DownloadConsoleLogAsync which already has the standard error handling pattern (HttpRequestException, TaskCanceledException, etc.), so no additional try/catch needed in the search method itself.

## Files Changed

- `src/HelixTool.Core/HelixService.cs` — Added LogSearchResult, LogMatch records and SearchConsoleLogAsync method
- `src/HelixTool/Program.cs` — Added `search-log` CLI command
- `src/HelixTool/HelixMcpTools.cs` — Added `hlx_search_log` MCP tool
- `src/HelixTool.Mcp/HelixMcpTools.cs` — Added `hlx_search_log` MCP tool (mirror)

---

### 2026-02-12: Consolidate HelixMcpTools into HelixTool.Core

**By:** Ripley
**Requested by:** Larry Ewing

`HelixMcpTools.cs` was duplicated in two projects:
- `src/HelixTool/HelixMcpTools.cs` (namespace `HelixTool`) — used by stdio MCP via `hlx mcp`
- `src/HelixTool.Mcp/HelixMcpTools.cs` (namespace `HelixTool.Mcp`) — used by HTTP MCP server

Both copies contained identical logic and had to be kept in sync manually. Consolidated to a single `src/HelixTool.Core/HelixMcpTools.cs` with `namespace HelixTool.Core`.

**Changes:**

1. **Created** `src/HelixTool.Core/HelixMcpTools.cs` — single source of truth, namespace `HelixTool.Core`
2. **Added** `ModelContextProtocol` v0.8.0-preview.1 package to `HelixTool.Core.csproj` (needed for `[McpServerToolType]`/`[McpServerTool]` attributes)
3. **Deleted** `src/HelixTool/HelixMcpTools.cs`
4. **Deleted** `src/HelixTool.Mcp/HelixMcpTools.cs`
5. **Updated** `src/HelixTool/Program.cs` — `.WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)` (explicit assembly reference since tools now live in Core)
6. **Updated** `src/HelixTool.Mcp/Program.cs` — same explicit assembly reference
7. **Updated** test files — changed `using HelixTool.Mcp;` → `using HelixTool.Core;`, removed duplicate using directives
8. **Updated** `HelixTool.Tests.csproj` — removed `ProjectReference` to `HelixTool.Mcp` (no longer needed; tests only need Core)

**Key insight:** `WithToolsFromAssembly()` without arguments only scans the calling assembly. Since `HelixMcpTools` moved to `HelixTool.Core.dll`, both consumers must use `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`.

**Verification:** `dotnet build` — 0 errors, 0 warnings. `dotnet test` — 126/126 passed.



### 2025-07-21: CI workflow added at .github/workflows/ci.yml
**By:** Ripley
**What:** Created a GitHub Actions CI workflow that runs on push/PR to main/master. Matrix: ubuntu-latest + windows-latest. Uses .NET 10 preview SDK. Steps: checkout, restore, build, test. NuGet restore uses the repo-root nuget.config which already includes the dotnet-eng Azure Artifacts feed.
**Why:** The project had no CI. This gives us build+test validation on every PR and push to main branches, on both Linux and Windows, matching the cross-platform nature of the tool.

### 2026-02-11: McpServer package type support
**By:** Ripley
**What:** Added PackageType McpServer and .mcp/server.json to HelixTool.csproj for dnx zero-install support
**Why:** Enables `dnx hlx mcp` pattern — MCP clients can reference hlx without requiring pre-installation

### 2025-07-23: Rename NuGet package from `hlx` to `lewing.helix.mcp`
**By:** Ripley
**What:** Changed PackageId from `hlx` to `lewing.helix.mcp` in HelixTool.csproj. Updated `.mcp/server.json` to use Chet's 2025-10-17 schema format with `registryType`/`identifier` fields, added `title`, `version`, `websiteUrl`. Added PackageTags, PackageReadmeFile, PublishRepositoryUrl, and Content item to pack README.md. ToolCommandName (`hlx`) is unchanged.
**Why:** Follows the established `{owner}.{tool}.mcp` naming convention (same as `baronfel.binlog.mcp`). The old bare `hlx` name was too generic for a public NuGet package and didn't convey ownership or purpose. The server.json update aligns with the latest MCP server registry schema that tools like VS Code and Copilot CLI consume for zero-install discovery.


### 2025-02-12: NuGet Trusted Publishing workflow
**By:** Ripley
**What:** Created `.github/workflows/publish.yml` that publishes `lewing.helix.mcp` to nuget.org on `v*` tag push using NuGet Trusted Publishing (OIDC) via `NuGet/login@v1`. Creates a GitHub Release with the nupkg attached. No API key secrets — only `NUGET_USER` is needed. Pattern adapted from baronfel/mcp-binlog-tool.
**Why:** Trusted Publishing is the modern NuGet approach — OIDC tokens are short-lived and scoped to the workflow, eliminating long-lived API key secrets. The workflow mirrors CI's .NET 10 preview SDK setup for consistency. Using `-o src/HelixTool/nupkg` gives a predictable output path for both the push glob and the release artifact attachment. Changelog support intentionally deferred — simple `Release ${{ github.ref_name }}` body for now.


### 2026-02-12: Refined Cache Requirements — SQLite-backed, Cross-Process Shared Cache
**By:** Larry (via Coordinator)
**What:** Refined caching requirements superseding the original Dallas design. Key changes: SQLite-backed (not in-memory), cross-process shared cache for stdio MCP server instances, XDG-compliant cache location, 1GB default cap (configurable).

**Why:** Each MCP stdio invocation is a fresh process — in-memory cache is useless for the primary use case. SQLite provides safe concurrent access across multiple hlx instances (WAL mode), structured queryable metadata, and reliable cross-process sharing.

---

#### Architecture

- **Storage:** SQLite database for API metadata + disk files for downloaded artifacts (tracked by SQLite)
- **SQLite location:** `{cache_root}/hlx/cache.db`
- **Artifact directory:** `{cache_root}/hlx/artifacts/{jobId-prefix}/{workItem}/{fileName}`
- **Cache root resolution:**
  - Windows: `%LOCALAPPDATA%`
  - Linux/macOS: `$XDG_CACHE_HOME` (fallback: `~/.cache`)
- **Concurrency:** SQLite WAL mode for safe concurrent reads/writes across processes

#### TTL Matrix (per Dallas's revised design, unchanged)

| Data Type | Running Job | Completed Job |
|---|---|---|
| Job details (name, queue, creator, state) | 15s | 4h |
| Work item list (names, counts) | 15s | 4h |
| Work item details (exit code, state per item) | 15s | 4h |
| File listing (per work item) | 30s | 4h |
| Console log content | **NO CACHE** | 1h |
| Downloaded artifact (binlog, trx, files on disk) | Until eviction | Until eviction |
| Binlog scan results (FindBinlogsAsync) | 30s | 4h |

#### Console Logs — Never Cache While Running

Console logs for running work items are append-only streams. Bypass cache entirely when job has no `Finished` timestamp. For completed jobs, cache for 1 hour.

#### Disk Cache Eviction

- **Max size:** Configurable, default 1 GB
- **TTL-based cleanup:** Artifacts older than 7 days (by last access) are evicted
- **LRU eviction:** When over max size, oldest-accessed files deleted first
- **Cleanup triggers:** At startup + after every download operation

#### CLI Commands

- `hlx cache clear` — Wipe all cached data (SQLite + artifact files)
- `hlx cache status` — Show cache size, entry count, oldest/newest entries

#### Configuration

- Max cache size configurable (environment variable or config file — TBD by implementer)
- Default: 1 GB

### 2026-02-12: Cache Implementation Design Review
**By:** Dallas
**What:** SQLite-backed cross-process caching layer for hlx — interface design, integration strategy, schema, risk assessment, and action items for Ripley (implementation) and Lambert (tests).
**Why:** Each MCP stdio invocation (`hlx mcp`) is a fresh process. In-memory caching is useless for the primary use case. SQLite provides safe concurrent access across multiple hlx instances (WAL mode), structured queryable metadata, and reliable cross-process sharing. This design review translates Larry's refined requirements into concrete interfaces, classes, and tasks.

---

## 1. Architectural Decision: Decorator on IHelixApiClient

**Decision: Decorator pattern — `CachingHelixApiClient` wrapping `IHelixApiClient`.**

Rejected alternative: caching inside `HelixService`. Reasons:
- `HelixService` is already 600 lines. Adding cache logic doubles it.
- Cache concerns (TTL, eviction, SQLite) are orthogonal to business logic (failure classification, log search, batch status).
- The decorator is invisible to `HelixService` — it sees the same `IHelixApiClient` interface it already depends on.
- Testing: Lambert can test cache behavior by wrapping a mock `IHelixApiClient` with `CachingHelixApiClient`, without touching `HelixService` tests.

**The decorator intercepts 6 `IHelixApiClient` methods with cache-aware logic:**

| Method | Cache Strategy |
|---|---|
| `GetJobDetailsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `ListWorkItemsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `GetWorkItemDetailsAsync` | Cache. Running: 15s TTL. Completed: 4h TTL. |
| `ListWorkItemFilesAsync` | Cache. Running: 30s TTL. Completed: 4h TTL. |
| `GetConsoleLogAsync` | **Running: NO CACHE.** Completed: 1h TTL. Returns Stream — cache to disk, serve from disk. |
| `GetFileAsync` | Cache to disk. Eviction-based only (no TTL). LRU with 1GB cap. |

**Job state determination:** To apply the correct TTL, the decorator must know if a job is running or completed. Strategy: before checking the TTL for any sub-resource, the decorator checks its own SQLite cache for that job's `Finished` timestamp. If not cached, it calls the inner client's `GetJobDetailsAsync` (which is itself cached). This is a single extra call at most, amortized across all subsequent lookups for that job.

---

## 2. New Types and File Layout

All new code lives in `HelixTool.Core` (namespace `HelixTool.Core`).

### New Files

| File | Type | Purpose |
|---|---|---|
| `Cache/ICacheStore.cs` | Interface | Abstract cache storage — enables testing without real SQLite |
| `Cache/SqliteCacheStore.cs` | Class | SQLite + disk file implementation of `ICacheStore` |
| `Cache/CachingHelixApiClient.cs` | Class | Decorator implementing `IHelixApiClient`, delegates to inner client + `ICacheStore` |
| `Cache/CacheOptions.cs` | Record | Configuration: max size, cache root, TTLs |
| `Cache/CacheStatus.cs` | Record | Return type for `hlx cache status` |

### Interface: `ICacheStore`

```csharp
namespace HelixTool.Core;

/// <summary>
/// Abstract cache storage for API metadata and artifact files.
/// Implementations handle persistence (SQLite+disk) and eviction.
/// </summary>
public interface ICacheStore : IDisposable
{
    // Metadata (JSON-serialized API responses)
    Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default);
    Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default);

    // Artifact files (console logs, binlogs, downloaded files)
    Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default);
    Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default);

    // Job state cache (needed for TTL decisions)
    Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default);
    Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default);

    // Management
    Task ClearAsync(CancellationToken ct = default);
    Task<CacheStatus> GetStatusAsync(CancellationToken ct = default);
    Task EvictExpiredAsync(CancellationToken ct = default);
}

public record CacheStatus(
    long TotalSizeBytes,
    int MetadataEntryCount,
    int ArtifactFileCount,
    DateTimeOffset? OldestEntry,
    DateTimeOffset? NewestEntry,
    long MaxSizeBytes);
```

### Class: `CacheOptions`

```csharp
namespace HelixTool.Core;

public record CacheOptions
{
    /// <summary>Maximum cache size in bytes. Default: 1 GB.</summary>
    public long MaxSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Cache root directory. Default: platform-appropriate XDG path.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>Artifact expiry (last access). Default: 7 days.</summary>
    public TimeSpan ArtifactMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Resolve the actual cache root, respecting XDG conventions.</summary>
    public string GetEffectiveCacheRoot()
    {
        if (!string.IsNullOrEmpty(CacheRoot)) return CacheRoot;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hlx");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(!string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "hlx");
    }
}
```

### Class: `CachingHelixApiClient`

```csharp
namespace HelixTool.Core;

/// <summary>
/// Decorator that adds SQLite-backed caching to any IHelixApiClient.
/// Injected between DI registration and HelixService.
/// </summary>
public sealed class CachingHelixApiClient : IHelixApiClient
{
    private readonly IHelixApiClient _inner;
    private readonly ICacheStore _cache;

    public CachingHelixApiClient(IHelixApiClient inner, ICacheStore cache) { ... }

    // Each method: check cache → return if hit → call inner → store → return
    // TTL selection based on IsJobCompletedAsync result
}
```

---

## 3. DI Integration — Program.cs Changes

Current registration (CLI):
```csharp
services.AddSingleton<IHelixApiClient>(_ => new HelixApiClient(...));
services.AddSingleton<HelixService>();
```

New registration:
```csharp
services.AddSingleton<CacheOptions>(_ =>
{
    var opts = new CacheOptions();
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
    return opts;
});
services.AddSingleton<ICacheStore>(sp =>
{
    var opts = sp.GetRequiredService<CacheOptions>();
    return new SqliteCacheStore(opts);
});
services.AddSingleton<HelixApiClient>(_ => new HelixApiClient(...));
services.AddSingleton<IHelixApiClient>(sp =>
    new CachingHelixApiClient(
        sp.GetRequiredService<HelixApiClient>(),
        sp.GetRequiredService<ICacheStore>()));
services.AddSingleton<HelixService>();
```

**Same pattern applies to the `mcp` command's DI container.** The `HelixTool.Mcp` HTTP project gets the same registration if desired, but it's lower priority since it's long-lived (in-memory cache matters more there).

**`cache clear` and `cache status` commands** access `ICacheStore` directly from DI:
```csharp
[Command("cache clear")]
public async Task CacheClear()
{
    var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
    await cache.ClearAsync();
    Console.WriteLine("Cache cleared.");
}

[Command("cache status")]
public async Task CacheStatus()
{
    var cache = ConsoleApp.ServiceProvider!.GetRequiredService<ICacheStore>();
    var status = await cache.GetStatusAsync();
    // Format and print
}
```

**Note on ConsoleAppFramework:** CAF v5 supports nested command groups. `hlx cache clear` and `hlx cache status` may need to be registered as `app.Add("cache", ...)` or as a separate `CacheCommands` class. Ripley should verify the CAF subcommand routing pattern.

---

## 4. SQLite Schema

### Package Choice: `Microsoft.Data.Sqlite`

**Decision: `Microsoft.Data.Sqlite` (Microsoft's ADO.NET provider).**

Rejected:
- `sqlite-net-pcl` — ORM-ish, auto-creates tables from C# classes. Convenient but hides SQL, harder to control schema precisely, no built-in migration story.
- `EF Core SQLite` — massive dependency for what's essentially two tables. EF migrations are overkill.
- `Microsoft.Data.Sqlite` — lightweight (~200KB), raw SQL, explicit schema control, first-party Microsoft package. We need two tables and a few indexes. This is the right tool.

### Tables

```sql
-- API metadata cache (JSON blobs)
CREATE TABLE IF NOT EXISTS cache_metadata (
    cache_key   TEXT PRIMARY KEY,
    json_value  TEXT NOT NULL,
    created_at  TEXT NOT NULL,  -- ISO 8601
    expires_at  TEXT NOT NULL,  -- ISO 8601
    job_id      TEXT NOT NULL   -- for join/cleanup queries
);

CREATE INDEX IF NOT EXISTS idx_metadata_expires ON cache_metadata(expires_at);
CREATE INDEX IF NOT EXISTS idx_metadata_job ON cache_metadata(job_id);

-- Downloaded artifact files (tracked by SQLite, stored on disk)
CREATE TABLE IF NOT EXISTS cache_artifacts (
    cache_key       TEXT PRIMARY KEY,
    file_path       TEXT NOT NULL,    -- relative to artifacts dir
    file_size       INTEGER NOT NULL, -- bytes
    created_at      TEXT NOT NULL,    -- ISO 8601
    last_accessed   TEXT NOT NULL,    -- ISO 8601, updated on read
    job_id          TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_artifacts_accessed ON cache_artifacts(last_accessed);
CREATE INDEX IF NOT EXISTS idx_artifacts_job ON cache_artifacts(job_id);

-- Job completion state (for TTL decisions)
CREATE TABLE IF NOT EXISTS cache_job_state (
    job_id       TEXT PRIMARY KEY,
    is_completed INTEGER NOT NULL,  -- 0 or 1
    finished_at  TEXT,              -- ISO 8601, null if running
    cached_at    TEXT NOT NULL,     -- ISO 8601
    expires_at   TEXT NOT NULL      -- ISO 8601
);
```

### Cache Key Format

| Data Type | Cache Key Pattern |
|---|---|
| Job details | `job:{jobId}:details` |
| Work item list | `job:{jobId}:workitems` |
| Work item details | `job:{jobId}:wi:{workItem}:details` |
| File listing | `job:{jobId}:wi:{workItem}:files` |
| Console log | `job:{jobId}:wi:{workItem}:console` |
| Downloaded file | `job:{jobId}:wi:{workItem}:file:{fileName}` |

### Artifact File Path on Disk

`{cache_root}/hlx/artifacts/{jobId[0:8]}/{workItem}/{fileName}`

Using first 8 chars of jobId as directory prefix keeps the directory tree shallow and avoids filesystem limits.

### WAL Mode and Concurrency

```csharp
// On connection open:
connection.Execute("PRAGMA journal_mode=WAL;");
connection.Execute("PRAGMA busy_timeout=5000;");  // 5s retry on lock
```

WAL mode allows concurrent reads across processes. Writers queue with busy_timeout. This is safe for our use case: multiple `hlx mcp` processes reading simultaneously, occasional writes when cache misses occur.

---

## 5. Stream Caching Strategy

`GetConsoleLogAsync` and `GetFileAsync` return `Stream`. These need special handling because:
1. We can't serialize a `Stream` to SQLite — we need to write to disk.
2. The caller expects a `Stream` back, not a file path.

**Strategy for cached stream responses:**
1. On cache miss: call inner client, tee the stream to a temp file, return a `FileStream` from the temp file, then move the temp file to the artifact cache location.
2. On cache hit: return `File.OpenRead(cachedFilePath)`.
3. Update `last_accessed` in `cache_artifacts` on every cache hit.

**Implementation detail:** Use a write-then-rename pattern to avoid partial files on crash:
```csharp
var tempPath = artifactPath + ".tmp";
using (var fs = File.Create(tempPath))
    await sourceStream.CopyToAsync(fs, ct);
File.Move(tempPath, artifactPath, overwrite: true);
```

---

## 6. Eviction Implementation

Eviction runs at two points:
1. **Startup** — `SqliteCacheStore` constructor calls `EvictExpiredAsync()` synchronously (or fires-and-forgets with a small delay). Removes expired metadata entries and artifact files older than 7 days.
2. **After every artifact write** — Check total artifact size. If over cap, delete LRU entries until under cap.

```sql
-- Remove expired metadata
DELETE FROM cache_metadata WHERE expires_at < @now;

-- Remove expired artifacts (7-day last-access)
SELECT cache_key, file_path FROM cache_artifacts WHERE last_accessed < @cutoff;
-- Then delete files + rows

-- LRU eviction when over cap
SELECT cache_key, file_path, file_size FROM cache_artifacts ORDER BY last_accessed ASC;
-- Iterate, summing sizes, delete until total <= max
```

After deleting artifact rows, also delete the corresponding disk files. Handle `FileNotFoundException` gracefully (file may have been deleted by another process or manually).

---

## 7. Configuration

**Decision:** Environment variable `HLX_CACHE_MAX_SIZE_MB` (integer, megabytes).

Rejected config file approach — adds complexity for one setting. Environment variables work in all contexts (shell, MCP client config, CI).

Default: `1024` (1 GB per Larry's refined requirements — note: original Dallas design had 500 MB, Larry bumped to 1 GB).

To disable caching entirely: `HLX_CACHE_MAX_SIZE_MB=0` → `CachingHelixApiClient` passes through to inner client without caching.

---

## 8. Risk Assessment

### R1: SQLite Locking Under Heavy Concurrent Access
**Severity:** Medium
**Detail:** Multiple `hlx mcp` processes writing simultaneously could hit SQLITE_BUSY despite WAL mode. Mitigated by `busy_timeout=5000` (5s retry), but under extreme load (10+ concurrent MCP instances all cache-missing on the same job), contention is possible.
**Mitigation:** WAL + busy_timeout is the standard solution. If problems appear in practice, consider connection pooling or write-serialization via a named mutex. Monitor for now.

### R2: Schema Migration
**Severity:** Low (but important to plan for)
**Detail:** Once users have `cache.db` files, we can't casually change the schema. First release must get the schema right.
**Mitigation:** Add a `PRAGMA user_version` check on startup. Current version = 1. If the version doesn't match, drop all tables and recreate (destructive migration is acceptable for a cache — it's all regenerable data).

### R3: Stale "Running" Classification
**Severity:** Low
**Detail:** A job's `is_completed` status is cached. If a job completes while the "running" cache entry is still live (15s TTL), we serve shorter-TTL data for a few extra seconds. This is harmless — worst case we re-fetch data that just became cacheable for longer.
**Mitigation:** None needed. 15s staleness on job state is acceptable.

### R4: Disk Space Accounting Accuracy
**Severity:** Low
**Detail:** `file_size` in `cache_artifacts` is set at write time. If a file is modified externally (shouldn't happen, but possible), the accounting drifts.
**Mitigation:** `cache clear` resets everything. `cache status` could optionally do a fresh disk scan for accurate reporting. Periodic re-scan is overkill.

### R5: Testing Without Real SQLite
**Severity:** Medium (affects Lambert)
**Detail:** `ICacheStore` is the mock boundary for Lambert. Tests should NOT require a real SQLite database. However, `SqliteCacheStore` itself needs integration tests with a real (in-memory or temp-file) SQLite database.
**Mitigation:** Two test tiers:
- **Unit tests:** Mock `ICacheStore` with NSubstitute. Test `CachingHelixApiClient` logic (TTL selection, bypass for running logs, cache hit/miss flow).
- **Integration tests:** Real `SqliteCacheStore` with `:memory:` connection string or temp file. Test schema creation, eviction, concurrent access.

### R6: File Descriptor Leaks on Cached Streams
**Severity:** Medium
**Detail:** `GetArtifactAsync` returns a `FileStream`. If the caller doesn't dispose it, file handles leak. But this is the same contract as the inner client — callers already `await using` the streams.
**Mitigation:** Ensure all returned streams are documented as disposable. No code change needed — existing callers already use `await using`.

---

## 9. Action Items

### For Ripley (Implementation)

| ID | Task | Depends On | Notes |
|---|---|---|---|
| R-CACHE-1 | Add `Microsoft.Data.Sqlite` to `HelixTool.Core.csproj` | — | Version: latest stable |
| R-CACHE-2 | Create `Cache/ICacheStore.cs` | — | Interface per §2 |
| R-CACHE-3 | Create `Cache/CacheOptions.cs` | — | Record per §2, XDG path resolution |
| R-CACHE-4 | Create `Cache/CacheStatus.cs` | — | Record per §2 |
| R-CACHE-5 | Create `Cache/SqliteCacheStore.cs` | R-CACHE-2, R-CACHE-3 | Schema per §4, WAL mode, eviction per §6, `PRAGMA user_version=1` |
| R-CACHE-6 | Create `Cache/CachingHelixApiClient.cs` | R-CACHE-2 | Decorator per §2, TTL matrix per §1, stream caching per §5 |
| R-CACHE-7 | Update CLI `Program.cs` DI | R-CACHE-5, R-CACHE-6 | Registration per §3, both CLI container and `mcp` command container |
| R-CACHE-8 | Add `cache clear` command | R-CACHE-2 | Calls `ICacheStore.ClearAsync()` |
| R-CACHE-9 | Add `cache status` command | R-CACHE-2, R-CACHE-4 | Calls `ICacheStore.GetStatusAsync()`, format output |
| R-CACHE-10 | Update `llmstxt` | R-CACHE-8, R-CACHE-9 | Document new cache commands |
| R-CACHE-11 | Verify CAF subcommand routing | — | `hlx cache clear` / `hlx cache status` — test that CAF supports this or find workaround |

### For Lambert (Tests)

| ID | Task | Depends On | Notes |
|---|---|---|---|
| L-CACHE-1 | Unit tests: `CachingHelixApiClient` cache hit | R-CACHE-6 | Mock `ICacheStore`, verify inner client NOT called on cache hit |
| L-CACHE-2 | Unit tests: `CachingHelixApiClient` cache miss | R-CACHE-6 | Mock `ICacheStore` returning null, verify inner client called, result stored |
| L-CACHE-3 | Unit tests: TTL selection (running vs completed) | R-CACHE-6 | Mock `IsJobCompletedAsync` returning true/false, verify correct TTL passed to `SetMetadataAsync` |
| L-CACHE-4 | Unit tests: Console log bypass for running jobs | R-CACHE-6 | When `IsJobCompletedAsync` returns false, `GetConsoleLogAsync` must NOT cache |
| L-CACHE-5 | Unit tests: Console log cached for completed jobs | R-CACHE-6 | When `IsJobCompletedAsync` returns true, `GetConsoleLogAsync` must cache with 1h TTL |
| L-CACHE-6 | Integration tests: `SqliteCacheStore` CRUD | R-CACHE-5 | Use `:memory:` SQLite. Test set/get/evict/clear/status |
| L-CACHE-7 | Integration tests: Eviction (TTL + LRU) | R-CACHE-5 | Set entries with past expiry, verify eviction. Set entries over max size, verify LRU deletion |
| L-CACHE-8 | Integration tests: Schema creation idempotent | R-CACHE-5 | Open store twice on same DB file, verify no errors |
| L-CACHE-9 | Unit tests: `CacheOptions.GetEffectiveCacheRoot()` | R-CACHE-3 | Test Windows path, test XDG override, test default fallback |
| L-CACHE-10 | Unit tests: Cache disabled when max size = 0 | R-CACHE-6, R-CACHE-7 | `HLX_CACHE_MAX_SIZE_MB=0` → `CachingHelixApiClient` passes through |

---

## 10. Sequencing Recommendation

1. **R-CACHE-1 through R-CACHE-4** — Types and interfaces (no behavior yet). Lambert can start writing test skeletons against `ICacheStore`.
2. **R-CACHE-5** — `SqliteCacheStore` implementation. Lambert writes integration tests (L-CACHE-6 through L-CACHE-8).
3. **R-CACHE-6** — `CachingHelixApiClient` decorator. Lambert writes unit tests (L-CACHE-1 through L-CACHE-5).
4. **R-CACHE-7** — DI wiring. Smoke-test manually.
5. **R-CACHE-8, R-CACHE-9** — CLI commands. Lambert adds L-CACHE-9, L-CACHE-10.
6. **R-CACHE-10, R-CACHE-11** — Documentation and CAF verification.

Ripley should target the interfaces first so Lambert can write tests in parallel.

---

## 11. Open Questions (for Larry)

1. **Should HelixTool.Mcp (HTTP project) also get caching?** It's long-lived so in-memory caching matters more there, but SQLite would give persistence across restarts. Recommendation: yes, same DI registration, low incremental effort.
2. **Should `cache status` show per-job breakdown?** Or just totals? Recommendation: totals only for v1, per-job in v2 if useful.
3. **Should cache be opt-out?** Currently always on. `HLX_CACHE_MAX_SIZE_MB=0` disables, but should there be `--no-cache` flag on individual commands? Recommendation: defer to v2 unless MCP consumers need it.

### 2025-02-13: Consolidate MCP config examples in README — one example + file path table

**By:** Kane
**What:** Replaced three duplicate MCP client config JSON blocks (VS Code, Claude Desktop, Claude Code/Cursor) with a single canonical example plus a table of config file locations and key names. Added `--yes` flag to all `dnx` args. Removed stale "not yet published to nuget.org" notes since v0.1.0 is live.
**Why:** The three JSON blocks were nearly identical — only the top-level key (`servers` vs `mcpServers`) and file path differed. Duplicating them made maintenance error-prone (changes had to be made in 3+ places) and made the README unnecessarily long. The consolidated format is easier to maintain and scan. The `--yes` flag is required for MCP server definitions because `dnx` runs non-interactively when launched by an MCP client. Pattern established: when configs differ only by file path and a single key name, use one example + a table rather than repeating the full block.

### 2025-02-12: Cache Test Suite Complete (L-CACHE-1 through L-CACHE-10)
**By:** Lambert
**What:** 56 tests covering all 10 Dallas cache test action items, across 3 new test files.

---

## Files Created

| File | Tests | Coverage |
|---|---|---|
| `CachingHelixApiClientTests.cs` | 26 | L-CACHE-1 (cache hit), L-CACHE-2 (cache miss), L-CACHE-3 (TTL selection), L-CACHE-4 (console log bypass), L-CACHE-5 (console log cached), L-CACHE-10 (disabled cache) |
| `SqliteCacheStoreTests.cs` | 18 | L-CACHE-6 (CRUD), L-CACHE-7 (eviction), L-CACHE-8 (idempotent schema) |
| `CacheOptionsTests.cs` | 12 | L-CACHE-9 (XDG path resolution, defaults) |

## Test Count

126 → 182 (all passing, 0 warnings)

## Decisions Made

1. **No null-guard constructor tests:** Ripley's `CachingHelixApiClient` constructor does not null-guard its parameters. Rather than writing tests that would immediately fail, I wrote a `Constructor_MaxSizeZero_DisablesCache` test that verifies the important behavioral contract instead. **Suggestion for Ripley:** Consider adding `ArgumentNullException.ThrowIfNull()` guards to the constructor — this is a cheap safety net.

2. **Temp directories for SqliteCacheStore tests:** `SqliteCacheStore` requires file-based SQLite (constructor calls `Directory.CreateDirectory`). Tests use `Path.GetTempPath()` + GUID subdirs with cleanup in `Dispose()`/finally blocks. This is slightly slower than `:memory:` but matches the real usage pattern.

3. **Console log cache miss mock pattern:** The decorator's console log flow calls `GetArtifactAsync` twice (once for miss check, once to return stored result). NSubstitute's `.Returns(null, stream)` sequential return pattern handles this correctly.

## Notes for Ripley

- The `SchemaCreation_OpenTwice_NoErrors` test opens two `SqliteCacheStore` instances on the same directory. This works because of WAL mode but may show occasional `SQLITE_BUSY` under CI load. If this becomes flaky, consider adding `busy_timeout` to the test or serializing access.
- The LRU eviction test uses `MaxSizeBytes = 100` with 60-byte artifacts. This verifies the eviction fires but doesn't deeply test the LRU ordering — a more thorough test would need controlled `last_accessed` timestamps.

### 2026-02-12: Cache Implementation Details
**By:** Ripley
**What:** SQLite-backed caching layer implemented per Dallas's design review (R-CACHE-1 through R-CACHE-11).

**Implementation decisions:**

1. **Microsoft.Data.Sqlite v9.0.7** — latest stable at time of implementation.

2. **CachingHelixApiClient DTO round-tripping** — Private record types (`JobDetailsDto`, `WorkItemSummaryDto`, `WorkItemDetailsDto`, `WorkItemFileDto`) implement the projection interfaces (`IJobDetails`, etc.) directly. This avoids needing a separate deserialization step — the DTOs are both the serialization format and the return type.

3. **Artifact path strategy** — Instead of nested `{jobId[0:8]}/{workItem}/{fileName}`, I used `{jobId[0:8]}/{sanitized_cache_key}` where colons/slashes in the cache key are replaced with underscores. This is simpler and avoids ambiguity when work item names contain path-unsafe characters.

4. **ConsoleAppFramework subcommand routing** — CAF v5 supports `[Command("cache clear")]` and `[Command("cache status")]` directly. No special `app.Add("cache", ...)` registration needed — verified by successful build (R-CACHE-11).

5. **Schema migration strategy** — `PRAGMA user_version=1`. On mismatch, all tables are dropped and recreated (destructive migration is acceptable for cache data). This handles future schema changes cleanly.

6. **Startup eviction** — `EvictExpiredAsync()` is fire-and-forget via `Task.Run()` in the constructor. This avoids blocking startup while still cleaning up stale data.

**For Lambert:** All new types are in `HelixTool.Core` namespace. `ICacheStore` is the mock boundary for unit tests. `SqliteCacheStore` can be tested with `:memory:` connection string or temp file for integration tests. The `CachingHelixApiClient` constructor takes `(IHelixApiClient inner, ICacheStore cache, CacheOptions options)`.

**Files created:**
- `src/HelixTool.Core/Cache/ICacheStore.cs`
- `src/HelixTool.Core/Cache/CacheOptions.cs`
- `src/HelixTool.Core/Cache/CacheStatus.cs`
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs`
- `src/HelixTool.Core/Cache/CachingHelixApiClient.cs`

**Files modified:**
- `src/HelixTool.Core/HelixTool.Core.csproj` (added Microsoft.Data.Sqlite)
- `src/HelixTool/Program.cs` (DI, cache commands, llmstxt update)

---

### 2026-02-13: Cache Auth Isolation (Security Fix)
**By:** Ripley
**Requested by:** Larry Ewing

Separate SQLite databases and artifact directories per auth context, derived from HELIX_ACCESS_TOKEN:
- No token → `{base}/public/cache.db` + `{base}/public/artifacts/`
- Token present → `{base}/cache-{hash}/cache.db` + `{base}/cache-{hash}/artifacts/`

Where `{hash}` = first 8 hex chars of SHA256 of the token (lowercase, deterministic).

**Changes:**
- `CacheOptions.cs`: Added `AuthTokenHash` property, `GetBaseCacheRoot()` method, `ComputeTokenHash()` static helper. `GetEffectiveCacheRoot()` now subdivides by auth context.
- `Program.cs`: Both DI containers pass `CacheOptions.ComputeTokenHash(HELIX_ACCESS_TOKEN)` as `AuthTokenHash`.
- `cache clear` wipes ALL auth context subdirectories. `cache status` shows current auth context info.
- `SqliteCacheStore` unchanged — already uses `GetEffectiveCacheRoot()`.
- Same token always produces the same hash → cache reuse across restarts. Different tokens → different caches.

---

### 2026-02-13: Path Traversal Hardening for Cache and Download Paths (Security Fix)
**By:** Ripley
**Requested by:** Larry Ewing

Defense-in-depth against path traversal attacks via crafted inputs (job IDs, work item names, file names).

**New: `Cache/CacheSecurity.cs`** — Static helper class with three methods:
- `ValidatePathWithinRoot(path, root)` — canonical path traversal defense via `Path.GetFullPath` + prefix check with directory separator.
- `SanitizePathSegment(segment)` — strips `..` and replaces `/` and `\` with `_`.
- `SanitizeCacheKeySegment(value)` — same sanitization for `:`-delimited cache key components.

**Hardened sites:**
- `SqliteCacheStore`: Get/Set/Delete artifact operations validate paths within artifacts dir.
- `CachingHelixApiClient`: All 6 cache key construction sites sanitize user inputs.
- `HelixService`: 3 download methods (DownloadFilesAsync, DownloadFromUrlAsync, DownloadConsoleLogAsync) sanitize file names and validate output paths.

All 182 existing tests pass unchanged — sanitization is transparent for well-formed inputs.

### 2026-02-12: HTTP/SSE multi-client auth architecture for HelixTool.Mcp

**By:** Dallas
**Requested by:** Larry Ewing

**What:** Architectural design for supporting per-client Helix auth tokens in the HTTP/SSE MCP transport (`HelixTool.Mcp`), where the server is a long-running process serving multiple concurrent clients, each with potentially different Helix tokens.

**Why:** The current singleton `HelixApiClient` with a baked-in `HELIX_ACCESS_TOKEN` env var works for stdio (one process per client) but fails for HTTP/SSE where the server is shared. This decision documents the concrete architecture for per-session token isolation using the MCP C# SDK's built-in facilities.

---

## 1. MCP SDK Capabilities (verified against `modelcontextprotocol/csharp-sdk` main)

The SDK provides three critical mechanisms for this scenario:

### 1a. `HttpServerTransportOptions.ConfigureSessionOptions`
```csharp
Func<HttpContext, McpServerOptions, CancellationToken, Task>? ConfigureSessionOptions
```
Called once per new MCP session. Receives the `HttpContext` (including `Authorization` header) and the `McpServerOptions` for that session. This is the hook for extracting per-client tokens.

### 1b. `McpServerOptions.ScopeRequests = true` (default)
Each tool invocation creates a new `IServiceScope`. Services registered as `AddScoped<T>()` get per-request instances. This means scoped services resolve from a child scope created by the SDK per request.

### 1c. `IHttpContextAccessor` availability
When `PerSessionExecutionContext = false` (the default), tool handlers run on the HTTP request's `ExecutionContext`, so `IHttpContextAccessor` works. When `PerSessionExecutionContext = true`, it does NOT work (SDK docs explicitly warn about this).

### 1d. `ClaimsPrincipal` flow
The SDK reads `context.User` from `HttpContext` and flows it into `JsonRpcMessage.Context.User`. It also enforces session-user binding — subsequent requests to an existing session must have the same user identity claim.

## 2. Recommended Architecture

### Token Flow: HTTP Authorization Header → AsyncLocal → Scoped HelixApiClient

**Option chosen: `IHttpContextAccessor` + scoped `IHelixApiClient` factory pattern.**

This is preferred over `ConfigureSessionOptions` + `AsyncLocal<string>` because:
- It uses standard ASP.NET Core patterns (no custom session state management)
- It works with the SDK's `ScopeRequests = true` default (scoped DI per request)
- Token is available in every tool invocation without custom plumbing
- `IHttpContextAccessor` is the documented, supported approach in the SDK

### Approach

1. Client sends Helix token in HTTP `Authorization` header: `Authorization: token <helix-token>`
2. ASP.NET Core middleware (or `ConfigureSessionOptions`) extracts the token
3. Scoped `IHelixApiClient` is resolved per request, receiving the token from `IHttpContextAccessor`

## 3. Concrete Changes

### File: `src/HelixTool.Core/IHelixApiClientFactory.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Factory for creating <see cref="IHelixApiClient"/> instances with a specific auth token.
/// Used in HTTP/SSE MCP transport where each client has its own token.
/// </summary>
public interface IHelixApiClientFactory
{
    IHelixApiClient Create(string? accessToken);
}

public sealed class HelixApiClientFactory : IHelixApiClientFactory
{
    public IHelixApiClient Create(string? accessToken) => new HelixApiClient(accessToken);
}
```

### File: `src/HelixTool.Core/IHelixTokenAccessor.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Abstraction for resolving the current Helix access token.
/// Stdio: returns env var. HTTP: returns token from HttpContext.
/// </summary>
public interface IHelixTokenAccessor
{
    string? GetAccessToken();
}

/// <summary>Fixed token from environment variable — used by stdio transport and CLI.</summary>
public sealed class EnvironmentHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly string? _token;
    public EnvironmentHelixTokenAccessor(string? token) => _token = token;
    public string? GetAccessToken() => _token;
}
```

### File: `src/HelixTool.Mcp/HttpContextHelixTokenAccessor.cs` (NEW)

```csharp
using HelixTool.Core;
using Microsoft.AspNetCore.Http;

namespace HelixTool.Mcp;

/// <summary>
/// Extracts Helix token from the HTTP Authorization header.
/// Expected format: "token {value}" (matching Helix SDK convention).
/// Falls back to HELIX_ACCESS_TOKEN env var if no header present.
/// </summary>
public sealed class HttpContextHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string? _fallbackToken;

    public HttpContextHelixTokenAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _fallbackToken = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
    }

    public string? GetAccessToken()
    {
        var authHeader = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            // "token xxx" or "Bearer xxx"
            var spaceIdx = authHeader.IndexOf(' ');
            if (spaceIdx > 0)
                return authHeader[(spaceIdx + 1)..];
            return authHeader;
        }
        return _fallbackToken;
    }
}
```

### File: `src/HelixTool.Mcp/Program.cs` (MODIFIED)

```csharp
using HelixTool.Core;
using HelixTool.Mcp;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// HttpContext accessor for per-request token resolution
builder.Services.AddHttpContextAccessor();

// Token accessor reads from Authorization header, falls back to env var
builder.Services.AddScoped<IHelixTokenAccessor, HttpContextHelixTokenAccessor>();

// Factory is singleton — creates HelixApiClient instances on demand
builder.Services.AddSingleton<IHelixApiClientFactory, HelixApiClientFactory>();

// IHelixApiClient is SCOPED — one per request, using the per-request token
builder.Services.AddScoped<IHelixApiClient>(sp =>
{
    var tokenAccessor = sp.GetRequiredService<IHelixTokenAccessor>();
    var factory = sp.GetRequiredService<IHelixApiClientFactory>();
    var token = tokenAccessor.GetAccessToken();
    var client = factory.Create(token);

    // Wrap with caching if enabled
    var cacheOptions = new CacheOptions
    {
        AuthTokenHash = CacheOptions.ComputeTokenHash(token)
    };
    var maxStr = Environment.GetEnvironmentVariable("HLX_CACHE_MAX_SIZE_MB");
    if (int.TryParse(maxStr, out var mb))
        cacheOptions = cacheOptions with { MaxSizeBytes = (long)mb * 1024 * 1024 };

    if (cacheOptions.MaxSizeBytes <= 0) return client;

    var cacheStore = sp.GetRequiredService<ICacheStoreFactory>().GetOrCreate(cacheOptions);
    return new CachingHelixApiClient(client, cacheStore, cacheOptions);
});

// HelixService is SCOPED (depends on scoped IHelixApiClient)
builder.Services.AddScoped<HelixService>();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "hlx", Version = "1.0.0" };
        // ScopeRequests = true is the default — each tool call gets a new scope
    })
    .WithHttpTransport()
    .WithToolsFromAssembly(typeof(HelixMcpTools).Assembly);

var app = builder.Build();
app.MapMcp();
app.Run();
```

### File: `src/HelixTool.Core/Cache/ICacheStoreFactory.cs` (NEW)

```csharp
namespace HelixTool.Core;

/// <summary>
/// Factory for obtaining cache stores by auth context.
/// Needed for HTTP mode where multiple auth contexts coexist in one process.
/// Thread-safe: concurrent requests with the same token share the same cache store.
/// </summary>
public interface ICacheStoreFactory
{
    ICacheStore GetOrCreate(CacheOptions options);
}
```

The cache store factory is needed because `SqliteCacheStore` holds an open SQLite connection and should be reused across requests with the same token hash, not recreated per scope. A `ConcurrentDictionary<string, ICacheStore>` keyed by `AuthTokenHash ?? "public"` is the implementation strategy.

### File: `src/HelixTool/Program.cs` (CLI — MINIMAL CHANGES)

The CLI/stdio path stays largely unchanged. Register `IHelixTokenAccessor` as `EnvironmentHelixTokenAccessor` and keep `IHelixApiClient` as singleton:

```csharp
services.AddSingleton<IHelixTokenAccessor>(_ =>
    new EnvironmentHelixTokenAccessor(Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")));
services.AddSingleton<IHelixApiClient>(sp => ...); // unchanged — singleton is correct for stdio
```

## 4. DI Lifetime Summary

| Service | CLI/stdio | HTTP/SSE | Reason |
|---------|-----------|----------|--------|
| `IHelixTokenAccessor` | Singleton (`EnvironmentHelixTokenAccessor`) | Scoped (`HttpContextHelixTokenAccessor`) | CLI: one token per process. HTTP: one token per request context. |
| `IHelixApiClient` | Singleton | **Scoped** | CLI: one client per process. HTTP: per-request with per-client token. |
| `HelixService` | Singleton | **Scoped** | Depends on `IHelixApiClient`. |
| `ICacheStore` | Singleton | **Singleton** (via factory) | Cache stores are thread-safe, shared across requests with same token hash. |
| `ICacheStoreFactory` | Not needed | Singleton | Maps token hash → shared cache store instance. |
| `CacheOptions` | Singleton | Computed per scope | CLI: one config. HTTP: per-request based on token. |

## 5. Cache Interaction Analysis

### Existing design is correct
- Cache isolation by token hash (`CacheOptions.AuthTokenHash` → separate SQLite DBs) already works.
- Two clients with different tokens get independent cache stores/DBs — no cross-contamination.
- Two clients with the SAME token share the same cache store — this is correct and desired (cache hit efficiency).

### Concurrent access safety
- `SqliteCacheStore` uses SQLite WAL mode + `busy_timeout=5000` — already designed for cross-process concurrency.
- Within a single process, concurrent requests hitting the same `SqliteCacheStore` instance are safe because SQLite WAL handles concurrent reads, and writes are serialized by SQLite's internal locking.
- **One concern:** `SqliteCacheStore` currently uses a single `SqliteConnection` field. For concurrent in-process access, this should be changed to connection-per-operation (open/close from a connection pool) or protected with a lock. This is a pre-existing issue that becomes acute under HTTP load.

### Action item
- **R-HTTP-CACHE-1:** `SqliteCacheStore` should use connection pooling (`Data Source={path};Cache=Shared`) or `SemaphoreSlim` for write serialization. Current single-connection design is fine for stdio (sequential access) but risks corruption under concurrent HTTP requests.

## 6. Security Considerations

### Token handling in long-running process
1. **No logging:** Tokens must never appear in logs. The current code doesn't log tokens. The `IHelixTokenAccessor` pattern keeps tokens out of DI descriptions and diagnostics.
2. **Memory:** Tokens live only in `HelixApiClient._api` (Helix SDK `HelixApi` instance) and in the scoped `IHelixTokenAccessor`. Scoped objects are disposed at the end of the request scope. The `HelixApiClient` itself is scoped and gets GC'd after the request.
3. **Header extraction:** The `HttpContextHelixTokenAccessor` reads from `Authorization` header — standard HTTP pattern, protected by TLS in transit.
4. **Session binding:** The MCP SDK enforces that subsequent requests to an existing session come from the same `ClaimsPrincipal` (via `HasSameUserId` check). This prevents session hijacking where client B reuses client A's MCP session ID but with a different identity.
5. **Cache isolation:** Different tokens → different SQLite DBs → complete data separation. A client can only read cache entries from their own auth context.

### What NOT to do
- Don't store tokens in `AsyncLocal<string>` that might leak across requests via thread pool reuse. `IHttpContextAccessor` is the correct abstraction — it uses `AsyncLocal` internally but is scoped to the HTTP request pipeline.
- Don't pass tokens as MCP tool arguments (exposes tokens in tool call logs/traces).
- Don't cache tokens in a static dictionary keyed by session ID (memory leak, no expiry).

## 7. Backward Compatibility

### Zero breaking changes to `IHelixApiClient` interface
The interface is unchanged. The only difference is DI lifetime (singleton vs scoped).

### `HelixMcpTools` is unchanged
It depends on `HelixService` via constructor injection. Whether `HelixService` is singleton or scoped is invisible to the tool class.

### `HelixService` is unchanged
It depends on `IHelixApiClient` via constructor injection. Scoping is handled by DI, not by `HelixService` code.

### CLI binary (`hlx`) is unchanged
Stdio mode continues to use singleton lifetime. The `IHelixTokenAccessor` abstraction can be added opportunistically but isn't strictly required — the CLI can continue to read the env var directly at registration time.

## 8. Implementation Priority & Ordering

This is a **P2** feature. Rationale:
- No user has requested remote MCP yet
- All current consumers (Copilot CLI, ci-analysis skill, Claude Desktop, VS Code) use stdio
- The `HelixTool.Mcp` HTTP project exists but is not actively deployed
- The architecture should be designed now (this document) but implemented when HTTP/SSE demand materializes

When implemented, the ordering should be:

1. **R-HTTP-1:** Add `IHelixTokenAccessor` interface + `EnvironmentHelixTokenAccessor` to Core
2. **R-HTTP-2:** Add `IHelixApiClientFactory` to Core
3. **R-HTTP-3:** Add `ICacheStoreFactory` to Core, refactor `SqliteCacheStore` for concurrent access
4. **R-HTTP-4:** Add `HttpContextHelixTokenAccessor` to HelixTool.Mcp
5. **R-HTTP-5:** Rewire `HelixTool.Mcp/Program.cs` with scoped DI registrations
6. **R-HTTP-6:** (Optional) Rewire `HelixTool/Program.cs` CLI to use `IHelixTokenAccessor` for consistency
7. **L-HTTP-1:** Unit tests for `HttpContextHelixTokenAccessor` with mocked `IHttpContextAccessor`
8. **L-HTTP-2:** Integration test verifying two concurrent MCP sessions with different tokens get isolated results
9. **L-HTTP-3:** Test `ICacheStoreFactory` thread safety (concurrent `GetOrCreate` with same key)
10. **L-HTTP-4:** Test `SqliteCacheStore` under concurrent access

## 9. Alternatives Considered and Rejected

| Option | Verdict | Reason |
|--------|---------|--------|
| **A: `ConfigureSessionOptions` + `AsyncLocal<string>`** | Viable but fragile | Requires `PerSessionExecutionContext = true`, which breaks `IHttpContextAccessor` and ties us to SDK-specific behavior. Standard ASP.NET Core scoping is more robust. |
| **B: Token in MCP tool arguments** | Reject | Exposes secrets in tool call logs. Violates security best practices. Noisy API surface. |
| **C: Server-side config maps client identities to tokens** | Reject for now | Requires user management, identity federation. Overkill. Could layer on top later using ASP.NET Core auth middleware. |
| **D: Singleton `HelixApiClient` + `ConcurrentDictionary<token, HelixApi>`** | Reject | Unbounded memory growth. No clear eviction. Mixes transport concerns into the API client. |
| **E: Do nothing — require separate HTTP server process per token** | Reject for HTTP | Defeats the purpose of HTTP/SSE transport. Users would need N server processes for N tokens. |

## 10. Open Questions for Implementation

1. **SDK version:** We're on `0.8.0-preview.1`. The `ConfigureSessionOptions`, `ScopeRequests`, and `PerSessionExecutionContext` properties exist in the current codebase on `main`. Verify these are available in our pinned version, or upgrade.
2. **Helix SDK client lifecycle:** `HelixApi` (from `Microsoft.DotNet.Helix.Client`) — does it hold `HttpClient` instances that should be long-lived? If so, the scoped pattern should use `IHttpClientFactory` internally rather than creating new `HttpClient` per scope. Investigate `HelixApi` internals.
3. **Token format:** Helix uses `Authorization: token {value}`, not `Bearer`. The `HttpContextHelixTokenAccessor` should accept both formats for flexibility.

### 2026-02-12: Multi-auth support — defer, current design is sufficient

**By:** Dallas
**Requested by:** Larry Ewing

**What:** Analyzed whether hlx should support multiple simultaneous Helix auth tokens (e.g., public + internal, different orgs). Recommendation: **do not implement multi-auth**. The current single-token-per-process model is correct for our execution context.

**Why:**

## Architectural Analysis

### 1. Use cases examined

The plausible scenarios for multi-auth are:
- **Public vs internal jobs** in the same session — a user debugging CI failures across both public OSS and internal proprietary queues.
- **Different orgs/instances** — a user who works across multiple Helix deployments.
- **Different permission levels** — a read-only token for automated scanning, a write token for manual operations.

These are real scenarios, but they're **rare** in practice. The typical hlx user is debugging one CI pipeline at a time. When they switch contexts, they switch environment variables.

### 2. Current constraints make multi-auth unnecessary

**MCP stdio model:** The MCP server runs as `hlx mcp` — a fresh stdio process spawned by the MCP client (VS Code, Claude Desktop, Copilot CLI). The MCP client configuration already specifies env vars per server entry:

```json
{
  "mcpServers": {
    "hlx-public": {
      "command": "hlx",
      "args": ["mcp"],
      "env": {}
    },
    "hlx-internal": {
      "command": "hlx",
      "args": ["mcp"],
      "env": { "HELIX_ACCESS_TOKEN": "secret-token" }
    }
  }
}
```

The user can already configure **two separate MCP server entries** with different tokens. Each gets its own process, its own DI container, its own cache context (the SHA256-based isolation in `CacheOptions` handles this perfectly). This is the MCP-native way to handle multi-auth. We don't need to reinvent it.

**CLI model:** For CLI usage, users can trivially do:
```
HELIX_ACCESS_TOKEN=abc hlx status <internal-job>
hlx status <public-job>
```

Or use shell aliases / direnv / .env files. The operating system already has the right abstraction for per-invocation environment configuration.

### 3. Design options rejected

| Option | Verdict | Reason |
|--------|---------|--------|
| **A: Named connections** (`--connection internal`) | Reject | Requires a connection registry (config file or env var naming scheme). Adds complexity to every command signature. Solves a problem users don't have. |
| **B: Per-request MCP auth passthrough** | Reject | MCP protocol does support auth at the transport level, but it's session-scoped, not per-tool-call. Would require breaking the current clean DI model where `IHelixApiClient` is a singleton. |
| **C: Multiple env vars** (`HELIX_ACCESS_TOKEN_INTERNAL`) | Reject | Ad-hoc naming convention, no discoverability, pollutes the env var namespace. Which one wins when both are set? |
| **D: Config file with profiles** | Reject | Adds file I/O, a config schema, a config file location convention, and a `hlx config` command. Massive complexity for marginal value. |
| **E: Do nothing** | ✅ Accept | The MCP client config already solves the MCP case. The shell environment already solves the CLI case. |

### 4. MCP protocol considerations

The MCP specification (as of the version we're using with `ModelContextProtocol` v0.8.0-preview.1) treats auth as session-level, not per-tool-call. There's no standard mechanism for an MCP client to pass a different bearer token per `tools/call` request. The auth context is established when the transport connects. This means multi-auth within a single MCP session would require a custom, non-standard extension — which would break interop with standard MCP clients.

### 5. Cache implications

The cache is already correctly isolated per token via `CacheOptions.AuthTokenHash` → separate SQLite DB + artifact directory. Two MCP server instances with different tokens get completely independent caches. No changes needed.

### 6. What we SHOULD do instead (if anything)

If users report friction switching between authenticated and unauthenticated access, the right move is:

1. **Better error messages** — when a 401/403 occurs, suggest both the env var AND the MCP multi-server-entry pattern in the error message. Today we only mention the env var.
2. **`hlx auth check`** — a diagnostic command that reports whether a token is set, whether it's valid (test API call), and what access level it provides. Zero-cost, high-value for debugging auth issues.

Neither of these requires any architectural changes.

### Decision

**Do not implement multi-auth.** The current single-token-per-process model is the correct abstraction for both execution contexts (CLI and MCP). Multi-auth is already achievable through existing OS and MCP client mechanisms. Adding it to hlx would introduce complexity with no corresponding user value.

Re-evaluate if: (a) Helix deploys multiple independent instances that users need to query in a single debugging session, or (b) MCP protocol adds per-tool-call auth, or (c) multiple users report the env var switching workflow as painful.

### Lambert: HTTP/SSE auth test suite written (L-HTTP-1 through L-HTTP-4)

**By:** Lambert
**Date:** 2026-02-12

**What:** Created 28 tests across 4 new test files for the HTTP/SSE multi-client auth abstractions defined in Dallas's design spec.

**Files created:**
- `src/HelixTool.Tests/HelixTokenAccessorTests.cs` — 5 tests for EnvironmentHelixTokenAccessor
- `src/HelixTool.Tests/HelixApiClientFactoryTests.cs` — 5 tests for HelixApiClientFactory
- `src/HelixTool.Tests/CacheStoreFactoryTests.cs` — 8 tests for CacheStoreFactory (including thread safety via Parallel.For)
- `src/HelixTool.Tests/SqliteCacheStoreConcurrencyTests.cs` — 10 tests for SqliteCacheStore concurrent access

**Status:** All test files compile syntactically. Build is currently blocked by Ripley's in-progress SqliteCacheStore connection-per-operation refactor (R-HTTP-CACHE-1). Once Ripley completes that refactor, tests should build and pass.

**For Ripley:** The `SqliteCacheStoreConcurrencyTests` specifically exercise the connection-per-operation pattern you're implementing. Key scenarios: concurrent reads, concurrent writes to different/same keys, concurrent mixed read+write, and two `SqliteCacheStore` instances sharing the same SQLite DB (simulating HTTP mode). The `CacheStoreFactoryTests` verify `GetOrCreate` deduplication and `Parallel.For` thread safety.

### Lambert: HttpContextHelixTokenAccessor test suite written (L-HTTP-5)

**By:** Lambert
**Date:** 2026-02-12

**What:** Created 17 tests in `src/HelixTool.Tests/HttpContextHelixTokenAccessorTests.cs` for the HTTP-specific token accessor that Ripley is implementing in HelixTool.Mcp.

**Tests cover:**
1. Bearer token extraction (`Authorization: Bearer mytoken123`)
2. `token` format extraction (`Authorization: token mytoken456`)
3. No auth header with env var set → returns env var value
4. No auth header, no env var → returns null
5. No HttpContext, no env var → returns null
6. Empty Authorization header with env var → falls back to env var
7. Empty Authorization header, no env var → returns null
8. Case insensitivity: `bearer`, `BEARER`, `Bearer`, `beArEr` all work
9. Case insensitivity: `TOKEN`, `Token` work
10. Extra spaces around token → trimmed
11. Tabs around token → trimmed
12. Malformed: `Bearer` with no token value → null or empty
13. Malformed: `Bearer   ` (only spaces) → null or empty
14. Unknown scheme (`Basic ...`) → falls back to env var
15. Interface compliance (implements IHelixTokenAccessor)

**Csproj change:** Added `<ProjectReference Include="..\HelixTool.Mcp\HelixTool.Mcp.csproj" />` to HelixTool.Tests.csproj.

**Status:** Tests will compile once Ripley implements `HttpContextHelixTokenAccessor` in HelixTool.Mcp. Currently 1 expected compile error (type not found). All other test files build clean.

**For Ripley:** The tests expect `HttpContextHelixTokenAccessor` to:
- Accept `IHttpContextAccessor` in constructor
- Implement `IHelixTokenAccessor.GetAccessToken()`
- Parse `Bearer {token}` and `token {token}` formats (case-insensitive)
- Trim whitespace from extracted tokens
- Fall back to `HELIX_ACCESS_TOKEN` env var when no recognized auth header present
- Return null when no auth source is available

### 2025-07-18: US-9 Script Removability Analysis Complete
**By:** Ash
**What:** Completed comprehensive function-by-function mapping of ci-analysis Helix API code to hlx equivalents. All 6 core API functions (152 lines) are 100% replaceable. Overall Helix-related coverage is ~85% (217/305 extended lines). Phase 1 migration can proceed immediately with zero blockers — net reduction of ~120 lines. Only meaningful gap is structured test failure extraction (US-22, P2, ~88 lines at ~40% coverage). Analysis delivered at `.ai-team/analysis/us9-script-removability.md`.
**Why:** This analysis is the prerequisite for ci-analysis adopting hlx. Without quantified coverage and a gap list, the migration plan was aspirational. Now it's actionable: the team knows exactly which functions to replace, which to keep, and which user stories (if any) to promote. The key finding — that NO user stories need promotion for Phase 1 — means migration can start in the next sprint.

### 2026-02-13: US-6 Download E2E Verification
**By:** Lambert
**What:** Created 46 comprehensive tests for DownloadFilesAsync and DownloadFromUrlAsync in `DownloadTests.cs`, organized into 4 test classes: DownloadFilesTests (27), DownloadFromUrlParsingTests (5), DownloadSanitizationTests (6), DownloadPatternTests (8). All 298 tests pass.
**Why:** Download commands had zero test coverage. Tests verify happy paths (single/multi-file, pattern matching, binary content), security (path traversal via `..`, `/`, `\` — all sanitized by CacheSecurity), error handling (401/403/404/timeout/cancellation), input validation, and edge cases (empty streams, unicode filenames, same-name files, URL-encoded characters). Each test class uses a distinct ValidJobId GUID to prevent temp directory collisions during parallel xUnit execution — a pattern discovered when shared GUIDs caused file contention failures.

### 2026-02-15: README update for caching, HTTP multi-auth, and project structure
**By:** Kane
**What:** Added Caching section with settings table and policy docs; added HTTP per-request auth subsection under Authentication; expanded Project Structure to include Cache/, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor, and 298 test count; added ci-analysis replacement note in Architecture.
**Why:** README was stale — caching, HTTP multi-auth, and new source files were undocumented. These are user-facing features (caching affects performance; HTTP auth matters for shared deployments). Kept documentation concise with tables and bold-label bullets to match existing README tone.

### 2026-02-13: Requirements audit — P0/P1/P2 completion status
**By:** Ash
**What:** Comprehensive audit of all 30 user stories against the actual codebase. Marked 25 stories as ✅ Implemented in requirements.md. Replaced the "Implementation Gaps" section with "Resolved Gaps" (all 8 original gaps fixed) and a new "Remaining Implementation Gaps" section (5 minor items). Updated the feature table from 7 features to 15. Identified 7 acceptance criteria that were NOT met despite the parent feature being functional — left these unchecked with explanatory notes.
**Why:** The requirements document was significantly out of date — it still described the project as an MVP with 6 commands and 8 implementation gaps, when in reality it has 15 capabilities and all original gaps are resolved. This caused confusion about project maturity and remaining work. The updated document now accurately reflects that hlx is feature-complete for ci-analysis integration (all P1s done), with only P3 stories and one partial P2 (structured test failure parsing) remaining.

**Key findings for the team:**
1. **US-22 (structured test failure parsing)** is the only P2 that's NOT fully done. `hlx_search_log` provides generic search, but the structured `hlx_test_failures` tool was never built. This is the single remaining gap for full ci-analysis migration.
2. **US-21 failure categorization** uses heuristics (exit code + state + work item name) rather than log content parsing as originally specified. The log-based criteria (checking for "Traceback", "Failures: 0") were not implemented. Current approach is sufficient for most cases.
3. **US-17 code organization** is functionally complete (namespaces fixed) but the models-extraction and Display/ cleanup criteria are outstanding — minor refactoring debt.
4. **US-11 --json flag** doesn't cover `find-binlogs` or `batch-status` — minor gap for power users.

### 2026-02-13: MCP API design review
**By:** Dallas
**What:** Comprehensive API design review of all 9 MCP tool endpoints in HelixMcpTools.cs. Verdict: the surface is well-designed for its domain and NOT overly ci-analysis-specific — it's a general-purpose Helix job inspection API that ci-analysis happens to consume. Identified 6 actionable improvements: (1) rename `hlx_logs` → `hlx_log_content`, (2) rename `hlx_download_url` → `hlx_download_file_url`, (3) fix `hlx_batch_status` comma-separated string → proper array parameter, (4) add `hlx_list_work_items` as a missing navigation tool, (5) standardize response envelope with consistent `{data, error?}` shape, (6) fix `hlx_status` inconsistent `all` parameter naming. Priority order: P0 batch_status array fix, P1 list_work_items gap, P2 naming improvements, P3 response envelope standardization.
**Why:** Larry raised concern that the MCP surface was too tightly coupled to ci-analysis workflows. After thorough review, the tools map to Helix API primitives (jobs, work items, files, logs) rather than ci-analysis-specific orchestrations. The naming uses `hlx_` prefix consistently and maps to Helix domain concepts. However, there are real usability issues that would trip up non-ci-analysis consumers: the comma-separated jobIds string in batch_status is hostile to programmatic callers, the missing list_work_items tool forces consumers to use hlx_status (heavy) just to discover work item names, and some tool names don't self-document well. These fixes would make the API genuinely general-purpose.

### 2026-02-14: Generalize hlx_find_binlogs to hlx_find_files with pattern parameter
**By:** Dallas
**Requested by:** Larry Ewing

---

## Question

Should `hlx_find_binlogs` (which hardcodes `.binlog` extension matching across work items in a job) become a generic `hlx_find_files` with a pattern parameter?

## Recommendation: **Yes — add generic `FindFilesAsync` in Core, keep `hlx_find_binlogs` as a convenience alias**

This is a two-layer solution:

### Layer 1: Core — Generic `FindFilesAsync`

Rename/generalize `FindBinlogsAsync` → `FindFilesAsync(string jobId, string pattern = "*", int maxItems = 30)`:

- Reuse the existing `MatchesPattern` helper (already used by `DownloadFilesAsync`)
- Rename `BinlogResult` → `FileSearchResult(string WorkItem, List<FileEntry> Files)`
- The old `FindBinlogsAsync` becomes a one-liner: `FindFilesAsync(jobId, "*.binlog", maxItems)`

**Rationale:** The core method should not encode knowledge of a specific file type. `DownloadFilesAsync` already proves this pattern works — it takes a glob and delegates to `MatchesPattern`. The scan-across-work-items operation is the same regardless of file type.

### Layer 2: MCP — Add `hlx_find_files`, keep `hlx_find_binlogs`

Add a **new** MCP tool `hlx_find_files` with a `pattern` parameter (defaulting to `"*"`). Keep `hlx_find_binlogs` as an alias that calls `hlx_find_files` with `pattern = "*.binlog"`.

**Rationale for keeping both:**

1. **Backward compatibility.** `hlx_find_binlogs` is referenced by the `ci-analysis` skill and potentially other LLM consumers. MCP tool names are effectively a public API contract for LLM agents. Removing it would break existing tool-use patterns that are baked into prompt engineering and skill definitions.

2. **LLM ergonomics.** Specific tool names are easier for LLMs to select correctly. When an LLM sees `hlx_find_binlogs`, it knows exactly what to call. A generic `hlx_find_files` requires the LLM to also figure out the right pattern. Keeping the specific tool as a convenience reduces LLM decision-making overhead.

3. **Discoverability.** Binlogs are the dominant use case. Having a named tool for the common case improves tool-list scanning. The generic tool serves the long tail (crash dumps, coverage files, test results).

### Layer 3: CLI — Add `find-files`, keep `find-binlogs`

Same pattern as MCP:
- Add `find-files <jobId> --pattern "*.dmp" [--max-items N]`
- Keep `find-binlogs` as shorthand for `find-files --pattern "*.binlog"`

---

## Use cases beyond binlogs

Real scenarios that justify the generic method:

| Pattern | Use case |
|---------|----------|
| `*.binlog` | MSBuild binary logs for build analysis |
| `*.dmp` / `*.mdmp` | Crash dump analysis |
| `testResults.xml` / `*.trx` | Test result files |
| `*.gcov` / `*.cobertura.xml` | Code coverage artifacts |
| `*.etl` | ETW trace files |
| `console.log` | Console output files |

These are all cases where an LLM or human needs to scan across work items to find which ones produced a specific artifact type.

---

## What NOT to do

- **Do NOT remove `hlx_find_binlogs`.** It's a stable contract.
- **Do NOT add per-file-type convenience tools** (`hlx_find_dumps`, `hlx_find_coverage`, etc.). One generic + one common-case convenience is the right surface area. More than that is tool sprawl.
- **Do NOT change the `hlx_files` tool.** `hlx_files` operates on a single work item and already returns all files grouped by type. `hlx_find_files` operates across work items in a job — different scope, different operation.

---

## Implementation guidance

**Core changes:**
```csharp
// Rename BinlogResult → FileSearchResult
public record FileSearchResult(string WorkItem, List<FileEntry> Files);

// Generic method
public async Task<List<FileSearchResult>> FindFilesAsync(
    string jobId, string pattern = "*", int maxItems = 30, CancellationToken ct = default)
{
    // Same logic as FindBinlogsAsync, but use MatchesPattern(f.Name, pattern)
    // instead of hardcoded .binlog check
}

// Convenience (can be extension method or just kept in HelixService)
public Task<List<FileSearchResult>> FindBinlogsAsync(
    string jobId, int maxItems = 30, CancellationToken ct = default)
    => FindFilesAsync(jobId, "*.binlog", maxItems, ct);
```

**MCP changes:**
```csharp
[McpServerTool(Name = "hlx_find_files")]
public async Task<string> FindFiles(string jobId, string pattern = "*", int maxItems = 30) { ... }

[McpServerTool(Name = "hlx_find_binlogs")]
public async Task<string> FindBinlogs(string jobId, int maxItems = 30)
    => await FindFiles(jobId, "*.binlog", maxItems);
```

**CLI changes:**
```
[Command("find-files")]
public async Task FindFiles(string jobId, string pattern = "*", int maxItems = 30) { ... }

[Command("find-binlogs")]
public async Task FindBinlogs(string jobId, int maxItems = 30)
    => await FindFiles(jobId, "*.binlog", maxItems);
```

---

## Status

**Decision:** Approved by Dallas
**Implementation:** Assigned to Ripley
**Tests:** Assigned to Lambert (update existing FindBinlogsAsync tests, add FindFilesAsync tests with various patterns)
**Docs:** Assigned to Kane (update CLI help text, README)


---

### 2026-02-13: camelCase JSON assertion convention

# Decision: camelCase JSON assertion convention

**Author:** Lambert  
**Date:** 2026-02-13  
**Status:** Implemented

## Context
`HelixMcpTools.s_jsonOptions` now uses `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. All MCP JSON output properties are camelCase.

## Decision
All test assertions against MCP JSON output must use camelCase property names in `GetProperty()` calls (e.g., `GetProperty("name")` not `GetProperty("Name")`). This applies to all existing and future tests in HelixMcpToolsTests, StructuredJsonTests, McpInputFlexibilityTests, and any new MCP test files.

## Impact
- 8 tests fixed across 3 files
- Convention must be followed for all future MCP tool tests

---

### 2026-02-15: MCP API Batch — Tests Need CamelCase Update

# MCP API Batch: Tests Need CamelCase Update

**Author:** Ripley
**Date:** 2026-02-15
**Status:** For Lambert

## Context

The MCP API batch implementation changed `s_jsonOptions` in `HelixMcpTools.cs` to use `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`. This means all MCP JSON output now uses camelCase property names.

## Test Impact

Tests in `HelixMcpToolsTests.cs` that assert PascalCase JSON property names need updating:
- `"Name"` → `"name"`
- `"ExitCode"` → `"exitCode"`
- `"State"` → `"state"`
- `"MachineName"` → `"machineName"`
- `"QueueId"` → `"queueId"`
- `"Uri"` → `"uri"`

Also, `FindBinlogs` MCP tool now delegates to `FindFiles`, so the test on line 259 referencing `"binlogs"` key should be `"files"` instead.

`WorkItemDetailTests.cs` assertions about `IsBinlog`/`IsTestResults` have been replaced with simple name assertions (already compiles).

`JsonOutputTests.cs` file grouping logic updated to use `HelixService.MatchesPattern` (already compiles).

## Files Affected
- `src/HelixTool.Tests/HelixMcpToolsTests.cs`
- `src/HelixTool.Tests/WorkItemDetailTests.cs` (already fixed for compilation)
- `src/HelixTool.Tests/JsonOutputTests.cs` (already fixed for compilation)


### 2025-07-23: STRIDE Threat Model — Completed and Approved

**By:** Ash (analysis), Dallas (review)
**Date:** 2025-07-23
**Artifact:** `.ai-team/analysis/threat-model.md`

**What:** Completed a STRIDE-based threat model for lewing.helix.mcp covering all 6 categories. Identified 16 specific threats grounded in actual source code. Two are High severity (both related to HTTP MCP server lacking authentication), two are Medium (arbitrary URL download SSRF, unbounded batch size). Path traversal protection and token handling are well-implemented.

**Why:** Larry asked whether we should do a threat model. The answer is yes — the HTTP MCP server has no authentication middleware, which is a real deployment risk. The stdio mode (default) is secure by design. The `hlx_download_url` accepting arbitrary URLs is a secondary concern since the HttpClient is unauthenticated, but it still enables SSRF probing. These findings should inform prioritization before any HTTP deployment.

**Review verdict:** ✅ APPROVED with minor amendments. Dallas verified 15+ line-number references against source — all correct. One amendment: S1 recommendation should be "Add auth middleware for non-localhost deployment" rather than "default to localhost" (already the ASP.NET Core default).

**Action items for the team:**
1. **S1/I4 (HTTP auth) is a pre-GA gate** — not a current blocker for stdio-only deployments. Track as a requirement for any HTTP mode production use.
2. **E1 (URL scheme validation)** — Ripley should add a scheme check in `DownloadFromUrlAsync` as part of normal hardening. One-liner: reject non-`http`/`https` schemes.
3. **D1 (batch size limit)** — Add `ArgumentException` guard in `GetBatchStatusAsync` if `idList.Count > 50`. Prevents agent-driven resource exhaustion.
4. **T2 (domain allowlist for download-url)** — Defer. Too restrictive for a diagnostic tool where blob storage URLs vary. Document the risk instead.


### 2025-07-23: P1 Security Fixes — E1 URL Scheme Validation + D1 Batch Size Limit

**By:** Ripley
**Date:** 2025-07-23
**Requested by:** Larry Ewing (per Dallas-approved threat model action items)

**E1 — URL scheme validation in `DownloadFromUrlAsync`:**
- `HelixService.DownloadFromUrlAsync` now validates `uri.Scheme` is `"http"` or `"https"` before making any HTTP request.
- Throws `ArgumentException` with message: `"Only HTTP and HTTPS URLs are supported. Got scheme '{scheme}'."` and `paramName: "url"`.
- Blocks `file://`, `ftp://`, and any other non-HTTP scheme — prevents SSRF via scheme manipulation.
- The `ArgumentException` is thrown before the `try` block's HTTP call, so it propagates directly (not wrapped in `HelixException`).

**D1 — Max batch size limit in `GetBatchStatusAsync`:**
- Added `internal const int MaxBatchSize = 50` on `HelixService`.
- `GetBatchStatusAsync` throws `ArgumentException` if `idList.Count > MaxBatchSize`.
- Message: `"Batch size {count} exceeds the maximum of {MaxBatchSize} jobs."` with `paramName: "jobIds"`.
- Constant is `internal` so Lambert's tests can reference `HelixService.MaxBatchSize` directly.

**MCP tool description:**
- `hlx_batch_status` tool description updated to: `"...Maximum 50 jobs per request."` so MCP clients (AI agents) are aware of the limit before calling.

**Test impact:**
- Lambert should add tests for:
  1. `DownloadFromUrlAsync` with `ftp://...` → `ArgumentException`
  2. `DownloadFromUrlAsync` with `file:///etc/passwd` → `ArgumentException`
  3. `DownloadFromUrlAsync` with `https://...` → no scheme error (existing behavior)
  4. `GetBatchStatusAsync` with 51 job IDs → `ArgumentException`
  5. `GetBatchStatusAsync` with 50 job IDs → no error (existing behavior)
  6. `HelixService.MaxBatchSize == 50` (constant value assertion)

### 2026-02-15: Security Validation Test Strategy

**By:** Lambert
**Date:** 2026-02-15
**Requested by:** Larry Ewing

**Context:** Threat model identified P1 security findings E1 (SSRF via DownloadFromUrlAsync) and D1 (unbounded batch size in GetBatchStatusAsync). Tests written concurrently with Ripley's production fixes.

**Decisions:**

1. **URL scheme tests use negative assertion pattern:** For HTTP/HTTPS acceptance, tests use `Record.ExceptionAsync` + `Assert.IsNotType<ArgumentException>` rather than asserting no exception at all. The method will fail with network-related exceptions (no real server), but that's expected — we only verify scheme validation doesn't reject valid schemes.

2. **No-scheme URL test accepts both UriFormatException and ArgumentException:** The `new Uri(url)` constructor throws `UriFormatException` for schemeless strings *before* the scheme check runs. Both exception types are acceptable rejection behavior. Test uses `Assert.True(ex is A || ex is B)`.

3. **Batch boundary tests at 50/51:** MaxBatchSize = 50 tested at exact boundary (50 accepted, 51 rejected). Bulk mock setup uses `Enumerable.Range` with formatted GUIDs.

4. **MCP tool tests verify propagation:** `hlx_batch_status` tests confirm that the service-layer `ArgumentException` propagates through the MCP tool method. No separate MCP-layer validation needed — the service enforces the limit.

**Files:** `src/HelixTool.Tests/SecurityValidationTests.cs` — 18 tests (all passing)

### 2026-02-13: Remote search and structured file querying — feature design

**By:** Dallas
**Requested by:** Larry Ewing
**What:** Feature design for remote search/grep across Helix work item artifacts — general text search, structured TRX/XML querying, and the boundary with external binlog tools.
**Why:** Users need to find patterns in Helix artifacts without downloading files locally. The current `hlx_search_log` only covers console logs. This design extends search to arbitrary text files and adds structured querying for test results (TRX). It maps to existing backlog (US-22, US-14) and identifies new stories needed.

---

## 1. Current State

### What we have

| Tool | Scope | How it works |
|------|-------|-------------|
| `hlx_search_log` | Console logs only | Downloads log via Helix SDK `ConsoleLogAsync`, searches in-memory with `string.Contains`, deletes temp file |
| `hlx_files` | File listing | Lists work item files, categorizes by extension (.binlog, .trx, other) |
| `hlx_find_files` | Cross-work-item scan | Scans up to N work items to find files matching a glob pattern |
| `hlx_download` / `hlx_download_url` | File retrieval | Downloads files to temp dir for local processing |

### The gap

Once a user identifies a file via `hlx_files` or `hlx_find_files`, they must download it to search its contents. For text files (logs, XML, TRX), this means:
1. `hlx_download` → local file path
2. Client reads file, searches locally

For MCP clients (LLM agents), this is expensive — each file download consumes tool calls and context window. The client must also know *how* to parse structured formats (TRX XML, binlogs).

### The goal

Enable "search without download" for:
- **Text files** — any uploaded text artifact (not just console logs)
- **TRX files** — structured test result queries (which tests failed? what were their error messages?)
- **Binlogs** — already handled by external `mcp-binlog-tool-*` MCP tools (what's our role?)

---

## 2. General Text Search (Remote Grep)

### 2.1 Architecture: Streaming vs. Download-and-Search

**Option A: Download-and-search (like `hlx_search_log` today)**
- Download file via `IHelixApiClient.GetFileAsync` → stream to temp file → search → delete
- Proven pattern, already works for console logs
- File size is bounded by Helix upload limits (typically <100MB for text artifacts)

**Option B: Streaming search (line-by-line from HTTP stream)**
- Read lines from the HTTP response stream, match as they arrive, discard non-matches
- Lower memory footprint for large files
- More complex: can't do context lines easily (need a sliding window), can't report `totalLines` without reading everything

**Recommendation:** Option A. The download-and-search pattern is proven (`SearchConsoleLogAsync` already does this), the files are bounded in size, and it enables context lines trivially. Memory optimization (streaming) can be deferred — it's the same internal refactor either way.

### 2.2 Proposed MCP Tool: `hlx_search_file`

```csharp
[McpServerTool(Name = "hlx_search_file"),
 Description("Search a work item's uploaded file for lines matching a text pattern. " +
              "Returns matching lines with context. Works with any text file " +
              "(logs, XML, config, scripts). Use hlx_files to find available files first.")]
public async Task<string> SearchFile(
    [Description("Helix job ID (GUID), Helix URL, or full work item URL")]
    string jobId,

    [Description("Work item name (optional if included in jobId URL)")]
    string? workItem = null,

    [Description("File name to search (must match a file name from hlx_files output)")]
    string fileName,

    [Description("Text pattern to search for (case-insensitive substring match)")]
    string pattern,

    [Description("Lines of context before and after each match (default: 2)")]
    int contextLines = 2,

    [Description("Maximum number of matches to return (default: 50)")]
    int maxMatches = 50)
```

**Return format** (follows `hlx_search_log` pattern exactly):
```json
{
  "workItem": "tests.dll.1",
  "fileName": "testResults.trx",
  "pattern": "FAIL",
  "totalLines": 1250,
  "matchCount": 7,
  "matches": [
    { "lineNumber": 42, "line": "...", "context": ["..."] }
  ]
}
```

### 2.3 Design Decisions for Larry

**Decision 1: File size limit?**
- **Option A:** No limit — download whatever Helix has, search it. Simple, but a 500MB dump file could OOM the process.
- **Option B:** Hard limit (e.g., 50MB) — reject with error if file is too large. Clear, but blocks legitimate use cases.
- **Option C:** Soft limit with warning — search first N bytes (e.g., 50MB), return results with `truncated: true` flag. User knows they got partial results.
- **Recommendation:** Option C with 50MB default. The parameter could be exposed as `maxFileSize` if needed later, but start without it.

**Decision 2: Should `hlx_search_log` be deprecated in favor of `hlx_search_file`?**
- **Option A:** Keep both — `hlx_search_log` continues to use the Helix `ConsoleLogAsync` endpoint (which doesn't require knowing the file name), `hlx_search_file` uses `GetFileAsync` (requires file name from `hlx_files`).
- **Option B:** Make `hlx_search_log` delegate to `hlx_search_file` internally.
- **Recommendation:** Option A. `hlx_search_log` is a shortcut — console logs have a dedicated API endpoint that doesn't require listing files first. It's the most common search target by far. Keep the convenience path. `hlx_search_file` is for everything else.

**Decision 3: Pattern matching — substring or regex?**
- Current `hlx_search_log` uses `string.Contains(pattern, OrdinalIgnoreCase)` — no regex.
- Threat model (E2) explicitly notes this is good: "no regex in user-facing pattern matching" eliminates ReDoS risk.
- **Option A:** Keep substring-only. Simple, safe, handles 90% of use cases.
- **Option B:** Add an optional `useRegex` boolean parameter (default false). Adds power but introduces ReDoS risk.
- **Option C:** Support a limited "glob-like" syntax (e.g., `error*CS*` using wildcards only). Middle ground but invents a custom syntax.
- **Recommendation:** Option A. Substring matching with case-insensitive comparison covers the vast majority of CI investigation patterns ("error", "FAIL", "CS1234", "Exception", "timeout"). If regex is needed, the user can download the file. Maintaining the no-regex invariant is worth the tradeoff.

**Decision 4: Binary file detection?**
- `hlx_search_file` should not try to search binary files (binlogs, DLLs, crash dumps).
- **Proposal:** Check the first 8KB for null bytes. If found, return `{ "error": "File appears to be binary. Use hlx_download to retrieve it locally." }`. This is how `git diff` and `ripgrep` detect binary files.

### 2.4 Core Service Method

```csharp
// In HelixService
public async Task<FileSearchResult> SearchFileAsync(
    string jobId, string workItem, string fileName,
    string pattern, int contextLines = 2, int maxMatches = 50,
    long maxFileSizeBytes = 50 * 1024 * 1024,
    CancellationToken cancellationToken = default)
```

Implementation: reuse the same download-search-delete pattern from `SearchConsoleLogAsync`, but use `GetFileAsync` instead of `GetConsoleLogAsync`. Factor out the search logic into a private helper shared between the two.

### 2.5 CLI Command

```bash
hlx search-file <jobId> <workItem> <fileName> <pattern> [--context 2] [--max 50]
```

---

## 3. Structured Search: TRX Test Results

### 3.1 The Problem

TRX files are XML documents containing test results from xUnit, NUnit, and MSTest runs. They're the richest source of "which tests failed and why" — more structured than parsing console log output for `[FAIL]` lines.

The current workflow is: `hlx_files` → find .trx file → `hlx_download` → parse locally. An MCP client (LLM agent) cannot parse XML natively — it needs hlx to do the structured extraction.

### 3.2 Proposed MCP Tool: `hlx_test_results`

```csharp
[McpServerTool(Name = "hlx_test_results"),
 Description("Parse TRX test result files from a work item. Returns structured test outcomes " +
              "including test names, pass/fail status, duration, and error messages. " +
              "Failed tests are listed first.")]
public async Task<string> TestResults(
    [Description("Helix job ID (GUID), Helix URL, or full work item URL")]
    string jobId,

    [Description("Work item name (optional if included in jobId URL)")]
    string? workItem = null,

    [Description("Include passed tests in output (default: false — only failures shown)")]
    bool includePassed = false,

    [Description("Maximum number of test results to return (default: 200)")]
    int maxResults = 200)
```

**Return format:**
```json
{
  "workItem": "tests.dll.1",
  "trxFile": "testResults.trx",
  "summary": {
    "total": 150,
    "passed": 142,
    "failed": 5,
    "skipped": 3,
    "duration": "2m 31s"
  },
  "failed": [
    {
      "testName": "Namespace.Class.MethodName",
      "outcome": "Failed",
      "duration": "1.2s",
      "errorMessage": "Assert.Equal() Failure...",
      "stackTrace": "at Namespace.Class.MethodName() in ..."
    }
  ],
  "passed": null
}
```

### 3.3 Design Decisions for Larry

**Decision 5: Which TRX file to parse when there are multiple?**
- A work item might upload multiple .trx files (e.g., if multiple test assemblies run).
- **Option A:** Parse ALL .trx files, merge results. Simple API for the caller, but merging across files loses the "which file had the failure" info.
- **Option B:** Parse all .trx files, return results grouped by file name. More verbose but preserves provenance.
- **Option C:** Accept an optional `trxFileName` parameter to parse a specific one. If omitted, parse all (Option B behavior).
- **Recommendation:** Option C. The tool auto-discovers .trx files from `hlx_files`, parses all by default, groups by file, but allows narrowing to one file for large work items. The parameter is optional so the simple case ("just show me failures") stays simple.

**Decision 6: What TRX content to extract?**
- TRX schema is large. The minimum viable extraction is:
  - `/TestRun/Results/UnitTestResult` → `@testName`, `@outcome`, `@duration`
  - `/TestRun/Results/UnitTestResult/Output/ErrorInfo/Message` → error message
  - `/TestRun/Results/UnitTestResult/Output/ErrorInfo/StackTrace` → stack trace
  - `/TestRun/ResultSummary` → counters (total, passed, failed, etc.)
- **Option A:** Extract all the above. Good starting point.
- **Option B:** Also extract `@computerName`, `@startTime`, `@endTime`. Useful for timing analysis.
- **Recommendation:** Start with Option A. The method signature should be extensible (add fields later without breaking the API shape).

**Decision 7: Error message truncation?**
- Some test failures produce enormous error messages (e.g., large expected/actual diffs from `Assert.Equal` on multi-line strings).
- **Option A:** Return full error messages. Let the MCP client deal with context window limits.
- **Option B:** Truncate error messages to N characters (e.g., 500) with a `"...[truncated]"` suffix.
- **Recommendation:** Option B with 500 character default. MCP tool responses go into LLM context windows. A single test with a 50KB diff string would blow the budget. Add a `maxErrorLength` parameter so callers can override.

### 3.4 Security Considerations (Flag for Ash)

**SEC-1: XML External Entity (XXE) Injection**
- TRX files are XML. If parsed with default `XmlReader`/`XDocument` settings, they could contain `<!ENTITY>` declarations that reference external resources (file:// URLs, HTTP endpoints) or expand to gigabytes of text (billion laughs attack).
- **Mitigation:** Use `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }`. This is the standard .NET defense. Zero tolerance for DTDs in TRX files.
- **Risk level:** High if unmitigated; Low with the above settings.

**SEC-2: TRX files from untrusted CI jobs**
- Helix work items are submitted by CI jobs. The CI job author controls the content of uploaded .trx files. A malicious actor could craft a .trx file that:
  - Contains XXE payloads (mitigated by SEC-1)
  - Has extremely large test names or error messages (DoS via memory exhaustion)
  - Embeds content designed to confuse LLM agents when returned via MCP (prompt injection via test names)
- **Mitigation for size:** Cap parsed content — `maxResults` parameter limits number of tests returned; `maxErrorLength` truncates messages.
- **Mitigation for content:** We return test data as structured JSON fields, not as raw prompts. The MCP client (LLM) sees them as data, not instructions. No special mitigation needed beyond standard output sanitization.
- **Trust boundary:** TRX content has the same trust level as console logs (semi-trusted — user-submitted but from within Microsoft CI infrastructure for the primary use case). Document this.

**SEC-3: File size limits for in-memory XML parsing**
- `XDocument.Load()` reads the entire XML into memory as a DOM tree. A 100MB TRX file would consume several hundred MB of RAM.
- **Mitigation:** Check file size before parsing. Reject files > 50MB with a clear error. TRX files from normal test runs are typically 100KB–5MB.

**SEC-4: Pattern complexity (text search)**
- Current design uses `string.Contains` — O(n*m) worst case but no catastrophic backtracking.
- **No regex** — explicitly maintained from existing threat model finding E2.
- **No risk of ReDoS.**

**SEC-5: Trust boundary for file content from Helix**
- All file content retrieved from Helix blob storage is semi-trusted:
  - The Helix API returns signed Azure Blob Storage URLs with SAS tokens
  - The content was uploaded by a CI job that ran on Helix infrastructure
  - The CI job author (or a PR contributor) controls what gets uploaded
  - For open-source repos, ANY pull request author can submit CI jobs
- **Principle:** Treat all file content as untrusted input. Parse defensively. Never execute, evaluate, or interpret content as code.
- **This applies to:** TRX parsing, text search results, and any future structured parsers.

---

## 4. Structured Search: Binlogs

### 4.1 Current State

External `mcp-binlog-tool-*` MCP tools already exist (visible in the MCP tool inventory this conversation has access to). They provide rich binlog analysis:
- `load_binlog` / `search_binlog` / `get_diagnostics` / `get_expensive_targets` etc.
- These tools operate on local file paths — they need the binlog downloaded first.

### 4.2 hlx's Role

hlx's role with binlogs is **discovery and retrieval**, not parsing:

| hlx responsibility | External binlog tool responsibility |
|---|---|
| `hlx_find_binlogs` — find which work items have binlogs | `load_binlog` — load a local .binlog file |
| `hlx_find_files *.binlog` — same, generic | `search_binlog` — query the loaded binlog |
| `hlx_download` — download binlog to local path | `get_diagnostics` — extract errors/warnings |
| `hlx_download_url` — download by direct URI | All other analysis tools |

**Decision 8: Should hlx wrap binlog tools for a one-stop experience?**
- **Option A:** No — hlx discovers/downloads, external tools analyze. Clean separation. The MCP client orchestrates the two-step workflow.
- **Option B:** hlx adds convenience tools like `hlx_binlog_errors <jobId> <workItem>` that download + delegate to binlog tools. Reduces MCP round-trips but creates a dependency on the binlog tool being co-installed.
- **Option C:** hlx adds binlog analysis directly (embed MSBuild Structured Log Viewer library). Full self-contained solution but large dependency.
- **Recommendation:** Option A. hlx is a Helix API tool, not an MSBuild analysis tool. The MCP client is the orchestrator — it calls `hlx_download` then `mcp-binlog-tool-load_binlog`. Adding binlog parsing to hlx would violate the single-responsibility principle and create a maintenance burden. The external binlog tools are mature and actively maintained.

If the two-step workflow is too many round-trips, the right fix is an MCP "workflow" or "composite tool" at the client layer, not embedding analysis into hlx.

---

## 5. Backlog Mapping

### Existing Stories Covered

| Story | Title | Coverage |
|-------|-------|----------|
| **US-22** | Console Log Content Search / Pattern Extraction | **Generic search ✅ done** (`hlx_search_log`). Structured test failure parsing from console logs NOT done — this design proposes `hlx_test_results` (TRX-based) as the better solution. The "parse `[FAIL]` lines from logs" approach in US-22's original spec is fragile (format-dependent). TRX gives us structured data directly. **Recommendation: close the structured parsing acceptance criteria in US-22 as "won't fix — superseded by TRX parsing (US-14)".** |
| **US-14** | TRX Test Results Parsing | **Directly addressed** by `hlx_test_results`. All acceptance criteria map to the proposed tool. Promote from P3 to P2. |
| **US-16** | Retry/Correlation Support | **Not directly addressed** by this design. Cross-job work item correlation is a separate feature. However, `hlx_search_file` + `hlx_test_results` make it easier to compare failures across jobs (search for a test name in multiple work items). |

### New Stories Needed

| ID | Title | Priority | Description |
|----|-------|----------|-------------|
| **US-31** | Remote file text search | P2 | `hlx_search_file` MCP tool + `hlx search-file` CLI. Search uploaded text files for patterns without downloading. Extends `hlx_search_log` to arbitrary files. |
| **US-32** | TRX test results parsing | P2 | `hlx_test_results` MCP tool + `hlx test-results` CLI. Parse .trx XML, return structured pass/fail/error data. **Replaces the structured parsing portion of US-22 and implements US-14.** Requires SEC-1 through SEC-5 mitigations. |

### Stories NOT needed

- No new story for binlog analysis — hlx's role (discovery/download) is already covered by `hlx_find_binlogs` and `hlx_download`.
- No new story for regex support — deliberately excluded (Decision 3).
- No story for `hlx_search_log` changes — it stays as-is.

---

## 6. Implementation Phasing

### Phase 1: `hlx_search_file` (US-31)
**Ships first. Low risk. Incremental.**

- Refactor `SearchConsoleLogAsync` to extract shared search logic into a private helper
- Add `SearchFileAsync` to `HelixService` using the same download-search-delete pattern
- Add `hlx_search_file` MCP tool and `hlx search-file` CLI command
- Add binary file detection (null byte check)
- Add file size cap (50MB default)
- **Estimated scope:** ~100 lines Core + ~30 lines MCP + CLI wiring
- **Tests:** Lambert writes unit tests for search logic (mock `IHelixApiClient.GetFileAsync`)
- **Security:** Minimal new attack surface — same patterns as existing `hlx_search_log`

### Phase 2: `hlx_test_results` (US-32)
**Ships second. Medium risk. Requires security review.**

- Add `ParseTrxAsync` to `HelixService` — download .trx, parse with safe `XmlReader`, extract results
- Add `hlx_test_results` MCP tool and `hlx test-results` CLI command
- **Pre-implementation:** Ash reviews SEC-1 through SEC-5 and produces XML parsing security guidelines
- **Implementation constraints:**
  - `XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }`
  - File size check before parsing (reject > 50MB)
  - `maxResults` cap on returned test entries
  - `maxErrorLength` truncation on error messages
- **Estimated scope:** ~200 lines Core + ~40 lines MCP + CLI wiring
- **Tests:** Lambert writes tests with crafted .trx files (valid, malformed, XXE attempts, oversized)

### Phase 3 (deferred): Structured console log failure extraction
**Not recommended now.**

The original US-22 structured parsing (xUnit `[FAIL]`, NUnit, MSTest format detection) is fragile — it depends on console output format which varies by test framework version, CI runner, and configuration. TRX parsing (Phase 2) gives us the same information in a reliable, structured format. If specific scenarios arise where TRX files are unavailable but console logs have extractable failure patterns, revisit this.

### Phase 4 (deferred): Cross-file search
**Future capability if demand materializes.**

Search across ALL files in a work item (or across work items) for a pattern. Like `hlx_find_files` but for content, not names. This is expensive (download every file, search each one) and the use cases are unclear. Defer until a concrete scenario drives it.

---

## 7. API Shape Summary

### New tools (2)

| Tool | Phase | Parameters | Returns |
|------|-------|-----------|---------|
| `hlx_search_file` | 1 | `jobId`, `workItem?`, `fileName`, `pattern`, `contextLines=2`, `maxMatches=50` | JSON: matches with line numbers, context, totalLines |
| `hlx_test_results` | 2 | `jobId`, `workItem?`, `includePassed=false`, `maxResults=200` | JSON: test summary + failed/passed test entries with names, outcomes, error messages |

### Existing tools (unchanged)

| Tool | Notes |
|------|-------|
| `hlx_search_log` | Stays as console-log-specific shortcut. Not deprecated. |
| `hlx_files` | Already categorizes .trx files. No changes needed. |
| `hlx_find_files` | Already supports `*.trx` pattern. No changes needed. |
| `hlx_download` / `hlx_download_url` | Still needed for binlogs and binary files. |

### Existing tools (future consideration)

| Tool | Possible change | Priority |
|------|----------------|----------|
| `hlx_find_binlogs` | Already aliased to `hlx_find_files *.binlog` — no change | N/A |

---

## 8. Open Questions for Larry

1. **File size cap** — 50MB for text search, 50MB for TRX parsing. Are these reasonable defaults, or should they be configurable via parameters?

2. **US-22 structured parsing** — Can we formally close the "parse `[FAIL]` from console logs" acceptance criteria as superseded by TRX parsing? Or are there scenarios where TRX files are unavailable and console log parsing is the only option?

3. **Phase 1 vs Phase 2 priority** — `hlx_search_file` (text grep) is safer and simpler. `hlx_test_results` (TRX parsing) has more immediate value for CI investigation. Which ships first, or should they be developed in parallel?

4. **`hlx_test_results` auto-discovery** — Should the tool automatically find and parse ALL .trx files in a work item, or require the caller to specify which file? (Decision 5 above recommends auto-discovery with optional filtering.)

5. **Error message length** — 500 character default for `maxErrorLength` on test failure messages. Too aggressive? Too generous? Should this even be a parameter?

---

## 9. Appendix: Relationship to External Tools

### MCP Tool Ecosystem Context

In a typical CI investigation session, an MCP client has access to:
- **hlx** tools — Helix job/work item discovery, file listing, download, log search
- **mcp-binlog-tool** — MSBuild binary log analysis (load, search, diagnostics)
- **azure-devops** tools — AzDO build pipelines, PRs, work items

hlx's value proposition is being the **Helix-specific layer** — it knows how to find, list, and retrieve Helix artifacts. It should NOT try to replicate analysis capabilities that other tools provide (binlog parsing, AzDO build analysis). The new `hlx_search_file` and `hlx_test_results` tools are the exception — they handle Helix-specific file formats (TRX) that no other MCP tool covers, and they avoid the download round-trip for simple text search.

### Decision Principle (reaffirmed)

From the `find-binlogs → find-files` generalization decision:
> "For cross-work-item file scanning, one generic tool + one common-case convenience is the right surface area. Do NOT add per-file-type convenience tools — that's tool sprawl."

`hlx_test_results` is justified as a convenience tool because TRX parsing requires structured XML analysis that cannot be done with text search alone. It's not "another file-type convenience" — it's a fundamentally different operation (structured extraction vs. text matching).


### 2026-02-13: Security analysis — structured file parsing

**By:** Ash
**What:** STRIDE-aligned security analysis for proposed XML/TRX parsing, text search extension, and binlog parsing features in hlx.
**Why:** These features introduce new attack surface — untrusted file content from Helix work items will be parsed into structured data. XML parsers have well-known vulnerability classes (XXE, entity expansion DoS), and extending text search to arbitrary files changes the trust boundary. This analysis provides concrete .NET API recommendations and size limits, grounded in the existing codebase patterns.

---

## 1. XML Parsing Threats

### 1a. XXE (XML External Entity) Attacks

**Threat:** A malicious TRX or XML file uploaded to a Helix work item could contain external entity declarations that exfiltrate local data when parsed:

```xml
<?xml version="1.0"?>
<!DOCTYPE foo [
  <!ENTITY xxe SYSTEM "file:///etc/passwd">
]>
<TestRun><Results>&xxe;</Results></TestRun>
```

**Risk level:** **High** if using default `XmlDocument` or `XDocument.Load(stream)` without settings. .NET's default XML parsing behavior varies by API:

| API | XXE by default? | Notes |
|-----|----------------|-------|
| `XmlReader.Create()` with default `XmlReaderSettings` | **No** (.NET Core+) | `DtdProcessing = Prohibit` since .NET Core 1.0 |
| `XmlDocument.Load()` | **No** (.NET Core+) | Uses `XmlReader` internally with safe defaults |
| `XDocument.Load(stream)` | **No** (.NET Core+) | Uses `XmlReader` internally with safe defaults |
| `XmlTextReader` (legacy) | **Yes** | DTD processing enabled by default — DO NOT USE |

**.NET-specific mitigations we get for free:** On .NET Core/.NET 5+, the default `XmlReaderSettings` has `DtdProcessing = DtdProcessing.Prohibit` and `XmlResolver = null`. This means XXE is **blocked by default** on our target platform (.NET 10). However, we should still set these explicitly for defense-in-depth and clarity.

**Recommendation — concrete `XmlReaderSettings`:**

```csharp
var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Prohibit,  // Blocks all DTD processing (XXE, entity expansion)
    XmlResolver = null,                       // No external resource resolution
    MaxCharactersFromEntities = 0,            // No entity expansion at all
    MaxCharactersInDocument = 50_000_000,     // 50MB character limit (~100MB UTF-16)
    Async = true                              // Enable async reading
};
```

This should be defined once as a `static readonly` field (following the `s_jsonOptions` pattern in `HelixMcpTools.cs` line 10-14) and reused for all XML parsing.

### 1b. Billion Laughs / Entity Expansion DoS

**Threat:** Even without external entities, internal entity expansion can cause exponential memory growth:

```xml
<!DOCTYPE lolz [
  <!ENTITY lol "lol">
  <!ENTITY lol2 "&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;&lol;">
  <!ENTITY lol3 "&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;&lol2;">
  <!-- ... exponential expansion -->
]>
```

**Mitigation:** `DtdProcessing.Prohibit` blocks this entirely — DTDs are rejected before any entity declaration is parsed. The `MaxCharactersFromEntities = 0` setting is a belt-and-suspenders defense. Both are included in our recommended settings above.

**Verdict:** Fully mitigated by the recommended `XmlReaderSettings`. No residual risk on .NET 10.

---

## 2. TRX File Trust Boundary

### 2a. Trust Level Assessment

TRX files are MSTest XML result files produced by test runners inside Helix work items. The trust chain is:

```
CI job creator → Helix work item → test runner → TRX file → blob storage → hlx parser
```

- CI job creators are internal Microsoft engineers (typically), but Helix supports external contributors' PRs.
- A malicious PR author could craft a test that produces a TRX file with adversarial content.
- The TRX file schema is well-defined (`vstst:TestRun`) but content is arbitrary text (test names, error messages, stack traces).

**Trust level:** Semi-trusted. The XML structure is constrained by the test runner, but text content within elements is attacker-controlled.

### 2b. Data Exposure vs. Sanitization

**Safe to expose directly:**
- Test outcome (Passed/Failed/Skipped) — enum values from test framework
- Test name, class name, method name — these are code identifiers, not user input
- Duration, start/end times — numeric/datetime values
- Error message and stack trace — already visible in console logs (not a new disclosure)
- Test counters (total, passed, failed, skipped)

**Sanitize or limit:**
- `StdOut` / `StdErr` capture — may contain accidentally logged secrets. Apply the same treatment as console logs (expose but document risk, per threat model finding I1).
- File paths in stack traces — may reveal internal directory structures. Not a blocker but note in documentation.
- Custom properties / deployment metadata — could contain arbitrary key-value pairs. Expose but don't interpret.

**Verdict:** TRX content is no more sensitive than console logs, which we already serve verbatim (threat model I1). Apply the same policy: expose the data, document that it may contain secrets, rely on Helix API access control as the primary gate.

### 2c. Beyond-XML Harm from TRX Files

Once XML parsing is secure (XXE/DoS blocked), the remaining risk from TRX files is:
- **Memory exhaustion from very large TRX files** — a test suite with 100K tests could produce a multi-GB TRX file. Mitigate with file size limits (see §5).
- **Misleading test results** — a malicious TRX could claim all tests passed when they didn't. This is a data integrity concern, not a security concern. The MCP tool should document that it reports what the TRX contains, not ground truth.

---

## 3. Text Search / Grep Concerns

### 3a. Simple Matching vs. Regex

**Current state:** `SearchConsoleLogAsync` (HelixService.cs:586) and `MatchesPattern` (HelixService.cs:613-619) use `string.Contains` and `string.EndsWith` — simple string operations with zero ReDoS risk. This was a deliberate design choice documented in the threat model (E2).

**Recommendation: Stay with simple matching.** The benefits of adding regex are marginal for CI log search (users want to find "error", "CS1234", stack traces — all work with substring matching). The ReDoS risk is real: MCP tool parameters come from AI agents that may be prompt-injected, and crafting a ReDoS payload is trivial.

If regex is ever needed, use:
```csharp
var regex = new Regex(pattern, RegexOptions.None, matchTimeout: TimeSpan.FromSeconds(5));
```
The `matchTimeout` parameter (available since .NET 4.5) prevents catastrophic backtracking. But simple matching is preferred.

### 3b. File Size Limits for In-Memory Search

**Current state:** `SearchConsoleLogAsync` calls `File.ReadAllLinesAsync` (line 581) which reads the entire file into memory. Console logs are typically 1-10 MB but can reach 100+ MB for verbose test suites.

**Recommendation for extending to arbitrary files:**

| File type | Recommended max size | Rationale |
|-----------|---------------------|-----------|
| Console logs | 50 MB | Current behavior, acceptable for text search |
| TRX/XML files | 50 MB | Parsed into DOM; memory amplification ~3-5x |
| Arbitrary text files | 50 MB | Consistent limit across all text operations |
| Binlog files | Delegate to external tool | Binary format, no text search applicable |

Enforce with a pre-check before loading:
```csharp
var fileInfo = new FileInfo(path);
if (fileInfo.Length > MaxSearchFileSizeBytes)
    throw new HelixException($"File too large for search ({fileInfo.Length / 1_048_576} MB, max {MaxSearchFileSizeMB} MB).");
```

This follows the pattern of `MaxBatchSize` (HelixService.cs:488) — a constant with a clear error message.

### 3c. Encoding Detection

**Threat:** Files from CI environments can be any encoding — UTF-8, UTF-16, Windows-1252, even binary files misnamed as `.txt`. `File.ReadAllLinesAsync` uses UTF-8 by default and will silently produce garbled text for other encodings.

**Recommendation:**
- Default to UTF-8 (covers >95% of CI output).
- For XML/TRX: rely on the XML declaration (`<?xml version="1.0" encoding="utf-8"?>`). `XmlReader` handles this automatically.
- For arbitrary text search: accept UTF-8 only. If the file contains invalid UTF-8 sequences, skip those lines rather than crashing. Use `Encoding.UTF8` with `DecoderFallback.ReplacementFallback` (the .NET default) which replaces invalid bytes with `�`.
- Do NOT auto-detect encoding — encoding detection libraries add complexity and can be wrong.

---

## 4. Binlog Security

### 4a. Parsing Risks

MSBuild binary logs (.binlog) are a custom binary format. Parsing them requires the `MSBuild.StructuredLogger` NuGet package (or the newer `Microsoft.Build.Logging.StructuredLogger`). Risks:

- **Deserialization vulnerabilities** — binary format parsers can have buffer overflow, integer overflow, or type confusion bugs. The MSBuild structured logger is widely used but has had bugs.
- **Resource exhaustion** — binlogs can be very large (100+ MB). Loading one fully into memory uses 3-10x the file size.
- **Embedded file content** — binlogs can embed source file snapshots, environment variables, and MSBuild property values. These may contain secrets.

### 4b. Build vs. Delegate Decision

**Current state:** hlx already integrates with external binlog MCP tools (the `mcp-binlog-tool-*` functions visible in the tool registry). These tools provide comprehensive binlog analysis: project listing, target inspection, task analysis, diagnostic extraction, and freetext search.

**Recommendation: Delegate binlog parsing to the external binlog MCP tool. Do NOT parse binlogs in hlx.**

Reasons:
1. **The external tool already exists and is comprehensive** — it has 20+ operations covering all binlog analysis needs.
2. **Parsing binlogs requires a heavy dependency** (`Microsoft.Build.Logging.StructuredLogger`) which would significantly increase hlx's package size and attack surface.
3. **hlx's role is Helix API integration** — downloading binlogs and providing their file paths to the external tool is the correct layered architecture (per the original architecture proposal in decisions.md §1).
4. **Binary format parsing is higher-risk than XML** — we'd inherit any deserialization bugs in the structured logger library.

hlx should continue to: download binlogs (`hlx_download`, `hlx_download_url`), find binlogs (`hlx_find_binlogs`), and hand off file paths to the binlog MCP tool for parsing.

### 4c. Cache Poisoning for Parsed Results

If we were to cache parsed binlog results (we shouldn't parse, but for completeness):
- The existing cache isolation pattern (per-token SQLite databases, `CacheSecurity.SanitizeCacheKeySegment`) would apply.
- Cache key must include a content hash of the binlog file, not just the URL (URLs can be reused with different content if SAS tokens rotate).
- TTL should match artifact TTL (7 days per current cache policy).

Since we recommend delegating binlog parsing, this concern is moot for hlx. The external binlog tool manages its own state.

---

## 5. Recommendations Summary

### Concrete .NET API Recommendations

| Concern | API | Settings |
|---------|-----|----------|
| XML parsing | `XmlReader.Create(stream, settings)` | `DtdProcessing = Prohibit`, `XmlResolver = null`, `MaxCharactersFromEntities = 0`, `MaxCharactersInDocument = 50_000_000` |
| JSON serialization | `System.Text.Json` (existing) | Already safe — no polymorphic deserialization, no `TypeNameHandling` equivalent |
| Text search | `string.Contains(pattern, StringComparison.OrdinalIgnoreCase)` | Keep current pattern — no regex |
| Encoding | `Encoding.UTF8` with default fallback | Skip encoding auto-detection |

### Size/Complexity Limits

| Resource | Limit | Rationale |
|----------|-------|-----------|
| XML/TRX file for parsing | 50 MB | DOM parsing amplifies memory ~3-5x |
| Text file for search | 50 MB | `ReadAllLines` loads entire file into string array |
| Max search results | 50 (existing) | Already enforced in `SearchConsoleLogAsync` |
| Max batch size | 50 (existing) | Already enforced in `GetBatchStatusAsync` |
| XML nesting depth | No explicit limit needed | `XmlReader` has internal limits; `DtdProcessing.Prohibit` blocks the main amplification vector |

### In-Scope vs. Delegate

| Feature | Recommendation | Rationale |
|---------|---------------|-----------|
| TRX/XML parsing | **In-scope for hlx** | Natural extension of Helix file analysis. XML parsing is safe with correct settings. |
| Text search on artifact files | **In-scope for hlx** | Extension of existing `SearchConsoleLogAsync` pattern. |
| Binlog parsing | **Delegate to external binlog MCP tool** | Heavy dependency, complex binary format, tool already exists. |
| Structured test failure extraction (US-22) | **In-scope for hlx** | Parse TRX files to extract test name, outcome, error message, stack trace. |

### Implementation Pattern for XML Parsing

Follow existing patterns in the codebase:
1. **Define a static `XmlReaderSettings`** — like `s_jsonOptions` in `HelixMcpTools.cs:10`
2. **Download file first, then parse** — like `SearchConsoleLogAsync` downloads to temp, reads, deletes
3. **Check file size before parsing** — like `MaxBatchSize` check in `GetBatchStatusAsync:496`
4. **Sanitize all path segments** — reuse `CacheSecurity.SanitizePathSegment` (Cache/CacheSecurity.cs:32)
5. **Validate paths within root** — reuse `CacheSecurity.ValidatePathWithinRoot` (Cache/CacheSecurity.cs:12)

### New Security Constant Recommendations

```csharp
// In HelixService.cs or a dedicated SecurityLimits.cs
internal const int MaxParseFileSizeBytes = 50 * 1024 * 1024;  // 50 MB
internal const int MaxSearchFileSizeBytes = 50 * 1024 * 1024; // 50 MB
```

---

## Risk Summary

| Threat | Severity | Mitigation |
|--------|----------|-----------|
| XXE in TRX/XML files | Low (with correct settings) | `DtdProcessing.Prohibit`, `XmlResolver = null` — .NET 10 defaults are safe but set explicitly |
| Entity expansion DoS | None | Blocked by `DtdProcessing.Prohibit` |
| Memory exhaustion from large files | Medium | 50 MB file size limit pre-check |
| ReDoS in text search | None | Keep simple `string.Contains` matching — no regex |
| Encoding issues | Low | UTF-8 default with replacement fallback |
| Binlog deserialization bugs | N/A | Delegate to external tool — not parsed in hlx |
| TRX data exfiltration | Low | Same trust level as console logs (already exposed) |
| Cache poisoning of parsed results | Low | Existing cache isolation patterns apply |



---

### 2026-02-13: User directive
**By:** Larry Ewing (via Copilot)
**What:** Remote file search and structured parsing features (hlx_search_file, hlx_test_results) should be disableable via a configuration setting, as a security safeguard for operators who want to restrict file content access.
**Why:** User request — defense-in-depth. Operators deploying the HTTP MCP server may want to limit the attack surface by disabling content parsing features while still allowing file listing and download.

---

# US-31: hlx_search_file Phase 1 Implementation

**By:** Ripley
**Date:** 2025-07-23

## What was implemented

1. **Extracted `SearchLines` helper** — private static method in HelixService that encapsulates the line-matching + context-gathering logic. Both `SearchConsoleLogAsync` and the new `SearchFileAsync` use it, eliminating duplication.

2. **`SearchFileAsync`** — downloads a single file via `DownloadFilesAsync(exact fileName)`, checks for binary content (null byte in first 8KB), enforces 50MB size limit (`MaxSearchFileSizeBytes`), then delegates to `SearchLines`. Returns `FileContentSearchResult` record.

3. **Config toggle** — `HLX_DISABLE_FILE_SEARCH=true` env var disables both `SearchConsoleLogAsync` and `SearchFileAsync` (throws `InvalidOperationException`). MCP tools check via `HelixService.IsFileSearchDisabled` and return JSON error instead of throwing.

4. **MCP tool `hlx_search_file`** — follows exact `SearchLog` pattern with URL resolution and config toggle.

5. **CLI command `search-file`** — follows `search-log` pattern with positional args.

## Decisions made

- **Binary detection strategy**: Check for null byte in first 8KB. Simple and effective — avoids searching compiled binaries, zip files, etc.
- **File download reuse**: Used existing `DownloadFilesAsync` with exact fileName as pattern rather than adding a new download method. Works because `MatchesPattern` does substring match.
- **Config toggle scope**: Applied to both search-log and search-file per Larry's directive. The env var name `HLX_DISABLE_FILE_SEARCH` covers all file content search operations.

## For Lambert

Tests needed:
- `SearchLines` helper (extracted logic, same behavior as before)
- `SearchFileAsync` — binary detection, size limit, normal search, file-not-found
- `IsFileSearchDisabled` toggle — both service methods and MCP tools
- `SearchFile` MCP tool — URL resolution, config toggle, binary file JSON error
- `SearchFile` CLI command smoke test

## Files changed

- `src/HelixTool.Core/HelixService.cs` — SearchLines helper, SearchFileAsync, FileContentSearchResult, IsFileSearchDisabled, MaxSearchFileSizeBytes
- `src/HelixTool.Core/HelixMcpTools.cs` — hlx_search_file tool, config toggle on hlx_search_log
- `src/HelixTool/Program.cs` — search-file CLI command