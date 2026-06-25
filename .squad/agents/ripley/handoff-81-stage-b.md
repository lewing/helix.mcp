# Handoff: Issue #81 Stage B — Unknown-Param Filter with Levenshtein Hints

**Date:** 2026-06-24  
**Author:** Ripley  
**Branch:** `squad/81-strict-mode-stage-b`  
**For:** Lambert (tests)

---

## What was implemented

New `AddUnknownParameterFilter(Assembly, ILogger?)` extension method on `McpServerOptions`
in `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs`.

**Filter pipeline order** (both Program.cs files):
1. `AddBindingErrorFilter` — alias normalization + SDK `ArgumentException` wrapping
2. `AddUnknownParameterFilter` — proactive unknown-param check (Stage B)
3. SDK dispatch — `UnmappedMemberHandling.Disallow` as safety net (Stage A)

**At startup:** Scans the `HelixMcpTools` assembly using `RuntimeHelpers.GetUninitializedObject`
+ `McpServerTool.Create` to extract `ProtocolTool.InputSchema["properties"]` for each tool.
Builds `toolName → ToolParamInfo` (canonical set + ordered names). Captured in filter closure.

**At request time:** Computes `unknowns = argKeys - canonicalSet`. On non-empty unknowns,
throws `McpException` with the format:
```
Unknown parameter 'minFinishTime' for tool 'azdo_builds'.
Did you mean: minTime?
Allowed parameters: org, project, top, branch, prNumber, definitionId, status, minTime, maxTime, queryOrder.
```
Or for multiple unknowns:
```
Unknown parameters for tool 'azdo_builds':
  'minFinishTime' — did you mean: minTime?
  'foo'
Allowed parameters: org, project, top, branch, prNumber, ...
```

**Levenshtein threshold:** 6. Omits "Did you mean" if no candidate within threshold.
(Threshold raised from the original spec's ≤3 to 6 to cover the `minFinishTime`→`minTime` regression case, which has distance 6. Ash validated the choice.)

---

## Test scenarios Lambert should cover

All tests belong in `McpServerOptionsExtensionsTests.cs`. The `CreateUnknownParamFilteredToolHandler`
helper follows the same pattern as `CreateStrictFilteredToolHandler` but calls
`options.AddBindingErrorFilter().AddUnknownParameterFilter(typeof(AzdoMcpTools).Assembly)`.

| # | Scenario | Tool | Args | Expected |
|---|----------|------|------|----------|
| 1 | Canonical-only args pass | `azdo_builds` | `org, project` | No exception |
| 2 | Aliased arg passes (alias resolved before filter) | `azdo_search_timeline` | `result=failed, buildIdOrUrl=42, pattern=x` | No exception; `result` renamed to `resultFilter` before this filter fires |
| 3 | Single unknown, close match | `azdo_builds` | `minFinishTime=2024-01-01` | `McpException` with "Did you mean: minTime?" |
| 4 | Single unknown, no close match | `azdo_builds` | `foo=bar` | `McpException` WITHOUT "Did you mean", WITH "Allowed parameters:" |
| 5 | Multiple unknowns, all surfaced | `azdo_builds` | `minFinishTime=x, fooBar=y` | `McpException` listing both unknowns |
| 6 | Bug-class regression | `azdo_builds` | `minFinishTime=2024-01-01` | Message contains "Did you mean: minTime?" (threshold 6; Levenshtein distance = 6) |
| 7 | Missing required param still wraps as McpException | `azdo_build` | (empty) | `McpException` from `AddBindingErrorFilter` catch (unchanged behavior) |
| 8 | No `properties` schema (parameterless tool) | create a test tool with no params | any arg | `McpException` with unknown param; allowed list "(none)" |
| 9 | Unknown key casing | `azdo_builds` | `ORG=x, MINFINISHTIME=y` | `ORG` passes (case-insensitive match to `org`); `MINFINISHTIME` flagged |

### Helper pattern

```csharp
private static McpRequestHandler<CallToolRequestParams, CallToolResult>
    CreateUnknownParamFilteredToolHandler(string toolName, AzdoMcpTools tools)
{
    var options = new McpServerOptions()
        .AddBindingErrorFilter()
        .AddUnknownParameterFilter(typeof(AzdoMcpTools).Assembly);
    // two filters now in options.Filters.Request.CallToolFilters
    var method = typeof(AzdoMcpTools).GetMethods(BindingFlags.Instance | BindingFlags.Public)
        .Single(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name == toolName);
    var toolCreateOptions = new McpServerToolCreateOptions
    {
        SerializerOptions = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        }
    };
    var tool = McpServerTool.Create(method, tools, toolCreateOptions);
    // Chain the two filters:
    var filter1 = options.Filters.Request.CallToolFilters[0];
    var filter2 = options.Filters.Request.CallToolFilters[1];
    McpRequestHandler<CallToolRequestParams, CallToolResult> baseHandler = (req, ct) => tool.InvokeAsync(req, ct);
    return filter1(filter2(baseHandler));
}
```

> Note: Lambert — the filter index ordering above assumes AddBindingErrorFilter is [0] and
> AddUnknownParameterFilter is [1]. Verify against the actual SDK pipeline composition if
> a test fails unexpectedly.

---

## Files changed

- `src/HelixTool.Mcp.Tools/McpServerOptionsExtensions.cs` — new filter + helpers
- `src/HelixTool.Mcp/Program.cs` — `options.AddUnknownParameterFilter(...)`
- `src/HelixTool/Program.cs` — `options.AddUnknownParameterFilter(...)`

## Build/test status at handoff

`dotnet build --no-restore -warnaserror` → succeeded (0 warnings, 0 errors)  
`dotnet test --no-build` → 1345 passed, 2 skipped, 0 failed
