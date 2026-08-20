# Dallas — History (Condensed)

## Executive Summary

**Role:** Decision lead on MCP schema reduction, parameter aliasing, parameter plumbing, and strict-mode architecture.

**Recent Focus (2026-06-24 through 2026-07-20):**
- Refined blanket "flatten all outputSchema" → tiered approach (FLATTEN 10 / KEEP 3 / LEAVE 12)
- Authorized PR #78 (azdo param plumbing: minTime/maxTime/queryOrder, top, outcomes)
- Designed CallToolFilter layer for unknown-param rejection (Issue #81) vs SDK-layer strict mode
- Triaged Issue #81/#82 sequencing (correctness before cleanup)

---

## 2026-07-20: Tiered outputSchema Recommendation (REFINED)

**Challenge:** User questioned blanket "flatten all 20 tools" — which genuinely warrant richer schema?

**Method:** Read result DTOs; assess each tool on: (1) output chained downstream, (2) shape ambiguous, (3) drives decision.

**Recommendation:**
- **FLATTEN 10:** helix_status, azdo_build, helix_parse_uploaded_trx, azdo_search_timeline, helix_batch_status, helix_work_item, helix_search, helix_find_files, helix_files, helix_download (5,541 bytes, 18% savings)
- **KEEP 3:** azdo_timeline (log.id disambiguation), azdo_helix_jobs (HelixJobId bridge), azdo_build_analysis (known/unmatched discrimination) (2,212 bytes retained)
- **LEAVE 12:** Already minimal or degenerate (68-byte LimitedResults, string-only returns, azdo_search_log)
- **Optional enrich:** 2 field descriptions (~90 bytes)

**Net savings:** ~5,450 bytes (18% vs. 28% blanket). Retains extraction-critical guidance on 3 chaining junctions.

**Decision filed:** `.squad/decisions/decisions.md` (merged from inbox 2026-07-20)

---

## 2026-06-24: Param Plumbing & Alias Coercion

### PR #78 — Three AzDO Bugs Fixed
1. **azdo_builds:** minTime/maxTime/queryOrder missing from filter + URL
2. **azdo_test_attachments:** top param not forwarded to REST URL
3. **azdo_test_results:** outcomes hardcoded (no override)

Four Copilot review rounds; 14 new tests added; all 1337 tests pass.

### PR #75 — Numeric Alias Coercion (Gap fix)
**Finding:** Numeric `build_id` values (JSON numbers) fail string parameter binding.
**Fix:** Implement `CoerceToStringElement()` in CallToolFilter. Ripley + Lambert executed.

---

## 2026-06-01: Parameter Alias Layer Decision

**Problem:** Agents passed `build_id`/`buildUrl` instead of canonical `buildIdOrUrl`.

**Layer choice:** CallToolFilter in McpServerOptionsExtensions (pre-binding), not per-tool attributes or method signatures.

**Rationale:** Key-normalization problem, not value problem. MCP SDK 1.3.0 has no tool-level alias surface.

**Pattern:** Flat global alias map (OrdinalIgnoreCase); insertion order significant; combine with binding-error filter.

**Calibration:** Drift telemetry must be built in from day one (Debug-level logging).

---

## Issue #81/#82 Framing (2026-06-24)

**Sequencing heuristic:** User-visible correctness (#81 Stage A) before architectural cleanup (#82).

**Stage A/B coexistence:** Both UnmappedMemberHandling.Disallow (SDK layer) and CallToolFilter "did you mean" (pipeline layer) can coexist; Stage B makes Stage A redundant for our tools. Document the choice in PR, don't silently remove.

**Pre-work scope rule:** When enabling enforcement gate, grep telemetry/issue history for known-tolerated variants before flipping switch. Example: `result` → `resultFilter` alias landed in Stage A PR, not as follow-up.

---

## Previous Work Archive

See `.squad/agents/dallas/history-archive-2026-06-01.md` for:
- Issue #61 (silent MCP failures, exception centralization)
- PR #66 (external contributor review, null-coercion pattern)
- Issue #74 (schema measurement, 28.26 KB baseline, conditional NO)
- MCP 1.4.0 bump safety analysis

---

## Key Decisions Referenced

- **Issue #74:** Conditional NO on active trimming (28.26 KB, <1% session budget). Lever 1 available (~8.9 KB zero-risk).
- **Issue #81/#82:** Stage A (strict unknown-param) correct; Stage A/B can coexist; centralize normalization (cleanup after correctness).
- **PR #78:** Param plumbing shipped; auth + cache remain; 14 new tests added.
- **Lever 1 (Tiered outputSchema):** Ready for go/no-go; ~5,450 bytes savings with extraction guidance preserved.

---

## 2026-07-20: Progressive Disclosure Analysis

### Finding: SDK supports dynamic ToolCollection (v1.4.0)
- `ToolCollection` property on server supports Add/Remove at runtime
- SDK auto-sends `notifications/tools/list_changed` to clients
- Mechanism exists in protocol and SDK — feasible technically

### Finding: No natural trigger in MCP protocol
- No client gesture / context hint flows server-side mid-session
- Server has no visibility into what the model is doing
- Only triggers available: (a) tool A was called → reveal tool B, (b) timer/config

### Tool Bucketing (25 tools, ~29 KB total)
**CORE (always needed, 8 tools, ~12.8 KB):**
azdo_build (1531), azdo_builds (2103), azdo_timeline (2099), azdo_helix_jobs (1425), azdo_search_log (2027), helix_status (1771), azdo_build_analysis (est ~1200), helix_ci_guide (479)

**WORKFLOW-GATED (need build/job in hand, 11 tools, ~12.5 KB):**
azdo_log, azdo_changes, azdo_test_runs, azdo_test_results, azdo_search_timeline, azdo_artifacts, helix_logs, helix_files, helix_work_item, helix_search, helix_find_files

**NICHE (narrow scenarios, 6 tools, ~4.6 KB):**
helix_parse_uploaded_trx (1703, self-identifies "Niche"), helix_batch_status, helix_download, azdo_test_attachments, helix_auth_status (330), azdo_auth_status (330)

### Mechanism Feasibility
1. **Dynamic list + list_changed:** FEASIBLE in SDK 1.4.0. But trigger problem is real — after azdo_builds/helix_status called, reveal drill-down tools. Complexity: moderate (state machine per session).
2. **Meta/gateway tool:** FEASIBLE but anti-pattern for models. Worse tool-use accuracy. Not recommended.
3. **Resources vs Tools:** CiKnowledgeResource ALREADY exists alongside helix_ci_guide tool. Resources are NOT in tools/list — they're in resources/list. Moving guide to resource-only saves 479 bytes but loses tool-call discoverability. Marginal.
4. **Config-based profiles:** FEASIBLE, simplest. Operator picks "minimal" (8 core) or "full" (25). Static at startup. No runtime complexity.

### Verdict
Progressive disclosure is NOT worth pursuing as a primary lever here.
- Maximum reclaim from hiding NICHE bucket: ~4.6 KB (16% of total)
- Maximum reclaim from hiding WORKFLOW-GATED: ~12.5 KB (43%)
- But WORKFLOW-GATED tools are what models need to SEE to plan multi-step investigations — hiding them degrades planning quality
- The trigger problem means you'd reveal tools AFTER the model already decided it needed them (chicken-and-egg)
- flatten-10/keep-3 outputSchema lever saves ~5.5 KB with ZERO ergonomic cost and no runtime complexity
- Combined: flatten + description trim could reach ~8-9 KB savings without progressive disclosure

### Recommendation
1. Ship flatten-10/keep-3 (outputSchema tiering) — already designed, zero-risk
2. If further cuts needed: config-based "minimal" profile (option 4) for operators who only need AzDO OR only Helix
3. Do NOT pursue runtime dynamic disclosure — complexity/trigger problem outweighs marginal byte gain
4. Progressive disclosure and outputSchema flatten compose (orthogonal) but overlap in motivation — flatten is strictly better ROI

---

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
