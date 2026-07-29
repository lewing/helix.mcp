---
name: "mcp-sibling-schema-consistency"
description: "Keep MCP tool parameter schemas consistent across sibling tools that share a conceptual resource (e.g., jobId + workItem)."
domain: "mcp-server-design"
confidence: "high"
source: "earned"
---

## Context

Use this when adding parameters to one MCP tool in a family where sibling tools already share a parameter pattern. Inconsistency causes hard schema-rejection errors for calling models that learn the pattern from one sibling and apply it to others.

## Pattern

### Identify the canonical parameter set for each resource type

For Helix tools, the canonical per-work-item parameter set is:
```
(string jobId, string? workItem = null, ...)
```
Where `jobId` accepts a GUID, a job URL, or a full work-item URL (with `workItem` auto-extracted via `HelixIdResolver.TryResolveJobAndWorkItem`).

Every tool that can be scoped to a single work item MUST accept `workItem`.

### The URL extraction pattern (replicated in each MCP tool method)

```csharp
// If workItem not provided, try to extract from jobId URL
if (string.IsNullOrEmpty(workItem) && HelixIdResolver.TryResolveJobAndWorkItem(jobId, out var resolvedJobId, out var resolvedWorkItem))
{
    if (!string.IsNullOrEmpty(resolvedWorkItem))
    {
        jobId = resolvedJobId;
        workItem = resolvedWorkItem;
    }
}
```

This is intentionally repeated per tool (not centralized) so each tool's parameter flow is self-contained.

### Single-item fast path in the service

When `workItem` is added to a multi-item scan method (e.g., `FindFilesAsync`), add a fast path:
```csharp
if (!string.IsNullOrWhiteSpace(workItem))
{
    // Skip ListWorkItemsAsync; query single item directly
    var files = await _api.ListWorkItemFilesAsync(workItem, id, cancellationToken);
    // ... apply filter, build result
    return new FindFilesResults(..., Truncated: false, TotalWorkItems: 1);
}
// ... original multi-item scan path
```

### Watch for parameter ordering in modified signatures

When inserting a new optional parameter into an existing method, any callers that pass subsequent optional params POSITIONALLY will silently bind to the wrong slot (type-compatible) or fail to compile (type-incompatible). Prefer named arguments for optional params after the first.

**Example:** Adding `string? workItem = null` before `CancellationToken cancellationToken` meant the existing `FindBinlogsAsync` call `FindFilesAsync(jobId, pattern, maxItems, progress: null, cancellationToken)` would bind `cancellationToken` to the `workItem` slot — a compile error here, but a silent bug if types were compatible. Fix: use `cancellationToken: cancellationToken`.

## Audit Checklist

When adding a new tool to a family:
1. List all sibling tools' parameter signatures
2. Identify the canonical resource-scoped parameter set
3. Ensure the new tool includes all canonical params (at minimum as optional)
4. Confirm URL extraction is present if siblings do it
5. Add the single-item fast path at the service layer if the tool does multi-item iteration
6. **No test change needed for new work-item-scoped tools** — the guard `HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped` discovers tools automatically via reflection. Simply adding `workItem` to the method signature is sufficient. For a new **job-scoped** tool (intentionally no `workItem`), add its MCP name to the `intentionallyJobScopedTools` set in that test with an explanatory comment.

## Testing Considerations

### Schema consistency test pattern

The guard is `HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped` in `McpToolDescriptionTests.cs` — a `[Fact]` that **automatically discovers** all `HelixMcpTools` methods via reflection. It fails if any tool accepts `jobId` but omits `workItem` and is not in the `intentionallyJobScopedTools` exclusion set.

**Adding a work-item-scoped tool:** add `workItem` to the method signature. No test edit required.

**Adding a job-scoped tool:** add the tool's MCP name (with a justification comment) to `intentionallyJobScopedTools` in the test. Current job-scoped tools: `helix_status`, `helix_batch_status`.

Describe the PATTERN in documentation (reflection-based discovery + explicit declared exclusions) rather than pinning exact assertion text, because the exclusion set evolves as new tools are added.

### Reflection-based behavioral tests (for anticipated params)

When writing a test that calls a method with a parameter not yet in the codebase, use:
```csharp
var workItemParam = method.GetParameters().FirstOrDefault(p => p.Name == "workItem");
Assert.True(workItemParam is not null, "Missing 'workItem' — implementation pending.");

// Position-independent invocation:
var args = method.GetParameters().Select<ParameterInfo, object?>(p => p.Name switch {
    "jobId"    => someJobId,
    "workItem" => "target-wi",
    "pattern"  => "*.trx",
    _ when p.HasDefaultValue => p.DefaultValue,
    _ => null
}).ToArray();
var result = await (Task<TResult>)method.Invoke(instance, args)!;
```

This compiles immediately (no forward-reference error), fails clearly if the param is absent, and passes once the implementation arrives.

### Positional-arg fragility

When inserting a new optional parameter before other optional parameters, existing tests using positional args silently bind to the wrong slot. **Use named args for optional parameters after the first in tests.** Example: `FindFiles(jobId, pattern: "*.trx")` not `FindFiles(jobId, "*.trx")`.

## Anti-Patterns

- Adding a parameter to most-but-not-all tools in a family because one does "batch" scanning (models still try the parameter)
- Documenting `maxItems` without explaining that `workItem` bypasses it
- Relying on `did-you-mean` to guide callers — it suggests `maxItems` for `workItem`, which is actively misleading
