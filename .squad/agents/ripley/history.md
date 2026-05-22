## Learnings — Release flow v0.7.3 (2026-05-22T14:04:00-05:00)

**Release execution summary:**
- Synced main with `git pull` (already up-to-date, 2211115).
- Bumped three version stamps: `HelixTool.csproj` line 12 + `server.json` top-level + packages array (all 0.7.2 → 0.7.3).
- Build: 0 errors, 0 warnings (10.58s).
- Tests: 1292/1292 passed (3s) — baseline 1180 + 112 from prior extensions.
- Version commit: 73e65fd (`release: v0.7.3`).
- Main push: `2211115..73e65fd`.
- Tag: `v0.7.3` annotated, pushed to origin.
- Workflow run: 26306754843, completed in 41s (all jobs green).
- Release verification: `https://github.com/lewing/helix.mcp/releases/tag/v0.7.3`, asset `lewing.helix.mcp.0.7.3.nupkg` attached.
- NuGet indexing: Package pushed to nuget.org; appears within typical 5-10 min indexing window.

**Pattern confirmation:**
- Third consecutive haiku-driven mechanical release (v0.7.1, v0.7.2, v0.7.3) executed flawlessly with zero deviations.
- Release recipe is fully automated and repeatable: version bumps → build+test gate → commit → push main → tag+push → workflow auto-creation of release and NuGet push.
- Sed-based `.json` edits continue to work reliably (no Unicode em-dash issues encountered in v0.7.3).

**All releases follow the established recipe with 100% consistency.** Recipe is production-ready.

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

## Learnings — Strict Coordinator Dispatch Rule (2026-05-21 v0.7.1)

- Coordinator dispatched the entire v0.7.1 release workflow to Ripley (claude-haiku-4.5) per the strict role-to-model map, with no ad-hoc hand-offs or human intervention mid-stream.
- Ripley executed cleanly: merged PR #54, bumped 3 version stamps (HelixTool.csproj + server.json variants), pushed tag, watched publish.yml workflow complete (26243596534, 33s, all green), verified asset on nuget.org.
- No deviations from the established pattern (ship-after-merge, tag-based trigger, ncipollo/release-action auto-creation).
- **First release enforcing strict dispatch rule end-to-end.** Success signal for scaling Squad's role-based task routing.

## Learnings — v0.7.2 Design: Surface WorkItemSummary fields (2026-05-21 Dallas)

Dallas filed design proposal in `.squad/decisions/inbox/dallas-surface-workitem-fields.md` (Brady approved option B: surface + optimize `GetJobStatusAsync`). Proposal details interface changes to `IWorkItemSummary`, adapter wiring, ~95% API call reduction for jobs with mostly-passing items, test plan, and risks. Extracted reusable skill guidance to `.squad/skills/sdk-adapter-extension/` for future SDK field surfacing. Ripley to implement on branch `feat/workitem-summary-exit-code`.

## Learnings — v0.7.2 implementation notes (2026-05-21)

- SDK-backed summary fields must be added in lockstep across `IWorkItemSummary`, `HelixApiClient.WorkItemSummaryAdapter`, and `CachingHelixApiClient.WorkItemSummaryDto`; leaving any layer behind silently drops the new data.
- `GetJobStatusAsync` optimization pattern: branch on `summary.ExitCode` immediately after `ListWorkItemsAsync` — `0` can return a lightweight passed `WorkItemResult`, while `null` and non-zero must still flow through the throttled detail-fetch path.
- Nullable SDK fields are the compatibility boundary: `int? ExitCode` and `string? ConsoleOutputUri` must stay nullable so older cached payloads and older server responses deserialize to `null` and trigger the detail fallback instead of being misclassified.
- For this repo's feature shipping flow, branch creation + validation + draft PR was: `git switch -c feat/workitem-summary-exit-code`, `dotnet build --nologo`, `dotnet test --nologo --no-build`, then `gh pr create --draft` once the branch was pushed.

## Learnings — v0.7.2 release flow (2026-05-21 Ripley mechanical release)

**Release execution summary:**
- PR #55 merged upstream; synced main with `git pull` (fast-forward to eb3bb74).
- Bumped three version stamps: `HelixTool.csproj` line 12 + `server.json` top-level + packages array (all 0.7.1 → 0.7.2).
- Build: 0 errors, 0 warnings (10.73s).
- Tests: 1195/1195 passed (4s) — 1180 baseline + 15 new from Lambert's test suite additions.
- Version commit: 6f9262a (`release: v0.7.2`).
- Main push: `eb3bb74..6f9262a`.
- Tag: `v0.7.2` annotated, pushed to origin.
- Workflow run: 26245033630, completed in 38s (all jobs green).
- Release verification: `https://github.com/lewing/helix.mcp/releases/tag/v0.7.2`, asset `lewing.helix.mcp.0.7.2.nupkg` attached.

**Pattern confirmation:**
- Stashing local state before branch checkout remains necessary (`.squad/agents/dallas/history.md` had uncommitted changes).
- sed-based version edit for `.json` files works when edit tool encounters Unicode characters (`\u2014` em-dash); fallback to line-targeted sed on duplicate patterns.
- `gh run watch <id>` polls until workflow completion (no timeout trap; Ctrl+C cancels but doesn't affect upstream workflow).
- Publish workflow triggers exclusively on tag push; main branch push alone does nothing.
- Release asset attachment is confirmed via `gh release view v0.7.2` (no asset listing needed; presence implies successful NuGet push).

**All three release runs (v0.7.0, v0.7.1, v0.7.2) executed identically with no deviations from the recipe.** Recipe is solid.

## Learnings — AzDO timeline filter presets (2026-05-22T13:03:40-05:00)

- Shared filter logic now lives on `AzdoService` as `public static` helpers because `HelixTool.Mcp.Tools` needs the exact same normalization/validation/predicate flow and `HelixTool.Core` only exposes internals to tests today.
- The clean MCP pattern is: keep `AllowedValues` canonical, run `NormalizeFilter(...)` before validation, and accept silent aliases (`inProgress`, `in-progress`, `active`, `notStarted`, `not-started`) without advertising them in schema.
- `azdo_helix_jobs` must relax its old issues-only gate for `running` / `pending` / `incomplete`; otherwise active Helix submission tasks disappear before issue text exposes a GUID. Returning `HelixJobId = ""` preserves the existing record shape while surfacing those state-based matches.
- Branch: `feat/azdo-timeline-filter-presets`. PR: #56.

## Team Update (2026-05-22)

**Lambert's PR #56 merged.** 97 unit tests for AzDO timeline filter presets (`running`, `pending`, `incomplete`, `issues`) and aliases (`inProgress`, `notStarted`, `in-progress`, `active`) now passing in main. Ripley's description tightening work on feat/mcp-description-tightening can proceed independently; no rebase required.

## Learnings — MCP description tightening pass (2026-05-22)

- For `[McpServerTool]` descriptions, follow the `mcp-server-design` rubric in `.squad/skills/mcp-filter-api-design/SKILL.md`: lead with a verb, stay around 20 words or less, and push defaults/filter enumerations down into parameter descriptions.
- Schema dumps and repo-specific/domain guidance belong in response content (`CiKnowledgeService` overview/profile text), not in always-loaded tool description metadata.
- The second-pass audit on 2026-05-22 showed description drift had already crept back in roughly three months after the prior tightening pass, so periodic re-audits are warranted.

## Team Update (2026-05-22 completion)

**PR #57 merged to main at 3c4728c.** Ripley completed description tightening on 8 tools (229 → 93 words, 136 recovered). Lambert fixed assertion coupling by routing devdiv knowledge verification to CiKnowledgeService response content. Dallas reviewed, approved, and merged; flagged two follow-ups: (a) establish quarterly description audit cadence, (b) restore azdo_builds→azdo_search_timeline cross-reference in future pass. Baseline decision recorded in decisions.md with full audit counts and pattern guidance for next drift check.

- [2026-05-22] v0.7.3 shipped (PR #56 + PR #57 → main → NuGet)

## Learnings — DTO consolidation refactor 2026-05-22

- Safe consolidation pattern here was **centralize DTO definitions into `src/HelixTool.Mcp.Tools/McpToolResults.cs`, but keep distinct CLI vs MCP types when wire formats differ**. The CLI `--json` contracts still rely on a mixed PascalCase/camelCase shape, so direct reuse of MCP DTOs would have changed output.
- The low-risk move was to add public CLI DTOs in the shared results file and alias them back into `Program.cs`, then delete the nested `Program.cs` copies. That removed the parallel definitions without changing command logic.
- Wire-compat verification worked best as a two-layer check: full `dotnet test --nologo --no-build` for Lambert's existing JSON tests, plus explicit `--schema` spot-checks on `status`, `files`, and `work-item` to confirm property casing stayed exactly where expected.
- The surprising detail was that the "duplicate" classes were only structurally close, not identical: MCP status includes `helixUrl` and camelCase attributes, while CLI status intentionally omits that field and leaves several properties PascalCase.
