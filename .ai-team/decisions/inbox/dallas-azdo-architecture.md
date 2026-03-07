# Architecture Proposal: Azure DevOps Pipeline Support

**By:** Dallas (Lead)
**Date:** 2026-03-07
**Requested by:** Larry Ewing
**Status:** DRAFT — awaiting Larry's review

---

## 1. Executive Summary

Add Azure DevOps (AzDO) pipeline wrapping to helix.mcp, following our established Helix patterns. This enables the ci-analysis skill and other MCP consumers to query AzDO builds, timelines, logs, and test results through the same tool they already use for Helix — eliminating the need for a separate AzDO MCP server.

The design mirrors the Helix architecture: `IAzdoApiClient` → `CachingAzdoApiClient` (decorator) → `AzdoService` → `AzdoMcpTools`. Auth uses Azure Identity (`AzureDeveloperCliCredential`), keeping Helix PAT auth separate and unchanged.

---

## 2. Scope — What the ci-analysis Skill Needs

From analyzing `Get-CIStatus.ps1` and the skill's reference docs, these are the core AzDO operations:

| # | Operation | AzDO REST API | ci-analysis Usage |
|---|-----------|---------------|-------------------|
| 1 | **Get build** | `GET _apis/build/builds/{id}` | Status, result, triggerInfo |
| 2 | **List builds** | `GET _apis/build/builds?branchName=...` | PR build progression, find by PR |
| 3 | **Get timeline** | `GET _apis/build/builds/{id}/timeline` | Stage→Job→Task tree, identify failures |
| 4 | **Get build log** | `GET _apis/build/builds/{id}/logs/{logId}` | Read task output, extract errors |
| 5 | **Get build changes** | `GET _apis/build/builds/{id}/changes` | What commits are in a build |
| 6 | **Get test results** | `GET _apis/test/runs?buildUri=...` | AzDO-published test runs |
| 7 | **URL parsing** | N/A (client-side) | Extract org/project/buildId from URLs |

Operations NOT in scope (Phase 1):
- Pipeline management (create, run, cancel) — `azp` handles this
- Pipeline definitions list — not needed for failure investigation
- Work item linking — out of scope
- Repository/branch queries — GitHub MCP handles this

---

## 3. Project Structure

### Recommendation: **Add to HelixTool.Core** (no new projects)

```
HelixTool.slnx (unchanged)
├── src/HelixTool.Core/
│   ├── HelixService.cs              (existing)
│   ├── IHelixApiClient.cs           (existing)
│   ├── Cache/CachingHelixApiClient.cs (existing)
│   │
│   ├── AzDO/                        ◀ NEW FOLDER
│   │   ├── IAzdoApiClient.cs        — mockable API boundary
│   │   ├── AzdoApiClient.cs         — REST API implementation
│   │   ├── CachingAzdoApiClient.cs  — decorator
│   │   ├── AzdoService.cs           — business logic
│   │   ├── AzdoIdResolver.cs        — URL parsing
│   │   ├── AzdoMcpTools.cs          — MCP tool definitions
│   │   ├── AzdoModels.cs            — DTOs and result types
│   │   └── IAzdoTokenAccessor.cs    — auth abstraction
│   │
│   ├── Cache/
│   │   ├── SqliteCacheStore.cs      (existing, reused as-is)
│   │   └── ICacheStore.cs           (existing, extended)
│   └── ...
├── src/HelixTool/Program.cs         (add AzDO DI registrations)
├── src/HelixTool.Mcp/Program.cs     (add AzDO DI registrations)
└── src/HelixTool.Tests/
    └── AzDO/                        ◀ NEW TEST FOLDER
```

**Rationale — why NOT a separate project:**
- AzDO and Helix data are tightly coupled (Helix jobs are spawned by AzDO builds; the ci-analysis skill cross-references them constantly)
- Shared cache infrastructure (same `ICacheStore`, same `SqliteCacheStore`)
- Both are registered in the same DI containers and MCP server
- A separate `HelixTool.AzDO.Core` project adds a project reference, a namespace, assembly isolation — complexity for zero benefit at this scale
- When we split `HelixService` (per the god-class recommendation), `AzdoService` will already be separate by concern

**Counter-argument considered:** "Keep AzDO optional so the tool works without AzDO deps." Response: The NuGet packages (`Microsoft.TeamFoundationServer.Client`, `Azure.Identity`) add ~5MB to the published tool. Users who only want Helix will carry this weight. Acceptable trade-off — the tool's value proposition is CI investigation, which inherently spans both systems. If size becomes a concern later, we can split.

---

## 4. API Client Layer

### 4a. Interface Design

```
┌──────────────────────────────────────────────────────┐
│                  IAzdoApiClient                       │
│                                                      │
│  GetBuildAsync(org, project, buildId)                │
│  ListBuildsAsync(org, project, filters)              │
│  GetTimelineAsync(org, project, buildId)             │
│  GetBuildLogAsync(org, project, buildId, logId)      │
│  GetBuildChangesAsync(org, project, buildId)         │
│  GetTestRunsAsync(org, project, buildId)             │
│  GetTestResultsAsync(org, project, runId)            │
└──────────────────┬───────────────────────────────────┘
                   │ implements
    ┌──────────────┴──────────────┐
    │       AzdoApiClient         │ ← HttpClient + Azure Identity
    └──────────────┬──────────────┘
                   │ decorated by
    ┌──────────────┴──────────────┐
    │   CachingAzdoApiClient      │ ← same SqliteCacheStore
    └─────────────────────────────┘
```

**Key design choice: REST API via HttpClient, NOT TeamFoundationServer.Client SDK.**

Why:
1. **The SDK pulls in 40+ transitive packages** including `Newtonsoft.Json`, `System.Data.SqlClient` (with CVE), and the entire VSTS client object model. Our `HelixTool.Core.csproj` currently has 3 NuGet dependencies. Adding the TFS SDK would 10x the dependency tree.
2. **The AzDO REST API is stable and well-documented.** We need exactly 7 endpoints (see §2). The SDK wraps them with layers of abstraction we don't need.
3. **The ci-analysis skill's own script uses raw REST.** `Invoke-RestMethod` calls to `_apis/build/builds/{id}/timeline?api-version=7.0`. We know these endpoints work for our use case.
4. **Logan's `azp` tool uses the SDK because it needs `PipelinesHttpClient`** for pipeline YAML expansion (preview runs). We don't need that.
5. **HttpClient + `System.Text.Json` is what we already use** for the Helix cache DTOs. Zero new serialization dependencies.

```csharp
// IAzdoApiClient.cs — thin, mockable boundary
public interface IAzdoApiClient
{
    Task<AzdoBuild> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default);
    Task<AzdoTimeline> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<string> GetBuildLogAsync(string org, string project, int buildId, int logId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, CancellationToken ct = default);
    Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, CancellationToken ct = default);
}
```

**Why `org` and `project` as parameters, not constructor args:**
The ci-analysis skill works across two AzDO orgs (`dnceng-public` for PR builds, `dnceng` for internal builds). Baking the org into the client constructor would require maintaining two client instances. Per-call org/project keeps it simple and matches how the skill actually operates.

### 4b. Model Types

```csharp
// AzdoModels.cs — DTOs matching the REST API JSON shapes
public record AzdoBuild(
    int Id, string BuildNumber, string Status, string? Result,
    string? SourceBranch, string? SourceVersion,
    DateTimeOffset? QueueTime, DateTimeOffset? StartTime, DateTimeOffset? FinishTime,
    AzdoBuildDefinition? Definition, AzdoTriggerInfo? TriggerInfo);

public record AzdoBuildDefinition(int Id, string Name);
public record AzdoTriggerInfo(string? PrSourceSha, string? PrNumber);

public record AzdoBuildFilter(
    string? BranchName = null, int? DefinitionId = null,
    string? StatusFilter = null, string? ResultFilter = null,
    int? Top = null, string? QueryOrder = null);

public record AzdoTimeline(IReadOnlyList<AzdoTimelineRecord> Records);
public record AzdoTimelineRecord(
    string Id, string? ParentId, string Type, string Name,
    string? State, string? Result, int? Order,
    DateTimeOffset? StartTime, DateTimeOffset? FinishTime,
    int? LogId, IReadOnlyList<AzdoIssue>? Issues);
public record AzdoIssue(string Type, string Message, string? Category);

public record AzdoBuildChange(string Id, string? Message, string? Author, DateTimeOffset? Timestamp);

public record AzdoTestRun(int Id, string Name, string? State, int TotalTests, int PassedTests, int FailedTests);
public record AzdoTestResult(string TestCaseTitle, string Outcome, string? ErrorMessage, string? StackTrace, TimeSpan? Duration);
```

### 4c. Implementation

```csharp
// AzdoApiClient.cs — HttpClient-based
public sealed class AzdoApiClient : IAzdoApiClient
{
    private readonly HttpClient _http;
    private const string ApiVersion = "7.0";

    public AzdoApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<AzdoBuild> GetBuildAsync(string org, string project, int buildId, CancellationToken ct)
    {
        var url = $"https://dev.azure.com/{org}/{project}/_apis/build/builds/{buildId}?api-version={ApiVersion}";
        var json = await _http.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<AzdoBuild>(json, s_options)!;
    }
    // ... similar for other methods
}
```

---

## 5. Auth Pattern

### The Problem

Helix uses opaque PAT tokens (`Authorization: token <TOKEN>`).
AzDO uses Azure AD/Entra tokens via `Azure.Identity` (`Authorization: Bearer <TOKEN>`).

These are fundamentally different auth systems. We need both, independently.

### The Design

```
┌─────────────────────────────────────────────────────────────┐
│                      DI Container                           │
│                                                             │
│  IHelixTokenAccessor ─→ ChainedHelixTokenAccessor           │
│    (env var → git credential → null)                        │
│                                                             │
│  IAzdoTokenAccessor  ─→ AzureIdentityAzdoTokenAccessor      │
│    (AzureDeveloperCliCredential → DefaultAzureCredential)   │
│                                                             │
│  HttpClient (for AzDO) ← configured with DelegatingHandler  │
│    that calls IAzdoTokenAccessor for Bearer token            │
└─────────────────────────────────────────────────────────────┘
```

```csharp
// IAzdoTokenAccessor.cs
public interface IAzdoTokenAccessor
{
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
}

// AzureIdentityAzdoTokenAccessor.cs
public sealed class AzureIdentityAzdoTokenAccessor : IAzdoTokenAccessor
{
    private const string AzDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private readonly TokenCredential _credential;

    public AzureIdentityAzdoTokenAccessor()
    {
        // Try azd first (matches Logan's azp tool), fall back to DefaultAzureCredential
        _credential = new ChainedTokenCredential(
            new AzureDeveloperCliCredential(),
            new DefaultAzureCredential());
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var context = new TokenRequestContext([AzDevOpsScope]);
        var token = await _credential.GetTokenAsync(context, ct);
        return token.Token;
    }
}
```

**Why `async` for AzDO but sync for Helix?**
Helix token is a static string lookup (env var or git credential — <100ms). AzDO token acquisition involves a subprocess call to `azd` or a network call to the Azure Identity endpoint. Async is correct here.

**HTTP MCP transport (multi-client):**
For the HTTP MCP server, we'll need a scoped `IAzdoTokenAccessor` that reads from `HttpContext` (similar to `HttpContextHelixTokenAccessor`). Phase 1 can use a singleton since AzDO tokens are typically the same for all callers (machine-level `azd auth`). Phase 2 can add per-request Azure AD token forwarding if needed.

**New NuGet dependency:** `Azure.Identity` (brings in `Azure.Core`, `Microsoft.Identity.Client`). This is ~3MB but is the standard .NET way to do Azure auth. No alternatives.

---

## 6. Caching Strategy

### Reuse `ICacheStore` and `SqliteCacheStore`

The existing `ICacheStore` is API-agnostic — it stores keyed JSON metadata and artifact streams. It already handles TTL, LRU eviction, and WAL-mode SQLite. We reuse it directly.

**Changes needed to ICacheStore:** None. The `IsJobCompletedAsync`/`SetJobCompletedAsync` methods use `jobId` strings — we'll use them for AzDO `buildId` tracking too (cache key prefix: `azdo:` instead of `job:`).

**One change needed to `SqliteCacheStore`:** The `ExtractJobId` method hardcodes `"job:"` prefix. Refactor to support `"azdo:{org}:{project}:{buildId}:..."` prefix. This is a 5-line change.

### Cache Key Format

```
azdo:{org}:{project}:{buildId}:build       → build metadata
azdo:{org}:{project}:{buildId}:timeline    → timeline JSON
azdo:{org}:{project}:{buildId}:log:{logId} → log content (artifact)
azdo:{org}:{project}:{buildId}:changes     → build changes
azdo:{org}:{project}:{buildId}:testruns    → test run list
azdo:{org}:{project}:testrun:{runId}       → test results
```

### TTL Matrix

| Data | Running Build | Completed Build | Rationale |
|------|--------------|-----------------|-----------|
| Build metadata | 15s | 4h | Status changes rapidly while running |
| Timeline | **never cache** | 4h | Timeline changes as jobs complete |
| Build log | 1h | 4h | Logs are immutable once task completes |
| Build changes | 4h | 4h | Commits don't change |
| Test runs | 30s | 4h | Results publish incrementally |
| Test results | 1h | 4h | Immutable once run completes |

**Critical:** Timeline for in-progress builds must NEVER be cached. The ci-analysis script explicitly handles this (`-SkipCacheWrite:$BuildInProgress`). Our decorator must check build status before caching timeline data.

### Cache Isolation

AzDO tokens are typically machine-level (via `azd auth login`), not per-user like Helix PATs. For Phase 1, AzDO cache shares the default cache directory (no token-hash isolation). If we add per-request Azure AD token forwarding later, we'll need the same hash-based isolation we use for Helix.

---

## 7. MCP Tool Surface

### Naming Convention

Follow existing `hlx_` prefix pattern. Use `azdo_` prefix for AzDO tools.

```
azdo_build         — Get build status and metadata
azdo_builds        — List builds (by PR, branch, definition)
azdo_timeline      — Get build timeline (stage→job→task tree)
azdo_log           — Get build task log content
azdo_changes       — Get commits in a build
azdo_test_runs     — Get test runs for a build
azdo_test_results  — Get test results for a test run
```

### Tool Definitions (summary)

| Tool | ReadOnly | UseStructuredContent | Key Parameters |
|------|----------|---------------------|----------------|
| `azdo_build` | ✅ | ✅ | `buildId` (int or URL), `org?`, `project?` |
| `azdo_builds` | ✅ | ✅ | `prNumber?`, `branch?`, `definition?`, `org?`, `project?`, `top?` |
| `azdo_timeline` | ✅ | ✅ | `buildId`, `org?`, `project?` |
| `azdo_log` | ✅ | ❌ (raw text) | `buildId`, `logId`, `org?`, `project?`, `tail?` |
| `azdo_changes` | ✅ | ✅ | `buildId`, `org?`, `project?` |
| `azdo_test_runs` | ✅ | ✅ | `buildId`, `org?`, `project?` |
| `azdo_test_results` | ✅ | ✅ | `runId`, `org?`, `project?`, `outcomeFilter?` |

**Default org/project:** `dnceng-public` / `public` (the most common case for PR builds). Override via parameters when needed.

**URL parsing:** `azdo_build` and `azdo_builds` accept AzDO URLs as the `buildId` parameter (similar to how `hlx_status` accepts Helix URLs). `AzdoIdResolver.ResolveBuildId(string input)` extracts org/project/buildId from URLs like `https://dev.azure.com/dnceng-public/public/_build/results?buildId=1276327`.

### MCP Tool Class

```csharp
[McpServerToolType]
public sealed class AzdoMcpTools
{
    private readonly AzdoService _svc;

    public AzdoMcpTools(AzdoService svc) => _svc = svc;

    [McpServerTool(Name = "azdo_build", Title = "AzDO Build Status", ReadOnly = true, UseStructuredContent = true)]
    [Description("Get status and metadata for an Azure DevOps build...")]
    public async Task<AzdoBuildResult> Build(...) { ... }

    // ... 6 more tools
}
```

**Registration:** `WithToolsFromAssembly(typeof(HelixMcpTools).Assembly)` already picks up all `[McpServerToolType]` classes in `HelixTool.Core`. No registration change needed — `AzdoMcpTools` will be auto-discovered.

---

## 8. Helix ↔ AzDO Cross-Referencing

### The Connection

AzDO builds spawn Helix jobs. The link is:
1. **AzDO → Helix:** Timeline records of type `Task` with name `*Helix*` contain Helix job IDs in their log output
2. **Helix → AzDO:** Helix job properties contain `AzureDevOpsBuildId`, `AzureDevOpsProject`, etc.

### Phase 1: Let the Consumer Cross-Reference

The ci-analysis skill already knows how to cross-reference — it reads Helix task logs from AzDO timelines, extracts Helix job IDs, then queries Helix. We provide both `azdo_*` and `hlx_*` tools; the skill orchestrates.

### Phase 2 (future): Built-in Cross-Reference Tool

```
azdo_helix_jobs   — Given an AzDO buildId, find all Helix jobs
hlx_azdo_build    — Given a Helix jobId, find the parent AzDO build
```

These would parse timeline logs or Helix job properties. Defer to Phase 2.

---

## 9. CLI Commands

### Recommendation: MCP-only for Phase 1, CLI later

The primary consumer is the ci-analysis skill (via MCP). CLI commands can wait.

If we do add CLI commands later, they'd mirror `azp`:
```
hlx azdo status <buildId>     — Build status
hlx azdo timeline <buildId>   — Timeline tree
hlx azdo log <buildId> <logId> — Task log
```

The `hlx azdo` subcommand group keeps them separate from existing `hlx` Helix commands.

---

## 10. Test Results Integration

### AzDO Test API vs Helix TRX Files

| Aspect | AzDO Test API | Helix TRX Parsing |
|--------|---------------|-------------------|
| Source | `_apis/test/runs?buildUri=...` | `.trx` files in Helix work item uploads |
| Format | AzDO TestResult JSON | MSTest TRX XML |
| Scope | Tests that published results to AzDO | Tests that ran on Helix (may not publish) |
| Detail | Name, outcome, error, duration | Name, outcome, error, stack trace, duration |

**These are complementary, not overlapping.** Some pipelines publish test results to AzDO (via `PublishTestResults` task); others only upload TRX files to Helix. The ci-analysis skill queries both.

### Recommendation: Keep them separate

`azdo_test_results` returns AzDO Test API data. `hlx_test_results` returns parsed TRX data. The consumer decides which to query based on the pipeline type.

No new unified test result format — that's abstraction for abstraction's sake. The skill already handles both.

---

## 11. Dependencies

### New NuGet Packages

| Package | Version | Purpose | Concern |
|---------|---------|---------|---------|
| `Azure.Identity` | 1.17.x | AzDO auth via `azd auth login` | ~3MB, pulls in MSAL. Required. |

**That's it.** By using `HttpClient` + `System.Text.Json` instead of the TFS SDK, we avoid:
- `Microsoft.TeamFoundationServer.Client` (40+ transitive deps)
- `Newtonsoft.Json` (we use System.Text.Json)
- `System.Data.SqlClient` (CVE-affected, needs manual override)

### .NET 10 Compatibility

`Azure.Identity` targets `netstandard2.0` and is tested against .NET 10. No known issues. The rest of our stack (`Microsoft.Data.Sqlite`, `ModelContextProtocol`, `ConsoleAppFramework`) is already proven on .NET 10.

---

## 12. DI Registration

### CLI (Program.cs)

```csharp
// AzDO services — singleton (machine-level auth)
services.AddSingleton<IAzdoTokenAccessor, AzureIdentityAzdoTokenAccessor>();
services.AddHttpClient<AzdoApiClient>((sp, client) =>
{
    client.DefaultRequestHeaders.Accept.Add(new("application/json"));
}).AddHttpMessageHandler<AzdoBearerTokenHandler>();
services.AddSingleton<IAzdoApiClient>(sp =>
    new CachingAzdoApiClient(
        sp.GetRequiredService<AzdoApiClient>(),
        sp.GetRequiredService<ICacheStore>(),
        sp.GetRequiredService<CacheOptions>()));
services.AddSingleton<AzdoService>();
```

### HTTP MCP (Program.cs)

```csharp
// AzDO services — scoped when we add per-request auth, singleton for Phase 1
builder.Services.AddSingleton<IAzdoTokenAccessor, AzureIdentityAzdoTokenAccessor>();
builder.Services.AddHttpClient<AzdoApiClient>(...);
builder.Services.AddScoped<IAzdoApiClient>(...);
builder.Services.AddScoped<AzdoService>();
```

---

## 13. Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                        MCP Consumers                                │
│  (ci-analysis skill, Copilot CLI, other MCP clients)               │
└────────┬──────────────────────────────────┬─────────────────────────┘
         │                                  │
    ┌────▼──────────┐                ┌──────▼──────────┐
    │ HelixMcpTools │                │  AzdoMcpTools   │
    │ hlx_status    │                │  azdo_build     │
    │ hlx_logs      │                │  azdo_timeline  │
    │ hlx_files     │                │  azdo_log       │
    │ hlx_test_results               │  azdo_test_runs │
    │ ...           │                │  ...            │
    └────┬──────────┘                └──────┬──────────┘
         │                                  │
    ┌────▼──────────┐                ┌──────▼──────────┐
    │ HelixService  │                │  AzdoService    │
    │ (business     │                │  (business      │
    │  logic)       │                │   logic)        │
    └────┬──────────┘                └──────┬──────────┘
         │                                  │
    ┌────▼──────────────────┐        ┌──────▼──────────────────┐
    │ CachingHelixApiClient │        │ CachingAzdoApiClient    │
    │ (decorator)           │        │ (decorator)             │
    └────┬──────────────────┘        └──────┬──────────────────┘
         │                                  │
    ┌────▼──────────┐                ┌──────▼──────────┐
    │ HelixApiClient│                │  AzdoApiClient  │
    │ (Helix SDK)   │                │  (HttpClient)   │
    └────┬──────────┘                └──────┬──────────┘
         │                                  │
    ┌────▼──────────┐                ┌──────▼──────────┐
    │ Helix REST API│                │ AzDO REST API   │
    │ helix.dot.net │                │ dev.azure.com   │
    └───────────────┘                └─────────────────┘
                    ╲              ╱
                     ╲            ╱
                  ┌───▼──────────▼───┐
                  │  SqliteCacheStore │
                  │  (shared cache)   │
                  └──────────────────┘
```

---

## 14. Implementation Plan

### Phase 1: Core API + MCP Tools (Priority)

| Work Item | Owner | Depends On |
|-----------|-------|------------|
| `IAzdoApiClient` interface + models | Ripley | — |
| `AzdoApiClient` (HttpClient impl) | Ripley | IAzdoApiClient |
| `AzdoIdResolver` (URL parsing) | Ripley | — |
| `IAzdoTokenAccessor` + Azure Identity impl | Ripley | — |
| `CachingAzdoApiClient` (decorator) | Ripley | IAzdoApiClient, ICacheStore |
| `AzdoService` (business logic) | Ripley | AzdoApiClient, CachingAzdoApiClient |
| `AzdoMcpTools` (7 tools) | Ripley | AzdoService |
| DI registration (CLI + HTTP MCP) | Ripley | All above |
| Unit tests (API client, resolver, cache) | Lambert | IAzdoApiClient |
| Update README | Kane | AzdoMcpTools |

### Phase 2: Cross-Reference + CLI (Later)

- `azdo_helix_jobs` tool
- `hlx_azdo_build` tool
- `hlx azdo` CLI subcommand group
- Per-request Azure AD token forwarding for HTTP MCP transport

---

## 15. Risk Assessment

| Risk | Severity | Mitigation |
|------|----------|------------|
| `Azure.Identity` pulls in heavy deps (MSAL) | Medium | Accept — it's the standard auth path. ~3MB is tolerable. |
| AzDO REST API changes (unlikely, v7.0 is stable) | Low | Pin `api-version=7.0` in all requests |
| Token caching/refresh for `AzureDeveloperCliCredential` | Medium | `Azure.Identity` handles token caching internally. |
| Timeline data is large (100s of records) | Medium | Cache completed timelines aggressively (4h). Never cache in-progress. |
| Two auth systems increase config complexity | Medium | Clear separation: `HELIX_ACCESS_TOKEN` for Helix, `azd auth login` for AzDO. Different error messages. |
| `SqliteCacheStore.ExtractJobId` assumes `job:` prefix | Low | Refactor to handle `azdo:` prefix — small change, no breaking. |

---

## 16. Open Questions for Larry

1. **Package naming:** Should the NuGet package remain `lewing.helix.mcp` or become something broader like `lewing.ci.mcp`?
2. **Default org/project:** We default to `dnceng-public`/`public`. Should we also support an env var override (e.g., `AZDO_DEFAULT_ORG`)?
3. **Tool prefix:** `azdo_` (clear, matches existing `hlx_` pattern) vs `ci_` (more generic) vs `azp_` (matches Logan's tool name)?
4. **Phase 1 scope:** Should we include `azdo_test_results` in Phase 1 or defer? The ci-analysis script uses `az devops invoke` for this — it's less common than timeline/log queries.
5. **`azp` tool coordination:** Logan's `azp` tool covers pipeline management (run, cancel, wait). Do we explicitly NOT expose write operations, or should we eventually support `azdo_retry_build`?

---

## 17. Decision Record

**DECISION:** Add AzDO support as a new `AzDO/` folder in `HelixTool.Core`, using `HttpClient` + `Azure.Identity` (not TFS SDK), with the decorator caching pattern, 7 MCP tools prefixed `azdo_`, and no CLI commands in Phase 1.

**Alternatives considered:**
- Separate `HelixTool.AzDO.Core` project → rejected (coupling, complexity)
- `Microsoft.TeamFoundationServer.Client` SDK → rejected (40+ deps, Newtonsoft.Json conflict)
- Unified test result format → rejected (premature abstraction)
- CLI-first approach → rejected (MCP consumer is the priority)
