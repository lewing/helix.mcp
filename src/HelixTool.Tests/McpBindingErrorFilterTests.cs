using HelixTool.Mcp.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class McpBindingErrorFilterTests
{
    private const string ToolName = "helix_status";

    [Fact]
    public async Task BindingArgumentException_IsConvertedToMcpException()
    {
        var inner = new ArgumentException("missing 'jobId'", "arguments");
        var handler = CreateFilteredHandler(inner);
        var request = CreateRequestContext();

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Same(inner, ex.InnerException);
        Assert.Contains($"Parameter binding error for '{ToolName}'", ex.Message);
        Assert.Contains(inner.Message, ex.Message);
    }

    [Fact]
    public async Task NonBindingArgumentException_IsNotConverted()
    {
        var inner = new ArgumentException("bad value", "userInput");
        var handler = CreateFilteredHandler(inner);
        var request = CreateRequestContext();

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler(request, CancellationToken.None));

        Assert.Same(inner, ex);
    }

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> CreateFilteredHandler(Exception exception)
    {
        var options = new McpServerOptions().AddBindingErrorFilter();
        var filter = Assert.Single(options.Filters.Request.CallToolFilters);

        return filter((_, _) => throw exception);
    }

    private static RequestContext<CallToolRequestParams> CreateRequestContext()
        => new(
            server: Substitute.For<McpServer>(),
            jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" },
            parameters: new CallToolRequestParams { Name = ToolName });
}
