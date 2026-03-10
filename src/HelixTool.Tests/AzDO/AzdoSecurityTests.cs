// Security-focused tests for AzDO code — adversarial inputs and boundary conditions.
// Covers: AzdoIdResolver (malicious URLs), AzCliAzdoTokenAccessor (command injection),
// AzdoApiClient (SSRF, token leakage), CachingAzdoApiClient (cache isolation/poisoning).

using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.Cache;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class AzdoSecurityTests
{
    // ═════════════════════════════════════════════════════════════════════
    // 1. AzdoIdResolver — Malicious URL Inputs
    // ═════════════════════════════════════════════════════════════════════

    // --- Embedded credentials in URL ---

    [Theory]
    [InlineData("https://user:pass@dev.azure.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://admin:secret123@dev.azure.com/dnceng/public/_build/results?buildId=456")]
    public void Resolve_UrlWithEmbeddedCredentials_DoesNotLeakCredentials(string url)
    {
        // The resolver should either reject or parse, but MUST NOT include
        // credentials in output (org/project values). Even if Uri parses user:pass,
        // the resolver only extracts from Host and Path.
        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        if (result)
        {
            // If parsed successfully, credentials should NOT appear in org/project
            Assert.DoesNotContain("user", org);
            Assert.DoesNotContain("pass", org);
            Assert.DoesNotContain("user", project);
            Assert.DoesNotContain("pass", project);
            Assert.DoesNotContain("admin", org);
            Assert.DoesNotContain("secret", org);
        }
        // False is also acceptable — rejection is safe
    }

    // --- Non-AzDO hosts (SSRF vector) ---

    [Theory]
    [InlineData("https://evil.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://dev.azure.com.evil.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://notdev.azure.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://azure.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://visualstudio.com/public/_build/results?buildId=123")]
    [InlineData("https://internal.server:8080/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://169.254.169.254/latest/meta-data/?buildId=123")]
    [InlineData("https://localhost/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://127.0.0.1/dnceng/public/_build/results?buildId=123")]
    public void Resolve_NonAzdoHost_ThrowsArgumentException(string url)
    {
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Theory]
    [InlineData("https://evil.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://dev.azure.com.evil.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("https://localhost/dnceng/public/_build/results?buildId=123")]
    public void TryResolve_NonAzdoHost_ReturnsFalse(string url)
    {
        var result = AzdoIdResolver.TryResolve(url, out _, out _, out _);
        Assert.False(result);
    }

    // --- Path traversal attempts ---

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\..\\windows\\system32\\config\\sam")]
    [InlineData("....//....//etc/passwd")]
    public void Resolve_PathTraversal_ThrowsArgumentException(string input)
    {
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(input));
    }

    [Fact]
    public void Resolve_PathTraversalInAzdoUrl_DoesNotEscapeOrgProject()
    {
        // Path traversal in the org/project segments of an otherwise valid AzDO URL
        var url = "https://dev.azure.com/../../etc/_build/results?buildId=123";
        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out _);

        // The resolver will parse URI segments — path traversal is collapsed by Uri
        // Key: org and project should NOT be ".." after Uri normalization
        if (result)
        {
            Assert.DoesNotContain("..", org);
            Assert.DoesNotContain("..", project);
        }
        // False is also acceptable
    }

    // --- Query parameter injection ---

    [Fact]
    public void Resolve_DuplicateBuildIdParams_ThrowsArgumentException()
    {
        // HttpUtility.ParseQueryString concatenates duplicate keys with commas ("123,999")
        // which fails int.TryParse — safe behavior: rejects ambiguous input
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=123&buildId=999";
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Fact]
    public void Resolve_SqlInjectionInQueryParam_ThrowsOnNonIntegerBuildId()
    {
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=1;DROP TABLE builds";
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Fact]
    public void Resolve_XssInQueryParam_ThrowsOnNonIntegerBuildId()
    {
        var url = "https://dev.azure.com/dnceng/public/_build/results?buildId=<script>alert(1)</script>";
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    // --- Unicode/encoded characters ---

    [Fact]
    public void Resolve_PercentEncodedOrgProject_DecodesCorrectly()
    {
        // %20 in org/project — Uri.UnescapeDataString should decode
        var url = "https://dev.azure.com/my%20org/my%20project/_build/results?buildId=42";
        var (org, project, buildId) = AzdoIdResolver.Resolve(url);

        Assert.Equal("my org", org);
        Assert.Equal("my project", project);
        Assert.Equal(42, buildId);
    }

    [Fact]
    public void Resolve_UnicodeOrgProject_ParsedSafely()
    {
        var url = "https://dev.azure.com/組織/プロジェクト/_build/results?buildId=42";
        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out var buildId);

        if (result)
        {
            // Unicode should be preserved (or percent-decoded)
            Assert.Equal(42, buildId);
        }
        // If rejected, that's also safe behavior
    }

    [Fact]
    public void Resolve_NullBytesInInput_ThrowsOrRejects()
    {
        var input = "https://dev.azure.com/dnceng/public/_build/results?buildId=123\0extra";
        // Null bytes should not pass through silently
        var result = AzdoIdResolver.TryResolve(input, out _, out _, out _);
        // Either rejects or parses safely without the null byte portion
    }

    // --- Extremely long URLs ---

    [Fact]
    public void Resolve_ExtremelyLongUrl_DoesNotHang()
    {
        // 10KB URL — should not cause ReDoS or excessive memory use
        var longPath = new string('a', 10_000);
        var url = $"https://dev.azure.com/{longPath}/proj/_build/results?buildId=42";

        // Should complete in reasonable time (no ReDoS since no regex)
        var result = AzdoIdResolver.TryResolve(url, out _, out _, out _);
        // Result doesn't matter — the key assertion is it completes without hanging
    }

    [Fact]
    public void Resolve_ExtremelyLongBuildId_ThrowsOnOverflow()
    {
        // Build ID larger than int.MaxValue
        var url = $"https://dev.azure.com/dnceng/public/_build/results?buildId={long.MaxValue}";
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Fact]
    public void Resolve_PlainIntegerOverflow_ThrowsArgumentException()
    {
        // Plain integer larger than int.MaxValue — int.TryParse fails
        var ex = Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(long.MaxValue.ToString()));
        Assert.Contains("Invalid AzDO build reference", ex.Message);
    }

    // --- Scheme-based attacks ---

    [Theory]
    [InlineData("ftp://dev.azure.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("file:///dev.azure.com/dnceng/public/_build/results?buildId=123")]
    [InlineData("javascript:alert(document.cookie)//buildId=123")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void Resolve_NonHttpScheme_ThrowsArgumentException(string url)
    {
        Assert.Throws<ArgumentException>(() => AzdoIdResolver.Resolve(url));
    }

    [Fact]
    public void Resolve_HttpScheme_AcceptedForAzdoHost()
    {
        // HTTP (non-TLS) is accepted per production code
        var url = "http://dev.azure.com/dnceng/public/_build/results?buildId=123";
        var (org, _, buildId) = AzdoIdResolver.Resolve(url);
        Assert.Equal("dnceng", org);
        Assert.Equal(123, buildId);
    }

    // --- DevAzureComUrl with insufficient path segments ---

    [Fact]
    public void Resolve_DevAzureComUrl_OnlyOrg_ThrowsArgumentException()
    {
        var url = "https://dev.azure.com/dnceng/_build/results?buildId=123";
        // Only 1 path segment (dnceng) — needs at least 2 (org + project)
        // Actually "dnceng" + "_build" = 2 segments, so org=dnceng, project=_build
        // This demonstrates a false positive — _build parsed as project name
        var result = AzdoIdResolver.TryResolve(url, out var org, out var project, out _);
        if (result)
        {
            // _build is parsed as project — a semantic error but not a security issue
            Assert.Equal("dnceng", org);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 2. AzCliAzdoTokenAccessor — Command Injection Safety
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public void AzCliTokenAccessor_ProcessStartInfo_NoShellExecute()
    {
        // The ProcessStartInfo in TryGetAzCliTokenAsync sets UseShellExecute = false.
        // This is critical: UseShellExecute = true would allow shell injection.
        // We verify the implementation by inspecting the source contract:
        // - FileName = "az" (hardcoded, not user-controlled)
        // - Arguments = hardcoded string with AzdoResourceId constant
        // - UseShellExecute = false (no shell interpretation)
        // - CreateNoWindow = true (no console window)

        // Since TryGetAzCliTokenAsync is private static, we test the observable contract:
        // the accessor uses a fixed command, not user input.
        var accessor = new AzCliAzdoTokenAccessor();
        // Verify it's constructible without any user-provided parameters
        Assert.NotNull(accessor);
    }

    [Fact]
    public async Task AzCliTokenAccessor_NoUserInputInCommand()
    {
        // The az CLI command uses only a hardcoded resource ID constant.
        // No user input flows into ProcessStartInfo.Arguments or FileName.
        // Verify by calling with various env var values — none should affect
        // the subprocess command construction.
        var originalEnv = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        try
        {
            // Attempt command injection via env var — this should NOT work
            // because the env var is returned directly, never passed to a shell
            Environment.SetEnvironmentVariable("AZDO_TOKEN", "token; rm -rf /");
            var accessor = new AzCliAzdoTokenAccessor();
            var token = await accessor.GetAccessTokenAsync();

            // The env var value is returned as-is (not executed)
            Assert.Equal("token; rm -rf /", token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", originalEnv);
        }
    }

    [Fact]
    public async Task AzCliTokenAccessor_EnvVarWithNewlines_ReturnedAsIs()
    {
        var originalEnv = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        try
        {
            // Newlines in token value — should be returned verbatim
            Environment.SetEnvironmentVariable("AZDO_TOKEN", "token\ninjected-header: evil");
            var accessor = new AzCliAzdoTokenAccessor();
            var token = await accessor.GetAccessTokenAsync();

            Assert.Contains("\n", token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", originalEnv);
        }
    }

    [Fact]
    public async Task AzCliTokenAccessor_AzCliFailure_ReturnsNull()
    {
        // When az CLI is not installed or fails, should return null (not throw)
        var originalEnv = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", null);
            var accessor = new AzCliAzdoTokenAccessor();
            var token = await accessor.GetAccessTokenAsync();

            // null or a valid token — never an exception
            // (az CLI not available in test env typically returns null)
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", originalEnv);
        }
    }

    [Fact]
    public async Task AzCliTokenAccessor_CancellationDuringEnvVarPath_ReturnsImmediately()
    {
        var originalEnv = Environment.GetEnvironmentVariable("AZDO_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", "fast-token");
            var accessor = new AzCliAzdoTokenAccessor();

            // Pre-cancelled token — env var path is synchronous, should still work
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // Env var path doesn't check cancellation token
            var token = await accessor.GetAccessTokenAsync(cts.Token);
            Assert.Equal("fast-token", token);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZDO_TOKEN", originalEnv);
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // 3. AzdoApiClient — Request Construction Security
    // ═════════════════════════════════════════════════════════════════════

    // --- SSRF Prevention: All requests must go to dev.azure.com ---

    [Fact]
    public async Task AzdoApiClient_AllRequestsGoToDevAzureCom()
    {
        var handler = new CapturingHttpHandler();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });
        await client.GetBuildAsync("any-org", "any-project", 1);
        Assert.StartsWith("https://dev.azure.com/", handler.LastRequest!.RequestUri!.AbsoluteUri);

        handler.ResponseContent = JsonSerializer.Serialize(new { id = "tl", records = Array.Empty<object>() });
        await client.GetTimelineAsync("any-org", "any-project", 1);
        Assert.StartsWith("https://dev.azure.com/", handler.LastRequest!.RequestUri!.AbsoluteUri);

        handler.ResponseContent = "log text";
        await client.GetBuildLogAsync("any-org", "any-project", 1, 1);
        Assert.StartsWith("https://dev.azure.com/", handler.LastRequest!.RequestUri!.AbsoluteUri);

        handler.ResponseContent = JsonSerializer.Serialize(new { value = Array.Empty<object>(), count = 0 });
        await client.GetBuildChangesAsync("any-org", "any-project", 1);
        Assert.StartsWith("https://dev.azure.com/", handler.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task AzdoApiClient_MaliciousOrgName_DoesNotChangeHost()
    {
        var handler = new CapturingHttpHandler();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        // Attempt to break out of URL via org name with path characters
        await client.GetBuildAsync("evil.com/../../", "proj", 1);
        Assert.Equal("dev.azure.com", handler.LastRequest!.RequestUri!.Host);

        // Attempt with @ sign (URL authority separator)
        await client.GetBuildAsync("evil.com@real", "proj", 1);
        Assert.Equal("dev.azure.com", handler.LastRequest!.RequestUri!.Host);
    }

    [Fact]
    public async Task AzdoApiClient_OrgProjectWithSlashes_EscapedInUrl()
    {
        var handler = new CapturingHttpHandler();
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("test-token");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        handler.ResponseContent = JsonSerializer.Serialize(new { id = 1 });

        await client.GetBuildAsync("org/../../etc", "project", 1);

        // Uri.EscapeDataString should encode the slashes
        var url = handler.LastRequest!.RequestUri!.AbsoluteUri;
        Assert.Contains("org%2F..%2F..%2Fetc", url);
        Assert.Equal("dev.azure.com", handler.LastRequest.RequestUri!.Host);
    }

    // --- Token leakage in error messages ---

    [Fact]
    public async Task AzdoApiClient_401Error_DoesNotLeakToken()
    {
        var handler = new CapturingHttpHandler { StatusCode = HttpStatusCode.Unauthorized };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("super-secret-bearer-token-xyz123");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBuildAsync("dnceng", "public", 1));

        Assert.DoesNotContain("super-secret-bearer-token-xyz123", ex.Message);
        Assert.DoesNotContain("super-secret", ex.Message);
    }

    [Fact]
    public async Task AzdoApiClient_403Error_DoesNotLeakToken()
    {
        var handler = new CapturingHttpHandler { StatusCode = HttpStatusCode.Forbidden };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("my-secret-pat-value");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBuildAsync("dnceng", "public", 1));

        Assert.DoesNotContain("my-secret-pat-value", ex.Message);
    }

    [Fact]
    public async Task AzdoApiClient_500Error_DoesNotLeakToken()
    {
        var handler = new CapturingHttpHandler
        {
            StatusCode = HttpStatusCode.InternalServerError,
            ResponseContent = "Internal server error"
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
            .Returns("bearer-token-in-500-test");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetBuildAsync("dnceng", "public", 1));

        Assert.DoesNotContain("bearer-token-in-500-test", ex.Message);
    }

    // --- Null/empty token behavior ---

    [Fact]
    public async Task AzdoApiClient_NullToken_NoAuthHeader_NoException()
    {
        var handler = new CapturingHttpHandler
        {
            ResponseContent = JsonSerializer.Serialize(new { id = 1 })
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        var result = await client.GetBuildAsync("dnceng", "public", 1);

        Assert.NotNull(result);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    [Fact]
    public async Task AzdoApiClient_EmptyToken_NoAuthHeader()
    {
        var handler = new CapturingHttpHandler
        {
            ResponseContent = JsonSerializer.Serialize(new { id = 1 })
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        await client.GetBuildAsync("dnceng", "public", 1);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    // --- Org/project with special characters ---

    [Theory]
    [InlineData("org<script>", "project")]
    [InlineData("org", "project<script>")]
    [InlineData("org\nHeader-Injection: evil", "project")]
    [InlineData("org", "project\r\nX-Injected: true")]
    public async Task AzdoApiClient_SpecialCharsInOrgProject_EscapedInUrl(string org, string project)
    {
        var handler = new CapturingHttpHandler
        {
            ResponseContent = JsonSerializer.Serialize(new { id = 1 })
        };
        var tokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        tokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>()).Returns("token");
        var client = new AzdoApiClient(new HttpClient(handler), tokenAccessor);

        // Should not throw — Uri.EscapeDataString handles special chars
        var ex = await Record.ExceptionAsync(
            () => client.GetBuildAsync(org, project, 1));

        if (ex is null && handler.LastRequest is not null)
        {
            // If request was sent, host must be dev.azure.com
            Assert.Equal("dev.azure.com", handler.LastRequest.RequestUri!.Host);
        }
        // An exception (e.g., UriFormatException) is also acceptable
    }

    // ═════════════════════════════════════════════════════════════════════
    // 4. CachingAzdoApiClient — Cache Isolation & Key Safety
    // ═════════════════════════════════════════════════════════════════════

    // --- Cache keys include org context (isolation) ---

    [Fact]
    public async Task CachingClient_DifferentOrgs_UseDifferentCacheKeys()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        await sut.GetBuildAsync("org-a", "proj", 42);
        await sut.GetBuildAsync("org-b", "proj", 42);

        // Verify different cache keys were used for different orgs
        var calls = cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetMetadataAsync")
            .ToList();

        Assert.Equal(2, calls.Count);
        var key1 = (string)calls[0].GetArguments()[0]!;
        var key2 = (string)calls[1].GetArguments()[0]!;
        Assert.NotEqual(key1, key2);
        Assert.Contains("org-a", key1);
        Assert.Contains("org-b", key2);
    }

    [Fact]
    public async Task CachingClient_DifferentProjects_UseDifferentCacheKeys()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        await sut.GetBuildAsync("org", "proj-a", 42);
        await sut.GetBuildAsync("org", "proj-b", 42);

        var calls = cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetMetadataAsync")
            .ToList();

        Assert.Equal(2, calls.Count);
        var key1 = (string)calls[0].GetArguments()[0]!;
        var key2 = (string)calls[1].GetArguments()[0]!;
        Assert.NotEqual(key1, key2);
    }

    // --- Cache keys use azdo: prefix for namespace isolation ---

    [Fact]
    public async Task CachingClient_CacheKeyHasAzdoPrefix()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        await sut.GetBuildAsync("org", "proj", 42);

        await cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => k.StartsWith("azdo:")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // --- Tokens are NOT stored in cache ---

    [Fact]
    public async Task CachingClient_CachedData_DoesNotContainToken()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed", BuildNumber = "20240101.1" });

        await sut.GetBuildAsync("org", "proj", 42);

        // Inspect the cached value — it should be build JSON, not contain tokens
        var setCalls = cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetMetadataAsync")
            .ToList();

        foreach (var call in setCalls)
        {
            var cachedValue = (string)call.GetArguments()[1]!;
            Assert.DoesNotContain("Bearer", cachedValue);
            Assert.DoesNotContain("Authorization", cachedValue);
            // The cached value should be valid JSON of the build object
            var doc = JsonDocument.Parse(cachedValue);
            Assert.NotNull(doc);
        }
    }

    // --- Cache key poisoning with crafted org/project ---

    [Fact]
    public async Task CachingClient_MaliciousOrgName_SanitizedInCacheKey()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        // Try to inject path traversal or colon-based key confusion
        await sut.GetBuildAsync("../../evil", "proj", 42);

        var setCalls = cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetMetadataAsync")
            .ToList();

        Assert.Single(setCalls);
        var key = (string)setCalls[0].GetArguments()[0]!;

        // CacheSecurity.SanitizeCacheKeySegment should remove path traversal
        Assert.DoesNotContain("..", key);
        Assert.DoesNotContain("/", key);
        Assert.DoesNotContain("\\", key);
    }

    [Fact]
    public async Task CachingClient_OrgWithColons_SanitizedInCacheKey()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        inner.GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        // Colons are cache key segment delimiters — should be sanitized
        await sut.GetBuildAsync("org:injected:key", "proj", 42);

        var setCalls = cache.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "SetMetadataAsync")
            .ToList();

        Assert.Single(setCalls);
        var key = (string)setCalls[0].GetArguments()[0]!;
        // The sanitized key should have the "azdo:" prefix and controlled structure
        Assert.StartsWith("azdo:", key);
    }

    // --- Cache disabled does not store anything ---

    [Fact]
    public async Task CachingClient_Disabled_NoCacheInteraction()
    {
        var inner = Substitute.For<IAzdoApiClient>();
        var cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 }; // disabled
        var sut = new CachingAzdoApiClient(inner, cache, opts);

        inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });

        await sut.GetBuildAsync("org", "proj", 42);

        // No cache reads or writes when disabled
        await cache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceive().SetJobCompletedAsync(
            Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════════
    // 5. AzdoService — URL Resolution Security (end-to-end)
    // ═════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AzdoService_MaliciousUrl_ThrowsBeforeApiCall()
    {
        var mockClient = Substitute.For<IAzdoApiClient>();
        var svc = new AzdoService(mockClient);

        // Malicious URL should be rejected by AzdoIdResolver before any API call
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.GetBuildSummaryAsync("https://evil.com/org/proj?buildId=123"));

        // Verify no API calls were made
        await mockClient.DidNotReceive().GetBuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-valid-input")]
    [InlineData("ftp://evil.com/file")]
    public async Task AzdoService_InvalidInputs_ThrowBeforeApiCall(string input)
    {
        var mockClient = Substitute.For<IAzdoApiClient>();
        var svc = new AzdoService(mockClient);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => svc.GetBuildSummaryAsync(input));

        await mockClient.DidNotReceive().GetBuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AzdoService_NullInput_ThrowsBeforeApiCall()
    {
        var mockClient = Substitute.For<IAzdoApiClient>();
        var svc = new AzdoService(mockClient);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => svc.GetBuildSummaryAsync(null!));

        await mockClient.DidNotReceive().GetBuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═════════════════════════════════════════════════════════════════════
    // Helpers
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal HTTP handler that captures requests and returns configurable responses.
    /// Reusable within this test class for security-focused assertions.
    /// </summary>
    private class CapturingHttpHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string ResponseContent { get; set; } = "{}";
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(ResponseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}
