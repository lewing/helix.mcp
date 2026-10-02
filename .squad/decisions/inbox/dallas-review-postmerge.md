# Dallas post-merge review gate — commit 85c5bd1

Date: 2026-10-02T15:55:00-05:00
Reviewer: Dallas
Commit: 85c5bd1 "Fix post-merge review findings on #154/#155 and SQLite NUL data loss"
Verdict: REJECT

I reject this gate on two release-blocking compatibility/safety gaps. The named regression-test coverage exists for all 18 Copilot post-merge findings (`*_Finding4168886770`, `*_Finding4168886828`, `*_Finding4168886873`, and `*_Finding4169308691` through `*_Finding4169309251`) and spot-checks align with the intended fixes. The additional PureWeen/NUL issue is materially fixed for newly written metadata and raw AzDO log rows, and the branch has coverage for large `\0raw\n` logs, marker-only corrupt rows, eval-mode invalid-response replay, snapshot validation, collect/export readback, and encoded acquisition-error JSON.

## Required fixes

1. `src/HelixTool.Core/AzDO/AzdoApiClient.cs:328-384` — continuation-token paging is unbounded. `GetListAsync` follows `x-ms-continuationtoken` until the server stops returning it, with no maximum page count and no repeated-token/URL guard. A malformed or hostile AzDO response can make `list_build_changes`, `list_builds`, `list_test_runs`, `list_test_results`, `list_artifacts`, `list_test_attachments`, and `list_build_logs` loop indefinitely, and collect calls those paths through `--all`/complete-key population. Add an explicit bound and cycle detection, fail with a structured acquisition error (`invalid_response` or equivalent existing provider-failure kind), and add tests for a repeated continuation token and for exceeding the page cap. The existing `BuildChanges_FollowsContinuationTokensBeforeComplete_Finding4168886770` proves the happy path only.

2. `src/HelixTool.Core/Cache/SqliteCacheStore.cs:280-293` and `src/HelixTool.Tests/SqliteCacheStoreTests.cs:53-63` — marker-prefix backward compatibility is incomplete. New writes correctly encode values that contain NUL or start with `hlx:nul-base64\n`, and invalid legacy marker-prefixed plaintext is now returned verbatim. But an existing #154/pre-fix plaintext metadata value beginning with `hlx:nul-base64\n` whose suffix is syntactically valid Base64 will still be silently decoded into different text. Either move new encoded rows to an unambiguous marker/version or make decode distinguish legacy plaintext from encoded rows (for example, only accepting decoded values that match the set this encoder can produce: contain NUL or themselves start with the marker). Add a regression where the legacy plaintext suffix is valid Base64 and must round-trip unchanged.

## Accepted review points

- The 18 post-merge findings have direct named test coverage, including complete-list cache keys, public eight-hex AzDO org partition parsing, disabled-cache rejection, synchronized/per-task attempt merging, resume verification, argv URL redaction, authenticated cache-key reporting, aggregate Helix failure truncation clearing, test-scope `all` replay for default failed results, cached selected-file resume, classified download failure recording/replay, pre-open byte-budget enforcement, eviction detection before export, selected-file retry, resume rehydration through retry handling, transient non-recordability, negative retry-delay rejection, and Retry-After behavior.
- SQLite metadata values are encoded before binding, acquisition-error content with NUL round-trips through JSON escaping, empty/marker-only raw AzDO log rows are treated as miss/invalid rather than success, and snapshot validation decodes metadata before checking corrupt AzDO log entries.
- The remaining raw-NUL rejection in structural TEXT columns is acceptable for this release after the two required fixes above: those columns are cache keys, job-state IDs, timestamps, table names, paths, and columns derived from sanitized org/project/build IDs or Helix job/work-item/file-name identifiers. Error messages and other arbitrary provider text flow through encoded metadata or JSON, not raw structural columns.
- The one-hour Retry-After safety ceiling is sensible: it honors server delays beyond `--retry-max-delay` without permitting an unbounded single sleep.
- Per-task collection merges are deterministic enough for the manifest: child work uses local attempt lists, merges by input index, and final attempts are sorted by phase/parent/id.
- Live-mode AzDO auth partitioning did not regress: collection resolves auth before constructing attempts, caching calls still refresh auth context before keys, eval-mode partition selection is structural, and manifest auth records the non-secret partition id.

## Release-note items if the required fixes land

- **Breaking changes:** CLI list commands now have JSON envelope/paging semantics and truncated output exits 2 unless explicitly allowed; `hlx collect azdo-build` has deterministic manifest/snapshot completeness semantics; eval-mode AzDO snapshots may require `HLX_EVAL_AZDO_PARTITION=public` or `cache-xxxxxxxx` when multiple partitions exist.
- **Security/privacy:** collection redacts URL credentials, query secrets, and fragments from serialized argv; auth-scoped snapshots use non-secret cache partition IDs rather than credential material.
- **Data-loss/corruption fix:** SQLite cache metadata now preserves NUL-containing values, including raw AzDO logs stored with `\0raw\n`. Users should clear local caches and re-collect/re-export snapshots made by v0.10.3 or earlier, because already-truncated zero-byte TEXT rows cannot be recovered. Marker-only or empty raw-log rows should be treated as corrupt evidence and recollected.
- **Reliability:** Retry-After is honored beyond the configured exponential-backoff cap with a one-hour safety ceiling; collect verifies successful evidence remains present before export and records replayable provider failures where supported.

---

# Dallas post-merge re-review gate — commit c6b7b9a

Date: 2026-10-02T16:20:00-05:00
Reviewer: Dallas
Commit: c6b7b9a "Bound AzDO continuation paging; unambiguous v2 metadata encoding"
Verdict: APPROVE

I approve this gate. The two required fixes are present and covered: `AzdoApiClient.GetListAsync` now caps continuation-token paging at `MaxContinuationPages = 1000`, detects repeated continuation tokens and repeated request URLs, and fails closed with `invalid_response`; regression tests cover the happy path, repeated token, and page-cap failure. `SqliteCacheStore` now writes an unambiguous v2 encoded metadata envelope (`hlx:b64:v2:<length>:<sha256>\n<base64>`), validates length and SHA-256 on decode, and only decodes legacy `hlx:nul-base64\n` rows when the decoded value proves it came from the old encoder (contains NUL or starts with an encoded marker), so valid-Base64 plaintext no longer collides.

Additional release-blocker checks passed: snapshot validation decodes metadata before raw-log corruption checks, exported/eval snapshots replay collected AzDO/Helix manifest items, v1 snapshots remain accepted with compatibility warnings, docs/changelog/README explain the 1000-page fail-closed behavior and v0.10.3-or-earlier cache/snapshot remediation, and `git log origin/main..HEAD` contains only the three intended review-fix commits. Local targeted validation passed: `DOTNET_ROLL_FORWARD=Major dotnet test src/HelixTool.Tests/HelixTool.Tests.csproj --no-restore --verbosity minimal --filter "FullyQualifiedName~AzdoPagingPr1Tests|FullyQualifiedName~SqliteCacheStoreTests|FullyQualifiedName~SnapshotExportTests|FullyQualifiedName~SnapshotEvalModeTests|FullyQualifiedName~AzdoBuildCollectorPr2Tests"` = 78 passed / 0 failed / 0 skipped. No blocking issues remain for merge or release.
