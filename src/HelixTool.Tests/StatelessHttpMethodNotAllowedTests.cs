using System.Net;
using HelixTool.Core.Helix;
using Microsoft.AspNetCore.TestHost;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// T4 (mandatory gate, .squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md §6): integration
/// contract asserting GET and DELETE on the mapped MCP endpoint return
/// <see cref="HttpStatusCode.MethodNotAllowed"/> (405) under <c>HttpServerSessionMode.Stateless</c>.
///
/// Per the SDK 2.2.0 XML docs for <c>HttpServerTransportOptions.SessionMode</c>: "the GET,
/// DELETE, and '/sse' endpoints will be disabled" in Stateless mode, because there is no
/// session to resume (GET, for the SSE stream reconnect) or terminate (DELETE) — every request
/// is independent. Mirrors the upstream MCP C# SDK v2.2.0 test
/// <c>EnablingStatelessMode_Disables_GetAndDeleteEndpoints</c>
/// (see <see href="https://github.com/modelcontextprotocol/csharp-sdk/blob/v2.2.0/tests/ModelContextProtocol.AspNetCore.Tests/StatelessServerTests.cs">StatelessServerTests.cs</see>),
/// adapted onto this repo's Microsoft.AspNetCore.TestHost convention and real HelixMcpTools registration via
/// <see cref="StatelessMcpTestHost"/> (shared with T1) instead of the upstream's synthetic
/// tool + KestrelInMemoryTest fixture.
/// </summary>
public class StatelessHttpMethodNotAllowedTests
{
    [Fact]
    public async Task Get_OnMappedMcpEndpoint_Returns405_UnderStatelessMode()
    {
        using var host = await StatelessMcpTestHost.CreateAsync(Substitute.For<IHelixApiClient>());
        var client = host.GetTestServer().CreateClient();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Delete_OnMappedMcpEndpoint_Returns405_UnderStatelessMode()
    {
        using var host = await StatelessMcpTestHost.CreateAsync(Substitute.For<IHelixApiClient>());
        var client = host.GetTestServer().CreateClient();

        var response = await client.DeleteAsync("/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }
}
