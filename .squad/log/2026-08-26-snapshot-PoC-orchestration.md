# Orchestration Log: Snapshot PoC Implementation & Review

**Session:** 2026-08-26  
**Branch:** lewing-potential-chainsaw  
**Scope:** `HLX_EVAL_SNAPSHOT` snapshot-based eval mode PoC  
**Final Status:** ✅ **APPROVED**

---

## Agent Runs & Review Gates

### Phase 1: Research & Analysis

| Agent | Task | Date | Duration | Outcome |
|-------|------|------|----------|---------|
| Ash | Research Vally stimulus architecture & ci-evidence-reader operations | 2026-08-26 | — | ✅ Documented durable context in `.squad/decisions/inbox/ash-vally-research.md` |
| Ash | Audit ci-evidence-reader operation coverage in existing cache | 2026-08-26 | — | ✅ Documented cache table inventory in `.squad/decisions/inbox/ash-ci-evidence-history.md` |

### Phase 2: Design Exploration

| Agent | Task | Date | Duration | Outcome |
|-------|------|------|----------|---------|
| Dallas | Initial HLX eval workflow design (Phase 1 attempt with stateful mode) | 2026-08-26 | — | 🔄 **SUPERSEDED** by PoC design; Phase 1 rejected — stateless is correct mode |
| Dallas | Design reconciliation & feasibility review | 2026-08-26 | — | 🔄 **SUPERSEDED** by PoC; reconciled approach but later replaced by snapshot-based PoC |
| Ripley | Analyze HLX eval mode mechanics (TTL, network, WAL) | 2026-08-26 | — | ✅ Documented analysis in `.squad/decisions/inbox/ripley-hlx-eval-mechanics.md` |
| Ripley | Snapshot-based eval mode proposal (design analysis) | 2026-08-26 | — | ✅ Proposal written to `.squad/decisions/inbox/ripley-snapshot-eval.md` |
| Dallas | **APPROVED** snapshot-based PoC design (final canonical decision) | 2026-08-26 | — | ✅ **DESIGN APPROVED** — Document: `.squad/decisions/inbox/dallas-snapshot-poc-design.md` |

### Phase 3: Implementation

| Agent | Task | Date | Duration | Outcome |
|-------|------|------|----------|---------|
| Ripley | Implement production code: `CacheOptions.EvalMode`, offline stubs, DI wiring (CLI + MCP) | 2026-08-26 | — | ✅ Implementation notes: `.squad/decisions/inbox/ripley-snapshot-eval-impl.md` |
| **REVIEW GATE 1** | First review of Ripley's implementation | 2026-08-26 | — | ❌ **REJECTED** — WAL/SHM deletion, schema mutation, missing HTTP handler blocking |
| Bishop | Escalation: Fix critical findings (WAL preservation, read-only conn, HTTP blocking) | 2026-08-26 | — | ✅ **FIXES ACCEPTED** — Document: `.squad/decisions/inbox/bishop-snapshot-integrity.md` |
| Parker | Seal HTTP blocking path & add composition coverage | 2026-08-26 | — | ✅ Production code changes accepted (per Dallas test retro) |
| **REVIEW GATE 2** | Second review post-Bishop/Parker fixes | 2026-08-26 | — | ✅ **APPROVED** with one missed DI path (found by final reviewer) |

### Phase 4: Testing & Validation

| Agent | Task | Date | Duration | Outcome |
|-------|------|------|----------|---------|
| Lambert | Author tests (44 new tests across 3 files) | 2026-08-26 | — | ⚠️ Initial run: 4 test defects found (not production bugs) |
| Dallas | Test failure retrospective & corrective assignment | 2026-08-26 | — | 📋 Documented in `.squad/decisions/inbox/dallas-snapshot-test-retro.md` |
| Brett | Correct 4 test defects (DI fixture seeding, WAL test assertion) | 2026-08-26 | — | ✅ All 4 tests fixed |
| **FINAL VALIDATION** | Full suite run with `DOTNET_ROLL_FORWARD=Major` | 2026-08-26 | — | ✅ **1622 passed, 2 skipped, 0 failed** |

### Phase 5: Final Review & Approval

| Agent | Task | Date | Duration | Outcome |
|-------|------|------|----------|---------|
| Lambert | Review + final approval (all acceptance criteria verified) | 2026-08-26 | — | ✅ **APPROVE** — Document: `.squad/decisions/inbox/lambert-snapshot-eval-review.md` |
| Final Reviewer | Approve production code + correct remaining tests | 2026-08-26 | — | ✅ **APPROVED** — One missed DI path corrected |

---

## Key Decisions & Outcomes

### Canonical Design Decision

**File:** `.squad/decisions/inbox/dallas-snapshot-poc-design.md`  
**Status:** ✅ **APPROVED**  
**Key Features:**
- Environment variable activation only: `HLX_EVAL_SNAPSHOT=/path/to/snapshot`
- Hard network blocking via `OfflineAzdoApiClient` & `OfflineHelixApiClient` stubs
- Read-only SQLite connections (`Mode=ReadOnly`)
- **Preserved WAL files** (no deletion; read-only connections follow WAL correctly)
- TTL bypass in `GetMetadataAsync` & `IsJobCompletedAsync`
- Eval mode schema validation (throw on mismatch, no DDL)
- Shared DI pattern (env var read in both `Program.cs` files independently)
- **No export command in POC** (manual `PRAGMA wal_checkpoint(FULL)` + `cp` sufficient)

### Critical Findings & Resolutions

1. **WAL/SHM Deletion** (Bishop Finding 1: CRITICAL)
   - ❌ Initial implementation deleted WAL/SHM files
   - ✅ **FIXED**: Preserve WAL/SHM; read-only connections follow them correctly
   - **Rationale**: WAL files are integral to DB state; deletion would lose committed transactions

2. **Schema Mutation in Eval Mode** (Bishop Finding 2: HIGH)
   - ❌ Initial implementation ran `PRAGMA journal_mode=WAL` in eval mode
   - ✅ **FIXED**: Split schema init into `InitializeSchema()` (normal) & `ValidateEvalSchema()` (eval, read-only)
   - **Rationale**: Eval mode must not mutate snapshot under any circumstances

3. **HTTP Downloads Not Blocked** (Bishop Finding 4: HIGH)
   - ❌ `HelixService` received real HTTP client in eval mode
   - ✅ **FIXED**: Introduced `EvalModeBlockingHandler` to throw on any HTTP send
   - **Rationale**: Network must be hard-blocked for determinism

4. **Test Defects** (Dallas retro, corrected by Brett)
   - Fixture seeding: DI tests created empty snapshot dir without `cache.db`
   - WAL preservation test: assertion on non-standard SQLite WAL behavior removed
   - **Both corrected** — production code is sound

---

## Test Results

### Final Suite Results
```
Total: 1,624 tests
Passed: 1,622 ✅
Skipped: 2 (pre-existing, unrelated)
Failed: 0 ✅
```

**Test Coverage:**
- 44 new eval-mode tests (Lambert)
- 4 corrected post-test-retro (Brett)
- All existing tests remain green

---

## Files Changed

### Production Code (Ripley, Bishop, Parker)
- `src/HelixTool.Core/Cache/CacheOptions.cs` — `EvalMode` property
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs` — TTL bypass, read-only schema validation, WAL preservation
- `src/HelixTool.Core/AzDO/OfflineAzdoApiClient.cs` (new) — offline stub
- `src/HelixTool.Core/Helix/OfflineHelixApiClient.cs` (new) — offline stub
- `src/HelixTool.Core/EvalModeBlockingHandler.cs` (new) — HTTP blocking
- `src/HelixTool/Program.cs` — eval mode DI wiring
- `src/HelixTool.Mcp/Program.cs` — eval mode DI wiring

### Test Code (Lambert, Brett)
- `src/HelixTool.Tests/CacheOptionsTests.cs` — 5 eval-mode tests
- `src/HelixTool.Tests/SqliteCacheStoreTests.cs` — 17 eval-mode tests
- `src/HelixTool.Tests/SnapshotEvalModeTests.cs` (new) — 22 integration tests

---

## Approval Chain

1. **Dallas (Design Lead)** — Approved snapshot PoC design over rejected hybrid/stateful approaches
2. **Bishop (Escalation)** — Identified critical WAL/schema/HTTP issues; fixes approved
3. **Parker (Specialist)** — Sealed HTTP blocking and composition coverage
4. **Brett (QA)** — Corrected 4 test defects
5. **Final Reviewer** — Approved production code + remaining tests

**Canonical status:** ✅ **APPROVED FOR PRODUCTION**

---

## Non-Goals (Explicitly Out of Scope)

- `hlx cache export` command (manual export sufficient for POC)
- `hlx cache import` command (follow-up)
- Authenticated/private snapshot support (public/unauthenticated only for POC)
- `ListJobNamesByBuildAsync` caching (separate issue)
- Any changes to MCP tool schemas or CLI command signatures
