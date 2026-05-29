---
name: "helix-iscompleted-bucketing"
description: "Completion-safe bucketing for Helix work items with nullable exit codes"
domain: "helix"
confidence: "high"
source: "issue-70"
---

# Helix IsCompleted Bucketing

Use this pattern when mapping Helix work item details into pass/fail/in-progress results.

## Pattern

- Treat `details.ExitCode.HasValue` as the completion signal.
- Preserve `details.ExitCode ?? -1` as the existing sentinel value for incomplete items.
- Only classify failures when `isCompleted && exitCode != 0`.
- Put incomplete items in an explicit in-progress/incomplete bucket rather than failed.
- Surface `IsCompleted` to callers when an API still exposes the `-1` sentinel so consumers can distinguish sentinel from a real exit code.

## Example

```csharp
var exitCode = details.ExitCode ?? -1;
bool isCompleted = details.ExitCode.HasValue;
FailureCategory? category = isCompleted && exitCode != 0
    ? ClassifyFailure(exitCode, details.State, duration, workItem)
    : null;
```

## Proven uses

- Bulk status path (`WorkItemResult`): PR #66.
- Single work-item detail path (`WorkItemDetail`): issue #70.
