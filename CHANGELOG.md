# Changelog

All notable changes to helix.mcp are documented here. Versions follow [semantic versioning](https://semver.org/).

For releases prior to v0.7.6, see the [GitHub Releases page](https://github.com/lewing/helix.mcp/releases).

---

## [Unreleased]

### `azdo_evidence_plan` — Failed job → evidence artifact planner (MCP + CLI)

New read-only tool for planning which artifacts correspond to failed or canceled jobs in an AzDO build. Maps jobs to evidence via two strategies:

- **Primary:** GUID join (`artifact.source == job.id`) — 100% resolution on real builds, handles retried attempts correctly by construction.
- **Fallback:** Normalized-exact name matching (dotnet/runtime PR #132609 parity) — for unmapped jobs when GUID join leaves gaps.

Matching strategy is configurable via `--match` parameter: `auto` (default, recommended), `source-id`, `normalized-exact`, or `exact`. The `exact` strategy uses ordinal-ignore-case equality after prefix stripping, with no normalization.

**CLI:** `hlx azdo evidence plan <buildId> [--job-results RESULTS] [--artifact-pattern PAT] [--artifact-job-prefix PREFIX] [--keep-attempt-prefix] [--match MODE] [--json]`. `AttemptN_` is stripped and recorded by default; pass the bare `--keep-attempt-prefix` flag to retain it. The former `--strip-attempt-prefix` spelling is no longer recognized, but it only restated the default: remove it from scripts rather than replacing it with the opposite-meaning keep flag.

**MCP:** `azdo_evidence_plan(buildIdOrUrl, jobResults?, artifactPattern?, artifactJobPrefix?, stripAttemptPrefix?, match?, ...)` → structured plan with status per job (`mapped`, `ambiguous`, `missing`), ranked candidates, and completeness signal. MCP retains the positive `stripAttemptPrefix` parameter (default `true`); CLI `--keep-attempt-prefix` is its inverse.

**Exit codes:** `0` complete, `2` incomplete-but-useful (plan still output), `1` error.

**Key properties:**
- Never silently chooses ambiguous candidates — the full candidate count is reported, retained candidates are ranked, and the entry has `status: "ambiguous"`.
- Preserves attempt numbers for deterministic ranking (real builds retry with `Attempt1`, `Attempt2`, etc.).
- Read-only planning boundary: no download, extract, or write operations; analysis remains in binlog-mcp.
- Completeness contract: `complete` signals whether all jobs are unambiguously mapped and no output was truncated. `incompleteReasons[]` explains gaps.
- Warning contract: `warnings[]`, `warningTotal`, and `warningsTruncated` are always present. `warnings` contains the first 10 original diagnostics in deterministic order (never a synthetic truncation member); `warningTotal` reports the pre-cap count and `warningsTruncated` reports whether any were omitted.
- Structured output: job records (including timeline order and attempt when present), candidate artifacts (name, id, size, download URL, type, source GUID, attempt), and build provenance (PR metadata if applicable).
- Partial-response bounds: 200 entries, 10 candidates per entry. Every entry reports `candidateTotal` and `candidatesTruncated`; candidate overflow also supplies `candidateNote`. Entry or candidate overflow sets plan-level `complete: false` and `truncated: true`; `totalEntries` reports the selected-job total and `note` summarizes the truncation.

**Why `auto` is default:** Testing on real failed builds shows normalized-name matching (PR #132609) has a 12.7% miss rate because AzDO job display names include a matrix-leg `crossaot` suffix that artifact names omit, and 100% ambiguity on retried attempts. `auto` uses the source-GUID join first and normalized-name fallback only for unmapped jobs; `source-id` is the source-ID-only mode.

### Fixed: `StringHelpers.MatchesPattern` — trailing-`*` prefix globs now work

`MatchesPattern("Logs_Build_Attempt1_x", "Logs_Build_*")` now returns `true`. Previously, trailing-`*` globs (prefix patterns) matched nothing because they were treated as literal substrings.

This fixes `hlx azdo artifacts --pattern 'Logs_Build_*'` and `azdo_artifacts(pattern: 'Logs_Build_*')`, which are now usable for evidence planning. The fix is additive and ReDoS-free (O(n) scan with early exit). Suffix globs (`*.binlog`) and bare-substring matching remain unchanged.

---

## [v0.9.1] — 2026-07-28

### helix_find_files — optional `workItem` parameter for faster, scoped searches (#117)

`helix_find_files` now accepts an optional `workItem` parameter, making it compatible with all other Helix job-inspection tools and eliminating the hard parameter-rejection error callers hit when passing `workItem` to this tool.

When `workItem` is supplied, the search is scoped to that single work item (equivalent to calling `helix_files` on that item and filtering by pattern), avoiding costly scans of up to 50 work items.

This fixes a common LLM error: calling models that passed `workItem` to `helix_find_files` encountered a strict parameter-rejection error because it was the only work-item-scoped tool in its family missing that parameter. Now all work-item-scoped Helix tools accept `workItem`; `helix_status` and `helix_batch_status` intentionally remain job-scoped and do not expose `workItem`.

Additional fixes in PR #117:
- Missing work items now report a specific "work item not found" error instead of "Job not found".
- Fixed a whitespace-predicate mismatch (`IsNullOrEmpty` vs `IsNullOrWhiteSpace`) that caused `workItem: " "` to silently scan the whole job while the tool reported only one item scanned.
- Added a reflected schema guard asserting every `jobId`-taking tool also takes an optional `string? workItem` (with explicit exceptions for `helix_status` and `helix_batch_status`).

### Squad governance (#117)

Scribe agents can now commit agent-extracted skills directly, removing a manual step from the skill-extraction workflow.

---

## [v0.9.0] — 2026-07-14

### Containerized MCP server (#77)

New `Dockerfile` publishes a multi-platform stdio MCP image (`linux/amd64`, `linux/arm64`) to `ghcr.io/lewing/helix.mcp` on release tags.

### Canonical Helix-side job enumeration for AzDO builds (#92, #96)

`azdo_helix_jobs` now resolves job IDs via canonical Helix metadata (`Job.ListAsync`); AzDO timeline task-name parsing is retained as fallback when Helix returns no results.

### Arcade alignment — JobDetails fields and test-file extensions (#93, #95)

`JobDetails` field surface aligned with arcade canonical definitions; expanded test-result file extension recognition.

### Work item `ExitCode` and `ConsoleOutputUri` (#91, #94)

`WorkItemSummary` now surfaces `ExitCode` and `ConsoleOutputUri` following the `Microsoft.DotNet.Helix.Client 11.0.0-beta.26325.102` bump.

### HTTP 204 No Content handling (#105, #106)

AzDO GET helpers now treat 204 No Content as an empty result instead of throwing; consistent with 200 + empty-body responses.

### Dependency updates

- `Microsoft.DotNet.Helix.Client` → 11.0.0-beta.26325.102 (#91, #94)
- `actions/checkout` (#103), `actions/setup-dotnet` (#107), `zizmorcore/zizmor-action` 0.5.6 → 0.5.7 (#104)
- Docker CI actions: `docker/build-push-action` → 7.3.0 (#108), `docker/metadata-action` → 6.2.0 (#109), `docker/setup-buildx-action` → 4.2.0 (#110), `docker/setup-qemu-action` → 4.2.0 (#111), `docker/login-action` → 4.4.0 (#112)

---

## [v0.8.0] — 2026-06-24

### Strict parameter rejection with "Did you mean?" hints (#81, PRs #83/#84/#87)

MCP tools now reject unknown or mistyped parameter names immediately, with a structured error message that includes:
- A **"Did you mean: X?"** suggestion when the unknown name is close to a known parameter (Levenshtein distance ≤ 6)
- The **full list of allowed parameter names** so callers can self-correct without consulting docs

Previously, unknown params were silently dropped (the SDK discarded them before invoking the tool). This change turns silent data-loss failures into immediate, actionable errors.

Example response when an LLM passes a hallucinated param name:
```
Unknown parameter 'minFinishTime' for tool 'azdo_builds'.
Did you mean: minTime?
Allowed parameters: org, project, top, branch, prNumber, definitionId, status, minTime, maxTime, queryOrder
```

### AzDO parameter plumbing — `minTime`/`maxTime`, `outcomes`, `top` (#78)

Three parameters that were accepted by MCP tools but silently not forwarded to the REST API are now plumbed end-to-end:

| Tool | Parameter | Was | Now |
|------|-----------|-----|-----|
| `azdo_builds` | `minTime`, `maxTime`, `queryOrder` | accepted, dropped | forwarded to AzDO REST API |
| `azdo_test_results` | `outcomes` | hardcoded to `Failed` | forwarded; configurable (default still `Failed`) |
| `azdo_test_attachments` | `top` | accepted, dropped | forwarded to AzDO REST API |

All defaults preserve prior behavior — no breaking changes for existing callers.

`minTime`/`maxTime` filter the time field selected by `queryOrder`. For example, `queryOrder=finishTimeDescending` means `minTime`/`maxTime` filter by finish time. Default `queryOrder` is `queueTimeDescending`.

### Alias support (#75)

Tools that accept a build identifier resolve common parameter name aliases automatically before strict validation runs:

| Alias | Canonical | Tools affected |
|-------|-----------|----------------|
| `buildId` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `build_id` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `buildUrl` | `buildIdOrUrl` | AzDO tools that accept a `buildIdOrUrl` parameter |
| `result` | `resultFilter` | `azdo_search_timeline` |

### AzDO filter normalization (#82, PR #85)

Internal refactor — centralized AzDO filter normalization (trim, case-fold, default-collapse). User-visible side effects:

- `queryOrder` values are now sent lowercase in REST URLs (e.g. `finishtimedescending` instead of `finishTimeDescending`). AzDO treats this as case-insensitive; behavior is unchanged.
- Cache key format changed. One-time invalidation on deploy; self-heals within the normal TTL (≤ 30s for in-progress builds, ≤ 4h for completed builds).

### Dependency updates

- `ModelContextProtocol` 1.3.0 → 1.4.0
- `SQLitePCLRaw` pinned to 3.x for [CVE-2025-6965 / GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)

---

## [v0.7.6] — 2026-05-29

- **User-Agent identifier** (PR #73, @akoeplinger): All outbound HTTP traffic from hlx now carries `User-Agent: helix.mcp/{version}` and a custom `X-Helix-Mcp-Tool: helix.mcp` header on AzDO and Helix clients, enabling arcade-services to distinguish hlx traffic from other callers.
- **Work item status bucketing fix** (PR #71, backport of #70): `GetWorkItemDetailAsync` now applies `IsCompleted` bucketing correctly — in-progress and waiting work items are no longer miscounted as failed in detailed work item queries.
