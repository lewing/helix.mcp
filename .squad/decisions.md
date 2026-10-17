
| Build | Raw timeline records | Matched / selected | First-page rows | Stdio wire bytes | HTTP body bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1621192 | 1524 | 14 / 25 | 2 | 10020 | 10042 |
| 1600801 | 189 | 11 / 15 | 5 | 12110 | 12131 |

Both defaults are below 12288 bytes. 1621192's first row is Monitor Helix Jobs, record 0ca3371c-1de8-59d8-daef-35ea5811e911, log 1466, monitorEvidence=parsed. Its first preview explicitly names System.Diagnostics.Process.Tests, Helix job d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a and exit code -3, ahead of the generic Bash error. 1600801's first three rows are failed DeviceTests Tasks with logs 122/146/136 and their PowerShell errors. Neither default contains Phase rows.

Walked every default stdio page: 1621192 returned 25 distinct IDs over 24 advancing pages, maximum 10020 bytes; 1600801 returned 15 distinct IDs over three pages, maximum 12110 bytes. No duplicates/gaps; final nonzero pages correctly remain incomplete with next=null.

Independently reran the four targeted contract classes: **95 passed / 1 failed / 96 total**, reproducing exactly the 4351-byte receipt against 4096. The initial testhost launch needed DOTNET_ROLL_FORWARD=Major because this host has .NET 11 preview rather than .NET 10; rerun used the already-built HEAD assemblies. Compilation emitted no warnings. The supplied full-suite result, 2479 passed / 1 failed / 9 skipped, is corroborating handoff evidence, not a new full-suite run by Dallas.

Offline old-key replay for triage/compact/full/summary, immutable snapshot DB hashes, scoped runtime evidence delivery outside snapshots, UTF-8/base64 reconstruction, CLI envelope/raw compatibility and CLI explicit-output contracts passed. These provider-derived fixtures are reduced slices (4 and 19 records), not the full 1524/189-record timelines; that explains why the broader live defects were not gated by their 95 passing cases.

Live CLI --json used .results[], no Phase rows and exit 2 (10 of 25 selected, 28234 stdout bytes). --filter all --raw-json used the legacy id/records shape, retained all 1524 records/Phase rows and exited 0 (1049231 stdout bytes). Its explicit raw output is intentionally not an MCP-sized response.

All stdio/HTTP servers started by this review were terminated and waited for, including the initial HTTP /mcp probe (the actual mapped endpoint is /). Specifically issued delivery files and sidecars were deleted. No shared delivery/cache directory was deleted, no source/test files were reverted, and no commit was created.

## Concurrent HEAD delta - ce0631b

Before final handoff, HEAD advanced to `ce0631bd22134e0f78bf6652edaab22a5755894b`. Reviewed its entire three-file delta against a6e24e9: only the selectedTotal>1 overage restriction and HTTP/stdio 150-repeat boundary cases changed. Rebuilt and ran the seven directly affected receipt/default-wire cases: **6 passed / 1 failed**, with the same 4351-byte receipt failure. Accept the single-selection overage restriction; it satisfies that part of required fix 1. The 8 KiB normalization/final-wire accounting and the other required fixes are unchanged. **REJECT also applies to ce0631b.** Earlier live measurements remain measurements of a6e24e9, not a claimed new live run of ce0631b.

## Requested working-tree re-review - 2026-10-03

Reviewed `git diff`, not just a6e24e9, after the boundary-case follow-up. The selectedTotal>1 fix is now committed in ce0631b. A newer uncommitted budget change goes further: it calculates the incremental cost of the unshrunk row against its cleared-rows envelope and allows the fixed 1024-byte tolerance only when that row costs at most 2048 bytes. This correctly distinguishes an oversized name from ordinary row content even when multiple records are selected; accept the outlier-row materialization part of this change, subject to required fix 1's advancing-page/final-wire conditions. Do not interpret this as blanket approval of over-target ordinary rows or a revived 4x allowance.

Rebuilt and ran all current default-wire boundary cases plus the 4096 receipt case: **12 passed / 1 failed / 13 total**. The helper blob hash was `1b1606312ee2605c68c63ca326e6082dd06c1f3f` before and after this run, so those results apply to the inspected working-tree helper. Both single-row HTTP/stdio 150/200/400 cases and two-row HTTP/stdio 120/130/140 cases passed. Additional read-only invocations of the existing HTTP two-row contract for 141 through 149 also passed; no test source was edited.

The receipt still measures exactly 4351 bytes against requested 4096. **Decision (a), the 8192-byte minimum effective target with complete provenance and requested/effective reporting, remains unchanged. Ripley implements; Lambert updates the contract. REJECT remains**, with the broader numbered access/recovery/validation/snapshot/docs fixes still required. Working-tree boundary results are distinct from the earlier real-build live measurements.

## HEAD recheck requested at 2026-10-03T05:31Z

Re-read `git show HEAD`: HEAD remains ce0631b, with no committed delta from the reviewed commit. Its single-row delivery restriction and 150-repeat boundary cases are accepted as a partial fix. The newer incremental-row-cost change remains uncommitted and is not represented as HEAD. `AzdoMcpTools.cs:142` still clamps to 2048..16384, so the decided 8192-byte floor is not implemented. The other numbered blocker paths are unchanged. **REJECT at ce0631b remains.** Updated full-suite handoff evidence is 2481 passed / 1 failed (4096 receipt) / 9 skipped; no new full-suite run is claimed by this recheck.

## PR1 re-review at 9620719 - 2026-10-03

**Verdict: REJECT** `9620719138436a80f2d621ee50a89bfd425c5927`.

Reviewed the entire production delta in 39903da, the embedded full 1524/189-record fixtures and new regression cases, the HEAD public documentation delta, and the updated deviation note. Larry's authorization for Ripley to revise production is respected; this review makes no source/test/public-document edits and imposes no new author lockout.

### Accepted fixes and independently rerun evidence

The 8192-byte floor, requested/effective reporting, preserved descriptor provenance and original 4096 receipt test are now accepted. Large positive evidence reads advance, zero/negative sizes fail explicitly, nondefault preview continuations execute against their own viewId, clipped undeduplicated messages have fullAction, and nonzero unbounded issue windows remain incomplete with executable whole-record recovery.

Live normal-mode MCP `filter=all, projection=full, expand=none, limit=1` returned readable verified files with **1524/1524 records and complete=true** for all=true, delivery=file and delivery=chunked. A separate authenticated production HTTP request read the preceding request's evidence successfully (2048 bytes, matching SHA-256). The production-host alpha/beta regression also passed, verifying fresh request scopes and cross-context denial.

Independently rebuilt and ran the timeline-related tests, delivery contracts, snapshot-partition selection tests and existing WAL replay test: **281 passed / 0 failed / 1 skipped / 282 total**. The build emitted no warnings, and git diff --check passed. The provided full-suite result, 2547 passed / 0 failed / 9 skipped, remains handoff evidence rather than a new full-suite run by Dallas.

Live default measurements at this HEAD differ from the handoff approximation:

| Build | First-page rows | Stdio JSON-RPC bytes | HTTP body bytes |
| --- | ---: | ---: | ---: |
| 1621192 | 3 | 7555 | 7577 |
| 1600801 | 8 | 12672 | 12693 |

1621192 now shows Monitor Helix Jobs Task (log 1466, parsed System.Diagnostics.Process.Tests failure and exit -3), failed monitor Job (log 1470, nameHint), and failed Build Stage first. 1600801 shows failed DeviceTests Tasks/Jobs/Stages first. Every default page advances without duplicated/missing IDs (25 IDs over 23 runtime pages, 15 IDs over three MAUI pages). However, MAUI's first page and its second stdio page (12512 bytes) exceed the 12288-byte default, exposing a production serializer mismatch not covered by the passing generic TestServer wire-size tests.

### Remaining numbered required fixes

1. **WAL-aware partition discovery - Ripley; regression Lambert.** `src/HelixTool.Core/AzDO/EvalSnapshotAzdoPartition.cs:86`. The new unconditional immutable=1 is not safe for the selector's existing input contract. Partition prefixes can be committed only in WAL, just like payloads. In an isolated valid schema-v1 fixture, public timeline metadata was checkpointed into the main DB and authenticated cache-feedface timeline metadata committed only to WAL. Ordinary SQLite sees both partitions; immutable discovery sees only public. Actual CLI auto-selection wrongly succeeds in public instead of failing closed for multiple partitions; explicit HLX_EVAL_AZDO_PARTITION=cache-feedface aborts claiming the present partition is absent. DB/WAL hashes remain unchanged. Restore WAL-aware read-only discovery (normal exported rollback-journal snapshots do not need sidecars), or prove a standalone/WAL-free precondition without silently losing the released WAL replay/partition behavior. The deviation claim that a prefix scan "never needs WAL-resident content" is disproved. Add auth keys and acquisition-error keys resident only in WAL to selector/CLI regressions.

2. **Budget the actual production serializer - Ripley; regression Lambert.** `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:199-211`, `src/HelixTool/Program.cs:1149`, `src/HelixTool.Mcp/Program.cs:156`. Helper payload serialization uses McpJsonUtilities.DefaultOptions (null omission); actual strict production tool registration uses new JsonSerializerOptions that emit ordinary nullable row fields. A direct existing-fixture projection of MAUI's eight-row page measures **11928 CallToolResult bytes in the helper**, but **12636 with production payload options**; real stdio adds framing to reach 12672. Extra nullable monitorEvidence (8), parentId (2), contextParentId (2) and log (2) fields account for the discrepancy. Share/match the actual payload options without an unrelated global wire migration, and exercise default calls through both real production hosts with raw-response byte capture. Also make final-receipt measurement operational: `_ = Measure(receipt)` at line 174 observes a size but does not enforce the already-required final-wire condition.

3. **Observable and honest CLI file delivery - Ripley; regressions Lambert.** `src/HelixTool/Program.cs:1859,1877`. Two isolated snapshot-backed probes remain broken. A normal non-JSON `--filter all --projection full --expand none --delivery file` exits 0 with **zero stdout and zero stderr**, although an opaque runtime file containing all 1524 records was created: no path, descriptor or recovery reaches the caller. Print the readable delivery receipt in human mode as well. Separately, removing the monitor Phase and requesting full/all ancestor-expanded file delivery produces backing complete=false/timeline_graph_invalid but exits 0 solely because delivery.complete=true proves bytes were written. Preserve logical/source completeness independently of physical delivery completeness; genuinely incomplete backing selections must exit 2 unless explicitly accepted. Cover both malformed-graph delivery and an auto-materialized partial page, not only complete all/file requests.

4. **Physical snapshot output containment - Ripley; regression Lambert.** `src/HelixTool/Program.cs:1886`. The lexical Path.GetFullPath/prefix check does not resolve links. An outside-snapshot directory symlink pointing into the active snapshot, used as --output <outside-link>/derived.json, exits 0 and creates the file **inside the immutable snapshot**. Resolve destination/ancestor links against the physical snapshot root, using the repository's established path/identity safeguards; protect existing aliases too. Test physical containment and snapshot hashes, not only lexically contained paths. This is an actual mutation of an isolated test snapshot, not a hypothetical vulnerability claim.

5. **Complete the monitor-name-hint ordering fix - Ripley; regression Lambert.** `src/HelixTool.Core/AzDO/AzdoTimelineProjector.cs:460-468`. Separating warning successes from actual failed results is accepted, but the explicitly required monitor nameHint band is still absent. A two-record pure-projector probe orders a generic failed Bash Task/error before a failed Monitor Helix Jobs Job/nameHint. The design's order is parsed/unresolved monitor evidence, failed monitor name hints, then generic failed Task errors. Add the explicit hint band and a failure-versus-hint test; the new hint-versus-successful-warning test is only a proxy for this requirement.

6. **Document only supported delivery/recovery syntax - Kane.** `docs/cli-reference.md:157,179,201`, `README.md:161`, `CHANGELOG.md:39`. These now advertise --delivery all / delivery="all", but Core validates only auto|inline|file|chunked; an actual documented --delivery all command exits 1. Remove that string alias from copy and use --all/all=true separately (or explicitly coordinate a fully wired alias across schemas, Core and tests). The --record-id <guid> --projection full --raw-json example also cannot perform its implied exact lookup: raw mode intentionally ignores the new selectors/projection, preserving legacy filter semantics. Use non-raw full output for that example. Explain that next provides page coordinates, whereas continuation preserves the complete exact selectors/settings; next alone is not the complete next invocation.

### Probe isolation and cleanup

No source, test, public docs, commits, tags, pushes or PR creation by Dallas. Own CLI/WAL/symlink fixtures lived in specifically owned temporary directories and were cleaned after their child commands completed. Four specifically issued live evidence files and sidecars were deleted; both production stdio/HTTP servers were terminated and waited for. Additional pure existing-assembly probes ran in-memory through F# Interactive and exited. HEAD remained 9620719 throughout the re-review; only decision/history notes were appended.

## Round 3 re-review at 7ce53b4 - 2026-10-03

**Verdict: REJECT** `7ce53b4e7c09c3bfe684cabe73f947de5f8ee92e`. The WAL, production wire-serializer, monitor-hint ordering and documentation fixes are accepted. Non-JSON receipt printing, malformed-graph exit status and auto-materialized partial-page exit status also pass. Two narrower residuals remain.

### Independent validation

Rebuilt and reran the same focused gate: **298 passed / 0 failed / 1 skipped / 299 total**, with no repository build warnings. git diff --check is clean. The supplied 2564-pass/0-fail/9-skip full suite is handoff evidence, not an additional full-suite execution by this review.

Actual fresh-call default measurements:

| Build | First-page rows | Stdio JSON-RPC bytes | HTTP body bytes |
| --- | ---: | ---: | ---: |
| 1621192 | 3 | 7555 | 7577 |
| 1600801 | 7 | 11731 | 11753 |

Both transports now fit the 12288-byte default while retaining nullable payload fields, source identity and failure/monitor-first rows. Walking all stdio pages retains all 25/15 distinct selected IDs without gaps/duplicates; maximum runtime-page bytes are 8353 and maximum MAUI-page bytes are 11732 (that run's later two-digit JSON-RPC ID adds one framing byte). Complete full/file access again returns 1524/1524 records, with a 5760-byte receipt. A fresh authenticated production HTTP request reads the preceding request's evidence successfully (2048 bytes, matching SHA-256).

WAL-only discovery is now accepted: an isolated schema-v1 fixture with its sole authenticated feedface key committed only to WAL succeeds via both automatic and explicit CLI selection. Adding a public key only in WAL makes automatic selection fail closed for multiple partitions; an explicit authenticated selection still succeeds. DB and WAL hashes remain unchanged. The new metadata/acquisition-error WAL regressions also pass.

Non-JSON full file delivery prints a readable 479-byte path/hash/read receipt and exits 0. Malformed ancestor graph file delivery exits 2 with backing complete=false. The new auto-materialized partial-page case exits 2 with a verified file containing 1 of 2 selected records. The original direct-parent directory symlink case is now refused with exit 1 and zero snapshot mutation.

### Two remaining required fixes

1. **Resolve every existing ancestor, not only the last existing path component - Ripley; regressions Lambert.** `src/HelixTool/Program.cs:1940-1963`. ResolvePhysicalPath stops at the longest existing prefix and calls ResolveLinkTarget only on that prefix itself. If an earlier ancestor is the symlink, an already-existing directory or file beneath it is not itself a symlink, so the helper returns the still-aliased lexical path. Actual owned-snapshot probes at an outside-link -> snapshot demonstrate both failures: `--output <outside-link>/artifacts/nested-derived.json` exits 0 and creates a new file inside the snapshot; `--output <outside-link>/artifacts/existing.json` exits 0 and overwrites existing snapshot evidence. Walk/canonicalize every existing component/ancestor (prefer the repository's established physical path safeguards), including an existing destination, before containment and writing. Extend the regression beyond a new direct child of the symlink to existing nested directories and existing files. Snapshot protection is not complete while these mutations are possible.

2. **Preserve pre-materialization logical completeness for auto-delivered complete selections - Ripley; regressions Lambert.** `src/HelixTool/Program.cs:1903-1905`, `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:163`. The partial-page fix correctly refuses to equate verified bytes with a complete selection, but now rejects genuinely complete auto-file selections too: MaterializeReceipt deliberately clears the inline rows/Complete flag, and IsEffectivelyComplete falls back to that cleared receipt flag when wantsComplete=false. An exact `--record-id <successful-task> --projection full --expand none --json` request exits 0 with a 32-byte issue; the identical request with a 1 MiB issue exits 2 after auto-file delivery, although the backing file has selectedTotal=1, returned=1, complete=true, issueComplete=true and verified delivery.complete=true. The request's selected representation is complete; only the delivery mechanism changed. Retain the original projected payload's logical completeness separately from the inline receipt and physical-write status. A complete auto-delivered selection must exit 0; a real auto-materialized partial page and a malformed graph must remain exit 2 unless --allow-truncated is explicit. Add the complete one-record auto-file sibling to the existing partial-page regression.

All source/tests/public docs remain untouched by Dallas; only review/history notes appended. Both production hosts and the subsequent fresh-call byte-check host were terminated and waited for. Specifically issued live evidence files/sidecars and all owned WAL/CLI/symlink/large-issue fixture directories were cleaned. HEAD stayed 7ce53b4 throughout.

## Round 4 re-review at ed4e9d6 - 2026-10-03

**Verdict: REJECT** `ed4e9d668062114001683da8c73a35e7ca60f005`. Physical containment is now accepted. The original complete exact-record auto-file case and genuinely partial issue-window/file siblings are also accepted, but the new input-based completeness heuristics still give incorrect statuses for two valid neighboring requests.

Independently rebuilt and reran the focused gate: **308 passed / 0 failed / 1 skipped / 309 total**, with no repository build warnings and clean git diff --check. The supplied full-suite 2574-pass/0-fail/9-skip result was not rerun. Reviewed both changed source/test files and every new regression. No source/test/public-document changes by Dallas.

Owned-snapshot direct probes confirm outside -> mid/artifacts, where mid -> snapshot, is refused for both a new output file and an existing file overwrite: exit 1 and byte-identical snapshot evidence. The recursive component walk now handles the earlier unresolved-ancestor cases. No new containment blocker from these probes.

### One remaining root-cause fix, with two opposing failures

**Preserve actual projected logical completeness instead of inferring it from request flags - Ripley; regressions Lambert.** `src/HelixTool/Program.cs:1748-1763,1920-1922` (the `impliesCompleteRecordLookup`/`hasExplicitPartialIssueWindow` calculation and IsEffectivelyComplete), plus `src/HelixTool.Mcp.Tools/AzDO/McpPresentationBudget.cs:163` where the inline receipt clears Complete.

- **Complete issue windows falsely exit 2.** In an isolated valid snapshot, a successful exact full record has three issues (one 1 MiB, two short). With issueOffset=0 and issueLimit=3 or 5, the backing representation is complete=true, issueComplete=true, issueReturned=3, issueTotal=3, and physically verified. Nevertheless, all four delivery modes (auto/inline/file/chunked) exit 2 because any non-null issueLimit is classified as partial solely from input. A limit that covers every issue is not a partial window. The same record without issueLimit exits 0. Complete delivered representations must exit 0 regardless of the mechanism; retain exit 2 for issueLimit=1 returning 1/3, which currently works.
- **An expanded partial record page falsely exits 0.** A valid two-record timeline contains the same large Task and its existing Job ancestor. `--record-id <task> --projection full --limit 1 --json` uses the default ancestor expansion and materializes a verified file containing only 1 of 2 selected records. The backing complete flag is false, but CLI exits 0 because recordId+full is assumed to imply a complete one-record lookup even though expansion changes the selected set. A single seed is not necessarily a single selected row. This genuinely partial page must exit 2, as the non-recordId partial-page sibling already does.

Carry the original projected payload's logical Complete/issue completeness through materialization separately from the cleared inline receipt and physical delivery.complete. Do not recreate Core's completion decisions from whether recordId/issueLimit flags were supplied. Test exact/over-covering issue limits and default ancestor expansion with a smaller row limit, alongside the already-green complete lookup, partial issue window, malformed graph and auto-partial page cases.

The WAL, wire-budget, provenance/8 KiB floor, authenticated-reader, monitor-rank and docs approvals from Round 3 remain applicable: this commit only changes CLI containment/completeness and regression cases. Every new CLI probe completed; the specifically owned temporary fixture directory (including generated runtime evidence files) was cleaned. No server left running, no source/test/docs edit, commit, push or PR creation. HEAD remained ed4e9d6.

## Final Round 5 gate at 989b306 - 2026-10-03

**Verdict: APPROVE** `989b306cb78634a1ce20543ff0803870575bbdd2`. Supersedes the earlier P0-1 gate rejections. No required fixes remain from this review.

Reviewed the complete delta from ed4e9d6: ShapeTimeline now returns the original Core result's logical Complete alongside the shaped inline/file receipt at every return path. CLI --output uses the same Core result directly. The flag-based guesses are removed; IsLogicallyComplete(result) simply returns result.Complete, retaining Core's combined row/issue completeness and graph-validity decision. MCP still returns the original public shaped DTO, not the internal tuple. Reviewed all 26 new regression cases and the adjusted prior case.

Independently rebuilt/reran the focused gate: **334 passed / 0 failed / 1 skipped / 335 total**, with no repository build warnings. Then independently ran the **full suite: 2600 passed / 0 failed / 9 skipped / 2609 total**. git diff --check is clean.

Owned snapshot-backed CLI probes pass all 16 delivery/issue-window combinations (auto/inline/file/chunked x omitted/partial/exact/over-covering issue limit). Complete three-issue requests exit 0, including the 1 MiB file-delivered body; 1/3-issue windows exit 2. Exact-record default ancestor expansion with --limit 1 exits 2 for the real 1/2 selected page in auto/inline mode. A missing parent with issueComplete=true still gives result.Complete=false and exit 2 across auto/file/chunked and explicit --output. Recursive outside -> mid/artifacts -> snapshot targets are refused with exit 1 and zero mutation for new and existing files.

Fresh normal-mode production transport measurements at this HEAD:

| Build | First-page rows | Stdio JSON-RPC bytes | HTTP body bytes |
| --- | ---: | ---: | ---: |
| 1621192 | 3 | 7555 | 7577 |
| 1600801 | 7 | 11731 | 11753 |

Both fit the 12288-byte default, retain failure/monitor-first diagnostics, and match the earlier corrected measurements. Live complete full selection returns 1524/1524 records with complete=true in the backing file; its verified receipt measured 5759 bytes. A separate authenticated HTTP request reads that request's evidence (2048 bytes, matching SHA-256; partitioned source identity retained).

WAL-only metadata/acquisition-error partition discovery, automatic/explicit selection, multiple-partition fail-closed behavior, original snapshot replay, scoped cross-context denial, 8192 requested/effective floor, preserved provenance, raw CLI compatibility, copied-null wire serialization, full-action/continuation recovery and documentation fixes remain accepted and their regression coverage passed in the full suite.

Review-only work: no production/test/public-document edits, commits, tags, push or PR creation by Dallas. All started production stdio/HTTP hosts were stopped and waited for, all issued live evidence files/sidecars deleted, and all owned CLI fixture/runtime delivery directories cleaned. Only this decision and Dallas's history appended. Approval is for the pinned 989b306 HEAD; Larry retains the push/PR action.

---

## PR #159 Copilot Review Findings (Ripley)

# Ripley: PR #159 Copilot review findings — fixes and deviations

Date: 2026-10-08
Author: Ripley
Scope: All 10 Copilot review findings on PR #159 (all judged valid). Production code only —
Lambert owns `src/HelixTool.Tests` concurrently (Windows SQLite cache-cleanup work); the only test
file I touched is a brand-new file, `src/HelixTool.Tests/Text/HlxNameGlobTests.cs`.

## 2026-10-16 — Larry-approved cache-root simplification (supersedes runtime-store decisions below)

- Deleted the separate private runtime security subsystem: `UnixDirectorySecurity.cs`,
  `WindowsDirectorySecurity.cs`, and `CrossProcessPartitionLock.cs`. No native ACL/owner APIs,
  subprocess probes, ancestor walks, directory watchers, identity caches, or delivery-specific
  runtime-root environment override remain. Delivery now deliberately shares `cache.db`'s trust
  boundary; plain directory creation uses 0700 on Unix and new files use 0600.
- Live: `<GetEffectiveCacheRoot()>/delivery/<credential-partition>/`. Eval/offline:
  `<normal-user-base-cache-root>/eval-delivery/<SHA256(normalized absolute snapshot path)>/<partition>/`.
  Eval's `CacheRoot` names the snapshot, so the normal base is resolved with fresh `CacheOptions`
  rather than accidentally treating the snapshot as a writable base. On Windows the path is
  case-normalized before hashing. No snapshot writes or shared OS-temp paths.
- Disabled cache (`MaxSizeBytes <= 0`): `<GetBaseCacheRoot()>/delivery-ephemeral/<partition>/`;
  file delivery remains available. No new root override; the existing XDG cache-root override
  continues to apply on Unix.
- Small per-delivery-partition `delivery.db` sidecar replaces JSON descriptor/status files and
  lock-file choreography. Immediate SQLite transactions serialize content publication, dedupe,
  24-hour pin renewal, accounting, and eviction across processes. Eviction re-reads the pin in
  that transaction before deleting. Database connections are unpooled and connection strings
  use the builder (paths containing semicolons remain valid).
- Approved derived-sub-budget option: delivery soft cap = one quarter of configured cache bytes
  (minimum one byte), or 256 MiB with caching disabled. Accounting is exposed through
  `TryGetRetentionStatus`, separately from artifact-only `hlx cache status`; it does not claim
  a combined hard cap. Age eviction uses `ArtifactMaxAge`; oldest-access-first size eviction
  excludes live pins. Overage remains visible instead of deleting promised evidence. A failed
  file deletion is retained/accounted for and retried on a later opportunistic sweep.
- Provenance is rebuilt for each publication even on content-addressed dedupe, so identical bytes
  from a later backing key/source retain the current response's provenance without rewriting data.
- Retained the evidence read API and UTF-8/base64 semantics. Plain cache-directory trust is an
  intentional user decision, not an incomplete attempt at the discarded private runtime policy.

Validation (Lambert owns the only build/test slot):
- `dotnet build -warnaserror`: 0 warnings, 0 errors after the final database-open fix.
- Focused delivery + timeline: 355 total, 354 passed, 1 existing skip, 0 failures.
- Initial concurrency validation exposed reopening an active `delivery.db` via `FileStream`
  conflicting with SQLite platform locks on macOS. Fixed by creating the database only when
  absent, with `FileMode.CreateNew` and a concurrent-creator catch; existing databases are opened
  exclusively through SQLite. The rebuilt focused suite is green.
- Final full suite, three serial runs on the frozen final source: each 2,678 total,
  2,669 passed, 9 existing platform skips, 0 failures. Durations: 1m14s, 1m19s, 1m24s.
- Final first-creation sharing permits read/write/delete while the creator handle is briefly
  open, avoiding a Windows first-use race; existing SQLite files are never reopened via FileStream.
- Lambert preserves logs/TRX under ignored `.squad/evidence/lambert-cache-delivery/` and cleans
  the project-local runtime scratch. No branch switch or commit performed.
- Net line delta vs `989b306`: delivery-only +291; source/docs +1,430. Compared with starting
  `61ea58d`, this simplification removes 1,297 production/documentation lines (including the
  three deleted helper files). These counts exclude this gitignored deviations document.



## 2026-10-08 (later) — Dallas's `f953f59` REJECT (R1/R2/R3) — fixed

Dallas rejected `f953f59` on three findings (see `.squad/agents/dallas/history.md`, "PR #159
review-fix gate at f953f59"). Lambert owned all test files this round; I touched only
`src/HelixTool.Core/**` plus this file. Fixes below, independently validated: `dotnet build
-warnaserror --no-incremental` clean (0 warnings/errors); full suite (run by both Lambert and
independently by me) 2,677 passed / 12 skipped (pre-existing platform skips) / 0 failed / 2,689
total.

### R1 — Access-first retention (leases/pins)

- `EvidenceDescriptor` gains a new non-`required` field `PinnedUntilUtc` (JSON `pinnedUntil`) —
  deliberately not `required`, so a pre-upgrade sidecar without the field still deserializes
  (treated as already-expired until next handed out, per Lambert's STJ-required-member catch).
- `FileEvidenceDeliveryStore.MinRetentionWindow` = 24h. Refreshed (sidecar rewritten atomically via
  temp-file + rename) on every hand-out path: `PutBytes`/`PutText` (fresh write **and**
  content-addressed dedupe hit — dedupe now refreshes the lease instead of only touching
  last-access), `Read` (via its internal `TryGetDescriptor` call, before any bytes are returned —
  covers an in-progress chunked read), and `TryGetDescriptor` itself (a lookup is a hand-out too).
- `EnforceRetention` partitions entries into pinned (`now < PinnedUntilUtc`) vs. unpinned up front.
  Pinned entries are excluded from both the age-based and size-based eviction passes entirely — the
  size-based pass sorts the whole survivor set oldest-access-first but skips any still-pinned entry,
  so a freshly-pinned entry can't incorrectly "shield" an older unpinned one. If pinned entries alone
  keep the partition over `CacheOptions.MaxSizeBytes` after evicting everything eligible, retention
  stops (never deletes a live pin) and persists a `RetentionStatus` (`.retention-status.json`,
  readable via new `TryGetRetentionStatus(cachePartition)`) reporting the soft-cap overage —
  access-first per Larry's directive, not a silent failure.
- Per-partition-root in-process lock (`ConcurrentDictionary<string, object>`) now guards `PutBytes`,
  `TryGetDescriptor`, and `EnforceRetention` itself (reentrant), per Lambert's requirement that the
  sweep lock itself rather than relying only on the caller.

### R2 — Private-directory boundary

- `EnsureSecurePrivateDir` (single-arg): full ancestor-prefix-to-root symlink pre-check (every path
  prefix checked individually via its own `LinkTarget`, not just the final/concatenated path) so a
  symlink anywhere in the chain — including one masked by an otherwise-real nested directory further
  down (e.g. `link -> target`, `target/nested` genuinely exists) — is rejected before any descendant
  is created. Missing ancestors are created top-down, each fully verified. Added a lightweight
  `EnsureSecurePrivateDir(trustedBase, segment)` overload for the three real
  `ResolveDeliveryRuntimeRoot` call sites, which only checks/creates the one new segment relative to
  an OS-provided trusted base (`XDG_RUNTIME_DIR`, `LocalApplicationData`, OS temp root) — the full
  single-arg walk is reserved for the `HLX_DELIVERY_RUNTIME_DIR` override and direct testing, because
  walking a *trusted* OS base all the way to the filesystem root incorrectly flags legitimate OS
  symlinks (e.g. macOS's `/var` -> `/private/var`, which the temp-dir fallback resolves under) as
  attacker-planted.
- Real Unix ownership check: new `UnixDirectorySecurity` (P/Invoke `geteuid()`; owner uid read via
  `stat -f%u`/`stat -c%u` rather than hand-rolling a platform-specific `lstat` struct, which isn't
  portably safe across Linux/macOS/BSD field layouts without per-platform structs). ACL detection
  beyond POSIX mode bits via `ls -lde` (macOS) / `getfacl` (Linux), best-effort/fail-open only when
  no inspection tool exists. macOS ACL detection reads the *entire* `ls -lde` output for numbered ACL
  entry lines, not just the first line's `+` marker — macOS shows `@` instead of `+` when the entry
  also carries extended attributes, masking the simpler check (caught by Lambert's project-local
  repro).
- Windows: new `WindowsDirectorySecurity` creates with a DACL restricted to the current user
  (`DirectorySecurity`/`FileSystemAclExtensions.Create`, inheritance protection on) and verifies the
  owner SID on reuse. Marked `[SupportedOSPlatform("windows")]` so CA1416 enforces the
  `OperatingSystem.IsWindows()` guard at call sites (keeps the build warning-free without manual
  suppressions).
- New env var `HLX_DELIVERY_RUNTIME_DIR` overrides the resolved eval-mode runtime root outright;
  named in every thrown verification error as the actionable escape hatch.
- Regular (non-eval) delivery directory hardened too: both the `delivery` parent and the partition
  leaf are now symlink-rejected (hard boundary, always) and tightened to 0700/0600 best-effort
  in-place (not fail-closed on mode/owner, so pre-upgrade directories with looser legacy permissions
  aren't bricked — only a symlink is a hard reject here).

### R3 — Glob semantics regression

- `HlxNameGlob.MatchGlob`: the `'*'` branch is now checked *before* the literal/`'?'` equality
  branch. Previously, when `pattern[pi] == '*'` and the candidate's character at that position also
  happened to be a literal `'*'` (e.g. `IsMatch("*ab", "*b")`), the equality check matched first and
  consumed the wildcard position without ever recording the `starPi`/`starCi` backtrack bookmark —
  reproduced exactly as Dallas measured (`false` before the fix, `true` after, `"zab"` control
  unaffected throughout).
- Doc corrected: the class and method XML comments previously called the algorithm "linear-time";
  it is bounded O(candidate.Length × pattern.Length) worst case (each pending `'*'` bookmark can be
  retried once per remaining candidate character) — safe from regex-style catastrophic backtracking,
  but not actually linear. Both the class summary and `MatchGlob`'s own doc now state this precisely.

### Collector flakiness investigation (Dallas's "one collector test failed once")

- `ConcurrentAttemptAppends_DoNotLoseManifestEntries_Finding4169308785` — Lambert (test owner)
  isolated the root cause to the *test fixture*, not production: a non-atomic `ConsoleCalls++`-style
  counter under the fixture's 64-worker concurrent case. Fixed with atomic counters; reran 16
  executions across isolated + combined/parallel-stress runs with no reproduction. No production
  Collect/Timeline code changed as a result — findings relayed to Lambert during the session, who
  confirmed and fixed the fixture.

## Validation

- `dotnet build -warnaserror` (including `--no-incremental`): 0 warnings, 0 errors.
- Focused suite (Timeline/EvidenceDelivery/AzdoMcpTools/HlxEvidence/EvalSnapshotAzdoPartition
  filter, 358 tests) x10 consecutive runs: 3,570/3,570 passed, 10 skipped (same pre-existing skip
  each run), 0 failures.
- New `HlxNameGlobTests` (19 tests) standalone: 19/19 passed.
- Full suite: 2,638 total / 2,629 passed / 9 pre-existing platform skips / 0 failures (Lambert's
  baseline 2,610 passed + my 19 new glob tests).

## Findings → fixes

| # | Finding | Fix | File(s) |
|---|---|---|---|
| 1 | `all=true`/`delivery=file|chunked` kept a caller-supplied nonzero offset, materializing only the tail | Normalize `offset` to 0 whenever `request.All` is set (never reject); report the ignored value via new `requestedOffset` field | `AzdoTimelineProjector.cs` (`Project`), `AzdoTimelineViews.cs` (new `RequestedOffset`) |
| 2 | `viewId` truncated the source-content hash to 64 bits before final SHA-256 | `ComputeTimelineContentHash` now returns the full SHA-256 hex (64 chars), not `[..16]` | `AzdoTimelineProjector.cs` |
| 3 | Eval-mode delivery used shared predictable `/tmp/hlx-delivery-runtime` with default perms | Per-user private runtime dir: Unix prefers `XDG_RUNTIME_DIR`, else `{temp}/hlx-delivery-{user}/hlx-delivery-runtime`; Windows uses `LocalApplicationData\hlx\delivery-runtime`. New `EnsureSecurePrivateDir` creates with owner-only 0700 (`File.SetUnixFileMode`), rejects symlinked/reparse-point directory entries, and rejects a pre-existing dir whose perms aren't exactly 0700 | `FileEvidenceDeliveryStore.cs` |
| 4 | Backtracking regex rebuilt per row | Replaced with the classic linear two-pointer `*`/`?` glob algorithm (no regex, no per-call compilation, no recursion); legacy no-wildcard substring path unchanged | `HlxNameGlob.cs` |
| 5 | Cycles in children/descendants traversal skipped silently without `CycleDetected` | `AddChildren`'s cycle-guard-add failure now sets `graphIssues.CycleDetected = true` before `continue` (matches ancestor-expansion behavior) | `AzdoTimelineProjector.cs` (`ExpandSelection`) |
| 6 | Issue window only applied when exactly one row remained (silently ignored under default `expand=ancestors`) | `BuildFullProjection` now locates the exact `recordId` target anywhere in the row page and windows only that record's issues, leaving ancestor/context rows unwindowed; overall `complete`/`continuation` account for both row-paging and issue-window completeness | `AzdoTimelineProjector.cs` (`BuildFullProjection`) |
| 7 | Connection string built via raw interpolation (`;` in path breaks it) | `SqliteConnectionStringBuilder` (`Mode=ReadOnly, Pooling=false`), matching the existing pattern in `SnapshotExporter`/`SnapshotValidator` | `EvalSnapshotAzdoPartition.cs` |
| 8 | Unbounded GUID-named delivery files | Content-addressed ids (SHA-256 of bytes + media type) with put-time dedupe (skip rewrite when an identical entry already exists); bounded retention (`EnforceRetention`, run after each `Put`) derived from `CacheOptions.ArtifactMaxAge`/`MaxSizeBytes` — age-based delete then oldest-access-first eviction until under the size cap; a failed delete (likely an in-flight `hlx_read_evidence` read — Windows share-mode enforced, best-effort on POSIX) is skipped rather than retried that sweep. `Read` touches last-access on each read. Documented via XML comments on the class and each helper | `FileEvidenceDeliveryStore.cs` |
| 9 | Empty-but-non-null timelines bypassed the projector (shortcut skipped effectiveFilter/selectionScope/includePhase/viewId validation) | Shortcut now only triggers on a genuinely null timeline (`if (timeline is null)`); an empty-but-non-null timeline flows through `AzdoTimelineProjector.Project` like any other timeline | `AzdoMcpTools.cs` |
| 10 | First `hlx_read_evidence` read candidate used the full requested range (possibly `long.MaxValue`) before halving down to the wire budget | First candidate is seeded at ~0.7x the wire budget (bounded by `originallyRequested`), not the full requested range; the existing halving loop still corrects for any residual overshoot; `Finalize`'s completeness semantics (measured against `originallyRequested`) are unchanged | `HlxEvidenceMcpTools.cs` |

## Deviations / judgment calls (flagging for review)

- **#1 new field `requestedOffset`**: added as a new optional/nullable JSON field on
  `TimelineProjectionResult` rather than overloading `Note`, following the existing
  `requestedMaxResponseBytes`-alongside-`maxResponseBytes` precedent. Only emitted when `all=true`
  actually ignored a nonzero offset; omitted otherwise (no contract change for existing callers).
- **#3 ownership check is permission-bits-based, not a real UID check**: the BCL has no direct
  "stat owner uid" API without an extra package. I require exactly owner-only (0700) permissions on
  a pre-existing directory as a portable proxy for "owned/controlled by this user" — this stops a
  *different* OS user on a shared host (the realistic attacker in the original finding), but does
  not stop a same-privilege process running as the *same* user, which is already inside the current
  trust boundary. Documented this limitation in the XML comment. If stricter owner verification is
  wanted, that would need a P/Invoke `stat()` call or a package like `Mono.Posix` — flagging as a
  possible follow-up rather than doing it under this fix.
- **#8 retention is a lazy filesystem sweep, not SQLite-backed**: unlike the primary cache's
  `cache_artifacts` table (indexed, atomic), delivery retention here does a directory enumeration +
  sort on every `Put`. This is fine at delivery-directory scale (small number of oversized-payload
  entries vs. the full artifact cache) but would not scale to cache-sized entry counts. Flagging in
  case a future reviewer expects a DB-backed index instead.
- **#8 in-flight-read protection is best-effort/platform-asymmetric**: documented explicitly that
  Windows share-mode locking causes `File.Delete` to throw while a `Read` holds the file open (so
  retention skips it), but POSIX allows unlinking an open file, so this protection does not apply on
  Linux/macOS. No portable fix exists without a lock-file/refcount scheme, which felt like scope
  creep beyond "integrated with/derived from cache size limits."
- **#6 continuation arguments for the issue-window branch now carry the caller's `offset`/`limit`**
  instead of the previous hardcoded `offset: 0, limit: 1` (which was only correct when the target
  was provably the only row). Existing tests only assert presence of a continuation/recovery
  action, not its exact arguments, so this is not observed as a behavior break, but flagging the
  change in case any undocumented caller depended on the old hardcoded shape.

## Tests

- Added (by me, new file, does not touch Lambert's test files):
  `src/HelixTool.Tests/Text/HlxNameGlobTests.cs` — classic glob semantics (`*`, `?`, collapsed
  `**`, case sensitivity override, legacy substring fallback) plus a structural
  adjacent-star-run-vs-long-candidate case for the linear-matcher fix (#4).
- Suggested additional coverage for Lambert (not added, to avoid touching
  `src/HelixTool.Tests` beyond the one new file per the don't-conflict directive):
  - #1: `azdo_timeline` / `AzdoTimelineProjector.Project` with `all=true` (or `delivery=file`) and
    an explicit nonzero `offset` — assert the full selection is returned (not just the tail),
    `offset=0` in the result, and `requestedOffset` echoes the ignored value.
  - #2: a `ViewId` regression test asserting length 64 and that two timelines whose content differs
    only in the low 48 bits of the old truncated hash now produce different `viewId`s (would have
    caught the truncation; hard to construct without a targeted hash collision fixture, so a
    simpler "no viewId collision across N varied timelines" property test may be more practical).
  - #3: a non-eval-mode-independent unit test for `FileEvidenceDeliveryStore` on Unix asserting the
    runtime dir is created 0700, and that pre-creating it as 0755 (or as a symlink) throws
    `InvalidOperationException` from `PutBytes`/`PutText`.
  - #5: a `TimelineProjectorContractTests` case with `expand=children` or `expand=descendants` over
    a parent-child cycle, asserting `timeline_graph_invalid`/`CycleDetected` the same way the
    existing ancestor-cycle test does.
  - #6: a `full` projection request with `recordId` + default `expand=ancestors` (so ancestor
    context rows are present) plus `issueOffset`/`issueLimit`, asserting the target row's issues are
    windowed while ancestor rows' issues are not, and `issueComplete`/`continuation` reflect the
    window.
  - #8: `FileEvidenceDeliveryStore` dedupe (two `PutBytes` calls with identical content/media type
    resolve to the same `EvidenceId`/file, second call doesn't rewrite bytes) and retention (seed
    entries with old `LastAccessTimeUtc` via `File.SetLastAccessTimeUtc`, set a tiny
    `CacheOptions.MaxSizeBytes`/`ArtifactMaxAge`, assert oldest-first eviction keeps the store under
    the cap and never touches a file currently open via a held `FileStream`).
  - #9: an `azdo_timeline` MCP-tool-level test with a non-null timeline whose `Records` is empty,
    asserting the response includes projector-computed `viewId`/`selectionScope`/`includePhase`
    (not the all-empty literal shortcut shape) and that an invalid parameter (e.g. `issueLimit=0`)
    still errors instead of silently succeeding.
  - #10: an `HlxEvidenceMcpTools.ReadEvidence` test with a very large `lengthBytes` (e.g.
    `long.MaxValue`) against a large-but-bounded fixture, asserting the result is correct/complete
    and that the store's `Read` is invoked a small, bounded number of times (not O(log
    long.MaxValue) full-range reads) — may need a test double/wrapper around `IEvidenceDeliveryStore`
    to count invocations, since the current store doesn't expose call counts.

---

## P0-1 Implementation Deviations (Ripley)

---
date: 2026-10-03T00:00:00-05:00
author: Ripley
model: claude-sonnet-5
status: final
topic: P0-1 timeline implementation deviations from Dallas's design
---

# P0-1 implementation deviations

Implemented against `audit-p0-design.md` section 3 (P0-1) plus section 2 shared contracts
(2.1, 2.1a, 2.2, 2.3). All deviations below are narrow, documented, and do not change the
overriding access-first invariant: every explicit request still has a complete access path.

## 1. Budget overage accepted only for an ordinary-sized row's irreducible envelope overhead

Design: "12/16 KiB are inline shaping targets, never a size-error path" and "no zero-row
repeated page is an access path." In practice, the MCP SDK's default JSON options
(`McpJsonUtilities.DefaultOptions`) duplicate every payload into both `content[].text` (a
JSON-escaped **string** copy) and `structuredContent` (a structured copy), and the default
encoder escapes `"` as `\u0022` (6 bytes) rather than `\"` (2 bytes) inside that string copy.
For a small, genuinely non-empty page (1 row) with realistic envelope/`counts` overhead, this
can land 5-10% over an aggressively tight requested `maxResponseBytes` even after exhausting
every shrink step (row count, then preview length/count for triage). Rather than ever falling
back to a zero-row page in that situation, `McpPresentationBudget.ShapeTimeline` accepts the
smallest non-empty candidate — but only when the row's own *incremental* byte cost (measured
row+envelope minus envelope-alone) is at most `MaxNormalRowBytes` (2048 bytes), i.e. an ordinary
row whose overage is attributable to envelope/counts/viewId bulk a zero-row receipt would carry
anyway. A row whose own incremental cost exceeds that (e.g. one outlier oversized `name`/`issue`
field) always materializes to a verified evidence file instead, regardless of `selectedTotal` or
how close the bare envelope is to budget.

**Update (Dallas P0-1 review, superseded the prior residual gap):** the effective shaping target
is now clamped to **8,192–16,384 bytes** (was 2,048–16,384), with both the caller's original
`requestedMaxResponseBytes` and the clamped effective `maxResponseBytes` reported in every
envelope, and the accept-vs-materialize decision now measures the **final** receipt — including
`delivery`/`continuation`/`note` — not just the pre-receipt candidate. The previously-documented
residual gap (an explicit 4,096-byte target producing a ~4,351-byte zero-row receipt) is now
moot: a requested `maxResponseBytes` below 8,192 is clamped up before shaping ever runs, so that
specific floor-budget edge case can no longer be requested. No known residual gap remains in this
mechanism as of this round.

**New, tracked separately:** adding the now-required `requestedMaxResponseBytes` field to every
envelope costs ~60-90 bytes (duplicated across `content[].text` and `structuredContent`, per the
same SDK duplication above). One pre-existing contract test
(`TimelineViewContractTests.ExplicitSelectors_AndIntersections_BypassOnlyImplicitFailurePreset`,
`parentId`-selector case, `limit=100`) sits close enough to the unchanged 12,288-byte *default*
that this fixed overhead now tips its 8-row result from `returned=8/complete=true` to
`returned=6/complete=false` under the default budget. Row selection/order is unaffected (verified
8/8 correct via a standalone repro bypassing MCP shaping); this is a test sitting at a budget
edge, not a selection/ranking regression. Flagged to Lambert — recommend bumping that one case's
`maxResponseBytes` or accepting `complete=false` there.

## 2. `IssueNext`/`Next` force-emitted as literal JSON `null`

`McpJsonUtilities.DefaultOptions` sets `DefaultIgnoreCondition = WhenWritingNull` globally, which
would otherwise omit the pagination `next` field and the evidence-reader `next` field whenever
there is no further page/range — but callers (and the design's own continuation contract) need
to distinguish "no further page" (`next: null`, key present) from "field not applicable." Both
properties use `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` to force-emit literal
`null`. The single-record issue-window fields (`issueTotal`/`issueReturned`/`issueOffset`/
`issueLimit`/`issueComplete`/`issueTruncated`) stay default-omit-when-null (only meaningful
together, when a `recordId`+`projection=full` issue window is active); only `issueNext` is
force-emitted, so its "window active but no next page" case is distinguishable from "no window
at all" without force-emitting the whole group and inflating every other response.

## 3. `viewId` is a full 64-character SHA-256 hex digest, including a real content fingerprint

The design shows truncated/abbreviated digests in illustrative tables. The shipped
implementation emits the full un-truncated hex digest (64 chars) and — beyond the design's
listed fingerprint inputs — folds in an actual low-cost content hash of the timeline's records
(id/parentId/type/name/state/result/attempt/log.id/issue type+message), not just the timeline id
and record count. Two distinct timelines with the same `id` and record count (e.g. a retried
attempt that changed an `Attempt` number or issue text) must invalidate a cached `viewId`, and
`id`+`count` alone could not detect that.

## 4. CLI `--output` writes the CLI #154 envelope, not the bare MCP `records` array

For `hlx azdo timeline ... --output <path>`, the file written contains the same envelope shape
CLI JSON mode uses (`ok`, `results`, `returned`, `complete`, ... — `results` rather than MCP's
`records`, matching the existing list-command convention), not a bare array. This was validated
against Lambert's CLI replay test, which accepts either `results` or `records` at the top level
of the written file.

## 5. Not implemented in this PR (explicitly out of P0-1 scope per the design)

- CLI `--delivery chunked` and MCP `delivery="chunked"` are accepted as valid enum values and
  behave identically to `"file"` (both now *always* materialize the complete selection to a
  verified evidence file and return its descriptor, per the Dallas review's "deliberate delivery
  mechanism choice" requirement — not merely an overflow fallback as before); there is no
  separate true server-push chunked-transfer mode. The design's own `hlx_read_evidence`
  chunk-continuation path already provides equivalent incremental access once materialized.
- Section 6 near-free P1 follow-ups (CreateLimitedResults pagination on other MCP list tools,
  AzDO/Helix search streaming, evidence-plan paging, Helix batch-status >50, etc.) are explicitly
  deferred per the design's own phasing ("near-free P1 wins ... design follow-ups, not code
  changes made here") and are not touched by this PR.

## 6. Dallas P0-1 rejection — 9 numbered production fixes (this round)

Dallas rejected the original PR1 (`ce0631b`) with 9 numbered required fixes plus the 8,192-byte
floor decision; Larry authorized Ripley to fix production code directly. All 9 are implemented
in this round (budget floor/dual-reporting/final-receipt measurement; HTTP evidence-read
auth-partition resolution; `all`/`file`/`chunked` complete-selection retrieval + CLI `--delivery`;
continuation preview/byte-target preservation; clipped-message `fullAction` eligibility; triage
ranking band fix separating "failed-like for monitor classification" from "actually
non-succeeded for severity"; issue-window `complete` honoring `issueOffset==0`-only; Core-level
validation for `projection`/`expand`/`delivery`/`result`/`state`/`issueOffset`/`issueLimit` plus
overflow-safe evidence-reader arithmetic; CLI `--output` eval-snapshot containment guard with a
recovery `argv`). Two narrow findings from implementing these:

- **SQLite `immutable=1` tradeoff (reverted).** An early attempt at the `--output` snapshot guard
  also tried opening eval-mode SQLite connections with `?immutable=1` to eliminate the empty
  `-wal`/`-shm` sidecar files SQLite creates when reading a WAL-journal-mode snapshot. This broke
  two existing Core tests (`AzdoLiveMode_NeverServesNegativeRowsAndPositiveEvidenceWins`,
  `EvalMode_WalFilePresentInSnapshot_IsPreserved`) because `immutable=1` also stops SQLite from
  consulting real WAL-resident (not yet checkpointed) data — a genuine, deliberate prior design
  choice ("the WAL file ... may contain committed transactions and SQLite read-only connections
  follow the WAL without checkpointing it"). Reverted `SqliteCacheStore` to its original
  `Mode=ReadOnly`-only connection string; kept the narrower, side-effect-free `immutable=1` fix
  only in `EvalSnapshotAzdoPartitionSelector.DiscoverPartitions` (a one-shot partition-prefix
  scan that never needs WAL-resident content).
- **Residual Dallas-9 CLI test gap traced to the test harness, not production.** Two cases of
  `TimelineDallasCliRegressionTests.Dallas9_CliOutputInsideEvalSnapshot_IsRefusedWithoutMutation_AndOutsideRecoveryExecutes`
  still observe empty `cache.db-shm`/`cache.db-wal` files appearing after a (correctly refused,
  content-unchanged) CLI run. Root cause: `SnapshotEvalTestHarness.CreateStableSnapshotAsync`
  backs up file-to-file via `SqliteConnection.BackupDatabase`, which inherits the live writer's
  `journal_mode=WAL` page setting onto the snapshot db. The real production exporter
  (`SnapshotExporter.cs`) instead stages through an **in-memory** connection and serializes the
  raw bytes (forcing `journal_mode=memory`), so a real `hlx collect`/export-produced snapshot
  never carries a WAL flag and never needs a sidecar on read — `SnapshotExporter` even asserts
  `EnsureNoDatabaseSidecars` after writing. This is a test-harness/production mismatch, not a
  production defect; flagged to Lambert with a recommended harness fix (checkpoint + switch the
  destination to `journal_mode=DELETE` after backup, matching the in-memory approach).

## 7. Dallas P0-1 second re-review at `9620719` — 5 numbered production fixes (this round)

Dallas re-reviewed `9620719` and REJECTED it with 5 further numbered fixes; Larry's lockout
override still applies. All 5 implemented:

1. **`EvalSnapshotAzdoPartition.cs` `immutable=1` reverted again.** The prior round's narrower
   fix (kept `immutable=1` only in partition discovery, having already reverted it in
   `SqliteCacheStore`) was itself wrong: partition prefixes — not just payload content — can be
   committed only to WAL, so an `immutable=1` discovery scan silently misses any
   WAL-only-committed partition, causing wrong auto-selection and false explicit-partition
   rejection. Reverted to plain `Mode=ReadOnly` (no `immutable=1`), matching
   `SqliteCacheStore`'s approach exactly. There is no known production scan in this codebase that
   can safely assume "no WAL-resident content."
2. **Budget measurement now uses the actual production serializer.** `McpPresentationBudget`'s
   internal helper previously measured payloads with `McpJsonUtilities.DefaultOptions`
   (`DefaultIgnoreCondition = WhenWritingNull`), but both production hosts (`hlx mcp` and the
   standalone `HelixTool.Mcp` HTTP host) register tools with a separate, bare
   `JsonSerializerOptions` (no ignore-condition set) for correctness reasons (`UnmappedMemberHandling
   = Disallow` + an explicit `DefaultJsonTypeInfoResolver`), which is what the SDK actually uses to
   serialize `structuredContent`/`content[].text`. That options object omits nothing, so several of
   `TimelineTriageRow`'s nullable fields (`parentId`, `contextParentId`, `log`, `monitorEvidence`)
   — which only carry a bare `[JsonPropertyName]`, not a per-property `[JsonIgnore]` — are emitted
   as literal `null` on the real wire but were invisible to the old measurement. Added
   `McpPresentationBudget.ProductionToolJsonOptions` as the single shared definition; both hosts'
   `WithToolsFromAssembly` calls now reference it directly instead of each constructing an
   independent (and already-identical, but duplicated) copy, so shaping and the real wire can never
   drift again. Also made the final-receipt budget check in `MaterializeReceipt` operational: it
   previously computed a size and discarded it (`_ = Measure(receipt)`); it now actually shrinks the
   receipt's free-text `Note` (terse, then omitted) if the full note would push the materialized
   receipt over budget — the descriptor/continuation fields themselves remain untouched as required
   provenance.
3. **CLI non-JSON file delivery was silent, and exit code conflated physical vs. logical
   completeness.** A plain (non-`--json`) `--delivery file`/`chunked` call previously printed
   nothing at all once rows were cleared for the materialized receipt — no path, hash, or note
   reached the caller. Non-JSON mode now prints the same `[delivery] <complete|partial> <path>
   (<sha256>)` line (plus the `Note`, if present) that the CLI already shows for the explicit
   `--output` receipt. Separately, `delivery.complete=true` (all intended bytes verified as
   written) was being used as the *sole* completeness signal for exit code, even when the backing
   selection was itself a known-incomplete best-effort graph reconstruction
   (`incompleteDetails` non-empty, e.g. a dangling parent reference). `IsEffectivelyComplete` now
   requires `IncompleteDetails.Count == 0` in addition to `Complete`/`Delivery.Complete`, so a
   malformed-graph delivery exits 2 regardless of physical write success. Applied the same
   corrected check to the explicit `--output` receipt path, which had the identical gap (always
   reported `ok=true`/exit 0 regardless of logical completeness).
4. **Physical (not lexical) `--output` containment.** `Path.GetFullPath` is purely lexical and
   never consults the filesystem, so a destination (or any ancestor directory) that is itself a
   symlink pointing into the active snapshot previously passed the lexical prefix check and wrote
   inside the immutable snapshot. Added `ResolvePhysicalPath`, which walks up to the longest
   existing ancestor and follows its full symlink chain via `FileSystemInfo.ResolveLinkTarget
   (returnFinalTarget: true)` (bounded to 32 hops against a cyclic chain), then re-appends the
   not-yet-existing trailing segments; both the snapshot root and the requested output path are
   resolved this way before the containment comparison.
5. **Monitor name-hint ranking band added.** `ClassifyMonitorEvidence` already classified a failed
   record with no parseable/monitor-like issue text, but a monitor-looking *name*
   (`AzdoMonitorFailureParser.IsMonitorLikeName`), as `"nameHint"` — but `RankTriage`'s `Band`
   function had no corresponding band at all, so a nameHint record ranked identically to any other
   generic failed record and lost on `TypeRank`/source-order tie-breaks to a failed Task with a
   plain error issue. Added an explicit band (2, between unresolved-monitor-message and
   generic-Task-error) using the same `isActuallyFailedResult` gate as the other monitor bands, so
   the design's required order — parsed → unresolved-message → name-hint → generic Task error →
   generic error → generic failed → warning-only → context-only — is now fully represented.

Live-verified after this round: stdio and HTTP default wire bytes for 1621192 (7555/7577,
matching Dallas's own measurement exactly) and 1600801 (11731/11753 — now *under* the 12288
budget, down from Dallas's reported 12672/12693) using the real `hlx mcp`/`HelixTool.Mcp`
processes; a real symlink (`outside-dir -> snapshot`) `--output` attempt is refused with no
snapshot mutation and a working `recovery.argv`; non-JSON `--delivery file` now prints a receipt
and exits 0 for a complete delivery.

**New test-side consequence (flagged to Lambert):** `TimelineViewContractTests.
EveryFilterPreset_RemainsAvailable(filter: "all")` uses `WithSelectorRows()` (11 records) with
`projection=full, limit=100` and the unchanged 12,288-byte default — the now-correct
null-inclusive production measurement means this specific 11-row full-projection page no longer
fits in one page (returns 9/11), which is the same category of budget-edge issue as the earlier
`ExplicitSelectors_AndIntersections_BypassOnlyImplicitFailurePreset` case: selection is correct,
just needs the test to either paginate, use `all=true`/`delivery=file`, or raise its
`maxResponseBytes`.

## 8. CLI logical-completeness fix (following Lambert's `AutoMaterializedPartialPage` finding)

Item 3's `IsEffectivelyComplete` fix (section 7 above) was itself incomplete: `delivery.complete
== true` was accepted as a substitute for logical completeness unconditionally, but a materialized
evidence file only represents the *complete backing selection* when the caller actually asked for
one (`--all`, `--delivery file|chunked`, or `--output`). An ordinary auto-shaped request (default
`delivery=auto`, no `--all`, a small `--limit`) that happens to spill one oversized row/page to an
evidence file still gets a fully-written, verified file (`delivery.complete=true`) — but that file
holds only the requested page, not the full `selectedTotal`. `IsEffectivelyComplete` now takes a
`requestedCompleteDelivery` flag (the same `wantsComplete` the CLI already computes for `All`),
and only treats `delivery.complete` as a completeness substitute when that flag is true; otherwise
it falls back to `view.Complete` alone, which was already correctly `false` for a partial-page
materialize. Full suite after this fix: 2564 passed / 0 failed / 9 skipped / 2573 total, 0
warnings.

## 9. Dallas third re-review (commit `7ce53b4`) — 2 residual fixes

1. **`ResolvePhysicalPath` only resolved one symlink hop, not the full chain.** The prior fix
   correctly resolved an ancestor that is *itself* a symlink via `ResolveLinkTarget(returnFinalTarget:
   true)`, but that call only re-chases a node that is *repeatedly* a symlink — if the resolved
   target's own path contains a *different* symlink as one of its components (e.g. `outside-link
   -> mid/artifacts`, where `mid` is a separate symlink into the snapshot and `artifacts` is an
   ordinary directory once reached), a single-hop resolve lands on that real directory and stops,
   never re-examining `mid`. Fixed by recursing: every resolved target is passed back through the
   same component-by-component walk (bounded to 64 levels of recursion against a cyclic chain),
   guaranteeing every ancestor actually appearing in the fully-expanded chain is resolved, not
   just the first symlink directly encountered. Live-verified against a constructed nested chain
   (`outside-link -> mid/artifacts`, `mid -> snapshot`) for both a new-file write and an
   existing-file overwrite attempt: both now refused, zero snapshot mutation either way.
2. **Logical completeness wrongly conflated "routed to file delivery" with "incomplete" for a
   single complete record.** `IsEffectivelyComplete`'s `requestedCompleteDelivery` flag (added to
   fix the `AutoMaterializedPartialPage` case) only recognized `--all`/`--output`/`--delivery
   file|chunked` as complete-delivery intent, but `McpPresentationBudget.ShapeTimeline`'s bypass
   branch *also* always materializes the complete representation of a single exact record
   (`recordId` + `projection=full`) regardless of those flags — a large issue on that one record
   routing to file delivery for transport reasons is still a logically complete 1/1 result. Added
   `impliesCompleteRecordLookup` (mirroring that exact bypass condition) alongside the existing
   flag for the CLI completeness check only — `request.All` (Core's row-limit bump) is unaffected.
   Live-verified against a crafted single-record/20,000-byte-issue snapshot: `issueComplete=true`,
   `delivery.complete=true`, CLI exits 0; the existing `AutoMaterializedPartialPage` genuine
   partial-page case (2 records, limit=1, no `--all`) still correctly exits 2.

Full suite after both fixes: 2564 passed / 0 failed / 9 skipped / 2573 total, 0 warnings, `git diff
--check` clean on all touched production files.

## 10. Partial issue window wrongly treated as complete-delivery intent (Lambert's finding against section 9's fix)

Section 9 item 2's `impliesCompleteRecordLookup`/`wantsComplete` fix was itself too broad: an
explicit partial issue window (`--issue-offset`/`--issue-limit` selecting fewer than the record's
total issues) combined with `--delivery file`/`chunked` still only delivers that bounded issue
slice, not the record's complete issue set — yet both `wantsComplete` (true whenever
`delivery=file|chunked`) and `impliesCompleteRecordLookup` (true for any `recordId`+
`projection=full`) ignored this, so a verified 1-of-2-issues file delivery wrongly read as
logically complete (exit 0 instead of 2). Added `hasExplicitPartialIssueWindow` (`issueOffset != 0
|| issueLimit is not null`) and AND'd its negation into a single `requestedCompleteDelivery` flag
used by all three `IsEffectivelyComplete` call sites (including the explicit `--output` path,
which had the identical gap via its previously-hardcoded `true`). Live-verified against a crafted
2-issue single-record snapshot: `--issue-limit 1 --delivery file` now exits 2 (`issueComplete=
false`), while the full record with no issue window still exits 0. Full suite: 2574 passed / 0
failed / 9 skipped / 2583 total, 0 warnings.

## 11. Dallas fourth re-review (commit `ed4e9d6`) — root-cause refactor of completeness tracking

Dallas's root-cause diagnosis was correct: the prior rounds' CLI completeness fix was built on
flag-based inference (`wantsComplete`, `impliesCompleteRecordLookup`,
`hasExplicitPartialIssueWindow`) layered on top of each other as successive special cases were
found — each fix patched one repro without addressing the underlying problem: completeness was
being *guessed back out* from which delivery mode/request shape was used, rather than read
directly from the one place that actually knows it (the projector's own pre-receipt result).
Replaced the entire flag stack with a single source of truth:

- `McpPresentationBudget.ShapeTimeline` now returns `(TimelineProjectionResult Result, bool
  LogicallyComplete)`. `LogicallyComplete` is computed via a new `IsLogicallyComplete(result)` —
  `result.IssueWindow is not null ? result.IssueWindow.IssueComplete : result.Complete` — at
  *every* return point in `ShapeTimeline`, always against the pre-`ClearRows`/pre-materialize
  result (which already correctly reflects row-selection completeness, issue-window completeness,
  and a malformed/best-effort graph — Core already forces `Complete=false` for the latter). This
  is computed once per call, not re-derived from `all`/`delivery`/`recordId`/`--issue-*` flags
  after the fact.
- The CLI's `--output` path (which calls `Project(request)` directly, never through
  `ShapeTimeline`) applies the exact same `IsLogicallyComplete` rule to that raw result — made
  `IsLogicallyComplete` public for this reuse, rather than duplicating the rule.
- `AzdoMcpTools.Timeline` discards the second tuple element: MCP's own user-facing `Complete`
  field is unaffected and still correctly forced `false` for any materialized/file-delivered
  receipt (the small receipt's own row count, not the backing data) — that existing contract
  (`Assert.False(view.complete)` alongside `Assert.True(descriptor.complete)`) was never wrong;
  only the *CLI's exit code*, which needs the backing-selection truth MCP callers get by reading
  the delivery file themselves, needed this new channel.
- Deleted `IsEffectivelyComplete` and all three ad-hoc flags entirely — there is now exactly one
  completeness computation in the whole pipeline, and it can't drift out of sync with a newly
  discovered request shape the way the flag stack repeatedly did.

Live-verified both of Dallas's new repro cases: (1) `--issue-limit 3` on a 3-issue record
(`issueComplete=true`) now exits 0 in every delivery mode; (2) `--record-id` with the default
ancestor expansion and `--limit 1` delivering 1 of 2 selected records (`complete=false`) now
exits 2. Re-verified all prior-round cases still hold unchanged: nested symlink containment,
single complete record with an oversized issue (exit 0), and an explicit partial issue window via
`--delivery file` (exit 2, via a crafted multi-issue fixture). Full suite: 2574 passed / 0 failed /
9 skipped / 2583 total, 0 warnings, `git diff --check` clean.

## 12. Follow-up: `IsLogicallyComplete` wrongly preferred issue-only completeness over the combined truth

Lambert's fourth-review matrix caught a real regression in section 11's refactor itself:
`IsLogicallyComplete` used `result.IssueWindow is not null ? result.IssueWindow.IssueComplete :
result.Complete` — preferring the narrower issue-only signal *instead of* `Complete` whenever an
issue window was present. But `result.Complete` already *is* the correct combined truth: Core
sets it to `issueComplete` for the issue-window branch (or the ordinary row-selection rule
otherwise), then unconditionally forces it to `false` whenever the timeline graph itself is a
malformed/best-effort reconstruction, regardless of which branch produced the value first. By
preferring `IssueWindow.IssueComplete` instead of `Complete`, a record reached through a broken
ancestor chain (missing parent) but with every one of its own issues present (`issueComplete=
true`) was wrongly reported as logically complete. Fixed by simplifying `IsLogicallyComplete` to
return `result.Complete` directly — no special-casing needed at all, since Core's `Complete` was
already the single correct AND of both signals. Live-verified against Lambert's exact repro
(missing-parent record with all 3 of its issues present): `issueComplete=true`, `complete=false`
(with `incompleteDetails` populated) → now correctly exits 2; a healthy-graph equivalent with the
same full issue coverage still exits 0. Full suite: 2600 passed / 0 failed / 9 skipped / 2609
total, 0 warnings, `git diff --check` clean.
