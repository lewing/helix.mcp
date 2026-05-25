# MCP Error Surfacing

## Pattern

Use `McpExceptionHandler` at MCP tool entry points for service calls:

```csharp
return await McpExceptionHandler.RunServiceCallAsync(
    () => service.CallAsync(input),
    "do useful action");
```

The helper preserves deliberate `McpException`s, unwraps `AggregateException` from `Task.WhenAll`, catches cancellation (`TaskCanceledException` / `OperationCanceledException`), and converts all other unexpected exceptions into `McpException` with a non-empty message.

## Catch-extension list

Default known exception types:
- `InvalidOperationException`
- `HttpRequestException`
- `ArgumentException`
- `TaskCanceledException`
- `OperationCanceledException`

Tool families may extend the known list with a predicate, e.g. Helix adds `HelixException` and `RestApiException`.

## Structured MCP error shape

MCP SDK 1.3.0 maps thrown `McpException` to a tool-call error (`isError: true`) with text content containing the exception message. Avoid returning `null` for failure paths; throw `McpException` so clients receive a structured, actionable error.

## Special messages

Use the optional `getSpecialMessage` callback for domain-specific messages that should replace the generic `Failed to {action}: ...` prefix. AzDO build-not-found paths use this to append org/auth hints while preserving the original exception as the inner exception.

## Sites covered as of 2026-05-25

- AzDO MCP tools: `azdo_build`, `azdo_builds`, `azdo_timeline`, `azdo_log`, `azdo_changes`, `azdo_test_runs`, `azdo_test_results`, `azdo_artifacts`, `azdo_search_log`, `azdo_search_timeline`, `azdo_test_attachments`, `azdo_helix_jobs`, `azdo_build_analysis`.
- Helix MCP tools with `Task.WhenAll` service paths: `helix_status`, `helix_work_item`, `helix_batch_status`.
- Other Helix MCP service tools use the same helper for consistent error surfacing.
- `helix_ci_guide` uses the sync helper.
