### 2026-03-03: Core NuGet packaging — project structure and CI changes
**By:** Ripley
**What:** Executed W1-W6 and W9 from Dallas's architecture decision for publishing HelixTool.Core as a standalone NuGet package.

**Key structural changes:**
- `Directory.Build.props` (repo root) — centralizes `<Version>0.2.1</Version>` for all projects
- `src/HelixTool.Mcp.Tools/` — new class library containing `HelixMcpTools.cs` + `McpToolResults.cs` (moved from Core)
- `src/HelixTool.Core/Models/` — 11 record types extracted from nested definitions in `HelixService`
- `HelixTool.Core.csproj` — NuGet metadata added (PackageId: `lewing.helix.core`, MIT, doc gen)
- `publish.yml` — packs both `lewing.helix.mcp` and `lewing.helix.core`, version validation reads `Directory.Build.props`

**Namespace decision:** `HelixMcpTools.cs` and `McpToolResults.cs` kept `namespace HelixTool.Core` despite living in `HelixTool.Mcp.Tools` project. This avoids any call-site `using` changes. The project name conveys the architectural boundary; the namespace conveys the API surface.

**Visibility change:** `IsFileSearchDisabled` promoted from `internal static` to `public static` on `HelixService`.

**Pending for others:**
- **Lambert (W7):** Run full test suite — all tests should pass without modification
- **Kane (W8):** Add "Using as a library" section to README
