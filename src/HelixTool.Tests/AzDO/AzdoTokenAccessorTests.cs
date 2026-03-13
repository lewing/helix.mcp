// Tests for AzCliAzdoTokenAccessor — AzDO auth chain.
// Covers AZDO_TOKEN PAT/Bearer detection, AZDO_TOKEN_TYPE overrides, and fallback caching behavior.

using System.Reflection;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public class AzdoTokenAccessorTests : IDisposable
{
    private const string EnvVarName = "AZDO_TOKEN";
    private const string EnvVarTypeName = "AZDO_TOKEN_TYPE";
    private readonly string? _originalEnvValue;
    private readonly string? _originalEnvTypeValue;
    private readonly string? _originalPathValue;
    private readonly List<string> _tempDirectories = [];

    public AzdoTokenAccessorTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
        _originalEnvTypeValue = Environment.GetEnvironmentVariable(EnvVarTypeName);
        _originalPathValue = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable(EnvVarName, null);
        Environment.SetEnvironmentVariable(EnvVarTypeName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
        Environment.SetEnvironmentVariable(EnvVarTypeName, _originalEnvTypeValue);
        Environment.SetEnvironmentVariable("PATH", _originalPathValue);

        foreach (var tempDirectory in _tempDirectories)
        {
            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
                // Cleanup is best-effort only.
            }
        }
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarContainsPat_ReturnsBasicCredential()
    {
        const string pat = "my-azdo-pat-token";
        Environment.SetEnvironmentVariable(EnvVarName, pat);
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal(EncodePatForBasic(pat), credential!.Token);
        Assert.Equal("Basic", credential.Scheme);
        Assert.Equal("AZDO_TOKEN (PAT)", credential.Source);
        Assert.Equal(pat, credential.DisplayToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarLooksLikeJwt_ReturnsBearerCredential()
    {
        const string jwt = "header.payload.signature";
        Environment.SetEnvironmentVariable(EnvVarName, jwt);
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal(jwt, credential!.Token);
        Assert.Equal("Bearer", credential.Scheme);
        Assert.Equal("AZDO_TOKEN (Bearer)", credential.Source);
        Assert.Equal(jwt, credential.DisplayToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTokenTypeOverrideIsPat_SkipsJwtHeuristic()
    {
        const string patWithDots = "token.with.dots";
        Environment.SetEnvironmentVariable(EnvVarName, patWithDots);
        Environment.SetEnvironmentVariable(EnvVarTypeName, "pat");
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal("Basic", credential!.Scheme);
        Assert.Equal("AZDO_TOKEN (PAT)", credential.Source);
        Assert.Equal(EncodePatForBasic(patWithDots), credential.Token);
        Assert.Equal(patWithDots, credential.DisplayToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenTokenTypeOverrideIsBearer_SkipsJwtHeuristic()
    {
        const string token = "plain-token";
        Environment.SetEnvironmentVariable(EnvVarName, token);
        Environment.SetEnvironmentVariable(EnvVarTypeName, "BeArEr");
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal("Bearer", credential!.Scheme);
        Assert.Equal("AZDO_TOKEN (Bearer)", credential.Source);
        Assert.Equal(token, credential.Token);
        Assert.Equal(token, credential.DisplayToken);
    }

    [Theory]
    [InlineData("oauth", "header.payload.signature", "Bearer", "AZDO_TOKEN (Bearer)")]
    [InlineData("", "plain-pat", "Basic", "AZDO_TOKEN (PAT)")]
    public async Task GetAccessTokenAsync_WhenTokenTypeOverrideIsUnknownOrEmpty_FallsBackToHeuristic(
        string overrideValue,
        string token,
        string expectedScheme,
        string expectedSource)
    {
        Environment.SetEnvironmentVariable(EnvVarName, token);
        Environment.SetEnvironmentVariable(EnvVarTypeName, overrideValue);
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal(expectedScheme, credential!.Scheme);
        Assert.Equal(expectedSource, credential.Source);
        Assert.Equal(token, credential.DisplayToken);
        Assert.Equal(expectedScheme == "Basic" ? EncodePatForBasic(token) : token, credential.Token);
    }

    [Fact]
    public void TryGetEnvCredential_WhenTokenTypeOverrideSetWithoutToken_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(EnvVarTypeName, "pat");

        var credential = (AzdoCredential?)typeof(AzCliAzdoTokenAccessor)
            .GetMethod("TryGetEnvCredential", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, null);

        Assert.Null(credential);
    }

    [Fact]
    public void AzdoCredential_RecordPropertiesEqualityAndNullHandling_Work()
    {
        var credential = new AzdoCredential("encoded-token", "Basic", "AZDO_TOKEN (PAT)")
        {
            DisplayToken = "pat-token"
        };
        var same = new AzdoCredential("encoded-token", "Basic", "AZDO_TOKEN (PAT)")
        {
            DisplayToken = "pat-token"
        };
        var different = new AzdoCredential("encoded-token", "Basic", "AZDO_TOKEN (PAT)")
        {
            DisplayToken = "different-display-token"
        };

        Assert.Equal("encoded-token", credential.Token);
        Assert.Equal("Basic", credential.Scheme);
        Assert.Equal("AZDO_TOKEN (PAT)", credential.Source);
        Assert.Equal("pat-token", credential.DisplayToken);
        Assert.Equal(credential, same);
        Assert.NotEqual(credential, different);

        AzdoCredential? nullableCredential = null;
        Assert.Null(nullableCredential);
    }

    [Fact]
    public void AzdoCredential_LegacyStringConversion_PreservesDisplayToken()
    {
        var credential = new AzdoCredential("encoded-pat", "Basic", "AZDO_TOKEN (PAT)")
        {
            DisplayToken = "plain-pat"
        };

        Assert.Equal("plain-pat", credential.DisplayToken);

#pragma warning disable CS0618
        AzdoCredential? legacyCredential = "legacy-token";
        AzdoCredential? nullCredential = (string?)null;
#pragma warning restore CS0618

        Assert.NotNull(legacyCredential);
        Assert.Equal("legacy-token", legacyCredential!.Token);
        Assert.Equal("Bearer", legacyCredential.Scheme);
        Assert.Equal("Legacy string token", legacyCredential.Source);
        Assert.Equal("legacy-token", legacyCredential.DisplayToken);
        Assert.Null(nullCredential);
    }

    [Fact]
    public void AzdoCredential_DoesNotExposeImplicitConversionToString()
    {
        var implicitOperators = typeof(AzdoCredential)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit")
            .ToArray();

        Assert.DoesNotContain(implicitOperators, method =>
        {
            var parameters = method.GetParameters();
            return method.ReturnType == typeof(string)
                && parameters.Length == 1
                && parameters[0].ParameterType == typeof(AzdoCredential);
        });
    }

    [Fact]
    public void AzdoCredential_StringConversion_IsMarkedObsolete()
    {
        var fromStringOperator = typeof(AzdoCredential)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "op_Implicit"
                && method.ReturnType == typeof(AzdoCredential)
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string));

        var obsolete = fromStringOperator.GetCustomAttribute<ObsoleteAttribute>();

        Assert.NotNull(obsolete);
        Assert.Contains("legacy compatibility", obsolete!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avoid accidental token exposure", obsolete.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvVarSet_DoesNotResolveFallbackChain()
    {
        const string pat = "priority-pat";
        Environment.SetEnvironmentVariable(EnvVarName, pat);
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal("AZDO_TOKEN (PAT)", credential!.Source);
        Assert.Equal("Basic", credential.Scheme);
        Assert.Equal(EncodePatForBasic(pat), credential.Token);
        Assert.Equal(pat, credential.DisplayToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_EnvVarCheckedEveryCall()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "first-token");
        var accessor = new AzCliAzdoTokenAccessor();

        var first = await accessor.GetAccessTokenAsync();
        Assert.NotNull(first);
        Assert.Equal("AZDO_TOKEN (PAT)", first!.Source);
        Assert.Equal("first-token", first.DisplayToken);

        Environment.SetEnvironmentVariable(EnvVarName, "header.payload.signature");
        var second = await accessor.GetAccessTokenAsync();

        Assert.NotNull(second);
        Assert.Equal("AZDO_TOKEN (Bearer)", second!.Source);
        Assert.Equal("header.payload.signature", second.Token);
        Assert.Equal("header.payload.signature", second.DisplayToken);
    }

    [Fact]
    public async Task GetAccessTokenAsync_AfterFallbackResolution_UsesCachedCredential()
    {
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        var first = await accessor.GetAccessTokenAsync();
        var second = await accessor.GetAccessTokenAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCalls_AllReturnSameEnvCredential()
    {
        const string pat = "concurrent-token";
        Environment.SetEnvironmentVariable(EnvVarName, pat);
        var accessor = new AzCliAzdoTokenAccessor();

        var tasks = Enumerable.Range(0, 10)
            .Select(_ => accessor.GetAccessTokenAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, credential =>
        {
            Assert.NotNull(credential);
            Assert.Equal("Basic", credential!.Scheme);
            Assert.Equal(EncodePatForBasic(pat), credential.Token);
            Assert.Equal(pat, credential.DisplayToken);
        });
    }

    [Fact]
    public void TryGetJwtExpiration_WhenJwtContainsExp_ReturnsExpiration()
    {
        var expected = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var token = CreateJwt(new Dictionary<string, object?>
        {
            ["exp"] = expected.ToUnixTimeSeconds(),
            ["sub"] = "lambert"
        });

        var expiration = AzdoCredential.TryGetJwtExpiration(token);

        Assert.Equal(expected, expiration);
    }

    [Fact]
    public void TryGetJwtExpiration_WhenJwtMissingExp_ReturnsNull()
    {
        var token = CreateJwt(new Dictionary<string, object?>
        {
            ["sub"] = "lambert"
        });

        Assert.Null(AzdoCredential.TryGetJwtExpiration(token));
    }

    [Fact]
    public void TryGetJwtExpiration_WhenTokenIsNotJwt_ReturnsNull()
    {
        Assert.Null(AzdoCredential.TryGetJwtExpiration("plain-pat-token"));
    }

    [Fact]
    public void TryGetJwtExpiration_WhenPayloadIsMalformedBase64_ReturnsNull()
    {
        Assert.Null(AzdoCredential.TryGetJwtExpiration("header.%%%%.signature"));
    }

    [Fact]
    public void BuildCacheIdentity_WhenJwtContainsStableClaims_IncludesThem()
    {
        var token = CreateJwt(new Dictionary<string, object?>
        {
            ["tid"] = "tenant-id",
            ["oid"] = "object-id",
            ["sub"] = "subject-id"
        });

        var identity = AzdoCredential.BuildCacheIdentity("AzureCliCredential", token);

        Assert.Equal("AzureCliCredential:tenant-id:object-id:subject-id", identity);
    }

    [Fact]
    public void BuildCacheIdentity_WhenTokenIsNotJwt_AppendsShortTokenHash()
    {
        var identity = AzdoCredential.BuildCacheIdentity("env:AZDO_TOKEN:pat", "plain-pat-token");

        Assert.Equal("env:AZDO_TOKEN:pat:c9c19750", identity);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenEnvJwtContainsIdentityClaims_PopulatesCacheIdentityAndExpiration()
    {
        var expiresOn = DateTimeOffset.FromUnixTimeSeconds(1_900_000_123);
        var token = CreateJwt(new Dictionary<string, object?>
        {
            ["tid"] = "tenant-id",
            ["oid"] = "object-id",
            ["sub"] = "subject-id",
            ["exp"] = expiresOn.ToUnixTimeSeconds()
        });
        Environment.SetEnvironmentVariable(EnvVarName, token);
        var accessor = new AzCliAzdoTokenAccessor();

        var credential = await accessor.GetAccessTokenAsync();

        Assert.NotNull(credential);
        Assert.Equal("env:AZDO_TOKEN:bearer:tenant-id:object-id:subject-id", credential!.CacheIdentity);
        Assert.Equal(expiresOn, credential.ExpiresOnUtc);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenCachedFallbackCredentialIsFresh_ReturnsCachedCredential()
    {
        ConfigurePathWithoutAz();
        var accessor = new AzCliAzdoTokenAccessor();
        var cachedCredential = new AzdoCredential("cached-fallback-token", "Bearer", "Cached fallback")
        {
            DisplayToken = "cached-fallback-token",
            AuthPath = "AzureCliCredential",
            CacheIdentity = "AzureCliCredential:cached-user",
            ExpiresOnUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        SetCachedFallbackResolution(
            accessor,
            cachedCredential,
            CreateStatus(cachedCredential, looksExpired: false),
            DateTimeOffset.UtcNow.AddMinutes(10));

        var credential = await accessor.GetAccessTokenAsync();

        Assert.Same(cachedCredential, credential);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenCachedFallbackCredentialIsExpired_ReacquiresCredential()
    {
        ConfigurePathWithoutAz();
        var accessor = new AzCliAzdoTokenAccessor();
        var staleCredential = new AzdoCredential("stale-fallback-token", "Bearer", "Stale fallback")
        {
            DisplayToken = "stale-fallback-token",
            AuthPath = "AzureCliCredential",
            CacheIdentity = "AzureCliCredential:stale-user",
            ExpiresOnUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        SetCachedFallbackResolution(
            accessor,
            staleCredential,
            CreateStatus(staleCredential, looksExpired: false),
            DateTimeOffset.UtcNow.AddMinutes(-1));

        var credential = await accessor.GetAccessTokenAsync();

        Assert.Null(credential);
    }

    [Fact]
    public async Task AuthStatusAsync_WhenEnvVarContainsPat_ReturnsEnvironmentVariableStatus()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "plain-pat-token");
        var accessor = new AzCliAzdoTokenAccessor();

        var status = await accessor.AuthStatusAsync();

        Assert.True(status.IsAuthenticated);
        Assert.Equal("environment variable", status.Path);
        Assert.Equal("AZDO_TOKEN (PAT)", status.Source);
        Assert.Null(status.LooksExpired);
        Assert.Contains(status.Warnings, warning => warning.Contains("PAT expiry cannot be determined locally.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthStatusAsync_WhenEnvVarContainsExpiredJwt_ReturnsExpiryWarning()
    {
        var token = CreateJwt(new Dictionary<string, object?>
        {
            ["sub"] = "lambert",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds()
        });
        Environment.SetEnvironmentVariable(EnvVarName, token);
        var accessor = new AzCliAzdoTokenAccessor();

        var status = await accessor.AuthStatusAsync();

        Assert.True(status.IsAuthenticated);
        Assert.True(status.LooksExpired);
        Assert.Equal("environment variable", status.Path);
        Assert.Equal("AZDO_TOKEN (Bearer)", status.Source);
        Assert.Contains(status.Warnings, warning => warning.Contains("appears expired", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AuthStatusAsync_WhenNoCredentialsResolve_ReturnsAnonymousStatus()
    {
        ConfigurePathWithoutAz();
        var accessor = new AzCliAzdoTokenAccessor();

        var status = await accessor.AuthStatusAsync();

        Assert.False(status.IsAuthenticated);
        Assert.Equal("anonymous", status.Path);
        Assert.Equal("anonymous", status.Source);
        Assert.Null(status.LooksExpired);
        Assert.Contains(status.Warnings, warning => warning.Contains("No AzDO credentials resolved", StringComparison.Ordinal));
    }

    [Fact]
    public void ImplementsIAzdoTokenAccessor()
    {
        IAzdoTokenAccessor accessor = new AzCliAzdoTokenAccessor();
        Assert.NotNull(accessor);
    }

    [Fact]
    public async Task GetAccessTokenAsync_WithCancellationToken_EnvVarPath_ReturnsImmediately()
    {
        Environment.SetEnvironmentVariable(EnvVarName, "cancel-test-token");
        var accessor = new AzCliAzdoTokenAccessor();
        using var cts = new CancellationTokenSource();

        var credential = await accessor.GetAccessTokenAsync(cts.Token);

        Assert.NotNull(credential);
        Assert.Equal("cancel-test-token", credential!.DisplayToken);
        Assert.Equal("AZDO_TOKEN (PAT)", credential.Source);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ReturnTypeIsNullableCredential()
    {
        Environment.SetEnvironmentVariable(EnvVarName, null);
        var accessor = new AzCliAzdoTokenAccessor();

        AzdoCredential? credential = await accessor.GetAccessTokenAsync();

        Assert.True(credential is null || !string.IsNullOrEmpty(credential.Token));
    }

    private void ConfigurePathWithoutAz()
    {
        var tempDirectory = CreateTempDirectory("azdo-no-az");
        Environment.SetEnvironmentVariable("PATH", tempDirectory);
    }

    private string CreateTempDirectory(string prefix)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        _tempDirectories.Add(tempDirectory);
        return tempDirectory;
    }

    private static void SetCachedFallbackResolution(
        AzCliAzdoTokenAccessor accessor,
        AzdoCredential? credential,
        AzdoAuthStatus status,
        DateTimeOffset refreshAfterUtc)
    {
        var cachedResolutionType = typeof(AzCliAzdoTokenAccessor)
            .GetNestedType("CachedResolution", BindingFlags.NonPublic)!;
        var cachedResolution = Activator.CreateInstance(
            cachedResolutionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [credential, status, refreshAfterUtc],
            culture: null);

        typeof(AzCliAzdoTokenAccessor)
            .GetField("_cachedFallbackResolution", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(accessor, cachedResolution);
    }

    private static AzdoAuthStatus CreateStatus(AzdoCredential credential, bool? looksExpired, params string[] warnings)
        => new()
        {
            IsAuthenticated = true,
            Path = credential.AuthPath,
            Source = credential.Source,
            LooksExpired = looksExpired,
            ExpiresOnUtc = credential.ExpiresOnUtc,
            Warnings = warnings
        };

    private static string CreateJwt(IReadOnlyDictionary<string, object?> claims)
    {
        var header = Base64UrlEncode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64UrlEncode(JsonSerializer.Serialize(claims));
        return $"{header}.{payload}.signature";
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string EncodePatForBasic(string token)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"));
}
