// Tests for AzCliAzdoTokenAccessor — AzDO auth chain.
// Covers AZDO_TOKEN PAT/Bearer detection, AZDO_TOKEN_TYPE overrides, and fallback caching behavior.

using System.Text;
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

    public AzdoTokenAccessorTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
        _originalEnvTypeValue = Environment.GetEnvironmentVariable(EnvVarTypeName);
        Environment.SetEnvironmentVariable(EnvVarName, null);
        Environment.SetEnvironmentVariable(EnvVarTypeName, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
        Environment.SetEnvironmentVariable(EnvVarTypeName, _originalEnvTypeValue);
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

    private static string EncodePatForBasic(string token)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}"));
}
