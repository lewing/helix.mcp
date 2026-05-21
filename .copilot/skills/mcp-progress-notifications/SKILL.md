# Skill: Emitting MCP Progress Notifications (ModelContextProtocol C# SDK ≥ 1.3.0)

## When to apply
A `[McpServerTool]` method routinely takes more than ~1 second
(downloads, multi-step API scans, multi-file processing). The MCP client
needs interim progress so its UI doesn't appear hung.

## The pattern (server side, C#)

```csharp
[McpServerTool(Name = "long_running_thing")]
public async Task<MyResult> Do(
    string someArg,
    int batchSize = 100,
    // Auto-injected by the SDK. Parameter is *omitted from the tool's
    // JSON schema* — clients never see it.
    IProgress<ProgressNotificationValue>? progress = null)
{
    int total = ComputeTotal();
    int step  = Math.Max(1, total / 10);   // ~10 emits over the run

    progress?.Report(new ProgressNotificationValue
    {
        Progress = 0, Total = total,
        Message = $"Starting {total} item(s)",
    });

    int done = 0;
    foreach (var item in items)
    {
        await ProcessAsync(item);
        done++;
        if (done % step == 0 || done == total)
        {
            progress?.Report(new ProgressNotificationValue
            {
                Progress = done, Total = total,
                Message  = $"Processed {done} of {total}",
            });
        }
    }
    return result;
}
```

### Key facts about the SDK behavior
- The SDK auto-injects `IProgress<ProgressNotificationValue>` for any
  parameter of that type. **You don't register or wire anything.**
- If the client included `_meta.progressToken` in `tools/call`, every
  `Report` becomes a `notifications/progress` JSON-RPC message tagged
  with that token.
- **If the client did NOT supply a progress token**, the injected sink
  is a no-op. `progress?.Report(...)` is always safe; never branch on
  "did the client want progress?" — just always call it.
- `ProgressNotificationValue` has init-only `float Progress`,
  `float? Total`, `string? Message`. Use object-initializer syntax.
- The escape hatch `McpSession.NotifyProgressAsync(ProgressToken, …)`
  exists for code that has a `RequestContext` but isn't inside a tool
  method's parameter frame. Don't reach for it from a tool method.

## Granularity rules (don't violate)
- **5–10 emits per long run.** Not per-item, not per-byte.
  `step = max(1, total / 10)`.
- For streaming bytes with known `Content-Length`: emit every 10% of
  total. Without `Content-Length`: every 1 MiB.
- Add a wall-clock throttle (≥ 250 ms between emits) so very fast
  small inputs don't flood the transport.
- Always emit one final 100% so the client can dismiss its progress UI.

## Always include a human-readable `Message`
The numeric pair drives progress bars *eventually*; the message is what
shows up in clients today (Claude Desktop, VS Code, mcp-inspector). Make
it parseable at a glance: `"Downloaded 42 of 128 MB"`,
`"Searched 12 of 50 log steps (3 matches)"`.

## Keeping the service layer transport-agnostic
Don't put `ModelContextProtocol` types in `HelixTool.Core` or any other
shared lib. Define a small domain record:

```csharp
// HelixTool.Core
public readonly record struct ProgressUpdate(
    double Current, double? Total, string? Message);
```

…and put a tiny adapter in the MCP tool layer:

```csharp
internal static class McpProgressAdapter
{
    public static IProgress<ProgressUpdate>? Wrap(
        IProgress<ProgressNotificationValue>? mcp)
        => mcp is null ? null : new Adapter(mcp);

    private sealed class Adapter(IProgress<ProgressNotificationValue> mcp)
        : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate v) => mcp.Report(new()
        {
            Progress = (float)v.Current,
            Total    = v.Total is null ? null : (float)v.Total.Value,
            Message  = v.Message,
        });
    }
}
```

Service methods take `IProgress<ProgressUpdate>?`, MCP tools translate at
the boundary. CLI callers (and unit tests) just pass `null`.

## Signature placement convention (this repo)
Put the new optional `IProgress<ProgressUpdate>?` parameter **before**
`CancellationToken cancellationToken = default`, so CT stays visually
last. Existing positional callers that passed `(…, cancellationToken)`
must be migrated to named args (`progress: null, cancellationToken`).

## Verification checklist
1. `dotnet build` clean.
2. `dotnet test` clean (existing tests should not regress — they pass
   `null` to the new optional param implicitly).
3. Tools that fetch in a single shot (no streaming, no per-item loop)
   should NOT be instrumented — the spec is for *long-running* ops.
4. Smoke-test with a real client (Claude Desktop, mcp-inspector, or a
   small SDK script using `McpClientTool.WithProgress(...)`) — confirm
   notifications arrive and counts match the granularity rule.

## References
- Tools instrumented in this repo: `helix_download` (bytes & files),
  `azdo_search_log` (log steps), `helix_find_files` (work items).
- Decision: `.squad/decisions/inbox/ripley-progress-notifications.md`
- Issue: #43
