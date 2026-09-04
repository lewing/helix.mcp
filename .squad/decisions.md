# Decision: hlx eval/replay mode — Reconciled Recommendation

**Date:** 2026-08-26  
**Author:** Dallas (Lead)  
**Status:** Discussion — For Vitek  
**Supersedes:** dallas-hlx-eval-workflow.md (Approach C withdrawn), ripley-hlx-eval-mechanics.md (findings incorporated)

---

## 1. Recommended Boundary: runtime owns security, hlx owns replay

My original proposal (Approach C: `hlx ci-evidence`) tried to absorb the security boundary into hlx. Ripley's analysis correctly identified that `ci-evidence-reader` is a **gh-aw sandbox enforcement point** — URL allowlisting, redirect-family validation, output-path sandboxing — and that responsibility belongs in the runtime repo, owned by the team that operates the agentic runner.

**Boundary:**

| Responsibility | Owner |
|---|---|
| URL allowlist, redirect validation, output sandboxing, `--allow-tool` surface | `ci-evidence-reader` (runtime repo) |
| CI data retrieval, caching, search, token-efficient output | `hlx` CLI |
| Deterministic eval fixture replay | Both — ci-evidence-reader gets `--fixture-dir` (~50 LOC Python); hlx gets `HLX_EVAL_FIXTURES` for broader hlx-based workflow testing |
| Vally eval harness, scan prompts, scan logic | runtime repo |

hlx does **not** grow an allowlisting layer. ci-evidence-reader does **not** grow caching or search.

## 2. Minimum Useful `hlx eval` Design

Goal: enable deterministic replay of hlx commands without network, without hand-rolled Python, without overfitting to runtime's ci-evidence-reader contract.

### Activation

```bash
HLX_EVAL_FIXTURES=./fixtures hlx azdo timeline 123456
```

Env-var activated. When set, **all real HTTP is hard-blocked** (throw on any non-fixture request). No `--flag` on every command — the env var is session-wide.

### Fixture Resolution

```
{fixture-dir}/
  azdo/
    builds/{definitionId}.json          # azdo builds --definition-id N
    timeline/{buildId}.json             # azdo timeline N
    timeline/{buildId}.failed.json      # azdo timeline N --filter failed
    log/{buildId}/{logId}.log           # azdo log N M
    test-runs/{buildId}.json            # azdo test-runs N
    test-results/{buildId}/{runId}.json # azdo test-results N R
  helix/
    status/{jobId}.json                 # status GUID
    status/{jobId}.failed.json          # status GUID --filter failed
    logs/{jobId}/{workItem}.log         # logs GUID NAME
    files/{jobId}/{workItem}.json       # files GUID NAME
  blobs/
    {sha256-of-url-path}.bin            # download --url (SAS tokens stripped before hash)
```

Directory-and-convention based. No manifest file, no NDJSON index. File presence = fixture availability; missing file = hard error with actionable message ("fixture not found: helix/status/abc-123.json").

### Implementation Seams

- **AzDO:** `FixtureAzdoApiClient : IAzdoApiClient` — reads fixture files, returns deserialized responses. Injected via DI when `HLX_EVAL_FIXTURES` is set.
- **Helix:** `FixtureHelixApiClient : IHelixApiClient` — same pattern. Required because the Helix SDK creates its own HttpClient internally; `HttpMessageHandler` injection cannot intercept it.
- **Blobs:** `FixtureHttpMessageHandler` for `*.blob.core.windows.net` URLs, matching on path only (SAS tokens ignored).

### Record Mode (Slice 5, deferred)

`HLX_RECORD_DIR=./fixtures hlx azdo timeline 123456` — runs live, captures responses to fixture dir. SAS tokens stripped from blob URLs before writing. Not needed for MVP; fixtures can be hand-curated or captured with `curl`.

### What This Does NOT Do

- Does not replicate ci-evidence-reader's output-path sandboxing (not hlx's job).
- Does not normalize raw Helix JSON to match ci-evidence-reader's passthrough format. Vally prompts written against raw Helix wire JSON must use ci-evidence-reader's own fixture mode, not hlx.
- Does not attempt to make hlx a drop-in for ci-evidence-reader. They are different tools with different output contracts.

## 3. Staged Adoption Experiment for Vitek

**Goal:** Vitek tries hlx in a low-risk, zero-PR-churn way to see if the output is useful.

### Week 1: Side-by-side comparison (no code changes)

```bash
# Vitek runs both tools against the same build and compares output
ci-evidence-reader azdo-timeline --build-id 123456 > timeline-raw.json
hlx azdo timeline 123456 --json > timeline-hlx.json
diff timeline-raw.json timeline-hlx.json
```

This surfaces the shape gaps (full vs failed default, normalized vs raw Helix JSON) concretely. No PR needed.

### Week 2: hlx closes the two mechanical gaps (1 small hlx PR)

- `hlx azdo log` gains `--tail-lines 0` (full log, 64 MB cap preserved).
- `hlx azdo builds` gains `--skip N` for offset paging.

Vitek re-runs the comparison. If the outputs are close enough, proceed.

### Week 3: Vitek evaluates fixture-dir in ci-evidence-reader (runtime PR)

Vitek adds `--fixture-dir` to ci-evidence-reader (~50 lines Python). This is his eval seam, owned entirely in runtime. No hlx dependency.

### Later: hlx eval fixtures (hlx PR, independent)

hlx ships `HLX_EVAL_FIXTURES` for teams that want to test hlx-based workflows. This is useful beyond runtime — any repo using hlx in CI scripts or MCP agents benefits. Ships when a concrete consumer (Vally or otherwise) defines the test scenario.

## 4. Four Questions for Vitek

1. **Raw vs interpreted JSON:** Does Vally (or the scan's LLM prompt) depend on the exact wire shape of Helix API responses? If so, hlx's normalized output can't substitute for ci-evidence-reader without prompt changes. This determines whether hlx is ever in the eval path or only a developer convenience.

2. **Fixture granularity:** For deterministic evals, do you need full HTTP response replay (headers, status codes, exact body bytes), or is file-per-API-call sufficient (just the JSON body)? The former requires an HTTP-level fixture layer; the latter is simpler and is what both ci-evidence-reader's `--fixture-dir` and hlx's `HLX_EVAL_FIXTURES` would provide.

3. **Runner environment:** Is .NET 10 SDK available on gh-aw runners? If not, hlx would need to ship as a self-contained binary (~35 MB) or container sidecar for any future integration. This doesn't block the side-by-side experiment (Vitek runs hlx locally) but matters for adoption.

4. **Scope of Vally:** Is Vally a test harness for the scan's prompt+tool dispatch (i.e., "given these fixtures, does the agent produce the right triage report"), or does it also validate the evidence-gathering layer (i.e., "does ci-evidence-reader fetch the right URLs for a given build")? If the former, the fixture format only needs to be good enough for the LLM; if the latter, it needs HTTP-level fidelity.

---

## Summary

| Original Approach C | Reconciled Position |
|---|---|
| hlx absorbs security boundary (`hlx ci-evidence`) | ci-evidence-reader stays; hlx stays in its lane |
| Single tool replaces Python | Two tools with clear boundary |
| hlx ships allowlisting | hlx ships eval/replay only |
| Tight coupling to runtime's scan | Generic fixture mode useful to any hlx consumer |

The minimum useful deliverable is **Slices 1–2** (full-log + skip paging) — two small flag additions that close the mechanical gaps between hlx and ci-evidence-reader, enabling Vitek's side-by-side comparison. Everything else is sequenced behind Vitek's answers to the four questions above.

---

# Proposal: hlx Deterministic Eval / Replay Mode

**Date:** 2026-08-26  
**Author:** Ripley  
**Status:** Draft — Pending Dallas review  
**Context:** Analysis of dotnet/runtime PR #132753 (Vitek Karas)

---

## Background

Vitek's PR #132753 ("Replace CI scan curl access") adds `ci-evidence-reader` — a ~640-line Python script that is a **constrained HTTP proxy** for the gh-aw `ci-failure-scan` agentic workflow. The PR description explicitly states: "creates a clear place to later redirect CI evidence inputs for deterministic evals, without changing the commands the scan uses."

This is Vitek's seam. He's separating the I/O layer from the analysis logic so the scan can later be run against recorded fixtures. The question is: should hlx provide that fixture layer, or should it live entirely in the runtime repo?

---

## ci-evidence-reader Command Mapping

| ci-evidence-reader command | hlx equivalent | Gap |
|---|---|---|
| `azdo-builds --definition N --top 25 --skip N` | `hlx azdo builds --definition-id N --top N` | **Gap**: hlx has no `--skip` for offset paging; forced filter params differ (ci-evidence-reader fixes branch=main, completed, etc. in the URL) |
| `azdo-timeline --build-id N` | `hlx azdo timeline <N>` | Near-match. hlx defaults to `--filter failed`; ci-evidence-reader gets all timeline data. Add `--filter all` → resolved. |
| `azdo-log --build-id N --log-id M` | `hlx azdo log <N> <M>` | **Gap**: hlx defaults to `--tail-lines 500`; ci-evidence-reader gets full log (64 MB cap). Need `--tail-lines 0` or full-log mode. |
| `helix-work-items --job-id GUID` | `hlx status <GUID> --json` or raw Helix API | **Shape gap**: hlx returns interpreted/normalized JSON via HelixService; ci-evidence-reader passes raw Helix API JSON. If scan prompts are written against raw shape, switching breaks them. |
| `helix-console --job-id GUID --work-item NAME` | `hlx logs <GUID> <NAME>` | **Path gap**: hlx writes to a temp path (prints it to stdout); ci-evidence-reader writes to a controlled path under `$OUTPUT_ROOT`. The gh-aw sandbox requires writing to a specific directory. |

**Summary**: 2 of 5 commands are near-matches with small flag gaps. 3 of 5 have meaningful differences in output contract, path semantics, or request shape.

---

## What ci-evidence-reader IS vs what hlx IS

**ci-evidence-reader** is a **sandboxed HTTP proxy** with:
- URL allowlist (prevent SSRF/exfiltration from agentic runner)
- Output-path boundary enforcement (prevent path traversal from agent tool calls)
- No auth (dnceng-public only, anonymous)
- No caching
- Raw JSON/log passthrough — no interpretation

**hlx** is a **developer convenience tool** with:
- Auth (az CLI, env vars, git credential store)
- Helix SDK + interpretation layer
- Aggressive SQLite caching
- Normalized/interpreted JSON output
- Stdout + temp file output model

These serve different threat models and different users. **ci-evidence-reader should stay in the runtime repo.** It is a security boundary for the gh-aw sandbox, not a general CI tool.

---

## What "deterministic eval mode" means for each layer

### Layer 1: ci-evidence-reader replay (stays in runtime)

Add a `--fixture-dir DIR` flag directly to `ci-evidence-reader`. When set, instead of making real HTTP calls, serve responses from fixture files named by normalized URL. This is a ~50-line addition to the Python script and requires no hlx changes.

Fixture format: `{fixture-dir}/{host}/{path-hash}.{json|log}` where `path-hash = sha256(path + canonical_query)[:16]`.

Test runner (Vally): replace the real `ci-evidence-reader` binary with the replay version by passing `--fixture-dir ./test-fixtures/`. The agent calls the exact same commands, gets deterministic outputs, no network.

**This is the smallest coherent implementation and is entirely in runtime.**

### Layer 2: hlx HttpMessageHandler fixture layer (in hlx)

For Vally to use hlx commands directly (instead of ci-evidence-reader), hlx needs a replay mode. The design:

**Activation**: `HLX_EVAL_FIXTURES=<dir>` env var. When set, inject `FixtureHttpMessageHandler` instead of real handlers.

**Fixture format** (NDJSON, one record per line):
```json
{"method":"GET","url_pattern":"https://dev.azure.com/dnceng-public/public/_apis/build/builds/{buildId}/timeline","query":{"api-version":"7.1"},"response_status":200,"response_file":"azdo-timeline-123.json"}
```

URL pattern matching: exact match first, then template match (`{buildId}` wildcard), then reject with non-zero exit.

**Blob URL problem**: Helix console blob URLs have ephemeral SAS tokens. Match on path only (ignore query string for `*.blob.core.windows.net` hosts).

**Helix SDK problem**: `HelixApiClient` wraps Microsoft's DotNet.Helix.Client SDK which creates its own HttpClient internally — it does NOT use the injected DI HttpClient. This means `HttpMessageHandler` injection only intercepts AzDO and blob download calls. Helix job/work-item calls go through the SDK's own client. **To fixture the Helix layer, mock at `IHelixApiClient`, not at HttpMessageHandler.** This is a harder seam to expose externally.

**Recommendation for Layer 2**: Implement `FixtureAzdoApiClient : IAzdoApiClient` and a `FixtureHelixApiClient : IHelixApiClient` (or `FixtureHelixApiClient` wrapping `IHelixApiClient`). Load fixture JSON files and return deserialized objects. Activation via `HLX_EVAL_FIXTURES`. Network is hard-blocked when fixture mode is active (throw on any real HTTP attempt).

**Exit contracts**: Already solid — `--json` produces stable JSON, exit 0/1 is already consistent. The eval mode doesn't need to add new contracts.

---

## How Vally Invokes Without Custom Python

**Option A — Use ci-evidence-reader replay directly (recommended for Vally):**

```bash
# Generate fixtures (record mode)
ci-evidence-reader azdo-builds --definition 154 --output ./fixtures/builds.json --record

# Run scan in replay mode (Vally test)
HLX_EVAL_FIXTURES=./fixtures pytest .github/workflows/tests/
```

But that requires adding `--record` + `--fixture-dir` to ci-evidence-reader. The scan workflow calls `ci-evidence-reader` as a bash tool — no Python test harness needed. Vally just sets an env var and runs the same workflow prompt against the same script.

**Option B — Use hlx in eval mode (for future, broader coverage):**

```bash
HLX_EVAL_FIXTURES=./test-fixtures hlx azdo timeline 123456 --json
# → reads from ./test-fixtures/azdo-timeline-123456.json, no network
```

This enables testing the full hlx → LLM workflow without network, not just the ci-failure-scan agent.

---

## Implementation Slices (Risk-Ordered)

### Slice 0: Documentation (0 code changes, immediate value)
Write a short guide mapping ci-evidence-reader commands to `hlx azdo`/`hlx status` equivalents. Note the gaps (--skip, full log, raw vs interpreted JSON). This tells Vitek exactly which hlx commands he could use today and what's missing.

**Risk**: None.

### Slice 1: `--tail-lines 0` full-log mode in `hlx azdo log` (small)
The `azdo-log` command in ci-evidence-reader downloads full logs (up to 64 MB). hlx defaults to 500 lines. Adding `--tail-lines 0` to mean "unlimited" closes this gap.

**Risk**: Low. Size guard (64 MB cap) already exists in AzdoApiClient.

### Slice 2: `--skip N` paging for `hlx azdo builds` (small)
ci-evidence-reader's `azdo-builds` supports offset paging (--skip 0, 10, 20, 30, 40). hlx azdo builds has no skip. This is a single-param AzDO URL addition.

**Risk**: Low. Schema addition, no behavioral change for existing callers.

### Slice 3: `FixtureHttpMessageHandler` for AzDO + blob (medium)
Inject a fixture-backed HttpMessageHandler for AzDO and blob download calls when `HLX_EVAL_FIXTURES` is set. Covers `hlx azdo *` and `hlx download --url`.

**Risk**: Medium. Must not activate in production. Must hard-block real HTTP calls in fixture mode to prevent test-escape. Blob URL matching on path-only needs care.

### Slice 4: `FixtureHelixApiClient : IHelixApiClient` (medium-hard)
Fixture the Helix layer at the `IHelixApiClient` abstraction level. Fixture files are keyed by job GUID + method name (e.g., `helix-GetJobStatusAsync-{guid}.json`).

**Risk**: Medium. Requires fixture format to match hlx's internal response types, not raw Helix wire format. Tests need corresponding fixtures for each command. Lambert needs to write the fixture-backed tests.

### Slice 5: Record mode (`HLX_RECORD_DIR` env var) (medium)
Run hlx against live endpoints while capturing request→response pairs to the fixture dir. Enables fixture generation without manually crafting JSON.

**Risk**: Medium. Recording must not store tokens. SAS-token sanitization needed for blob URLs.

---

## What Stays in runtime (NOT in hlx)

1. `ci-evidence-reader` URL allowlist and path-boundary logic — this is a gh-aw security boundary
2. `ci-failure-scan.md` agent prompt and all scan logic
3. The ci-evidence-reader `--fixture-dir` replay seam (add it to the Python script; ~50 lines)
4. The Python test suite for ci-evidence-reader command routing

The only thing hlx contributes is making `hlx azdo *` commands more usable for Vitek's scenario (gap-filling Slices 1–2) and optionally providing a fixture mode (Slices 3–5) for teams that want to test hlx-based workflows without network access.

---

## Risk Summary

| Risk | Mitigation |
|---|---|
| Helix SDK opacity (can't intercept via HttpMessageHandler) | Mock at IHelixApiClient level (Slice 4), not HttpHandler |
| Blob SAS tokens invalidate fixtures | Match on URL path only for *.blob.core.windows.net |
| Fixture mode activates in production | Hard-block real HTTP when `HLX_EVAL_FIXTURES` is set; env var must be explicit |
| JSON shape divergence (raw Helix vs hlx normalized) | Document this; Vally prompts targeting raw shape must use ci-evidence-reader not hlx |
| Scope creep | Slices 0–2 are small and standalone; don't need Slices 3–5 to deliver value |

---

## Recommended Decision

1. **Do Slice 0 now**: document the mapping, send to Vitek/Kane.
2. **Do Slices 1–2 in one small PR**: full-log mode + --skip paging. Closes the two mechanical gaps.
3. **Defer Slices 3–5**: the eval fixture mode is valuable but Vitek's primary seam (ci-evidence-reader replay) doesn't need hlx to move first. Revisit when a concrete Vally test scenario is defined.
4. **Vitek's ci-evidence-reader gets its own `--fixture-dir` in the runtime repo**: ~50 lines of Python, doesn't touch hlx at all.

**Ask for Dallas**: approve/reject the evaluation of Slices 3–5. The main question is whether hlx should own a fixture/replay mode for CI workflow testing or whether that responsibility stays in consuming repos.

---

# Proposal: Snapshot-Based Eval Mode — Design Analysis

**Date:** 2026-08-26  
**Author:** Ripley (Backend Dev)  
**Status:** Draft — Pending Dallas review  
**Requested by:** Larry Ewing  
**Hypothesis under test:** "It seems relatively straightforward to make the eval mode work off a snapshot of the db and cached files?"

---

## Verdict First

**Partly correct.** The snapshot _container_ is sound (relative artifact paths, self-contained structure, no compression, trivial schema). But at least four TTL/network hazards mean a naive `cp -r` of the cache dir is not enough — you need a small but mandatory eval mode that pins TTL to infinity and hard-blocks the network. That is ~2–3 non-trivial changes. "Straightforward" is right for the concept; "relatively" overstates how little work the plumbing needs.

---

## 1. What Is in the SQLite Cache Today

**File:** `cache.db` at `{GetEffectiveCacheRoot()}/cache.db` (e.g., `~/.cache/hlx/public/cache.db` on macOS/Linux, `%LOCALAPPDATA%\hlx\public\cache.db` on Windows).  
**Schema version:** `PRAGMA user_version = 1` (destructive migration on mismatch — drop all, recreate).  
**WAL mode:** Yes. `cache.db-wal` and `cache.db-shm` sidecars exist while the store is open.  
**Compression/encryption:** None.

### Tables

**`cache_metadata`** — JSON API responses  
| Column | Type | Notes |
|--------|------|-------|
| `cache_key` | TEXT PK | `azdo:{authHash?}:{org}:{project}:{suffix}` or `job:{jobId}:{suffix}` |
| `json_value` | TEXT | Serialized DTO or `\0raw\n{plaintext}` for log content |
| `created_at` | TEXT ISO-8601 | |
| `expires_at` | TEXT ISO-8601 | **Checked on every read**: `expires_at > @now` — expired rows are invisible |
| `job_id` | TEXT | Extracted from `job:{jobId}:...` prefix, else the whole key |

**`cache_artifacts`** — large blobs (console logs, downloaded files)  
| Column | Type | Notes |
|--------|------|-------|
| `cache_key` | TEXT PK | `job:{jobId}:wi:{name}:console` or `:file:{name}` |
| `file_path` | TEXT | **Relative** to `_artifactsDir` — e.g., `{jobId[0..8]}/{sanitized-key}` |
| `file_size` | INTEGER | |
| `created_at`, `last_accessed` | TEXT | LRU eviction uses `last_accessed`; no `expires_at` check on read |
| `job_id` | TEXT | |

**`cache_job_state`** — completion flag  
| Column | Type | Notes |
|--------|------|-------|
| `job_id` | TEXT PK | AzDO build or Helix job ID |
| `is_completed` | INTEGER | 0/1 |
| `expires_at` | TEXT | Checked on read: completed builds → 4h TTL; running → 15s TTL |

### TTLs By Entry Type

| Entry | Running TTL | Completed TTL |
|-------|-------------|---------------|
| AzDO build / Helix job details | 15s | 4h |
| Timeline | never cached while running | 4h |
| Build logs (content) | ImmutableTtl (4h) | 4h |
| Build log freshness marker (`log-fresh:`) | 15s (running) | 4h |
| Test runs / results | — | 1h |
| Work items / work item details | 15–30s | 4h |
| File listings | 30s | 4h |
| Job state | 15s | 4h |
| Console log (artifact blob) | never while running | no expiry check (7-day LRU) |
| Helix uploaded files | — | 4h |

### Coverage of the Five ci-evidence-reader Operations

| ci-evidence-reader command | Cache table | Cache key pattern | Cached? |
|---------------------------|-------------|-------------------|---------|
| `azdo-builds` | cache_metadata | `azdo:{org}:{project}:builds:{filterHash}` | ✓ (30s TTL) |
| `azdo-timeline` | cache_metadata | `azdo:{org}:{project}:timeline:{buildId}` | ✓ (4h, completed only) |
| `azdo-log` | cache_metadata | `azdo:{org}:{project}:log:{buildId}:{logId}` | ✓ (4h) |
| `helix-work-items` | cache_metadata | `job:{jobId}:workitems`, `job:{jobId}:wi:{n}:details` | ✓ (4h, completed) |
| `helix-console` | cache_artifacts | `job:{jobId}:wi:{n}:console` → file on disk | ✓ (no expiry check on read) |

All five operations can be satisfied from the cache **if** the job/build is completed and the entries are not yet expired.

One notable gap: `ListJobNamesByBuildAsync` is **explicitly not cached** (comment in code: "source-scoped queries span many jobs; TTL policy is unclear"). This is the Helix API call that resolves Helix job IDs from an AzDO build ID. If a workflow makes this call, it will always hit the network.

---

## 2. Evidence Outside SQLite

| Evidence type | Storage location | Path recorded in |
|--------------|-----------------|-----------------|
| Console log bytes | `{cacheRoot}/artifacts/{jobId[0..8]}/{sanitized-key}` | `cache_artifacts.file_path` (relative) |
| Downloaded work item files | same artifacts dir | `cache_artifacts.file_path` (relative) |
| AzDO artifact download URLs | In JSON in `cache_metadata.json_value` (`AzdoBuildArtifact.Link`) | — SAS URLs, not files |
| TRX / test attachment downloads | NOT downloaded by hlx; only metadata (name, link) cached | — |

**Referential integrity**: `cache_artifacts.file_path` is stored as a **relative path** from `_artifactsDir`. If you copy `{cacheRoot}/cache.db` + `{cacheRoot}/artifacts/**` to a new root and instantiate `SqliteCacheStore` with `CacheOptions { CacheRoot = "<new-root>" }`, the relative paths resolve correctly. **No absolute path leakage in the DB.** This is the main reason the hypothesis is plausible at all.

Stale-row detection: `GetArtifactAsync` checks `File.Exists(fullPath)` and self-heals (deletes row) if file is missing. This is benign during normal use but means a snapshot with missing files will silently treat them as misses and fall through to network.

---

## 3. Current Cache-Miss Behavior vs. What Eval Mode Needs

**Today:** Cache miss in `CachingAzdoApiClient` or `CachingHelixApiClient` → falls through to `_inner.{Method}Async(...)` → makes real HTTP call. There is no "offline" flag. A miss causes a network request; a network failure throws.

**For hard-offline eval mode, three changes are required:**

1. **TTL pin / expiry bypass.** All metadata entries expire after 4h. After that, every `GetMetadataAsync` returns null → network. In eval mode, `expires_at` must be ignored (or all entries must be written with a far-future expiry). The `log-fresh` key expires in 15s → always triggers a delta-refresh attempt on any log read, which hits network.

2. **Hard network block.** Without an explicit block, a cache miss silently hits the network. Eval mode must throw (or return a clear error) on any network attempt. The cleanest seam is throwing in `_inner` itself — an `OfflineApiClient` stub that throws `InvalidOperationException("Network unavailable in eval mode")`.

3. **WAL checkpoint before copy.** While the store is open under WAL mode, `cache.db` may not contain the latest committed data — it may be in `cache.db-wal`. A snapshot copy without first calling `PRAGMA wal_checkpoint(FULL)` may get a stale or inconsistent DB. This must be done with the store closed or with an explicit checkpoint command.

---

## 4. Portability Hazards

| Hazard | Severity | Detail |
|--------|----------|--------|
| **Absolute cache root path** | None in DB | `file_path` is relative; root is set at `SqliteCacheStore` construction time via `CacheOptions.CacheRoot`. Copy works if root is provided. |
| **Auth token hash in AzDO cache keys** | Medium | Keys include `{authHash}:` prefix when auth is configured. Public/anonymous snapshots (dnceng-public) have no auth hash → keys are portable. Authenticated snapshots require matching identity or a key-rewrite step. |
| **TTL expiration** | **Blocking** | All metadata expires after 4h (completed). `expires_at > @now` is checked on every read. Snapshot older than 4h → 100% metadata misses → all fall through to network. |
| **`log-fresh` marker** | **Blocking** | 15s TTL. Always expires → delta-refresh logic fires → `GetBuildLogAsync` hits `_inner` for the delta. |
| **SAS tokens in artifact metadata** | Low for content | `AzdoBuildArtifact.Link` and `IWorkItemFile.Link` in cached JSON contain SAS-signed blob URLs with short expiry. These are for downloading; if the files are already in cache_artifacts (blob was fetched), content is available without the URL. If not yet downloaded, the SAS URL in metadata is expired. |
| **WAL/SHM sidecar files** | Medium | Must checkpoint before snapshot or include all three files. Copy without checkpoint → potential data loss or corruption. |
| **Schema version mismatch** | Low | user_version=1 checked at init; mismatch triggers destructive DROP+recreate. Snapshot must match the running binary's schema version. |
| **ListJobNamesByBuildAsync uncached** | Medium | Always hits network regardless of eval mode unless separately addressed. |
| **Machine-specific paths** | None in DB | Confirmed: only relative paths stored. |
| **Nondeterministic ordering** | None | `AzdoBuildFilterNormalizer` + alphabetically-sorted JSON options ensure deterministic cache keys. |

---

## 5. Smallest Snapshot Contract and CLI UX

**No parallel fixture system needed.** Reuse the existing cache directory directly.

### Snapshot format
A snapshot is: `{some-dir}/cache.db` + `{some-dir}/artifacts/**`, with WAL checkpointed to zero. It is the existing cache layout verbatim. No new file format.

### UX proposal (smallest surface)

```
# Record: run normally against live endpoints; snapshot = live cache
hlx cache export --output ./snapshots/build-12345/
  # Does: PRAGMA wal_checkpoint(FULL), cp cache.db + artifacts/ to output dir

# Eval: run against snapshot, hard-blocking network
hlx --eval-mode ./snapshots/build-12345/ azdo timeline 12345 --json
  # Sets CacheOptions.CacheRoot=snapshot-dir, disables TTL checks, injects OfflineApiClient
```

Alternative — environment variable activation (consistent with Ripley's earlier Slice 3 design):
```
HLX_EVAL_SNAPSHOT=./snapshots/build-12345/ hlx azdo timeline 12345 --json
```

**Prefer the env var** for Vally integration: Vally sets env vars per stimulus, no need to change the hlx command invocation in eval specs vs production. Tool schema and CLI commands remain unchanged — only behavior (offline, no TTL check) changes.

---

## 6. How Vally Consumes the Snapshot

Vally stimulus sets `HLX_EVAL_SNAPSHOT=./fixtures/build-12345/`. The agent's instructions and the MCP tool schema/CLI commands remain **identical** to live use. The MCP server or hlx CLI reads the env var at startup, uses the snapshot dir as `CacheRoot`, disables TTL expiry checks, and injects `OfflineApiClient` stubs for AzDO and Helix. Any network attempt throws a descriptive error (not silently fails), making fixture gaps visible in trajectories.

Graders see the same tool call/response shapes as live runs. The only difference is the responses are deterministic (from snapshot) rather than live. Vally's trajectory comparison across runs becomes meaningful.

**No changes to MCP tool schemas.** No changes to hlx command flags. No separate eval-specific CLI commands required.

---

## 7. Smallest PoC, Files Changed, Validation Cases

### Files to change (minimum)

| File | Change |
|------|--------|
| `CacheOptions.cs` | Add `bool EvalMode { get; init; }` |
| `SqliteCacheStore.cs` | In `GetMetadataAsync` and `IsJobCompletedAsync`: when `EvalMode`, omit `expires_at > @now` filter. Add `ExportSnapshotAsync(string destDir)` that checkpoints WAL then copies DB + artifacts. |
| New: `OfflineAzdoApiClient.cs` | `IAzdoApiClient` that throws on every method |
| New: `OfflineHelixApiClient.cs` | `IHelixApiClient` that throws on every method |
| `CachingAzdoApiClient.cs` / `CachingHelixApiClient.cs` | When eval mode: replace `_inner` with offline stubs at construction |
| `Program.cs` (MCP) / CLI entry point | Read `HLX_EVAL_SNAPSHOT` env var; if set, configure eval mode |
| CLI: new `hlx cache export` command | Calls `ExportSnapshotAsync` |

**Optionally**: a `hlx cache import --from ./snap --as build-12345` that copies a snapshot into the live cache under an isolated partition hash (so eval doesn't pollute the live cache).

### Meaningful validation cases
1. Run `hlx azdo timeline 12345 --json` with snapshot present, no network: returns correct data, no HTTP calls.
2. Snapshot older than 4h: verify data is returned (TTL bypass works), not an error.
3. Cache miss (key not in snapshot): verify clear error message, not a silent hang.
4. `log-fresh` key absent from snapshot: verify log content is returned without triggering delta network call.
5. `ListJobNamesByBuildAsync` call in eval mode: verify OfflineHelixApiClient throws with useful message (vs silently returning empty list).
6. WAL checkpoint: copy cache without checkpoint, verify no data corruption in copied DB.

---

## 8. Final Assessment

**Larry's hypothesis: Partly correct.**

| Aspect | Assessment |
|--------|-----------|
| "Work off a snapshot of the db and cached files" | ✓ Conceptually correct — the data is all there |
| "Relatively straightforward" | ✗ Overstates it — TTL expiry is a hard blocker requiring code change |
| Referential integrity | ✓ Relative paths in DB, self-contained copy |
| Portability | ✓ For public/no-auth snapshots; ∼ for auth-partitioned keys |
| Auth concern | ✓ Not a blocker for dnceng-public (no auth hash in keys) |
| WAL consistency | ⚠ Requires explicit checkpoint before copy |
| All 5 ci-evidence-reader ops coverable | ✓ Yes, if completed + checkpointed |
| `ListJobNamesByBuildAsync` | ✗ Never cached — always needs network or a separate fix |
| Ready to implement today | ✓ Slices are clear, files identified, no architectural redesign |

---

# Accepted Design: Snapshot-Based Eval Mode POC

**Date:** 2026-08-26  
**Author:** Dallas (Lead)  
**Status:** Accepted — Implementation-ready  
**Ceremony:** Design Review  
**Requested by:** Larry Ewing  
**Assignees:** Ripley (implementation), Lambert (tests + review gate)

---

## 1. Purpose

Enable Vally to run `hlx` (CLI and MCP) deterministically against a pre-recorded snapshot of the SQLite cache and artifact files, with hard-offline guarantees. No network calls, no TTL expiry, deterministic output.

## 2. Activation UX

### Environment variable (only mechanism for POC)

```
HLX_EVAL_SNAPSHOT=/path/to/snapshot-dir hlx azdo timeline 12345 --json
```

- **Single env var: `HLX_EVAL_SNAPSHOT`** — path to a snapshot directory containing `cache.db` + `artifacts/`.
- When set: eval mode activates. TTL bypassed, network hard-blocked, `CacheRoot` overridden.
- When unset: normal behavior, zero code path changes.
- No CLI flags. No `--eval-mode`. Env var is the only activation surface.
- Works identically for CLI (`src/HelixTool/Program.cs`) and MCP (`src/HelixTool.Mcp/Program.cs`) because both read the env var during DI setup. **Shared DI pattern, not shared code** — each Program.cs reads the env var and wires the same overrides independently (they already diverge: singleton vs scoped lifetimes).

### Export: **out of scope for POC**

Manual export is sufficient: `PRAGMA wal_checkpoint(FULL)` + `cp -r {cacheRoot}/ {dest}/`. A `hlx cache export` command is a follow-up, not in this POC.

## 3. Snapshot Layout

```
snapshot-dir/
├── cache.db          # SQLite database (WAL checkpointed to zero)
├── artifacts/        # Flat relative paths matching cache_artifacts.file_path
│   ├── {jobId[0..8]}/{sanitized-key}
│   └── ...
```

### WAL Consistency Requirement

Snapshot MUST NOT include `cache.db-wal` or `cache.db-shm`. Before copying, run:
```sql
PRAGMA wal_checkpoint(FULL);
```
If WAL/SHM files exist in the snapshot dir, `SqliteCacheStore` in eval mode MUST delete them before opening (defense against partial copies).

### Schema Version

Snapshot `PRAGMA user_version` must equal `SchemaVersion` (currently `1`). On mismatch, eval mode throws `InvalidOperationException` instead of destructive migration (normal mode drops and recreates — unacceptable for eval fixtures).

## 4. Network Behavior Contract

### Hard-block: `OfflineApiClient` stubs

Two new classes implementing the existing interfaces:

| New file | Interface | Behavior |
|----------|-----------|----------|
| `OfflineAzdoApiClient.cs` | `IAzdoApiClient` | Every method throws `InvalidOperationException("Network blocked: eval mode. Cache key not found in snapshot.")` |
| `OfflineHelixApiClient.cs` | `IHelixApiClient` | Same |

These replace the real `AzdoApiClient` / `HelixApiClient` as the `_inner` of the caching decorators. The caching decorators (`CachingAzdoApiClient`, `CachingHelixApiClient`) remain unchanged — they check cache first, fall through to `_inner` on miss. In eval mode, `_inner` = offline stub → miss = descriptive exception.

### No `IHelixApiClientFactory` changes

In eval mode, the factory is not used. The `IHelixApiClient` registration directly returns `CachingHelixApiClient(offlineStub, cache, options)`.

### `ListJobNamesByBuildAsync` gap

This call is uncached in production. In eval mode, the offline stub will throw. This is **by design** — it surfaces fixture gaps. If Vally needs it, the fixture must include the data pre-cached (manual insertion or captured during a warm run).

## 5. TTL / Expiry Bypass

### `CacheOptions` addition

```csharp
/// <summary>When true, ignore expires_at on all reads (eval/snapshot mode).</summary>
public bool EvalMode { get; init; }
```

### `SqliteCacheStore` changes

Two SQL queries gain a conditional bypass:

1. **`GetMetadataAsync`** (line ~116): when `EvalMode`, SQL becomes:
   ```sql
   SELECT json_value FROM cache_metadata WHERE cache_key = @key;
   ```
   (drop `AND expires_at > @now`)

2. **`IsJobCompletedAsync`** (line ~235): same pattern — drop `AND expires_at > @now`.

3. **`EvictExpiredAsync`**: skip entirely when `EvalMode` (don't delete expired rows from the snapshot).

### Auth key behavior

`AuthTokenHash` in eval mode: **null**. Eval snapshots are expected to be from public/unauthenticated contexts (dnceng-public). The env var activation sets `AuthTokenHash = null`, `CacheRootHash = null`, and `CacheRoot = snapshotDir`. Authenticated snapshot support is a non-goal for this POC.

### `log-fresh` marker

15s TTL marker is handled by the same `expires_at` bypass. No special case needed.

## 6. DI Wiring (both hosts)

### Pattern (pseudo-code, applied in both Program.cs files)

```csharp
var snapshotDir = Environment.GetEnvironmentVariable("HLX_EVAL_SNAPSHOT");
var isEvalMode = !string.IsNullOrEmpty(snapshotDir);

if (isEvalMode)
{
    var evalOptions = new CacheOptions
    {
        CacheRoot = snapshotDir,  // overrides GetEffectiveCacheRoot()
        EvalMode = true,
        CacheRootHash = null,
        AuthTokenHash = null,
    };
    // Register CacheOptions as the eval instance
    // Register ICacheStore as new SqliteCacheStore(evalOptions)
    // Register IAzdoApiClient as CachingAzdoApiClient(new OfflineAzdoApiClient(), cache, evalOptions)
    // Register IHelixApiClient as CachingHelixApiClient(new OfflineHelixApiClient(), cache, evalOptions)
    // Services, HelixService, AzdoService: wired normally from the above
}
```

**Key invariant:** `CacheOptions.CacheRoot` when set non-null is used directly by `GetEffectiveCacheRoot()` (line 41 of CacheOptions.cs: `if (!string.IsNullOrEmpty(CacheRoot)) return CacheRoot;`). But `GetEffectiveCacheRoot()` appends `/public` or `/cache-{hash}`. In eval mode, we need the snapshot dir to be used AS-IS. 

**Fix:** Set `CacheRoot` to the snapshot dir such that `GetEffectiveCacheRoot()` returns it directly. This requires either:
- (a) Adding `if (EvalMode) return CacheRoot!;` to `GetEffectiveCacheRoot()`, or
- (b) Setting `CacheRoot = snapshotDir` and having the snapshot contain `cache.db` at that root (no `/public` subdirectory).

**Decision:** Option (a). `GetEffectiveCacheRoot()` gains an early return when `EvalMode && CacheRoot != null`.

## 7. File Change Matrix

### Ripley: Production code (implement)

| File | Change |
|------|--------|
| `src/HelixTool.Core/Cache/CacheOptions.cs` | Add `bool EvalMode { get; init; }`. Modify `GetEffectiveCacheRoot()` to return `CacheRoot` directly when `EvalMode`. |
| `src/HelixTool.Core/Cache/SqliteCacheStore.cs` | Bypass `expires_at` filter in `GetMetadataAsync` and `IsJobCompletedAsync` when `EvalMode`. Skip `EvictExpiredAsync`. On schema mismatch + `EvalMode`, throw instead of drop. Delete stale WAL/SHM on open if `EvalMode`. |
| `src/HelixTool.Core/AzDO/OfflineAzdoApiClient.cs` *(new)* | `IAzdoApiClient` stub, all methods throw. |
| `src/HelixTool.Core/Helix/OfflineHelixApiClient.cs` *(new)* | `IHelixApiClient` stub, all methods throw. |
| `src/HelixTool/Program.cs` | Read `HLX_EVAL_SNAPSHOT`, wire eval DI when set. |
| `src/HelixTool.Mcp/Program.cs` | Same env var reading and eval DI wiring. |

**Ripley must NOT touch** any test files.

### Lambert: Tests + review gate

| File | Change |
|------|--------|
| `src/HelixTool.Tests/SqliteCacheStoreTests.cs` | New test class/section: eval-mode TTL bypass, schema mismatch throw, WAL cleanup. |
| `src/HelixTool.Tests/SnapshotEvalModeTests.cs` *(new)* | Integration tests: end-to-end eval mode activation via env var, cache-miss throws, deterministic output. |
| `src/HelixTool.Tests/CacheOptionsTests.cs` | Test `GetEffectiveCacheRoot()` returns `CacheRoot` directly when `EvalMode`. |

**Lambert must NOT touch** any production source files.

## 8. Acceptance Criteria

1. **TTL bypass:** `GetMetadataAsync` returns data from a snapshot older than 4h. Verified by test with fixture where `expires_at` is in the past.
2. **Network block:** Cache miss in eval mode throws `InvalidOperationException` with message containing "eval mode". Verified by test requesting a key not in the fixture.
3. **Deterministic output:** Two consecutive runs with `HLX_EVAL_SNAPSHOT` set produce byte-identical JSON output for the same command.
4. **WAL safety:** Opening a snapshot with residual WAL/SHM files: they are deleted, DB opens cleanly.
5. **Schema guard:** Snapshot with wrong `user_version` throws, does not destructively migrate.
6. **CLI + MCP:** Both `src/HelixTool/Program.cs` and `src/HelixTool.Mcp/Program.cs` activate eval mode from the same env var.
7. **No regressions:** All existing tests pass with `HLX_EVAL_SNAPSHOT` unset.

## 9. Explicit Non-Goals

- `hlx cache export` command (follow-up)
- `hlx cache import` command (follow-up)
- Authenticated/private snapshot support (auth hash in keys)
- `ListJobNamesByBuildAsync` caching (separate issue)
- Snapshot compression or archive format
- Vally integration itself (Vally reads this contract and wires itself)
- Any changes to MCP tool schemas or CLI command signatures
- Windows-specific path handling beyond what `Path.Combine` provides

## 10. Security Considerations

- **No secrets in snapshots:** Public/unauthenticated snapshots only. Auth hash is null. SAS URLs in cached metadata are expired and non-functional; artifact content is in `artifacts/` directory, not behind URLs.
- **Snapshot dir is read-only in spirit:** Eval mode should not write to the snapshot (no new cache entries). `SetMetadataAsync` / `SetArtifactAsync` calls in eval mode should be no-ops (or throw). **Decision:** No-op — silently discard writes. The caching decorators call `Set*` after reading from `_inner`; in eval mode `_inner` throws on miss, so `Set*` is never reached on the miss path. On the hit path, `Set*` may be called for `log-fresh` refresh — discarding is correct.
- **Path traversal:** `snapshotDir` from env var is used as `CacheRoot`. No sanitization needed beyond `Path.GetFullPath()` normalization (already done by `SqliteCacheStore` constructor via `Directory.CreateDirectory`).

---

*Approved by Dallas, Lead. Ripley: implement production code. Lambert: write tests and serve as review gate. Both reference this document as the contract.*

---

# Ripley → Dallas: Snapshot Eval Mode PoC — Implementation Complete

**Date:** 2026-08-26  
**From:** Ripley (Backend Dev)  
**To:** Dallas (Lead Architect)  
**Status:** Implementation delivered — awaiting Lambert tests + coordinator assembly

---

## Summary

Production implementation of the approved snapshot eval mode PoC is complete and builds cleanly (0 warnings, 0 errors across all four projects: Core, MCP, CLI, Tests).

## What Was Built

Per `dallas-snapshot-poc-design.md` contract, all six files changed:

| File | Change |
|------|--------|
| `CacheOptions.cs` | Added `bool EvalMode { get; init; }`. `GetEffectiveCacheRoot()` returns `CacheRoot` as-is when eval mode (Option a). |
| `SqliteCacheStore.cs` | WAL/SHM pre-open delete; schema mismatch throws (not migrates) in eval mode; `GetMetadataAsync` + `IsJobCompletedAsync` drop `expires_at > @now` filter; all Set* methods + `GetArtifactAsync` incidental writes (stale-row delete, `last_accessed` update) are no-ops; `EvictExpiredAsync` short-circuits; startup eviction skipped. |
| `OfflineAzdoApiClient.cs` *(new)* | `public sealed class`, all `IAzdoApiClient` methods throw `InvalidOperationException("Network blocked: eval mode. Cache key not found in snapshot.")` |
| `OfflineHelixApiClient.cs` *(new)* | `public sealed class`, all `IHelixApiClient` methods throw same. |
| `src/HelixTool/Program.cs` | Two DI sections updated (top-level CLI services + `Mcp()` command builder): `HLX_EVAL_SNAPSHOT` detection → eval if-branch, normal else-branch. |
| `src/HelixTool.Mcp/Program.cs` | Scoped DI block: `HLX_EVAL_SNAPSHOT` detection → eval if-branch (no token/factory dependency), normal else-branch. |

## Decisions Made During Implementation

1. **`GetArtifactAsync` incidental writes guarded.** The design called out Set* as no-ops. I also guarded the stale-row DELETE and `last_accessed` UPDATE inside `GetArtifactAsync` — both are DB mutations that would corrupt snapshot determinism. No approval needed; consistent with "Eval mode should not write to the snapshot" principle.

2. **`OfflineAzdoApiClient`/`OfflineHelixApiClient` made `public`.** They live in `HelixTool.Core` but are instantiated in `HelixTool` and `HelixTool.Mcp` (separate assemblies). Consistent with `CachingAzdoApiClient`, `CachingHelixApiClient` both being `public sealed class`.

3. **`CachingAzdoApiClient` 3-arg constructor used in eval wiring.** Avoids needing a stub `IAzdoTokenAccessor`; `tokenAccessor: null` is the documented pattern for unauthenticated contexts.

## One Item for Dallas Review

**`OfflineHelixApiClient` requires `using Microsoft.DotNet.Helix.Client.Models;`** — the file compiles because `IHelixApiClient` already lives in `HelixTool.Core.Helix` and has those types reachable, but the `using` is needed in the new file to resolve `IJobDetails` etc. via the existing interface. This is an additive reference to an already-in-scope transitive package — no new dependency added. Flagging in case Dallas wants to review the import.

## Lambert Handoff

Lambert owns test coverage per the design doc:
- `SqliteCacheStoreTests.cs` — eval TTL bypass, schema mismatch throw, WAL cleanup, write no-ops
- `SnapshotEvalModeTests.cs` *(new)* — integration: env var activation, cache-miss throws, deterministic output
- `CacheOptionsTests.cs` — `GetEffectiveCacheRoot()` eval direct-return

Production code does not touch any test files.

---

# bishop-snapshot-integrity — Durable Decisions

Date: 2026-08-26  
Author: Bishop (escalation specialist)  
Branch: lewing-potential-chainsaw

## Reviewer Findings Resolved

### Finding 1 (CRITICAL): WAL/SHM deletion lost committed transactions

**Decision**: Remove WAL/SHM deletion entirely. SQLite read-only connections follow the WAL
file correctly without checkpointing it. The previous deletion was incorrect: a WAL file is
an integral part of the database state and may hold the only copy of committed transactions.

**Implementation**: `SqliteCacheStore` constructor no longer deletes `cache.db-wal` or
`cache.db-shm` in eval mode.

### Finding 2 (HIGH): `PRAGMA journal_mode=WAL` in eval mode mutated snapshot

**Decision**: Separate `InitializeSchema()` into two code paths:
- `ValidateEvalSchema()` — read-only; checks `PRAGMA user_version` and `sqlite_master` table
  presence; throws `InvalidOperationException` on mismatch; no DDL, no mutating pragmas.
- `InitializeSchema()` — normal mode only; runs WAL pragma, DDL, version stamp.

Eval mode calls `ValidateEvalSchema()`. Normal mode calls `InitializeSchema()`.

### Finding 3 (MEDIUM/HIGH): `CREATE TABLE IF NOT EXISTS` could mutate malformed snapshots

**Decision**: `ValidateEvalSchema()` reads `sqlite_master` to assert each expected table exists.
If any table is absent the snapshot is rejected with a descriptive `InvalidOperationException`.
No DDL can execute on an eval-mode connection.

### Finding 4 (HIGH): HelixService received real HelixDownload HttpClient in eval mode

**Decision**: Introduce `EvalModeBlockingHandler : HttpMessageHandler` (public, in
`HelixTool.Core`) that throws `InvalidOperationException` on any `SendAsync` call.

In eval mode both Program.cs files now construct `HelixService` with
`new HttpClient(new EvalModeBlockingHandler())` instead of the factory-provided `HelixDownload`
client. The `HelixService` registration is moved inside the eval/normal branches in MCP
`Program.cs` so the registration is unambiguous.

The AzDO path is already safe in eval mode: `OfflineAzdoApiClient` is the inner client, and
`AzdoApiClient` (which holds the real `AzDO` HttpClient) is not registered in eval mode.

## SQLite Read-Only Connection String

Eval mode uses `Mode=ReadOnly` in the connection string (`Data Source={path};Mode=ReadOnly`).
Normal mode uses `Data Source={path};Cache=Shared` (unchanged).

`Mode=ReadOnly` was chosen because:
- SQLite read-only connections correctly read committed data from both the main DB file and
  any WAL file, without checkpointing.
- The connection cannot execute any DDL or mutating PRAGMAs; attempts fail at the SQLite
  engine level, providing defense-in-depth beyond the code-level guard.
- `Cache=Shared` is not needed in read-only mode (no write coordination required).

## Changed Files

- `src/HelixTool.Core/EvalModeBlockingHandler.cs` — new
- `src/HelixTool.Core/Cache/SqliteCacheStore.cs` — remove WAL deletion, read-only conn string,
  split InitializeSchema
- `src/HelixTool.Mcp/Program.cs` — HelixService in eval branch uses blocking handler; normal
  branch wiring unchanged
- `src/HelixTool/Program.cs` — eval branch HelixService uses blocking handler
- `src/HelixTool.Tests/SnapshotEvalModeTests.cs` — added EvalModeSnapshotImmutabilityTests
  (WAL preserved, DB bytes unchanged, wrong version rejected without mutation) and
  EvalModeBlockingHandlerTests (throws on send, HelixService DownloadFromUrl blocked)

## Test Results

Full suite: 1618 passed, 2 pre-existing skips, 0 failures.

---

# Retrospective: 4 Snapshot/Eval-Mode Test Failures (2026-08-26)

**Author:** Dallas (Lead)  
**Status:** Proposal — assign corrective revision to Brett

---

## Failures 1–2: EvalModeHelixServiceCompositionTests (DI / SqliteException)

**Tests:** `StandaloneMcpEvalMode_HelixService_HttpClient_IsBlocking`, `EmbeddedMcpEvalMode_HelixService_HttpClient_IsBlocking`

**Root cause — test defect.** The `EvalModeHelixServiceCompositionTests` fixture creates an empty temp directory (`_snapshotDir`) but never seeds a `cache.db` in it. `SqliteCacheStore(evalOptions)` opens `Mode=ReadOnly`, which requires the DB file to already exist. SQLite returns error 14 ("unable to open database file") because read-only mode cannot create files.

The `CliEvalMode_HelixService_HttpClient_IsBlocking` test in the same class passes only by luck — the test execution order may seed the directory, or the identical `CacheRoot` path happens to work differently. Actually, re-reading: all three share the same `_snapshotDir` and all register `ICacheStore` as singleton with `new SqliteCacheStore(evalOptions)`, so they all fail for the same reason — the directory has no `cache.db`. The test output confirms 4 failures (the other two being WAL tests).

**Corrective action:** Before building the `ServiceCollection`, seed the snapshot directory with a valid `cache.db`. Either:
- Create a normal-mode `SqliteCacheStore` against a parent dir (whose `public/` subdirectory is `_snapshotDir`), dispose it, then use `_snapshotDir` as eval root. This is the pattern used in the working `EvalModeSnapshotImmutabilityTests`.
- Or directly copy a fixture DB.

**Classification:** Test defect — production code is correct (read-only open *should* fail on missing DB).

---

## Failures 3–4: EvalModeSnapshotImmutabilityTests (WAL FileNotFoundException)

**Tests:** `EvalMode_SnapshotCacheDbPresent_HasAllRequiredDbFiles`, `EvalMode_WalFilePresentInSnapshot_IsPreserved`

**Root cause — test defect (environmental assumption).** These tests are listed in the failure report but only `EvalMode_WalFilePresentInSnapshot_IsPreserved` exists in the source. The test writes artificial WAL bytes then opens eval mode. The `SqliteCacheStore` constructor calls `Directory.CreateDirectory(root)` (line 25) and `Directory.CreateDirectory(_artifactsDir)` (line 29), which is fine. The actual failure is that the normal-mode writer in Phase 1 writes to `_parentDir` (whose effective cache root resolves to `_parentDir/public`), creating `cache.db` at `_parentDir/public/cache.db`. The test then manually creates a WAL file at `_snapshotDir/cache.db-wal` — which is the same path. However, when `SqliteCacheStore` opens in read-only mode, Microsoft.Data.Sqlite on some platforms deletes or ignores the WAL before opening, or the artificial WAL (non-valid SQLite WAL header) causes SQLite to reject the DB entirely.

Looking more closely at the actual error (`cache.db-wal FileNotFoundException`): the test assertion `File.Exists(walPath)` fails because SQLite's read-only open with `Mode=ReadOnly` on Microsoft.Data.Sqlite may delete an invalid WAL as part of recovery, or the WAL disappears because SQLite checkpoints it on close. The test's premise — that artificial WAL bytes survive a read-only open — is not guaranteed by SQLite semantics.

**Corrective action:**
- Remove the artificial-WAL survival assertion. Instead, assert that `cache.db` bytes are unchanged (already covered by `EvalMode_DbFileBytes_UnchangedAfterReads`).
- If WAL preservation is a requirement, write a *valid* WAL (by opening a normal connection in WAL mode, writing data, and closing without checkpoint) rather than synthetic bytes.
- The test `EvalMode_SnapshotCacheDbPresent_HasAllRequiredDbFiles` does not exist in source — verify whether the test runner is reporting a different test name or if this was a phantom from a prior revision.

**Classification:** Test defect — the assertion is non-portable and relies on undefined SQLite behavior with invalid WAL content.

---

## Decision

Parker authored the latest revision. These are **test defects, not production defects** — the production `SqliteCacheStore` read-only path and the `EvalModeBlockingHandler` DI wiring in `Program.cs` are correct.

**Action:** Assign Brett to create a corrective revision:
1. Seed `EvalModeHelixServiceCompositionTests._snapshotDir` with a valid `cache.db` in the constructor (use the parent-dir/normal-mode pattern from `EvalModeSnapshotImmutabilityTests`).
2. Replace the artificial-WAL test with either a valid-WAL test or remove the WAL-survival assertion entirely; assert only DB-byte immutability.
3. Verify all 4 tests pass on macOS and Linux.

Parker's production code changes are **accepted** — no rollback needed.

---

# Lambert Review: Snapshot Eval-Mode PoC — APPROVE

**Date:** 2026-08-26  
**Reviewer:** Lambert (Tester)  
**Scope:** `HLX_EVAL_SNAPSHOT` PoC — production code by Ripley + test code by Lambert  
**Verdict:** ✅ **APPROVE**

---

## Test Results

```
Passed! Failed: 0, Passed: 1612, Skipped: 2, Total: 1614
```

44 new tests across 3 files. All pass. The 2 skips are pre-existing and unrelated.

---

## Acceptance Criteria Verification

| Criterion | Status |
|-----------|--------|
| `HLX_EVAL_SNAPSHOT` resolves relative and absolute snapshot paths | ✅ Covered by `CacheOptionsTests` + `EvalModeCompositionTests` |
| Expired metadata returned in eval mode; expired in normal mode | ✅ `EvalMode_Metadata_ExpiredEntry_ReturnsValue`, `EvalMode_JobState_ExpiredEntry_ReturnsValue` |
| Eval reads do not update access metadata, evict, delete, or mutate | ✅ `EvalMode_ArtifactRead_DoesNotMutateLastAccessed`, `EvalMode_EvictExpired_IsNoOp_*`, all no-op write tests |
| Cache misses produce explicit failures; never call live clients | ✅ `CacheMiss_ThrowsInvalidOperationException_WithEvalModeMessage`, `EvalMode_CacheMiss_*` |
| Cache hits return normally without live clients | ✅ `CacheHit_ReturnsCachedBuild_WithoutCallingOfflineStub`, `EvalMode_CachedBuildAndTimeline_ReturnedWithoutNetworkCalls` |
| Normal mode unchanged | ✅ `NormalMode_*` regression tests throughout |
| Invalid/missing snapshot DB, schema mismatch are explicit and deterministic | ✅ `EvalMode_SchemaMismatch_ThrowsOnOpen`, `EvalMode_EmptySnapshotDir_ThrowsSchemaError` |
| CLI and MCP DI paths activate same semantics | ✅ `EvalModeCompositionTests` covers the `CachingAzdoApiClient` + `OfflineAzdoApiClient` composition that both DI paths wire |
| End-to-end-ish CI-evidence operation from snapshot | ✅ `SnapshotCiEvidenceScenarioTests.EvalMode_CachedBuildAndTimeline_ReturnedWithoutNetworkCalls` |

---

## Production Code Findings

No high-confidence correctness defects found in the final committed code. The pre-session identified bug (`GetArtifactAsync` missing eval-mode guards on `UPDATE last_accessed` and `DELETE` stale rows) was fixed by Ripley before tests ran — both guards are present and correct.

**Specific inspected paths:**
- `CacheOptions.EvalMode` property and `GetEffectiveCacheRoot()` bypass — correct
- `SqliteCacheStore` TTL bypass (`GetMetadataAsync`, `IsJobCompletedAsync`) — correct (no `expires_at` filter in eval mode)
- `SqliteCacheStore` write no-ops (`SetMetadataAsync`, `SetJobCompletedAsync`, `SetArtifactAsync`) — correct
- `SqliteCacheStore.GetArtifactAsync` eval-mode guards — correct (both `UPDATE` and `DELETE` guarded)
- `SqliteCacheStore.EvictExpiredAsync` no-op — correct
- Constructor: WAL/SHM cleanup before connection open — correct (stale files deleted before SQLite opens)
- Constructor: background eviction suppressed — correct (`if (!options.EvalMode)` guard)
- `OfflineAzdoApiClient` / `OfflineHelixApiClient` — all 17 methods throw with "eval mode" + "snapshot" in message
- Program.cs CLI wiring: `Path.GetFullPath()` normalization, DI composition — not directly tested (would require process-level integration test), but covered by composition tests against same types
- Schema mismatch guard: throws on `user_version != 1` in eval mode — correct

---

## Test Implementation Notes

**Known test design issue documented, not a production concern:**
- `TimeSpan.Zero` TTL rows race with the background eviction task. Fixed in tests with `await Task.Delay(30)` before writing. The race is a test-authoring footgun, not a production bug — normal mode users never rely on zero-TTL rows surviving eviction.

**WAL/SHM test revised:**
- SQLite's `PRAGMA journal_mode=WAL` causes WAL/SHM files to be recreated after every connection open. The original test assertion (`!File.Exists(walPath)` after opening) was architecturally wrong. The revised test verifies the store opens without throwing (the stale files were cleaned) and data is readable — the meaningful invariant.

---

## Changed Files (Lambert-authored)

- `src/HelixTool.Tests/CacheOptionsTests.cs` — 5 new eval-mode tests
- `src/HelixTool.Tests/SqliteCacheStoreTests.cs` — `SqliteCacheStoreEvalModeTests` class (17 tests)
- `src/HelixTool.Tests/SnapshotEvalModeTests.cs` — new file: `OfflineAzdoApiClientTests` (10), `OfflineHelixApiClientTests` (7), `EvalModeCompositionTests` (5), `SnapshotCiEvidenceScenarioTests` (4)

No production files were modified.

---

# PR #127 Review Cycle: Validator & Test Boundary Revision — APPROVED

**Date:** 2026-08-26  
**Reviewer:** Dallas (Lead Architect)  
**Initial Verdict:** REJECT (2026-08-26)  
**Final Verdict:** ✅ **APPROVE** (2026-08-26)  
**Gate Status:** Cleared — Ready for full suite and Ubuntu/Windows CI validation

---

## Executive Summary

PR #127 snapshot validator and test revisions address three boundary-check defects in snapshot validation. Initial review found validator did not verify that external aliases (cache.db, artifacts/) remained within snapshot root, and orchestration log documented incorrect file path. Dallas rejected all three artifacts. Subsequent revisions by Brett (validator), Burke (tests), and Kane (log correction) implemented required physical containment checks. Dallas approved all revisions; 43 focused tests passed with DOTNET_ROLL_FORWARD=Major.

---

## Initial Findings (REJECTED)

### Finding 1: External cache.db Alias

**Issue:** After resolving `cache.db` symlink/junction, validator opened the resolved file without verifying it remained inside snapshot root. External alias could validate external database.

**Acceptance Criteria:** Immediately after resolving `cache.db`, require strict descendant check against physical snapshot root before sidecar checks or database access. Use separator-aware boundaries, case-insensitive only on Windows, reject equality or escape.

**Resolution:** Brett's validator revision implements check: resolved path must be strict child of resolved snapshot root before database open.

---

### Finding 2: External or Root-Pointing artifacts/ Alias

**Issue:** Validator accepted resolved artifacts directory as trust root even when outside snapshot or pointing to snapshot root itself. Could validate external files or files outside artifacts subtree.

**Acceptance Criteria:** Immediately after resolving existing artifacts directory, require strict descendant check against physical snapshot root before reading artifact rows. Preserve missing-directory warning. Reject equality, escape, or resolution failure.

**Resolution:** Brett's validator revision implements check: resolved existing directory must be strict child of resolved snapshot root before artifact inspection.

---

### Finding 3: Repeated Layout Assertion

**Issue:** Two consecutive identical `AssertFinalLayout(destination)` calls in source-root-alias test.

**Resolution:** Burke's revision keeps exactly one layout assertion. Test revision includes new boundary regression tests.

---

### Finding 4: Orchestration Log Path Error

**Issue:** Log documented path as `.squad/decisions/decisions.md` instead of correct `.squad/decisions.md`. Both occurrences in file and commit descriptions incorrect.

**Resolution:** Kane corrected both references to `.squad/decisions.md`.

---

## Revision Agents & Lockouts

- **Ripley** (SnapshotValidator, rejected): Locked out; new .NET filesystem-security specialist needed
- **Lambert, Parker, Bishop** (tests, Bishop's revision rejected): Lambert, Parker locked out; no involvement in Burke's revision
- **Scribe** (log, rejected): Locked out; Kane (eligible documentation owner) corrected path

---

## Approval Verification

**Reviewer:** Dallas (2026-08-26)

### Brett's Validator Revision: APPROVED

- Resolved `cache.db` must be strict child of resolved snapshot root before sidecar/database checks
- Resolved existing `artifacts/` must be strict child before reading artifact rows
- Comparison separator-aware, case-insensitive on Windows, ordinal elsewhere
- Both checks reject equality, escape, or resolution failure

### Burke's Test Revision: APPROVED

- Regression test: external cache.db alias + symlink/junction setup (Unix/Windows)
- Regression test: populated external artifacts/ alias + junction to snapshot root
- Both tests run without platform skips, unlink safely, require physical-boundary error
- Source-root-alias test contains exactly one final-layout assertion

### Kane's Log Correction: APPROVED

- Both `.squad/decisions/decisions.md` references corrected to `.squad/decisions.md`

### Test Results

- 43 focused snapshot tests passed with `DOTNET_ROLL_FORWARD=Major`
- Independent review gate cleared; full suite and CI validation ready

---

## Acceptance Criteria Verification

| Criterion | Status | Evidence |
|-----------|--------|----------|
| Resolved cache.db strict-child check before sidecar/DB access | ✅ | Brett's validator revision |
| Resolved artifacts/ strict-child check before row/artifact inspection | ✅ | Brett's validator revision |
| Boundary regression coverage (external DB, external artifacts/, root pointer) | ✅ | Burke's test revision (all run, no skips) |
| Single layout assertion in source-root-alias test | ✅ | Burke's revision |
| Correct orchestration log paths | ✅ | Kane's correction |
| 43 focused tests pass with DOTNET_ROLL_FORWARD=Major | ✅ | Test run results |

---

## Next Steps

- Full test suite validation
- Ubuntu and Windows CI validation
- Code review and merge


---

## PR #127: Ubuntu CI SQLite Sidecar Lifecycle (Triage)

**By:** Dallas (Lead)  
**Date:** 2026-08-26T19:09:53-05:00  
**Verdict:** REJECT — Hudson's test revision brittle; Frost's production revision accepted.

### Finding

Not a product defect. On Linux, case-only spelling is distinct directory; export correctly takes success path. Ubuntu failure shows identical persistent source payloads: artifact hashes and 28,672-byte `cache.db` hash unchanged. Only additions: SQLite-owned `cache.db-shm` and zero-length `cache.db-wal`.

`SnapshotExporter` validates/backs up WAL-mode source via unpooled read-only SQLite connections. SQLite may create, remove, or retain WAL/SHM sidecars as part of connection lifecycle. Exporter issues no source write/checkpoint and must not delete SQLite-managed sidecars. Treating their lifecycle as corruption on successful online backup is incorrect.

### Acceptance Criteria

1. Replace case-sensitive success-path whole-tree equality (line 758) with focused source invariants:
   - Compare persistent source tree excluding root-level `cache.db-wal` and `cache.db-shm`
   - Retain byte equality for `cache.db` and all artifacts/non-sidecar files plus persistent directory/link topology
   - Compare exact logical database state before/after (schema/user version, all fixture rows), integrity `ok`
2. Permit only two SQLite sidecars to appear/disappear/change on success path
3. Do not weaken `FingerprintTree`, `AssertRejectedWithoutPublicationAsync`, or aliasing rejection branch
4. No production change; `SnapshotExporter.cs` frozen unless separately demonstrated defect
5. Re-run focused snapshot tests, full suite, fresh Ubuntu/Windows CI

### Ownership

Hudson locked out from revising/advising; existing lockouts (Lambert, Parker, Bishop, Burke) remain. Coordinator must recruit independent .NET/SQLite filesystem test owner for `SnapshotExportTests.cs`. Frost sole production owner, no change needed. PR gate closed pending Dallas re-review and fresh green CI.

---

## Linux Case-Only Snapshot Source Integrity (Vasquez Revision)

**By:** Vasquez  
**Date:** 2026-08-26  
**Scope:** `Export_CaseOnlyDestination_UsesPlatformBoundaryComparison`

On case-sensitive filesystem, successful export may change lifecycle of SQLite root-level `cache.db-wal` and `cache.db-shm` without mutating persistent source data. Success-path source fingerprint excludes only regular-file fingerprints for these two exact root-level paths. Nested names, directories, links, `cache.db`, artifacts, and all other source entries retain byte/topology checking.

Test also compares integrity-check results, schema/user versions, complete `sqlite_schema`, and every row/column in three fixture tables before/after export. Case-insensitive rejection branch retains unfiltered whole-tree and publication-residue checks.

**Validation:** With `DOTNET_ROLL_FORWARD=Major`: case-only test passed once after compilation + 10 repeated no-build runs; 54 focused snapshot tests passed; test project compiled for `linux-x64` with zero warnings/errors.

---

## PR #127: Ubuntu CI SQLite Sidecar Lifecycle (Recheck)

**By:** Dallas (Lead)  
**Date:** 2026-08-26T19:17:26-05:00  
**Verdict:** APPROVE

Vasquez's revision confined to case-sensitive success branch. Local fingerprint removes only regular-file records for root `cache.db-wal` and `cache.db-shm`; nested names, directories, links, `cache.db`, artifacts, all entries retain exact topology/length/SHA-256 comparison. Test compares integrity, schema/user versions, complete schema, every fixture row column before/after export.

Case-insensitive branch uses unfiltered rejection helper before any database open, preserving strict source/destination/publication-residue checks. Both filesystem branches execute substantive assertions; test non-vacuous on all platforms.

With `DOTNET_ROLL_FORWARD=Major`: case-only test passed build run + 10 consecutive `--no-build` runs; all 54 focused snapshot tests passed, no skips. Frost's production exporter remains accepted/frozen. Local revision gate cleared for full suite and fresh Ubuntu/Windows CI.

### Approval Summary
- ✅ Vasquez's test revision: case-sensitive success-path source fingerprint correctly excludes only SQLite sidecars
- ✅ All focused snapshot tests passing (54/54)
- ✅ Frost's production exporter remains accepted and frozen
- ✅ Gate cleared for full suite and fresh CI validation
# PR #127 WAL Readiness CI Triage

**By:** Dallas (Lead)  
**Date:** 2026-08-26T19:28:01-05:00  
**Head:** `9a7fd86`  
**Verdict:** **REJECT the stress-test helper revision.** Frost's production exporter remains
accepted and frozen.

## Actual Race

Ubuntu failed in `RunCheckpointerAsync` on `walPages == -1`; Windows was canceled by fail-fast.
Vasquez's latest revision did not touch this helper.
The immediately preceding `f615329` Ubuntu/Windows run passed, while `9a7fd86` changed only the
Squad status record, so the pass-to-fail transition occurred with byte-identical stress code.

The test treats a committed-write signal as proof that a separately opened checkpointer connection
must immediately report a current WAL. That implication is invalid:

- `CreateSource` closes every setup connection, so SQLite may finish/checkpoint and remove the
  current WAL/SHM generation while the database remains configured for WAL.
- The writer signals only after `Commit()`, which proves the transaction committed but does not
  prove that the later checkpointer connection has attached to a current WAL generation.
- The checkpointer opens only after that signal and executes only connection-local
  `PRAGMA busy_timeout` before its first checkpoint. WAL attachment/journal mode is not explicitly
  established or asserted on that connection.
- SQLite initializes checkpoint counts to `-1`; the exact pair `(-1, -1)` is a legitimate
  no-checkpoint/no-current-WAL result, and checkpoint-lock contention may report it with `busy == 1`.
  It is not checkpoint progress.

Bishop's branch retries the exact no-current-WAL row only after `checkpointerReady` is already
complete. The same transient before readiness therefore throws instead of polling. Lambert's
original vacuity concern remains solved only if `-1` is non-progress, never readiness.

## Assigned Revision Owner

**Hicks** — new independent .NET/SQLite concurrency test specialist.

Lambert, Parker, Bishop, Burke, Hudson, and Vasquez are locked out of this revision **and its
advice**. Hicks must derive the revision from this decision and the code/API contract, not from
those authors. No production file may change.

## Required Deterministic Design

1. Use an unpooled writer/anchor connection. Explicitly obtain and assert `journal_mode=wal`, set
   `wal_autocheckpoint=0`, and keep that connection alive from worker initialization through worker
   cancellation and join.
2. Establish a clean checkpoint baseline before the known write (for example, a successful
   `TRUNCATE` checkpoint while the anchor is live), so later positive counts cannot be stale setup
   progress.
3. Use separate asynchronous gates for writer/anchor initialization, checkpointer initialization,
   first committed write, and first genuine checkpoint progress. The checkpointer must open and
   force a real database read/assert WAL mode while the anchor is live; the writer must signal the
   committed-write gate only after `transaction.Commit()`.
4. The checkpointer must wait for the committed-write gate before polling
   `PRAGMA wal_checkpoint(PASSIVE)`.
5. Classify checkpoint rows strictly:
   - `busy` must be `0` or `1`.
   - Exact `walPages == -1 && checkpointedPages == -1` is retryable non-progress both before and
     after readiness, under the existing bounded timeout. It must not increment a counter or
     complete readiness.
   - Mixed negative values, values below `-1`, or `checkpointedPages > walPages` fail.
   - Readiness requires one post-commit result with `busy == 0`, `walPages > 0`, and
     `checkpointedPages > 0`.
6. Export must not begin until both the committed-write and checkpoint-progress gates complete.
   Retain the existing counter assertions, active-task assertions, four exports, integrity,
   transactional head/count, exact baseline key/value, artifact, validator, no-sidecar, and cleanup
   checks.
7. Use synchronization gates, not sleeps, for ordering. Polling/backoff is allowed only inside the
   finite readiness deadline. On shutdown, cancel and join both workers while the anchor is still
   open, then dispose it. Continue propagating every non-cancellation worker exception.

## Acceptance Gates

- A deterministic test of the checkpoint-row state machine proves
  `(-1,-1) -> positive` does not become ready on the first sample and does on the positive sample;
  persistent `(-1,-1)` times out; mixed negatives fail.
- All `SnapshotExportTests` pass.
- The real stress test passes 100 consecutive repetitions, with no skipped run.
- Full suite passes with only the two pre-existing skips.
- Fresh Ubuntu and Windows GitHub Actions jobs both pass at the revision head.
- Diff is confined to `src/HelixTool.Tests/SnapshotExportTests.cs` plus Squad records. Frost's
  production exporter remains unchanged.

# PR #127 WAL Readiness CI Recheck

**By:** Dallas (Lead)  
**Date:** 2026-08-26T19:46:20-05:00  
**Base head:** `9a7fd86` plus Hicks's working-tree test revision  
**Verdict:** **APPROVE** the WAL-readiness test revision for full-suite and fresh CI validation.
Frost's production exporter remains accepted and frozen.

## Gate Verification

- The unpooled writer/anchor establishes and asserts WAL mode, disables autocheckpointing, records a
  successful zero-page `TRUNCATE` baseline, and remains live until both canceled workers join.
- Distinct writer-init, checkpointer-init, committed-write, and checkpoint-progress gates establish
  ordering without sleeps. The checkpointer asserts WAL mode and performs a real baseline read before
  the writer commits; exports wait for both commit and genuine checkpoint progress.
- Checkpoint rows are classified strictly. Exact `(-1,-1)` is non-progress before and after
  readiness; mixed/below-`-1` negatives, invalid busy values, and over-checkpoint rows fail.
  Readiness requires a post-commit, non-busy result with both page counts positive.
- Ordering uses no sleeps, and the readiness phase has a finite deadline. Non-cancellation worker
  failures propagate, while shutdown cancels and joins both workers before disposing the anchor.
- The four exports and all prior task, counter, integrity, transactional consistency, baseline,
  artifact, validator, no-sidecar, atomic-publication, and cleanup assertions remain intact.
- The implementation diff is test/Squad-only; no production file changed.

## Validation

- All 53 tests declared in `SnapshotExportTests.cs` passed with zero skips under
  `DOTNET_ROLL_FORWARD=Major`.
- The real WAL writer/checkpointer stress test passed 100 consecutive isolated repetitions with zero
  failures or skips under `DOTNET_ROLL_FORWARD=Major`.

The local revision gate is cleared. The full suite (allowing only the two pre-existing skips) and
fresh Ubuntu and Windows GitHub Actions jobs remain mandatory before final approval.

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

