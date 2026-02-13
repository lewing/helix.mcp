using HelixTool.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HelixTool.Tests;

public class ApiKeyMiddlewareTests : IDisposable
{
    private string? _savedApiKey;

    public ApiKeyMiddlewareTests()
    {
        _savedApiKey = Environment.GetEnvironmentVariable(ApiKeyMiddleware.EnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, _savedApiKey);
    }

    private static async Task<IHost> CreateHost(string apiKey)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.Configure(app =>
                {
                    app.UseMiddleware<ApiKeyMiddleware>(apiKey);
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = 200;
                        await ctx.Response.WriteAsync("OK");
                    });
                });
            })
            .StartAsync();
        return host;
    }

    [Fact]
    public async Task ValidApiKey_Returns200()
    {
        using var host = await CreateHost("test-secret-key");
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ApiKeyMiddleware.HeaderName, "test-secret-key");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MissingApiKey_Returns401()
    {
        using var host = await CreateHost("test-secret-key");
        var client = host.GetTestClient();

        var response = await client.GetAsync("/");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongApiKey_Returns401()
    {
        using var host = await CreateHost("test-secret-key");
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ApiKeyMiddleware.HeaderName, "wrong-key");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task EmptyApiKeyHeader_Returns401()
    {
        using var host = await CreateHost("test-secret-key");
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ApiKeyMiddleware.HeaderName, "");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeysAreCaseSensitive()
    {
        using var host = await CreateHost("MySecretKey");
        var client = host.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add(ApiKeyMiddleware.HeaderName, "mysecretkey");

        var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Extension method tests ---

    [Fact]
    public void UseApiKeyAuthIfConfigured_NoEnvVar_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, null);

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var result = app.UseApiKeyAuthIfConfigured();

        Assert.False(result);
    }

    [Fact]
    public void UseApiKeyAuthIfConfigured_EmptyEnvVar_ReturnsFalse()
    {
        Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, "");

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var result = app.UseApiKeyAuthIfConfigured();

        Assert.False(result);
    }

    [Fact]
    public void UseApiKeyAuthIfConfigured_WithEnvVar_ReturnsTrue()
    {
        Environment.SetEnvironmentVariable(ApiKeyMiddleware.EnvVarName, "some-key");

        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        var result = app.UseApiKeyAuthIfConfigured();

        Assert.True(result);
    }

    [Fact]
    public async Task Middleware_Returns401Body()
    {
        using var host = await CreateHost("key");
        var client = host.GetTestClient();

        var response = await client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Invalid or missing API key", body);
    }

    [Fact]
    public async Task Middleware_Returns401ContentType()
    {
        using var host = await CreateHost("key");
        var client = host.GetTestClient();

        var response = await client.GetAsync("/");

        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
    }
}
