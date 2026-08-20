---
name: "mcp-sdk-major-upgrade"
description: "How to evaluate and land a major-version upgrade of the ModelContextProtocol C# SDK (e.g. 1.x → 2.x) in this repo"
domain: "dependencies, mcp, architecture"
confidence: "high"
source: "earned"
tools: []
---

## Context

Applies when the `ModelContextProtocol` / `ModelContextProtocol.AspNetCore` packages get a
new **major** version. Complements `dependency-audit` (which finds the gap) — this skill is
about deciding *how* to cross it. Earned on the 1.4.0 → 2.2.0 migration (2026-08-20).

Both MCP packages live in `Directory.Packages.props` and **must move in lockstep**.

## Patterns

### 1. Establish ground truth before reading anyone's analysis

Agent-written migration reports are unreliable on release metadata. Verify first — it costs
seconds:

```bash
# Real versions + real publish dates
curl -s "https://api.nuget.org/v3-flatcontainer/modelcontextprotocol/index.json"
gh release list --repo modelcontextprotocol/csharp-sdk --limit 12

# The authoritative breaking-change list
gh release view v2.0.0 --repo modelcontextprotocol/csharp-sdk --json body -q .body
```

Two traps seen repeatedly:

- **Invented dates.** Reports dated the release to "today" and the old baseline to
  "early <year>". Both wrong.
- **The missed servicing release.** Reports enumerate the new major's releases and skip the
  patch on the *old* major (e.g. 1.4.1). **That patch is the correct rollback target**, not
  the version you were pinned to.

### 2. Read the SDK source, not just the release notes, for anything you'll touch

Release notes state *what* changed; the source states *what the API now looks like*.
Confirm each API the repo actually uses still exists with the same shape:

```bash
# List public members of a type at the target tag
curl -s "https://raw.githubusercontent.com/modelcontextprotocol/csharp-sdk/vX.Y.Z/src/<path>.cs" \
  | grep -nE "^\s*(public|\[)"

# Find files at a tag without cloning
curl -s "https://api.github.com/repos/modelcontextprotocol/csharp-sdk/git/trees/vX.Y.Z?recursive=1" \
  | python3 -c "import sys,json; [print(e['path']) for e in json.load(sys.stdin)['tree'] if e['path'].startswith('src/') and 'Filters' in e['path']]"
```

Our standing surface to re-verify each time:
`McpServerFilters.Request.CallToolFilters` · `WithToolsFromAssembly(Assembly?, JsonSerializerOptions?)` ·
`WithResourcesFromAssembly(Assembly?)` · `WithStdioServerTransport()` · `WithHttpTransport(...)` ·
`IProgress<ProgressNotificationValue>` injection · `McpServerToolAttribute` · `McpException`.

### 3. Void-check reported risks against the repo before reasoning about them

One grep kills most "MODERATE" findings. Example: a reported .NET-8-minimum requirement
collapsed instantly — every project is `net10.0`.

```bash
grep -rn "TargetFramework" Directory.Build.props src/*/*.csproj
grep -rn "TreatWarningsAsErrors\|NoWarn" Directory.Build.props src/*/*.csproj
```

### 4. Hunt changed *defaults* — they are silent diffs

The highest-risk change in a major bump is often a default that flipped under a call site
that doesn't change textually. In 2.0.0, `HttpServerTransportOptions.SessionMode` flipped
stateful → `Stateless`, so `.WithHttpTransport()` changed behavior with a zero-line diff.

**Rule: configure the changed default explicitly in source, even when the new default is
the one you want.** One line buys a reviewable diff, immunity to a future flip,
operator-readable intent, and a home for the escape-hatch comment.

```csharp
.WithHttpTransport(options =>
{
    // Explicit, not inherited. SDK 2.x flipped this default (stateful in 1.4.0).
    options.SessionMode = HttpServerSessionMode.Stateless;
})
```

Then pin it with an assertion test (see §7 T3) so deleting the line fails CI.

### 5. Prefer a new enum over a retained bool proxy

When an SDK adds an enum alongside a legacy bool, **adopt the enum immediately**. The bool
usually cannot express the full state space and often encodes a wrong answer for the most
likely future need.

Concrete: `HttpServerTransportOptions.Stateless` is a proxy over `SessionMode`.
`Stateless = false` selects `Stateful` — which *refuses* new-revision clients with
`-32022 UnsupportedProtocolVersion`. The value a maintainer would actually reach for,
`StatefulForInitializeClients` (hybrid), is unreachable through the bool.

### 6. Test any backward-compat mode against capabilities you actually use

Before adopting a compat/hybrid mode, name the specific capability it preserves, then check
the repo uses it. Hybrid HTTP session mode exists solely to hand *sessions* to down-level
clients; sessions exist for `Mcp-Session-Id` affinity, standalone GET SSE, and
server→client requests (sampling / elicitation / roots). If you use none, hybrid is pure
cost — session affinity plus already-`[Obsolete]` idle-session tracking. Load-balancer
affinity is a one-way ratchet; don't acquire it speculatively.

Also: **spec breakage ≠ SDK breakage.** The 2026-07-28 spec dropped the `initialize`
handshake, but the SDK retains it as the down-level fallback (probe `server/discover`, fall
back to `initialize`). Read the SDK's compat shims before planning defensive work.

### 7. Gate on whatever every report called "no impact"

If independent reports converge on "no impact" for the one area genuinely adjacent to the
breaking change — and none of them ran anything — that convergence is the signal to demand
a test.

For this repo the recurring adjacency is **progress notifications under stateless HTTP**.
Progress is request-scoped (`TokenProgress` → `session.NotifyProgressAsync`, riding the POST
response SSE stream), so it *should* survive the loss of unsolicited server→client messages.
Prove it end-to-end with `Microsoft.AspNetCore.TestHost`; a silent loss here is user-visible
and invisible to the existing suite.

Standing test set for an MCP major bump:

| Test | Purpose |
|---|---|
| Progress-over-transport integration | Blocking. Assert `notifications/progress` reaches the client and `Progress`/`Total`/`Message` round-trip. |
| Structured-content invariant guard | Reflect over tool classes; assert every `UseStructuredContent = true` method returns an object type (non-object returns emit raw values since #1568). |
| Config-intent assertion | Resolve transport options from DI; assert the explicitly-configured default (§4) still holds. |
| Endpoint contract | Assert verbs disabled by the new mode return the expected status (e.g. GET/DELETE → 405 in stateless). |

Plus: full suite green, and a **`tools/list` before/after byte + shape diff** — any delta
must be explained, not absorbed. `McpToolsListPayloadTests` is the harness.

### 8. Read the whole warning list by hand

This repo sets no `TreatWarningsAsErrors`, so new SDK diagnostics will not self-report. The
2.x line uses `MCP9005` (deprecated Roots/Sampling/Logging), `MCP9006` (stateful-only
options), `MCP9007` (OAuth redirect delegate). **Any `MCP9xxx` appearing is a
stop-and-escalate** — it means the migration analysis missed a real dependency.

### 9. Stdio smoke test needs no GUI client

A "launch a real client" smoke gate doesn't require MCP Inspector or VS Code — MCP stdio
transport is newline-delimited JSON-RPC over stdin/stdout, so a ~30-line Python harness
does the job and is scriptable/repeatable:

```python
proc = subprocess.Popen(["dotnet", "<dll>", "mcp"], stdin=PIPE, stdout=PIPE, stderr=PIPE, text=True)
send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {...}})
# read one line of stdout -> initialize result
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
send({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {"name": "...", "arguments": {}}})
# read one line -> tool result
```

Pick a tool call that is read-only and needs no live credentials/network for a deterministic
gate — a tool whose description says "No API call made" (e.g. an auth-status check backed
by a local credential probe) is ideal.

### 10. Implementation notes for the standing test set (§7), from actually building it

Confirmed working end-to-end against SDK 2.2.0 (`src/HelixTool.Tests/`:
`ProgressOverStatelessHttpTests.cs`, `StructuredContentReturnTypeTests.cs`,
`HttpTransportSessionModeTests.cs`, `StatelessHttpMethodNotAllowedTests.cs`,
`StatelessMcpTestHost.cs`):

- **The progress-over-transport test needs a real client over a real transport, not just a
  real server.** `ModelContextProtocol.Client.HttpClientTransport(HttpClientTransportOptions,
  HttpClient, ILoggerFactory?, bool ownsHttpClient)` accepts *any* `HttpClient`, including
  `Microsoft.AspNetCore.TestHost.TestServer.CreateClient()` — obtained via
  `host.GetTestServer().CreateClient()` (note: `IHost` has no `.GetTestClient()` itself).
  This gets a genuine Streamable-HTTP/SSE MCP client-server round trip entirely in-process,
  no sockets, without the upstream SDK test suite's heavier `KestrelInMemoryTest` fixture.
  `app.UseRouting(); app.UseEndpoints(e => e.MapMcp());` is required (not bare `app.MapMcp()`,
  a `WebApplication`-only extension) — and if wiring the host via `HostBuilder()
  .ConfigureWebHost(...)` rather than `WebApplication.CreateBuilder()`, `services
  .AddRouting()` must be added explicitly or `UseRouting()` throws
  `InvalidOperationException`. `McpClient.CallToolAsync(...)` returns
  `ValueTask<CallToolResult>` — store it, then consume exactly once with
  `.AsTask().WaitAsync(timeout)`.
- **Gate the substituted async dependency behind a `TaskCompletionSource`, don't just await
  the whole call.** Proving "a notification arrived *while the request was still in flight*"
  (not just "eventually") requires the one substituted call in the tool's chain (e.g. the
  API client the tool wraps) to be a controllable `TaskCompletionSource<T>
  (TaskCreationOptions.RunContinuationsAsynchronously)` — await a "first progress received"
  signal, *then* release the TCS, *then* await the tool call's completion. Without the gate,
  a fast/synchronous fake races ahead of the SSE flush and the "≥1 notification" assertion
  either flakes or passes vacuously without proving anything about survival mid-flight.
- **Recovering a pre-bump baseline for the tools/list diff (§7) doesn't require a separate
  base-branch checkout if the bump is still uncommitted.** Check `git diff HEAD -- <bump
  files>` first — if that *is* the only uncommitted change, HEAD already **is** the
  pre-bump baseline. Otherwise, from the *main* repo directory (`git worktree list`), run
  `git worktree add --detach <scratch-path> <baseline-commit>`, build there, run the
  existing measurement harness against both trees, then `git worktree remove
  <scratch-path>`. Fully non-destructive; never touch the working tree's git state.
- **A version-specific scratch project to check an SDK default via direct instantiation
  (not just XML-doc prose, which can be ambiguous) must live outside any repo using Central
  Package Management** — a `<PackageReference Version="...">` inside a CPM-governed tree
  fails `NU1008`. Use a sibling scratch directory, and delete it when done.
- **Top-level-statements `Program.cs` blocks `WebApplicationFactory<Program>`- or
  reflection-based testing of the real app registration** unless it exposes a
  `partial class Program` marker (standard, behavior-neutral ASP.NET Core convention) or
  grants `InternalsVisibleTo` to the test project. A Tester should reconstruct the identical
  registration line in test code as a best-effort substitute and escalate the seam proposal
  rather than add it unilaterally — but flag in the report that the reconstruction cannot
  catch a regression to the real `Program.cs` line itself. (Resolved in this migration: a
  `public partial class Program;` marker was added at the bottom of `Program.cs`, so later
  gates — see below — could boot the real host directly instead of reconstructing it.)
- **Any test that mutates/reads a *process* environment variable (not just per-instance state)
  needs a `DisableParallelization` xUnit collection, not only ctor/Dispose save-restore.**
  xUnit runs different test classes in parallel by default; save-restore alone doesn't prevent
  a concurrently-running class from observing the mutated value mid-test. Pattern used here:
  `[CollectionDefinition("Name", DisableParallelization = true)]` marker class +
  `[Collection("Name")]` on every class touching that variable (see `HlxApiKeyEnvCollection`
  in `src/HelixTool.Tests/`, joined by every test class touching `HLX_API_KEY`).
- **Proving per-request DI isolation (e.g. "does request 2's scoped state leak from request
  1?") against a real `WebApplicationFactory<Program>` host, without changing production
  code:** set any ambient toggle the host reads at build time (e.g. an API key env var) to a
  fixed test constant in the factory's constructor — before the host is lazily built — and
  restore it in `Dispose(bool)`, so the fixture's behavior never depends on the *ambient*
  value. Then override `ConfigureWebHost`/`ConfigureServices` and replace only the DI seams the
  app itself already exposes as request-scoped extensibility points with recording test doubles
  (`services.RemoveAll<T>()` + `services.AddSingleton<T>(recordingInstance)`, both themselves
  singletons so a `ConcurrentQueue<T>` on each records every request's call in order). Drive two
  separate `HttpClient`s from the same factory (same host/container, real per-request scoping)
  through a real `McpClient`/`HttpClientTransport` tool call each, sequentially, then assert the
  recorded per-call values are distinct and in request order. See
  `src/HelixTool.Tests/ApiKeyScopedRequestIsolationTests.cs` for a worked example (token →
  cache-partition-hash isolation across `IHelixApiClientFactory`/`ICacheStoreFactory`).

### Schema objectness decides the structured-content envelope — not the CLR type

The single highest-value check in an MCP SDK major upgrade. SDK 2.x implements SEP-2106
("natural output schemas"), and the whole behavior hinges on one question the SDK asks about
each tool: **is the generated JSON schema an object schema?** Not "is the return type a
class", not "is it non-scalar" — is the *schema* an object.

- If yes: the natural value is emitted as `structuredContent` at every protocol version.
- If no: the SDK wraps it as `{"result": <value>}` for clients negotiating below the natural-
  schema protocol revision, and emits it unwrapped for clients at/above it.

**A type carrying a custom `[JsonConverter]` is opaque to System.Text.Json's schema exporter,
which emits the permissive schema `true`.** `true` is not an object schema. So any DTO with a
custom converter — a very common pattern for types that also implement `IReadOnlyList<T>`,
`IDictionary<,>`, or otherwise need hand-written serialization — silently lands in the
non-object branch and gets a version-dependent wire contract, plus an information-free
`outputSchema` for modern clients. **Enumerate every `[JsonConverter]`-annotated type reachable
from a tool return type before the upgrade; those are your suspects.**

Fix, smallest first: SDK 2.x's `[McpServerTool(OutputSchemaType = typeof(TSchemaMirror))]`
(requires `UseStructuredContent = true`) lets you declare a schema-only mirror record whose
properties match the converter's actual `Write` output. **A hand-maintained mirror needs its own
drift guard** — a test asserting property-name set equality in both directions between the
mirror and the real serialized output, since nothing in the compiler ties them together.
Removing the custom converter is cleaner but usually refactors a DTO you were told not to touch.

**Guard tests must assert on the protocol surface, never on CLR shape.** A test that checks
"the return type is not a scalar" passes for exactly the types that are broken. Create the real
`McpServerTool` and assert `ProtocolTool.OutputSchema` has `"type":"object"`; then stand up a
real server + real client **pinned to both an old and a new protocol version** and assert
`structuredContent` is identical across them (and matches `content[0].text`). The
version-parameterized theory is what catches an envelope that splits by client.

### `ProtocolTool.OutputSchema` semantics can change between majors — byte baselines lie

Where the legacy `{"result": …}` wrapping happens moved in SDK 2.x: the older SDK wrapped
**eagerly at tool-creation time**, so `ProtocolTool.OutputSchema` already held the wrapped form;
the newer SDK stores the **natural** schema there and wraps at the **wire-emission sites**,
gated on the negotiated protocol version.

Consequence: a `tools/list` size harness that reads `ProtocolTool.OutputSchema` is measuring
different things on either side of the upgrade. **It is not a wire measurement post-upgrade.**
Before reporting any cross-version byte delta from such a harness, state which side is pre- and
which is post-wrapping — otherwise a schema *information loss* can be reported as a compaction
*win*, which is exactly backwards.

More generally: **when a byte-count delta doesn't decompose cleanly into named causes, diff the
raw serialized objects per item rather than reaching for a plausible narrative.** Real example:
an unexplained −897 B turned out to be 23 of 25 tools each losing exactly 39 bytes —
`,"execution":{"taskSupport":"optional"}`, emitted by the old SDK on every `Task`-returning tool
and dropped by the new one. The two tools that didn't change were the only two *synchronous*
ones. Two independent effects had been merged into one wrong explanation. Diffing per-item JSON
between a `git worktree add --detach` of the baseline commit and the upgraded tree found it in
minutes.

### Verify a reviewer's severity claim, not just his mechanism

A correct diagnosis of *why* something changed does not establish *who was affected*. When a
review says a breaking wire change is shipping, measure the actual wire at each negotiated
protocol version before accepting the framing — the fix may still be right while the stated
impact is wrong. Watch for the inversion: with version-gated compatibility shims, **doing
nothing may preserve old clients exactly, and the fix may be what changes them.** If so, that
belongs at the top of the report as an explicit escalation, not in a footnote.

## Anti-patterns

- Trusting release dates or version lists from a written report without a registry check.
- Leaving a flipped default implicit because "the new default is what we want anyway".
- Copying a mitigation snippet from an analysis doc without checking the API it names is
  the canonical one.
- Adopting a hybrid/compat mode "for safety" without naming the capability it protects.
- Accepting "no impact" on the one risk area adjacent to the breaking change.
- Carrying a speculative "phase 2 refactor" backlog item when there is no code to refactor.
- Asserting CLR return-type shape as a proxy for the wire contract — the SDK inspects the
  generated schema, so this passes for exactly the types that are broken.
- Comparing byte counts across a major upgrade from a property whose semantics changed.
- Accepting a reviewer's severity claim because his mechanism is correct.
- Shipping a hand-maintained schema mirror with no drift guard tying it to the real serializer.
- Claiming a mutation-tested guard is stronger than it is; document the coverage gap instead.
