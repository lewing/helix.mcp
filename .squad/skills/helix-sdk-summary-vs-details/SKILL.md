---
name: "helix-sdk-summary-vs-details"
description: "Audit pattern for comparing WorkItemSummary (list API) fields against WorkItemDetails (per-item API) after a Helix.Client SDK bump"
domain: "helix"
confidence: "high"
source: "issue-91"
---

# Helix SDK: WorkItemSummary vs. WorkItemDetails Field Surface

Use this pattern when a Helix.Client SDK bump potentially adds new fields to `WorkItemSummary` (the list-API response), and you need to decide which `DetailsAsync` (per-item) call sites can be eliminated.

## The optimization problem

`ListAsync` returns a `WorkItemSummary` for every work item in one HTTP call.  
`DetailsAsync` fetches `WorkItemDetails` per work item — O(N) HTTP calls.

When the list API starts returning fields that previously required a per-item details call, those details calls become unnecessary (for those fields). The key question: which fields does each call site actually need?

## Audit checklist (run after each SDK bump)

For every call site that calls `GetWorkItemDetailsAsync`:

1. **List the fields read from `details`.**  
   Common: `ExitCode`, `State`, `MachineName`, `Started`, `Finished`, `ConsoleOutputUri`.

2. **Check which of those fields are now on `IWorkItemSummary`.**  
   After arcade PR #16722 (client ≥ `11.0.0-beta.26325.x`): `ExitCode`, `ConsoleOutputUri`.

3. **Decision matrix:**

| Fields needed from details | Can skip DetailsAsync? |
|---|---|
| ONLY `ExitCode` | ✅ Use `summary.ExitCode` |
| ONLY `ConsoleOutputUri` | ✅ Use `summary.ConsoleOutputUri` |
| ONLY `ExitCode` + `ConsoleOutputUri` | ✅ Use both from summary |
| `State` or `MachineName` or `Started`/`Finished` (duration) | ❌ Keep DetailsAsync |

4. **For loop/batch callers (N work items):** Only skip DetailsAsync for the whole batch if NONE of the loop iterations need `State`/`MachineName`/`Duration`. A hybrid is also valid: use `summary.ExitCode` to avoid DetailsAsync for zero-exit-code items, still call it for non-zero/null.

## The ExitCode=0 fast-path (canonical optimization)

```csharp
// In a loop over IWorkItemSummary items:
var tasks = workItems.Select(wi => wi.ExitCode == 0
    ? Task.FromResult(CreatePassedResult(wi, id))   // skip DetailsAsync
    : CreateDetailedResultAsync(wi, id, semaphore, ct)) // still calls DetailsAsync
    .ToList();
```

This is correct because:
- `ExitCode == 0` → item passed, no State/MachineName/Duration needed for a pass result
- `ExitCode != 0` → classification + duration require `State`/`MachineName`/`Started`/`Finished`
- `ExitCode == null` → in-progress; check details for `isCompleted = details.ExitCode.HasValue`

## IsCompleted semantics (must not regress)

See `helix-iscompleted-bucketing` skill. After adopting list-API `ExitCode`:
- `summary.ExitCode == null` for in-progress items → still route to DetailsAsync, set `isCompleted = false`
- `summary.ExitCode.HasValue` for completed items → can skip DetailsAsync IF no other fields are needed
- Never infer completion from `summary.ExitCode.HasValue` and skip DetailsAsync if you need `details.State`

## ConsoleOutputUri: field vs. constructed URL

`summary.ConsoleOutputUri` is now available from the list API. The codebase also constructs the URL via:
```
https://helix.dot.net/api/2019-06-17/jobs/{jobId}/workitems/{name}/console
```
Both resolve to the same endpoint. The constructed URL is always valid (even for in-progress items where the field may be null). Prefer the field if switching; keep the constructed URL if the call site needs reliability over null-safety.

## Version history

| SDK version | `WorkItemSummary.ExitCode` populated at runtime | `WorkItemSummary.ConsoleOutputUri` populated |
|---|---|---|
| ≤ `11.0.0-beta.26265.121` | ❌ (field exists in model, server returns null) | ❌ |
| ≥ `11.0.0-beta.26325.102` | ✅ | ✅ |

## Proven uses

- Issue #91: SDK bump `26265.121 → 26325.102`, confirmed ExitCode=0 fast-path now works at runtime in `GetJobStatusAsync`. No additional refactoring needed (all other call sites need `State`/`MachineName`/`Duration`).
