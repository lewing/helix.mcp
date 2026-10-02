---
date: 2026-10-02
author: Dallas
status: rejected
topic: PR156 review fixes gate
reviewed-commit: a20fb3639dbf9918171fded8c7c6ad90fd9ae9d5
---

# PR156 review-fix gate

## Verdict

REJECT for release. Accept the six direct Copilot fixes; block tagging on the unguarded all-test acquisition fan-out and its lack of observable progress. Lambert is eligible to revise Ripley's implementation.

This replaces the earlier draft with independently confirmed measurements. The worktree advanced to `e0a257a` during review (only local-manifest cleanup in that commit), and subsequently acquired other agents' uncommitted source changes. All finding locations below refer to `a20fb36`; those concurrent changes are not approved by this gate. No implementation files were edited.

## Hang diagnosis

Classification: **(a) volume, amplified by per-result attachment acquisition**, not **(b) a non-advancing paging loop** or **(c) the continuation-page cap** on the observed endpoint.

A temporary read-only diagnostic wrapped the current AzDO client HTTP handler to time each response, then called the same `AzdoService.GetTestResultsPageAsync(All = true)` path used by the CLI and collector. It had a 180-second cancellation deadline and a 240-second outer process deadline. Run `44916566` from build `1621192` completed in **66.745 seconds**, returning **133,036 distinct results**, IDs `100000..233035`, `complete=true`. HTTP-200 responses contained **285,338,304 bytes** before model projection. Every response lacked `x-ms-continuationtoken`.

| Page | `$skip` | Rows | Seconds |
|---|---|---|---|
| 1 | 0 | 10,000 | 6.602 |
| 2 | 10,000 | 10,000 | 3.878 |
| 3 | 20,000 | 10,000 | 5.386 |
| 4 | 30,000 | 10,000 | 4.189 |
| 5 | 40,000 | 10,000 | 4.247 |
| 6 | 50,000 | 10,000 | 4.250 |
| 7 | 60,000 | 10,000 | 3.792 |
| 8 | 70,000 | 10,000 | 3.785 |
| 9 | 80,000 | 10,000 | 3.931 |
| 10 | 90,000 | 10,000 | 4.603 |
| 11 | 100,000 | 10,000 | 5.858 |
| 12 | 110,000 | 10,000 | 5.467 |
| 13 | 120,000 | 10,000 | 5.320 |
| 14 | 130,000 | 3,036 | 3.480 |

The actual locally built CLI independently completed the following uncached command with a 180-second hard timeout in **60.765 seconds**, exit 0, `returned=total=133036`, `complete=true`, `truncated=false`, and 133,036 unique IDs:

```sh
HLX_CACHE_MAX_SIZE_MB=0 dotnet src/HelixTool/bin/Debug/net10.0/HelixTool.dll \
  azdo test-results 1621192 44916566 --all \
  --outcomes Passed,Failed,NotExecuted,Inconclusive,Timeout,Aborted,Error,NotApplicable \
  --json
```

`AzdoApiClient.cs:218-236` requests 10,000-row windows and advances `$skip` by returned rows. `GetListAsync` follows tokens when supplied, uses the remaining overall limit, and stops capped reads without overfilling their window. Its 1,000-page budget is local to each helper invocation. The observed no-token path makes 14 helper invocations of one page each, so it cannot hit that cap. Even a token-based 10,000-row traversal would need only 14 pages. A hypothetical provider returning 100 rows per token would exceed the cap for 133K results and fail explicitly as `invalid_response`, not hang; that is not the provider behavior measured here. Do not remove loop protection or raise the cap to address this incident.

The interrupted `pr156-live-20261002160512/cache/public/cache.db`, inspected read-only with SQLite immutable mode, contains **13 complete all-outcome run-result sets totaling 219,424 results**, their 13 derived `Failed` entries, and **43,427 distinct attachment-list entries, all `[]`**. Attachment creation timestamps span `21:05:15.3885100Z..21:20:26.6296480Z`. Thus the supposedly stalled phase was doing substantial hidden work.

The cached complete test-run list confirms **49 runs, 1,405,433 total results, five unanalyzed failures**. At `AzdoBuildCollector.cs:549-567`, all selected results, including passed/not-applicable rows, each trigger an attachment-list request. Without a separate selection policy, the full build entails about **1.4 million attachment-list calls** in addition to result paging. Raising concurrency or paging caps does not fix this acquisition amplification. The exact stopped run was not completed during this gate; diagnosis rests on advancing live result pages plus its persisted attachment work, not a claimed full all-scope success.

## Required fixes

1. **Build-wide all-result guard.** `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:486-493`, `src/HelixTool.Core/Collect/CollectPolicy.cs:29`, and `src/HelixTool/CollectCommands.cs:71,128`: before any all-outcome result acquisition, sum the already-collected runs' `TotalTests` using a safe wide accumulator. Add `--max-test-results` with a conservative default threshold (10,000 is a reasonable release default). Above that threshold, require an explicitly supplied budget at least as large as the build-wide estimate; an explicit but insufficient budget must still skip. Record the requested count, effective budget, stable skip reason, and remediation in manifest attempts/incomplete details. Budget refusal must remain `complete=false`, exit 2 by default; `--allow-incomplete` may permit exit 0 for this policy-only gap without changing completeness. Do not disguise refusal as `policy_excluded` that the current completeness calculation ignores. Preserve default failed-only behavior, capped MCP reads, resume handling, and older manifest readability. Add threshold-boundary, insufficient/raised-budget, manifest/exit, and resume regressions. Update help, manifest policy/cap fields, README/CLI reference, and release notes; `docs/cli-reference.md:531` must also stop advertising the now-removed `NotRunnable` outcome.
2. **Safe attachment selection independent of result scope.** `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:549-567`: collecting all result rows must not silently select all their attachments. Default attachment acquisition to failed/diagnostic results; offer wider coverage only through an explicit bounded attachment policy, and record exclusions/limits so the manifest accurately describes selected coverage. Keep existing failed-result attachment replay. Add a deterministic many-passed/few-failed regression asserting exact provider attachment-call counts and selected policy; do not merely assert that collection eventually returns.
3. **Observable tests-phase progress.** `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:190-191,493-510,549-567`, wired through `src/HelixTool/CollectCommands.cs:164-166`: report run count, estimated rows, guard decisions, per-run result start/finish and counts, and rate-limited attachment completed/selected/skipped counts to stderr. For a long single run, report page/row progress or an elapsed heartbeat rather than waiting silently for completion. Keep JSON stdout parseable, obey cancellation, and do not expose credentials or continuation tokens. Add stdout/stderr and cancellation coverage.

After revision, bounded live validation on `1621192` must demonstrate that ordinary `--test-scope all` produces an explicit policy skip rather than entering million-call acquisition, and that opted-in result collection uses safe attachment selection and visible progress. Revalidate default collect/resume/export, credential-free offline `Failed` replay, and snapshot validation. No full million-request reproduction is required.

## Accepted fixes and compatibility

- The shared parser covers every production metadata/build-state/list-key builder for valid org/project and provider outcome inputs. Its authenticated-first matching uses full suffix shapes, avoiding ambiguity when an org resembles an auth hash or a project is named `build`, `log`, `timeline`, or `log-fresh`. The deterministic builder-to-parser matrix passed.
- Raw-log validation uses the parsed resource kind. Suggested Helix fetches dedupe canonical job/tool/work-item coordinates; older duplicate-attempt manifests load with last-entry-wins behavior. Derived `Failed` rows have required manifest attempts and participate in final evidence verification. Streaming HTTP, I/O, and non-caller timeout failures enter retry classification, release reserved bytes, and remove temporary downloads.
- Comparing the #155 `df1ee3a` and #156 `f992541` key builders with this revision found no key-format migration: public/authenticated metadata keys, build-state keys, complete/window list versions, and legacy capped variants remain supported. Existing v1/v2 and metadata-envelope compatibility tests pass. This does not repair provider duplicates or previously lost SQLite log bytes in old snapshots; existing re-collect guidance still applies. No separately retained #155 live snapshot fixture was available for a new live replay claim.
- The retained #156 default snapshot validates with 128 metadata entries, one artifact, no acquisition errors, and no missing files. Current credential-free offline CLI replay returns exactly the five `Failed` results for run `44916260`. Capped MCP complete-key slicing and legacy-key fallback regressions pass; MCP signatures/output shapes did not change.
- The correctly matched focused paging/cache-key/collector/auth/immutability/SQLite/export/validator test selection passed **218 tests, six platform skips, zero failures**. The initial narrower selection passed 87 tests; those counts overlap and are not additive. The CLI build completed with zero warnings/errors. The reported full 2278-pass/9-skip suite was not rerun.
- All diagnostic processes completed; no long-running process started for this review remains. Detailed page evidence is retained in the session files `pr156-paging-probe.stdout` and `pr156-paging-probe.stderr`.

## Guard recommendation

**Must land before release**, together with safe attachment selection and stderr progress. The gate is not asking for a paging rewrite: paging advances correctly at this scale. It is requiring explicit acquisition-volume consent and an observable bounded collection policy before shipping the all-test scanner path.

---

# Final pre-tag gate - 2026-10-02

Reviewed HEAD: `0794f610dec677af8cacad49f535b3b44274a677`.
Read `git log origin/main..HEAD`, the review-fix diff against `origin/main`, the release
changes against `839a9bb`, the prior Dallas history/verdict, the independent review, and
Ripley's deviation record. Implementation and documentation were left unchanged.

## Verdict: REJECT

Accept Lambert's three required volume/progress revisions. Do not tag this HEAD:
independent finding 6 is not actually resolved, and the real-SDK classification fix missed
the Helix job-discovery endpoint. Documentation also needs correction before promotion.
Both Ripley and Lambert have authored the rejected implementation artifact; escalate its
revision to Larry. Kane is eligible for the documentation-only artifact.

## Release blockers

### R1 - Required Helix evidence is still evicted by optional downloads

Artifact: `src/HelixTool.Core/Collect/AzdoBuildCollector.cs:724-730`,
`CollectHelixSuggestedFetchesAsync`; related misleading guarantee:
`src/HelixTool/CollectCommands.cs:52`.

The reserve is computed before any required Helix fetches. It includes prior AzDO
attempt bytes, but excludes the required console artifacts that this same phase creates.
Required fetches and optional downloads share one traversal, so this is not merely the
concurrent-metadata residual risk described in Ripley's deviation.

A deterministic, fully offline probe reused the existing independent-review fixture with
`MaxConcurrency=1`, a 1,048,576-byte cache cap, a 614,400-byte required console and a
716,800-byte optional file. Both file caps were 1,048,576 bytes. The pre-Helix reserve was
only 4,766-4,767 bytes, so the optional file fit the advertised effective budget. Its cache
write evicted the previously acquired console. The file attempt remained `ok`; the required
console became `failed/not_in_snapshot`, with `artifact_missing`, `complete=false`, exit 2.
This reproduces the independent finding's evidence destruction without any race.

`OptionalDownloads_CannotEvictRequiredEvidenceUnderSmallCacheCap_IndepReview6` passes
because its optional files both exceed the small effective/per-file caps and are skipped.
It does not test an individually in-budget file whose combination with required Helix
artifacts exceeds the cache cap.

Required revision: protect the run's required artifacts across the whole acquisition,
including later/concurrent console fetches. If using budget clamping, finish required Helix
acquisition before determining actual artifact headroom and starting optional downloads;
coordinate publication/eviction so concurrent writes cannot invalidate that reserve.
Pinning collected evidence is another viable approach. Add the single-worker reproduction,
multiple-work-item/concurrent cases, and snapshot validation plus offline console replay.
Do not describe the current clamp as a guarantee that required evidence cannot be evicted.

### R2 - Helix job discovery still leaks Azure SDK exceptions

Artifact: `src/HelixTool.Core/Helix/HelixApiClient.cs:130-166`,
`ListJobsByBuildAsync`; propagation:
`src/HelixTool.Core/AzDO/AzdoService.cs:1105-1114`.

The six metadata/stream methods now use `ClassifyAsync`, including its
`Azure.RequestFailedException` branch. `ListJobsByBuildAsync` retains a separate catch
sequence that omits that exception. The real SDK wraps a transport handler's raw
`HttpRequestException` in `Azure.RequestFailedException`, as Ripley already discovered.

A fully offline probe used the existing real-SDK regression helper, its fake transport
throwing HTTP 403, and disabled SDK retries. Both direct `ListJobsByBuildAsync` and
`AzdoService.GetHelixJobsAsync` threw raw `Azure.RequestFailedException`.
The service now catches only `HlxAcquisitionException` on the primary discovery path,
so it cannot fall back to the timeline with `PrimaryAcquisitionError`. MCP's generic
wrapper likewise cannot produce the new acquisition-error envelope for this failure.

Required revision: route the job-list call through the same client-boundary classifier,
preserving caller cancellation and operation `list_helix_jobs_by_build`. Add real-SDK
transport-wrapper regressions and prove service fallback reports `complete=false` with
the classified primary error instead of throwing the provider exception.

### R3 - Release documentation is not fully accurate

Documentation artifact: `CHANGELOG.md:26`, `docs/cli-reference.md:679,716,760`;
source help also repeats the partition statement in `SnapshotCommands.cs`.

- The claim that every multi-partition snapshot requires `HLX_EVAL_AZDO_PARTITION`
  conflicts with `EvalSnapshotAzdoPartitionSelector.Select`: precedence is explicit
  environment selection, collector-manifest selection, single discovered partition,
  ambiguous-partition refusal, then public default. A probe exported a collector snapshot
  containing public and `cache-deadbeef` rows and observed automatic `public` selection
  with `Source=manifest`. Document that manifest selection is an existing selection,
  and reserve the fail-closed claim for ambiguous snapshots without one; otherwise Larry
  must deliberately change the implementation and tests to the documented contract.
- Export isolation works for the documented one-command scanner flow, but each implicit
  export cache is fresh. A subsequent `--resume --export` does not rediscover the old
  temporary root. Explain how to reuse the recorded `command.options.cacheDir` together
  with the prior manifest and a new export destination, or show an explicitly isolated
  `--cache-dir` from the first invocation. Supplying `--manifest` alone does not reuse
  the previous cache.
- Unreleased release notes should explicitly record the export-isolation/data-exposure
  fix, the cache-local standalone manifest default, mid-body transport classification,
  invalid-snapshot completeness, complete outcome coverage and TTL-independent final
  verification. The current review-fix changelog delta records only volume/progress.
- Align the optional-download documentation/help with the corrected R1 behavior.
  Manifest `policy.caps.maxTotalBytes` currently records the requested value, not the
  smaller cache-derived budget; do not present it as the effective capacity.

Kane may revise Markdown docs and CHANGELOG. The source-help and behavioral repairs
remain part of the implementation escalation to Larry.

## Required checks and accepted changes

| Requirement | Result at this HEAD |
| --- | --- |
| Build-wide result guard | Accepted: 64-bit sum; inclusive 10,000 default boundary; explicit sufficient consent; negative totals fail closed; required `test_result_limit`; resume rechecks; policy-only allow-incomplete preserves false completeness. |
| Independent safe attachment policy | Accepted: diagnostic outcomes by default; 1,000 build-wide cap; wider all scope requires an explicit positive cap; deterministic run/result selection; aggregate exclusions/limits; failed-result offline replay preserved. |
| Observable progress | Accepted: synchronized stderr reporting, two-second throttling, five-second acquisition heartbeat, cancellation joins timer tasks, JSON stdout remains parseable. |
| Independent finding 1 | Accepted for the reviewed AzDO body reads and Helix console streams: classified transport/timeout failures, retries, failed attempts and persisted manifests; caller cancellation remains cancellation. |
| Independent finding 2 | Accepted for the six named cached metadata/stream operations, with real-SDK negative recording/replay coverage. R2 is a separate adjacent job-list classification gap. |
| Independent finding 3 | Accepted at the CLI boundary: export without cache-dir creates a fresh per-run root before lazy cache-store construction; unrelated build/auth/artifact/negative rows stay out. Explicit cache-dir exports still include that directory's whole cache. |
| Independent finding 4 | Accepted: validation failure sets complete=false, exit 1 and snapshot_validation_failed in both persisted manifests. |
| Independent finding 5 | Accepted: all 15 supported TestOutcome values, excluding rejected NotRunnable; derived Failed rows retained and verified. |
| Independent finding 6 | Rejected: deterministic required-console eviction, R1 above. |
| Independent finding 7 | Accepted: SQLite presence verification bypasses TTL without changing ordinary live TTL reads; expired-but-present test rows export and replay. |
| Test seam | Accepted: CreateForTesting is internal static; the SDK-taking constructor is private; public reflection lookup finds no seam; existing test-only friend assembly is unchanged. |
| Manifest cleanup/default | Accepted: accidental tracked manifest removed and ignored; default standalone manifest is effective-cache-root/hlx-collect-manifest.json; exported manifest remains manifest/hlx-collect-manifest.json. |

The six original Copilot fixes remain accepted: structured cache-key parsing, raw-log key
recognition, suggested-fetch deduplication and legacy duplicate-manifest handling, derived
Failed-key verification, streamed file-read classification/budget release, and correctly
bounded continuation windows. Existing v1/v2 snapshot and legacy capped-key compatibility
remain covered; no new live #155 fixture was available.

## Validation and scope limits

- Reran the focused selection: 136 passed, zero failed/skipped.
- Reran the complete suite with `DOTNET_ROLL_FORWARD=Major`: 2,330 passed, 9 skipped,
  zero failed, no compiler/analyzer warnings. The installed SDK/runtime is .NET 11 preview;
  the projects remain net10.0. Green tests do not cover the two reproduced blockers.
- Independently ran the actual current CLI on build 1621192 with an isolated cache and
  logs/Helix disabled: ordinary all scope refused 1,405,433 estimated rows in 2.16 seconds,
  exit 2, zero attachment attempts, one required budget skip. JSON parsed and stderr
  included the 49-run estimate and remediation.
- Lambert's 311.22-second opted-in live acquisition of 1,405,433 rows/five attachment
  lists and live default/resume/offline runs are corroborating reported evidence, not
  reruns performed by this final gate. Their temporary snapshots were already cleaned.
  This gate separately exercised offline replay/export through the regressions.
- No production, test, README, CLI-reference or CHANGELOG edits; only the requested
  decision/history appends and disposable diagnostic/test artifacts. No tag, commit,
  branch change or long-running review process.

## Final release-notes items for the next version

- **Breaking:** Five AzDO CLI list commands now return envelopes instead of arrays
  (`changes`, `test-runs`, `test-results`, `artifacts`, `test-attachments`): consume
  `.results[]`. Partial list/evidence output exits 2; use `--all` or intentionally
  opt into `--allow-truncated`. Collect's standalone manifest defaults to the effective
  cache directory rather than CWD.
- **Security:** Collector export without `--cache-dir` uses a fresh isolated cache,
  preventing unrelated builds/auth partitions from being bundled. Recorded argv and
  acquisition-error URLs redact credential-bearing userinfo/query/fragment components.
  Explicit cache-dir and standalone snapshot exports still export their whole cache.
- **Data loss:** Lossless SQLite NUL/marker-prefixed metadata encoding and corrupt
  raw-log validation prevent empty AzDO log replay. Old lost bytes cannot be recovered:
  clear affected v0.10.3-or-earlier caches and re-collect/validate snapshots.
- **Features:** Deterministic `collect azdo-build`, manifested completeness/resume/
  retries/export, credential-free replay, schema-v2 recorded provider failures,
  complete-list CLI paging/full logs, and Helix-aware queue-monitor evidence plans.
  Include the 10,000-result default consent guard, independent diagnostic attachments
  with a 1,000-request cap, explicit wider attachment budgets and stderr heartbeats.
- **Fixes:** Advancing bounded continuation paging with repeated-token/page-cap
  protection; legacy cache-key/snapshot compatibility; fetch deduplication and derived
  Failed replay verification; mid-body failure classification/retries; fail-closed
  snapshot-validation manifests; full supported test-outcome coverage; TTL-independent
  final evidence checks; streaming download caps/retry-budget release; and provider
  Retry-After handling with a one-hour safety ceiling.

Do not advertise required-evidence eviction prevention or completely classified Helix
job discovery as fixed until R1/R2 are repaired and re-gated.

---

# Release re-review - 2026-10-02, HEAD 083e252

Reviewed `083e252c5539f3f8cf97b3c7ec237e7e27622222` with Larry's explicit override
authorizing Lambert's implementation revision. **REJECT for release, documentation only.**
R1 and R2 are resolved and accepted; the remaining changes belong to Kane's Markdown
documentation/CHANGELOG artifact. No further implementation repair is requested.

## Accepted code and reproduced outcomes

- Recreated and reran my original fixture-based eviction probe at concurrency 1 and 6:
  1 MiB cache, 600 KiB required console, 700 KiB optional file, 1 MiB file/total limits.
  Both now finish complete=true/exit 0, skip the optional file as total_size_limit,
  retain the 614,400-byte console in credential-free offline replay, and export valid
  snapshots. The required phase finishes and verifies before optional acquisition;
  actual occupied artifact capacity is reserved, with atomic accounting for new writes.
- New retention regressions also cover two synchronized optional streams, exact-cap
  success, resume without double-counting existing bytes, and unrelated cache artifacts
  becoming more recently used. RequiredEvidenceRetentionTests is a partial of
  IndependentReviewRegressionTests, so the focused selection includes these cases.
- Reran the real SDK probe with the original fake transport throwing HTTP 403:
  ListJobsByBuildAsync now throws HlxAcquisitionException/access_denied, preserves
  HTTP 403 and list_helix_jobs_by_build, and retains Azure.RequestFailedException
  only as InnerException. AzdoService.GetHelixJobsAsync returns timeline fallback
  with complete=false and the classified PrimaryAcquisitionError, not a raw SDK
  exception. All seven public provider methods use ClassifyAsync; caller cancellation
  regressions pass. CreateForTesting remains internal.
- Focused independent/retention/discovery selection: 74 passed, zero failed/skipped.
  Full suite rerun: 2,351 passed, 9 skipped, zero failed and no compiler/analyzer
  warnings, using DOTNET_ROLL_FORWARD=Major as before.

## Remaining documentation-only blocker

`CHANGELOG.md:25-30` contains four inaccurate new release entries:

- Line 25: transport/timeout failures are not negative-cached, and un-retried/exhausted
  transient failures produce `failed`, not `recorded_failure`. Describe both outcomes
  according to the existing recording policy, rather than promising transient replay.
- Line 26: the "does not yet cover Helix job-discovery ... tracked separately" statement
  is obsolete in this same commit. All seven client operations classify SDK errors.
  Keep the distinction that only the six cached operations have negative-cache replay;
  discovery carries its classified primary error in the timeline fallback.
- Line 27: standalone `hlx snapshot validate` reports validity and sets its process exit
  code; it does not rewrite any collector manifests. It is the collector's post-export
  validation branch that sets complete=false/exit 1 and snapshot_validation_failed
  in both manifests. Name that operation accurately.
- Line 30: the old "reducing (not eliminating) ... still evict ... hardened further"
  description predates the accepted R1 revision. Document the required-first phase and
  separate requested-total/remaining-new-artifact budgets, including resumed-file
  accounting, rather than claiming the original ordering bug remains. State the
  guarantee for this collector's own optional writes, not unrelated external writers.

`docs/cli-reference.md:681-698` correctly explains that an implicit cache is not
rediscovered and that manifest alone cannot resume it, but its example passes
`--manifest /tmp/snap-v1/manifest/hlx-collect-manifest.json`. This option is both the
resume input and the new output: CollectAzdoBuildAsync/WriteManifestAsync overwrite
that prior exported bundle's manifest with the resumed run's policy/attempts and snap-v2
path while leaving snap-v1's database/artifacts unchanged. Use the original standalone
manifest under the recorded cache's effective public directory, or copy the old manifest
to a writable standalone path before resuming. Do not mutate the uploaded/source bundle.
Also qualify the paragraph's "normal hlx cache root by default": a new invocation with
--export and no cache-dir creates another fresh isolated root.

Kane is eligible to revise these Markdown-only items. Partition selection precedence,
export isolation and standalone/exported manifest-path descriptions otherwise match
source. The updated CollectCommands max-total-bytes help correctly distinguishes
requested limits from cache-derived headroom. SnapshotCommands' multi-partition message
describes standalone exports, which do not include a collector manifest selection.

## Release-note adjustment after the accepted code revision

The prior breaking/security/data-loss/features items remain valid. Add to **Fixes**:
required Helix evidence is acquired/verified before optional downloads, and shared
atomic budgets reserve occupied artifact capacity without double-counting resumed
files; all seven Helix client operations classify SDK failures, with structured
job-discovery timeline fallback and preserved caller cancellation.
Do not imply transient negative replay or standalone-validator manifest mutation.
The inaccurate CHANGELOG text above must be corrected before release approval.

Only the requested decision/history records were appended. No implementation, tests,
release docs, tag or commit were changed. Disposable probes were cleaned and all
review command processes completed.

---

# Documentation re-review - 2026-10-02, HEAD 3bc3c5b

Reviewed `3bc3c5b8f7a2bae14ab535c2e1238bf5fd52c5fa` against source.
**REJECT, one documentation-only correction; Kane remains eligible.**
The four CHANGELOG corrections now match the accepted implementation. Code approval
and the previously rerun probes/full suite at 083e252 remain valid; this commit changes
only CHANGELOG and the CLI reference, so no new runtime validation was needed.

The resume example at `docs/cli-reference.md:686,696` omits the effective cache's
`public/` subdirectory. `command.options.cacheDir` is the base directory
`/tmp/hlx-collect-cache/<guid>`, while CacheOptions.GetEffectiveCacheRoot appends
`public` in the CLI and ResolveManifestPath writes the default standalone manifest
there. The real path is therefore
`/tmp/hlx-collect-cache/<guid>/public/hlx-collect-manifest.json`, not the example's
`/tmp/hlx-collect-cache/<guid>/hlx-collect-manifest.json`.

The example points --resume at a nonexistent prior manifest; LoadPriorAttemptsAsync
then supplies an empty prior-attempt set and writes a new file at the wrong location.
Correct both the explanatory comment and explicit --manifest path, or omit --manifest
from the resume command: the supplied original --cache-dir already makes the default
resolve to the correct standalone manifest. Do not change the base --cache-dir to
include public, which would instead create another nested effective cache directory.

All other reviewed documentation changes and the adjusted release-note list are
accepted. No source, test or release-document edits were made by this gate.

---

# Final release approval - 2026-10-02, HEAD 9a939a4

**APPROVE** `9a939a456714d4420ab0efb91815542ca53f083b` for release.
This supersedes the earlier rejected gate verdicts; no release blockers remain.

Verified the documentation-only HEAD against CacheOptions.GetEffectiveCacheRoot,
CollectCommands' base-cache selection, and AzdoBuildCollector.ResolveManifestPath.
The resume example keeps the recorded base --cache-dir, omits --manifest, resolves
the existing standalone manifest under the effective partition directory (public/
for this CLI example), and exports to a new destination without rewriting snap-v1.
The flag and manifest-shape descriptions now distinguish the base directory from
the effective partition directory.

All earlier implementation and CHANGELOG corrections remain accepted. The breaking,
security, data-loss, features and fixes release-note list is confirmed, including
the 083e252 additions for required-evidence retention/atomic optional budgets,
resumed-file accounting and classified SDK job-discovery timeline fallback.

The independently rerun 083e252 probes and full suite (2351 passed, 9 skipped,
zero failed, no compiler/analyzer warnings) remain applicable: the two subsequent
commits change only release documentation. No extra runtime validation was needed.
Only requested decision/history records were appended; no code/docs/test edits,
tag, commit or long-running process was started by this approval.
