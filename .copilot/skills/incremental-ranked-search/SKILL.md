---
name: "incremental-ranked-search"
description: "Pattern for searching across many items with ranked priority and early termination"
domain: "api-design"
confidence: "low"
source: "earned"
---

## Context
When building a tool that must search across many remote resources (logs, files, database records) where:
- The total set is too large to download all at once
- Some items are far more likely to contain results than others
- The caller wants "first N matches" not "all matches"

Use a three-phase pattern: cheap metadata → rank → incremental fetch with early exit.

## Pattern

### Phase 1: Metadata Collection
Fetch lightweight metadata about ALL items in parallel. This gives you the information needed to rank without downloading content. Look for APIs that return counts, sizes, or status without payload.

Example: AzDO's `GET builds/{id}/logs` returns `lineCount` per log without content. Timeline returns `result` per task without log content.

### Phase 2: Ranked Queue
Assign items to priority buckets based on metadata, then sort within buckets by a secondary signal (e.g., size descending = larger items more likely to contain matches).

```
Bucket 0: Known failures (highest priority)
Bucket 1: Items with warnings/issues
Bucket 2: Partial failures
Bucket 3: Normal items above minimum size threshold
Bucket 4: Orphans / unknowns
```

Filter items below a minimum threshold (e.g., skip files < 5 lines) to eliminate noise.

### Phase 3: Incremental Fetch with Early Exit
Process items sequentially from the ranked queue. Track `remainingMatches` and pass it to each search call as that call's own `maxMatches`. Stop when:
- `remainingMatches <= 0` (early exit — found enough)
- Queue exhausted
- `maxItemsToSearch` limit reached (API call budget)

```
remainingMatches = maxMatches
for item in rankedQueue[0..maxItems]:
    if remainingMatches <= 0: break
    result = search(item, maxMatches: remainingMatches)
    remainingMatches -= result.matchCount
```

## Key Design Decisions

### Sequential > Parallel (Phase 3)
Start sequential. Parallel downloads complicate early termination (you may over-download). Add bounded parallelism (`SemaphoreSlim`) only when measured performance requires it.

### Three Safety Guards
1. **maxMatches** — caps total output (context overflow protection)
2. **maxItemsToSearch** — caps API calls (rate limit protection)
3. **minItemSize** — filters noise (boilerplate elimination)

All three should have reasonable defaults and be caller-overridable.

### Report What You Skipped
The result should include `totalItems`, `itemsSearched`, `itemsSkipped`, and `stoppedEarly` so the caller knows whether the search was exhaustive or partial. This is critical for AI consumers that need to know "did I miss something?"

## Anti-Patterns

### Downloading Everything Then Searching
Fetching all content up front wastes bandwidth and memory. The ranked approach typically finds matches in the first 3–5 items.

### Parallel Fetch Without Budget
Launching N parallel downloads without a `maxItemsToSearch` cap can overwhelm the API and download far more data than needed.

### Flat Priority (No Ranking)
Searching in arbitrary order (e.g., by ID) means you might download 30 boring logs before hitting the one that failed. Always rank by failure/error likelihood.
