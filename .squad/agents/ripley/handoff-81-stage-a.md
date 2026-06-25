# Handoff: Issue #81 Stage A — Lambert (tests) + Dallas (status)

**Date:** 2026-06-24  
**Branch:** `squad/81-strict-mode-stage-a`  
**Commit:** `fce8686`  
**Status:** Build green (0 warnings), 1337 passed / 2 skipped — ready for Lambert to add tests

---

## For Lambert — Tests to Write

All tests go in `src/HelixTool.Tests/McpServerOptionsExtensionsTests.cs` (or a sibling file if you prefer to separate the new scenarios). Use the existing `CreateFilteredToolHandler` / `CreateFilteredHandler` / `CreateRequest` / `Arguments` helpers already in that file — they're exactly the right shape per `mcp-calltoolfilter-tests` SKILL.md.

### Scenario 1 — Smoke regression: canonical args still pass
Confirm a valid call to `azdo_builds` with only canonical params (e.g. `buildIdOrUrl`, `project`, `repo`) still succeeds end-to-end through the filter. This guards against any regression from the `UnmappedMemberHandling.Disallow` setting.

Note: The serializer options are set on `WithToolsFromAssembly` at the host level. The existing `CreateFilteredToolHandler` creates a `McpServerTool` with `options: null` — you'll need to pass the strict serializer options to `McpServerTool.Create` to exercise the Disallow path:
```csharp
var strictOptions = new McpServerToolCreateOptions
{
    SerializerOptions = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }
};
var tool = McpServerTool.Create(method, tools, strictOptions);
```

### Scenario 2 — `result: 'failed'` still works via new alias
Call `azdo_search_timeline` with `result: "failed"` (not `resultFilter: "failed"`). Assert the call succeeds and the underlying service receives the call with the correct filter. Use the `CreateFilteredToolHandler` + mock client pattern from the existing `AddBindingErrorFilter_EndToEndSearchTimeline_AliasReachesService` test as a template.

### Scenario 3 — Unknown param to `azdo_builds` throws `McpException`
Call `azdo_builds` with an unknown param (e.g. `minFinishTime: "2024-01-01"`). Assert `McpException` is thrown (not silent drop). Verify `ex.Message` contains `"Parameter binding error"` and `ex.InnerException` is an `ArgumentException`. Use `McpServerTool.Create` with the strict serializer options (see Scenario 1 note).

### Scenario 4 — Unknown param message names the bad key
From Ash's report, the error message format is:
`"The arguments dictionary contains an unexpected key 'minFinishTime' that does not correspond to any parameter of '...'"`
Assert that the `McpException.Message` or `InnerException.Message` contains the name of the unknown key. This gives callers actionable information.

### Scenario 5 — Multiple unknown params (if possible)
Send two unknown params (e.g. `minFinishTime` and `maxTime` — both hallucinated). Confirm `McpException` is thrown. The SDK may only report the first unknown key per throw (check the actual error message shape), so at minimum assert the exception is thrown and at least one unknown key is named.

### Scenario 6 — Missing required param still throws `McpException` (not regression)
Existing test `AddBindingErrorFilter_StillConvertsBinderError_WhenNoCanonicalOrAliasExists` covers this. Run it against the strict serializer options to confirm the existing missing-required-param behavior is unchanged alongside the new Disallow setting.

### Scenario 7 — `result` alias is removed from dict after rename
Assert that after the filter processes a `result: "failed"` request, the `result` key is absent from `request.Params.Arguments` and `resultFilter` is present. This guards the "alias key removed" fix.

---

## For Dallas — Status Summary

**Stage A is complete and pushed.** Branch `squad/81-strict-mode-stage-a`, commit `fce8686`.

**What shipped:**
1. `("result", "resultFilter")` added to `s_argumentAliases` — the `azdo_search_timeline` caller flow is safe.
2. `arguments.Remove(aliasKey)` added after alias rename — without this, the alias key would survive into strict-mode checking and trigger a rejection on every aliased call. (This was a pre-existing latent bug revealed by the strict-mode work.)
3. `NormalizeArgumentAliases` loop uses `continue` instead of `return` — callers passing aliases for two different canonicals now have both resolved in one filter pass.
4. `JsonUnmappedMemberHandling.Disallow` set via `WithToolsFromAssembly` serializer options in both `HelixTool.Mcp/Program.cs` and `HelixTool/Program.cs`.
5. `AddBindingErrorFilter` unchanged — the existing `ArgumentException(paramName: "arguments")` catch covers the strict-mode throw path as Ash confirmed.

**No alias gaps found** beyond `result → resultFilter` during implementation review.

**Open question from triage resolved in ripley-issue-81-stage-a.md:** Keep Stage A's `Disallow` setting when Stage B lands; it covers the `HasCustomParameterBinding == true` edge case that Stage B's CallToolFilter approach misses.

**Lambert is next:** Lambert adds the 7 test scenarios above. Once tests pass, PR is ready for review.

**Stage B (did-you-mean CallToolFilter) is NOT in this branch** — separate PR per Dallas's plan.
