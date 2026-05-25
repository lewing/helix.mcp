# MCP Exception Tests Skill

**Purpose:** Add unhappy-path tests proving MCP tool methods convert service failures into user-visible `McpException` messages instead of silent/null MCP failures.

## Standing rule

Every `[McpServerTool]` method must have at least one direct MCP tool test for an exception path. Prefer service failures over guard-only cases when the tool calls network/file services.

## Test pattern

1. Instantiate the real MCP tool (`AzdoMcpTools`, `HelixMcpTools`, etc.) with mocked service seams (`IAzdoApiClient`, `IHelixApiClient`).
2. Configure the mock to return a faulted task or throw the target exception.
3. Invoke the MCP tool method directly.
4. Assert `McpException` and a non-empty, actionable message containing both the tool action and underlying cause.

```csharp
var ex = await Assert.ThrowsAsync<McpException>(() => tool.Builds());
Assert.False(string.IsNullOrWhiteSpace(ex.Message));
Assert.Contains("list builds", ex.Message, StringComparison.OrdinalIgnoreCase);
Assert.Contains("AzDO unavailable", ex.Message, StringComparison.OrdinalIgnoreCase);
```

## Exception families to cover

- `HttpRequestException`: use `Task.FromException<T>` on the API mock; these should be active tests on current code.
- `AggregateException`: use a faulted task whose exception is an `AggregateException` wrapping the underlying cause; this models Bug B's `Task.WhenAll` boundary.
- `TaskCanceledException` / `OperationCanceledException`: use `Task.FromException<T>` to model timeouts/cancellation; these require centralized MCP exception handling if current per-tool filters do not catch them.

## Skips before centralization

If Ripley's centralized handler has not landed, add contract tests with:

```csharp
[Fact(Skip = "Requires Ripley Bug B MCP exception centralization for issue #61 (...)")]
```

Do not weaken assertions just to make tests pass. The skipped test documents the required contract; unskip after centralization merges.

## Quality bar

A weak test only asserts `Assert.Throws`. A good MCP exception test asserts:

- exception type is `McpException`;
- message is not blank;
- message names the tool action;
- message includes the original failure cause;
- where relevant, the original exception is preserved as `InnerException`.
