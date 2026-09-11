# Decision: Helix Queue Monitor Compatibility Roadmap — APPROVED WITH CORRECTIONS

**Date:** 2026-09-04
**Status:** ✅ **APPROVED WITH CORRECTIONS** — Investigation complete, implementation roadmap staged
**Final verdict by:** Dallas (Lead/Reviewer)
**Agents:** Ash (Product Analyst), Ripley (Backend Dev), Dallas (Lead)
**Investigation type:** Multi-agent design review with architectural findings

---

## Executive Summary

Two independent agents (Ash, Ripley) completed analysis of Helix queue-monitor compatibility for helix.mcp. Both correctly identified that queue-monitor support is not a set of new capabilities but primarily a **projection bug** in existing code. Ripley's audit uncovered five concrete defects; Ash's requirements analysis identified eight opportunities. Dallas's review accepted six items now (four fixes + one parallel enhancement + one doc correction), deferred two items to a later gate with evidence, and rejected all proposals for new MCP tools beyond what existing tools can compose.

The key architectural finding: `HelixApiClient.ListJobNamesByBuildAsync` already fetches `JobSummary` containing `QueueId`, `Properties`, `Created`, `Finished`, and `FailureReason`, then discards everything except the name with `.Select(j => j.Name)`. Restoring this projection costs zero additional HTTP calls and unblocks queue-monitor investigations for both legacy and new topologies.

---

## Investigations Submitted

### Ripley (Backend Dev) — Audit of Local Implementation
**Status:** APPROVED as evidence, with one material correction
**Key findings:**
1. Queue-monitor message format (`Work item 'X' in job 'NAME (guid)' failed (State).`) will never match our `FailedWorkItemRegex` which requires literal `has failed` + bare GUID.
2. Real build-wide errors (no Helix job GUID) are silently dropped when `filter="failed"` due to `preserveIssuesGate` guard logic.
3. The Helix-side primary path (`Job.ListAsync` + `BuildId` filter) is the correct long-term direction and pays off most in queue-monitor repos.
4. `CiKnowledgeService` guidance is now stale — describes the fallback as the primary path.
5. `ListJobNamesByBuildAsync` discards `QueueId`, `Properties`, job status metadata already in the response.
6. **Correction required:** P4's dedup algorithm (group by `(PhaseName, QueueId)` + max attempt) is wrong. Arcade uses lineage: a job is superseded iff another job's `PreviousHelixJobName` points to it. Ripley retains ownership of the corrected D5a/D5b form.

### Ash (Product Analyst) — Requirements Analysis
**Status:** APPROVED as requirements, topology analysis accurate
**Key findings:**
1. Eight dotnet repositories (runtime, aspnetcore, roslyn, SDK, installer, WPF, Cecil, XDT) are adopting queue monitor.
2. Queue monitor vs. legacy per-leg topology fundamentally differs: monitor queries Helix via source string + BuildId; legacy scrapes AzDO timeline task names.
3. Identified six genuine gaps (A–F); proposed eight user stories (US-Q1 through US-Q8).
4. **Correction needed:** US-Q2 acceptance criterion ("responds in <5s for 500+ jobs") conflates submission-level summary (achievable, 1 call) with result-level aggregation (not achievable from Helix list API; requires per-job calls).
5. US-Q1, US-Q4, US-Q6 propose new tools; actual capability already served by `azdo_timeline` + `azdo_search_timeline` + `azdo_search_log` in composition.

---

## Ranked Roadmap — Decisions (Approved Items)

### Approved NOW (Four Fixes, One Enhancement, One Doc Correction)

**D1 — Widen the Helix-side projection. (ACCEPTED; supersedes Ripley P1, Ash US-Q2, US-Q3, US-Q7)**

Change `ListJobNamesByBuildAsync` to project the whole `JobSummary`, not just `Name`. Populate `HelixJobFromBuild` from data already in hand:

- `ParentJobName` — `System.PhaseName` → `System.JobDisplayName` → `System.JobName` (rejecting literal `__default`) → `""`.
- `Result` — `"completed"` when `Finished != null`, else `"running"`, replacing unconditional `"unknown"`. Do NOT claim pass/fail here; the list API does not carry it.
- New optional fields: `QueueId`, `WorkItemCount` (`InitialWorkItemCount`), `Superseded` (see D5a), and `Source` on `HelixJobsFromBuildResult`.

`Source` on the result object is how Ash's real need (diagnostic transparency) is met without adding a tool. The `Note` field already exists and must be updated to reflect that `Result` is now meaningful for completion but still not for pass/fail.

- **Schema impact:** Additive and optional throughout. `HelixJobFromBuild` gains nullable members; existing consumers unaffected.
- **Compatibility:** Improves legacy per-leg repos too. The same Helix SDK submitter stamps `System.PhaseName` on every job regardless of topology.
- **HTTP cost:** Zero additional calls. This is the item satisfying "leverage data already fetched."
- **Owner:** Ripley
- **Test surface:** Fixture `JobSummary` set carrying `System.PhaseName` resolves `ParentJobName` from it; where only `System.JobName: "__default"` is present, result is empty. `Result` is `"completed"` when `Finished` is set, `"running"` when null. Exactly two outbound calls (`GetBuildAsync`, `Job.ListAsync`).

**D2 — Emit a row when issues exist but no job GUID parses. (ACCEPTED; Ripley P3)**

Correctness bug predating queue monitor and now the common case. When a task has `Issues` but no job GUID extracts, emit a row with `HelixJobId: ""` plus the raw issue text instead of dropping silently. Reuse existing `FailedWorkItems` slot; add `HelixJobFromBuild.Message` only if existing slot proves semantically wrong in review.

- **Schema impact:** None if reusing existing fields; new field optional if added.
- **Owner:** Ripley
- **Test surface:** Timeline fixture with a task having issues and no parseable GUID, queried with `filter="failed"`, returns `TotalHelixJobs >= 1` and surfaces issue text. Today returns 0.

**D3 — Parse the monitor's message format in the fallback. (ACCEPTED; Ripley P2)**

Arcade's format is now verbatim (`Work item '(.+?)' in job '.+?' failed \((.+?)\)\.`), so add a bounded parser pattern. Detect by task name `Monitor Helix Jobs`; match the format and extract the GUID from the console URL. Keep the legacy regex intact and unconditional — both formats must work.

- **Schema impact:** Parser addition only; no new fields or types.
- **Owner:** Ripley
- **Test surface:** Fixture using arcade's verbatim string yields both the work-item name and job GUID. Existing legacy-format tests pass unchanged.

**D4 — Correct the `CiKnowledgeService` description. (ACCEPTED; Ripley P5. Owner: Kane.)**

`CiKnowledgeService.cs:229`, `:240`, `:787` describe the fallback as if it were the whole tool. This is wrong today independent of dotnet/sdk's behavior. Rewrite to: Helix-side `Job.ListAsync(source) + BuildId` is primary; timeline substring scraping is the fallback; the `[HelixJob:GUID]` test-run-name trick is a second-order workaround.

- **Owner:** Kane
- **Acceptance criterion:** No occurrence of the claim that `azdo_helix_jobs` detects Helix by timeline substring matching remains as a description of primary behavior.

**D6 — Parallelize `FindFilesAsync`. (ACCEPTED, low priority; Ash US-Q5)**

Real inefficiency, unrelated to queue monitor. Use `SemaphoreSlim(10)` matching `helix_status` pattern. Progress contract must stay monotonic; `ProgressOverStatelessHttpTests` must pass unchanged.

- **Owner:** Ripley
- **Acceptance criteria:** Default maxItems = 30 (not 50 as Ash stated). Scaling test: 500 work items in <60s. Response includes result count + duration. Graceful timeout/rate-limit handling. Existing `--json` flag works.

### Approved LATER (Two Items, Gated on Evidence)

**D5 — Attempt/lineage handling. Split into two.**

- **D5a (now, inside D1):** Carry `PreviousHelixJobName` through the projection and expose `Superseded: true` on any job that another returned job points to. Free, additive, changes no counts, makes the phenomenon observable.
  - **Owner:** Ripley
  - **Acceptance criterion:** Fixture where job B carries `PreviousHelixJobName: A` marks exactly A as superseded, marks B as not, leaves `TotalHelixJobs` unchanged.

- **D5b (later, gated):** Actually filter superseded jobs out of `TotalHelixJobs` / `FailedHelixJobs`. **Not approved yet.** No live build demonstrates duplicates. D5a produces that evidence. Revisit only when a real build shows `Superseded` rows and use arcade's leaf rule verbatim — not Ripley's original group-by-and-max-attempt (which would delete legitimate concurrent jobs, a silent undercounting).
  - **Sequencing:** Annotate first (D5a), filter second (D5b). Silently changing a user-visible count on an unproven hypothesis is exactly the change Ripley correctly escalated to review; answer is "prove it with the cheap version first."
  - **Owner:** Ripley (pending evidence from live build)

---

## Rejected Items

| Item | Reason | Justification |
|------|--------|---|
| **Ash US-Q1** — monitor job status tool | Fully served by `azdo_timeline` + `azdo_search_timeline`. New surface for zero new capability. |  Existing tools already query timeline for job status, exit code, logId. |
| **Ash US-Q6** — monitor log tool | Fully served by `azdo_search_log` (accepts null `logId`) + `azdo_log`. | Two-step path exists today; no new tool needed. |
| **Ash US-Q4** — topology detection tool | Heuristic wrapper over a timeline filter with uncalibrated confidence field and no consumer that changes behavior based on it. D1 makes the primary path work for both topologies, removing the reason to branch on topology at all. | Proposed tool creates maintenance burden for zero user benefit once primary path is fixed. |
| **Ash US-Q3 (tool form)** — `azdo_helix_source_string` tool | The need is real; a dedicated tool is not the answer. Met by the additive `Source` field in D1. | Raw data provided in result; dedicated tool adds no value. |
| **Ash US-Q7** — platform mapping | D1 returns `QueueId`, from which callers can group. Parsing queue names as "platform" is a lossy heuristic we would own forever. | Data is provided; lossy inference is caller's responsibility. |
| **Ash US-Q8** — historical trends | Needs a history store we do not have. Ash's own note says out of scope. | Deferred as acknowledged out of scope; rejected rather than left hanging. |
| **Ripley P4 as specified** | Algorithm is wrong — groups by `(PhaseName, QueueId)` would delete legitimate concurrent jobs. | Accepted only in revised D5a/D5b form using arcade's lineage-leaf rule. |

---

## Compatibility Rules for Legacy Pipelines

1. **No behavior may branch on "monitor detected."** D1 improves both topologies because the Helix SDK submitter stamps the same properties either way. D5's leaf rule is a provable no-op on legacy because `PreviousHelixJobName` is never present there.
2. **The legacy `has failed` regex stays, unconditional.** D3 adds a pattern; it does not replace one.
3. **Every new field is optional and omitted when null.** Follow existing `JsonIgnoreCondition.WhenWritingNull` convention.
4. **No count semantics change without evidence.** `TotalHelixJobs` and `FailedHelixJobs` keep current meaning through D1–D4 and D5a. Only D5b may alter them, and only after D5a produces real build evidence.

---

## Success Criteria

- **D1** — For fixture `JobSummary` with `System.PhaseName`, `ParentJobName` resolves from it. Where only `System.JobName: "__default"`, result is empty. `Result` is `"completed"`/`"running"` per `Finished` state. Exactly two outbound calls.
- **D2** — Timeline fixture with issues + no parseable GUID, `filter="failed"`, returns `TotalHelixJobs >= 1` surfacing issue text.
- **D3** — Arcade's verbatim string yields work-item name + job GUID. Legacy tests unchanged.
- **D4** — No claim that `azdo_helix_jobs` detects Helix by timeline substring remains in `CiKnowledgeService`.
- **D5a** — Job B with `PreviousHelixJobName: A` marks A as superseded, B as not, `TotalHelixJobs` unchanged.
- **D6** — `ProgressOverStatelessHttpTests` unchanged; progress monotonic and terminates.

---

## Ownership & Lockout

**Ripley** — owns D1, D2, D3, D6, D5a/D5b. Strong audit; all findings verified. P4 error is narrow; correction is mechanical, so no lockout.

**Ash** — no lockout. Requirements analysis is accurate; topology survey valuable. But recurring failure mode: proposed tools before checking if existing tools compose. **Any revived proposal for US-Q1, US-Q4, or US-Q6 must include a concrete failing investigation transcript** showing composition of `azdo_timeline` / `azdo_search_timeline` / `azdo_search_log` on a real build. Without that, rejection stands.

**Kane** — owns D4 only (doc corrections in `CiKnowledgeService`).

**Lambert** — owns test surface for D1, D2, D3, D5a (fixtures specified above; no further design input needed).

No production code written during review. Worktree is clean.

---
---
date: 2026-09-04
author: Scribe (session merger)
status: decided
---

### 2026-09-04T15:38:22.971-05:00: Queue-monitor implementation outcome (authoritative correction)

**By:** Ripley, Lambert, Kane, Ash, Dallas

**What:** This supersedes the roadmap's stale “implementation not started” state and any design implying that completed Helix jobs have known outcomes. Ripley implemented metadata projection, optional fields, original leg names, lineage annotation without count filtering, monitor warning/tree parsing, and issue-only fallback rows; Lambert added tests; Kane corrected guidance and changelog. After Dallas rejected treating all primary jobs as failed and omitting active monitor issues, Lambert revised the primary result to `FailedHelixJobs = 0`, `OutcomeUnknownHelixJobs = N`, preserving `Strategy = "helix"`. Completion timestamps never establish outcome. A single timeline enrichment request returns all issue-bearing Tasks as build-level `timelineIssues`, with no topology, name, or GUID gate. Omitted `timelineIssues` plus a warning `Note` means enrichment was unavailable; `[]` means it was queried and no issues existed. After Dallas rejected unhandled expected JSON/offline errors, Ash added narrow `JsonException`/`InvalidOperationException` handling and regression coverage while preserving caller cancellation. Dallas's final Sol review approved the current diff.

**Why:** Primary Helix discovery and build-level timeline diagnostics are complementary. Unknown outcome must not be converted into failure, while monitor issues must remain visible even after successful Helix discovery. Serialization distinguishes unavailable enrichment from a successful empty query.

**Validation and state:** Final targeted validation passed 117 tests under `DOTNET_ROLL_FORWARD=Major` because local runtime 11 runs tests targeting 10; parent verified a clean final diff check. The earlier full suite (1,943 passed, 8 existing skips) predates the final enrichment corrections and is not claimed as final full-suite validation. All source, tests, and docs remain uncommitted, with no PR. Count filtering and parallel scans remain deferred. Sol/no Opus is a session-only model directive.

---

---
decision: startup-cache-eviction-lifecycle
issue: lewing/helix.mcp#129
author: dallas
role: Lead
date: 2026-09-11
status: accepted
ceremony: pre-implementation design review (read-only)
---

# Startup cache-maintenance lifecycle — design decision (#129)

Read-only review. No production file and no test file was touched. Baseline build verified
before and after (Release, 0 Warning(s), 0 Error(s)); worktree carries only this brief.

## 1. What is actually wrong

`SqliteCacheStore`'s constructor ends with `_ = Task.Run(() => EvictExpiredAsync())`
(`SqliteCacheStore.cs:45`). Three distinct defects are bundled in that one line, and the issue
title only names the first:

1. **Untracked.** No handle is retained, so nothing can await it, nothing can cancel it, and a
   fault becomes an unobserved `TaskScheduler` exception.
2. **Unordered against disposal.** `Dispose()` calls `SqliteConnection.ClearAllPools()` while the
   maintenance pass may still hold an open connection.
3. **Late-binding cutoff — this is the one that actually broke Windows CI.** The pass computes
   `DateTimeOffset.UtcNow` *when it finally runs*, not when the store was constructed. A row
   written after the constructor returned is therefore a legitimate deletion candidate. Tracking
   the task alone would make that race *observable*; it would not make it *go away*.

Defect 3 is why `EvalMode_EvictExpired_IsNoOp_ExpiredEntriesRemain` failed on PR #128, and why
two test-side workarounds already exist to route around it:
`SnapshotEvalModeTests.cs:53-60` (a documented `SQLITE_BUSY` retry loop around `BackupDatabase`)
and the entire rationale header of `ExpiredSnapshot.cs`. Both name the fire-and-forget task
explicitly. **The fix must retire the reason those workarounds exist, and the comments that
describe the bug must stop describing it as current.**

## 2. Decision

### 2.1 Start asynchronously; join deterministically

**Startup maintenance starts asynchronously.** Rejected the synchronous-constructor option:
`OpenConnection` sets `PRAGMA busy_timeout=5000`, so a synchronous pass makes every `hlx`
invocation pay up to five seconds of another process's write lock before the first byte of
output. `hlx` is a CLI first; a startup stall is a worse defect than the one being fixed.

Asynchronous does **not** mean fire-and-forget. The constructor retains the handle:

- `private readonly CancellationTokenSource _maintenanceCts`
- `private readonly Task _startupMaintenance` — never null.
- Eval mode assigns `Task.CompletedTask` and starts nothing.

### 2.2 Pin the cutoff at construction time

The startup pass evicts **what was already stale when the store was opened**, not what is stale
when the thread-pool gets around to it. The constructor captures one `DateTimeOffset` and the
pass uses only that value for both `expires_at` and the `ArtifactMaxAge` cutoff.

This is a correctness change, not a convenience: it makes "startup maintenance deleted a row I
wrote after construction" structurally impossible, on every platform, regardless of scheduling.

**The public `EvictExpiredAsync(ct)` contract is unchanged** — an explicit call still evaluates
`UtcNow` at call time, so `NormalMode_EvictExpired_RemovesExpiredRows` and every existing caller
keep their current semantics. Implement by extracting a private
`EvictExpiredAsync(DateTimeOffset asOf, CancellationToken ct)`; the public one-argument overload
delegates with `DateTimeOffset.UtcNow`.

### 2.3 How callers observe completion

`internal Task StartupMaintenance { get; }` on `SqliteCacheStore`. Internal, not public.

`HelixTool.Core.csproj` already carries `<InternalsVisibleTo Include="HelixTool.Tests" />`, so
tests get full fidelity — including faults, by awaiting the task — with **zero public API
added**. That is the whole of the "observable/awaitable" requirement.

**Nothing is added to `ICacheStore`.** Production callers observe completion through `Dispose()`,
which joins. If a future production caller genuinely needs to await maintenance, we promote the
member then, on evidence. Do not pre-build it.

Honest limitation, recorded rather than overclaimed: the CLI never disposes its
`ServiceProvider` (`Program.cs:113`), so at process exit the pass is abandoned by the OS, not
joined. The guarantee this change delivers is *"no untracked work"*, not *"always joined"*.
Anyone reading the eventual CHANGELOG line should not be told otherwise.

### 2.4 Cancellation and disposal ordering

`Dispose()` executes in exactly this order. The ordering is the contract; deviation is a
reject-on-sight condition:

1. **Idempotency guard** — `Interlocked.Exchange` on a `_disposed` field; second and later calls
   return immediately. Required: `SqliteCacheStoreTests.SchemaCreation_OpenTwice_NoErrors` and the
   fixtures dispose stores on paths that can overlap.
2. **Cancel** — `_maintenanceCts.Cancel()`. Signal before waiting, never after.
3. **Join, bounded** — wait on `_startupMaintenance` with a named constant
   `DisposeJoinTimeout = TimeSpan.FromSeconds(10)`. Generous against the 5 s `busy_timeout`, and
   still a bound, so a pathological lock cannot hang a CLI exit forever.
4. **Observe the fault** — see §2.5. On timeout, attach a fault-observing continuation so the
   exception can never resurface as an unobserved-task crash.
5. **Dispose the CTS** — strictly after the join. Disposing it earlier hands the running pass a
   disposed token.
6. **`SqliteConnection.ClearAllPools()` last.** This is the ordering point the issue is about:
   pools must not be cleared while a maintenance connection is open.

Cancellation checkpoints inside the pass: `ct.ThrowIfCancellationRequested()` before each `DELETE`,
before the artifact `SELECT`, and once per iteration of the `DeleteArtifactRows` loop. Give
`DeleteArtifactRows` a `CancellationToken` parameter — it is private, so this costs no surface.

### 2.5 Exception semantics — no broad swallowing

**The maintenance body catches nothing** except cancellation, and cancellation is detected the
way `.copilot/skills/cancellation-vs-timeout` requires — on
`cancellationToken.IsCancellationRequested`, never on token identity. Everything else propagates
into `_startupMaintenance`, where a test that awaits it sees the real exception.

`Dispose()` is the only place that absorbs anything, and it absorbs a **named, narrow** set:

| Caught in `Dispose` | Why it is expected |
|---|---|
| `OperationCanceledException` | We asked for it in step 2. |
| `SqliteException` | Lock contention / read-only database. Cache data is regenerable. |
| `IOException` | Artifact file removed externally — already the precedent at `DeleteArtifactRows`. |

Anything else — `InvalidOperationException`, `NullReferenceException`, `ArgumentException` —
**propagates out of `Dispose()`**. A logic bug in cache maintenance must not be laundered into
silence. Explicitly banned: `catch { }`, `catch (Exception)`, and `catch (Exception) when (true)`
anywhere on this path.

### 2.6 Eval mode

Unchanged behavior, now structurally guaranteed rather than guarded after the fact:

- The constructor starts **no** task; `StartupMaintenance` is `Task.CompletedTask` and is already
  completed when the constructor returns.
- The existing `EvalMode` early-return inside `EvictExpiredAsync` **stays**, as defense in depth.
- `Dispose()`'s cancel/join steps are no-ops and must remain safe to run.
- Net effect on a snapshot: zero database work beyond `ValidateEvalSchema`, and no `-wal`/`-shm`
  sidecar creation — which is also the snapshot single-link requirement documented in CHANGELOG.

## 3. Test contract — determinism without a testing API

Non-negotiable constraints:

- **Banned:** `Task.Delay`, `Thread.Sleep`, `SpinWait`, retry-until-it-passes loops, and any
  assertion whose truth depends on scheduling.
- **Banned:** any new `[CollectionDefinition(..., DisableParallelization = true)]`. Every test owns
  a unique temp cache root (the existing pattern), so isolation comes from the filesystem, not from
  serializing the suite. A process-global mutable test hook would force serialization — that is the
  reason none is being added.
- **Banned:** asserting *which* outcome a disposal race produced. "Ran to completion" vs. "was
  canceled" is genuinely timing-dependent, and asserting it is precisely the mistake that produced
  the original Windows failure. Assert only invariants true on both platforms.

Determinism comes from `await store.StartupMaintenance` and from observable end-state, not from
an injected seam. Required coverage:

1. **Regression, exact.** Construct a normal-mode store, write a `TimeSpan.Zero` row, await
   `StartupMaintenance`, assert the row is still present. Proves §2.2. This is #129's failure,
   inverted into a permanent guard.
2. **Normal expiration preserved.** A row already expired *before* construction is gone once
   `StartupMaintenance` completes; an explicit `EvictExpiredAsync()` still removes a
   just-written `TimeSpan.Zero` row. Proves §2.2 did not weaken the public contract.
3. **Disposal ordering.** After `Dispose()` returns, `StartupMaintenance.IsCompleted` is true, and
   a fresh `SqliteConnection` to the same database opens and writes immediately — deterministic
   proof that no untracked work still holds the database.
4. **Disposal is quiet and idempotent.** Construct-then-immediately-dispose over a cache root
   pre-seeded with many expired artifacts (seeded by a prior, already-disposed writer) throws
   nothing; `Dispose()` twice throws nothing.
5. **Eval mode.** `StartupMaintenance` is already completed on return from the constructor;
   `cache.db` length and last-write time are unchanged across open/evict/dispose; no `-wal`/`-shm`
   sidecar appears.
6. **Concurrency.** Multiple stores over one cache root, constructed concurrently, all reach
   completed maintenance and leave the database usable — extending the existing
   `TwoStoreInstances_SameDb_ConcurrentAccess_IsSafe` shape, not replacing it.

**Escalation rule:** if an invariant genuinely cannot be expressed without an injected seam,
Lambert escalates to me. She does not add one unilaterally. "Minimal public impact" is a merge
criterion, not a preference.

## 4. Assignments

### Ripley — production (R1)

Exactly one file: **`src/HelixTool.Core/Cache/SqliteCacheStore.cs`**.

Deliver §2.1–§2.6. Do not touch `ICacheStore.cs`, `ICacheStoreFactory.cs`, `EvalModeServices.cs`,
or `Program.cs` — if any of them needs to change, the design is wrong and you stop and tell me.
"The production diff is one file" is the checkable form of minimal public impact. Do not open test
files.

### Lambert — tests (L1)

- `src/HelixTool.Tests/SqliteCacheStoreTests.cs` — items 1, 2, 5.
- `src/HelixTool.Tests/SqliteCacheStoreConcurrencyTests.cs` — items 3, 4, 6.
- `src/HelixTool.Tests/SnapshotEvalModeTests.cs` — correct the comment at lines ~53-60. Keep
  `BackupWithRetryAsync`: it still legitimately guards cross-process WAL contention. Rewrite the
  justification so it no longer cites a fire-and-forget constructor task that no longer exists,
  and await the writer's maintenance before the backup now that it is joinable.
- `src/HelixTool.Tests/ExpiredSnapshot.cs` — same treatment for its rationale header. The seeding
  strategy stays (it is race-free by construction and remains the right approach); the paragraph
  explaining *why* must match reality.

Stale comments that describe a fixed bug as current are the same defect class I rejected in the
last documentation cycle. They are in scope here.

### Kane — documentation (K1)

`CHANGELOG.md` `[Unreleased]` only. One entry, referencing #129: startup cache maintenance is now
tracked and canceled/joined on disposal, and the startup pass no longer removes entries written
after the cache was opened.

`README.md:76` stays as-is — the LRU/7-day description remains accurate, and no command, flag, or
environment variable changes. No `docs/cli-reference.md` change. Do not claim maintenance is
"always awaited at shutdown"; §2.3 records why that would be false for the CLI.

## 5. Reject-on-sight conditions for my merge review

1. Any change to `ICacheStore`, or any new `public` member on `SqliteCacheStore`.
2. `catch { }` or `catch (Exception)` anywhere on the maintenance or disposal path.
3. `ClearAllPools()` called before the join, or the CTS disposed before the join.
4. `Task.Delay` / `Thread.Sleep` / `SpinWait` in any test added or modified by this change.
5. A new `DisableParallelization` collection.
6. A test asserting that the startup pass did, or did not, delete a specific row — outcome-of-race
   assertions are banned even when they pass locally.
7. `_startupMaintenance` reachable as null, or a disposal path that can leave a fault unobserved.
8. The stale comments in `SnapshotEvalModeTests.cs` / `ExpiredSnapshot.cs` left describing the
   fire-and-forget behavior as current.
9. Production diff spanning more than `SqliteCacheStore.cs`.

## 6. Deliberately out of scope

- **`SqliteConnection.ClearAllPools()` is process-global** and clears pools belonging to *every*
  store in the process, not just the one being disposed. Real, pre-existing, and orthogonal to
  #129. Recorded here so it is a known wart rather than a future rediscovery.
- Post-disposal operation semantics (today `GetMetadataAsync` on a disposed store still works,
  because connections are per-operation). Not touched; changing it would break callers for no
  reason #129 gives us.
- Periodic/background maintenance beyond the single startup pass. Not requested, and it would add
  precisely the long-lived background work this issue is about removing.

---

---
decision: startup-cache-eviction-implementation
issue: lewing/helix.mcp#129
author: ripley
role: Backend Dev
date: 2026-09-11
status: for-review
depends_on: dallas-startup-cache-eviction-lifecycle
---

# R1 implementation report — startup cache-eviction lifecycle (#129)

Implemented Dallas's accepted design (`dallas-startup-cache-eviction-lifecycle.md`) exactly, in
`src/HelixTool.Core/Cache/SqliteCacheStore.cs` only. No other production file touched.

## What changed

- **Member name for tests to await:** `internal Task StartupMaintenance { get; }` — matches the
  name Dallas specified in §2.3. Never null; eval mode assigns `Task.CompletedTask`.
- **Cutoff pinning (§2.2):** constructor captures `var asOf = DateTimeOffset.UtcNow;` before
  calling `Task.Run(() => EvictExpiredAsync(asOf, _maintenanceCts.Token))`. Public
  `EvictExpiredAsync(CancellationToken ct = default)` is unchanged in observable behavior — it
  still delegates to `DateTimeOffset.UtcNow` at call time via a new private overload
  `EvictExpiredAsync(DateTimeOffset asOf, CancellationToken ct)`.
- **Cancellation checkpoints (§2.4):** added before each `DELETE`, before the artifact `SELECT`,
  and once per `DeleteArtifactRows` loop iteration (new `CancellationToken` parameter on that
  private method, both call sites updated).
- **Dispose ordering (§2.4), exact:** idempotency guard (`Interlocked.Exchange` on `_disposed`) →
  cancel → bounded join (`DisposeJoinTimeout = TimeSpan.FromSeconds(10)`) → narrow fault
  absorption → CTS disposed → `ClearAllPools()` last.
- **Exception handling (§2.5):** the maintenance body catches nothing. `Dispose()` uses
  `AggregateException.Handle(e => e is OperationCanceledException or SqliteException or
  IOException)` — the narrow, named set from the brief's table. No `catch { }` / `catch
  (Exception)` anywhere on this path.

## One implementation note for whoever reviews this against the brief

`Task.Wait(TimeSpan)` never throws a bare `OperationCanceledException`/`SqliteException`/
`IOException` — it always wraps in `AggregateException` (that's how `Task.Wait` works for both
canceled and faulted tasks). So the brief's "caught in Dispose" table is implemented as one
`catch (AggregateException ex) { ex.Handle(...) }` rather than three separate `catch` clauses.
This is the correct narrow-catch shape for this API, not a broadening — `ex.Handle` rethrows a new
`AggregateException` containing anything the predicate doesn't accept, so unexpected exception
types still propagate out of `Dispose()` untouched, per §2.5's "anything else propagates"
requirement. Flagging so it isn't misread as a deviation during merge review.

## Validation

- `dotnet build` on `HelixTool.Core.csproj` and the full solution: 0 Warning(s), 0 Error(s)
  (Release).
- Targeted existing tests (`SqliteCacheStore*`, `SnapshotEvalMode*`, `ExpiredSnapshot*`): 52/52
  passed, run before Lambert's new coverage landed — i.e. against the pre-existing test contract,
  to confirm no regression in current behavior.
- Full existing suite: 1981 passed / 8 pre-existing skips / 0 failed.
- `git diff --stat` confirms the production diff is exactly `SqliteCacheStore.cs` (90 insertions,
  10 deletions). No test file was opened or edited. `ICacheStore.cs`, `ICacheStoreFactory.cs`,
  `EvalModeServices.cs`, and `Program.cs` are all untouched.

## Self-check against §5 reject-on-sight conditions

1. No change to `ICacheStore`; no new `public` member on `SqliteCacheStore` (`StartupMaintenance`
   is `internal`, reachable via `InternalsVisibleTo` to `HelixTool.Tests` already present).
2. No `catch { }` / `catch (Exception)` on the maintenance or disposal path.
3. `ClearAllPools()` is the last statement in `Dispose()`; CTS is disposed strictly before it and
   strictly after the join.
4. N/A — no test file touched.
5. N/A — no test file touched.
6. N/A — no test file touched.
7. `StartupMaintenance` is never null (verified by construction: both branches of the `if
   (options.EvalMode)` assign it).
8. N/A — stale-comment cleanup in test files is Lambert's item (L1), not touched here.
9. Production diff is exactly `SqliteCacheStore.cs`.

Ready for Dallas's merge review whenever Lambert's test PR is ready to land alongside it.

---

---
decision: startup-cache-eviction-fault-reaffirmation
issue: lewing/helix.mcp#129
author: ripley
role: Backend Dev
date: 2026-09-11
status: for-review
depends_on: ripley-startup-cache-eviction-implementation
---

# R1 reaffirmation check — Dispose() fault handling (#129)

Larry reaffirmed a specific property of the already-delivered `Dispose()`: unexpected
(non-cancellation) worker faults must be preserved/propagated when `StartupMaintenance` completes
within the bounded join, and on an actual timeout the eventual fault must be observed without
broad swallowing, with no new public API.

## Result: no code change

I did not assume the existing implementation (from
`ripley-startup-cache-eviction-implementation.md`) already satisfied this — I verified it
empirically with a throwaway scratch console app outside the repo tree (deleted before finishing;
no test file was opened or edited), confirming two .NET behaviors my `Dispose()` relies on:

1. **`Task.Wait(TimeSpan)` always wraps in `AggregateException`** — for both a canceled task and a
   faulted task, never a bare exception. So `catch (AggregateException ex) { ex.Handle(e => e is
   OperationCanceledException or SqliteException or IOException); }` is the correct narrow-catch
   shape for the accepted design's three-row table, not a broadening: `Handle` rethrows a fresh
   `AggregateException` containing whatever the predicate rejects, so an unexpected fault (e.g.
   `InvalidOperationException`) still propagates out of `Dispose()` when the pass completes inside
   the join window.
2. **The timeout-path `ContinueWith` only marks the fault "observed"** for the process-wide
   unobserved-task-exception check — it does not consume it. A second, independent `await` on the
   same `StartupMaintenance` task instance after that continuation ran still received the original
   exception, unchanged. This is what keeps `StartupMaintenance` usable as a genuine fault-carrying
   handle for Lambert's tests even after `Dispose()` has already touched it on a timeout.

Conclusion: the `Dispose()` implementation delivered in the initial R1 pass already satisfies the
reaffirmed contract exactly. No further change was made to `SqliteCacheStore.cs`. Production diff
remains exactly that one file, 90 insertions / 10 deletions, unchanged from the original R1
delivery.

**Validation:** targeted suite 60/60 passed; full suite 1989 passed / 8 pre-existing skips / 0
failed (Lambert's new coverage had landed by this point). Build 0 Warning(s) / 0 Error(s).

## Incidental finding — not mine to fix, already resolved by Lambert

While isolating an intermittent failure in
`SqliteCacheStoreEvalModeTests.EvalMode_OpenEvictDispose_DatabaseUnchanged_No(New)WalOrShmSidecars`,
traced it (via the same scratch-app technique, also reproduced against the pre-#129 original code —
confirming it predates and is unrelated to this fix) to the test harness
(`SnapshotEvalTestHarness.CreateStableSnapshotAsync`): it backs up straight from a live WAL-mode
source into an on-disk destination via `SqliteConnection.BackupDatabase`, which carries the
source's WAL header flag into the destination even with no live `-wal` file. Opening that file
later, even `Mode=ReadOnly`, makes SQLite (re-)establish WAL machinery and (re)create `-wal`/`-shm`
sidecars — exactly what the test forbids. Production's real `SnapshotExporter.ExportAsync` avoids
this by staging through an in-memory connection and asserting `PRAGMA journal_mode = 'memory'`
before serializing to disk, stripping the WAL flag; the test harness didn't replicate that step.
By the time I'd finished isolating it, Lambert had already landed her own fix and the suite was
green — recording the root cause here only so it doesn't need re-discovery if it resurfaces
elsewhere in the test suite.

---

---
decision: startup-cache-eviction-lifecycle-tests
issue: lewing/helix.mcp#129
author: lambert
role: Tester
date: 2026-09-11
status: for-review
depends_on: dallas-startup-cache-eviction-lifecycle
---

# Startup cache-maintenance lifecycle — test coverage (#129)

Implements assignment L1 from Dallas's accepted decision. No production file was touched;
`src/HelixTool.Core/Cache/SqliteCacheStore.cs` was read only to compile against Ripley's
internal contract (`internal Task StartupMaintenance { get; }`), which had already landed in
this worktree by the time I validated.

## What was added

**`src/HelixTool.Tests/SqliteCacheStoreTests.cs`** (3 new facts, normal mode + 2 new facts, eval
mode):
- `StartupMaintenance_ZeroTtlRowWrittenAfterConstruction_Survives` — #129's regression, inverted
  into a permanent guard.
- `StartupMaintenance_RowExpiredBeforeConstruction_IsRemoved` — proves the cutoff still evicts
  genuine pre-existing staleness.
- `StartupMaintenance_DoesNotWeakenExplicitEviction_ZeroTtlRowStillRemoved` — proves the public
  `EvictExpiredAsync(ct)` contract is unchanged after startup maintenance completes.
- `EvalMode_StartupMaintenance_IsAlreadyCompletedOnConstruction`.
- `EvalMode_OpenEvictDispose_DatabaseUnchanged_NoWalOrShmSidecars`.

**`src/HelixTool.Tests/SqliteCacheStoreConcurrencyTests.cs`** (3 new facts):
- `Dispose_JoinsStartupMaintenance_DatabaseImmediatelyWritable` — `busy_timeout=0` proves no lock
  is held, not merely that a wait eventually succeeds.
- `Dispose_ImmediatelyAfterConstruction_WithManyPreSeededExpiredArtifacts_IsQuietAndIdempotent` —
  50 pre-seeded artifacts, immediate + double `Dispose()`, no exception.
- `MultipleStores_SameRoot_AllCompleteStartupMaintenance_DatabaseRemainsUsable` — extends
  `TwoStoreInstances_SameDb_ConcurrentAccess_IsSafe`'s shape to 5 stores; asserts only that every
  pass completes and the db stays usable, never which pass evicted what.

**`src/HelixTool.Tests/SnapshotEvalModeTests.cs`** — rewrote the `BackupWithRetryAsync` rationale
comment; it no longer cites the retired fire-and-forget constructor task. `BackupWithRetryAsync`
itself is unchanged — it still legitimately guards ordinary WAL-checkpoint/connection-pool-release
timing. Added an explicit `await writer.StartupMaintenance;` in `CreateStableSnapshotAsync`,
inside the `using` block, before the writer closes.

**`src/HelixTool.Tests/ExpiredSnapshot.cs`** — rewrote the rationale header. The seeding strategy
(write through a live writer with a live TTL, back up, backdate only the copy) is unchanged and
still the right approach — now explained by the correct current reason (startup maintenance's
construction-time cutoff would make a pre-backdated row a legitimate target for *any* store that
opens it, backup source included) rather than the retired race.

Zero `Task.Delay`/`Thread.Sleep`/`SpinWait`/retry-until-pass loops/`DisableParallelization`
anywhere in the diff. No assertion anywhere is conditioned on which outcome a disposal race
produced — every new assertion is either a database-row-presence check via raw `SqliteConnection`,
or `StartupMaintenance.IsCompleted`/database-usability after an explicit `await`.

## Two findings escalated to future readers (not new seams, no unilateral additions)

1. **`GetMetadataAsync`'s own TTL filter reads `UtcNow` at call time**, so a `TimeSpan.Zero` row
   always reads back as absent through the public getter regardless of whether startup
   maintenance's DELETE actually ran. Tests asserting "did startup maintenance evict/spare this
   row" must check row presence directly via a raw connection to the underlying table, not
   through `GetMetadataAsync`/`IsJobCompletedAsync`.
2. **`SqliteConnection.BackupDatabase` (disk-to-disk) copies the source's persisted
   `journal_mode` header setting, not just rows.** A destination file backed up from a live
   WAL-mode writer is WAL-*tagged* even with no `-wal`/`-shm` file yet on disk; the next
   connection — even `Mode=ReadOnly`, even a pure read — is then entitled to materialize one to
   honor that header, which is ordinary SQLite protocol, not a defect. This does not match what a
   real exported snapshot looks like on disk (production's `SnapshotExporter` stages through an
   in-memory connection and asserts `EnsureNoDatabaseSidecars`). The new eval-mode "no sidecar"
   test forces `PRAGMA journal_mode=DELETE;` on the seeded copy before asserting the clean
   baseline, rather than assuming the disk-to-disk backup helper alone produces one.

## Validation

- 43 targeted tests (`SqliteCacheStoreTests`, `SqliteCacheStoreConcurrencyTests`,
  `SnapshotEvalModeTests`) green across 3 consecutive runs.
- Full suite: 1989 passed, 8 skipped, 0 failed, across 3 consecutive runs.
- Required `DOTNET_ROLL_FORWARD=Major` locally (only .NET 11 preview SDK/runtime installed;
  projects target `net10.0`) — environment quirk, not a code issue, previously logged.

## For Dallas's review

No reject-on-sight condition from §5 of the design decision applies to this diff: no
`ICacheStore`/public-member changes (I only read `SqliteCacheStore.cs`), no banned test
primitives, no new `DisableParallelization`, no outcome-of-race assertion, and both stale
comments (`SnapshotEvalModeTests.cs`, `ExpiredSnapshot.cs`) now describe current behavior.

## Follow-up: non-cancellation worker-fault propagation through bounded disposal

Creator asked specifically whether bounded disposal hiding a non-cancellation worker fault could
be covered deterministically without a new broad/public seam or violating the no-hook/no-race
constraints. It can, via an already-reachable production path — no seam added.

**The path:** `DeleteArtifactRows` catches only `IOException` around
`CacheSecurity.ValidatePathWithinRoot`. That method throws `ArgumentException` — a type §2.5
names explicitly as required to propagate out of `Dispose()` — when a `cache_artifacts.file_path`
resolves outside the artifacts root (e.g. an absolute path). A `cache_artifacts` row with a
corrupted `file_path`, seeded directly via SQL (same technique the existing
`StaleRowCleanup_FileDeletedFromDisk_ReturnsNullAndCleansUp` test uses) and paired with
`ArtifactMaxAge=Zero` (a guaranteed startup-maintenance eviction candidate, used identically by
`Dispose_ImmediatelyAfterConstruction_WithManyPreSeededExpiredArtifacts_IsQuietAndIdempotent`),
makes the fault deterministic: no timing dependency, no injected hook, reproducible every run.

**Test added:** `StartupMaintenance_NonCancellationWorkerFault_PropagatesThroughAwaitAndDispose`
(`SqliteCacheStoreConcurrencyTests.cs`). Two assertions:
1. `await Assert.ThrowsAsync<ArgumentException>(() => store.StartupMaintenance)` — the fault is
   directly observable through the internal awaitable, proving it is real production behavior.
2. `Record.Exception(() => store.Dispose())` is non-null and unwraps to that same
   `ArgumentException` (`Dispose()` surfaces it via `AggregateException` from `.Wait()`) — proving
   bounded disposal does not launder it into silence.

Verified empirically on the first attempt: 5 repeated runs of the new test green, full suite 1990
passed / 8 skipped / 0 failed across 3 repeated runs. No regression, no flake observed.

**One test-hygiene note, not a production concern:** when `Dispose()` faults before reaching
`SqliteConnection.ClearAllPools()`, pooled native SQLite handles for that store are never
released by the store itself — expected, since a logic bug must not be laundered into silence,
and out of scope for #129. The test's own `finally` calls the same public
`SqliteConnection.ClearAllPools()` API itself (not a new seam) before deleting its temp
directory, to avoid a lingering pooled handle making cleanup flaky, particularly on Windows.
