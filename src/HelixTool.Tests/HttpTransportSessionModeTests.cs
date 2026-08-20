using HelixTool.Mcp;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// T3 (mandatory gate, .squad/decisions/inbox/dallas-csharp-mcp-sdk-review.md §B3): boots the
/// <em>real</em> HelixTool.Mcp host via <see cref="WebApplicationFactory{TEntryPoint}"/> and
/// asserts that the <see cref="HttpServerTransportOptions"/> it actually registers resolve to
/// <see cref="HttpServerSessionMode.Stateless"/>.
///
/// <para><b>Why the real host.</b> The previous version of this test rebuilt a fresh
/// <c>WebApplicationBuilder</c> and re-applied a hand-copied
/// <c>.WithHttpTransport(o =&gt; o.SessionMode = …)</c> line, so it asserted only that ASP.NET
/// Core options binding works — it could not observe Program.cs at all. This version boots the
/// real entry point, so a mutation of the production registration is detected: changing
/// Program.cs to <c>Stateful</c> fails both tests below, and
/// <c>StatefulForInitializeClients</c> fails the options assertion. (Honest limit: deleting the
/// line outright is currently undetectable, because SDK 2.2.0's own default is also
/// <c>Stateless</c> — no test can distinguish "set explicitly" from "defaulted to the same
/// value" by observing the resolved option. What these tests do guarantee is that the effective
/// value is Stateless <em>however</em> it was arrived at, which is the property clients depend
/// on, and they go red the moment a future SDK moves that default.)</para>
///
/// <para><b>Why pin the value at all.</b> The SDK default already changed once during this
/// migration: 1.4.0's <c>HttpServerTransportOptions.Stateless</c> was a <see cref="bool"/>
/// defaulting to <see langword="false"/> (stateful); 2.2.0 replaced it with the
/// <c>SessionMode</c> enum, whose default is currently <c>Stateless</c>. Relying on a default
/// that has already moved once is not a contract, so Program.cs sets it explicitly and this
/// test holds it there. (A test asserting the SDK's own default value was deliberately deleted:
/// it asserted upstream behavior, not this app's, and would have gone red on an unrelated SDK
/// change while catching no regression in this repo.)</para>
///
/// <para><b>Hermetic against ambient HLX_API_KEY (F1 fix).</b> Booting the real
/// <c>Program</c> also runs <c>app.UseApiKeyAuthIfConfigured()</c>
/// (src/HelixTool.Mcp/Program.cs), which reads the ambient <c>HLX_API_KEY</c> process
/// environment variable at pipeline-build time and, if set, installs
/// <see cref="ApiKeyMiddleware"/> in front of every request. The first version of this test
/// sent no <c>X-Api-Key</c> header, so it was RED on any machine (or CI job) with that variable
/// configured — a gate whose signal depended on the environment rather than on the property
/// under test. The fix: read the same ambient variable the middleware reads and attach the
/// matching header when it is set, so the request always exercises the actual transport-mode
/// behavior instead of failing at the auth gate. This class also joins the shared
/// non-parallel <c>HlxApiKeyEnv</c> collection (<see cref="HlxApiKeyEnvCollection"/>) so no
/// other test can mutate that variable between this class's host being built and its requests
/// being sent. Verified green with the suite run twice: once with <c>HLX_API_KEY</c> exported,
/// once with it unset (see .squad/decisions/inbox/lambert-csharp-mcp-sdk-final-gates.md).</para>
/// </summary>
[Collection("HlxApiKeyEnv")]
public class HttpTransportSessionModeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HttpTransportSessionModeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    /// <summary>
    /// Attaches <see cref="ApiKeyMiddleware.HeaderName"/> to <paramref name="request"/> when the
    /// real host would have required it — i.e. whenever the ambient
    /// <see cref="ApiKeyMiddleware.EnvVarName"/> variable is non-empty, mirroring exactly the
    /// condition <c>UseApiKeyAuthIfConfigured</c> uses to decide whether to install the
    /// middleware at all. When the variable is unset, no header is attached and the host has no
    /// auth middleware installed either, so both worlds reach the transport unauthenticated.
    /// </summary>
    private static void AttachApiKeyIfConfigured(HttpRequestMessage request)
    {
        var configuredKey = Environment.GetEnvironmentVariable(ApiKeyMiddleware.EnvVarName);
        if (!string.IsNullOrEmpty(configuredKey))
            request.Headers.Add(ApiKeyMiddleware.HeaderName, configuredKey);
    }

    [Fact]
    public void RealHost_RegistersStatelessHttpServerTransport()
    {
        using var scope = _factory.Services.CreateScope();

        var resolved = scope.ServiceProvider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value;

        Assert.Equal(HttpServerSessionMode.Stateless, resolved.SessionMode);
    }

    /// <summary>
    /// Behavioral companion to the options assertion: in Stateless mode the server issues no
    /// session, so an <c>initialize</c> response must not carry an <c>Mcp-Session-Id</c> header
    /// (both stateful modes do issue one). This catches a mode change through observable wire
    /// behavior rather than through DI state alone.
    /// </summary>
    [Fact]
    public async Task RealHost_IssuesNoSessionId_ForInitialize()
    {
        using var client = _factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"t3","version":"1.0"}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        AttachApiKeyIfConfigured(request);

        using var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode,
            $"initialize without a session id returned {(int)response.StatusCode} {response.StatusCode}: " +
            $"{await response.Content.ReadAsStringAsync()}.");

        Assert.False(response.Headers.Contains("Mcp-Session-Id"),
            "The host issued an Mcp-Session-Id for initialize, which only the stateful session modes do. " +
            "SessionMode is no longer Stateless.");
    }
}
