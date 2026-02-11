# Design Review — P0 Foundation (DI + Error Handling)
**Facilitator:** Dallas
**Participants:** Ripley, Lambert
**Context:** Pre-implementation alignment for US-12 and US-13

## Decisions

### D1: IHelixApiClient Wrapper Interface

We do NOT wrap the entire `HelixApi` class. Instead, we define a thin interface in `HelixTool.Core` that mirrors only the Helix SDK methods we actually call. This keeps the mockable surface minimal.

```csharp
// In HelixTool.Core/IHelixApiClient.cs
namespace HelixTool;

public interface IHelixApiClient
{
    Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default);
    Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default);
    Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default);
    Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default);
    Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default);
}
```

**Why a wrapper, not wrapping `HelixApi` directly:**
- `HelixApi` from `Microsoft.DotNet.Helix.Client` has no interface we can mock. It's a concrete class with sub-properties (`Job`, `WorkItem`) that are also concrete.
- A thin wrapper lets Lambert mock at the right granularity — individual API calls, not SDK plumbing.
- The wrapper's return types should be interfaces matching the SDK's return shapes (or we define our own DTOs). **Decision:** Use the SDK's own return types (`IJobDetails` etc. from `Microsoft.DotNet.Helix.Client.Models`) in the interface — they're already interfaces in the SDK. If they're not interfaces, define minimal DTOs.

**Ripley must verify:** Check whether `_api.Job.DetailsAsync()` returns a concrete type or an interface. If concrete, define result DTOs in Core. If interface, use them directly.

### D2: Default Implementation — HelixApiClient

```csharp
// In HelixTool.Core/HelixApiClient.cs
namespace HelixTool;

public sealed class HelixApiClient : IHelixApiClient
{
    private readonly HelixApi _api = new();

    // Implement each method by delegating to _api.Job.*, _api.WorkItem.*
}
```

This is the only place `new HelixApi()` appears in the entire codebase. Single instantiation point.

### D3: HelixService Constructor Injection

```csharp
public class HelixService
{
    private readonly IHelixApiClient _api;

    public HelixService(IHelixApiClient api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }
    // ... all methods unchanged except they use _api and accept CancellationToken
}
```

The field initializer `private readonly HelixApi _api = new()` is removed. No default constructor. DI is mandatory.

### D4: DI Registration — CLI Host

ConsoleAppFramework does not have built-in DI. The CLI must create and pass the service explicitly.

```csharp
// Program.cs (CLI)
var app = ConsoleApp.Create();
app.Add<Commands>();
app.Run(args);

public class Commands
{
    private readonly HelixService _svc;

    public Commands()
    {
        _svc = new HelixService(new HelixApiClient());
    }
    // ...
}
```

**Alternative (if ConsoleAppFramework supports ServiceProvider):** Register via `IServiceProvider`. Ripley should check if `ConsoleApp.Create()` accepts a `ConfigureServices` delegate. If yes, prefer that. If no, the manual construction above is acceptable — the CLI is a thin host.

### D5: DI Registration — MCP Host

The MCP SDK supports DI. `HelixMcpTools` becomes a non-static class with constructor injection.

```csharp
// MCP Program.cs
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IHelixApiClient, HelixApiClient>();
builder.Services.AddSingleton<HelixService>();

builder.Services
    .AddMcpServer(options => { options.ServerInfo = new() { Name = "hlx", Version = "1.0.0" }; })
    .WithHttpTransport()
    .WithToolsFromAssembly();

var app = builder.Build();
app.MapMcp();
app.Run();
```

```csharp
// HelixMcpTools.cs — remove static, add constructor injection
[McpServerToolType]
public sealed class HelixMcpTools
{
    private readonly HelixService _svc;

    public HelixMcpTools(HelixService svc)
    {
        _svc = svc;
    }

    // All methods become instance methods, not static
}
```

**Constraint:** `HelixMcpTools` methods must become instance methods (remove `static`). The `McpServerToolType` attribute + `WithToolsFromAssembly()` should resolve them via DI. Ripley must verify this works with the ModelContextProtocol SDK — if tool methods must be static, we need an alternative (e.g., static accessor to a service locator, which is ugly but functional).

### D6: Error Handling Contract

#### Exception hierarchy:

```csharp
// In HelixTool.Core/HelixException.cs
namespace HelixTool;

public class HelixException : Exception
{
    public HelixException(string message, Exception? inner = null)
        : base(message, inner) { }
}
```

We do NOT create a deep exception hierarchy. One exception type with descriptive messages. The message is the contract.

#### Catch points in HelixService:

Each public method in `HelixService` wraps its API calls:

```csharp
try
{
    var job = await _api.GetJobDetailsAsync(id, ct);
    // ...
}
catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
{
    throw new HelixException($"Job '{id}' not found.", ex);
}
catch (HttpRequestException ex)
{
    throw new HelixException($"Helix API error: {ex.Message}", ex);
}
catch (TaskCanceledException ex) when (ex.CancellationToken == ct)
{
    throw; // Let cancellation propagate naturally
}
catch (TaskCanceledException ex)
{
    throw new HelixException("Helix API request timed out.", ex);
}
```

**Do NOT catch `Exception`.** Only catch `HttpRequestException` and `TaskCanceledException` (for timeout vs cancellation). Everything else propagates — it's a bug, not an expected error.

#### Error surface — CLI vs MCP:

| Layer | Error handling |
|-------|---------------|
| **HelixService** | Catches `HttpRequestException` → throws `HelixException` with human message |
| **CLI (Commands)** | Catches `HelixException` → writes message to `Console.Error`, exits non-zero. Does NOT catch other exceptions (let them crash with stack trace — it's a bug). |
| **MCP (HelixMcpTools)** | Catches `HelixException` → returns JSON `{ "error": message }`. The MCP SDK may also have its own error handling — Ripley should check if throwing from a tool method produces a proper MCP error response. |

**Key principle:** HelixService never writes to Console. It throws. The hosts (CLI/MCP) decide how to present errors.

### D7: Input Validation

In `HelixService`, at the top of every public method:

```csharp
ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
// For methods that take workItem:
ArgumentException.ThrowIfNullOrWhiteSpace(workItem);
```

`HelixIdResolver.ResolveJobId` behavior change:
- Current: returns raw input if it can't parse → silent pass-through.
- New: If input is not a valid GUID and not a parseable Helix URL, throw `ArgumentException("Invalid job ID: must be a GUID or Helix URL")`.
- Guard against `id[..8]` on short strings: use `id.Length >= 8 ? id[..8] : id` for temp file naming.

### D8: CancellationToken Threading

Every async method in `HelixService` gets `CancellationToken cancellationToken = default` as the last parameter.

**Where does CancellationToken come from?**

| Context | Source |
|---------|--------|
| **CLI** | `CancellationToken.None` (implicit from `= default`). ConsoleAppFramework may provide one via `ConsoleAppContext` — Ripley should check. If it does, thread it through. If not, `default` is fine for now. |
| **MCP** | The MCP SDK should pass a `CancellationToken` representing the client request. Ripley must check if MCP tool methods receive one (e.g., via method parameter or a context object). If yes, thread it. If no, `default`. |

**Threading rule:** Every `await` call passes `cancellationToken`. This includes:
- `_api.GetJobDetailsAsync(id, ct)`
- `semaphore.WaitAsync(ct)`
- `stream.CopyToAsync(file, ct)`
- All `Task.WhenAll` inner lambdas

### D9: Mockable Surface for Lambert

Lambert needs to mock `IHelixApiClient`. That is the **only** mock boundary.

```
Test scope:
  HelixService + mock IHelixApiClient → unit tests
  HelixIdResolver → unit tests (already pure, no mock needed)
  MatchesPattern → unit tests (already internal static)
  Commands (CLI) → NOT unit tested (thin host, integration only)
  HelixMcpTools → NOT unit tested directly (thin host, integration only)
```

What Lambert needs from Ripley:
1. The `IHelixApiClient` interface definition (to build mocks against)
2. The DTOs or interface types that `IHelixApiClient` methods return (to construct fake responses)
3. The `HelixException` type (to verify error handling in tests)

**Test package:** Lambert should add `NSubstitute` (or `Moq`) to `HelixTool.Tests.csproj`. Preference: NSubstitute — simpler API, no Castle.DynamicProxy issues.

### D10: JsonSerializerOptions Consolidation (Opportunistic)

While Ripley is changing `HelixMcpTools`, hoist the repeated `new JsonSerializerOptions { WriteIndented = true }` to a `private static readonly` field. 4 allocations → 1.

```csharp
private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };
```

## Action Items

| Owner | Action |
|-------|--------|
| Ripley | Create `IHelixApiClient` interface in `HelixTool.Core` with the 6 methods from D1 |
| Ripley | Create `HelixApiClient` implementation wrapping `HelixApi` — verify SDK return types are interfaces or define DTOs |
| Ripley | Refactor `HelixService` to constructor-inject `IHelixApiClient` (D3) |
| Ripley | Add `CancellationToken cancellationToken = default` to all async methods in `HelixService` and thread through all awaits (D8) |
| Ripley | Create `HelixException` in `HelixTool.Core` (D6) |
| Ripley | Add try/catch in each `HelixService` public method per D6 contract |
| Ripley | Add `ArgumentException.ThrowIfNullOrWhiteSpace` guards per D7 |
| Ripley | Fix `HelixIdResolver.ResolveJobId` to throw on invalid input per D7 |
| Ripley | Guard `id[..8]` against short strings per D7 |
| Ripley | Refactor `HelixMcpTools` — remove static, add constructor injection (D5) |
| Ripley | Register `IHelixApiClient` and `HelixService` in MCP DI (D5) |
| Ripley | Update CLI `Commands` to construct `HelixService` with `HelixApiClient` (D4) |
| Ripley | Check ConsoleAppFramework for DI support (`ConfigureServices` or `ConsoleAppContext.CancellationToken`) |
| Ripley | Check MCP SDK: do tool methods receive CancellationToken? Do instance methods work with `WithToolsFromAssembly`? |
| Ripley | Hoist `JsonSerializerOptions` to static field in `HelixMcpTools` (D10) |
| Lambert | Add `NSubstitute` to `HelixTool.Tests.csproj` |
| Lambert | Write unit tests for `HelixService.GetJobStatusAsync` with mocked `IHelixApiClient` — happy path + error paths |
| Lambert | Write unit tests for `HelixException` throw on 404 (job not found) |
| Lambert | Write unit tests for `HelixIdResolver.ResolveJobId` — invalid input now throws |
| Lambert | Write unit tests for `ArgumentException` guards on null/empty `jobId` and `workItem` |
| Lambert | Verify `CancellationToken` is threaded correctly (cancel mid-operation → `OperationCanceledException`) |

## Notes

### Risks
1. **MCP SDK DI compatibility:** The `ModelContextProtocol` SDK v0.8.0-preview.1 may not support instance methods for tool classes. If `WithToolsFromAssembly()` requires static methods, Ripley needs a workaround (service locator pattern via static `IServiceProvider` reference set at startup). This is the highest-risk item — verify first.
2. **Helix SDK return types:** We don't know if `_api.Job.DetailsAsync()` returns an interface or concrete type. This affects whether `IHelixApiClient` can use SDK types directly or needs its own DTOs. Ripley must check before defining the interface.
3. **ConsoleAppFramework DI:** CAF v5 may or may not support `IServiceProvider`. If it doesn't, manual construction in `Commands` constructor is fine but means the CLI can't benefit from scoped services later.

### Constraints
- `HelixIdResolver` stays static — it's pure, testable as-is. Do NOT inject it.
- `MatchesPattern` stays `internal static` — same reasoning.
- Record types (`JobSummary`, `WorkItemResult`, etc.) stay nested in `HelixService` for now. Extracting to `Models/` is US-17 (P1), not in scope for this PR.
- Namespace cleanup is US-17 — do NOT change namespaces in this PR.
- File I/O in `DownloadConsoleLogAsync`/`DownloadFilesAsync` stays as-is (pragmatic trade-off per D2d in decisions.md). Test these via integration tests only.

### Edge Cases
- `SemaphoreSlim` in `GetJobStatusAsync` must pass `cancellationToken` to `WaitAsync` — otherwise cancellation doesn't interrupt queued work.
- `TaskCanceledException` can come from either cancellation or HTTP timeout. Distinguish by checking `ex.CancellationToken == ct` (cancellation) vs not (timeout).
- `HelixIdResolver.ResolveJobId` throwing `ArgumentException` is a breaking change — all callers must be updated. Since there are only 3 callers (all in `HelixService`), this is contained.

### Ordering
Ripley should implement in this order:
1. `IHelixApiClient` + `HelixApiClient` (unblocks Lambert)
2. `HelixException`
3. `HelixService` refactor (constructor injection + CancellationToken + error handling + validation)
4. MCP host changes (`HelixMcpTools` + DI registration)
5. CLI host changes (`Commands` + construction)

Lambert can start writing test scaffolding and mock setup as soon as step 1 is done.
