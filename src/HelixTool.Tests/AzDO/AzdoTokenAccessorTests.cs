// Tests for AzCliAzdoTokenAccessor — AzDO auth chain.
// Covers AZDO_TOKEN PAT/Bearer detection plus fallback caching behavior.

using System.Text;
using Xunit;
using HelixTool.Core.AzDO;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public class AzdoTokenAccessorTests : IDisposable
{
    private const string EnvVarName = "AZDO_TOKEN";
    private readonly string? _originalEnvValue;

    public AzdoTokenAccessorTests()
    {
        _originalEnvValue = Environment.GetEnvironmentVariable(EnvVarName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, _originalEnvValue);
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

        string? displayToken = credential;
        Assert.Equal(pat, displayToken);
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
    public void AzdoCredential_ImplicitConversions_UseDisplayToken()
    {
        var credential = new AzdoCredential("encoded-pat", "Basic", "AZDO_TOKEN (PAT)")
        {
            DisplayToken = "plain-pat"
        };

        string? displayToken = credential;
        Assert.Equal("plain-pat", displayToken);

        AzdoCredential? legacyCredential = "legacy-token";
        Assert.NotNull(legacyCredential);
        Assert.Equal("legacy-token", legacyCredential!.Token);
        Assert.Equal("Bearer", legacyCredential.Scheme);
        Assert.Equal("Legacy string token", legacyCredential.Source);
        Assert.Equal("legacy-token", legacyCredential.DisplayToken);

        AzdoCredential? nullCredential = (string?)null;
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
