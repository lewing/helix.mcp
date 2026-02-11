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

### 2025-07-17: Documentation improvement proposal — all public APIs need XML doc comments, llmstxt needs MCP coverage, README needs install/troubleshooting sections

**By:** Kane
**What:** After a full audit of README.md, XML doc comments, llmstxt output, and MCP tool descriptions, I've identified 15 specific improvements needed across these areas. The biggest gaps are: (1) llmstxt command doesn't document MCP tools at all, (2) public records in HelixService lack XML doc comments, (3) README has no installation instructions despite the tool being packaged as a dotnet tool, (4) no LICENSE file exists despite MIT claim in README, (5) llmstxt output has a whitespace indentation bug from the raw string literal.
**Why:** LLM agents using the `llmstxt` command get an incomplete picture of the tool's capabilities (no MCP info). Developers discovering the tool can't install it (no `dotnet tool install` instructions). Missing XML doc comments on public records hurt IntelliSense and generated API docs. These are all low-effort, high-impact fixes.

### 2025-07-14: MatchesPattern exposed via InternalsVisibleTo
**By:** Lambert
**What:** Changed `HelixService.MatchesPattern` from `private static` to `internal static` and added `<InternalsVisibleTo Include="HelixTool.Tests" />` to `HelixTool.Core.csproj` to enable direct unit testing.
**Why:** Cleanest approach for testing private logic — no reflection, no test helpers, no public API surface change. The method stays invisible to external consumers while being testable. If anyone adds more internal methods to Core, they're automatically testable too.

### 2025-07-14: Caching Strategy for Helix API Responses and Artifacts

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
