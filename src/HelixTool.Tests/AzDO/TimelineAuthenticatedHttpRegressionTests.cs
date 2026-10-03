using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Mcp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class TimelineAuthenticatedHttpRegressionTests
{
    [Fact]
    public async Task Dallas2_ProductionHost_FreshAuthenticatedRequests_ReadOwnEvidenceAndDenyOtherIdentity()
    {
        using var factory = new AuthenticatedTimelineFactory();
        JsonElement descriptor;
        JsonElement action;
        using (var http = factory.CreateAuthenticatedClient("alpha"))
        {
            await using var transport = new HttpClientTransport(new() { Endpoint = http.BaseAddress! }, http);
            await using var client = await McpClient.CreateAsync(transport);
            var result = await client.CallToolAsync("azdo_timeline", new Dictionary<string, object?>
            {
                ["buildIdOrUrl"] = "1621192", ["filter"] = "all", ["projection"] = "full",
                ["expand"] = "none", ["all"] = true, ["limit"] = 2000
            });
            var receipt = TimelineViewHost.Payload(result);
            descriptor = receipt.GetProperty("delivery").Clone();
            action = receipt.GetProperty("continuation").Clone();
            Assert.True(descriptor.GetProperty("complete").GetBoolean());
            Assert.Equal(1524, receipt.GetProperty("selectedTotal").GetInt32());
        }

        // A new MCP client/HTTP request must resolve credentials again; it cannot inherit
        // AuthTokenHash from the timeline request's now-disposed scoped CacheOptions.
        using (var http = factory.CreateAuthenticatedClient("alpha"))
        {
            await using var transport = new HttpClientTransport(new() { Endpoint = http.BaseAddress! }, http);
            await using var client = await McpClient.CreateAsync(transport);
            var result = await client.CallToolAsync(action.GetProperty("tool").GetString()!,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(action.GetProperty("arguments").GetRawText())!);
            var read = TimelineViewHost.Payload(result);
            Assert.True(read.GetProperty("returnedBytes").GetInt64() > 0);
            Assert.True(read.GetProperty("sourceComplete").GetBoolean());
            Assert.Equal(descriptor.GetProperty("sha256").GetString(), read.GetProperty("sha256").GetString());
        }

        // The Helix bearer/cache-root token stays the same; only the AzDO credential
        // changes. Denial must therefore come from the evidence partition itself.
        using (var http = factory.CreateAuthenticatedClient("beta"))
        {
            await using var transport = new HttpClientTransport(new() { Endpoint = http.BaseAddress! }, http);
            await using var client = await McpClient.CreateAsync(transport);
            var denied = await client.CallToolAsync("hlx_read_evidence", new Dictionary<string, object?>
            {
                ["evidenceId"] = descriptor.GetProperty("evidenceId").GetString()
            });
            Assert.True(denied.IsError);
        }

        var observed = factory.Options.ToArray();
        Assert.True(observed.Length >= 3);
        Assert.Equal(observed.Length, observed.Select(o => o.Options).Distinct(ReferenceEqualityComparer.Instance).Count());
        var alpha = observed.Where(o => o.Identity == "alpha").ToArray();
        Assert.True(alpha.Length >= 2);
        Assert.All(alpha, o => Assert.Equal(CacheOptions.ComputeAuthContextHash("fixture-credential-alpha"), o.Options.AuthTokenHash));
        Assert.All(observed.Where(o => o.Identity == "beta"),
            o => Assert.Equal(CacheOptions.ComputeAuthContextHash("fixture-credential-beta"), o.Options.AuthTokenHash));
    }

    private sealed class AuthenticatedTimelineFactory : WebApplicationFactory<Program>
    {
        private const string IdentityHeader = "X-Test-Azdo-Identity";
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-auth-timeline-{Guid.NewGuid():N}");
        private readonly string? _oldEval = Environment.GetEnvironmentVariable("HLX_EVAL_SNAPSHOT");
        private readonly string _timelineJson = JsonSerializer.Serialize(TimelineViewFixture.FullReal(1621192));
        public ConcurrentQueue<(string Identity, CacheOptions Options)> Options { get; } = new();

        public AuthenticatedTimelineFactory() => Environment.SetEnvironmentVariable("HLX_EVAL_SNAPSHOT", null);

        public HttpClient CreateAuthenticatedClient(string identity)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "shared-helix-fixture-token");
            client.DefaultRequestHeaders.Add(IdentityHeader, identity);
            var key = Environment.GetEnvironmentVariable(ApiKeyMiddleware.EnvVarName);
            if (!string.IsNullOrEmpty(key))
                client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, key);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // Keep production CacheOptions scope/identity construction; relocate only
                // its physical storage so this test cannot touch the user's real cache.
                var original = Assert.Single(services, d => d.ServiceType == typeof(CacheOptions));
                services.RemoveAll<CacheOptions>();
                services.AddScoped(sp =>
                {
                    var options = (CacheOptions)original.ImplementationFactory!(sp);
                    options.CacheRoot = _root;
                    var identity = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Request.Headers[IdentityHeader].ToString();
                    Options.Enqueue((identity, options));
                    return options;
                });
                services.RemoveAll<IAzdoTokenAccessor>();
                services.AddScoped<IAzdoTokenAccessor>(sp =>
                {
                    var identity = sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Request.Headers[IdentityHeader].ToString();
                    var accessor = Substitute.For<IAzdoTokenAccessor>();
                    accessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns(new AzdoCredential($"fixture-token-{identity}", "Bearer", "fixture")
                    {
                        CacheIdentity = $"fixture-credential-{identity}"
                    });
                    return accessor;
                });
                services.RemoveAll<IHelixApiClientFactory>();
                var helix = Substitute.For<IHelixApiClientFactory>();
                helix.Create(Arg.Any<string?>()).Returns(Substitute.For<IHelixApiClient>());
                services.AddSingleton(helix);
                services.AddHttpClient("AzDO").ConfigurePrimaryHttpMessageHandler(() => new ProviderHandler(_timelineJson));
            });
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                Environment.SetEnvironmentVariable("HLX_EVAL_SNAPSHOT", _oldEval);
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class ProviderHandler(string timelineJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/timeline", StringComparison.Ordinal)
                ? timelineJson
                : path.EndsWith("/builds/1621192", StringComparison.Ordinal)
                    ? """{"id":1621192,"status":"completed","result":"failed"}"""
                    : throw new InvalidOperationException($"Unexpected provider request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
