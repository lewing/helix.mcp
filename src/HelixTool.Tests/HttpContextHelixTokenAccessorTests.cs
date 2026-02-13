// Tests for HttpContextHelixTokenAccessor (L-HTTP-5).
// HttpContextHelixTokenAccessor reads token from HTTP Authorization header,
// falls back to HELIX_ACCESS_TOKEN env var. Lives in HelixTool.Mcp.
// Will compile once Ripley's HttpContextHelixTokenAccessor lands.

using HelixTool.Core;
using HelixTool.Mcp;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HelixTool.Tests;

public class HttpContextHelixTokenAccessorTests : IDisposable
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private string? _savedEnvVar;

    public HttpContextHelixTokenAccessorTests()
    {
        _httpContextAccessor = new HttpContextAccessor();
        _savedEnvVar = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
        // Clear env var by default so tests are isolated
        Environment.SetEnvironmentVariable("HELIX_ACCESS_TOKEN", null);
    }

    public void Dispose()
    {
        // Restore original env var
        Environment.SetEnvironmentVariable("HELIX_ACCESS_TOKEN", _savedEnvVar);
    }

    private HttpContextHelixTokenAccessor CreateAccessor()
    {
        return new HttpContextHelixTokenAccessor(_httpContextAccessor);
    }

    private void SetAuthorizationHeader(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["Authorization"] = value;
        _httpContextAccessor.HttpContext = context;
    }

    private void SetNoHttpContext()
    {
        _httpContextAccessor.HttpContext = null;
    }

    // --- 1. Bearer token extraction ---

    [Fact]
    public void GetAccessToken_BearerToken_ReturnsToken()
    {
        SetAuthorizationHeader("Bearer mytoken123");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("mytoken123", result);
    }

    // --- 2. Token format extraction ---

    [Fact]
    public void GetAccessToken_TokenFormat_ReturnsToken()
    {
        SetAuthorizationHeader("token mytoken456");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("mytoken456", result);
    }

    // --- 3. No auth header, env var set ---

    [Fact]
    public void GetAccessToken_NoAuthHeader_EnvVarSet_ReturnsEnvVar()
    {
        Environment.SetEnvironmentVariable("HELIX_ACCESS_TOKEN", "env-token-value");
        var context = new DefaultHttpContext();
        // No Authorization header set
        _httpContextAccessor.HttpContext = context;

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("env-token-value", result);
    }

    // --- 4. No auth header, no env var ---

    [Fact]
    public void GetAccessToken_NoAuthHeader_NoEnvVar_ReturnsNull()
    {
        var context = new DefaultHttpContext();
        _httpContextAccessor.HttpContext = context;

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Null(result);
    }

    [Fact]
    public void GetAccessToken_NoHttpContext_NoEnvVar_ReturnsNull()
    {
        SetNoHttpContext();

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Null(result);
    }

    // --- 5. Empty Authorization header ---

    [Fact]
    public void GetAccessToken_EmptyAuthHeader_EnvVarSet_FallsBackToEnvVar()
    {
        Environment.SetEnvironmentVariable("HELIX_ACCESS_TOKEN", "fallback-token");
        SetAuthorizationHeader("");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("fallback-token", result);
    }

    [Fact]
    public void GetAccessToken_EmptyAuthHeader_NoEnvVar_ReturnsNull()
    {
        SetAuthorizationHeader("");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Null(result);
    }

    // --- 6. Case insensitivity of Bearer ---

    [Theory]
    [InlineData("bearer lowercase-token")]
    [InlineData("BEARER uppercase-token")]
    [InlineData("Bearer mixedcase-token")]
    [InlineData("beArEr weird-case-token")]
    public void GetAccessToken_BearerCaseInsensitive_ReturnsToken(string headerValue)
    {
        SetAuthorizationHeader(headerValue);

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        // Extract expected token from the header value after the space
        var expectedToken = headerValue.Split(' ', 2)[1];
        Assert.Equal(expectedToken, result);
    }

    [Theory]
    [InlineData("TOKEN case-token")]
    [InlineData("Token case-token2")]
    public void GetAccessToken_TokenCaseInsensitive_ReturnsToken(string headerValue)
    {
        SetAuthorizationHeader(headerValue);

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        var expectedToken = headerValue.Split(' ', 2)[1];
        Assert.Equal(expectedToken, result);
    }

    // --- 7. Whitespace handling ---

    [Fact]
    public void GetAccessToken_ExtraSpacesAroundToken_ReturnsTrimmedToken()
    {
        SetAuthorizationHeader("Bearer   spaced-token   ");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("spaced-token", result);
    }

    [Fact]
    public void GetAccessToken_TabsAroundToken_ReturnsTrimmedToken()
    {
        SetAuthorizationHeader("Bearer \t tabbed-token \t");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("tabbed-token", result);
    }

    // --- 8. Malformed auth header ---

    [Fact]
    public void GetAccessToken_BearerWithNoToken_ReturnsNullOrEmpty()
    {
        SetAuthorizationHeader("Bearer");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.True(result is null or "", $"Expected null or empty but got '{result}'");
    }

    [Fact]
    public void GetAccessToken_BearerWithOnlySpaces_ReturnsNullOrEmpty()
    {
        SetAuthorizationHeader("Bearer   ");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.True(result is null or "", $"Expected null or empty but got '{result}'");
    }

    [Fact]
    public void GetAccessToken_UnknownScheme_FallsBackToEnvVar()
    {
        Environment.SetEnvironmentVariable("HELIX_ACCESS_TOKEN", "env-fallback");
        SetAuthorizationHeader("Basic dXNlcjpwYXNz");

        var accessor = CreateAccessor();
        var result = accessor.GetAccessToken();

        Assert.Equal("env-fallback", result);
    }

    // --- Interface compliance ---

    [Fact]
    public void ImplementsIHelixTokenAccessor()
    {
        var context = new DefaultHttpContext();
        _httpContextAccessor.HttpContext = context;

        IHelixTokenAccessor accessor = CreateAccessor();

        Assert.NotNull(accessor);
    }
}
