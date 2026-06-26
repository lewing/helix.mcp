# SKILL: Helix Job Source Filter

**Purpose:** Look up all Helix jobs for an AzDO build using the canonical
`Job.ListAsync(source) + BuildId` filter pattern, instead of scraping AzDO timeline
task names.

---

## Problem

AzDO timeline scraping (`Name.Contains("helix", …)`) fails for repos where the
Helix dispatch task has a non-standard name (e.g., dotnet/sdk uses "🟣 Run TestBuild
Tests"). The Helix API has first-class support for this lookup via job source strings
and the `BuildId` job property.

---

## Source string formula (from arcade `HelixJobSource.Compute`)

```
source = "{prefix}/{teamProject}/{repository}/{sourceBranch}"

prefix rules (case-insensitive, mirror arcade exactly):
  Build.Reason == "PullRequest"    → "pr"
  System.TeamProject == "internal" → "official"
  anything else                    → "ci"
```

**Examples:**
- PR build on public: `pr/public/dotnet/runtime/refs/pull/42/merge`
- CI on main (public): `ci/public/dotnet/sdk/refs/heads/main`
- Internal official: `official/internal/dotnet/runtime/refs/heads/main`
- Manual on public: `ci/public/dotnet/arcade/refs/heads/main` (NOT "official")

`System.TeamProject` (AzDO variable) is in `Build.Project.Name` in the REST response.
`Build.Repository.Name` for GitHub-backed repos is `owner/repo` (e.g. `dotnet/runtime`).

---

## Filter logic (from arcade `HelixService.GetJobsForBuildAsync`)

```csharp
// count: 100_000 cap — generous for ~1k–5k jobs per build; realistic CI never hits it
var jobs = await helixApi.Job.ListAsync(source: source, count: 100_000);
return jobs.Where(j => j.Properties is JObject props
    && props.TryGetValue("BuildId", out var id)
    && id.ToString() == buildId.ToString());
```

Key points:
- `BuildId` is stored as a JSON string in `Properties`; compare via `ToString()` not int
- `JToken.ToString()` on a string-valued token returns the string value directly
- `Job.ListAsync(source)` does a prefix match — pass the full source string for exact match

---

## AzdoBuild fields required

```json
{
  "reason": "pullRequest | manual | individualCI | batchedCI | schedule | ...",
  "project": { "name": "public | internal | ..." },
  "repository": { "name": "owner/repo or repo-name" },
  "sourceBranch": "refs/heads/main | refs/pull/42/merge | ..."
}
```

Add these properties to the AzdoBuild model if they don't exist yet.

---

## Implementation pattern in this codebase

1. `IHelixApiClient.ListJobNamesByBuildAsync(source, buildId, count)` — returns job GUIDs
2. `HelixApiClient` calls `_api.Job.ListAsync(source, count)` and filters by BuildId
3. `CachingHelixApiClient` forwards without cache (source queries span many jobs)
4. `AzdoService(IAzdoApiClient, IHelixApiClient?)` — two-arg constructor
5. Helix-side query is primary; fall back to timeline if 0 results or exception
6. DI: inject `IHelixApiClient` into `AzdoService` at registration time

---

## Fallback strategy

- Helix returns 0 → fall back to timeline (handles in-progress builds, old jobs)
- Helix throws → fall back to timeline (auth failure, API down)
- Timeline-only mode: use single-arg `AzdoService(IAzdoApiClient)` constructor

---

## Reference links

- arcade `HelixService.cs:47-65`: https://github.com/dotnet/arcade/blob/b076228a542025c4f879f254d38adb5cf34a2475/src/Microsoft.DotNet.Helix/JobMonitor/Services/HelixService.cs
- arcade `HelixJobSource.cs:63-75`: https://github.com/dotnet/arcade/blob/b076228a542025c4f879f254d38adb5cf34a2475/src/Microsoft.DotNet.Helix/JobMonitor/HelixJobSource.cs
- Issue: https://github.com/lewing/helix.mcp/issues/92
