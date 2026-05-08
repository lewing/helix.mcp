using HelixTool.Core;
using ModelContextProtocol;

namespace HelixTool.Mcp.Tools;

/// <summary>
/// Adapts the MCP SDK's <see cref="IProgress{T}"/> of <see cref="ProgressNotificationValue"/>
/// (which it auto-injects into tool methods when the client supplies a progress token)
/// to the transport-agnostic <see cref="ProgressUpdate"/> consumed by service-layer code.
/// </summary>
internal static class McpProgressAdapter
{
    /// <summary>
    /// Returns an <see cref="IProgress{ProgressUpdate}"/> that forwards to <paramref name="mcp"/>
    /// as <see cref="ProgressNotificationValue"/> notifications. Returns <c>null</c> when the
    /// MCP sink is itself <c>null</c> (i.e. the client did not include a progress token), so
    /// callers can pass <c>null</c> straight through to service methods with no overhead.
    /// </summary>
    public static IProgress<ProgressUpdate>? Wrap(IProgress<ProgressNotificationValue>? mcp)
        => mcp is null ? null : new Adapter(mcp);

    private sealed class Adapter : IProgress<ProgressUpdate>
    {
        private readonly IProgress<ProgressNotificationValue> _mcp;
        public Adapter(IProgress<ProgressNotificationValue> mcp) => _mcp = mcp;

        public void Report(ProgressUpdate value)
        {
            _mcp.Report(new ProgressNotificationValue
            {
                Progress = (float)value.Current,
                Total = value.Total is null ? null : (float)value.Total.Value,
                Message = value.Message,
            });
        }
    }
}
