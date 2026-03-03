### 2026-03-03: HelixTool.Core as separate NuGet package
**By:** Dallas
**What:** Architecture analysis and recommendation for publishing HelixTool.Core as a standalone NuGet library
**Why:** Users want to consume the Helix API client/service directly in their own .NET tools

---

## 1. Current State Assessment

### Public Types in HelixTool.Core Today

**Service layer:**
- `HelixService` — Core business logic (14 public methods). Constructor takes `IHelixApiClient`.
- `HelixIdResolver` — Static utility for parsing Helix URLs/GUIDs. Pure functions, no dependencies.
- `HelixException` — Domain exception type.
- `FailureCategory` — Enum for work item failure classification.

**API client abstraction:**
- `IHelixApiClient` — 6-method interface wrapping the Helix SDK surface.
- `HelixApiClient` — Default implementation wrapping `Microsoft.DotNet.Helix.Client.HelixApi`.
- `IHelixApiClientFactory` / `HelixApiClientFactory` — Factory for per-token client creation (HTTP mode).
- Projection interfaces: `IJobDetails`, `IWorkItemSummary`, `IWorkItemDetails`, `IWorkItemFile`.

**Auth abstractions:**
- `IHelixTokenAccessor` / `EnvironmentHelixTokenAccessor` — Token resolution.
- `ChainedHelixTokenAccessor` — Env var → stored credential chain. Exposes `TokenSource`.
- `ICredentialStore` / `GitCredentialStore` — `git credential` backed storage.
- `TokenSource` enum.

**Cache layer (7 files):**
- `ICacheStore` / `SqliteCacheStore` — SQLite + disk artifact cache.
- `ICacheStoreFactory` / `CacheStoreFactory` — Per-auth-context cache creation.
- `CachingHelixApiClient` — Decorator over `IHelixApiClient`.
- `CacheOptions` — Configuration record.
- `CacheStatus` — Status summary record.
- `CacheSecurity` — Internal path traversal prevention.

**MCP integration (should NOT be here):**
- `HelixMcpTools` — 12 MCP tool definitions. Depends on `ModelContextProtocol` SDK.
- `McpToolResults.cs` — 18 MCP-specific DTO types (`StatusResult`, `FilesResult`, `FileInfo_`, etc.).

**Nested record types in HelixService:**
- `WorkItemResult`, `JobSummary`, `FileEntry`, `FileSearchResult`, `WorkItemDetail`, `BatchJobSummary`, `LogSearchResult`, `LogMatch`, `FileContentSearchResult`, `TrxTestResult`, `TrxParseResult` — 11 types total.

### Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `Microsoft.DotNet.Helix.Client` | 11.0.0-beta.26110.116 | Helix SDK |
| `Microsoft.Data.Sqlite` | 9.0.7 | Cache storage |
| `ModelContextProtocol` | 1.0.0 | **MCP tools** |

### How It's Consumed Today

- **HelixTool (CLI):** References Core. Uses `HelixService` directly for CLI commands. Brings its own DI, hosting, ConsoleAppFramework.
- **HelixTool.Mcp (HTTP server):** References Core. Uses `HelixMcpTools` for MCP tool exposure. Uses `ModelContextProtocol.AspNetCore`.

---

## 2. Feasibility Analysis

### Can it be published as-is?

**No.** Several issues must be addressed:

#### Issue A: ModelContextProtocol dependency in Core (BLOCKER)

`HelixMcpTools` and `McpToolResults.cs` live in Core but are purely MCP presentation concerns. They depend on `ModelContextProtocol` (1.0.0), which:
- Pulls in `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, and other packages a library consumer doesn't need.
- Is semantically wrong — a "core library" should not know about its MCP presentation layer.
- Forces library consumers to take a transitive dependency on the MCP SDK.

**Verdict:** Must move. This is the single blocking issue.

#### Issue B: Nested record types in HelixService (SHOULD FIX)

11 record types defined as nested types inside `HelixService`:
```csharp
public class HelixService {
    public record WorkItemResult(...);
    public record JobSummary(...);
    // ... 9 more
}
```

These are return types consumed by external code. Nested types are:
- Harder to discover (`HelixService.JobSummary` vs `JobSummary`)
- Impossible to reference without the containing type
- A code smell when they're not implementation details

Previous decision (2025-07-18) already recommended extracting to `Models/`. Not yet done.

**Verdict:** Should extract to top-level types before publishing. Low risk, no behavioral change.

#### Issue C: IHelixApiClient abstraction quality (GOOD)

The interface is clean — 6 methods, all async, all take `CancellationToken`. Projection interfaces (`IJobDetails`, etc.) properly decouple from Helix SDK concrete types. External consumers can:
- Use `HelixApiClient` directly with an access token
- Implement `IHelixApiClient` for testing or custom scenarios
- Stack `CachingHelixApiClient` as a decorator

**Verdict:** Ready for external use. No changes needed.

#### Issue D: Cache layer (GOOD — already optional)

Caching is implemented as a decorator (`CachingHelixApiClient`) that wraps `IHelixApiClient`. It's opt-in — consumers who don't register it get uncached behavior. The `MaxSizeBytes = 0` path disables caching entirely.

The `Microsoft.Data.Sqlite` dependency (9.0.7) is lightweight and reasonable for a caching library. However, consumers who don't want caching would still pull this transitive dependency.

**Verdict:** Acceptable as-is. The decorator pattern means caching is already pluggable. If dependency weight becomes a concern, we could split caching into a separate `HelixTool.Core.Caching` package later — but that's premature optimization today.

#### Issue E: Auth abstractions (GOOD — clean interfaces)

`IHelixTokenAccessor` and `ICredentialStore` are clean single-purpose interfaces. `ChainedHelixTokenAccessor` and `GitCredentialStore` are concrete implementations that work standalone. Library consumers can:
- Use `EnvironmentHelixTokenAccessor` for simple env-var auth
- Use `ChainedHelixTokenAccessor` for the full resolution chain
- Implement their own `IHelixTokenAccessor` (e.g., Azure Identity, service principal)

**Verdict:** Ready for external use. No changes needed.

#### Issue F: FileInfo_ naming (COSMETIC)

`FileInfo_` has a trailing underscore to avoid collision with `System.IO.FileInfo`. Previous review noted `HelixFileInfo` would be cleaner. This is in `McpToolResults.cs` which is moving out anyway — so this only matters if we keep a similar type in Core.

**Verdict:** Non-blocking. `FileEntry` in `HelixService` is the Core-level type and is fine.

---

## 3. Recommended Approach

### 3.1 Move MCP code out of Core

Move `HelixMcpTools.cs` and `McpToolResults.cs` from `HelixTool.Core` to a new or existing project. Two options:

**Option A (RECOMMENDED): Move to HelixTool (CLI project)**
The CLI project already references both `HelixTool.Core` and `ModelContextProtocol`. It hosts the MCP server via `hlx mcp` subcommand. Moving the MCP tool definitions there keeps them co-located with their host. The HelixTool.Mcp project (HTTP server) also references Core — it would need to reference the CLI project or we duplicate. This is messy.

**Option B (RECOMMENDED): Move to HelixTool.Mcp.Shared or leave in both hosts**
Both CLI and HTTP hosts need `HelixMcpTools`. Creating a `HelixTool.Mcp.Shared` project just for tool definitions is over-engineering.

**Option C (BEST): Keep a HelixTool.Mcp.Core project**
Create `HelixTool.Mcp.Core` containing `HelixMcpTools.cs` and `McpToolResults.cs`. Both the CLI (`HelixTool`) and HTTP (`HelixTool.Mcp`) projects reference it. This respects the existing split pattern.

Actually, on reflection: **both HelixTool and HelixTool.Mcp already reference ModelContextProtocol** (directly or via AspNetCore). The simplest correct move is:

**DECISION: Move HelixMcpTools.cs and McpToolResults.cs into the HelixTool project (CLI).** The HelixTool.Mcp (HTTP) project already references HelixTool (it must, to get the tool definitions for MCP hosting). If it doesn't currently, add that reference. This is zero new projects, one file move, and removes the `ModelContextProtocol` PackageReference from Core.

Wait — checking: HelixTool.Mcp does NOT reference HelixTool. It only references Core. And the CLI project is an Exe, not a library. So we can't reference it from Mcp.

**REVISED DECISION: Create a new project HelixTool.Mcp.Core** (class library) containing:
- `HelixMcpTools.cs`
- `McpToolResults.cs`

Dependencies: `HelixTool.Core` + `ModelContextProtocol`. Both CLI and HTTP projects reference this instead of owning MCP tools.

Alternatively, since both hosts already depend on `ModelContextProtocol`, we could just **move the files to HelixTool.Core's parent directory** and have both projects include them as linked files. But linked files are fragile.

**FINAL DECISION: Move to a new `HelixTool.Mcp.Tools` project.**

```
src/
  HelixTool.Core/          ← Pure library (no MCP dependency)
  HelixTool.Mcp.Tools/     ← HelixMcpTools + McpToolResults (depends on Core + MCP SDK)
  HelixTool/               ← CLI (depends on Core + Mcp.Tools)
  HelixTool.Mcp/           ← HTTP server (depends on Core + Mcp.Tools)
```

This is the cleanest split: Core has no MCP knowledge, MCP tool definitions are shared, both hosts consume them.

### 3.2 Extract nested record types from HelixService

Move the 11 nested record types to `Models/` folder as top-level types:
- `Models/WorkItemResult.cs`
- `Models/JobSummary.cs`
- `Models/FileEntry.cs`
- `Models/FileSearchResult.cs`
- `Models/WorkItemDetail.cs`
- `Models/BatchJobSummary.cs`
- `Models/LogSearchResult.cs`
- `Models/LogMatch.cs`
- `Models/FileContentSearchResult.cs`
- `Models/TrxTestResult.cs`
- `Models/TrxParseResult.cs`

All stay in `namespace HelixTool.Core`. This is a source-breaking change for callers using `HelixService.JobSummary` syntax, but binary-compatible since the CLR doesn't distinguish nested vs. top-level types in IL.

### 3.3 NuGet packaging

Add to `HelixTool.Core.csproj`:
```xml
<PropertyGroup Label="Package Metadata">
  <PackageId>lewing.helix.core</PackageId>
  <Description>.NET client library for the Helix test infrastructure API — job status, work item details, file downloads, TRX parsing, and caching</Description>
  <Authors>Larry Ewing</Authors>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageTags>helix;dotnet;ci;testing;api-client</PackageTags>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
</PropertyGroup>
```

Key decisions:
- **PackageId:** `lewing.helix.core` (consistent with `lewing.helix.mcp` for the CLI tool)
- **GenerateDocumentationFile:** Enables XML doc comments in IntelliSense for consumers
- **No `PackAsTool`:** This is a library, not a tool

### 3.4 Versioning strategy

**RECOMMENDED: Same version as CLI tool, from a single source.**

Rationale:
- Core and CLI are in the same repo, built together, released together.
- Independent versioning adds cognitive load and release process complexity.
- If a Core change doesn't affect CLI, bump patch. If a CLI change doesn't affect Core, Core still gets bumped — no harm.
- SemVer still applies: breaking Core API changes = major bump for both.

Implementation: Move `<Version>` to `Directory.Build.props` so all projects share it.

### 3.5 API surface cleanup

Beyond extracting nested types:
1. **`MatchesPattern` is `internal static` but called from `HelixMcpTools`** — after moving MCP tools out of Core, this needs to become `public` or the pattern matching logic moves to Mcp.Tools. It's a general-purpose utility, so `public static` in `HelixService` (or better, in `HelixIdResolver` or a new `HelixUtilities` class) is fine.
2. **`IsFileSearchDisabled` is `internal static`** — same issue. This is config, should be exposed or injected.
3. **`FormatDuration` in HelixMcpTools is `internal static`** — presentation logic, stays in Mcp.Tools.

### 3.6 Breaking change analysis

| Change | CLI impact | MCP impact | External consumer impact |
|--------|-----------|------------|--------------------------|
| Move HelixMcpTools to Mcp.Tools | Update project reference | Update project reference | N/A (new consumers) |
| Extract nested records | Update `HelixService.X` → `X` references | Same | N/A (new consumers) |
| Remove MCP PackageReference from Core | None | None | Smaller dependency tree |
| Add NuGet metadata | None | None | Enables consumption |

All changes are internal restructuring. No wire-format changes. No behavioral changes. Risk: **LOW**.

---

## 4. Work Items

| ID | Title | Size | Assignee | Depends On | Description |
|----|-------|------|----------|------------|-------------|
| W1 | Create HelixTool.Mcp.Tools project | S | Ripley | — | New class library project with `ModelContextProtocol` + `HelixTool.Core` references. Move `HelixMcpTools.cs` and `McpToolResults.cs` from Core. Update namespace if needed. |
| W2 | Update CLI and HTTP projects | S | Ripley | W1 | Add `HelixTool.Mcp.Tools` project reference to both `HelixTool` and `HelixTool.Mcp`. Remove `ModelContextProtocol` PackageReference from `HelixTool.Core.csproj`. Verify build. |
| W3 | Extract nested record types | M | Ripley | — | Move 11 nested records from `HelixService` to `Models/` folder as top-level types. Update all call sites in Core, CLI, MCP, and tests. |
| W4 | Fix internal→public visibility | S | Ripley | W1 | Make `MatchesPattern` and `IsFileSearchDisabled` public (or refactor to injected config). These are needed by Mcp.Tools after the split. |
| W5 | Add NuGet packaging metadata | S | Ripley | W2 | Add PackageId, Description, License, Tags, etc. to `HelixTool.Core.csproj`. Add `GenerateDocumentationFile`. Create or link a library-specific README. |
| W6 | Centralize version in Directory.Build.props | S | Ripley | — | Move `<Version>` from `HelixTool.csproj` to `Directory.Build.props`. Ensure all projects share it. |
| W7 | Verify existing tests pass | S | Lambert | W1, W2, W3, W4 | Run full test suite after restructuring. No new tests needed for mechanical refactors. |
| W8 | Update README for library consumption | M | Kane | W5 | Add a "Using as a library" section to README showing how to reference the NuGet package, register services, and call HelixService directly. |
| W9 | CI: Add pack step for Core NuGet | S | Ripley | W5, W6 | Update CI pipeline to produce `lewing.helix.core` .nupkg alongside the existing tool package. |

**Execution order:** W6 → W1 → W2 + W4 → W3 → W5 → W7 → W8 + W9

**Total effort estimate:** ~2-3 days of focused work.

---

## 5. Open Questions for Larry

1. **PackageId:** `lewing.helix.core` or `lewing.helixtool.core`? The CLI is `lewing.helix.mcp` — do we want `helix` as the common prefix?
2. **Separate publish workflow?** Or same pipeline publishes both packages?
3. **Minimum supported TFM:** Currently `net10.0`. Should the library target `net8.0` or `net9.0` for broader adoption? The `Microsoft.DotNet.Helix.Client` package may constrain this.
4. **Should caching be a separate package?** (`lewing.helix.core.caching`) — I recommend no for now, but flagging for awareness.
