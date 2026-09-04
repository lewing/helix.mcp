# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, and strict-mode architecture.

**Current Focus:** PR #132 implementation is complete, final internal review is approved, and external review is in progress.

---

## 2026-06-01 Through 2026-08-31: Summary

Completed five major decision cycles:

1. **Parameter Plumbing (June):** Fixed three azdo_* parameter bugs (minTime/maxTime/queryOrder missing, top not forwarded, outcomes hardcoded). PR #78 merged; 14 new tests; all 1337 tests pass.

2. **Numeric Alias Coercion (June):** Implemented CoerceToStringElement in CallToolFilter to handle numeric build_id values. Gap fixed; Ripley + Lambert execution.

3. **Parameter Alias Layer (June):** Established canonical parameter names to reduce agent confusion (buildIdOrUrl, not build_id/buildUrl).

4. **Tiered outputSchema Reduction (July):** Refined "flatten all" → targeted approach (FLATTEN 10 / KEEP 3 / LEAVE 12). Net savings ~5,450 bytes (18% reduction). Dallas decision filed; work approved pending implementation.

5. **Strict-Mode Architecture (July):** Triaged Issue #81/#82 sequencing; designed CallToolFilter layer for unknown-param rejection. Correctness prioritized before cleanup.

All decisions archived to decisions.md. No lockouts issued; steady progress on parameter safety and schema reduction.

---

## Recent Detailed Work


## Learnings

### 2026-07-28: helix_find_files workItem review

**Finding:** When adding an optional parameter to an MCP tool, parameter position matters for C# test callers but NOT for MCP wire callers (JSON binds by name). The sibling convention (jobId → workItem → pattern → maxItems) is the right ordering for MCP tools even if it shifts positional args in tests — tests should use named arguments.

**Finding:** Anticipatory tests written via reflection (to compile before the implementation) become technical debt once the implementation lands — they should be simplified to direct named-argument calls. Flag for Lambert.

**Finding:** Hardcoded `[InlineData]` lists for schema-consistency `[Theory]` tests are pragmatic when no attribute/marker distinguishes the target tool class, but carry a maintenance burden. A code comment near the list is the minimum viable guard.

**Decision:** APPROVED Ripley's implementation + Lambert's tests. Non-blocking: simplify reflection-based tests to named-arg calls, clean up stale "RED until" comments.

## 2026-07-28 — helix_find_files workItem parameter review
Reviewed Ripley's FindFilesAsync + MCP tool implementation and Lambert's test suite. Verified parameter ordering consistency, fast-path correctness, error handling, and tool description. APPROVED. No source-compat concerns. Assigned Lambert non-blocking follow-up tasks: simplify tests, remove stale comments, harden schema test.

---

## 2026-08-20 — MCP C# SDK 1.4.0 → 2.2.0 migration decision

**Verdict:** APPROVED phase-1 (package bump + one explicit config line). REJECTED Ash's
hybrid-session-mode recommendation. Mandated explicit `SessionMode` + 4 new tests.
Decision: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md`.

### Learnings

**Verify agent-reported release metadata against the registry, always.** Ash and Ripley
disagreed on every release date. Ripley was right; Ash dated 2.2.0 to "today" and 1.4.0 to
"early 2026" (actual: 2026-08-13 and 2026-06-04). One `curl` to the NuGet registration
index settled it in seconds. **Both** reports also missed 1.4.1 entirely — the servicing
release on the old major, which is the correct rollback target. Analysts reliably enumerate
the *new* major's releases and skip servicing releases on the *old* one.

**A changed default is a silent diff — force it into source.** `WithHttpTransport()` with no
options changed from stateful to stateless across the major bump with zero textual change at
the call site. The most consequential effect of an upgrade should never be invisible in code
review. Declaring `SessionMode = Stateless` explicitly costs one line and buys: a reviewable
diff, immunity to a future default flip, operator-readable intent, and a place to hang the
"here's the escape hatch" comment. Pair it with an assertion test so deleting the line fails.

**Convenience-proxy properties are a migration trap.** Ripley's fallback snippet
(`Stateless = false`) is a bool proxy over the real `SessionMode` enum, and `false` maps to
`Stateful` — which *refuses* new-revision clients with -32022. The value a maintainer would
actually want (`StatefulForInitializeClients`) is unreachable through the bool. When an SDK
adds an enum alongside a legacy bool, adopt the enum immediately; the bool cannot express
the state space and encodes a wrong answer for the most likely future need.

**Reject backward-compat modes that protect capabilities you don't use.** Hybrid mode's sole
function is handing real sessions to down-level clients. Sessions exist for `Mcp-Session-Id`
affinity, standalone GET SSE, and server→client requests — we use none. Adopting it would
have reintroduced session affinity plus already-`[Obsolete]` idle-session tracking on day
one, and load-balancer affinity is a one-way ratchet operationally. Test for a backward-compat
mode: name the specific capability it preserves, then check we actually use that capability.

**"No impact" without evidence is the thing to gate on.** Both reports independently declared
progress notifications unaffected — neither demonstrated it. That was the *only* place our
design touched what stateless mode removes (unsolicited server→client messages). Reading
`TokenProgress` confirmed progress is request-scoped and should ride the POST response stream,
but agreement between two agents who both reasoned from release notes is not evidence. When
every report converges on "no impact" for the one genuinely adjacent risk, that convergence
is the signal to demand a test, not to relax.

**Spec breakage ≠ SDK breakage.** Ash framed the `initialize` handshake as "gone" because the
2026-07-28 spec removed it. The SDK retains it as the down-level fallback path (probe
`server/discover`, fall back to `initialize`). Reading the spec and reading the SDK's
compat shims are different jobs; overstating this would have justified defensive work we
did not need.

**Cheap void-checks first.** Ash's "MODERATE: must run on .NET 8.0+" collapsed against one
grep — every project is `net10.0`. Grep the repo for the claimed constraint before spending
any reasoning on a reported risk.

**Reflection-based invariant guards beat one-time verification.** #1568 changes wire format
only for non-object structured returns. We have none *today*. A reflection test asserting
every `UseStructuredContent = true` tool returns an object type converts a point-in-time
audit into a standing guard — the cheapest way to stop a future scalar-returning tool from
silently changing the wire format.

---

## 2026-08-20T15:47:37.675-05:00: Review — MCP SDK 2.2.0 migration + T1–T4 (REJECTED)

Reviewed Ripley's phase-1 implementation and Lambert's T1–T4/G4 against my accepted decision.
Production diff, G1, G6, T1, T4 approved. **T2, T3, G4 rejected; G7 found never executed.**
Verdict: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-review.md`.

**I was wrong in my own decision doc, and the error propagated into the test that was supposed
to catch it.** My §1 accepted Ash's and Ripley's shared conclusion that #1568 could not affect
us "because all structured tools return objects." That reasoning is invalid:

> **#1568's trigger is *schema* object-ness, not *CLR* object-ness.** A CLR class carrying a
> custom `[JsonConverter]` is opaque to `System.Text.Json`'s schema exporter, which emits the
> permissive schema `true`. SDK 1.4.0 classified such a type as a **non-object** and wrapped it
> in `{"result": …}`. 2.2.0 does not. The CLR type never changed; the envelope did.

Six tools returning `LimitedResults<T>` (custom converter factory) silently lost their
`{"result": …}` envelope — 24% of our tool surface, user-visible, shipping unremarked.

**How I caught it, and the generalizable move: reconcile a reported delta arithmetically
before accepting its narrative.** Lambert's G4 numbers were correct and the explanation —
"SDK 2.2.0 generates a more compact schema representation… a favorable improvement" — was
plausible. What broke it open was that `outputSchema` dropped to **4 bytes**. Four bytes cannot
describe an object; it is `true`. From there: `{"type":"object","properties":{"result":true},"required":["result"]}`
is **exactly 68 bytes**, and `6 × (68−4) = 384` matched the reported delta exactly. Three
independent numbers agreeing turned a hypothesis into proof without running the old SDK.
**Suspiciously small magnitudes are structural evidence.** A schema that shrinks by 94% has not
been compacted, it has been emptied.

**A green test suite is only evidence about what it asserts.** "1526 passed, 0 failed" was
offered as reassurance. `grep -rl "structuredContent" src/HelixTool.Tests/` returned exactly one
file — the new T2. No test in the repo had *ever* asserted the structuredContent wire shape, so
the suite was structurally incapable of detecting this. **Before crediting a green suite on a
specific risk, grep for a test that mentions it.** One grep, and "1526 passed" stops being an
argument.

**My own §6 T2 specified the wrong assertion — and a wrong guard is worse than none.** I asked
for a reflection test that the CLR return type is non-scalar. Lambert implemented exactly that,
correctly. `LimitedResults<T>` is a sealed class, so it *passed* — the guard green-lit the live
regression it was commissioned to prevent, and that false confidence is why the G4 delta got
rationalized instead of investigated. Correction to the prior entry in this file: an invariant
guard must assert on **the artifact that actually goes on the wire** (the generated
`ProtocolTool.OutputSchema`), not on a proxy for it (the CLR type). Acceptance criterion for
such a guard: **it must fail on the current tree.** A guard that passes on first write has not
been shown to guard anything.

**Reconstruction tests: reject them when the thing under test is that a specific line exists.**
Lambert's T3 rebuilt `Program.cs`'s registration in test code (top-level statements block
`WebApplicationFactory<Program>`) and escalated rather than adding a seam unilaterally — exactly
right. But the reconstruction asserts that ASP.NET Core's options binding round-trips a value:
Microsoft's job, cannot fail for any reason we care about, and stays green if the production
line is deleted. **Config-intent pins are worthless unless driven through the real composition
root.** Ruled for `public partial class Program;` — one behavior-neutral line, and it *also*
unblocks a real G7 against real auth middleware and real `AddScoped` lifetimes. When weighing a
small production seam, count every gate it unlocks, not just the one that prompted the ask.

**A test that goes red for the right event but points at the wrong culprit is a liability.**
Lambert's supplementary `SdkDefault_SessionMode_IsNotSomethingThisAppShouldRelyOn` asserts the
*SDK's* default equals Stateless. If a future SDK flips that default — the exact scenario the
pin exists for — this test fails while blaming the SDK, and the real regression stays green. It
trains the reader to distrust the wrong thing. **Rationale belongs in a comment; assertions
belong on things we control.**

**Gates handed off between agents get dropped in the seam.** G7 was Ripley's; Ripley deferred it
to "Lambert's TestHost coverage," which was never in Lambert's scope. Both reports read as
complete; nobody ran it. And the fixture that appeared to cover it registered `HelixService` as
`AddSingleton` where production uses `AddScoped` — **inverting the exact lifetime G7 exists to
prove.** Two lessons: assign each gate to exactly one owner and require that owner to post
evidence or explicitly escalate; and when a fixture is offered as covering a gate, diff its DI
registrations against production before crediting it.

**What held up.** T1 was excellent and retired the migration's highest risk: real `McpClient`
over real `HttpClientTransport` on `TestServer`, real tool and adapter chain, substitution
pushed down to `IHelixApiClient`, and a `TaskCompletionSource` gate proving the notification
crossed the per-request SSE stream *while the call was in flight* rather than merely landing
eventually — without that gate the assertion would have passed vacuously. Ripley's G6 substituted
a scripted raw JSON-RPC-over-stdio harness for the GUI client I specified and picked a
"No API call made" tool for determinism — better than what I asked for. **Accept substituted
evidence that clears the bar by a better method; the gate's purpose outranks its prescribed form.**

## 2026-08-20T17:15:37.128-05:00 — Final self-review of the MCP SDK 2.2.0 migration (REJECTED again: F1, F2, F3)

**A reported-green suite is only green in the reporter's environment.** Ripley's B1–B3 report
said "1,554 passed / 0 failed," and it was true — on a machine with no API key configured. I
re-ran it myself and got **Failed: 1**: the new `WebApplicationFactory<Program>` test boots the
real host, the real host reads the ambient `HLX_API_KEY`, and the test sends no header, so
initialize returns 401 before it ever reaches the transport. Deterministic in both directions,
not flaky. **Re-run the suite yourself in your own environment before crediting a green
claim** — and when a report omits the environment, that omission is the finding.

**The seam I mandated is the seam that broke.** I ruled Option B (boot the real `Program`) over
Ripley's proposal precisely to make T3 non-tautological, and it worked — T3 now detects a real
config mutation. But booting the real composition root inherits the real app's **environment
coupling**, and I did not anticipate that when I ruled. When a review mandates testing against
production wiring, the same ruling must specify how the test isolates production's ambient
inputs. Own the consequence of your own ruling.

**Scratch evidence is not gitignored by default.** `git check-ignore` proved `.squad/artifacts/`
and `.squad/evidence/` were **not** covered — including a 642-line verbatim copy of upstream SDK
test source with zero license or attribution, one `git add -A` from shipping into a product PR.
**Run `git check-ignore` over every untracked file before calling a diff clean;** reading the
file list is not the same as knowing what will be committed. And two production XML docs cited
that scratch path, so deleting it silently dangles references — cross-references into scratch
directories are a defect at the moment they are written.

**A guard scoped by a hardcoded list has a silent expiry date.** The rewritten structured-content
guard enumerates three tool classes by name; a fourth `[McpServerToolType]` would be silently
unguarded. That is structurally the *same* defect — a guard not covering what it claims — as the
tautology it replaced. When approving a replacement guard, check that the fix changed the failure
mode and not just the assertion.

**Verify a subordinate's severity claim, not just his mechanism — and accept it when he is
right.** Ripley inverted my §1 framing: I had called the legacy envelope a live regression, when
in fact doing nothing preserved legacy clients exactly and *the fix* is what changes them. He was
correct, and I accepted it in full. The point of re-review is that it can correct the Lead. But
verify the mechanism independently anyway: I generated the schema by reflection and measured
1,319 bytes for `azdo_builds`, matching his table, before crediting the number.

**A reassigned gate is still a dropped gate.** G7 has now been dropped by two owners across two
reviews (Ripley deferred, Lambert did not deliver). Reassignment alone does not create
accountability. Third assignment carries a single named owner, an explicit "post evidence or
escalate — silence is not delivery" clause, and no further reassignment. I also bound it to the
*same* fixture as the F1 fix, because splitting one fixture across two agents is exactly the
seam it fell through the first time.

**Byte-budget deltas should be decomposed, not narrated.** Lambert explained a +2,440-byte
`tools/list` growth as "more compact schema representation"; Ripley decomposed it exactly —
−897 (23 async tools × 39 bytes of dropped `execution.taskSupport`, with the two synchronous
tools unchanged as the control) + 3,337 (six real schemas), zero residual — which also disproved
the compaction narrative outright, since the 14 unaffected tools were byte-identical. **Demand
arithmetic that closes to zero; a plausible story about a number is not an account of it.**

---

## 2026-08-20 — Re-review of F1/F2/F3 → APPROVED (MCP SDK 2.2.0 migration cleared for PR)

**Verdict:** APPROVED. All three blocking findings resolved. Commit/PR may proceed; only the
PR-description gate remains (writing task, no code impact).
Decision: `.squad/decisions/inbox/dallas-csharp-mcp-sdk-rereview.md`.

### Learnings

**Mutation-test the gate before you accept it green — especially a gate that has been dropped
before.** G7 had been dropped by two owners across two reviews. A passing run proves the test
runs, not that it *discriminates*. I mutated production four ways and rebuilt each time:
`AddScoped<IHelixApiClient>`→`AddSingleton` (RED), `ComputeTokenHash(token)`→a constant (RED at
the partition assertion), removing `UseApiKeyAuthIfConfigured()` (RED on both 401 facts), and
`SessionMode`→`Stateful` (RED on both T3 facts). Only after that did the evidence mean anything.
The constant-hash mutation was the decisive one: it proved the cache-partition assertion is bound
to the real production computation rather than to a value the test itself supplies. Neither
implementer had demonstrated their gate *could* fail — that demonstration is the reviewer's job
when the gate is load-bearing. Do it with a SHA-256 checksum of the original file and verify
byte-identity on restore, so the diligence cannot corrupt the tree.

**"Test doubles must preserve the lifetimes under test" is checked by reading the production
registrations, not by reading the doubles.** My instinct was that `AddSingleton` doubles would
invalidate a per-request scoping proof — that instinct was right for `StatelessMcpTestHost` and
*wrong* here. `IHelixApiClientFactory` and `ICacheStoreFactory` are **already singletons in
production**, so singleton doubles introduce zero lifetime inversion, and every scoped
registration in the chain runs untouched. More than that: singleton factories are the *only*
correct observation point, because a singleton's recording queue accumulates across requests,
which is precisely what makes cross-request comparison possible. **A singleton test double is
suspicious only when it replaces a scoped production registration.** Compare the two lists
side by side before concluding.

**Specify the property, not the implementation — subordinates can beat your fix.** For F1 I
offered two remedies: attach the header, or clear the variable. Lambert took neither verbatim and
chose better — read the *same* env var through the *same* production constant, apply the *same*
non-empty predicate the middleware uses, and attach the header only when the real host would
require it. Result: both worlds stay under test rather than one being suppressed, and because
both sides reference `ApiKeyMiddleware.EnvVarName`/`HeaderName` rather than literals, a rename
cannot silently desync them. Had I mandated "clear the variable," I would have shipped a strictly
weaker gate.

**A test-ordering assumption should be asserted, not commented.** The G7 class shares one fixture
across four facts; three never dispatch a tool call. Rather than documenting that, the test
asserts `Count == 2` exactly — which converts the assumption into a tripwire that also catches
request retries and duplicate DI resolutions. Mutation M1 confirmed it fires. Prefer an assertion
that encodes the invariant over a comment that describes it.

**Gitignore *and* delete; either alone regresses.** F2 was closed by removing the vendored
upstream source and adding the paths to `.gitignore`. Deletion alone regresses the moment anyone
recreates the directory; ignoring alone leaves the licensing exposure on disk and in any archive.
Also: when a doc citation must point at third-party source, pin it to a **tag** (`blob/v2.2.0/…`),
never `main` — and actually fetch the URL. I did; it returned 200.

**Don't promote a pre-existing wart to a blocker on re-review.** I found eight source files citing
`.squad/decisions/inbox/` paths in XML docs — a gitignored directory, so those references dangle
for anyone reading the PR. Real inconsistency (the tracked precedent cites *tracked* files), but
the same pattern sits in T1/T2/T4 artifacts I had already approved without flagging. Blocking on
it now would be scope creep and would punish the revision authors for my earlier omission.
Recorded as MINOR with a concrete remedy. **A re-review is bounded by the findings that caused
the rejection; new observations get filed, not escalated.**

**Re-run the headline numbers yourself even when the report looks solid.** Lambert reported
1558/2/0/1560 in both env worlds. I reproduced it exactly — targeted (16/16) and full suite, both
worlds, before and after mutation testing. Cheap, and it converts a claim into a fact. It also
caught a small transcription error in Kane's report (files listed as `M` that are actually `??`),
harmless here but the kind of thing that misleads a later reader.

---

## 2026-08-20T18:00:34.354-05:00 — Review of Ripley's fixed-context revision → APPROVED (minimal `{"type":"object"}` outputSchema)

**Verdict:** APPROVED, no blocking code findings. Every claim reproduced independently.
Decision: `.squad/decisions/inbox/dallas-minimal-schema-review.md`. Only outstanding gate is the
PR #123 body, which I found materially inaccurate and for which I supplied exact replacement text.

### Learnings

**`git checkout --` is not a mutation-restore tool when the work under review is uncommitted.**
Mid-mutation I reverted `AzdoMcpTools.cs` with `git checkout --` and silently destroyed Ripley's
undelivered changes — the file went back to `HEAD`, not to the state I was reviewing. The only
reason I caught it is that I had taken SHA-256 checksums *before* mutating and compared after every
restore; the hash came back `a6d9d2d0…` instead of `61db2141…`. I rebuilt the six substitutions and
the record deletion from the diff I had captured in context and re-verified byte identity. **On an
uncommitted diff, restore by exact reverse string replacement, never by `git checkout`** — and treat
the pre-mutation checksum as mandatory, not as diligence theatre. It is the difference between a
recoverable slip and destroying a subordinate's unpushed work.

**A constant remainder is a stronger "nothing else changed" proof than reading the diff.** Rather
than eyeballing that only six attribute lines moved, I subtracted the six `LimitedResults<T>`
schemas from the all-tools `outputSchema` total in each of the three states: 8,961−408, 12,298−3,745,
8,655−102 — **8,553 every time**. One arithmetic identity certifies that all 14 other structured
tools are byte-identical across a *major SDK boundary*, which no amount of diff-reading does.
Prefer an invariant that must hold over an enumeration that must be complete.

**Verify the baseline's behaviour from the old SDK's source, not from the new one's model.** The PR's
compatibility note claimed the fix only moves pre-`2026-07-28` clients. I fetched
`AIFunctionMcpServerTool.cs` at tag **v1.4.0** and found `CreateStructuredResponse(object?)` — no
protocol-version parameter at all, wrapping decided once at creation time. `main` therefore shipped
`{"result": …}` to *every* client, so the change is universal, not down-level-only. The note wasn't
merely imprecise; it understated the blast radius, and it did so because it described the
*intermediate* PR state (2.2.0 without the fix) as if it were the merge target. **When a compat note
describes "before," pin which commit "before" means and read that commit's SDK.**

**Reproduce the numbers by re-running the repo's own harness in throwaway worktrees.** Detached
`git worktree add` at `6eb3905` and `9f007f19` let me run the existing
`McpToolsListPayloadTests.ToolsListPayload_ReportActualBytes` unmodified in each and get
30,366 / 32,806 / 29,163 — all three matching Ripley exactly, including the six per-tool sizes in
declaration order. Cheaper and far less error-prone than writing a probe, and it uses the same
measurement definition the branch is being judged by. Remove the worktrees in the same session;
`git worktree list` is the check that you did.

**Demand that the delta decompose, then check the decomposition at tool granularity too.**
−1,203 = −306 (schemas) + −897 (dropped `execution.taskSupport`), zero residual — and
`azdo_builds` alone: 2,103 → 2,013 = −51 − 39. A total that closes to zero can still hide two
compensating errors; a per-tool row that also closes to zero cannot.

**Mutation-test the *documented trap*, not just the guard.** The skill Ripley wrote warns that
`typeof(object)` exports as `true` and reintroduces the version split. I mutated one tool to
`typeof(object)` and watched the advertised schema become `true` at `2026-07-28` and the 68-byte
`{"result":true}` placeholder at `2025-06-18` — the split rendered visible in the schema itself. That
single mutation simultaneously proved the guard discriminates *and* that the skill's claim is true.
When a subordinate's write-up asserts a trap, the mutation that proves the guard should be the trap.

**"Weakened validation" must be judged against the merge target, not against the rejected draft.**
My instinct was that dropping a property mirror weakens the contract. It does — relative to the
*pre-revision PR*, which never shipped. Relative to `main`, whose schema was
`{"properties":{"result":true}}` (the permissive any-schema), descriptiveness is a **wash**, and
self-consistency actually improves because `main` advertised `required:["result"]` for a payload the
revision no longer emits. The C# SDK never validated `structuredContent` against `outputSchema` in
either version, so nothing enforceable was lost. **Ask "what does this PR change for a consumer of
`main`" before calling anything a regression.**

**Explicit `OutputSchema` short-circuits the return-schema path — the marker is more robust than
claimed.** SDK 2.2.0's `CreateOutputSchema` returns `toolCreateOptions.OutputSchema` *before*
consulting `function.ReturnJsonSchema`, so a future `[return: Description]` or `<returns>` doc
cannot inflate these six tools' 17 bytes. Reading two functions past the one under discussion turned
a maintenance worry into a documented guarantee. Read the caller and the fallback, not just the
predicate.

---

## 2026-08-26 — Snapshot export successor hardening design

Facilitated the required pre-work design review for the standalone successor to merged PR #125.
Inspected current `origin/main` and all five unresolved review threads. Approved a surgical
contract in `.squad/decisions/inbox/dallas-snapshot-hardening-design.md`.

The decisive architecture choices are:

- SQLite online backup (`SqliteConnection.BackupDatabase`) is the database consistency boundary;
  export never checkpoints or copies the live DB/WAL/SHM file set.
- Artifact selection comes from the backed-up `cache_artifacts` rows, not recursive live-directory
  enumeration.
- The complete temporary snapshot must pass schema, `integrity_check`, no-sidecar, containment,
  existence, and size checks before same-parent atomic rename.
- Destination comparison uses physical canonical paths, component-by-component link/junction
  resolution, Windows case-insensitive boundaries, and non-Windows ordinal boundaries. Resolution
  ambiguity fails closed before temp creation.
- The auth warning is unconditional. Identical `AZDO_TOKEN` plus identical effective
  `AZDO_TOKEN_TYPE` can replay environment-keyed entries through the merged environment-only eval
  accessor; Azure CLI identities remain unreproducible.
- Traversal errors invalidate a snapshot but never increment `MissingArtifactFiles`.

Ownership is non-overlapping: Ripley owns Core implementation, Kane owns `SnapshotCommands.cs` and
the PR narrative, and Lambert owns `SnapshotExportTests.cs`. Cache options, composition roots,
SQLite store behavior, project files, fixture format, key normalization, record mode, and Vally APIs
are frozen.

---

## 2026-08-26 — Snapshot export hardening independent review

Issued an overall **REJECT** while accepting Ripley's Core implementation and Kane's CLI wording.
The blocking defect is confined to Lambert's concurrency test artifact: its checkpoint readiness
counter advances for any checkpoint result row, including a zero-page attempt that can occur before
the first writer commit, and its baseline metadata check proves only row count rather than the
seeded keys and JSON values.

Targeted tests passed 29/29, the stress test passed twelve repeated runs, and the full suite passed
1,661 with two pre-existing skips. Those green runs do not repair a missing proof invariant.
Lambert is locked out of the next `SnapshotExportTests.cs` revision. Because every other rostered
specialist is barred by charter from writing tests, I explicitly requested escalation to a new
.NET concurrency/filesystem test specialist.

---

## 2026-08-26 — Parker snapshot stress revision re-review

**Verdict:** **REJECT.** Parker fixed both original proof gaps: checkpoint readiness now follows a
committed write and requires positive checkpoint progress, and every exported snapshot checks the
three exact baseline keys and JSON values.

Repeated execution exposed a new shutdown race in the same checkpoint loop. Two separate campaigns
both failed on attempt 24 because SQLite returned a WAL-page count of minus one after no WAL was
currently present, and the test treated that non-progress response as an assertion failure. The
targeted snapshot selection passed 40 tests, but the stress test passed only 46 of 48 repeated
attempts before the two failures stopped their campaigns.

Parker is now locked out of this artifact, Lambert remains locked out, and I requested another new
independent .NET and SQLite concurrency test specialist. The revision gate remains closed, so the
pull request is not ready for final suite or CI.

---

## 2026-08-26 — Bishop snapshot stress revision final re-review

**Verdict:** **APPROVE.** Bishop's narrow change handles only the exact no-current-WAL result after
checkpoint readiness as non-progress. It preserves the committed-write ordering and requires
positive checkpoint progress before readiness. The same response before readiness, malformed rows,
and inconsistent values still fail.

The cancellation, finite busy handling, bounded final wait, and background exception propagation
are unchanged. All 29 tests in `SnapshotExportTests.cs` passed with
`DOTNET_ROLL_FORWARD=Major`, followed by 48 consecutive passes of the writer/checkpointer stress
test.

The revision gate is cleared. The complete pull request is ready for final full-suite and Ubuntu
and Windows CI validation.

---

## 2026-08-26 — PR #127 review-thread triage

**Verdict:** **REJECT.** The unresolved database-link finding and the related artifact-directory
finding are both valid blockers. `SnapshotValidator` resolves `cache.db` and `artifacts/` but never
requires either resolved path to remain a strict child of the resolved snapshot root. It can
therefore validate an external database, an external artifact tree, or an `artifacts/` alias back
to the snapshot root.

The duplicate layout assertion is valid but minor. The orchestration log also records the wrong
decisions-file path in two places; that is not a runtime blocker, but the log is rejected as an
inaccurate record. The 28 focused exporter/validator tests pass, confirming that current coverage
does not exercise these aliases.

Ripley is locked out of the rejected validator revision. Lambert and Parker remain ineligible for
the test artifact, and Bishop owns the current rejected version, so a new independent test owner
is required. I requested a new .NET filesystem-security implementer and a separate cross-platform
filesystem test specialist. Scribe is locked out of the rejected log revision; Kane may make the
two factual path corrections. Detailed acceptance criteria are in
`.squad/decisions/inbox/dallas-pr127-review-triage.md`.

---

## 2026-08-26 — PR #127 boundary revision recheck

**Verdict:** **APPROVE.** Brett's validator now rejects a resolved database or existing artifacts
directory unless it is a strict physical child of the snapshot root, at the required points before
sidecar/database or row/file inspection. The comparison is separator-aware, ignores case only on
Windows, rejects equality, and preserves the missing-artifacts warning.

Burke's three focused regressions exercise an external database alias, a populated external
artifacts alias, and an artifacts alias to the snapshot root on every platform, with Windows
junctions for directories, safe alias cleanup, and focused boundary assertions. The duplicate
layout assertion is gone. Kane corrected both decisions-file references.

All 43 focused snapshot tests passed with `DOTNET_ROLL_FORWARD=Major`. The review gate is cleared,
and PR #127 is ready for the full suite and Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 second-review triage

**Verdict:** **REJECT.** The fresh macOS containment finding is valid and blocking. Exporter
boundaries use ordinal comparison outside Windows, but this worktree's macOS volume resolves
case-only spellings to the same directory. A new child below a case-only source spelling can
therefore bypass containment. The current case-only test returns without assertions on macOS and
passes vacuously.

The suppressed test findings are also valid. The hardening rewrite removed exporter rejection
coverage for missing source, database, destination parent, schema versions, and required tables,
and removed validator coverage for missing layout, wrong schema, and missing tables. The new
integrity check has no corrupt-database regression. The current-focus record is stale: the
1,661-test local suite and refreshed Ubuntu, Windows, and Squad checks completed successfully at
`dcc755a5`, although this rejection now requires them to run again after revision.

Ripley is locked out of the exporter revision. Lambert, Parker, Bishop, and Burke are locked out of
the next `SnapshotExportTests.cs` revision, which must have one newly recruited independent .NET
filesystem and SQLite test owner. Kane may correct the current-focus record; Scribe is locked out.
The exact revision and acceptance gates are recorded in
`.squad/decisions/inbox/dallas-pr127-second-review-triage.md`.

---

## 2026-08-26 — PR #127 second-review recheck

**Verdict:** **APPROVE.** Frost applied the required conservative macOS/Windows ignore-case
boundary rule while preserving ordinal Linux/other behavior, separator boundaries, pre-creation
containment, and the destination-parent recheck. Hudson restored every named exporter and validator
negative case, replaced the vacuous macOS test with an actual-filesystem branch, and added
deterministic non-throwing corruption coverage. Ten repeated corruption runs passed. No global
SQLite pool clear or unrelated weakening appeared.

Kane's focus record accurately captures the prior completed gates, the reopened review, the named
revisions, and the required reruns. All 43 focused `SnapshotExportTests` cases passed with
`DOTNET_ROLL_FORWARD=Major`, with no skips or failures. The revision gate is cleared; PR #127 is
ready for the full local suite and fresh Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 Ubuntu CI triage

**Verdict:** **REJECT** Hudson's current test revision; Frost's production revision remains
accepted. Ubuntu's case-sensitive success path preserved the source `cache.db` and artifact bytes
exactly but SQLite's read-only WAL opens materialized `cache.db-shm` and an empty `cache.db-wal`.
Those SQLite-managed sidecars may legitimately appear or disappear and are not source corruption.

The independent replacement must compare exact persistent source payload bytes and logical database
state while excluding only the two root SQLite sidecars on the success path. It must not weaken the
full-tree source checks on pre-database-open rejection paths or their shared helper. No production
change is warranted. Hudson is locked out from revision and advice; prior test-owner lockouts remain,
so the Coordinator must recruit a new independent .NET/SQLite filesystem test owner. Windows was
canceled during restore and must run again with the full suite and Ubuntu after Dallas re-review.

---

## 2026-08-26 — PR #127 Ubuntu CI recheck

**Verdict:** **APPROVE.** Vasquez's local success-path helper excludes only root regular-file
fingerprints for SQLite's WAL/SHM lifecycle while retaining exact database, artifact, other-file,
directory, and link comparison. Integrity, schema/user versions, the complete schema, and every
fixture table column are compared before and after export.

The case-insensitive rejection branch and shared strict helper are unchanged, and both platform
branches remain substantive. With `DOTNET_ROLL_FORWARD=Major`, the case-only test passed its build
run plus 10 repeated no-build runs, and all 54 focused snapshot tests passed with no skips. Frost's
production exporter remains accepted and frozen; the local gate is cleared for the full suite and
fresh Ubuntu/Windows CI.

---

## 2026-08-26 — PR #127 WAL readiness CI triage

**Verdict:** **REJECT** the stress helper at `9a7fd86`; Frost's production exporter remains accepted
and frozen. Ubuntu exposed the startup form of the WAL lifecycle race Bishop handled only after
readiness: a committed-write signal does not guarantee that a separately and lazily opened
checkpointer connection immediately has a current WAL, so exact `(-1,-1)` is legitimate
non-progress before readiness too.

The replacement must establish and hold an explicit WAL writer/anchor, assert WAL mode on both
worker connections, sequence worker initialization before a known committed write, and retry exact
`(-1,-1)` under a bounded timeout without completing readiness. Readiness still requires a
post-commit, non-busy PASSIVE result with positive WAL and checkpointed page counts.

Hicks is assigned as the new independent .NET/SQLite concurrency test specialist. Lambert, Parker,
Bishop, Burke, Hudson, and Vasquez are locked out from both revision and advice. Exact gates are in
`.squad/decisions/inbox/dallas-pr127-wal-readiness-ci-triage.md`; fresh Ubuntu and Windows CI remain
mandatory.

---

## 2026-08-26 — PR #127 WAL readiness CI recheck

**Verdict:** **APPROVE** Hicks's test-only WAL-readiness revision for the full-suite and fresh-CI
gate. The unpooled anchor now spans initialization through cancellation and worker join; WAL mode,
autocheckpointing, the zero-page baseline, four ordering gates, and checkpointer attachment are
explicit. The strict state machine treats exact `(-1,-1)` as retryable non-progress before and after
readiness, rejects invalid rows, and permits readiness only for positive post-commit progress.

All 53 tests in `SnapshotExportTests.cs` passed, followed by 100 isolated repetitions of the real
writer/checkpointer stress test, with zero failures or skips under `DOTNET_ROLL_FORWARD=Major`.
Existing export invariants are unchanged and no production file changed. Frost's exporter remains
accepted and frozen; the local gate is cleared for the full suite and fresh Ubuntu/Windows CI.

---

## 2026-09-04 — Helix queue monitor: combined review and roadmap

**Verdict:** **ACCEPT WITH CORRECTIONS** on Ash's requirements analysis and Ripley's backend
audit. Four fixes land now (D1–D4), two items defer (D5b, D6), six are rejected. Recorded in
`.squad/decisions/inbox/dallas-helix-queue-monitor-roadmap.md`.

The reframing that drove every decision: queue monitor did not create a capability gap, it
exposed a **projection gap**. Reflecting over `Microsoft.DotNet.Helix.Client`
`11.0.0-beta.26325.102` showed `JobSummary` — the type `Job.ListAsync` already returns —
carries `QueueId`, `Properties`, `Created`, `Finished`, `InitialWorkItemCount`, and
`FailureReason`. `HelixApiClient.ListJobNamesByBuildAsync` discards all of it via
`.Select(j => j.Name)`. Restoring that projection satisfies Ripley P1, most of Ash US-Q2, the
real need behind US-Q3, and the seed of US-Q7 at **zero additional HTTP cost**. Every accepted
item is a fix; every rejected item is a new feature. That split was not imposed, it fell out.

**Material correction — Ripley P4 is wrong as specified.** Ripley proposed grouping by
`(logical name | PhaseName, QueueId)` and keeping max `System.JobAttempt`, attributing it to
arcade's `GetLatestHelixJobAttempts`. Arcade (`MonitorState.cs:593-601`) actually uses a
**lineage-leaf rule**: superseded iff another job's `PreviousHelixJobName` points at it;
attempt numbers only order, never select. Ripley's key would silently *delete* legitimate
concurrent jobs — arcade's own `LogicalJobName` docs say one AzDO job can submit several Helix
jobs to the same queue. Undercounting is a worse failure than the overcounting it targeted.
Ripley cited the right primitive in §1 and failed to carry it into the recommendation, so the
correction is mechanical and Ripley keeps ownership; no lockout.

**Sequencing principle worth reusing:** annotate before filtering. D5a exposes `Superseded`
as a free additive field and changes no counts; D5b (actually filtering) stays unapproved
until D5a surfaces a real build proving duplicates occur. Changing a user-visible count on an
unproven hypothesis is precisely what Ripley was right to escalate, and the answer was "prove
it with the cheap version first," not "yes" or "no."

**Rejection pattern in Ash's proposal:** four of six opportunities proposed new MCP tools for
capabilities the server already composes. US-Q1 and US-Q6 are fully served by
`azdo_timeline`/`azdo_search_timeline`/`azdo_search_log` — the last already accepts a null
`logId` and scans all ranked steps. US-Q4 is a heuristic wrapper whose `confidence` field we
cannot calibrate, and D1 removes the reason to branch on topology at all. US-Q3's need was
real but met by an additive `Source` field, not a tool. Also caught two factual slips: the
find-files default is 30 not 50, and "<5s for 500+ jobs" conflates submission-level
aggregation (free) with result-level pass/fail (N calls, unachievable). No lockout on Ash —
requirements analysis is supposed to surface candidates review rejects — but reviving US-Q1,
US-Q4, or US-Q6 now requires a concrete failing investigation transcript.

Verified true against local code: the monitor's `failed ({State}).` format can never match
`FailedWorkItemRegex` (`AzdoService.cs:767`); `ParentJobName` collapses to `HelixJobMonitor`
(`:957-959`); build-wide prose errors vanish under `filter="failed"` (`:963` guard);
`CiKnowledgeService.cs:229/787` still describes the fallback as the whole tool, which is wrong
independent of dotnet/sdk behavior — so D4 does not block on a live build.

Compatibility rule I want held: **no behavior may branch on "monitor detected."** D1 helps both
topologies because the same submitter stamps the same properties either way, and D5's leaf rule
is a provable no-op on legacy. If an implementer needs a topology flag, the design went wrong.

No production code or tests written. The reflection probe was deleted and the worktree is clean.

## 2026-09-04: Helix queue-monitor design review and roadmap adjudication (completed)

Completed architectural review of Ash's requirements and Ripley's audit. Verified all claims against local code (HelixApiClient projection bug) and arcade source (lineage-leaf dedup rule). Adjudicated parallel proposals into ranked roadmap: six items approved (D1–D6: four fixes, one enhancement, one doc), two items deferred to later gate (D5b), seven new-tool proposals rejected. Key finding: existing tools compose to same capability; JobSummary metadata restoration is mechanical fix with zero additional HTTP cost.

**Status:** COMPLETED  
**Outcome:** Roadmap verdict ACCEPT WITH CORRECTIONS; D1–D6 ready for implementation planning; D5b gated on D5a evidence

## Ownership Assignment

- **Ripley:** D1, D2, D3, D5a, D5b, D6 (primary owner of queue-monitor fixes)
- **Kane:** D4 (documentation correction)

## 2026-09-04: Pre-work Design Review — queue-monitor compatibility slice 1 (completed)

Ran the read-only Design Review ceremony for the first implementation slice. Brief recorded at
`.squad/decisions/inbox/dallas-queue-monitor-design-review.md`. No production file or test
touched; worktree clean, build 0/0 before and after.

**Scope decided:** D1, D2, D3 (+ new D3b), D4, D5a. D6 pushed to slice 2 (unrelated subsystem,
drags `ProgressOverStatelessHttpTests` in). D5b still gated on D5a evidence.

**Two accepted premises corrected on primary-source evidence** (fetched `dotnet/arcade@main`
verbatim rather than trusting the prior summary):
- D3's "detect by task name `Monitor Helix Jobs`" is **struck**. `helix-job-monitor.yml` names
  both the Job and the Task `Monitor Helix Jobs`, so the existing `Name.Contains("helix")`
  predicate already finds them — and a name gate is precisely the "branch on monitor detected"
  my own compatibility rule #1 forbids. Parsers apply unconditionally.
- D3's "extract the GUID from the console URL" is **demoted to fallback**. `HelixJobInfo.cs:149`
  puts the GUID in `DisplayName` (`"{label} - {queue} ({guid})"`, or bare `{guid}`), and
  `MonitorState.cs:656` can emit the literal `"no console link available"`. DisplayName first.

**Found a second monitor format nobody had named** (`StatusReporter.cs:334-354`): an aggregated
`Failed work item information:` tree, emitted as `LogError`, carrying `// DO NOT CHANGE THIS
LINE - it's matched by Build Analysis`. Added as D3b — same function, same test file, zero new
surface, and it is the format arcade has explicitly pinned. Leaving the most stable signal
unparsed would have been the worse call.

**Reframed what D3 actually fixes.** The GUID is usually already recovered today (the console
URL matches `HelixJobIdRegex`). What is lost is the **work-item → job association**, because
`FailedWorkItemRegex` requires the literal `has failed`. Naming the defect precisely changed
the tests I demanded.

**Rulings worth remembering:**
- D2's open question ("reuse `FailedWorkItems`, or add a field?") — *this review was the review
  the decision deferred to*. Verdict: reusing it is semantically wrong, because clients feed
  that list to `helix_search(jobId, workItem)`; raw AzDO prose there produces a predictable
  misuse. Added bounded `Messages` (≤20 × ≤500 chars, only on empty-GUID rows).
- Added `Strategy` beyond the accepted decision, and justified it as **correctness, not
  convenience**: D1 gives `Result` two vocabularies across the two paths, so having introduced
  the ambiguity we are obliged to ship the discriminator.
- **Declined to narrow D1's `Result`** even though the new `State` field makes it redundant on
  the Helix path. Re-litigating an accepted criterion for tidiness, after Lambert holds it,
  costs more than one redundant field. Guard rail instead: `Result` is never a pass/fail verdict
  there, and `Note` must say so.
- "Running with errors" pinned as `State=="running" && Result=="unknown" && (TaskErrorCount>0 ||
  TaskWarningCount>0)`. Counts deliberately unchanged (rule #4) — the obligation that creates is
  **disclosure in `Note`**, not silence.
- Counts named `TaskErrorCount`/`TaskWarningCount` on purpose: N rows from one task all carry
  the same value, and the prefix is what stops someone summing them.

**Process lesson — the shim removed the only real ordering conflict.** Renaming
`ListJobNamesByBuildAsync` would red the test project the moment R1 landed, forcing either
Ripley into tests or Lambert into a wait. Keeping it as an undecorated delegating shim (not
`[Obsolete]` — that breaks the 0-warning gate that is itself a merge criterion) buys full
three-way parallelism for the price of one mandatory deletion step, R4. I made R4 a merge gate
so the shim cannot quietly become permanent.

**Two new standing compatibility rules** (now #5 and #6): no new *positional* record parameters
on wire types, because `azdo_helix_jobs` generates its output schema from the record and
positional params generate as required; and Newtonsoft stops at `HelixApiClient` — no
`JObject`/`JToken` may cross `IHelixApiClient`.

**Deliberately left broken:** the `filter="all"` wart at `AzdoService.cs:918` (issue-free helix
tasks skipped even under `all`). Real, pre-existing, and fixing it moves `TotalHelixJobs` without
evidence. Recorded in the brief so it is a known wart rather than a future rediscovery.

**Did not convene Ripley/Lambert/Kane as subagents.** Everything was answerable from local source
plus arcade; they would have re-read the same files. Recorded the omission as deliberate.

**Status:** COMPLETED
**Outcome:** Design accepted; R1/L1/K1 may begin. Eight named reject-on-sight conditions recorded
for my own merge review.
