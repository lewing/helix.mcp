# Lambert — History (Condensed)

## Executive Summary

**Role:** Integration testing, CCA follow-up fixes, code review patterns, test architecture.

**Focus (2026-06-24 through 2026-07-20):** Strict-mode implementation (PR #83–87), CCA cycles, test patterns, anticipated schema-reduction validation work.

---

## 2026-07-20: Tiered outputSchema Recommendation — PEER REVIEW

**Context:** Dallas refined "flatten all" → tiered (FLATTEN 10 / KEEP 3 / LEAVE 12).

**Anticipated Lambert work (pending user approval):**
- **Integration test:** Confirm tools/list shrinks ~5,450 bytes after tiered implementation
- **StructuredContent validation:** Verify responses still emit StructuredContent despite flattened schema
- **Test file patterns:** Reuse existing MCP_* or StructuredContent_* tests

**See also:** .squad/decisions/decisions.md (Dallas decision, merged from inbox 2026-07-20).

---

## 2026-06-24: Strict-Mode Implementation (PR #83–87)

### PR #83 Review — Issue #81 Stage A
**Blocking bug found:** Missing TypeInfoResolver in Program.cs → first-request crash (InvalidOperationException on read-only JsonSerializerOptions).
- SDK sets TypeInfoResolver auto when null, but only before MakeReadOnly
- If MakeReadOnly called first, auto-assign fails on read-only instance
- **Fix:** Add `TypeInfoResolver = new DefaultJsonTypeInfoResolver()` to both Program.cs files

**Non-blocking:** Alias removal, result→resultFilter, arguments.Remove(), existing bindings all correct.

**Tests:** 8 tests cover 7 scenarios + 2 alias-collision regressions.

### PR #87 — CCA Follow-Up Cleanup (#83–#85)
**Real bugs fixed (by Lambert under lockout):**
1. **Alias-removal hole** (McpServerOptionsExtensions.cs:75): Used `continue` on canonical present → alias key never removed. Fix: always remove alias key, skip only canonical-value promotion.
2. **Missing newline** (line 199): Single-unknown path had no trailing `\n` → concatenated "Did you mean: X?Allowed parameters...". Fix: `sb.AppendLine()`.

**Tests added:** 2 new alias-collision regression tests; message-format tests updated with `\n`-transition assertions. (1450 → 1452 passed; 2 skipped).

**CCA cycle pattern:** CCA finds bug → Ripley (author) locked out → Lambert fixes + tests under lockout → Larry reviews CCA second pass → Larry merges.

---

## 2026-06-01 through 2026-06-24: Param Plumbing & Strict-Mode Architecture

### PR #75 — Numeric Alias Coercion (Gap Fix)
**Finding:** Numeric `build_id` values (JSON numbers) fail binding to string parameter `buildIdOrUrl`.
**Fix:** Implement `CoerceToStringElement()` in CallToolFilter; validate upstream value kinds.
**Lesson:** When binding alias parameters, consider jsonElement.ValueKind early. Test all upstream kinds, not just expected types.

### MCP 1.4.0 Bump Safety
Decompiled Microsoft.Extensions.AI.Abstractions 10.5.2 (shared by MCP 1.3.0 and 1.4.0):
- UnmappedMemberHandling.Disallow check gates on `!HasCustomParameterBinding`
- Our tools (all plain value params, no DI) → HasCustomParameterBinding = false → check WOULD run
- No changes to CallToolFilter API, McpException shape, ProtocolTool.InputSchema structure, or alias-normalization paths
- **Bump to 1.4.0 is safe.** Zero migration work required.

---

## Test Architecture Patterns (Reusable)

### `[Theory] + [InlineData]` Contract Test Pattern
Per-param coverage with high test count, low LOC:

**URL construction:**
```csharp
[Theory]
[InlineData("main", "branchName=main")]
[InlineData("refs/heads/main", "branchName=refs%2Fheads%2Fmain")]
public async Task ListBuildsAsync_Branch_AppearsInUrl(string branch, string expectedPart) { }
```

**Cache key discrimination:**
```csharp
[Theory]
[InlineData("main", "develop")]
public async Task ListBuildsAsync_DifferentBranch_DistinctCacheKeys(string b1, string b2) { }
```

### Redundant-Test Removal Heuristic

Test is redundant iff:
1. Tests only a normalization RULE (not the layer's behavior), AND
2. Same rule is now covered by a direct unit test of the shared normalizer

**Safe to remove:** Normalization unit tests if centralizer has own coverage.
**Must keep:** Tests that verify URL construction, cache TTL, cache hit/miss behavior (layer tests, not rule tests).

**Practical rule:** Keep if test would fail after correct normalizer but broken call site. Remove only if would pass by testing normalizer alone.

---

## Prior Work Archive

See `.squad/agents/lambert/history-archive.md` for:
- PR #66–#78 exception handling, parameter standardization, caching patterns
- Cache normalization, exit codes, doc coupling learnings
- Array safety (use IReadOnlyList/FrozenSet, not readonly string[])
- SQLite test flakiness pre-existing issues
- Extensive test patterns and code review feedback cycles

---

## 2026-07-28: helix_find_files workItem schema consistency tests

### Context

User reported hard schema-rejection error: `helix_find_files` was missing `workItem` while all 6 sibling work-item tools had it. Ripley was concurrently implementing the fix.

### Learnings

**Test infrastructure:**
- `McpToolDescriptionTests.cs` is the home for MCP schema/contract tests. It already had `McpServerToolParameters_HaveDiscoverableDescriptions` using reflection over `HelixMcpTools`, `AzdoMcpTools`, `CiKnowledgeTool`. Adding a `[Theory]` with `[InlineData]` for each expected tool is the right extension point for schema consistency tests.
- The existing test class exposes `GetMcpToolMethods()` and `GetToolName()` as private statics — add new tests to the same class to reuse them without changing visibility.
- `HelixMcpToolsTests.cs` is the home for per-tool behavioral tests. Setup pattern: `IHelixApiClient` mock via NSubstitute, `HelixService` + `HelixMcpTools` wired together.

**Reflection-based behavioral tests for anticipated parameters:**
- When testing behavior that depends on a parameter not yet in the codebase, use `method.GetParameters().FirstOrDefault(p => p.Name == "X")` as a guard inside the test. If the parameter is absent, the test fails early with a clear message. If present, the test proceeds with reflection-based invocation using a name-based arg selector (`p.Name switch { ... }`) — this is position-independent and survives Ripley inserting the param at any slot.
- `Task<T>` return types can be cast directly from `method.Invoke(...)` if you know the concrete generic type.

**Parameter ordering hazard:**
- When a new optional parameter is inserted before existing optional parameters in a method, callers using positional args silently bind to the wrong slot (or fail to compile). Two pre-existing tests (`FindFiles_ReturnsValidJsonWithScanResults`, `FindFiles_WildcardPattern_ReturnsAllFiles`) broke this way — `"*.trx"` bound to the new `workItem` slot instead of `pattern`. Fix: use named args (`pattern: "*.trx"`) for all optional params after the first.

**`ScannedItems` adjustment:**
- Ripley's implementation sets `ScannedItems = string.IsNullOrEmpty(workItem) ? maxItems : 1` when workItem is provided. Tests that assert `ScannedItems == 50` remain valid when no workItem is given.

---

## 2026-07-28 — PR #117 guard hardening (workItem shape validation)

### Learnings

**Reflection-based contract tests must assert parameter SHAPE, not just presence.**
Checking `p.Name == "workItem"` only proves the parameter exists; it does not prevent a future tool from declaring `string workItem` (required), `int workItem`, or `string workItem = "default"`, all of which would pass the old guard while still breaking callers. The correct assertion is all three:
1. `p.Name == "workItem"` — parameter exists
2. `p.ParameterType == typeof(string)` — correct CLR type
3. `p.HasDefaultValue && p.DefaultValue is null` — optional with a null default

Each distinct failure path should carry a self-contained message naming the tool and quoting the required declaration (`string? workItem = null`) so the author has an actionable fix without reading the test body.

---

## Known Patterns & Conventions

- **Validation layers:** Validate at user boundary (CLI/MCP) → canonicalize at semantic boundary (cache key, URL) → share algorithm across layers
- **Silent param drop detection:** Audit tool method signature vs REST API capabilities; missing params + missing URL plumbing produce identical symptom
- **Cache key normalization:** Always normalize null/whitespace/defaults to identical representations before hashing
- **External PR reviews:** Clear feedback → merge promptly → file follow-ups ourselves
- **CCA follow-up cycle:** Expects author lockout; fixer can be different agent; ensure fix closes entire bug class, not just test case
- **Schema consistency guard:** Use `[Theory] + [InlineData(toolName)]` in `McpToolDescriptionTests` to explicitly enumerate the set of tools that must share a parameter; fails loudly when a new tool is added without it

## 2026-07-28 — helix_find_files workItem parameter test coverage
Added schema-consistency test (WorkItemScopedHelixTools_HaveOptionalWorkItemParameter) covering 7 work-item-scoped Helix tools. Added 2 behavioral tests for workItem fast path. Fixed 2 pre-existing tests after parameter ordering change. Full suite: 1506 passed. Approved by Dallas; assigned non-blocking cleanup (simplify reflection-based tests, remove stale comments, harden schema test).

## 2026-07-28: PR #117 Review Round — Guard Hardening (lewing-fix-find-files-workitem-param)

### Task
Route final review comment on helix_find_files workItem parameter — hardening schema-consistency guard assertions.

### Fix Applied
**Hardened HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped** reflection guard:
- Added parameter type assertion (`typeof(string)`)
- Added optionality assertion (`HasDefaultValue && DefaultValue is null`)
- Each with distinct, actionable failure message

Prevents future regressions from wrong-type or required parameters slipping through while still violating MCP contract.

**Commit:** 445abcb  
**Test outcome:** 1500/0 failed / 2 skipped  
**Branch:** lewing-fix-find-files-workitem-param

### Lesson: Skill Extraction Timing
**TEAM LESSON (cross-agent):** Skill was extracted mid-session and captured the INTENDED design. Subsequent discovery-based implementation (Lambert) replaced the referenced method with superior pattern, leaving skill pointing at code that never existed. Consider deferring skill extraction until after review completion to capture actual shipped behavior.

---

## 2026-08-20 — T1–T4 test gates + G4 for MCP C# SDK 1.4.0 → 2.2.0 migration

Implemented Dallas's mandatory test gates for Ripley's SDK bump (`Directory.Packages.props`
1.4.0→2.2.0, `Program.cs` `SessionMode = Stateless`). Full report:
`.squad/decisions/inbox/lambert-csharp-mcp-sdk-tests.md`. Result: T1/T2/T4 pass clean; T3
passes but with a documented production-seam gap (escalated, not resolved unilaterally).
Targeted 26/26 pass; full suite 1528 total/1526 passed/2 skipped (pre-existing)/0 failed/0
new skips. Zero production files touched.

### Reusable technique: TCS-gating a substituted async dependency to prove a race-prone assertion
When asserting "at least one notification arrived *during* an in-flight async call" (T1's
core claim), don't just await the whole call and then check the notification list — that
proves the notification arrived *eventually*, not that it survived the transport while the
request was still open. Gate the one substituted dependency
(`IHelixApiClient.ListWorkItemFilesAsync` here) behind a
`TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously)`, await a
"first notification received" signal *before* completing the TCS, then assert and only
afterward let the call finish. This is the difference between "the notification eventually
showed up" and "the notification genuinely crossed the wire while the request was still
in flight" — the latter is what T1 actually requires.

### Reusable technique: `TestServer` + real `McpClient`/`HttpClientTransport` for MCP protocol tests
SDK 2.2.0's `HttpClientTransport(HttpClientTransportOptions, HttpClient, ILoggerFactory?,
bool ownsHttpClient)` constructor accepts *any* `HttpClient`, including
`Microsoft.AspNetCore.TestHost.TestServer.CreateClient()` (via `host.GetTestServer()` — note:
`IHost` itself has no `.GetTestClient()`; you must call `.GetTestServer().CreateClient()`).
This gets you a real MCP client speaking real Streamable-HTTP JSON-RPC/SSE against a real
`AddMcpServer().WithHttpTransport(...)` registration, entirely in-process — no sockets, no
Kestrel, no upstream SDK's heavier `KestrelInMemoryTest`/`KestrelInMemoryConnection` fixture
needed. Gotchas found by trial: `app.UseRouting(); app.UseEndpoints(e => e.MapMcp());` (not
bare `app.MapMcp()`, which is a `WebApplication`-only extension) — and this requires
`services.AddRouting()` explicitly in `ConfigureServices` when using `HostBuilder()
.ConfigureWebHost(...)` rather than `WebApplication.CreateBuilder()` (which adds routing
implicitly). `McpClient.CallToolAsync(...)` returns `ValueTask<CallToolResult>`; store it in
a variable and consume with `.AsTask().WaitAsync(timeout)` exactly once — don't await a
`ValueTask` twice.

### Reusable technique: non-destructive pre-bump baseline via `git worktree add --detach`
To measure a "before" value (G4's tools/list byte count) without touching the working tree
or doing anything destructive: first check `git diff HEAD` for the file(s) in question — if
the only uncommitted change *is* the bump itself, HEAD already **is** the pre-bump baseline,
no separate branch lookup needed. Then, from the *main* repo directory (`git worktree list`
to find it), run `git worktree add --detach <scratch-path> <baseline-commit>`, build there,
run the existing measurement harness against both trees, and `git worktree remove
<scratch-path>` when done. Zero risk to the working tree's state.

### Finding: the SDK's own `SessionMode`/`Stateless` default already flipped once in this exact migration
Verified by direct instantiation (not just XML-doc prose) against each package version in a
throwaway console app *outside* the repo tree (CPM in `Directory.Packages.props` rejects
explicit `<PackageReference Version="...">` inside the repo tree — NU1008 — so scratch
verification projects for a specific package version must live outside it):
`new HttpServerTransportOptions().Stateless` (bool, 1.4.0) == `false` (stateful by default);
`new HttpServerTransportOptions().SessionMode` (enum, 2.2.0) == `Stateless` by default. This
means Ripley's explicit `SessionMode = Stateless` pin isn't merely restating today's default
— it protects against a *third* flip on some future SDK bump, which is the correct framing
for why T3 pins intent rather than "just checking the default holds."

### Known limitation carried forward: T3 cannot exercise the literal `Program.cs`
`src/HelixTool.Mcp/Program.cs` (top-level statements, no `partial class Program` marker, no
`InternalsVisibleTo`, unconditional blocking `app.Run()`) cannot be driven via
`WebApplicationFactory<Program>` or reflection. T3 reconstructs the identical one-line
registration in test code and proves the *mechanism*, but a future edit to Program.cs's
`SessionMode` line would not be caught by this test. Escalated the standard, behavior-neutral
`public partial class Program;` seam to Dallas rather than adding it myself (out of scope
for a Tester per my boundaries) — see the decision doc for the ask.

---

## 2026-08-20 — F1/F3 final-review gates (dallas-csharp-mcp-sdk-final-review.md)

### F1: hermetic real-host tests without forcing/clearing ambient state
The rejected artifact (`HttpTransportSessionModeTests.cs`) failed only when the ambient
`HLX_API_KEY` was set, because it sent no `X-Api-Key` header while the real host's
`app.UseApiKeyAuthIfConfigured()` (read once, at pipeline-build time) had installed
`ApiKeyMiddleware`. The correct fix is **not** to clear or force the env var (that would stop
testing one of the two worlds) — it's to have the test read the *same* ambient variable the
middleware reads, at the same time, and mirror its exact non-empty check when deciding whether
to attach the header. This makes the test agree with whatever the real host actually did,
in both worlds, instead of asserting on one specific ambient state.

### Reusable technique: shared non-parallel xUnit collection for ambient-env-var tests
Any test class that mutates or reads a *process* environment variable (not scoped per-instance)
races with xUnit's default cross-class parallelism. This repo's established fix
(`AzdoTokenEnv`, `FileSearchConfig` collections) is a `[CollectionDefinition("Name",
DisableParallelization = true)]` marker + `[Collection("Name")]` on every participating class.
Added `HlxApiKeyEnvCollection.cs` defining `HlxApiKeyEnv` and joined all three classes that
touch `HLX_API_KEY` (`ApiKeyMiddlewareTests`, `HttpTransportSessionModeTests`, the new
`ApiKeyScopedRequestIsolationTests`) to it. Generalizable: **any future test touching a
process-wide ambient value (env var, `CultureInfo.CurrentCulture`, static mutable config, etc.)
should join or create a `DisableParallelization` collection**, not just save/restore in
ctor/Dispose — save/restore alone is necessary but not sufficient once other classes can run
concurrently.

### Reusable technique: prove per-request DI isolation with a deterministic recording `WebApplicationFactory`
For F3/G7 (proving `HttpContextHelixTokenAccessor` → `IHelixApiClientFactory.Create` →
`CacheOptions.ComputeTokenHash` → `ICacheStoreFactory.GetOrCreate` are all resolved fresh
per-request, with zero cross-request leakage), the technique that worked without touching
production code:
1. Subclass `WebApplicationFactory<Program>`, set the ambient `HLX_API_KEY` to a **fixed test
   constant** in the constructor (before the host is ever lazily built) and restore the
   original value in `Dispose(bool)` — this makes the fixture's auth-enabled behavior
   independent of whatever the *real* ambient key is, so the class doesn't depend on how it's
   invoked (works identically whether the outer suite run has `HLX_API_KEY` set or not).
2. Override `ConfigureWebHost` → `ConfigureServices` and replace only the two seams Program.cs
   itself already exposes as request-scoped extension points (`IHelixApiClientFactory`,
   `ICacheStoreFactory`) via `services.RemoveAll<T>()` + `services.AddSingleton<T>(recordingInstance)`
   (`Microsoft.Extensions.DependencyInjection.Extensions`). Both replacements are themselves
   singletons so their recorded call history persists across every request the shared factory
   instance serves — exactly what's needed to assert "request 2 recorded exactly its own token,
   not request 1's."
3. Give each recording fake a `ConcurrentQueue<T>` (thread-safe, preserves call order) and
   expose it as `IReadOnlyList<T>` for assertions; back `IHelixApiClientFactory.Create` with an
   NSubstitute-configured fake `IHelixApiClient` (only `GetJobDetailsAsync`/`ListWorkItemsAsync`
   need stubbing — an empty work-item list short-circuits `HelixService.GetJobStatusAsync`
   before it needs `GetWorkItemDetailsAsync`), and back `ICacheStoreFactory.GetOrCreate` with a
   fully in-memory no-op `ICacheStore` (avoids any real SQLite/disk I/O in a "smoke" test).
4. Use two separate `HttpClient`s from `_factory.CreateClient()` (same underlying host/DI
   container, real per-request scoping) each with a distinct `Authorization: Bearer` value,
   drive each through a real `McpClient`/`HttpClientTransport` `tools/call` (not raw JSON-RPC,
   not `StatelessMcpTestHost`'s singleton-host reconstruction) sequentially, then assert the
   recorded token/hash sequences equal `[tokenA, tokenB]` / `[hash(tokenA), hash(tokenB)]` and
   that the pair is mutually distinct.
5. **Keep the auth-gating facts (401/401/200) tool-free.** `HelixMcpTools`/the recording
   factories are only DI-resolved when a `tools/call` actually dispatches to a tool — never
   during `initialize`. Sending only raw `initialize` requests in the gating facts (mirroring
   F1's pattern) keeps the isolation fact's recorded-call count deterministic (exactly 2) even
   though four `[Fact]`s share one fixture/host instance, without needing to reason about xUnit
   fact execution order.

This pattern (fixed-value deterministic auth + `RemoveAll<T>`/`AddSingleton<T>(recordingFake)`
via `ConfigureWebHost`) is generalizable to any future "prove request N's production DI resolves
independently of request N-1" gate against a real `WebApplicationFactory<Program>` host, without
ever needing to change production architecture to add a test seam.

### Environment quirk (not a code issue): SDK/runtime mismatch requires `DOTNET_ROLL_FORWARD=LatestMajor`
This sandbox has only the .NET 11 preview runtime installed while the solution targets
`net10.0`; `dotnet test`/`dotnet run` fail with "You must install or update .NET to run this
application" unless `DOTNET_ROLL_FORWARD=LatestMajor` is set in the environment for the test
invocation. Not a repo bug — just a note for future agents running tests in this exact worktree
image, so they don't mistake it for a build regression.

---

## 2026-08-26: Snapshot Eval-Mode PoC — Test Implementation & Reviewer Gate

**Context:** Dallas approved `HLX_EVAL_SNAPSHOT` PoC. Ripley owns production; Lambert owns tests and review.

### What was tested
44 new tests across three files:
- `CacheOptionsTests.cs` (5): EvalMode property, `GetEffectiveCacheRoot` bypass for absolute/relative paths.
- `SqliteCacheStoreTests.cs` (17): TTL bypass, no-op eviction, no-op writes, `last_accessed` mutation guard, schema mismatch throw, WAL/SHM cleanup, normal-mode regression.
- `SnapshotEvalModeTests.cs` (new, ~22): `OfflineAzdoApiClient`/`OfflineHelixApiClient` stubs (all methods throw "eval mode"), composition (cache-hit/miss, path resolution), end-to-end CI-evidence scenario.

### Key learnings

**1. `TimeSpan.Zero` TTL race with background eviction**
`SqliteCacheStore` fires `_ = Task.Run(() => EvictExpiredAsync())` on construction (normal mode only). Eviction runs `DELETE WHERE expires_at <= @now`. Writing with `TimeSpan.Zero` (`expires_at = now`) races with this task — if the task runs AFTER the write, it deletes the just-inserted row. Fix: `await Task.Delay(30)` between store creation and the zero-TTL write lets eviction drain on the empty DB first. Since eviction is fire-and-forget (runs once), subsequent writes are safe.

**2. WAL/SHM re-creation by SQLite WAL mode**
Production code deletes stale WAL/SHM files BEFORE opening the connection. However, `SqliteCacheStore` sets `PRAGMA journal_mode=WAL` inside `InitializeSchema()`. This causes SQLite to recreate WAL/SHM files on every connection open. Tests must NOT assert `!File.Exists(walPath)` after the store is open — that assertion will always fail in WAL mode. Instead assert that the store opens without throwing and data is readable.

**3. Eval mode requires pre-existing valid DB**
Eval mode `InitializeSchema()` throws "schema version mismatch: expected 1, found 0" on any DB whose `PRAGMA user_version` is not 1. An empty directory has no DB (version = 0). Tests simulating "cache miss in eval mode" must pre-seed the DB with a schema-only normal-mode writer first, then open the eval store. The seed writer creates the schema but writes no data rows — eval store then sees a valid (empty) DB and can open successfully.

**4. Ripley fixed the `GetArtifactAsync` mutation bug proactively**
The pre-session bug report (missing `if (!_options.EvalMode)` guards on `UPDATE last_accessed` and `DELETE` in `GetArtifactAsync`) was already fixed by the time tests ran. Both guards are present in the committed code. The mutation test now correctly passes.

**5. `OfflineAzdoApiClient` methods throw synchronously**
Methods use `=> throw Blocked()` pattern (throw before returning Task). xUnit's `CS0619` obsolete error on `Assert.Throws<T>(Func<Task>)` requires upgrading to `await Assert.ThrowsAsync<T>(...)` with `async Task` test methods. `ThrowsAsync` correctly catches synchronous throws from Task-returning methods.

**6. Reviewer verdict**
**APPROVE** — All 1614 tests pass (1612 pass, 2 pre-existing skips). All acceptance criteria are met by the production implementation. No high-confidence correctness defects found in the final code.
