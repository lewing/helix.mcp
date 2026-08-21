using HelixTool.Core.Helix;
using Microsoft.AspNetCore.TestHost;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// T1 — BLOCKING mandatory gate (.squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md §6):
/// proves that request-scoped progress notifications survive the stateless Streamable HTTP
/// transport end-to-end.
///
/// A real <see cref="McpClient"/>, connected over <see cref="Microsoft.AspNetCore.TestHost.TestServer"/>
/// via <see cref="HttpClientTransport"/>, invokes the real <c>helix_find_files</c> tool
/// (HelixMcpTools.FindFiles) with a progress callback — which causes the SDK to auto-generate
/// <c>_meta.progressToken</c> on the outgoing <c>tools/call</c> request — against an MCP server
/// configured with <c>HttpServerSessionMode.Stateless</c>. It asserts at least one
/// <c>notifications/progress</c> message arrives, and that Progress/Total/Message survive the
/// full stack unmodified: HelixService.FindFilesAsync's <c>ProgressUpdate</c> report →
/// McpProgressAdapter.Wrap → SDK's <c>ProgressNotificationValue</c> → SSE →
/// client-observed <see cref="ProgressNotificationValue"/>.
///
/// This intentionally does NOT unit-test McpProgressAdapter in isolation: the substitution
/// point is <see cref="IHelixApiClient.ListWorkItemFilesAsync"/> (the one HTTP-independent
/// call HelixService.FindFilesAsync's single-work-item fast path makes), so everything above
/// it — HelixMcpTools, HelixService, McpProgressAdapter, the MCP server pipeline, and the
/// stateless HTTP transport — runs unmodified.
///
/// Adapted from the upstream MCP C# SDK v2.2.0 test
/// <c>ProgressNotifications_Work_InStatelessMode</c>
/// (see <see href="https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/tests/ModelContextProtocol.AspNetCore.Tests/StatelessServerTests.cs">StatelessServerTests.cs</see>),
/// which uses a heavier KestrelInMemoryTest fixture and a synthetic echo tool; this version uses
/// Microsoft.AspNetCore.TestHost (this repo's established convention — see
/// ApiKeyMiddlewareTests.cs) and this repo's actual production tool/adapter code instead.
/// </summary>
public class ProgressOverStatelessHttpTests
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "workitem1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task FindFiles_WithWorkItem_EmitsProgressNotifications_OverStatelessHttp()
    {
        // Arrange: IHelixApiClient.ListWorkItemFilesAsync is gated by a TaskCompletionSource so
        // the test can assert the *first* progress notification arrived over the wire before
        // letting the tool call complete. HelixService.FindFilesAsync's single-work-item path
        // reports progress (0,1,"Scanning work item '<workItem>'"), THEN awaits this call, THEN
        // reports (1,1,"Scanned 1 work item (...)"). Without the gate, the tool call — which
        // otherwise does almost no work — can race ahead of the SSE notification flush, making
        // the "at least one notification arrived" assertion flaky or vacuously pass on a slow
        // CI box. This gate is what proves the notification really crossed the stateless
        // per-request SSE stream while the tool call was still in flight, not just that both
        // eventually landed in some order.
        var unblockApiCall = new TaskCompletionSource<IReadOnlyList<IWorkItemFile>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var api = Substitute.For<IHelixApiClient>();
        api.ListWorkItemFilesAsync(WorkItem, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => unblockApiCall.Task);

        using var host = await StatelessMcpTestHost.CreateAsync(api);
        var httpClient = host.GetTestServer().CreateClient();

        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = httpClient.BaseAddress! },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(transport);

        var notifications = new List<ProgressNotificationValue>();
        var firstProgressReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Invokes the handler synchronously (no thread-pool posting), matching the upstream
        // SDK test's SynchronousProgress<T> helper — avoids a second async race between the
        // notification callback and this test awaiting it.
        var progress = new SynchronousProgress<ProgressNotificationValue>(value =>
        {
            lock (notifications) notifications.Add(value);
            firstProgressReceived.TrySetResult();
        });

        // Act: invoke the real tool through the protocol/transport. Passing a non-null
        // IProgress<> is what causes the SDK client to attach _meta.progressToken to the
        // outgoing tools/call request — this is the protocol-level mechanism T1 requires,
        // not a hand-constructed JSON-RPC envelope.
        var callTask = client.CallToolAsync(
            "helix_find_files",
            arguments: new Dictionary<string, object?> { ["jobId"] = JobId, ["workItem"] = WorkItem },
            progress: progress,
            cancellationToken: CancellationToken.None);

        await firstProgressReceived.Task.WaitAsync(Timeout);

        unblockApiCall.SetResult([new FakeWorkItemFile("output.log", "https://example.test/output.log")]);

        var result = await callTask.AsTask().WaitAsync(Timeout);

        // Assert
        Assert.NotEqual(true, result.IsError);

        lock (notifications)
        {
            Assert.True(notifications.Count >= 1,
                "Expected at least one notifications/progress message to survive the " +
                "stateless HTTP transport; received none.");

            var first = notifications[0];
            Assert.Equal(0f, first.Progress);
            Assert.Equal(1f, first.Total);
            Assert.Contains(WorkItem, first.Message);

            // The second report (1/1, "Scanned 1 work item ...") is emitted synchronously
            // before the tool returns, on the same request-scoped SSE stream, so it is
            // expected to arrive too — assert it when present without weakening the "at
            // least one" requirement above if timing ever causes it to be dropped.
            if (notifications.Count >= 2)
            {
                var last = notifications[^1];
                Assert.Equal(1f, last.Progress);
                Assert.Equal(1f, last.Total);
                Assert.Contains("Scanned 1 work item", last.Message);
            }
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private sealed class FakeWorkItemFile(string name, string? link) : IWorkItemFile
    {
        public string Name { get; } = name;
        public string? Link { get; } = link;
    }
}
