using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.AspNetCore;

namespace HelixTool.Tests;

/// <summary>
/// Shared <see cref="Microsoft.AspNetCore.TestHost"/>-based fixture used by T1 (progress
/// notifications survive stateless HTTP transport) and T4 (GET/DELETE return 405 under
/// stateless mode) — see .squad/decisions/inbox/dallas-csharp-mcp-sdk-update.md §6.
///
/// Registers only <see cref="HelixMcpTools"/> against a caller-supplied (substitutable)
/// <see cref="IHelixApiClient"/>, matching the DI shape used in production
/// (src/HelixTool.Mcp/Program.cs: <c>HelixService</c> and <c>IHelixTokenAccessor</c> are
/// constructor-injected), while keeping the smallest coherent test surface — the full
/// <c>WithToolsFromAssembly</c> registration Program.cs uses would also require AzDO
/// dependencies unrelated to these gates.
///
/// This host uses <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> per this repo's
/// existing convention (see ApiKeyMiddlewareTests.cs), not the upstream MCP SDK test suite's
/// heavier KestrelInMemoryTest/KestrelInMemoryConnection fixture — TestServer's in-memory
/// HttpClient can be passed directly into <c>HttpClientTransport</c>, so the same
/// stateless-Streamable-HTTP wire behavior is exercised without needing real sockets.
/// </summary>
internal static class StatelessMcpTestHost
{
    public static async Task<IHost> CreateAsync(IHelixApiClient api)
    {
        return await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(api);
                    services.AddSingleton(sp =>
                        new HelixService(sp.GetRequiredService<IHelixApiClient>(), new HttpClient()));
                    services.AddSingleton<IHelixTokenAccessor>(new EnvironmentHelixTokenAccessor("test-token"));

                    services.AddMcpServer()
                        .WithHttpTransport(options =>
                        {
                            // Mirrors src/HelixTool.Mcp/Program.cs's explicit SessionMode choice.
                            // T3 pins this same value against the real app registration; this
                            // fixture reconstructs it so T1/T4 can exercise a real MCP client
                            // over a real stateless HTTP endpoint.
                            options.SessionMode = HttpServerSessionMode.Stateless;
                        })
                        .WithTools<HelixMcpTools>();
                });
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapMcp());
                });
            })
            .StartAsync();
    }
}
