# Decisions

> Shared team decisions — the single source of truth for architectural and process choices.

### 2025-07-18: Architecture Review — hlx project improvement proposal



### 2025-07-18: Architecture Review — Initial Assessment (archived)

- Archived summary of the original Dallas review: the Core/CLI/MCP split was sound, but namespace collisions, direct SDK construction, and missing error handling were the main structural risks.
- Most follow-up work landed in 2026 (DI/testability, cache architecture, namespace cleanup, tool extraction, folder reorg); remaining long-term guidance is to keep service boundaries clear and continue treating descriptions/docs as behavioral contracts.

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

- Foundation contract: `IHelixApiClient` is the single mock boundary; `HelixService` uses constructor injection; `HelixException`, input validation, and `CancellationToken` support are standard across async APIs.
- Use projection interfaces for concrete Helix SDK models and keep shared JSON options static instead of allocating per call.

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

- Use `cancellationToken.IsCancellationRequested` to distinguish true cancellation from HTTP timeout when catching `TaskCanceledException`.
- Helix SDK models are concrete, so `HelixApiClient` should adapt them to projection interfaces (`IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile`) before they reach the service layer.

- **TaskCanceledException timeout detection:** Use `cancellationToken.IsCancellationRequested` (true = real cancellation, false = HTTP timeout), NOT `ex.CancellationToken == cancellationToken`. The equality check fails when both tokens are `CancellationToken.None` (default parameter).
- **Helix SDK model types are concrete:** `JobDetails`, `WorkItemSummary`, `WorkItemDetails`, `UploadedFile` have no interfaces. Solved with projection interfaces (`IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile`) in `IHelixApiClient.cs` and private adapter classes in `HelixApiClient.cs`.
- **ConsoleAppFramework DI:** CLI uses `ConsoleApp.ServiceProvider = services.BuildServiceProvider()` before `ConsoleApp.Create()`. CAF v5 supports DI natively via this static property pattern.

### 2026-02-11: P0 Test Infrastructure Decisions

- NSubstitute 5.x is the mocking framework for Core tests; invalid-input tests were updated to assert throws after the resolver contract changed.
- Parallel/proactive test writing against an agreed design spec is acceptable when signatures are stable.

- **NSubstitute 5.* chosen as mocking framework** per Dallas's D9 recommendation. Simpler API than Moq (no `Setup(...).Returns(...)` ceremony, no `.Object` property). `.ThrowsAsync()` from `NSubstitute.ExceptionExtensions` maps cleanly to D6 error handling contract.
- **HelixIdResolver invalid-input tests updated for D7 breaking change:** Replaced 5 pass-through tests with `ArgumentException` throw assertions. Happy-path GUID/URL extraction tests unchanged.
- **Proactive parallel test writing validated:** 19 tests written against design spec before implementation existed. All compiled and passed once Ripley's code landed.

### 2026-02-11: US-1 & US-20 Implementation — Positional Args + Rich Status Output

- CLI commands use positional `jobId` / `workItem` arguments, and status output includes state, machine, duration, and other richer work-item metadata for both CLI and MCP.
- The duplicated `FormatDuration` helper is acceptable with two consumers; only extract it if a third caller appears.

- **US-1 (Positional Arguments):** Applied `[Argument]` attribute to `jobId` on all 5 commands and `workItem` on logs/files/download. Named `--job-id` flag still works. Updated `llmstxt` to reflect positional syntax.
- **US-20 (Rich Status Output):** Expanded `IWorkItemDetails` with `State`, `MachineName`, `Started`, `Finished`. `WorkItemResult` now includes `State`, `MachineName`, `Duration`. CLI shows `[FAIL] Name (exit code 1, 2m 34s, machine: helix-win-01)`. MCP JSON includes per-work-item `state`, `machineName`, `duration`.
- **FormatDuration duplication:** Helper is duplicated between CLI and MCP. Acceptable for two consumers; extract to Core if a third appears.
- **Test impact:** Updated mock setup for new `IWorkItemDetails` fields. 38/38 tests pass.

### 2026-02-12: Documentation fixes — llmstxt, README, XML doc comments

- `README.md` and the `llmstxt` output in `Program.cs` are the authoritative public docs and must be updated together whenever commands or MCP tools change.
- The 2026 docs pass also added XML comments broadly; remaining historical gaps were HelixIdResolver docs, HelixMcpTools class docs, LICENSE, and dotnet-tool install guidance.

- **llmstxt indentation fix** — Raw string literal now uses `var text = """...""";` pattern with proper indentation stripping.
- **MCP tools added to llmstxt** — All five MCP tools documented with JSON return shapes, parameters, and CLI-vs-MCP guidance.
- **README.md updated** — Added Architecture section, Installation section (build-from-source + NuGet feed), Known Issues section (dnceng#6072 workaround, US-28).
- **XML doc comments** — Added to `IHelixApiClient` (all 6 methods), `HelixApiClient`, `HelixException`, `HelixService` (constructor, all public methods, all 4 public record types).
- **Decision for team:** The llmstxt content and README now serve as the authoritative docs for the public API surface. When new commands or MCP tools are added, both must be updated together.
- **Remaining gaps:** `HelixIdResolver` XML docs, `HelixMcpTools` class-level doc comment, LICENSE file, `dotnet tool install` instructions.

### 2026-02-12: US-4 Authentication Design — HELIX_ACCESS_TOKEN env var, optional token constructor

- Helix auth uses the `HELIX_ACCESS_TOKEN` environment variable; `HelixApiClient(string? accessToken)` stays anonymous when unset and authenticated when present.
- No built-in login flow was added here — auth remained an environment/transport concern, with 401/403 errors pointing users toward the env var.

- **D-AUTH-1:** Token source is `HELIX_ACCESS_TOKEN` environment variable. Matches arcade naming convention. No config file, no secrets on disk.
- **D-AUTH-2:** `HelixApiClient` constructor accepts optional `string? accessToken`. Null/empty → anonymous (zero breakage). Non-empty → authenticated via `HelixApiTokenCredential`.
- **D-AUTH-3:** All 3 DI registrations (CLI, MCP stdio, MCP HTTP) read env var at startup with `Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN")`.
- **D-AUTH-4:** `IHelixApiClient` interface unchanged. Auth is a transport concern inside `HelixApiClient`. No test impact.
- **D-AUTH-5:** `HelixService` catches 401/403 with actionable error: "Set HELIX_ACCESS_TOKEN environment variable to access internal builds."
- **D-AUTH-6:** MCP client config example: `{ "command": "hlx", "args": ["mcp"], "env": { "HELIX_ACCESS_TOKEN": "<token>" } }`
- **D-AUTH-7:** No token acquisition built into tool. No `hlx auth login`. Token is opaque string from env var. ~35 lines of code, no new dependencies.

### 2026-02-12: Stdio MCP Transport — `hlx mcp` subcommand (Option B approved)

- Primary MCP transport is stdio via `hlx mcp` inside the CLI binary; HTTP transport remains a separate path for remote or multi-client scenarios.
- Run MCP logging to stderr and keep tool discovery explicit when scanning assemblies outside the entry assembly.

- **Decision:** Support stdio MCP via `hlx mcp` subcommand in CLI binary. Not in separate HelixTool.Mcp project.
- **Why:** All primary consumers (Copilot CLI, Claude Desktop, VS Code, ci-analysis skill) use stdio. Single binary install aligns with US-5.
- **Packages:** CLI adds `ModelContextProtocol` (base, ~100KB) + `Microsoft.Extensions.Hosting`. No ASP.NET Core for stdio.
- **HelixTool.Mcp kept:** HTTP transport remains for remote/multi-client scenarios.
- **Logging:** All output to stderr via `LogToStandardErrorThreshold = LogLevel.Trace`.
- **MCP client config:** `{ "command": "hlx", "args": ["mcp"] }`
- **Risks:** ConsoleAppFramework + Host may conflict; `mcp` command bypasses CAF and runs Host directly. Tool assembly scanning needs explicit assembly reference if tools are in Core.

### 2026-02-12: Stdio MCP Implementation — Runtime decisions

- The `mcp` command builds its own DI container with `Host.CreateApplicationBuilder()` instead of reusing the CLI command container.
- At this stage duplicated tool definitions were acceptable, but tool discovery still required `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)`.

- `HelixMcpTools.cs` copied to CLI project (same namespace, discovered by `WithToolsFromAssembly()`).
- `mcp` command creates its own DI container via `Host.CreateApplicationBuilder()` — does not reuse CLI's `ServiceCollection`.
- HelixTool.Mcp unchanged (HTTP transport stays).
- Duplication accepted; extract to Core if maintenance burden grows.
- Build and 55/55 tests pass.

### 2026-02-12: MCP Tools Test Strategy

- MCP tests parse JSON output and assert behavior through the tool surface rather than reaching into formatting helpers directly.
- Original error-path convention was structured error JSON for download failures; later structured-content work superseded the string-only transport details.

- Tests reference HelixTool.Mcp via `ProjectReference`. `HelixMcpTools` lives in `namespace HelixTool`.
- `FormatDuration` tested indirectly through Status output (6 branches via arranged timestamps).
- MCP tool methods return JSON strings; tests parse with `JsonDocument` and assert structure/values.
- Download error path returns `{error: "..."}` JSON rather than throwing.
- **Impact:** If `HelixMcpTools` is removed from HelixTool.Mcp, test ProjectReference must change.

### 2026-02-12: US-5 + US-25 Implementation — dotnet tool packaging + ConsoleLogUrl

- HelixTool ships as a dotnet tool, and work-item results include a computed `ConsoleLogUrl` in both CLI and MCP output for direct navigation.
- When MCP tool definitions lived in two hosts, both copies had to stay in sync for wire-format changes.

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

- Added `GetWorkItemDetailAsync` and `GetBatchStatusAsync`, plus matching CLI/MCP entry points for per-item details and multi-job aggregation.
- Batch status uses `SemaphoreSlim(5)` throttling and work-item detail fetches details/files in parallel.

**US-10 (Work Item Detail):** New `GetWorkItemDetailAsync` method in `HelixService` returns detailed info about a single work item — exit code, state, machine, duration, console log URL, and file list with type tags. Design: parallel fetch via `Task.WhenAll`, `WorkItemDetail` nested record, CLI `hlx work-item <jobId> <workItem> [--json]`, MCP `hlx_work_item` with US-29 URL resolution, standard error handling.

**US-23 (Batch Status):** New `GetBatchStatusAsync` method queries status for multiple jobs in parallel with `SemaphoreSlim(5)` throttling. Returns `BatchJobSummary` with per-job results and aggregate totals. Design: simple error propagation (exceptions bubble up), CLI one-line-per-job summary, MCP accepts comma-separated job IDs.

## Files Changed
- `src/HelixTool.Core/HelixService.cs` — `WorkItemDetail`, `GetWorkItemDetailAsync`, `BatchJobSummary`, `GetBatchStatusAsync`
- `src/HelixTool/Program.cs` — `work-item` and `batch-status` commands
- `src/HelixTool/HelixMcpTools.cs` — `hlx_work_item` and `hlx_batch_status` tools
- `src/HelixTool.Mcp/HelixMcpTools.cs` — same tools for HTTP MCP server

### 2026-02-12: US-21 Failure Categorization

- Failure categorization lives in a top-level `FailureCategory` enum and is populated only for failed work items.
- `ClassifyFailure` is a static helper on `HelixService`; state-based heuristics outrank exit-code guesses.

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

- `SearchConsoleLogAsync` followed a simple download-search-delete pattern and added `LogMatch`/`LogSearchResult` with optional context windows.
- Search reused existing console-log download/error handling instead of adding a separate streaming path.

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

- Tool definitions were consolidated into one `HelixTool.Core` source file so stdio and HTTP hosts stop carrying duplicate implementations.
- Both hosts must call `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)` because default scanning only inspects the calling assembly.

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

- Cache requirements settled on SQLite metadata + disk artifacts, XDG-compliant roots, a 1 GB default cap, and cross-process sharing for stdio MCP processes.
- Running console logs are never cached; completed logs, file lists, job/work-item metadata, and downloaded artifacts follow the documented TTL/eviction policy.

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

- Caching belongs in a `CachingHelixApiClient` decorator over `IHelixApiClient`, not inside `HelixService`, with `ICacheStore`, SQLite schema, and cache commands owned in `Cache/`.
- The design also established `HLX_CACHE_MAX_SIZE_MB`, WAL-mode SQLite, full-log/artifact caching rules, and the cache-key structure that later implementations followed.

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

- The first cache test pass covered the decorator, SQLite store, and options with 56 tests and established temp-dir + sequential-return patterns for cache-hit simulation.
- Cache behavior should continue to be split between fast mock-based unit tests and focused SQLite integration tests.

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

- The shipped cache used `Microsoft.Data.Sqlite`, destructive `user_version` migrations, a simplified artifact path strategy, and fire-and-forget startup eviction.
- `ICacheStore` is the test seam; `CachingHelixApiClient` returns DTOs that implement the same projection interfaces used elsewhere in Core.

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

- Cache roots split by auth context: `public/` for anonymous use and `cache-{hash}/` for authenticated contexts, where the hash is derived from `HELIX_ACCESS_TOKEN`.
- Same-token reuse is intentional across restarts, and `hlx cache clear` is expected to wipe every auth-context subdirectory.

Where `{hash}` = first 8 hex chars of SHA256 of the token (lowercase, deterministic).

**Changes:**
- `CacheOptions.cs`: Added `AuthTokenHash` property, `GetBaseCacheRoot()` method, `ComputeTokenHash()` static helper. `GetEffectiveCacheRoot()` now subdivides by auth context.
- `Program.cs`: Both DI containers pass `CacheOptions.ComputeTokenHash(HELIX_ACCESS_TOKEN)` as `AuthTokenHash`.
- `cache clear` wipes ALL auth context subdirectories. `cache status` shows current auth context info.
- `SqliteCacheStore` unchanged — already uses `GetEffectiveCacheRoot()`.
- Same token always produces the same hash → cache reuse across restarts. Different tokens → different caches.

---

### 2026-02-13: Path Traversal Hardening for Cache and Download Paths (Security Fix)

- Introduced `CacheSecurity` helpers for segment sanitization and root-boundary validation, then applied them to cache keys, artifact paths, and download outputs.
- Path handling in cache/download code should always sanitize user-controlled segments and confirm the resolved path stays beneath the intended root.

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

- Remote HTTP/SSE mode should resolve Helix tokens per request via `IHttpContextAccessor`, `IHelixTokenAccessor`, `IHelixApiClientFactory`, and per-token cache-store reuse.
- That design keeps `HelixService` and tool classes unchanged while isolating auth state and cache data across concurrent clients.

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

- Do not build general multi-auth UX for hlx yet; single-token-per-process matches stdio CLI/MCP usage and keeps the composition roots simple.
- Remote multi-client auth is a transport concern, not a reason to complicate the normal CLI or stdio workflows.

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

### 2026-02-15: README comprehensive updates (caching, v0.1.3, project structure)

- README was expanded to cover caching, auth, security, project structure, and the fuller tool surface as the product matured.
- That sweep also surfaced a docs-sync rule: the `llmstxt` raw string in `Program.cs` must be updated when README command/tool inventories change.

**What changed (cumulative):**
- Caching section added (settings table, TTL policy, auth isolation, CLI commands)
- HTTP per-request auth subsection added under Authentication
- MCP Tools table expanded from 9 → 12 tools (full inventory from HelixMcpTools.cs)
- CLI Commands reference table added (15 commands with exact signatures)
- Security section added (XML parsing, path traversal, URL validation, file search toggle, batch limits)
- Authentication updated with HLX_API_KEY documentation
- Project Structure expanded (Cache/, IHelixTokenAccessor, IHelixApiClientFactory, HttpContextHelixTokenAccessor)
- Test count updated 298 → 340
- Quick Start examples updated for current CLI signatures
- ci-analysis replacement note added in Architecture

**Note for Ripley:** The llmstxt output in Program.cs is now out of sync with the README — it’s missing hlx_search_file, hlx_test_results, and the search-file/	est-results CLI commands. It should be updated to match.

### 2026-02-13: Requirements audit — P0/P1/P2 completion status

**Key findings for the team:**
1. **US-22 (structured test failure parsing)** is the only P2 that's NOT fully done. `hlx_search_log` provides generic search, but the structured `hlx_test_failures` tool was never built. This is the single remaining gap for full ci-analysis migration.
2. **US-21 failure categorization** uses heuristics (exit code + state + work item name) rather than log content parsing as originally specified. The log-based criteria (checking for "Traceback", "Failures: 0") were not implemented. Current approach is sufficient for most cases.
3. **US-17 code organization** is functionally complete (namespaces fixed) but the models-extraction and Display/ cleanup criteria are outstanding — minor refactoring debt.
4. **US-11 --json flag** doesn't cover `find-binlogs` or `batch-status` — minor gap for power users.

### 2026-02-13: MCP API design review

**By:** Dallas
**What:** Comprehensive API design review of all 9 MCP tool endpoints in HelixMcpTools.cs. Verdict: the surface is well-designed for its domain and NOT overly ci-analysis-specific — it's a general-purpose Helix job inspection API that ci-analysis happens to consume. Identified 6 actionable improvements: (1) rename `hlx_logs` → `hlx_log_content`, (2) rename `hlx_download_url` → `hlx_download_file_url`, (3) fix `hlx_batch_status` comma-separated string → proper array parameter, (4) add `hlx_list_work_items` as a missing navigation tool, (5) standardize response envelope with consistent `{data, error?}` shape, (6) fix `hlx_status` inconsistent `all` parameter naming. Priority order: P0 batch_status array fix, P1 list_work_items gap, P2 naming improvements, P3 response envelope standardization.
**Why:** Larry raised concern that the MCP surface was too tightly coupled to ci-analysis workflows. After thorough review, the tools map to Helix API primitives (jobs, work items, files, logs) rather than ci-analysis-specific orchestrations. The naming uses `hlx_` prefix consistently and maps to Helix domain concepts. However, there are real usability issues that would trip up non-ci-analysis consumers: the comma-separated jobIds string in batch_status is hostile to programmatic callers, the missing list_work_items tool forces consumers to use hlx_status (heavy) just to discover work item names, and some tool names don't self-document well. These fixes would make the API genuinely general-purpose.
- The backlog audit showed the project was much further along than the old requirements doc implied: 25/30 stories were effectively implemented.
- At that point the main remaining P2 gap was structured test-failure parsing; other deltas were mostly wording, heuristics, or minor ergonomics.

### 2026-02-14: Generalize hlx_find_binlogs to hlx_find_files with pattern parameter

- Core scanning should be generic (`FindFilesAsync(jobId, pattern, maxItems)`), while `hlx_find_binlogs` stays as a convenience alias for the dominant case.
- Prefer one generic file-discovery tool plus one common-case alias instead of growing per-artifact tool sprawl.

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

- MCP JSON tests must assert camelCase property names because tool serialization uses `JsonNamingPolicy.CamelCase`.
- This convention applies to all existing and future MCP-surface test files.

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

- Batch-status and related MCP tests had to switch from PascalCase assertions to camelCase and from `binlogs` to `files` after the API cleanup.
- When MCP result shapes change, test assertions should be updated at the same time rather than patched ad hoc later.

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

- The STRIDE review identified 16 grounded threats; HTTP auth for remote deployment is a pre-GA requirement, while stdio remained secure by default.
- Immediate hardening priorities were URL-scheme validation and batch-size limits, with broader download allowlisting deferred.

**What:** Completed a STRIDE-based threat model for lewing.helix.mcp covering all 6 categories. Identified 16 specific threats grounded in actual source code. Two are High severity (both related to HTTP MCP server lacking authentication), two are Medium (arbitrary URL download SSRF, unbounded batch size). Path traversal protection and token handling are well-implemented.

**Why:** Larry asked whether we should do a threat model. The answer is yes — the HTTP MCP server has no authentication middleware, which is a real deployment risk. The stdio mode (default) is secure by design. The `hlx_download_url` accepting arbitrary URLs is a secondary concern since the HttpClient is unauthenticated, but it still enables SSRF probing. These findings should inform prioritization before any HTTP deployment.

**Review verdict:** ✅ APPROVED with minor amendments. Dallas verified 15+ line-number references against source — all correct. One amendment: S1 recommendation should be "Add auth middleware for non-localhost deployment" rather than "default to localhost" (already the ASP.NET Core default).

**Action items for the team:**
1. **S1/I4 (HTTP auth) is a pre-GA gate** — not a current blocker for stdio-only deployments. Track as a requirement for any HTTP mode production use.
2. **E1 (URL scheme validation)** — Ripley should add a scheme check in `DownloadFromUrlAsync` as part of normal hardening. One-liner: reject non-`http`/`https` schemes.
3. **D1 (batch size limit)** — Add `ArgumentException` guard in `GetBatchStatusAsync` if `idList.Count > 50`. Prevents agent-driven resource exhaustion.
4. **T2 (domain allowlist for download-url)** — Defer. Too restrictive for a diagnostic tool where blob storage URLs vary. Document the risk instead.

### 2025-07-23: P1 Security Fixes — E1 URL Scheme Validation + D1 Batch Size Limit

- Implemented URL scheme validation for direct downloads and capped batch status to 50 job IDs, surfacing the limit in tool descriptions as well.
- Security fixes of this kind should use `ArgumentException` guards at the service boundary and let tests pin exact boundary behavior.

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

- Security tests accept downstream network failures for valid HTTP/HTTPS URLs and focus only on whether validation rejected or allowed the scheme.
- Boundary coverage centers on 50/51 batch sizes, and MCP tests should verify that service-layer guards propagate through the tool surface.

**Context:** Threat model identified P1 security findings E1 (SSRF via DownloadFromUrlAsync) and D1 (unbounded batch size in GetBatchStatusAsync). Tests written concurrently with Ripley's production fixes.

**Decisions:**

1. **URL scheme tests use negative assertion pattern:** For HTTP/HTTPS acceptance, tests use `Record.ExceptionAsync` + `Assert.IsNotType<ArgumentException>` rather than asserting no exception at all. The method will fail with network-related exceptions (no real server), but that's expected — we only verify scheme validation doesn't reject valid schemes.

2. **No-scheme URL test accepts both UriFormatException and ArgumentException:** The `new Uri(url)` constructor throws `UriFormatException` for schemeless strings *before* the scheme check runs. Both exception types are acceptable rejection behavior. Test uses `Assert.True(ex is A || ex is B)`.

3. **Batch boundary tests at 50/51:** MaxBatchSize = 50 tested at exact boundary (50 accepted, 51 rejected). Bulk mock setup uses `Enumerable.Range` with formatted GUIDs.

4. **MCP tool tests verify propagation:** `hlx_batch_status` tests confirm that the service-layer `ArgumentException` propagates through the MCP tool method. No separate MCP-layer validation needed — the service enforces the limit.

**Files:** `src/HelixTool.Tests/SecurityValidationTests.cs` — 18 tests (all passing)

### 2026-02-13: Remote search and structured file querying — feature design

- File-content search and TRX parsing are in scope for hlx, built on the same remote-search patterns as console-log search, while binlog analysis stays delegated to the external binlog tool.
- Keep matching substring-based (no regex), cap in-memory file parsing/search sizes, and phase work as generic file search first, structured test parsing second.

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

- Structured file parsing must use explicit safe `XmlReaderSettings`, 50 MB pre-checks, and the same “treat file content as untrusted input” stance already used for console logs.
- TRX/XML parsing and text search stay in hlx, but binary/binlog parsing remains delegated to the dedicated external toolchain.

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

- Operators must be able to disable remote file-content analysis features as a defense-in-depth control, even while leaving metadata/file-list tooling available.
- That directive became the basis for the `HLX_DISABLE_FILE_SEARCH` toggle used by search and structured-result parsing.

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

---

# Decision: Status filter refactored from boolean to enum-style string

**By:** Ripley
**Date:** 2025-07-23
**Requested by:** Larry Ewing

## Context

The `status` command/tool previously used a boolean `--all`/`includePassed` flag to toggle showing passed work items. This was limited — you could only see "failed only" or "everything". There was no way to see "passed only".

## Decision

Replaced the boolean with a three-value string `filter` parameter:
- `"failed"` (default) — shows only failed items (backward-compatible default)
- `"passed"` — shows only passed items
- `"all"` — shows both failed and passed items

Validation throws `ArgumentException` for invalid values. Comparison uses `StringComparison.OrdinalIgnoreCase`.

## Impact

- **MCP tool (`hlx_status`):** `bool includePassed` → `string filter = "failed"`. When filter is `"passed"`, `failed` is null in output. When `"failed"`, `passed` is null. When `"all"`, both populated.
- **CLI command (`status`):** `bool all` → `[Argument] string filter = "failed"`. Second positional arg. Usage: `hlx status <jobId> [failed|passed|all]`.
- **Breaking change:** Existing callers using `--all` or `includePassed=true` must update to `filter="all"`.
- **Tests:** Lambert needs to update tests for the new parameter signature and filter logic.

---

# US-32: TRX Parsing Implementation Notes

**By:** Ripley  
**Date:** 2025-07-23

## What was implemented

Phase 2 of US-32 — structured TRX test result parsing via `ParseTrxResultsAsync` in HelixService, `hlx_test_results` MCP tool, and `test-results` CLI command.

## Key decisions made during implementation

1. **XmlReaderSettings as static readonly field** — Named `s_trxReaderSettings` following the `s_jsonOptions` naming convention. Security settings per Ash's review: `DtdProcessing.Prohibit`, `XmlResolver=null`, `MaxCharactersFromEntities=0`, `MaxCharactersInDocument=50_000_000`.

2. **Error truncation limits** — 500 chars for error messages, 1000 chars for stack traces. These are hard-coded in `ParseTrxFile`. If consumers need full error text, they can use `hlx_search_file` on the TRX file directly.

3. **Reuses `IsFileSearchDisabled` and `MaxSearchFileSizeBytes`** — Same config toggle and size guard as `SearchFileAsync`. TRX parsing is a form of file content analysis, so the same security controls apply.

4. **Filter logic** — Failed tests always included, non-pass/non-fail (skipped, etc.) always included, passed tests only when `includePassed=true`. This keeps default output focused on actionable results.

## For Lambert

Tests needed for:
- `ParseTrxResultsAsync` — happy path, file not found, oversized file, disabled toggle
- `ParseTrxFile` — valid TRX, empty TRX, error truncation, includePassed filter, maxResults cap
- `hlx_test_results` MCP tool — URL resolution, config toggle, missing workItem
- `test-results` CLI command — basic invocation

---

# Decision: Status Filter Test Coverage Strategy

**By:** Lambert (Tester)
**Date:** 2026-02-15
**Context:** Status API migration from `includePassed: bool` to `filter: string`

## Decision

The `filter: "passed"` case introduces a new behavioral pattern where `failed` is null (the reverse of `filter: "failed"` nulling `passed`). Tests cover this symmetry explicitly, plus case-insensitivity and invalid input rejection.

Test naming convention follows the pattern `Status_Filter{Value}_{Assertion}` for consistency with the new API shape. The old `Status_AllTrue`/`Status_AllFalse` names are retired.

## Tests Added (5 new + 2 renamed)

| Test | Filter | Validates |
|------|--------|-----------|
| `Status_FilterFailed_PassedIsNull` | "failed" | passed=null (renamed) |
| `Status_FilterAll_PassedIncludesItems` | "all" | passed populated (renamed) |
| `Status_DefaultFilter_ShowsOnlyFailed` | (none) | default = "failed" behavior |
| `Status_FilterPassed_FailedIsNull` | "passed" | failed=null, passed populated |
| `Status_FilterPassed_IncludesPassedItems` | "passed" | item structure verification |
| `Status_FilterCaseInsensitive` | "ALL" | uppercase accepted |
| `Status_InvalidFilter_ThrowsArgumentException` | "invalid" | ArgumentException thrown |

## Impact

Test count: 364 → 369 (net +5). All 15 status tests pass.

### 2025-07-25: Cache security expectations documented in README

**By:** Kane
**What:** Added a "Cached data" subsection under Security in README.md. Documents what gets cached (API responses + artifacts), where it lives (SQLite on disk in user profile directory), that auth tokens are never cached (only hash prefix for directory isolation), and recommends `hlx cache clear` for shared machines or security context switches. Addresses threat model items I1 (information disclosure via cached data) and I2 (cache persists after session).
**Why:** The threat model (`.ai-team/analysis/threat-model.md`) explicitly recommended documenting cache security expectations. Users need to know the cache directory may contain sensitive CI data (console logs with accidental secrets) and understand the auth isolation model. This closes the documentation gap for I1/I2 without requiring code changes.

### 2026-02-15: Isolate DownloadFilesAsync temp directories per invocation

- Download temp directories include a per-invocation GUID so concurrent stdio/MCP processes never write into the same folder.
- Any disk-backed workflow that can run in parallel should avoid predictable shared temp paths.

**Decided by:** Ripley  
**Date:** 2025-07-23  
**Status:** Implemented

## Context

The publish workflow triggers on `v*` tags and extracts the version from the tag. Previously, nothing verified that the csproj and server.json versions matched the tag, and `dotnet pack` used whatever version was in the csproj.

## Decision

1. **Tag is source of truth for package version.** The Pack step now passes `/p:Version=` from the tag, overriding the csproj value.
2. **Validation step before Pack** checks that `HelixTool.csproj <Version>`, `server.json .version`, and `server.json .packages[0].version` all match the tag. Fails the build on mismatch with clear error messages.
3. **Belt-and-suspenders approach:** validation catches mistakes early for developers, `/p:Version=` override ensures the published artifact is always correct.

## Impact

- All team members must update csproj and server.json version fields before tagging a release.
- The workflow will fail fast with actionable error messages if versions are out of sync.
- No CI workflow exists yet (`ci.yml`), so validation is only in the publish workflow.

### 2026-02-27: Enhancement layer documentation (consolidated)

- The consolidated enhancement pass documented hlx’s value-add over raw Helix APIs in README and aligned docs cleanup around that framing.
- Remaining follow-ups were mostly llmstxt/tool-description completeness, not new architectural work.

**What:**

1. **Value-add inventory (Dallas, 2025-07-23):** Cataloged 12 local enhancements hlx provides over raw Helix REST APIs — 5 MAJOR (cross-process SQLite cache, smart TTL policy, failure classification, TRX parsing, remote content search), 3 SIGNIFICANT (URL parsing, cross-work-item file discovery, batch aggregation), 3 MODERATE (file type classification, computed duration, auth-isolated cache), 1 MINOR (log URL construction).

2. **Docs gap analysis (Kane, 2025-07-18):** Audited README (B+), MCP [Description] attributes (C), and llmstxt (C+). Key gaps: MCP descriptions don't convey that features like failure categorization and TRX parsing are local enhancements; hlx_search_file description misleadingly says "without downloading"; llmstxt missing hlx_search_file and hlx_test_results.

3. **README section added (Kane, 2025-07-18):** Wrote "How hlx Enhances the Helix API" section with two tables — 5 major enhancements (3-column: enhancement/what you get/why it matters) and 7 convenience enhancements (2-column). Placed between Failure Categorization and Project Structure.

**Why:** Users (human and LLM) need to understand what hlx adds beyond raw API access. The README covered features but didn't frame them as enhancements over the raw API. MCP tool descriptions are the primary documentation surface for AI agents and lacked this context.

**Remaining gaps (prioritized):**
- **P1:** llmstxt missing hlx_search_file and hlx_test_results — needs Ripley to update Program.cs raw string literal
- **P1:** `hlx_status` description should list `failureCategory` as a response field (completeness fix, not implementation disclosure)
- ~~**P1:** MCP [Description] attributes should flag local enhancements~~ — **Resolved 2026-02-27:** Dallas decided MCP descriptions describe what/inputs/outputs, not implementation details. See "MCP tool descriptions should expose behavioral contracts" decision below.
- **P3:** Failure categorization heuristic details (exit code→ category mapping) not yet documented

### 2025-07-23: MCP tool descriptions should expose behavioral contracts, not implementation mechanics

- Tool descriptions should tell agents what the tool does, what inputs it accepts, and what it returns — not whether the implementation caches, parses locally, or streams.
- Implementation details belong in README or deeper docs unless they change invocation behavior (for example, a new parameter or observable contract).

**Why:**

The question is: does knowing the implementation mechanism change an LLM agent's decision-making? The answer is no for almost every case, and the few exceptions are already handled by describing *behavior*, not *mechanism*.

**Analysis by category:**

1. **Caching ("results are cached")** — An agent should never care whether results are cached. It doesn't change the call signature, the return shape, or when to call the tool. If caching affected correctness (stale data), the right fix would be a `noCache` parameter, not a description warning. Currently our cache is transparent and correct. Verdict: **implementation detail, omit.**

2. **TRX parsing ("parses TRX locally")** — The agent cares that `hlx_test_results` returns *structured test results with names, outcomes, durations, and error messages*. That's already in the description. Whether we parse XML locally or call a hypothetical Helix "parse TRX" API is irrelevant to the consumer. The description already says what you get. Verdict: **implementation detail, omit.**

3. **Remote search ("searches without downloading")** — This is the closest case to being behavior-relevant, because it implies "this is fast and doesn't leave files on disk." But the agent doesn't manage disk space or care about temp files. The behavioral contract is already correct: `hlx_search_file` says "Returns matching lines with optional context" and `hlx_search_log` says "Returns matching lines with optional context." That tells the agent exactly what it gets. Verdict: **implementation detail, omit.**

4. **Failure classification ("classifies failures locally")** — The agent cares that `hlx_status` returns `failureCategory` in the response. It doesn't care whether we computed that classification or the Helix API returned it. The current description says "failed items (with exit codes, state, duration, machine)" — this should include `failureCategory` in the description since it's a field in the response. But that's a *completeness* fix, not an "implementation detail" disclosure. Verdict: **fix the field list, don't mention local processing.**

5. **URL resolution ("accepts full Helix URLs")** — This IS behavior-relevant and IS already in tool descriptions. Every tool says "Helix job ID (GUID), Helix job URL, or full work item URL." This tells the agent what inputs are accepted. Correct and sufficient.

**The principle:** Tool descriptions are an API contract for *consumers*. They answer three questions:
- What does this tool do? (purpose)
- What inputs does it accept? (parameters)  
- What does it return? (output shape)

They do NOT answer:
- How does this tool work internally? (implementation)
- What makes this tool fast/efficient? (optimization)
- Where does processing happen? (architecture)

**The README vs. Description distinction:** The README is for *humans evaluating whether to adopt hlx*. They care about value-adds, architecture, and implementation quality. Tool descriptions are for *LLM agents selecting and invoking tools at runtime*. They care about capabilities, parameters, and return shapes. These are different audiences with different information needs.

**One exception — behavioral implications:** If an implementation detail creates a behavioral contract the agent must respect, it belongs in the description. Example: if we added a `noCache` parameter, the description should say "Bypass cache and fetch fresh data" because that changes invocation behavior. But "this is cached" with no opt-out is invisible to the consumer.

**Action items:**
- Do NOT modify any existing `[Description]` attributes to add implementation details
- DO ensure descriptions accurately list all response fields (the `failureCategory` omission in `hlx_status` is a minor gap)
- The README's "How hlx Enhances the Helix API" section is the correct home for implementation detail documentation
- This principle applies to all future MCP tools added to the project

### 2025-07-24: UseStructuredContent refactor — APPROVED with one naming issue noted

- Structured MCP tools should return typed objects with `UseStructuredContent = true`; `hlx_logs` remains raw text because its value is the plain console output.
- Use `McpException` for tool-surface failures and keep JSON property names/wire format stable even when internal C# type names change.

**Why:**
1. The MCP SDK 1.0.0 `UseStructuredContent` feature generates JSON output schemas automatically, which improves tool discovery for LLM consumers. Typed returns are also more maintainable — no more manual `JsonSerializer.Serialize` calls with shared `JsonSerializerOptions`.
2. `hlx_logs` is the correct exception — it returns raw text content (console logs), not structured data. Forcing it through structured content would add unnecessary wrapping.
3. The `FileInfo_` type name (trailing underscore to avoid collision with `System.IO.FileInfo`) is an acceptable pragmatic choice — it's not part of the public MCP wire format (only the `[JsonPropertyName]` matters for serialization), and consumers never see the C# type name. A rename to `HelixFileInfo` would be cleaner but is not blocking.
4. All `[JsonPropertyName]` attributes use camelCase, matching the previous manual serialization output. No breaking wire-format changes.
5. Error handling correctly uses `McpException` for tool-level errors (missing work item, no matching files, binary file) and `ArgumentException` for invalid parameters (bad filter value). This matches MCP SDK conventions.

### 2026-03-01: Release version checklist

The publish workflow (`publish.yml`) validates all three match the git tag. Missing any one will fail the release.
**Why:** v0.2.0 release required a force-push to fix because `server.json` wasn't updated alongside the csproj. The workflow caught it, but we should get it right the first time.

### 2026-03-03: Default CLI behavior based on terminal context

**By:** Ripley (Backend Dev)
**What:** Use `Console.IsInputRedirected` to auto-detect context: interactive terminal defaults to `["--help"]`, redirected stdin defaults to `["mcp"]`. Previously, running `hlx` with no arguments in a terminal would hang waiting for JSON-RPC input.
**Why:** `Console.IsInputRedirected` is a reliable .NET API — standard idiom for CLI tools that need different behavior in interactive vs. non-interactive contexts. No additional dependencies or platform-specific code required.
- Every release must update all three version sources together: `HelixTool.csproj`, `.mcp/server.json` top-level `version`, and `packages[0].version`.
- The publish workflow validates these against the git tag, so partial bumps will fail release automation.

### 2026-03-07: Helix auth UX — hlx login architecture (consolidated)

**By:** Ash (analysis), Dallas (architecture)
**Date:** 2026-03-03 (analysis), 2026-03-03 (architecture), consolidated 2026-03-07
**Status:** Approved — Phase 1 ready for implementation

**What:**
- Helix API does **not** accept Entra JWT tokens — server-side limitation. Opaque tokens only via `Authorization: token <TOKEN>`.
- Phase 1 approved: `hlx login` + `git credential` storage + token resolution chain.
- Three new CLI commands: `hlx login` (browser open + token paste + validation), `hlx logout` (remove stored credential), `hlx auth status` (report source + test API).
- Credential storage via `git credential` (Option A): zero new dependencies, cross-platform, proven pattern (`darc` precedent).
- Token resolution chain (CLI): env var → stored credential → null. Env var wins for backward compatibility and CI/CD override.
- New classes: `ICredentialStore`, `GitCredentialStore` (in Core), `ChainedHelixTokenAccessor` (in Core).
- HTTP MCP fallback: Authorization header → env var → stored credential.
- 7 work items defined (WI-1: ICredentialStore, WI-2: ChainedHelixTokenAccessor, WI-3: DI wiring, WI-4: hlx login, WI-5: hlx logout, WI-6: hlx auth status, WI-7: error message update).
- Error messages in HelixService.cs updated from "Set HELIX_ACCESS_TOKEN..." to "Run 'hlx login' to authenticate, or set HELIX_ACCESS_TOKEN."

**Why:**
- Manual token generation + env var setup is the biggest UX friction point. Users must navigate to helix.dot.net → Profile → generate token → export env var in every shell.
- `git credential` is battle-tested, cross-platform, and already familiar to the user base. Zero new dependencies.
- Env var priority over stored credential preserves backward compatibility and enables CI/CD overrides.
- Entra auth deferred to Phase 3 (blocked on Helix server adding JWT Bearer support).

### 2026-03-07: Test result file discovery and xUnit XML support (consolidated)

**By:** Ripley
**Date:** 2025-07-24 (xUnit XML), 2026-03-07 (file patterns), consolidated 2026-03-07
**Status:** Implemented

**What:**
- `ParseTrxResultsAsync` auto-discovers test result files using priority-ordered `TestResultFilePatterns` array: `*.trx`, `testResults.xml`, `*.testResults.xml.txt`, `testResults.xml.txt`.
- xUnit XML format (`<assemblies>/<assembly>/<collection>/<test>`) supported alongside TRX. Format auto-detected via `DetectTestFileFormat`.
- Strict parsing (XXE-safe, `DtdProcessing.Prohibit`) for `.trx`; best-effort for `.xml` fallback.
- `IsTestResultFile()` is the canonical check used by CLI, MCP, and service code.
- Single file list query reduces API calls from 2-3 to 1.
- HelixException must be caught and rethrown as McpException in MCP tool handlers. MCP SDK only surfaces McpException messages to clients; other exceptions get wrapped as generic errors (root cause of issue #4).

**Why:**
- Runtime CoreCLR tests upload `{name}.testResults.xml.txt` to regular files (not testResults category); iOS/XHarness tests upload `testResults.xml`. Previous code only searched `*.trx` then `*.xml`.
- ASP.NET Core projects use `--logger xunit` producing `TestResults.xml` in xUnit format, not `.trx`. Without fallback, `hlx_test_results` failed on those work items.

### 2026-03-07: AzDO pipeline support — architecture and foundation (consolidated)

**By:** Dallas (architecture), Ripley (implementation)
**Date:** 2026-03-07
**Status:** Foundation IMPLEMENTED, full architecture DRAFT

**What:**
- Add AzDO pipeline wrapping to helix.mcp, following established Helix patterns. Enables ci-analysis skill to query AzDO builds, timelines, logs, and test results.
- Architecture mirrors Helix: `IAzdoApiClient` → `CachingAzdoApiClient` (decorator) → `AzdoService` → `AzdoMcpTools`.
- Code lives in `src/HelixTool.Core/AzDO/` — no separate project. AzDO and Helix data are tightly coupled (Helix jobs spawned by AzDO builds).
- Auth uses Azure Identity (`AzureDeveloperCliCredential`), keeping Helix PAT auth separate.
- `IAzdoTokenAccessor.GetAccessTokenAsync()` returns `Task<string?>` (async, unlike Helix's sync accessor). `az CLI` subprocess call is inherently async.
- Token caching is session-scoped — cached after first `az` call, not refreshed mid-session.
- `AzdoIdResolver` parses AzDO URLs to extract org/project/buildId. `TryResolve()` wraps `Resolve()` via try/catch (single code path for correctness).
- `AzdoBuildFilter` is client-side only (no JSON serialization attributes).
- `AzdoTimelineAttempt` record included for `previousAttempts` on timeline records (needed for retried job detection).
- Phase 1 scope: 7 core operations (get build, list builds, get timeline, get build log, get build changes, get test runs/results, URL parsing).
- Foundation files created: `AzdoModels.cs`, `IAzdoTokenAccessor.cs`, `AzdoIdResolver.cs`.

**Why:**
- Eliminates need for a separate AzDO MCP server. CI investigation inherently spans both Helix and AzDO.
- Shared cache infrastructure (`ICacheStore`, `SqliteCacheStore`) reused.
- Separate project adds complexity for zero benefit at current scale.
- Async token accessor avoids blocking with `.GetAwaiter().GetResult()` when callers are already async.

### 2026-03-07: Future direction — AzDO pipeline wrapping

**By:** Larry Ewing (via Copilot)
**What:** After issue #4 is wrapped up, explore wrapping Azure DevOps pipelines as MCP tools, similar to how we wrap Helix today.
**Why:** User request — captured for team discussion after current work completes.

### 2026-03-07: AzDO test patterns and conventions (consolidated)

**By:** Lambert, Dallas
**Updated:** 2026-03-08 — merged security testing conventions

#### Test file locations
- `src/HelixTool.Tests/AzDO/AzdoIdResolverTests.cs` — 31 tests for URL parsing
- `src/HelixTool.Tests/AzDO/AzdoTokenAccessorTests.cs` — 10 tests for auth chain
- `src/HelixTool.Tests/AzDO/AzdoSecurityTests.cs` — 63 security-focused tests
- Namespace: `HelixTool.Tests.AzDO` (matches Core's `HelixTool.Core.AzDO`)

#### General conventions
- **Resolve() vs TryResolve():** AzdoIdResolver exposes both. Test Resolve() for throw behavior, TryResolve() for bool-return path. Both use same internal logic.
- **Env var isolation:** `AzdoTokenAccessorTests` implements `IDisposable` to save/restore `AZDO_TOKEN` env var. Critical for parallel xUnit — env vars are process-global. Use try/finally blocks.
- **Az CLI timeout:** Tests that fall through to az CLI take ~1s. Future: consider `[Trait("Category", "Slow")]` if test suite grows.

#### Security test categories
- **URL input validation** — malicious URLs, path traversal, SSRF vectors, injection
- **Command injection** — shell safety, env var passthrough, process construction
- **Request construction** — SSRF prevention, token leakage, header injection
- **Cache isolation** — key namespacing, org/project separation, poisoning resistance
- **End-to-end** — AzdoService rejects bad input before any API call

#### Security test patterns
- **Token leakage assertion:** `Assert.DoesNotContain("token-value", ex.Message)` on all error paths (401/403/500)
- **SSRF prevention:** `Assert.StartsWith("https://dev.azure.com/", url)` and `Assert.Equal("dev.azure.com", uri.Host)`
- **Cache key isolation:** Verify `SetMetadataAsync` called with different keys for different org/project
- **Credential leakage:** `Assert.DoesNotContain("user"/"pass", org/project)` for URLs with embedded credentials
- **Rejection is safe:** Use `TryResolve` for inputs where rejection or safe parsing are both acceptable
- **No-API-call guard:** After throwing on bad input, verify `DidNotReceive()` on mock client
- **CapturingHttpHandler:** Each security test class defines a local `CapturingHttpHandler` (same pattern as `FakeHttpMessageHandler`). Avoids coupling test classes.

#### Edge cases identified
1. **Negative/zero buildIds are accepted** — `int.TryParse` succeeds for `-5` and `0`. No positivity validation in `AzdoIdResolver`. Recommend adding `buildId > 0` check.
2. **TryResolve out-param defaults are not null** — they're `DefaultOrg`/`DefaultProject`, unlike typical `TryParse` patterns. Callers should check the return value, not the out params.
3. **_resolved flag not thread-safe** — concurrent first calls to `AzCliAzdoTokenAccessor` without env var may spawn multiple `az` processes. Benign but wasteful. Consider `SemaphoreSlim` if this matters.
4. **Env var read on every call** — `AZDO_TOKEN` is not cached. This is intentional (allows runtime token rotation) but means env var tests must carefully set/unset between assertions.
5. **Duplicate query params:** `HttpUtility.ParseQueryString` concatenates with commas → `int.TryParse` rejects safely
6. **Embedded credentials in URLs:** `Uri` parses `user:pass@` as UserInfo; resolver only reads `Host`/`Path` — safe
7. **Path traversal in org/project:** `Uri` normalizes `../../` away; `CacheSecurity.SanitizeCacheKeySegment` strips remaining
8. **Int overflow buildId:** `long.MaxValue` as buildId fails `int.TryParse` → `ArgumentException`
9. **Newlines in token env var:** Returned verbatim — potential header injection if token used unsafely (mitigated by `AuthenticationHeaderValue`)

#### Testability concerns for future AzDO work
- `AzdoApiClient` will need `HttpMessageHandler` injection for mocking (same pattern as `HelixApiClient`)
- `AzdoService` should take `IAzdoApiClient` via constructor for NSubstitute mocking
- `AzdoMcpTools` can follow `HelixMcpToolsTests` pattern: mock service, test tool wrappers

### 2026-03-07: XXE prevention test regression after xUnit XML refactor

**By:** Lambert
**What:** `ParseTrx_RejectsXxeDtdDeclaration` test now fails after xUnit XML auto-discovery refactor. `XmlException` is swallowed by `TryParseTestFile`/`DetectTestFileFormat`, returning `HelixException("Found XML files but none were in a recognized format")` instead. DTD content is not processed (safe), but error message no longer indicates security rejection.
**Why:** Need to verify `DetectTestFileFormat` uses `DtdProcessing.Prohibit` and that the swallowed exception doesn't silently process DTD content. Test should be updated to assert `HelixException` not `XmlException`.

### 2026-03-08: Use Lazy<T> in CacheStoreFactory to prevent concurrent factory invocation

**By:** Ripley
**Status:** Implemented
**What:** Changed `ConcurrentDictionary<string, ICacheStore>` to `ConcurrentDictionary<string, Lazy<ICacheStore>>`. `Lazy<T>` (default `LazyThreadSafetyMode.ExecutionAndPublication`) guarantees factory runs exactly once per key. `Dispose()` checks `IsValueCreated` before accessing `.Value`.
**Why:** `ConcurrentDictionary.GetOrAdd(key, factory)` doesn't guarantee single-invocation of the factory. Under contention, multiple `SqliteCacheStore` constructors raced on `InitializeSchema()` for the same SQLite file, producing `ArgumentOutOfRangeException` from SQLitePCL on Windows CI. Standard .NET pattern — use `Lazy<T>` wrapping whenever `ConcurrentDictionary.GetOrAdd` factories have side effects.

### 2026-03-07: AzDO caching strategy

**By:** Ripley
**Status:** Implemented
**What:** CachingAzdoApiClient decorator for IAzdoApiClient. Cache keys use `azdo:` prefix. Dynamic TTL by build status: completed builds 4h, in-progress 15s, timelines never while running (4h completed), logs/changes 4h, build lists 30s, test runs/results 1h. No DTO layer needed — AzDO model types are `sealed record` with `[JsonPropertyName]`, directly serializable. Reuses `ICacheStore.IsJobCompletedAsync` with composite keys.
**Why:** Follows CachingHelixApiClient pattern. Dynamic TTL prevents stale data for in-progress builds while minimizing API calls for stable data.

### 2026-03-07: AzdoService method signatures

**By:** Ripley
**Status:** Implemented
**What:** AzdoService business logic layer — all `buildIdOrUrl` params resolve via `AzdoIdResolver.Resolve()`. `GetBuildSummaryAsync` returns flattened `AzdoBuildSummary` with computed `Duration` and `WebUrl`. `GetBuildLogAsync` has `int? tailLines` for server-side slicing. `ListBuildsAsync` takes raw org/project (no URL resolution). No exception wrapping yet — `HttpRequestException` propagates; will add `AzdoException` when MCP tools need it.
**Why:** Mirrors HelixService pattern. URL resolution at service layer simplifies MCP tool implementations.

### 2026-03-07: AzdoMcpTools — return model types directly

**By:** Ripley
**What:** AzdoMcpTools returns AzDO model types directly instead of creating separate MCP result wrappers. API model types already have `[JsonPropertyName]` attributes. `azdo_log` returns plain `string` (no UseStructuredContent) matching `hlx_logs` pattern.
**Why:** Avoids duplicating DTOs that already have correct JSON serialization. If reshaping is needed later, add wrapper types then.
**Impact:** Lambert — test against `[JsonPropertyName]` names (camelCase). Kane — 7 new MCP tools need docs. Dallas — wrapper types deferred.

### 2026-03-08: AzDO Security Review Findings

**By:** Dallas
**What:** Security review of AzDO integration code (8 files)
**Why:** Pre-merge security gate for PR #6

---

## Findings

#### SEC-1 — Query Parameter Injection via `prNumber`
- **Severity:** Medium
- **Title:** Unescaped `prNumber` allows query parameter injection into AzDO API calls
- **Location:** `AzdoApiClient.cs`, `ListBuildsAsync`, line 41
- **Description:** The `prNumber` field is interpolated directly into the query string without `Uri.EscapeDataString`:
  ```csharp
  queryParams.Add($"branchName=refs/pull/{filter.PrNumber}/merge");
  ```
  Compare with `branch` (line 43) and `statusFilter` (line 49), which ARE properly escaped. A `prNumber` value like `"123&$top=99999"` would inject an additional query parameter into the AzDO API URL, potentially altering results.
- **Recommendation:** Either validate `prNumber` as an integer (`int.TryParse`) or apply `Uri.EscapeDataString`. Integer validation is preferred since PR numbers are always integers — this also prevents semantic abuse.
  ```csharp
  if (!string.IsNullOrEmpty(filter.PrNumber))
  {
      if (!int.TryParse(filter.PrNumber, out var prNum))
          throw new ArgumentException("prNumber must be a valid integer.", nameof(filter));
      queryParams.Add($"branchName=refs/pull/{prNum}/merge");
  }
  ```
- **Exploit scenario:** An MCP client sends `prNumber: "123&definitions=999"` to the `azdo_builds` tool. The injected `definitions` parameter overrides or supplements the intended query, returning builds from a different pipeline than expected. Impact is limited to data integrity (results are still from `dev.azure.com`, not SSRF).

---

#### SEC-2 — HttpClient Created Without IHttpClientFactory
- **Severity:** Low
- **Title:** Raw `new HttpClient()` risks socket exhaustion under load
- **Location:** `HelixTool.Mcp/Program.cs`, line 57; `HelixTool/Program.cs`, lines 44, 616
- **Description:** Both DI registrations create `new HttpClient()` directly instead of using `IHttpClientFactory`. In the HTTP/MCP server where many scoped requests may be created, each scoped `AzdoApiClient` gets a new `HttpClient` instance. While not a direct security vulnerability, socket exhaustion under sustained load can cause denial of service via `SocketException`.
- **Recommendation:** Register a named `HttpClient` via `builder.Services.AddHttpClient<AzdoApiClient>()` or inject `IHttpClientFactory`. This also centralizes timeout and handler configuration.

---

#### SEC-3 — Unbounded Response Size on Log Retrieval
- **Severity:** Low
- **Title:** `GetBuildLogAsync` reads entire log into memory without size limits
- **Location:** `AzdoApiClient.cs`, `GetBuildLogAsync`, line 78
- **Description:** Build logs are read as a full string via `response.Content.ReadAsStringAsync`. AzDO build logs can be tens of megabytes. In multi-client HTTP mode, several concurrent log requests could exhaust server memory. The Helix side has the same pattern but it pre-dates multi-client mode.
- **Recommendation:** Add a configurable max response size (e.g., 10 MB) or stream logs with a size cutoff. The `tailLines` parameter in `AzdoService.GetBuildLogAsync` mitigates this at the service layer but only AFTER the full content is already in memory.

---

#### SEC-4 — No Configurable Timeout on AzDO HttpClient
- **Severity:** Low
- **Title:** Default 100s timeout may be too generous for CI tool use case
- **Location:** `HelixTool.Mcp/Program.cs`, line 57 (`new HttpClient()`)
- **Description:** The HttpClient uses the default 100-second timeout. A slow or unresponsive AzDO API could tie up server threads for extended periods. In multi-client HTTP mode, this could exhaust the thread pool.
- **Recommendation:** Set an explicit timeout (e.g., 30s) appropriate for the AzDO API call patterns. Can be centralized if SEC-2's `IHttpClientFactory` recommendation is adopted.

---

#### SEC-5 — AzCliAzdoTokenAccessor Not Thread-Safe
- **Severity:** Info
- **Title:** Race condition on `_resolved`/`_cachedToken` in singleton accessor
- **Location:** `IAzdoTokenAccessor.cs`, `AzCliAzdoTokenAccessor`, lines 23–37
- **Description:** `_cachedToken` and `_resolved` are not protected by a lock or `Lazy<T>`. In HTTP mode where the accessor is singleton and multiple requests arrive concurrently on startup, `TryGetAzCliTokenAsync` could execute multiple times. Not exploitable — string references are atomic in .NET, and double-execution just wastes a process spawn. However, it deviates from the `Lazy<T>` pattern established for `CacheStoreFactory`.
- **Recommendation:** Use `SemaphoreSlim` or `Lazy<Task<string?>>` for one-shot initialization, consistent with the project's existing patterns.

---

#### SEC-6 — AzDO CLI Token Never Refreshed After Initial Resolution
- **Severity:** Info
- **Title:** Singleton token accessor caches az CLI token indefinitely
- **Location:** `IAzdoTokenAccessor.cs`, `AzCliAzdoTokenAccessor`, line 32–33
- **Description:** Once `_resolved = true`, the cached token is returned forever. Azure CLI tokens (Entra ID JWT) typically expire after ~1 hour. For long-running MCP servers, the server will start returning 401 errors after token expiry. Not a security vulnerability (fails closed), but an operational concern for availability.
- **Recommendation:** Either track token expiry and re-fetch, or document that long-running servers should use `AZDO_TOKEN` env var with externally managed rotation.

---

## Areas Reviewed — No Issues Found

#### ✅ Command Injection (`AzCliAzdoTokenAccessor`)
The `az account get-access-token` command uses only hardcoded constants (`AzdoResourceId`). No user-controlled input flows into `ProcessStartInfo.Arguments`. `UseShellExecute = false` prevents shell metacharacter interpretation. **Safe.**

#### ✅ SSRF (`AzdoApiClient.BuildUrl`)
All HTTP requests are constructed via `BuildUrl` which hardcodes `https://dev.azure.com/` as the base URL. `org` and `project` parameters are escaped with `Uri.EscapeDataString`, preventing authority override (`@`), path traversal (`../`), or fragment injection (`#`). Even raw MCP parameters (in `azdo_builds` tool) cannot redirect requests to a non-AzDO host. **Safe.**

#### ✅ SSRF (`AzdoIdResolver`)
The resolver validates the host is either `dev.azure.com` or `*.visualstudio.com` and throws `ArgumentException` for all other hosts. Extracted org/project values are then used with `BuildUrl` (which hardcodes the target host). URL parsing uses `Uri.TryCreate` — no regex, no ReDoS risk. **Safe.**

#### ✅ Token Leakage
- Tokens are never logged or included in error messages. `ThrowOnAuthFailure` says "Set AZDO_TOKEN or run 'az login'" without echoing the token.
- `ThrowOnUnexpectedError` includes a 500-char snippet of the AzDO error response body — this is API error text, not credentials.
- Cache stores serialized response data, not tokens. Cache keys include org/project but not token material.
- `AzCliAzdoTokenAccessor` catches all exceptions silently (returns `null`) — no stack traces that might reveal token fragments.
**Safe.**

#### ✅ Cache Isolation (Multi-User HTTP Mode)
- `IAzdoTokenAccessor` is singleton (shared server credentials) — correct for server-side AzDO auth.
- `ICacheStore` is scoped by Helix token hash via `CacheOptions.AuthTokenHash` → separate SQLite databases per user.
- `CachingAzdoApiClient` cache keys use `CacheSecurity.SanitizeCacheKeySegment()` for org/project — path traversal and key delimiter injection are prevented.
- AzDO data cached under one user's Helix token hash cannot be accessed by another user.
**Safe.**

#### ✅ TLS Enforcement
`BuildUrl` hardcodes `https://` scheme. `AzdoIdResolver` accepts `http://` URLs for parsing only — the actual API request always uses the HTTPS URL from `BuildUrl`. Default `HttpClient` validates TLS certificates (no custom `HttpClientHandler`). **Safe.**

#### ✅ Input Validation (MCP Parameters)
- `buildId` parameters pass through `AzdoIdResolver.Resolve()` which validates format (integer or recognized AzDO URL).
- Integer parameters (`logId`, `runId`, `top`, `definitionId`) are type-safe at the MCP schema level.
- `branch` and `statusFilter` are properly `Uri.EscapeDataString`-escaped.
- Exception: `prNumber` — see SEC-1.

#### ✅ Consistency with Existing Helix Patterns
- Cache key sanitization reuses `CacheSecurity.SanitizeCacheKeySegment()` from the Helix caching code.
- URL construction uses `Uri.EscapeDataString`, consistent with Helix URL handling.
- Error handling follows the established `ThrowOnAuthFailure`/`ThrowOnUnexpectedError` pattern.
- The decorator caching pattern mirrors `CachingHelixApiClient`.

---

## Verdict

**PR #6 is safe to merge with one recommended fix (SEC-1).**

- **SEC-1 (Medium)** is the only finding that warrants a code change before merge. The fix is a one-line `int.TryParse` validation — minimal risk, high value. Without it, an MCP client can inject arbitrary query parameters into AzDO API calls, which is a violation of the principle of least surprise even though the blast radius is limited to `dev.azure.com`.

- **SEC-2/3/4 (Low)** are real but non-blocking improvements that can be addressed in a follow-up. They affect availability under load, not confidentiality or integrity.

- **SEC-5/6 (Info)** are correctness and operational concerns, not security vulnerabilities. Document as known limitations.

**Conditional approval: merge after fixing SEC-1.** The remaining findings should be tracked as follow-up work items.

### 2026-03-08: AzDO MCP Tool Context-Limiting Defaults

**By:** Ripley
**Status:** Implemented

**What:** Added safe output-size defaults to all AzDO MCP tools, matching Helix tool patterns:

| Tool | Parameter | Default | Rationale |
|------|-----------|---------|-----------|
| `azdo_log` | `tailLines` | `500` | Matches `hlx_logs`; logs can be 100MB+ |
| `azdo_timeline` | `filter` | `"failed"` | New param; non-succeeded + parent chain |
| `azdo_changes` | `top` | `20` | Reasonable commit history window |
| `azdo_test_runs` | `top` | `50` | Enough for most builds |
| `azdo_test_results` | `top` | `200` | Matches `hlx_test_results`; was hardcoded 1000 |

**Why:**
- Unbounded outputs exhaust agent context windows
- All parameters remain nullable/overridable for callers needing more data
- Defaults live in MCP tool method signatures (not service code)
- Cache keys include limit parameters to prevent stale partial results
- Timeline filtering is client-side (AzDO API has no timeline filter support): identifies non-succeeded records + walks parentId chain for hierarchical context

### 2026-03-08: User directive — AzDO artifacts must follow Helix patterns

**By:** Larry Ewing (via Copilot)
**What:** AzDO artifact and attachment tools must follow the same caching and search patterns as the Helix tools (hlx_files, hlx_find_files, hlx_search_file, hlx_download)
**Why:** User request — captured for team memory. Consistency between Helix and AzDO tool behavior is important for agent usability.

### 2026-03-08: AzDO Artifact/Attachment Test Patterns

**By:** Lambert
**Status:** Informational

**What:** 33 tests added for `azdo_artifacts` and `azdo_test_attachments` MCP tools. Patterns established:
1. CamelCase JSON assertions: use `GetProperty("name").GetString()` to avoid xUnit2002 on `JsonElement` (value type).
2. TestAttachments top limiting happens in service layer via `Take(top)` — AzDO API doesn't support server-side limiting for attachments.
3. Artifact caching uses `ImmutableTtl` (4h). Attachment caching uses `TestTtl` (1h).

**Why:** Documents test conventions and caching decisions for AzDO artifact tools. Total test count: 700.

### 2026-03-08: AzDO documentation uses subsections within existing README structure

**By:** Kane
**What:** AzDO tools are documented as a `### AzDO Tools` subsection under `## MCP Tools`, AzDO auth as `### Azure DevOps` under `## Authentication`, and AzDO TTLs as inline additions under `## Caching` — rather than creating separate top-level sections.
**Why:** Keeps the README scannable and reinforces that Helix and AzDO are parts of the same tool. The MCP Configuration section needed no changes because the same MCP server serves both tool sets. This pattern should be followed for any future API domains added to hlx.

### 2026-03-08: llmstxt updated with AzDO tools under a separate "AzDO MCP Tools" subsection

**By:** Kane
**What:** The `llmstxt` command output now includes all 9 AzDO MCP tools in a dedicated subsection, plus AzDO auth chain and AzDO-specific caching TTLs.
**Why:** LLM agents reading `llmstxt` need to know about AzDO tools to use them. Keeping Helix and AzDO tool lists visually separated makes it clear which tools work with which system.

### 2026-03-08: IHttpClientFactory with named clients for HTTP lifecycle management

**By:** Ripley
**What:** Replaced static `HttpClient` in HelixService and `new HttpClient()` in AzdoApiClient DI with IHttpClientFactory named clients ("HelixDownload" and "AzDO"), both configured with 5-minute timeout.
**Why:** Static HttpClient causes socket exhaustion and blocks DNS refresh. IHttpClientFactory manages handler lifecycle automatically. Named clients allow different timeout/config per use case. The optional `HttpClient?` constructor parameter on HelixService preserves backward compatibility with 17 test files.

### 2026-03-08: HttpCompletionOption.ResponseHeadersRead for all AzDO HTTP requests

**By:** Ripley
**What:** All AzDO API client methods (GetAsync, GetListAsync, GetBuildLogAsync) now use ResponseHeadersRead instead of default ResponseContentRead.
**Why:** Prevents buffering entire response bodies in memory before processing. Especially important for large build logs. HelixService.DownloadFromUrlAsync already used this pattern — now consistent across both backends.

### 2026-03-08: AzDO CLI commands mirror MCP tools 1:1

**By:** Ripley
**What:** Added 9 `azdo-*` CLI subcommands in a separate `AzdoCommands` class, each calling the same `AzdoService` methods as the MCP tools.
**Why:** Users without MCP clients can now use all AzDO functionality directly from the command line. Follows the existing Helix pattern (Commands class with ConsoleAppFramework attributes). Timeline filtering logic is duplicated from MCP tools rather than extracted to a shared method — flagging this for Dallas review on whether to extract.

### 2026-03-08: Proactive Test Patterns for SEC-2/3/4

**By:** Lambert
**Date:** 2026-03-08

#### Context
Ripley is working on SEC-2 (IHttpClientFactory), SEC-3 (streaming), SEC-4 (timeout config), and AzDO CLI subcommands in parallel. Tests written proactively to validate expected behavior.

#### Test Files Created
- `src/HelixTool.Tests/HttpClientConfigurationTests.cs` — 13 tests
- `src/HelixTool.Tests/StreamingBehaviorTests.cs` — 18 tests
- `src/HelixTool.Tests/AzDO/AzdoCliCommandTests.cs` — 22 tests

#### Key Patterns Established

### Timeout vs. Cancellation Testing

- Timeout: `TaskCanceledException` without cancelled token → wraps in `HelixException`
- Cancellation: `TaskCanceledException` with cancelled token → rethrows directly
- Use `DelayingHttpMessageHandler` to simulate real timeouts

### NSubstitute for Stream-returning methods

- Cannot use `.ThrowsAsync()` on `Task<Stream>` — use `.Returns<Stream>(_ => throw ...)` instead

### Init-only Properties in Test Helpers

- Models with `init;` setters need all values in object initializer
- Create helper methods with optional parameters: `CreateBuild(id, status, result, startTime?, finishTime?)`

#### What May Need Updating After Ripley's Changes
- **SEC-2:** If IHttpClientFactory is used, add tests for named client configuration
- **SEC-3:** If streaming replaces read-all-to-string, StreamingBehaviorTests may need refactoring
- **SEC-4:** If explicit timeout is configured in DI, add a test asserting the configured value
- **CLI:** If AzDO CLI commands are added as a new Commands class, add registration and argument parsing tests

### 2025-07-18: Promoted IsFileSearchDisabled to public visibility

- `HelixService.IsFileSearchDisabled` became public so the extracted `HelixTool.Mcp.Tools` assembly could reuse the same guard without new friend-assembly coupling.
- Shared static helpers that cross assembly boundaries should either move to a neutral utility type or become intentionally public.

### 2026-03-08: AzDO Search/Filter Gap Analysis (consolidated)

**By:** Ash
**Status:** P0 (`azdo_search_log`) implemented in PR #10. See "CI-analysis skill usage patterns" below for detailed analysis.

**Summary:** Gap analysis identified `azdo_search_log` (P0), test results name filter (P1), and timeline name filter (P2) as missing AzDO search capabilities. `SearchLines()` extraction to `TextSearchHelper` recommended and implemented. Superseded by the CI-analysis skill usage study which validated priorities from real agent usage patterns.

### 2026-03-08: CI-analysis skill usage patterns and AzDO search recommendations

**By:** Ash

**What:** Deep analysis of how the ci-analysis skill (in `lewing/agent-plugins` and `blazor-playground/copilot-skills`) uses AzDO and Helix tools in practice, with updated recommendations for AzDO search/filter tools based on real usage patterns.

**Why:** The prior AzDO search gap analysis (ash-azdo-search-gaps.md) was based on API surface review. This analysis examines how agents *actually* use these tools during CI failure investigation, revealing specific pain points and new tool ideas that weren't visible from the API alone.

---

## 1. How the CI-Analysis Skill Currently Works

### Tool Call Flow (happy path)

1. **Step 0 — Gather PR context**: GitHub MCP (`pull_request_read`, `list_commits`, `get_file_contents`) to classify PR type (code/flow/backport/merge), read labels, check existing comments
2. **Step 1 — Run Get-CIStatus.ps1**: PowerShell script that queries AzDO timeline, extracts Helix job IDs from build logs, fetches Helix console logs, and produces a `[CI_ANALYSIS_SUMMARY]` JSON
3. **Step 1b — Supplement with MCP**: When script output is insufficient, use `ado-dnceng-public` MCP tools (`get_builds`, `get_build_log`, `get_build_log_by_id`) for additional data
4. **Step 2 — Analyze**: Cross-reference `failedJobDetails` with `knownIssues`, correlate with PR changes, check build progression for multi-commit PRs
5. **Step 3 — Deep dive (if needed)**: Helix MCP tools (`hlx_search_log`, `hlx_search_file`, `hlx_status`, `hlx_test_results`, `hlx_logs`) for individual work item investigation
6. **Step 4 — Binlog analysis (if needed)**: `hlx_download` + `mcp-binlog-tool` for MSBuild-level diagnosis

### Key Design Decisions

- **Script does heavy lifting**: The PowerShell script handles AzDO auth, timeline parsing, Helix URL extraction, and error categorization. Agents are instructed to parse its output first, not re-query.
- **MCP tools as supplements, not primaries for initial scan**: The script can access AzDO and Helix via REST APIs independently. MCP tools fill gaps the script can't cover.
- **Helix search is remote-first**: `hlx_search_log` and `hlx_search_file` are preferred over `hlx_logs` (full download). This was a trained behavior — agents initially defaulted to download-first until explicit "prefer remote search" guidance was added.

### Service Access Stack

| Service | Primary | Fallback |
|---------|---------|----------|
| AzDO builds/timeline | `ado-dnceng-public-*` MCP tools | Script via `Invoke-RestMethod` |
| AzDO build logs | `ado-dnceng-public-pipelines_get_build_log_by_id` | Script via `Invoke-RestMethod` |
| Helix job status | `hlx_status` | Script / `curl` |
| Helix work item errors | `hlx_search_log` (remote search) | `hlx_logs` (full download) |
| Helix artifacts | `hlx_search_file` / `hlx_files` | `hlx_download` |
| GitHub | GitHub MCP tools | `gh` CLI |

---

## 2. Specific Pain Points Where Search/Filter Capabilities Would Help

### Pain Point 1: AzDO Build Log Crawling (P0 — validates prior analysis)

**Evidence (training log session b563ef92):** Agent spent 7+ tool calls crawling AzDO build logs after the script had already provided the error. The agent guessed log IDs by file size instead of searching for error content. Even after training improvements (Change 1), the script's `Get-BuildLog` function still fetches *entire* log content (`Invoke-RestMethod` on the full log endpoint) and then does client-side pattern matching with `Select-String`.

**What happens today:**
- Script fetches full build log via REST API (no size limit, no server-side search)
- Script has `Extract-BuildErrors` function with ~11 regex patterns for common errors (CS/MSB/NU errors, linker errors, etc.)
- Agents sometimes need to look at logs the script didn't fetch (e.g., checkout logs for build progression, step logs for package restore errors)
- When agents use `ado-dnceng-public-pipelines_get_build_log_by_id`, they get raw text — may be 10K+ lines with no way to search

**What `azdo_search_log` would enable:**
- Agent asks "search log 5 of build 1283986 for 'Merge' pattern" → gets 3 matching lines with context (vs downloading 650+ lines of git output)
- Agent asks "search log 565 for 'error'" → gets error lines immediately (vs downloading entire step log)
- The delegation pattern for build progression (Pattern 5 in delegation-patterns.md) explicitly needs to search checkout logs for a merge line around line 500-650 — a search tool would replace the workaround of fetching with `startLine` guessing

### Pain Point 2: Helix Console Log Search Already Works — Parity Gap (P0)

**Evidence (training log Session 3):** After SkillResearcher found that `hlx_search_log` wasn't mentioned in the skill, training added it and achieved 5/5 compliance across all models. Agents now reliably use `hlx_search_log(pattern="error", contextLines=3)` as the first step for Helix investigation.

**The parity gap:** Helix has `hlx_search_log` (pattern search with context lines) and `hlx_search_file` (search uploaded artifacts). AzDO has neither. When the agent needs to search an AzDO build log, it must:
1. Fetch the entire log with `get_build_log_by_id`
2. Pipe through `Select-String` in PowerShell (burns context window with full log content)
3. Or use the script's `Extract-BuildErrors` (only works for patterns the script knows about)

An `azdo_search_log` tool matching `hlx_search_log`'s interface would let agents use the same mental model for both services.

### Pain Point 3: Build Log ID Discovery (P1 — NEW finding)

**Evidence:** The `manual-investigation.md` reference and `build-progression-analysis.md` both mention hardcoded log IDs (`logId: 5` for checkout). The script discovers log IDs by traversing the timeline to find Helix tasks. But when agents need to search a *specific* step's log (e.g., the "Restore" step, or "Send to Helix"), they have no way to find its log ID without fetching the full timeline and scanning for matching record names.

**What would help:** The existing `ado-dnceng-public-pipelines_get_build_log` tool already returns a list of all logs with metadata. But a filter parameter (e.g., `nameFilter` on the timeline/records endpoint) would let agents find the right log ID without processing the full timeline.

### Pain Point 4: AzDO Test Results Name Filtering (P1 — validates prior analysis)

**Evidence:** The skill's `sql-tracking.md` reference has agents creating `failed_jobs` tables to track individual failures. The script extracts `failedJobDetails` from AzDO test runs, but when agents need to search for a specific test name across multiple builds (e.g., for build progression analysis), they must iterate through each build's test results.

**What would help:** A `testNameFilter` on the test results MCP tool. The `build_failures` SQL table pattern (build-progression-analysis.md) queries `SELECT test_name, COUNT(DISTINCT build_id) as fail_count` — this currently requires fetching all failures from each build and inserting into SQL. A server-side name filter would reduce round trips.

### Pain Point 5: Delegation Context Budget (P1 — NEW finding)

**Evidence (training log, delegation patterns):** The skill defines 5 delegation patterns for subagents. Pattern 1 (scanning console logs) and Pattern 4 (parallel artifact extraction) both involve subagents that need to search through content. Subagents run in separate context windows — they can't share the script's output. Each subagent independently fetches logs/artifacts.

**Impact:** When the main agent delegates "search 5 work items for [FAIL] lines" to a subagent, that subagent currently must either:
- Use `hlx_search_log` for each work item (efficient — remote search)
- Fetch full logs with `hlx_logs` for each (context-expensive)

For AzDO build logs, there's no search equivalent. A subagent delegated to "extract target HEAD from build checkout log" must fetch the entire log.

---

## 3. Updated Recommendations for AzDO Search Tools

### P0: `azdo_search_log` — Search within AzDO build step logs

**Priority elevated from prior analysis.** Real skill usage confirms this is the #1 gap.

**Interface (matching `hlx_search_log` for consistency):**
```
azdo_search_log(
    buildId: int,         // AzDO build ID
    logId: int,           // Log ID (from build log list or timeline)
    pattern: string,      // Search pattern (case-insensitive)
    contextLines: int = 2,  // Lines before/after each match
    maxMatches: int = 50    // Limit results
)
```

**Implementation notes:**
- Reuse `SearchLines()` from `HelixService` (already identified in prior analysis)
- AzDO REST API has no server-side search — must fetch log content and search client-side
- Could use `startLine`/`endLine` on `get_build_log_by_id` to paginate large logs, but search is still client-side
- Consider caching fetched logs to avoid re-downloading on repeated searches

**Use cases from real skill patterns:**
1. Build progression: search checkout log for `"HEAD is now at"` merge line
2. Error diagnosis: search step log for `"error"` pattern when script didn't capture it
3. Package restore: search restore log for `"NU1102"` or `"Unable to find package"`
4. Helix job discovery: search "Send to Helix" log for `"Sent Helix Job"` GUIDs

### P1: `azdo_search_timeline` — Search timeline records by name

**New recommendation** (not in prior analysis).

**Interface:**
```
azdo_search_timeline(
    buildId: int,
    nameFilter: string,     // Substring match on record name
    typeFilter: string = null,  // "Job", "Task", "Stage"
    resultFilter: string = null // "failed", "succeeded", "canceled"
)
```

**Use cases:**
1. Finding the log ID for a specific step (e.g., "Checkout", "Restore", "Send to Helix")
2. Filtering to just failed jobs (already partially done — `hlx_status` does this for Helix)
3. Finding Helix-related tasks within a specific job

**Implementation notes:**
- The AzDO timeline API returns all records. Client-side filter is fine.
- This replaces the pattern of fetching full timeline → PowerShell `Where-Object` filtering

### P1: Test results name filter (unchanged from prior analysis)

Add `testNameFilter` parameter to the existing `azdo_test_results` tool.

### P2: Timeline name filter (merged into P1 `azdo_search_timeline` above)

---

## 4. NEW Tool Ideas from Studying the Skill

### NEW P1: `azdo_search_log_across_steps` — Multi-step log search *(superseded by 2026-03-09 full design spec below)*

Superseded. See `### 2026-03-09: azdo_search_log_across_steps design spec` for the full implementation design.

### NEW P2: `azdo_build_summary` — Structured build failure summary

**Evidence:** The script produces `[CI_ANALYSIS_SUMMARY]` JSON with structured failure data. When MCP tools are the primary access method (no script), agents must piece together build status from timeline + test results + log snippets manually.

**Interface:**
```
azdo_build_summary(
    buildId: int,
    includeHelixJobs: bool = false  // Extract Helix job IDs from logs
)
```

Returns: `{ result, failedJobs: [{name, result, logId, errorSnippet}], canceledJobs: [...] }`

This would replicate the script's core value proposition in a single MCP tool call.

### NEW P2: `azdo_log_error_extract` — Pre-built error extraction

**Evidence:** The script's `Extract-BuildErrors` function has 11 carefully crafted regex patterns for .NET build errors. This domain knowledge (CS errors, MSB errors, NU errors, linker errors, AzDO annotations) could be baked into a tool.

**Interface:**
```
azdo_log_error_extract(
    buildId: int,
    logId: int,
    contextLines: int = 5
)
```

Returns only matching error lines with context — no need for the agent to know patterns. This is a specialized version of `azdo_search_log` with built-in .NET error knowledge.

---

## 5. Cross-Cutting Observations

### The "Remote Search First" Pattern is Proven

Training Session 3 proved that when agents have search tools (`hlx_search_log`), they prefer them over download (`hlx_logs`) — 5/5 across all 3 model families after guidance. This validates the entire `azdo_search_log` approach: agents WILL use search tools if they exist, and it dramatically reduces context consumption.

### 🚨 Blockquote Rules Drive Universal Compliance

The training log validates that format matters more than content depth. Every `🚨` rule in SKILL.md achieved 3/3 model compliance. When we add `azdo_search_log`, the skill should get a `🚨` rule: "Prefer `azdo_search_log` over `get_build_log_by_id` for finding specific content in build logs."

### Script vs MCP Tools Tension

The skill relies on a PowerShell script (Get-CIStatus.ps1, ~2000 lines) for initial data gathering, with MCP tools as supplements. As MCP tools gain search/filter capabilities, the script becomes less necessary. The long-term trajectory is: script handles orchestration logic (multi-step workflow), MCP tools handle individual data access. `azdo_build_summary` (P2) would be a step toward making the script optional for simple investigations.

### `SearchLines()` Extraction is the Key Implementation Step

The prior analysis identified that `SearchLines()` in `HelixService` should be extracted to a shared utility. This analysis confirms the need: both `azdo_search_log` and `azdo_search_log_across_steps` would use the same search logic. Extract to `TextSearchHelper.SearchLines()` in Core, then both Helix and AzDO tools can use it.

---

## Priority Summary (Updated)

| Priority | Tool | Status | Evidence |
|----------|------|--------|----------|
| **P0** | `azdo_search_log` | Confirmed from real usage | Training log session b563ef92, delegation Pattern 5, manual-investigation.md |
| **P1** | `azdo_search_timeline` | NEW — from skill analysis | Timeline traversal in multiple reference docs |
| **P1** | `azdo_search_log_across_steps` | NEW — from delegation patterns | Multi-step search needed for Helix URL discovery |
| **P1** | Test results name filter | Confirmed from real usage | build-progression-analysis.md, sql-tracking.md |
| **P2** | `azdo_build_summary` | NEW — script replacement path | Get-CIStatus.ps1 core function replicated as MCP tool |
| **P2** | `azdo_log_error_extract` | NEW — domain-specific search | Script's `Extract-BuildErrors` patterns |

### 2026-03-08: Extract search types to top-level namespace

**By:** Ripley  
**Date:** 2026-03-08  
**Context:** azdo_search_log implementation

## What

Extracted `LogMatch`, `LogSearchResult`, and `FileContentSearchResult` record types from `HelixService` (nested records) to top-level types in the `HelixTool.Core` namespace, in the new `TextSearchHelper.cs` file.

## Why

`SearchLines()` needed to be shared between Helix and AzDO search operations. Since `AzdoService` lives in `HelixTool.Core.AzDO`, the return types couldn't remain nested inside `HelixService`. Making them top-level is cleaner and follows the existing pattern where shared types are accessible across sub-namespaces.

## Impact

- Any code referencing `HelixService.LogSearchResult` etc. needs to drop the `HelixService.` prefix (no existing code used it that way)
- Tests already had `TextSearchHelperTests.cs` anticipating this extraction
- No breaking changes to MCP tool DTOs (those use their own `SearchMatch`/`SearchLogResult` types in `HelixTool.Mcp.Tools`)

### 2025-07-14: Incremental log fetching for AzDO build logs

- AzDO log APIs should support optional `startLine`/`endLine` ranges so tail reads, delta refresh, and cross-step search avoid full-log downloads when possible.
- Caching uses a full-log cache plus a short-lived freshness marker for in-progress builds, appending deltas instead of repeatedly re-downloading the entire log.

### 2026-03-09: azdo_search_log_across_steps design spec

**By:** Dallas
**What:** Incremental search across ALL log steps in an AzDO build, ranked by failure likelihood, with early termination.
**Why:** The existing `azdo_search_log` requires the caller to already know *which* log ID to search. In a build with 160–380 timeline records, an AI agent must make dozens of sequential tool calls to find the needle. This tool automates the scan-and-rank pattern that a human would follow: check failed steps first, skip boilerplate, stop when enough matches are found.

---

## 1. Tool Identity

| Surface | Name | Description |
|---------|------|-------------|
| MCP | `azdo_search_log_across_steps` | Search ALL log steps in an Azure DevOps build for lines matching a pattern. Automatically ranks logs by failure likelihood (failed tasks first, then tasks with issues, then large succeeded logs) and returns matches incrementally. Stops early when maxMatches is reached. Use instead of manually iterating azdo_search_log across many log IDs. |
| CLI | `hlx azdo search-log-all` | Search all build log steps for a pattern, ranked by failure priority. |

The name uses `across_steps` rather than `across_logs` because MCP consumers think in pipeline terms (stages/jobs/steps), not log IDs.

## 2. Parameters

### MCP Tool Signature

```csharp
[McpServerTool(Name = "azdo_search_log_across_steps",
               Title = "Search All AzDO Build Logs",
               ReadOnly = true,
               UseStructuredContent = true)]
public async Task<CrossStepSearchResult> SearchLogAcrossSteps(
    [Description("AzDO build ID (integer) or full AzDO build URL")]
    string buildIdOrUrl,

    [Description("Text pattern to search for (case-insensitive substring match)")]
    string pattern = "error",

    [Description("Lines of context before and after each match (default: 2)")]
    int contextLines = 2,

    [Description("Maximum total matches across all logs (default: 50). Search stops early once reached.")]
    int maxMatches = 50,

    [Description("Maximum number of individual log steps to download and search (default: 30). Limits API calls for very large builds.")]
    int maxLogsToSearch = 30,

    [Description("Minimum line count to include a log in the search (default: 5). Filters out tiny boilerplate logs.")]
    int minLogLines = 5)
```

### CLI Signature

```
hlx azdo search-log-all <buildId> [--pattern P] [--context-lines N] [--max-matches N] [--max-logs N] [--min-lines N] [--json]
```

### Validation Rules

| Parameter | Rule | Exception |
|-----------|------|-----------|
| `pattern` | `ArgumentException.ThrowIfNullOrWhiteSpace` | `ArgumentException` |
| `contextLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| `maxMatches` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `maxLogsToSearch` | `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_, 0)` | `ArgumentOutOfRangeException` |
| `minLogLines` | `ArgumentOutOfRangeException.ThrowIfNegative` | `ArgumentOutOfRangeException` |
| env check | `HelixService.IsFileSearchDisabled` | `InvalidOperationException` / `McpException` |

No regex. Substring match only (`string.Contains(pattern, OrdinalIgnoreCase)`).

## 3. Return Types

### New types in `AzdoModels.cs` (Core layer)

```csharp
/// <summary>A log entry from the AzDO Build Logs List API (GET _apis/build/builds/{id}/logs).</summary>
public sealed record AzdoBuildLogEntry
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("lineCount")]
    public long LineCount { get; init; }

    [JsonPropertyName("createdOn")]
    public DateTimeOffset? CreatedOn { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>Matches found in a single log step during a cross-step search.</summary>
public sealed class StepSearchResult
{
    [JsonPropertyName("logId")] public int LogId { get; init; }
    [JsonPropertyName("stepName")] public string StepName { get; init; } = "";
    [JsonPropertyName("stepType")] public string? StepType { get; init; }
    [JsonPropertyName("stepResult")] public string? StepResult { get; init; }
    [JsonPropertyName("parentName")] public string? ParentName { get; init; }
    [JsonPropertyName("lineCount")] public long LineCount { get; init; }
    [JsonPropertyName("matchCount")] public int MatchCount { get; init; }
    [JsonPropertyName("matches")] public List<LogMatch> Matches { get; init; } = [];
}

/// <summary>Result of searching across all log steps in a build.</summary>
public sealed class CrossStepSearchResult
{
    [JsonPropertyName("build")] public string Build { get; init; } = "";
    [JsonPropertyName("pattern")] public string Pattern { get; init; } = "";
    [JsonPropertyName("totalLogsInBuild")] public int TotalLogsInBuild { get; init; }
    [JsonPropertyName("logsSearched")] public int LogsSearched { get; init; }
    [JsonPropertyName("logsSkipped")] public int LogsSkipped { get; init; }
    [JsonPropertyName("totalMatchCount")] public int TotalMatchCount { get; init; }
    [JsonPropertyName("stoppedEarly")] public bool StoppedEarly { get; init; }
    [JsonPropertyName("steps")] public List<StepSearchResult> Steps { get; init; } = [];
}
```

### New MCP result type in `McpToolResults.cs`

Not needed. `CrossStepSearchResult` already uses `[JsonPropertyName]` on all properties and `LogMatch` is already in `TextSearchHelper.cs`. The result type can live in Core because it's not reshaping — it IS the domain result. Same pattern as `TimelineSearchResult`.

## 4. Algorithm

### Phase 1: Metadata Collection (2 cheap API calls, parallelizable)

```
1. Resolve buildIdOrUrl → (org, project, buildId)
2. Parallel:
   a. GET _apis/build/builds/{buildId}/logs → List<AzdoBuildLogEntry>  (line counts, no content)
   b. GET _apis/build/builds/{buildId}/timeline → AzdoTimeline          (record states, log refs)
```

### Phase 2: Build Ranked Log Queue

Join timeline records to log entries by `record.Log.Id == logEntry.Id`:

```
For each timeline record with a log reference:
  - Lookup logEntry to get lineCount
  - Skip if lineCount < minLogLines (tiny boilerplate)
  - Assign priority bucket:
    Bucket 0: record.Result is "failed" or "canceled"
    Bucket 1: record.Issues is non-empty (warnings/errors attached)
    Bucket 2: record.Result is "succeededWithIssues"
    Bucket 3: record.Result is "succeeded" or null, lineCount >= minLogLines
  - Within each bucket: sort by lineCount descending (larger logs more likely to contain errors)
```

Orphan logs (logEntry.Id not referenced by any timeline record): appended at end (Bucket 4), sorted by lineCount desc. These are rare but possible in retried builds.

### Phase 3: Incremental Search (sequential downloads, early exit)

```
remainingMatches = maxMatches
logsSearched = 0

for each log in ranked queue (up to maxLogsToSearch):
  if remainingMatches <= 0: break (early exit)

  content = await GetBuildLogAsync(org, project, buildId, log.Id, ct)
  // Normalize line endings, split, search (reuse TextSearchHelper.SearchLines)
  searchResult = TextSearchHelper.SearchLines(
      identifier: $"log:{log.Id}",
      lines: normalizedLines,
      pattern: pattern,
      contextLines: contextLines,
      maxMatches: remainingMatches   // ← pass REMAINING, not total
  )

  if searchResult.Matches.Count > 0:
    add StepSearchResult with step metadata + matches
    remainingMatches -= searchResult.Matches.Count

  logsSearched++
```

### Normalization

Same `\r\n` → `\n`, `\r` → `\n` normalization as `SearchBuildLogAsync`. Extract into a private helper `NormalizeAndSplit(string content)` to DRY up.

## 5. Safety Guards & Limits

| Guard | Default | Rationale |
|-------|---------|-----------|
| `maxMatches` | 50 | Caps total matches across all logs. Primary context-overflow protection. |
| `maxLogsToSearch` | 30 | Caps API calls. A 380-log SDK build would need 380 HTTP requests without this. 30 covers all failures + the largest succeeded logs in typical builds. |
| `minLogLines` | 5 | Filters out ~60% of logs in a typical build (setup/teardown boilerplate with 1–4 lines). |
| No parallel downloads | — | Sequential downloads avoid hammering AzDO API. If we later add parallelism, limit to 3–5 concurrent. |
| `IsFileSearchDisabled` | env check | Same kill switch as all search tools. |
| No download size limit needed | — | `GetBuildLogAsync` already streams the response. Individual logs in AzDO builds rarely exceed 2MB. The `maxLogsToSearch` cap provides aggregate protection. |

### What about in-progress builds?

In-progress builds will have a partial timeline. The tool should work correctly — it just searches whatever logs exist. The timeline fetch is not cached for in-progress builds (established caching rule), so re-running the tool shows fresh results.

## 6. Relationship to `azdo_search_log`

**Complement, not replace.**

| Tool | Use case |
|------|----------|
| `azdo_search_log` | "Search log 47 for 'OutOfMemory'" — caller already knows the log ID (from timeline, from a previous search, from a URL). Fast, single API call. |
| `azdo_search_log_across_steps` | "Find 'error CS' anywhere in this build" — caller doesn't know which log(s) contain the pattern. Automated ranking + early exit. |

The `azdo_search_log_across_steps` description should mention `azdo_search_log` for targeted follow-up: "For targeted search of a specific log step, use azdo_search_log instead."

`azdo_search_log` remains the right tool when the caller has a specific log ID (common after `azdo_timeline`). The new tool is for the "I don't know where to look" workflow.

## 7. Interface & Client Changes

### `IAzdoApiClient` — new method

```csharp
/// <summary>List all build logs with metadata (line counts) without downloading content.</summary>
Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default);
```

### `AzdoApiClient` — implementation

```csharp
public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(
    string org, string project, int buildId, CancellationToken ct = default)
{
    var url = BuildUrl(org, project, $"build/builds/{buildId}/logs");
    return await GetListAsync<AzdoBuildLogEntry>(url, ct);
}
```

### `CachingAzdoApiClient` — caching wrapper

Cache with same dynamic TTL rules (completed build → 4h, in-progress → 15s). The logs list is immutable once a build completes.

### `AzdoService` — new method

```csharp
public async Task<CrossStepSearchResult> SearchBuildLogAcrossStepsAsync(
    string buildIdOrUrl, string pattern,
    int contextLines = 2, int maxMatches = 50,
    int maxLogsToSearch = 30, int minLogLines = 5,
    CancellationToken ct = default)
```

This is where the ranking algorithm, incremental search, and early termination live. Follows existing pattern: MCP tool is thin wrapper, business logic in `AzdoService`.

## 8. Estimated Test Surface for Lambert

### Unit Tests (AzdoService layer)

| ID | Test | Notes |
|----|------|-------|
| T-1 | Empty build (no logs, no timeline) | Returns 0 matches, logsSearched=0 |
| T-2 | All logs below minLogLines | Returns 0 matches, all skipped |
| T-3 | Single failed log with matches | Bucket 0 prioritization, correct StepSearchResult |
| T-4 | Ranking order: failed → issues → succeededWithIssues → succeeded | Verify download order via mock call sequence |
| T-5 | Early termination at maxMatches | Set maxMatches=3, provide 5 matches across 2 logs, verify stoppedEarly=true and exactly 3 matches |
| T-6 | maxLogsToSearch limit | 50 eligible logs, maxLogsToSearch=5, verify only 5 downloaded |
| T-7 | Orphan logs (no timeline record) | Log in logs list but not in timeline → Bucket 4 |
| T-8 | Pattern not found in any log | Returns 0 matches, stoppedEarly=false |
| T-9 | Timeline record with no log reference | Skipped (no log to download) |
| T-10 | Context lines propagation | Verify contextLines flows to TextSearchHelper |
| T-11 | Line ending normalization | `\r\n` and `\r` normalized before search |

### MCP Tool Tests

| ID | Test | Notes |
|----|------|-------|
| M-1 | Successful search returns CrossStepSearchResult | UseStructuredContent=true, verify JSON shape |
| M-2 | IsFileSearchDisabled → McpException | Not InvalidOperationException |
| M-3 | Service throws InvalidOperationException → McpException | Exception remapping |
| M-4 | Service throws HttpRequestException → McpException | Exception remapping |
| M-5 | Service throws ArgumentException → McpException | Exception remapping |

### Integration-level Tests (IAzdoApiClient mock)

| ID | Test | Notes |
|----|------|-------|
| I-1 | GetBuildLogsListAsync returns correct AzdoBuildLogEntry list | Deserialization from AzDO format |
| I-2 | Caching: completed build logs list cached at 4h TTL | CachingAzdoApiClient test |
| I-3 | Caching: in-progress build logs list cached at 15s TTL | Dynamic TTL |

**Estimated total: ~19 tests.** Aligns with the ~700 existing test count.

## 9. Implementation Notes

### Extract `NormalizeAndSplit`

Both `SearchBuildLogAsync` and `SearchBuildLogAcrossStepsAsync` need the same line normalization. Extract to a private static method:

```csharp
private static string[] NormalizeAndSplit(string content)
{
    var normalized = content
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal);
    var lines = normalized.Split('\n');
    if (normalized.EndsWith("\n", StringComparison.Ordinal) && lines.Length > 0)
        Array.Resize(ref lines, lines.Length - 1);
    return lines;
}
```

`SearchBuildLogAsync` should be updated to use this helper (minor refactor, not breaking).

### Timeline + Logs List Join

The join is by `timelineRecord.Log.Id == logEntry.Id`. Timeline records of type "Stage" and "Phase" rarely have logs, but if they do, include them. Records with `Log == null` are skipped (no downloadable log).

### Parallel Metadata Fetch

The timeline and logs list are independent API calls. Use `Task.WhenAll` to fetch both concurrently:

```csharp
var timelineTask = _client.GetTimelineAsync(org, project, buildId, ct);
var logsListTask = _client.GetBuildLogsListAsync(org, project, buildId, ct);
await Task.WhenAll(timelineTask, logsListTask);
```

### Future Optimization: Parallel Log Downloads

Deferred. Sequential downloads are simpler and sufficient for Phase 1. If performance is a problem (unlikely with `maxLogsToSearch=30`), add a `SemaphoreSlim(3)` bounded parallel download later.

# Decision: Timeline search result types live in Core

**By:** Ripley
**Date:** 2025-07-19
**Context:** azdo_search_timeline implementation

## Decision

`TimelineSearchMatch` and `TimelineSearchResult` are defined in `HelixTool.Core.AzDO` (AzdoModels.cs), not in `McpToolResults.cs`. The MCP tool returns the Core types directly.

## Rationale

- Core can't reference Mcp.Tools (dependency direction: Mcp.Tools → Core, not reverse).
- Same pattern as `AzdoBuildSummary` — domain types in Core, MCP tools return them directly.
- `[JsonIgnore]` on `TimelineSearchMatch.Record` keeps MCP JSON flat while exposing the raw `AzdoTimelineRecord` for programmatic consumers and tests.

## Impact

- Future search-style features in Core should follow this pattern: define result DTOs with `[JsonPropertyName]` in Core, add `[JsonIgnore]` for any raw-data properties that shouldn't serialize.
- `McpToolResults.cs` is for MCP-specific wrapper types that don't map 1:1 to service returns (like `SearchBuildLogResult` which reshapes `LogSearchResult`).

---

### 2025-07-18: Performance review findings

- The main allocation hot spots were line-ending normalization, tail trimming, repeated substring work in pattern matching, and serializing large log strings unnecessarily.
- Prioritize perf fixes on search/log/cache paths where multi-megabyte content is handled repeatedly; minor cache-key and helper allocations are secondary.

### 2026-03-09: Cache format change — raw: prefix (Ripley perf fixes)

**By:** Ripley

**Context:** CachingAzdoApiClient stored log content via `JsonSerializer.Serialize<string>()`, double-escaping multi-MB strings. Changed to plain text with `raw:` sentinel prefix.

**Decision:** Backward-compatible migration via sentinel detection. `DeserializeLogContent` checks for `raw:` prefix first, falls back to JSON deserialization for legacy entries. No explicit migration step — natural TTL expiry handles transition.

**Risk:** Low. Legacy entries are still readable. New entries are written in the efficient format. Cache key structure is unchanged, so there's no key collision.

**For Dallas to review:** Is the `raw:` prefix approach acceptable long-term, or should we consider a versioned cache format? The prefix relies on log content never starting with `raw:` literally — extremely unlikely for AzDO build logs but worth noting.

### 2026-03-09: SearchConsoleLogAsync decoupled from disk download (Ripley perf fixes)

**By:** Ripley

**Context:** `SearchConsoleLogAsync` used `DownloadConsoleLogAsync` (stream→disk) then `File.ReadAllLinesAsync` (disk→memory). Changed to use `GetConsoleLogContentAsync` (stream→memory directly).

**Decision:** Safe to decouple because `DownloadConsoleLogAsync` is only used by the CLI download command and `SearchFileAsync` (which needs disk for binary detection). Search doesn't need disk presence.

**Risk:** None observed — 864/864 tests pass. If a future change adds caching or rate-limiting at the download layer, search would bypass it. Worth noting but not a current concern.

### 2026-03-09: Shared StringHelpers in Core (Ripley perf fixes)

**By:** Ripley

**Context:** Both AzdoService and HelixService had identical tail-trimming patterns. Extracted to `HelixTool.Core.StringHelpers` (internal static class).

**Decision:** `internal` visibility is sufficient — only Core code needs it. If CLI or MCP projects need it in the future, promote to `public`.

### 2026-03-09: CI repo profile analysis for MCP tool improvements

**By:** Ash
**What:** Analysis of CI repo profiles identifying improvements for MCP tool descriptions and error messages
**Why:** Real-world CI investigation patterns should inform tool guidance to reduce agent iteration cycles

---

## Executive Summary

Analyzed 6 CI repo profiles (runtime, aspnetcore, sdk, roslyn, efcore, vmr) plus the SKILL.md umbrella document against current MCP tool implementations in `HelixMcpTools.cs`, `AzdoMcpTools.cs`, and `HelixService.cs`. Found **14 actionable recommendations** across 4 categories. The highest-impact change is improving `helix_test_results` description and error messages — agents currently waste 2-3 tool calls discovering that TRX files don't exist for most repos.

---

## 1. Tool Description Improvements

### REC-1: `helix_test_results` — Add repo-aware guidance to tool description (P0)

**Current description:**
> "Parse TRX test result files from a Helix work item. Returns structured test results including test names, outcomes, durations, and error messages for failed tests. Auto-discovers all .trx files or filter to a specific one."

**Problem:** The name says "TRX" but the tool also parses xUnit XML. More critically, agents don't know that this tool **fails for 4 of 6 major repos** (aspnetcore, sdk, roslyn, efcore). They try it first, get an error, then have to figure out an alternative — wasting 2-3 tool calls per investigation.

**Recommended new description:**
> "Parse test result files (TRX or xUnit XML) from a Helix work item's blob storage. Returns structured test results including test names, outcomes, durations, and error messages. Auto-discovers files matching *.trx, testResults.xml, *.testResults.xml.txt. **Important:** Most dotnet repos do NOT upload test result files to Helix blob storage — the Arcade reporter consumes them locally and publishes to AzDO instead. This tool works for: runtime CoreCLR tests (job names containing 'coreclr_tests'), runtime iOS/Android XHarness tests. For all other repos/workloads (aspnetcore, sdk, roslyn, efcore, runtime libraries), use azdo_test_runs + azdo_test_results instead."

**Profiles motivating this:** runtime.md (lines 41-49, 63-71), aspnetcore.md (lines 9, 17-28), sdk.md (lines 37-39), roslyn.md (lines 43-47), efcore.md (lines 60-65)

---

### REC-2: `helix_search_log` — Add repo-specific search pattern guidance (P0)

**Current description mentions:**
> "Common patterns: '  Failed' (2 leading spaces) for xUnit test failures, 'Error Message:' for test error details, 'exit code' for process crashes."

**Problem:** The best search pattern varies dramatically by repo. `[FAIL]` works for runtime but not aspnetcore. `  Failed` works for aspnetcore but not runtime. Neither works for roslyn (crashes dominate). Agents pick the wrong pattern and get 0 results, then iterate.

**Recommended addition to description:**
> "Best search patterns vary by repo: runtime uses '[FAIL]' (xunit runner format); aspnetcore uses '  Failed' (2 leading spaces, dotnet test format); sdk uses 'Failed' or 'Error' (build-as-test, infra failures dominate); roslyn uses 'aborted' or 'Process exited' (crashes dominate, not assertion failures); efcore uses '[FAIL]' (xunit console runner). For build failures in any repo, try 'error MSB' or 'error CS'."

**Profiles motivating this:** runtime.md (lines 93-101), aspnetcore.md (lines 57-71), sdk.md (lines 90-101), roslyn.md (lines 75-86), efcore.md (lines 97-103)

---

### REC-3: `azdo_test_runs` — Warn about untrustworthy summary counts (P1)

**Current description:**
> "Get test runs for an Azure DevOps build. Returns test run summaries with total, passed, and failed counts. Use to get an overview of test execution for a build before drilling into individual test results with azdo_test_results."

**Problem:** Every single profile documents the same gotcha — run-level `failedTests: 0` metadata lies. Agents trust the summary and skip drilling into runs, missing real failures.

**Recommended addition:**
> "⚠️ Run-level failedTests counts can be 0 even when the run contains actual failures. Always call azdo_test_results on runs associated with failed Helix jobs — do not trust the summary count to determine if tests passed."

**Profiles motivating this:** runtime.md (lines 84-88), aspnetcore.md (lines 50-51), sdk.md (lines 63-67), roslyn.md (lines 68-71)

---

### REC-4: `azdo_timeline` — Add repo-specific task name guidance (P1)

**Current description:**
> "Get the build timeline showing stages, jobs, and tasks for an Azure DevOps build."

**Problem:** The Helix-dispatching task has different names across repos. Agents search for "Send to Helix" universally, but this fails for sdk (`🟣 Run TestBuild Tests`), roslyn (no separate task — Helix is inside `Run Unit Tests`/`Test`), and efcore (`Send job to helix`).

**Recommended addition to description:**
> "To find Helix job IDs, search for the Helix-dispatching task. Task names vary by repo: runtime/aspnetcore use 'Send to Helix'; sdk uses '🟣 Run TestBuild Tests'; roslyn embeds Helix inside 'Run Unit Tests' or 'Test' tasks (no separate Helix task); efcore uses 'Send job to helix'. Use azdo_search_timeline to find the right task."

**Profiles motivating this:** sdk.md (lines 48-55), roslyn.md (lines 16-29), efcore.md (line 178)

---

### REC-5: `helix_status` — Clarify exit code interpretation (P2)

**Current description:**
> "Get work item pass/fail summary for a Helix job. Returns structured JSON with job metadata, failed items (with exit codes, state, duration, machine, failureCategory), and passed count."

**Problem:** Exit codes mean different things and agents don't know how to interpret them. Also, runtime exit code 0 can mask test failures — the tool description should warn about this.

**Recommended addition:**
> "Common exit codes: 0 = passed (but runtime may report 0 with actual [FAIL] results — check console), 1 = test assertion failure, 130 = crash/SIGINT, -3 = timeout, -4 = infrastructure failure (docker/environment). Check failureCategory: 'InfrastructureError' or 'Crash' = infra problem, not a test bug."

**Profiles motivating this:** runtime.md (lines 152-156), sdk.md (lines 81-86), roslyn.md (lines 57-60), efcore.md (lines 91-95)

---

### REC-6: `azdo_search_timeline` — Suggest as the primary triage entry point (P2)

**Current description is functional but doesn't suggest workflow.** Every profile's recommended investigation order starts with `azdo_timeline(buildId, filter="failed")`, but agents often skip straight to Helix tools without knowing which jobs failed.

**Recommended addition:**
> "This is typically the first tool to call when investigating a build failure. Use pattern '*' with filter='failed' to see all failures, or search for specific task names like 'Send to Helix', 'Build', or 'Test' to find relevant steps."

**Profiles motivating this:** All 6 profiles list `azdo_timeline` as step 1

---

## 2. Error Message Improvements

### REC-7: `helix_test_results` "No test result files" error — Add actionable next steps (P0)

**Current error message (HelixService.cs line 931):**
> "No test result files found in work item '{workItem}'. Searched for: {patterns}."

The message already has crash-artifact detection and file-listing logic (good!), but the generic case (files found, no test results) suggests searching for `'  Failed'` only — which is wrong for runtime (`[FAIL]`) and roslyn (`aborted`).

**Recommended change:** Replace the generic fallback (line 943) with repo-aware guidance:

```
$"{fileNames.Count} files found but none match test result patterns. "
+ "Test results are likely published to AzDO instead of Helix blob storage. "
+ "Use azdo_test_runs + azdo_test_results to get structured test data. "
+ "For quick console triage, try helix_search_log with patterns: "
+ "'[FAIL]' (runtime/efcore), '  Failed' (aspnetcore), "
+ "'Failed' (sdk), 'aborted' (roslyn crashes)."
```

**Profile motivating this:** All non-runtime profiles document this as the primary failure mode

---

### REC-8: `helix_test_results` — Strengthen the "no files at all" message (P1)

**Current message (line 947):**
> "The work item has no uploaded files."

**Recommended change:**
> "The work item has no uploaded files. This typically means the test host crashed before producing results. Check helix_status for exit code and failureCategory. For roslyn, search console for 'aborted' or 'Process exited'. For sdk, check for architecture mismatch ('incompatible')."

**Profiles motivating this:** roslyn.md (lines 129-138), sdk.md (lines 131-138), efcore.md (line 111-112)

---

## 3. Missing Tool Capabilities

### REC-9: `azdo_search_log_across_steps` — Powerful but description needs workflow guidance (P1)

This tool exists and is well-implemented, but agents often don't know when to use it vs `azdo_search_log`. The profiles suggest clear use cases.

**Recommended addition to description:**
> "Especially useful for: VMR builds (44+ verticals, need to find which component failed — search for 'error MSB3073'); runtime builds (50-200+ jobs); sdk builds (find architecture mismatch errors across jobs — search for 'incompatible'). Use azdo_search_log when you already know the specific log ID."

**Profiles motivating this:** vmr.md (lines 86-93), runtime.md (lines 166-183)

---

### REC-10: No tool for extracting Helix job IDs from AzDO build logs (P2 — future capability)

**Problem:** All profiles describe a manual step: read AzDO task log → find Helix job GUID. For runtime/aspnetcore, job IDs appear in "Send to Helix" task output. For roslyn, they're in the body of "Run Unit Tests" output (`"Work item workitem_N in job <GUID> has failed"`). For sdk, they're in `🟣 Run TestBuild Tests` issue messages.

**Recommendation:** Consider a future tool or enhancement that auto-extracts Helix job IDs from an AzDO build. This would eliminate 1-2 manual tool calls per investigation. Could be implemented as an enhancement to `azdo_timeline` that parses known patterns from failed task messages/logs.

**Profiles motivating this:** All Helix-using profiles (runtime, aspnetcore, sdk, roslyn, efcore)

---

### REC-11: `helix_batch_status` could surface cross-job failure patterns (P3 — future)

**Problem:** When an entire Helix queue fails (e.g., efcore macOS crash exit -3 on all 20 work items, or sdk architecture mismatch on all items), agents need to recognize the pattern. Currently they check individual work items.

**Recommendation:** Future enhancement — `helix_batch_status` or `helix_status` could detect and flag patterns like "all N items on queue X failed with same exit code" → likely infrastructure issue, not individual test failures.

**Profiles motivating this:** efcore.md (lines 133-136), sdk.md (lines 126-128)

---

## 4. Search Pattern Guidance

### REC-12: Create a consolidated pattern reference in tool descriptions (P1)

The profiles document different console log patterns per repo. This knowledge should be surfaced somewhere accessible to agents. Options:

**Option A (recommended):** Enrich `helix_search_log` description with a concise pattern table (see REC-2 above)

**Option B:** Add a `helix_search_patterns` read-only tool that returns repo-specific search guidance when given a repo name. Low implementation cost but adds tool count.

**Option C:** Document in the ci-analysis skill prompt. Already partially done in the SKILL.md, but agents using raw MCP tools (not the skill) don't see it.

---

### REC-13: VMR builds don't use Helix — agents should know this (P1)

**Problem:** VMR (dotnet/dotnet) doesn't use Helix at all. Agents may waste calls trying `helix_status` on VMR builds.

**Recommended:** Add to `helix_status` description:
> "Note: VMR (dotnet/dotnet) builds do NOT use Helix. For VMR build failures, use azdo_timeline and azdo_search_log_across_steps to find build errors (typically 'error MSB3073')."

**Profile motivating this:** vmr.md (lines 14-18)

---

### REC-14: `azdo_test_results` — Clarify synthetic vs real results (P2)

**Problem:** SDK and roslyn produce synthetic `WorkItemExecution` results when Helix work items crash — these are not real test failures. Agents may misinterpret them.

**Recommended addition:**
> "For sdk/roslyn builds, watch for synthetic results with testCaseTitle ending in 'Work Item' and automatedTestName ending in 'WorkItemExecution' — these indicate Helix work item crashes, not actual test failures. Check helix_status for the underlying exit code."

**Profile motivating this:** sdk.md (lines 68-75)

---

## Priority Summary

| Priority | Rec | Tool/Area | Impact |
|----------|-----|-----------|--------|
| **P0** | REC-1 | `helix_test_results` description | Saves 2-3 wasted calls per investigation for 4/6 repos |
| **P0** | REC-2 | `helix_search_log` description | Prevents wrong-pattern searches, saves 1-2 iterations |
| **P0** | REC-7 | `helix_test_results` error message | Provides actionable next steps instead of dead end |
| **P1** | REC-3 | `azdo_test_runs` description | Prevents agents from trusting lying summary counts |
| **P1** | REC-4 | `azdo_timeline` description | Eliminates failed "Send to Helix" searches for 3/6 repos |
| **P1** | REC-8 | `helix_test_results` error (no files) | Better crash diagnosis guidance |
| **P1** | REC-9 | `azdo_search_log_across_steps` description | Better workflow guidance for large builds |
| **P1** | REC-12 | Pattern reference location | Ensures agents can find the right search pattern |
| **P1** | REC-13 | `helix_status` description | Prevents wasted Helix calls on VMR builds |
| **P2** | REC-5 | `helix_status` description | Exit code interpretation |
| **P2** | REC-6 | `azdo_search_timeline` description | Workflow guidance |
| **P2** | REC-10 | Future: auto-extract Helix job IDs | Eliminates manual log-reading step |
| **P2** | REC-14 | `azdo_test_results` description | Synthetic result disambiguation |
| **P3** | REC-11 | Future: cross-job pattern detection | Infrastructure failure recognition |

---

## Implementation Notes

- **REC-1, 2, 3, 4, 5, 6, 9, 13, 14** are pure description text changes in `HelixMcpTools.cs` and `AzdoMcpTools.cs` — no logic changes needed
- **REC-7, 8** are error message changes in `HelixService.cs` (lines 931-948)
- **REC-10, 11** are future capability proposals requiring design review
- **REC-12** is a decision about where to surface pattern knowledge — recommend Option A (enrich tool descriptions) as the simplest path
- All description changes should be verified by Lambert with existing tests to ensure no regressions

# Decision: Test Quality Review — Tautological Test Findings

**Date:** 2025-07-24
**Decided by:** Dallas (Lead)
**Status:** RECOMMENDATION — requires team discussion

## Executive Summary

Reviewed all 776 tests across 50 test files (~14,200 lines). Found **~40 problematic tests** (5% of total) across 4 categories, with the most significant issue being **redundant duplication between AzdoCliCommandTests and AzdoServiceTests** (~14 near-duplicate tests). The test suite is generally well-engineered — the problems are concentrated, not systemic.

**Severity: LOW-MEDIUM.** The test suite is not bloated to the point of harm, but the duplication wastes CI time and creates maintenance burden when service signatures change.

---

## Detailed Findings

### Category 4 — Redundant Tests (MOST SIGNIFICANT: ~20 tests)

**The biggest problem.** `AzdoCliCommandTests` was written "proactively for CLI subcommand registration" but tests the _same AzdoService methods_ as `AzdoServiceTests`. Both test files mock IAzdoApiClient, construct AzdoService, and call the same methods with the same patterns.

| AzdoCliCommandTests | AzdoServiceTests | Identical? |
|---|---|---|
| `GetBuildSummary_PlainBuildId_DefaultsToPublic` | `GetBuildSummaryAsync_PlainId_UsesDefaultOrgProject` | YES |
| `GetBuildSummary_AzdoUrl_ResolvesOrgProject` | `GetBuildSummaryAsync_DevAzureUrl_ParsesOrgAndProject` | YES |
| `GetBuildSummary_NotFound_ThrowsInvalidOperation` | `GetBuildSummaryAsync_NullBuild_ThrowsInvalidOperation` | YES |
| `GetBuildSummary_InvalidBuildId_ThrowsArgumentException` | `GetBuildSummaryAsync_InvalidUrl_ThrowsArgumentException` | YES |
| `GetTimeline_ValidBuild_ReturnsTimeline` | `GetTimelineAsync_PlainId_ResolvesToDefaultOrgProject` | YES |
| `GetTimeline_NoBuild_ReturnsNull` | `GetTimelineAsync_NullResult_ReturnsNull` | YES |
| `GetBuildLog_ReturnsContent` | `GetBuildLogAsync_NullTailLines_ReturnsFullContent` | ~90% |
| `GetBuildLog_WithTailLines_ReturnsLastN` | `GetBuildLogAsync_TailLines_ReturnsLastNLines` | YES |
| `GetBuildLog_NotFound_ReturnsNull` | `GetBuildLogAsync_NullContent_ReturnsNull` | YES |
| `GetBuildChanges_ReturnsChangeList` | `GetBuildChangesAsync_PlainId_PassesDefaultsToClient` | YES |
| `GetTestRuns_ReturnsRunsList` | `GetTestRunsAsync_PlainId_PassesDefaultsToClient` | YES |
| `GetTestResults_ReturnsResults` | `GetTestResultsAsync_Url_ResolvesOrgProject` | ~80% |
| `GetBuildSummary_CalculatesDuration` | `GetBuildSummaryAsync_Duration_ComputedFromStartAndFinish` | YES |
| `GetBuildSummary_InProgressBuild_NullDuration` | `GetBuildSummaryAsync_NullStartOrFinish_DurationIsNull` | YES |

Also **AzdoMcpToolsTests** overlaps with both:
- `Build_ReturnsBuildSummary` overlaps with `AzdoServiceTests.GetBuildSummaryAsync_MapsAllFieldsCorrectly`
- `Changes_ReturnsChangeList`, `TestRuns_ReturnsRunList`, `TestResults_ReturnsResultList` are passthrough-verifying tests that mostly just confirm the MCP tool delegates to the service

And in Helix:
- `HelixMcpToolsTests.Status_ReturnsValidJsonWithExpectedStructure` substantially overlaps with `HelixServiceDITests.GetJobStatusAsync_HappyPath_ReturnsAggregatedSummary`
- `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed` verify the same behavior (default filter = "failed")

**Recommendation:** CONSOLIDATE. Delete `AzdoCliCommandTests` entirely — it provides zero coverage that `AzdoServiceTests` doesn't already have. The CLI tests should only exist once CLI command classes exist and need registration/parsing testing. The `AzdoCliCommandTests.GetBuildArtifacts_*` and `GetBuildChanges_WithTopParameter_PassesToClient` tests are the only ones adding unique value — move those to `AzdoServiceTests`. Merge the two overlapping HelixMcpToolsTests filter tests.

---

### Category 2 — Identity-Transform / Passthrough Tests (~8 tests)

Tests where the code under test is essentially `return await _client.Method(...)` and the test just verifies the return value matches the mock:

| Test | What it actually tests |
|---|---|
| `AzdoServiceTests.ListBuildsAsync_EmptyList_ReturnsEmpty` | Passthrough — service calls client, returns result |
| `AzdoServiceTests.GetBuildChangesAsync_EmptyList_ReturnsEmpty` | Same |
| `AzdoServiceTests.GetTestRunsAsync_EmptyList_ReturnsEmpty` | Same |
| `AzdoServiceTests.ListBuildsAsync_PassesFilterToClient` | Only asserts `Received(1)` — verifies wiring, not logic |
| `AzdoMcpToolsTests.Changes_ReturnsChangeList` | MCP → Service → Client passthrough |
| `AzdoMcpToolsTests.TestRuns_ReturnsRunList` | Same |
| `AzdoMcpToolsTests.TestResults_ReturnsResultList` | Same |
| `AzdoMcpToolsTests.Builds_ReturnsBuildList` | Same |

**Recommendation:** KEEP with reduced priority. These do have marginal value as regression guards — if someone accidentally breaks the wiring, they'll catch it. But they should never be the ONLY tests for a feature. They're acceptable as "contract smoke tests" but should not be treated as meaningful coverage.

---

### Category 5 — Setup-Heavy / Assertion-Light (~5 tests)

| Test | Assertion |
|---|---|
| `HelixApiClientFactoryTests.ImplementsIHelixApiClientFactory` | `Assert.NotNull(factory)` |
| `HttpContextHelixTokenAccessorTests.ImplementsIHelixTokenAccessor` | `Assert.NotNull(accessor)` |
| `HelixMcpToolsTests.Constructor_AcceptsHelixService` | `Assert.NotNull(tools)` |
| `HelixApiClientFactoryTests.Create_WithToken_ReturnsValidClient` | `Assert.NotNull` + `IsAssignableFrom` |
| `HelixApiClientFactoryTests.Create_NullToken_ReturnsUnauthenticatedClient` | `Assert.NotNull` + `IsAssignableFrom` |

**Recommendation:** REMOVE the "ImplementsI*" and "Constructor_Accepts*" tests. These are compile-time guarantees — if the class doesn't implement the interface, the code won't compile. The `Create_*_ReturnsValidClient` tests have marginal value; consider keeping one and dropping the rest.

---

### Category 1 — Mock-Verifying Tests (~5 tests)

| Test | Pattern |
|---|---|
| `AzdoMcpToolsTests.Build_ReturnsBuildSummary` | Mock returns AzdoBuild, assert AzdoBuildSummary fields match |
| `StructuredJsonTests.Status_IncludesJobId` | 20 lines setup, asserts `result.Job.JobId == ValidJobId` |
| `StructuredJsonTests.Status_IncludesHelixUrl` | 20 lines setup, asserts URL construction |

**Recommendation:** The `Build_ReturnsBuildSummary` test is a duplicate of `AzdoServiceTests.GetBuildSummaryAsync_MapsAllFieldsCorrectly` — CONSOLIDATE. The StructuredJsonTests are borderline; they test real output structure but the setup-to-assertion ratio is high. KEEP but note they're low-value.

---

## Well-Written Tests (Positive Examples)

These files exemplify the patterns the team should follow:

1. **AzdoSecurityTests** — Tests real security boundaries with adversarial inputs (SSRF, XSS, SQL injection, path traversal, embedded credentials). Each test verifies defense-in-depth behavior. **Gold standard for security testing.**

2. **AzdoIdResolverTests / HelixIdResolverTests** — Pure-function tests. No mocking. Clear input→output contracts. Easy to read, fast to execute.

3. **TextSearchHelperTests** — Tests real algorithmic logic (context lines, max matches, case sensitivity, edge cases). No mocks.

4. **AzdoServiceTailTests** — Tests meaningful optimization logic (tail vs full fetch). Verifies both the optimization path AND the fallback. Uses `Received`/`DidNotReceive` to verify the correct API was called — this is the RIGHT way to use mock verification.

5. **CachingAzdoApiClientTests / CachingHelixApiClientTests** — Decorator pattern tests done right. Cache hit → skip inner. Cache miss → call inner + store. Dynamic TTL. These test real caching logic, not just passthrough.

6. **StreamingBehaviorTests** — Tests real I/O edge cases (empty streams, large content, tail behavior, UTF-8 encoding, stream disposal). The `TrackingMemoryStream` helper is a good pattern.

7. **SqliteCacheStoreTests / SqliteCacheStoreConcurrencyTests** — Integration tests with real SQLite. Tests real storage behavior.

---

## Recommendations

### Immediate (Lambert should action)

1. **Delete `AzdoCliCommandTests.cs`** — Move the 3 unique tests (`GetBuildArtifacts_DefaultPattern_ReturnsAll`, `GetBuildArtifacts_PatternFilter_FiltersResults`, `GetBuildChanges_WithTopParameter_PassesToClient`) to `AzdoServiceTests.cs`. Delete the rest (~16 tests). Net reduction: ~13 tests, ~280 lines.

2. **Delete the 3 "ImplementsI*" / "Constructor_Accepts*" tests** — Compile-time guarantees don't need runtime tests. Net reduction: 3 tests.

3. **Merge `Status_FilterFailed_PassedIsNull` and `Status_DefaultFilter_ShowsOnlyFailed`** in `HelixMcpToolsTests` — They test the same thing. Net reduction: 1 test.

### Future Guidelines

4. **Rule: No test file per layer for the same behavior.** When testing Service methods, one test file is enough. Don't create CLI-level and MCP-level test files that re-test the same Service calls. When a proactive test file (written before production code) overlaps with a later "real" test file, prune the proactive tests during the PR that adds the real tests — don't let duplicates accumulate. *(Consolidated from Lambert's independent finding on 2025-07-18.)*

5. **Rule: Passthrough methods get at most 1 smoke test,** not exhaustive input variations. If a method is `return await _client.Foo(args)`, one test proving the delegation is sufficient.

6. **Rule: Interface compliance tests are redundant.** The compiler already enforces `IFoo foo = new Bar()` — testing it at runtime wastes CI.

### Estimated Impact

- Tests to remove/consolidate: ~17
- Lines to remove: ~350
- Tests remaining: ~759
- Coverage impact: ZERO (all removed tests are duplicates of retained tests)

### 2026-03-10: Enriched CiKnowledgeService with full 9-repo knowledge base

**By:** Ripley
**What:** Expanded `CiRepoProfile` record with 9 new properties (PipelineNames, OrgProject, ExitCodeMeanings, WorkItemNamingPattern, KnownGotchas, RecommendedInvestigationOrder, TestFramework, TestRunnerModel, UploadedFiles) and added 3 new repos (maui, macios, android) to the knowledge base. Updated FormatProfile() and GetOverview() to render enriched fields. Updated CiKnowledgeTool.cs description for expanded repo set. Total coverage: 9 repos.
**Why:** The CI knowledge base was the single source of truth for agent investigation guidance but only covered 6 repos with basic fields. Agents investigating MAUI, macios, or android failures had no guidance at all. Critical operational knowledge (exit code meanings, known gotchas like failedTests=0 lying, org/project differences for devdiv repos, pipeline-specific investigation paths for MAUI's 3 pipelines) was missing. The enriched profiles prevent wasted tool calls (e.g., trying helix_* on devdiv repos) and encode hard-won investigation patterns from reference profile analysis.

### 2026-03-10: Updated MCP tool descriptions with CI knowledge

**By:** Ripley
**What:** Updated 5 MCP tool descriptions (helix_test_results, helix_search_log, azdo_test_runs, azdo_test_results, azdo_timeline) to embed repo-specific CI knowledge from CiKnowledgeService profiles. Added warnings about common failure paths, repo-specific search patterns, Helix task name mappings, and cross-references to helix_ci_guide.
**Why:** Agents were wasting calls on helix_test_results for repos that don't upload TRX (4/6 major repos), using wrong search patterns, and not knowing which Helix task names to look for in timelines. These description changes are the cheapest possible fix — zero runtime cost, immediate agent behavior improvement. Descriptions are the first thing agents read before deciding which tool to call, so getting them right eliminates entire classes of dead-end investigations.

### 2025-07-24: Architectural Analysis — Helix vs AzDO File Structure Separation

- Adopt folder-level symmetry before project splitting: `Core/Helix`, `Core/AzDO`, `Core/Cache`, parallel test folders, and separated MCP tool folders.
- Shared helpers such as pattern matching and file-search toggles should live outside `HelixService` so AzDO code does not depend on Helix-specific types.

### 2025-07-24: Restructure Execution Notes — Option A

- Option A was executed mechanically: Helix-specific files moved under `Core/Helix`, cache infrastructure got its own namespace, and shared helpers moved to `StringHelpers`.
- Folder and namespace moves should preserve behavior while making domain boundaries visible; use the new helper locations for future work instead of legacy wrapper methods.

### 2026-03-10: README structure — lead with value prop, promote caching and context reduction

**By:** Kane
**What:** Restructured README to prioritize "why" (value prop for AI agents), cross-process caching, and context-efficient design as the three top-level stories. Removed project structure, moved CLI reference to docs/cli-reference.md, de-emphasized TRX parsing from featured section to tool list entry.
**Why:** The previous README was comprehensive but organized by implementation surface (CLI commands, project structure, enhancement tables) rather than by what matters to someone evaluating the tool. The two biggest differentiators — that cached data is shared across MCP server instances, and that tools are designed to return minimal context-window-friendly output — were buried in subsections. The overhaul puts these front and center so the README answers "why should I use this?" before "how do I use it?".

### 2026-03-10: Keep the strict HelixService HttpClient requirement

**By:** Lambert
**What:** Validated the removal of `HelixService`'s implicit `new HttpClient()` fallback and found no remaining one-argument construction sites in repo code or tests. Existing DI wiring in `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` already supplies named `HelixDownload` clients, and constructor null-guard coverage exists in `src/HelixTool.Tests/Helix/HelixServiceDITests.cs`.
**Why:** This keeps the service aligned with explicit dependency injection and avoids silently bypassing configured HTTP policies. Focused tests plus the full suite passed, so there is no current production follow-up required for the fallback removal.

### 2026-03-10: Security boundaries and download transports must be explicit

**By:** Ripley
**What:** `CacheSecurity.ValidatePathWithinRoot` now treats path containment as an exact, case-sensitive boundary check after full-path normalization and root-boundary trimming. `HelixService` no longer creates a fallback `HttpClient`; every caller must provide one, and the constructor null-guards both dependencies.
**Why:** Ignore-case prefix checks can let a case-variant sibling path look like it is under the trusted root on case-sensitive filesystems. Requiring injected `HttpClient` instances keeps timeout/handler configuration centralized in DI and avoids hidden transport creation that bypasses host configuration.

### 2026-03-10: Mark review-fix findings resolved in planning artifacts

**By:** Ash
**What:** The README value-prop rewrite, cache path-boundary hardening, and `HelixService` explicit-`HttpClient` requirement are now confirmed by updated code, tests, and docs. Planning artifacts should record these as fixed findings and keep only residual follow-up work around discoverability and documentation/tool-description synchronization.
**Why:** Leaving those earlier review findings in the active backlog would make the knowledgebase stale and could send future analysts chasing already-resolved work instead of the remaining product gaps.

### 2026-03-10: User directive

**By:** Larry Ewing (via Copilot)
**What:** Treat the knowledgebase as a living document that should be updated from the latest file state rather than preserved as a static snapshot.
**Why:** User request — captured for team memory

# Dallas decisions inbox — Discoverability review (2026-03-10)

1. **Do not add a new composite failure-investigation tool in this increment.**
   Improve discoverability through existing surfaces: MCP tool descriptions, fallback/error messages, `helix_ci_guide`, README, and llmstxt/help output.

2. **Make `helix_ci_guide(repo)` the recommended repo-specific entry point for workflow selection.**
   Tool descriptions and failure messages should direct agents there when pattern choice or result-location expectations vary by repo.

3. **Clarify the behavioral contract of `helix_test_results`.**
   It should be described as structured Helix-hosted test-result parsing with existing fallback support, but not as the universal first choice across repos. Failure guidance must route callers to the correct next tool sequence.

4. **Clarify the behavioral contract of `helix_search_log`.**
   It should be positioned as the preferred remote-first console-log search path, with explicit note that search patterns vary by repo/test runner.

5. **Keep discoverability surfaces synchronized.**
   MCP descriptions, README, llmstxt/help output, and CI-guide wording must align on when to use `helix_test_results`, when to pivot to AzDO structured results, and when to use `helix_search_log`.

### 2026-03-10: Keep discoverability docs as a short routing note

**By:** Kane
**What:** Updated the README and CLI reference to explain the investigation path in three steps: start with `helix_ci_guide(repo)` when repo workflow expectations vary, use `helix_test_results` only for Helix-hosted structured results, then pivot to AzDO structured results or `helix_search_log` as appropriate.
**Why:** The design review called for better discoverability without turning docs into a manual. A compact routing note keeps the surfaces aligned with Dallas's decisions while preserving the README's concise, evaluator-friendly shape.

### 2026-03-10: Prefer explicit fallback routing in CI investigation copy

**By:** Ripley
**What:** MCP descriptions, `helix_test_results` failure text, `helix_ci_guide`, and `Program.cs` help output should explicitly route callers between `helix_test_results`, `azdo_test_runs + azdo_test_results`, `helix_search_log`, and `helix_ci_guide(repo)` without adding a composite tool or new parameters.
**Why:** Repo-specific CI workflows already exist, but vague warnings still cause wasted tool calls. Concise “use this when / otherwise go here next” wording improves discoverability while preserving the approved incremental surface.

### 2025-07-25: Rename "vmr" profile key to "dotnet"

**By:** Ripley
**Status:** Implemented

## Context

The CI knowledge profile for dotnet/dotnet used `"vmr"` as its dictionary key and `RepoName`. Agents searching for `dotnet` or `dotnet/dotnet` would miss it unless they knew about a special-case fallback, and overview listings showed `vmr` instead of the repo name agents actually recognize.

## Decision

- Dictionary key changed from `["vmr"]` to `["dotnet"]`
- `RepoName` changed from `"vmr"` to `"dotnet/dotnet"`
- `DisplayName` remains `"dotnet/dotnet (VMR)"`
- The special-case `dotnet`/`dotnet/dotnet` → `vmr` lookup is removed; direct key matching and existing short-name extraction handle the repo now
- String literals and doc comments referring to the profile key now use `dotnet`; `VMR` is preserved where it describes the product/build context

## Impact

- `GetProfile("dotnet")` now resolves by direct key match
- `GetProfile("dotnet/dotnet")` now resolves through the existing short-name extraction path
- `KnownRepos` and overview listings show `dotnet` instead of `vmr`
- `profile.RepoName` is now `dotnet/dotnet`
- 196 CI knowledge tests pass

### 2025-07-25: Document MCP resources and idempotent annotations

**By:** Lambert
**Requested by:** Larry Ewing

## Decision

- Add an `MCP Resources` section to `README.md` after the MCP tools table, documenting `ci://profiles` and `ci://profiles/{repo}` as client-discoverable mirrors of `helix_ci_guide`
- Add an `Idempotent annotations` row to the README's Context-Efficient Design table so the retry/cache safety contract is documented alongside other client-facing efficiency choices
- All read-only MCP tools should carry `Idempotent = true`; `helix_download` and `helix_download_url` keep `Idempotent = true` without `ReadOnly = true` because they write files to disk

## Why

- MCP resources deserve the same discoverability in docs as tools because agents can consume them directly
- `Idempotent = true` is a durable MCP convention for this repo: it lets clients retry and cache tool calls safely, and it should remain paired with all read-only tools going forward
- README documentation should reflect that safety contract so agents and humans see the same routing guidance

### 2026-03-13: AzDO list tools return truncation metadata via LimitedResults<T>

**By:** Ripley

## Context

User feedback from steveisok showed agents were hitting list/search caps and treating capped results as complete.

## Decision

For MCP-only list-style AzDO tools that previously returned raw lists (`azdo_builds`, `azdo_artifacts`, `azdo_test_results`, `azdo_test_attachments`), wrap results in a `LimitedResults<T>` type that:
- serializes as an object with `results`, `truncated`, and an optional `note`
- still implements `IReadOnlyList<T>` so direct C# callers and tests keep normal list ergonomics

## Why

A plain top-level object gives agents an explicit truncation signal, but changing local call sites to a non-list type would create unnecessary churn. The wrapper preserves runtime/test compatibility while making capped MCP responses self-describing.

## Follow-up

If more MCP tools need truncation metadata, prefer reusing this wrapper pattern instead of inventing per-tool response shapes.

### 2026-03-13: AzDO auth should use a narrow, scheme-aware credential chain (consolidated)

**By:** Dallas, Ripley
**What:** Keep AzDO auth layered as `AZDO_TOKEN` → `AzureCliCredential` → `az account get-access-token` subprocess → anonymous, without `DefaultAzureCredential`. `IAzdoTokenAccessor` should return scheme-aware `AzdoCredential` metadata plus `AuthStatusAsync` output so callers can choose `Basic` for PATs, `Bearer` for Entra/JWT sources, and safe auth-status reporting. Fallback Azure CLI / `az` credentials should cache with a refresh deadline, invalidate on 401/403, and derive `AzdoCredential.CacheIdentity` from stable auth-source identity (`auth path` + stable JWT claims such as `tid`/`oid`/`appid`/`sub`) rather than raw token bytes so `CachingAzdoApiClient` can partition AzDO cache keys by auth context after the first successful authenticated response. `AzdoCredential.Token` stays the on-wire payload, `DisplayToken` remains compatibility-only, `AZDO_TOKEN_TYPE=pat|bearer` remains the explicit override ahead of the JWT heuristic, and token-like material in unexpected AzDO error snippets must be redacted before surfacing. Keep cache-root selection stable via `CacheOptions.CacheRootHash` while leaving `AuthTokenHash` as the mutable AzDO cache-key partition, and seed that AzDO auth hash immediately after credential resolution so the first cached AzDO read is already partitioned correctly.
**Why:** The existing az CLI fallback is the proven escape hatch for WSL/libsecret failures, while a deliberately narrow Azure.Identity probe avoids the latency and opaque failure modes of `DefaultAzureCredential`. Expiry-aware fallback caching and 401/403 invalidation keep long-running CLI/MCP hosts from sticking to stale developer tokens, and stable auth-source identity keeps cache isolation refresh-safe while preventing one authenticated AzDO context from reusing another context's private cached responses. Safe `auth-status` metadata gives operators a supported way to inspect the active auth path without probing AzDO directly, and the stricter token/string handling closes the remaining low-effort leak and ambiguity cases from the STRIDE/CCA follow-up review. Separating stable cache-root selection from mutable AzDO key partitioning prevents misleading cache-root reporting and avoids an initial unpartitioned AzDO cache lookup before auth context is known.

### 2026-03-13: MCP tool descriptions should stay short and defer repo-specific guidance to helix_ci_guide (consolidated)

**By:** Ripley
**What:** The project first embedded repo-specific CI knowledge into selected MCP tool descriptions to improve routing, then tightened 17 `Description()` attributes to ≤35 words and moved repo-specific patterns/task-name guidance back into `helix_ci_guide`. Keep only high-value warnings and routing hints in tool descriptions; keep detailed repo workflows in the on-demand CI guide.
**Why:** Description text is loaded into every agent context whether the tool is used or not, so verbose repo-specific guidance creates a permanent token tax. Short behavioral descriptions still steer tool selection, while `helix_ci_guide` carries the richer repo detail only when a caller actually needs it.

### 2026-03-13: Helix MCP tool names should reflect actual scope (consolidated)

**By:** Ripley
**What:** Renamed MCP-visible tool names from `helix_test_results` to `helix_parse_uploaded_trx` and from `helix_search_log` to `helix_search` across tool registration, docs/help text, tests, and README while keeping the internal/CLI names that still fit (`ParseTrxResultsAsync`, `SearchLog`, `search-log`) stable.
**Why:** The earlier names were context traps: `helix_test_results` sounded like the universal first stop even though most repos publish structured results to AzDO, and `helix_search_log` no longer matched a tool that can search both console logs and uploaded files. Scope-accurate names improve agent discoverability without unnecessary internal churn.

### 2026-03-14: PR #29 review round 3 — AzDO auth cache identity updates

**By:** Ripley

## Decision

Store the resolved AzDO auth cache identity on `CacheOptions` and let both `CachingAzdoApiClient` and `AzdoApiClient` update the shared auth context through `UpdateAuthContext(...)`.

## Why

The auth-hash partition can no longer be treated as write-once because long-running processes may observe Azure CLI or `az` credential changes. Keeping the last resolved identity next to the derived hash lets both layers react consistently when the principal changes, while `CacheStoreFactory` now keys stores strictly by the stable effective cache root so auth-key churn never creates duplicate `SqliteCacheStore` instances for the same database path.
