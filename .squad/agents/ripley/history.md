
**2026-05-21 10:33Z:** Ash audit found helix_ci_guide needs exception wrapping (5-line fix). See decisions.md for details.

**2026-05-21 12:22Z:** Ran full dependency audit against Directory.Packages.props. Key findings:
- Azure.Identity 1.13.2 is NuGet-deprecated ("Other") — update to 1.21.0 clears the flag; types type-forwarded to Azure.Core in 1.21, non-breaking for our `AzureCliCredential` usage.
- Microsoft.Data.Sqlite 9.0.7 → 10.0.8 is a natural net10 alignment bump.
- Microsoft.Extensions.* (DI/Hosting/Http) at 10.0.0–10.0.3 can be patched to 10.0.8 safely (servicing releases).
- Microsoft.CodeAnalysis.CSharp 4.12.0 → 5.3.0 is a major version for the generator project; warrants a compile check before shipping.
- ConsoleAppFramework 5.7.13 and ModelContextProtocol 1.3.0 are at latest.
- Microsoft.DotNet.Helix.Client reports "Not found at sources" — only updates from the dnceng feed, so leave pinned.
- xunit 2.x is marked Legacy; test framework migration is out of scope for a patch.
- No vulnerable packages found. Recommended ship 5 🟢 packages (Azure.Identity, Microsoft.Data.Sqlite, 3x Microsoft.Extensions.*) as v0.7.1.

## Learnings — Pagination Standardization Implementation (2026-05-20, commit 1a2e1d0)

**Pattern learned:** When changing service-layer return types:
1. Update the record definition (or add new wrapper)
2. Update the service method signature + implementation
3. Update all tool/CLI call sites
4. Update tests that directly call the service
5. Clean build to avoid stale reference errors

📌 Team update (2026-05-21): Pagination Phase 1+2 implemented — wrapped `azdo_changes`/`azdo_test_runs` in `LimitedResults<T>`, added `truncated`/`note` to 8 result types. Build clean. Commit 0a82e58. Full suite: 1180/1180 passing.

## Learnings — RollForward policy for global tool (2026-05-21)

- Set `<RollForward>Major</RollForward>` only in `src/HelixTool/HelixTool.csproj` for the generated `HelixTool.runtimeconfig.json`.
- Do not add `RollForward` to library projects; this startup policy is only consumed by the executable entry point/runtimeconfig.

## Learnings — MCP exception cleanup (2026-05-21)

- Confirmed the MCP tool exception pattern is `catch (Exception ex) when (...)` followed by `throw new McpException($"Failed to {action}: {ex.Message}", ex);`, preserving the original exception as `InnerException` for debugging.
- `azdo_auth_status` is **not** sync-safe in its current shape: `AzCliAzdoTokenAccessor.AuthStatusAsync()` can await `_resolutionLock.WaitAsync(...)` and perform fallback credential resolution through `AzureCliCredential` or `az account get-access-token` on cache miss.
- PR #53 tracks the `helix_ci_guide` exception wrap and the auth-status audit follow-up.
See history-archive.md for complete history.

# Summary (archived older history before 2026-05-20)

See history-archive.md for complete history including AzDO auth patterns, MCP SDK upgrades, CLI schema generation, release conventions, and earlier learnings from 2025-03 through 2026-03.

## Learnings — dnceng feed & Helix.Client version format (2026-05-21)

**Feed URL pattern:** `https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-eng/nuget/v3/flat2/{package-id}/index.json` for the flat index. The registration endpoint (for publish dates) is accessed via the GUID-based URL found in the index response's `items[].@id` fields.

**Version number format:** `11.0.0-beta.{YYMDD}.{build}` where `M` is the **single-digit month** (1–9 for Jan–Sep, 10–12 for Oct–Dec) and `DD` is zero-padded two-digit day. So `26110` = YY=26, M=1(Jan), DD=10 → **Feb 10, 2026** (the publish date, not necessarily the commit date — there is a pipeline delay of ~1 day). The embedded date reflects the **build pipeline date**, not the git commit date.

**Gotcha:** The task-prompt shorthand "26110 = 2026-01-10 build" is misleading — NuGet registration shows `26110.116` was actually published **2026-02-10**. The number encodes the Arcade daily build pipeline run date.

**Source repo:** `dotnet/arcade` (not `dotnet/arcade-services`). Helix.Client lives at `src/Microsoft.DotNet.Helix/Client/CSharp/`. Search commits with `gh api "repos/dotnet/arcade/commits?path=src/Microsoft.DotNet.Helix/Client&since=..."`.

**Multiple major versions:** The dnceng feed publishes 11.x, 10.x, 9.x, 8.x, 6.x simultaneously (different SDK stream branches). We are on 11.x which is the head/main stream. Stick to 11.x when bumping.

**Daily build cadence:** The feed produces multiple builds per day (build numbers 100–130 = internal official builds; 1–9 = PR/validation builds). The highest-numbered build of the day is typically the last official build.

## Learnings — CPM bump for v0.7.1 (2026-05-21)

**CPM bump workflow confirmed:** For a pure version-bump PR in this repo, the only file to touch is `Directory.Packages.props`. No `.csproj` changes needed — CPM resolves versions centrally. Restore picks up new versions automatically on first run after the props edit.

**Azure.Identity deprecation behavior:** NuGet marks 1.13.2 as deprecated with reason "Other" (not "HasVulnerability" or "CriticalBugs"). The deprecation is purely a "please upgrade" signal, not a security advisory. Upgrading to 1.21.0 clears it. The type-forwarding change (types moved from Azure.Identity to Azure.Core via `[TypeForwardedTo]`) is binary-compatible — our `AzureCliCredential` usage compiles and runs unchanged.

**Microsoft.Data.Sqlite major version cross (9→10):** Moving from 9.0.7 to 10.0.8 crosses a major version boundary but is safe here because we target `net10.0` and there are no API surface changes for our usage patterns (basic connection/command/reader). SQLitePCLRaw transitive dependencies bump automatically.

**PR #54:** `chore(deps): bump 6 packages for v0.7.1` — branch `chore/v0.7.1-deps`, all 6 bumps in one commit. Build green (0/0), tests green (1180/1180). Lewing will merge and tag v0.7.1 separately.

## Learnings — Release flow v0.7.1 (2026-05-21 12:55Z)

**Three version stamps to bump:** The workflow validates all three before creating a release:
1. `src/HelixTool/HelixTool.csproj` — `<Version>0.7.1</Version>`
2. `src/HelixTool/.mcp/server.json` — top-level `"version": "0.7.1"` and package `"version": "0.7.1"`

**Build & test gate:** Both must pass (0 errors, 0 warnings; 1180/1180 tests) before commit.

**Never manual `gh release create`:** The `publish.yml` workflow uses `ncipollo/release-action` and creates the GitHub Release automatically when the tag is pushed. Manually running `gh release create` causes a 422 error and **skips the NuGet push step** entirely.

**Release flow (in order):**
1. Verify all THREE version stamps match the tag (e.g., 0.7.1)
2. Build clean + all tests pass
3. Commit version bumps: `git commit -F -` with detailed changelog
4. Push main: `git push origin main`
5. Create annotated tag: `git tag -a v0.7.1 -m "Release v0.7.1..."`
6. Push tag: `git push origin v0.7.1` ← **workflow triggers here**
7. Workflow auto-creates GitHub Release with nupkg asset; no manual steps needed

**Timing:** Workflow runs in ~33s (validation, pack, create release, NuGet auth, push to NuGet). Watch with `gh run watch <id> --exit-status`.

**Verification:** After workflow completes, `gh release view v0.7.1 --json assets` confirms `lewing.helix.mcp.0.7.1.nupkg` is attached. Release URL: `https://github.com/lewing/helix.mcp/releases/tag/v0.7.1`

**All steps confirmed working 2026-05-21 v0.7.1 release.**
