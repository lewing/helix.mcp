using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

/// <summary>
/// F3 / G7 (.squad/decisions/inbox/dallas-csharp-mcp-sdk-final-review.md) — BLOCKING gate that
/// was never delivered by a prior artifact. Boots the <em>real</em> HelixTool.Mcp host via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> (not <see cref="StatelessMcpTestHost"/>'s
/// hand-reconstructed singleton host — see that file's header for why that fixture is
/// unsuitable for this proof) and exercises the real production middleware pipeline and DI
/// container end-to-end, proving three things a mocked/singleton host cannot:
///
/// <list type="number">
/// <item>with API-key auth deterministically enabled (a fixed test key, not depending on the
/// ambient environment), a missing or incorrect <c>X-Api-Key</c> header is rejected with 401,
/// and the correct key proceeds;</item>
/// <item>two independent, sequential, authenticated MCP requests carry different caller
/// <c>Authorization: Bearer</c> tokens end-to-end;</item>
/// <item>each request resolves its own token through the real, unmodified
/// <see cref="HttpContextHelixTokenAccessor"/> (scoped to that request's <see
/// cref="Microsoft.AspNetCore.Http.HttpContext"/>) and that token maps to its own
/// <see cref="CacheOptions.ComputeTokenHash"/> partition, with the second request's recorded
/// token/hash carrying none of the first request's state.</item>
/// </list>
///
/// <para><b>How isolation is observed without changing production architecture.</b>
/// <see cref="ApiKeySmokeTestFactory"/> replaces only the two DI seams Program.cs itself already
/// exposes as request-scoped extensibility points — <see cref="IHelixApiClientFactory"/> and
/// <see cref="ICacheStoreFactory"/> — with recording test doubles, via the supported
/// <see cref="WebApplicationFactory{TEntryPoint}.ConfigureWebHost"/>/<c>ConfigureServices</c>
/// override and <c>RemoveAll&lt;T&gt;</c> + <c>AddSingleton</c>. Everything upstream of those
/// seams — <see cref="ApiKeyMiddleware"/>, ASP.NET Core routing, the MCP server pipeline,
/// <see cref="HttpContextHelixTokenAccessor"/>, and the scoped <c>CacheOptions</c>/<c>IHelixApiClient</c>
/// registrations in Program.cs — runs completely unmodified. No production code was changed to
/// make this test possible.</para>
///
/// <para><b>Why the auth-gating fact never calls a tool.</b> <see cref="MissingApiKey_Returns401"/>,
/// <see cref="IncorrectApiKey_Returns401"/> and <see cref="CorrectApiKey_Proceeds"/> only send a
/// raw <c>initialize</c> request (same pattern as <see cref="HttpTransportSessionModeTests"/>) —
/// never a <c>tools/call</c>. <c>HelixMcpTools</c>/<c>IHelixApiClientFactory</c>/
/// <c>ICacheStoreFactory</c> are only DI-resolved when a tool call actually dispatches to
/// <c>helix_status</c>, not during the protocol handshake — confirmed empirically below by the
/// isolation fact asserting *exactly* two recorded tokens/hashes (not more), even though four
/// facts share the same <see cref="ApiKeySmokeTestFactory"/> instance and host.</para>
///
/// <para><b>Deterministic auth, independent of ambient HLX_API_KEY.</b>
/// <see cref="ApiKeySmokeTestFactory"/> sets <see cref="ApiKeyMiddleware.EnvVarName"/> to a fixed
/// test key in its constructor (before the host is ever built) and restores whatever the ambient
/// value was on disposal, so this class's outcome never depends on whether the process's real
/// <c>HLX_API_KEY</c> happens to be set. It still joins the shared non-parallel
/// <c>HlxApiKeyEnv</c> collection (<see cref="HlxApiKeyEnvCollection"/>) so no other class can
/// observe or race this mutation. Verified green with the suite run twice: once with
/// <c>HLX_API_KEY</c> exported, once with it unset (see
/// .squad/decisions/inbox/lambert-csharp-mcp-sdk-final-gates.md).</para>
/// </summary>
[Collection("HlxApiKeyEnv")]
public class ApiKeyScopedRequestIsolationTests : IClassFixture<ApiKeyScopedRequestIsolationTests.ApiKeySmokeTestFactory>
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

    private readonly ApiKeySmokeTestFactory _factory;

    public ApiKeyScopedRequestIsolationTests(ApiKeySmokeTestFactory factory) => _factory = factory;

    private static HttpRequestMessage BuildInitializeRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"g7","version":"1.0"}}}
                """,
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        return request;
    }

    [Fact]
    public async Task MissingApiKey_Returns401()
    {
        using var client = _factory.CreateClient();
        using var request = BuildInitializeRequest();

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task IncorrectApiKey_Returns401()
    {
        using var client = _factory.CreateClient();
        using var request = BuildInitializeRequest();
        request.Headers.Add(ApiKeyMiddleware.HeaderName, "definitely-the-wrong-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CorrectApiKey_Proceeds()
    {
        using var client = _factory.CreateClient();
        using var request = BuildInitializeRequest();
        request.Headers.Add(ApiKeyMiddleware.HeaderName, ApiKeySmokeTestFactory.TestApiKey);

        using var response = await client.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode,
            $"initialize with the correct X-Api-Key returned {(int)response.StatusCode} " +
            $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}.");
    }

    /// <summary>
    /// F3 points 2 and 3: two independent, sequential, authenticated MCP <c>tools/call</c>
    /// requests against the real host, each with its own caller bearer token. Asserts both the
    /// production token resolution (<see cref="HttpContextHelixTokenAccessor"/> →
    /// <see cref="IHelixApiClientFactory.Create"/>) and the production cache partitioning
    /// (<see cref="CacheOptions.ComputeTokenHash"/> → <see cref="ICacheStoreFactory.GetOrCreate"/>)
    /// are computed fresh per request, with request 2 observing none of request 1's state.
    /// </summary>
    [Fact]
    public async Task TwoConsecutiveRequests_ResolveIndependentTokensAndCachePartitions()
    {
        const string tokenA = "caller-bearer-token-AAAA-1111";
        const string tokenB = "caller-bearer-token-BBBB-2222";

        var resultA = await CallHelixStatusAsync(tokenA);
        Assert.NotEqual(true, resultA.IsError);

        // "Consecutive": request A has fully completed (awaited above) before request B starts.
        var resultB = await CallHelixStatusAsync(tokenB);
        Assert.NotEqual(true, resultB.IsError);

        var observedTokens = _factory.ApiClientFactory.ObservedTokens;
        var observedHashes = _factory.CacheStoreFactory.ObservedCacheRootHashes;

        // Exactly two calls recorded — the three auth-gating facts above never reach a tool
        // dispatch, so if this were ever more/fewer than 2 it would mean either a fact ordering
        // assumption broke or a request was retried/duplicated somewhere in the pipeline.
        Assert.Equal(2, observedTokens.Count);
        Assert.Equal(2, observedHashes.Count);

        // Point 2: each request's own caller token was resolved, in request order, by the real
        // HttpContextHelixTokenAccessor — not a shared/stale value.
        Assert.Equal(tokenA, observedTokens[0]);
        Assert.Equal(tokenB, observedTokens[1]);
        Assert.NotEqual(observedTokens[0], observedTokens[1]);

        // Point 3: each request's token mapped to its own CacheOptions.ComputeTokenHash
        // partition, matching what Program.cs itself computes for that token — and request 2's
        // partition is distinct from request 1's, i.e. it inherited none of request 1's cache
        // scoping.
        var expectedHashA = CacheOptions.ComputeTokenHash(tokenA);
        var expectedHashB = CacheOptions.ComputeTokenHash(tokenB);
        Assert.Equal(expectedHashA, observedHashes[0]);
        Assert.Equal(expectedHashB, observedHashes[1]);
        Assert.NotEqual(observedHashes[0], observedHashes[1]);
    }

    private async Task<ModelContextProtocol.Protocol.CallToolResult> CallHelixStatusAsync(string bearerToken)
    {
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, ApiKeySmokeTestFactory.TestApiKey);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = httpClient.BaseAddress! },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);

        await using var client = await McpClient.CreateAsync(transport);

        return await client.CallToolAsync(
            "helix_status",
            arguments: new Dictionary<string, object?> { ["jobId"] = JobId },
            cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Records every access token <see cref="IHelixApiClientFactory.Create"/> was invoked with,
    /// in call order, and returns a minimally-configured fake <see cref="IHelixApiClient"/> whose
    /// <c>GetJobDetailsAsync</c>/<c>ListWorkItemsAsync</c> are just enough for
    /// <c>HelixService.GetJobStatusAsync</c> (the implementation behind the <c>helix_status</c>
    /// tool) to succeed without any further Helix API dependency.
    /// </summary>
    public sealed class RecordingHelixApiClientFactory : IHelixApiClientFactory
    {
        private readonly ConcurrentQueue<string?> _observedTokens = new();

        public IReadOnlyList<string?> ObservedTokens => [.. _observedTokens];

        public IHelixApiClient Create(string? accessToken)
        {
            _observedTokens.Enqueue(accessToken);

            var jobDetails = Substitute.For<IJobDetails>();
            jobDetails.Name.Returns("g7-smoke-job");
            jobDetails.QueueId.Returns("g7.smoke.queue");
            jobDetails.QueueAlias.Returns("g7.smoke.queue.alias");
            jobDetails.Creator.Returns("lambert-g7-smoke");
            jobDetails.Source.Returns("pr/lambert/helix.mcp/g7-smoke");
            jobDetails.Created.Returns("2026-08-20T00:00:00Z");
            jobDetails.Finished.Returns("2026-08-20T00:05:00Z");

            var fake = Substitute.For<IHelixApiClient>();
            fake.GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(jobDetails);
            fake.ListWorkItemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<IWorkItemSummary>());
            return fake;
        }
    }

    /// <summary>
    /// Records every <see cref="CacheOptions.CacheRootHash"/> partition
    /// <see cref="ICacheStoreFactory.GetOrCreate"/> was invoked with, in call order, and returns
    /// a fully in-memory no-op <see cref="ICacheStore"/> so this smoke test performs no real
    /// disk/SQLite I/O.
    /// </summary>
    public sealed class RecordingCacheStoreFactory : ICacheStoreFactory
    {
        private readonly ConcurrentQueue<string?> _observedCacheRootHashes = new();

        public IReadOnlyList<string?> ObservedCacheRootHashes => [.. _observedCacheRootHashes];

        public ICacheStore GetOrCreate(CacheOptions options)
        {
            _observedCacheRootHashes.Enqueue(options.CacheRootHash);
            return new NoOpCacheStore();
        }

        private sealed class NoOpCacheStore : ICacheStore
        {
            public Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default) =>
                Task.FromResult<string?>(null);

            public Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default) =>
                Task.FromResult<Stream?>(null);

            public Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default) =>
                Task.FromResult<bool?>(null);

            public Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default) =>
                Task.CompletedTask;

            public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;

            public Task<CacheStatus> GetStatusAsync(CancellationToken ct = default) =>
                Task.FromResult(new CacheStatus(0, 0, 0, null, null, 0));

            public Task EvictExpiredAsync(CancellationToken ct = default) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }

    /// <summary>
    /// Real <see cref="Program"/> host with API-key auth deterministically enabled (independent
    /// of the ambient environment) and the two request-scoped Helix DI seams replaced with
    /// recording test doubles via supported <c>ConfigureWebHost</c>/<c>ConfigureServices</c>
    /// overrides. No production code changed.
    /// </summary>
    public sealed class ApiKeySmokeTestFactory : WebApplicationFactory<Program>
    {
        public const string TestApiKey = "lambert-g7-smoke-api-key";

        private readonly string? _originalApiKey;

        public RecordingHelixApiClientFactory ApiClientFactory { get; } = new();

        public RecordingCacheStoreFactory CacheStoreFactory { get; } = new();

        public ApiKeySmokeTestFactory()
        {
            // Set *before* the host is built (WebApplicationFactory builds lazily, on first
            // Services/CreateClient access) so Program.cs's UseApiKeyAuthIfConfigured() — which
            // reads this variable once, at pipeline-build time — deterministically installs
            // ApiKeyMiddleware, regardless of what the ambient HLX_API_KEY happens to be.
            _originalApiKey = Environment.GetEnvironmentVariable(ApiKeyMiddleware.EnvVarName);
            Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, TestApiKey);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHelixApiClientFactory>();
                services.AddSingleton<IHelixApiClientFactory>(ApiClientFactory);

                services.RemoveAll<ICacheStoreFactory>();
                services.AddSingleton<ICacheStoreFactory>(CacheStoreFactory);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, _originalApiKey);
        }
    }
}
