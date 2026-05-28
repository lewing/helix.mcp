# MCP Error Surfacing via `McpException`

**Updated:** 2026-05-28 (CallToolFilters middleware implemented + schema audit findings captured)

## Core Strategy

The MCP SDK deliberately suppresses inner exception messages as a security boundary. Our strategy is NOT to bypass this; it's to **use `McpException` as the designated channel** where we want clients to see detail. The MCP SDK's `CallToolFilters` middleware pipeline provides a clean interception point for SDK parameter binding failures.

## Three Layers of MCP Error Handling

### Layer 1: SDK Parameter Binding — FIXED via CallToolFilters (v0.7.5)
- **Exception source:** `Microsoft.Extensions.AI.AIFunctionFactory` during reflection-based parameter marshalling
- **Observable:** Exception logged to stderr, generic "An error occurred invoking 'X'." sent to client before the fix
- **McpExceptionHandler reached?** ❌ No (method not invoked)
- **Our fix:** ✅ **CallToolFilters middleware** — intercept `ArgumentException` where `ParamName == "arguments"`, convert to `McpException`
- **Scope:** All 25 tools automatically protected from both MCP startup paths
- **Example:** Agent uses `buildIdOrUrl` on a Helix tool expecting `jobId` (parameter binding fails)

### Layer 2: MCP Server Error Formatting (Infrastructure)
- **Exception source:** MCP Server's unhandled-exception handler
- **Observable:** Full exception logged to stderr, generic response to client
- **Our lever:** custom middleware only if the current CallToolFilters approach proves insufficient

### Layer 3: Application Layer — COVERED via PR #64 (Runtime Exceptions)
- **Exception source:** Service layer, API client, or application code
- **McpExceptionHandler reached?** ✅ Yes (caught inside `RunServiceCallAsync`)
- **Our fix:** ✅ PR #64 (complete)
- **Example:** HttpRequestException, TaskCanceledException, ArgumentException from business logic

---

## CallToolFilters Pattern (Class A Fix — v0.7.5)

Register cross-cutting CallToolFilters middleware through an extension method on `McpServerOptions`, shared by both startup paths. The ModelContextProtocol 1.3.0 API path is `McpServerOptions.Filters.Request.CallToolFilters`; this repo scopes the catch to `ArgumentException` with `ParamName == "arguments"` via a named constant so ordinary tool-body `ArgumentException`s are not mislabeled as binding failures.

```csharp
options.AddBindingErrorFilter();
```

Keep the helper in `src/HelixTool.Mcp.Tools/` next to other shared MCP helpers. `McpServerOptionsExtensions.AddBindingErrorFilter` owns the exact filter registration and the binder param-name constant.

**Why this works:**
- ✅ Intercepts `ArgumentException` from SDK reflection-based parameter validation
- ✅ Converts to `McpException` (the designated surfacing channel)
- ✅ Works for all tools automatically (one filter, not per-tool changes)
- ✅ Respects SDK's security boundary
- ✅ Clear ownership: filters handle binding, `McpExceptionHandler` handles runtime

**Result:** SDK binding errors now surface as `McpException` with the actual error message.

---

## Validation Prologue Pattern (Class A / Optional for Combo Rules)

**When:** Add to specific tools for cross-parameter validation the SDK binder can't see.

**Examples of cross-parameter rules:**
- "Exactly one of parameter A or B is required" (not applicable to most tools today)
- "If parameter A is set, parameter B must also be set"
- Named presets or enums that SDK binder doesn't validate

**Implementation:**
```csharp
[McpServerTool("tool_name", Description = "...")]
public async Task<ResultType> ToolName(
    [Description("Parameter A description")] string paramA,
    [Description("Parameter B description")] string? paramB = null)
{
    if (string.IsNullOrEmpty(paramA) && string.IsNullOrEmpty(paramB))
        throw new McpException("At least one of paramA or paramB is required.");

    if (!string.IsNullOrEmpty(paramB) && !ValidEnumValues.Contains(paramB))
        throw new McpException($"paramB must be one of: {string.Join(", ", ValidEnumValues)}");

    return await McpExceptionHandler.RunServiceCallAsync(
        () => _service.CallAsync(paramA, paramB),
        "perform action");
}
```

**Scope after CallToolFilters:**
- Very narrow — only tools with actual multi-parameter validation logic
- Post-v0.7.5 decision: add on case-by-case basis if combo-rule failures emerge

---

## Runtime Exception Handling Pattern (Class B Fix — PR #64)

**When:** Exception thrown during method execution (after invocation, inside method body).

```csharp
return await McpExceptionHandler.RunServiceCallAsync(
    () => service.CallAsync(input),
    "do useful action");
```

The helper:
- ✅ Preserves deliberate `McpException`s (don't double-wrap)
- ✅ Unwraps `AggregateException` from `Task.WhenAll` (PR #64 fix)
- ✅ Catches cancellation (`TaskCanceledException` / `OperationCanceledException`)
- ✅ Converts all other unexpected exceptions into `McpException` with context

**Default known exception types:**
- `InvalidOperationException`
- `HttpRequestException`
- `ArgumentException`
- `TaskCanceledException`
- `OperationCanceledException`

Tool families may extend the known list with a predicate, e.g. Helix adds `HelixException` and `RestApiException`.

---

## Structured MCP Error Shape

MCP SDK 1.3.0 maps thrown `McpException` to a tool-call error (`isError: true`) with text content containing the exception message. Avoid returning `null` for failure paths; throw `McpException` so clients receive a structured, actionable error.

---

## Failure Classification for Triage

When investigating "why didn't the client see this error?":

| Class | Layer | Root Cause | Observable | Fixed By | Handler Reached? |
|-------|-------|-----------|-----------|----------|---|
| **A** | SDK Binding | Parameter name/type mismatch | Real exception in stderr, generic response to client before filter | ✅ CallToolFilters | ❌ No |
| **B** | App Runtime | Exception during method execution | Caught by McpExceptionHandler, structured message | ✅ PR #64 | ✅ Yes |
| **C** | Schema | Tool schema doesn't match method signature or agent expectations | Root cause of Class A failures | ✅ PR #68 schema audit + follow-ups | N/A |

---

## Schema Audit Findings (Class C Prevention, PR #68)

- **Tools audited:** 25 `[McpServerTool]` methods across AzDO, Helix, and CI guidance.
- **Generated `required` arrays:** 25/25 match the C# signature shape.
- **Parameter description coverage:** reflection test now enforces every user-visible parameter has a non-empty `[Description]`.
- **Adequate AzDO/Helix disambiguation:** 10/25 before → 25/25 after.
- **Quick-win fixes shipped:** description-only changes; no production logic, type, or method-name changes.
- **Flagged follow-up:** conditional requirement expressiveness (`workItem` unless URL contains it; `helix_download` URL vs job/work-item inputs) and string-ID numeric binding behavior.

### Schema Audit Checklist

- [x] All `[McpServerTool]` methods have `[Description]` on every user-visible parameter
- [x] Required parameters lack `?` or `= default`
- [x] `tools/list` schema sets generated `required` arrays for actual required params
- [x] Parameter naming drift documented in descriptions:
  - Helix identifiers call out JSON string GUID/URL and "not an AzDO build ID"
  - AzDO identifiers call out JSON string build ID/URL and "not a Helix job ID"

### Before/After Capture Excerpt

Before:

```text
helix_status required=['jobId'] jobId="Helix job ID (GUID), Helix URL, or full work item URL"
azdo_search_timeline required=['buildIdOrUrl','pattern'] buildIdOrUrl="AzDO build ID or full build URL" pattern="Text pattern to search for (case-insensitive)"
azdo_build_analysis required=['buildIdOrUrl'] buildIdOrUrl="AzDO build ID or full build URL"
```

After:

```text
helix_status required=['jobId'] jobId="Helix job ID as a JSON string (GUID), Helix job URL, or full Helix work item URL; not an AzDO build ID"
azdo_search_timeline required=['buildIdOrUrl','pattern'] buildIdOrUrl="AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID" pattern="Case-insensitive text substring to search for; not a regex"
azdo_build_analysis required=['buildIdOrUrl'] buildIdOrUrl="AzDO build ID as a JSON string (for example, '1438863') or full Azure DevOps build URL; not a Helix job ID"
```

Full captures live in `.squad/decisions/inbox/lambert-tools-list-before-2026-05-28.jsonl` and `.squad/decisions/inbox/lambert-tools-list-after-2026-05-28.jsonl`.

---

## Special Messages

Use the optional `getSpecialMessage` callback on `McpExceptionHandler` for domain-specific messages that should replace the generic `Failed to {action}: ...` prefix. AzDO build-not-found paths use this to append org/auth hints while preserving the original exception as the inner exception.

---

## Sites Covered (as of 2026-05-28)

### Layer 1 (CallToolFilters — v0.7.5) ✅ All 25 tools protected
- Call-tool parameter binding filter registered by `McpServerOptionsExtensions.AddBindingErrorFilter` from both MCP startup paths: `src/HelixTool/Program.cs` (stdio `hlx mcp`) and `src/HelixTool.Mcp/Program.cs` (HTTP server).
- Works for all AzDO, Helix, Maestro, and CiKnowledge tools.

### Layer 3 (PR #64 — Runtime Exception Handling) ✅ Complete
- AzDO MCP tools: `azdo_build`, `azdo_builds`, `azdo_timeline`, `azdo_log`, `azdo_changes`, `azdo_test_runs`, `azdo_test_results`, `azdo_artifacts`, `azdo_search_log`, `azdo_search_timeline`, `azdo_test_attachments`, `azdo_helix_jobs`, `azdo_build_analysis`.
- Helix MCP tools with `Task.WhenAll` service paths: `helix_status`, `helix_work_item`, `helix_batch_status`.
- Other Helix MCP service tools use the same helper for consistent error surfacing.
- `helix_ci_guide` uses the sync helper.

### Layer 3 (Optional Per-Tool Prologues — Case-by-Case)
- High-risk combo-rules are handled only when evidence shows the SDK binder cannot express the rule.

---

## Sequencing

### v0.7.5 (Merged)
1. **CallToolFilters middleware** (all tools, SDK binding errors)
2. Unit test + documentation (how filter converts `ArgumentException` → `McpException`)
3. Schema audit captured in PR #68

### v0.7.6+ Follow-ups
1. Conditional schema expressiveness for Helix work-item inputs if the MCP SDK supports safe overrides
2. Per-tool prologues only if combo-rule failures emerge

---

## References

- **Decision document:** `.squad/decisions/inbox/dallas-mcpexception-policy-2026-05-28.md`
- **Ash's investigation:** `.squad/decisions/inbox/ash-silent-mcp-failures-post-v0.7.4-2026-05-28.md`
- **PR #64:** Centralized MCP exception handling for Class B (runtime) errors
- **PR #68:** Schema audit and description-string fixes
- **PR #69:** CallToolFilters implementation merged to main
- **Issue #61:** Original bug report (PR #64 merge gate)
- **Issue #65:** Follow-up work (schema test, flatten exceptions, unskip tests)

---

## Learnings & Calibration

**Why is the CallToolFilters approach cleaner than per-tool prologues?**
- Central implementation (1 filter, not 8–12 per-tool changes)
- Auto-scales to new tools (filter works for all, no new code needed)
- Respects SDK's security design (doesn't try to bypass mechanisms)
- Clear separation: filters handle SDK binding, handlers handle runtime

**When NOT to add per-tool prologues:**
- Don't add prologue for every tool "just in case"
- Wait for evidence of actual combo-rule failures
- Only add when cross-parameter validation is genuinely needed

**When to add per-tool prologues:**
- Cross-parameter combo rules: "exactly one of X or Y"
- Dependent parameters: "if A is set, B must also be set"
- NOT for simple required params (SDK binder handles those)
