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

### 2025-07-14: Caching Strategy for Helix API Responses and Artifacts *(superseded by 2025-07-18 Revised Cache TTL Policy)*

**By:** Dallas
**What:** A two-tier caching system in `HelixTool.Core` — an in-memory LRU cache for API metadata and a disk cache for downloaded artifacts, keyed by `{jobId}/{workItem}/{fileName}`, with job-completion-aware invalidation.
**Why:** Helix API calls are slow (especially when fanning out across work items), and downloaded artifacts (binlogs, TRX files) are large and immutable once a job completes. Caching avoids redundant network I/O in both the short-lived CLI (repeated commands during a debugging session) and the long-lived MCP server (same job queried multiple times by an agent). The design below is opinionated, concrete, and implementable.

### 2025-07-18: Revised Cache TTL Policy — Per-Data-Type × Job-State Matrix, Bounded Lifetimes, Automatic Eviction

**By:** Dallas
**What:** Replaces the previous caching strategy's TTL model (indefinite for completed, blanket 60s for running) with a granular TTL matrix per data type and job state, plus automatic disk eviction.
**Why:** Larry correctly identified two problems: (1) "cache forever" for completed jobs is a disk leak — there's no reason to keep months-old job data around, and (2) a single 60s TTL for running jobs doesn't match real debugging behavior. Console logs grow continuously. File listings change as work items produce artifacts. Job details change state. These need different cache lifetimes. The revised design treats each data type independently.

---

## Changes from Previous Design

### 1. TTL Matrix

| Data Type | Running Job | Completed Job |
|---|---|---|
| **Job details** (name, queue, creator, state) | 15s | 4h |
| **Work item list** (names, counts) | 15s | 4h |
| **Work item details** (exit code, state per item) | 15s | 4h |
| **File listing** (per work item) | 30s | 4h |
| **Console log content** | **NO CACHE** | 1h |
| **Downloaded artifact** (binlog, trx, files on disk) | Until eviction (see §3) | Until eviction (see §3) |
| **Binlog scan results** (FindBinlogsAsync) | 30s | 4h |

### 2. Console Logs — Never Cache While Running

Console logs for running work items are append-only streams. Any cached version is stale the moment it's stored. The previous design's 60s TTL would serve stale logs to a user who's actively tailing output.

**Decision:** `GetConsoleLogContentAsync` and `DownloadConsoleLogAsync` bypass the cache entirely when the job is not yet finished. For completed jobs, cache for 1 hour — the log is immutable at that point but doesn't need to live in memory forever.

To determine job state: the cache layer checks whether a `JobSummary` for that job ID has a non-null `Finished` timestamp. If the job summary isn't cached yet, one API call is made to get it (and that call itself is cached per the matrix above).

### 3. Completed Job Max TTL: 4 Hours (Memory), 7 Days (Disk)

The previous design said "indefinite" for completed jobs. Revised:

- **Memory cache:** 4-hour sliding expiration for all completed-job metadata. A debugging session rarely spans more than a few hours. After 4h of no access, entries are evicted.
- **Disk cache:** Downloaded artifacts (binlogs, trx files) live for 7 days from last access. After that, automatic eviction (see §4).

Rationale: Completed jobs don't change, but they don't need to live in cache indefinitely either. 4h covers a debugging session. 7 days covers "I'll come back to this tomorrow." Beyond that, re-fetching is fine.

### 4. Disk Cache Eviction Policy

Previous design: manual `cache clear` only. Revised — automatic eviction on two triggers:

1. **LRU with max size:** Disk cache directory has a configurable max size (default: 500 MB). When exceeded, oldest-accessed files are deleted first. Size check runs at startup and after every download.
2. **TTL-based cleanup:** Files older than 7 days (by last access time) are deleted. Cleanup runs at startup.
3. **Manual override preserved:** `cache clear` command still works for immediate wipe.

Cache directory: `{TEMP}/hlx-cache/{jobId-prefix}/{workItem}/{fileName}`

Startup eviction is synchronous but fast — it's a directory scan, not API calls. The MCP server runs it once on startup. The CLI runs it before the first cache-writing operation.

### 5. Cache Key Design (Unchanged)

Same as previous design: `{jobId}/{workItem}/{fileName}` for artifacts, `{jobId}` for job-level metadata, `{jobId}/{workItem}` for work-item-level metadata. No changes here.

### 6. The "Log Grew Since Last Fetch" Problem

This is inherently unsolvable with caching — you can't cache a stream that's still being written to. The correct answer is: **don't cache it.** For running jobs, every console log request hits the API fresh.

If we want to optimize bandwidth in the future, we could add an `If-Modified-Since` or byte-range approach (fetch only bytes after the last-known position). But that's an optimization for later and depends on the Helix API supporting range requests. For now: no cache, full re-fetch. It's the only correct behavior.

### 7. Summary of What Changed

| Aspect | Previous | Revised |
|---|---|---|
| Completed job TTL | Indefinite | 4h memory, 7d disk |
| Running job TTL | Blanket 60s | 15-30s per data type; console logs: none |
| Console log caching (running) | 60s | **Disabled** |
| Disk eviction | Manual only | Automatic: 500MB cap + 7-day expiry |
| File listing TTL (running) | 60s | 30s |
| Job details TTL (running) | 60s | 15s |

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

