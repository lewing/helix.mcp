// Tests for IHelixTokenAccessor / EnvironmentHelixTokenAccessor (L-HTTP-1).
// EnvironmentHelixTokenAccessor wraps a fixed token — used by stdio/CLI transport.
// Will compile once Ripley's IHelixTokenAccessor.cs lands in HelixTool.Core.

using HelixTool.Core;
using HelixTool.Core.Helix;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class HelixTokenAccessorTests
{
    [Fact]
    public void GetAccessToken_ReturnsTokenPassedInConstructor()
    {
        var accessor = new EnvironmentHelixTokenAccessor("my-secret-token");

        var result = accessor.GetAccessToken();

        Assert.Equal("my-secret-token", result);
    }

    [Fact]
    public void GetAccessToken_NullToken_ReturnsNull()
    {
        var accessor = new EnvironmentHelixTokenAccessor(null);

        var result = accessor.GetAccessToken();

        Assert.Null(result);
    }

    [Fact]
    public void GetAccessToken_EmptyToken_ReturnsEmptyString()
    {
        var accessor = new EnvironmentHelixTokenAccessor("");

        var result = accessor.GetAccessToken();

        Assert.Equal("", result);
    }

    [Fact]
    public void GetAccessToken_CalledMultipleTimes_ReturnsSameValue()
    {
        var accessor = new EnvironmentHelixTokenAccessor("stable-token");

        var first = accessor.GetAccessToken();
        var second = accessor.GetAccessToken();

        Assert.Equal(first, second);
    }

    [Fact]
    public void ImplementsIHelixTokenAccessor()
    {
        IHelixTokenAccessor accessor = new EnvironmentHelixTokenAccessor("test");

        Assert.NotNull(accessor);
        Assert.Equal("test", accessor.GetAccessToken());
    }
}

// Uses the internal (store, envToken) ctor so tests never read the real HELIX_ACCESS_TOKEN.
public class ChainedHelixTokenAccessorTests
{
    private readonly ICredentialStore _store = Substitute.For<ICredentialStore>();

    [Fact]
    public async Task EnvToken_WinsWithoutCallingStore()
    {
        var accessor = new ChainedHelixTokenAccessor(_store, "env-token");

        Assert.Equal("env-token", accessor.GetAccessToken());
        Assert.Equal(TokenSource.EnvironmentVariable, accessor.Source);
        await _store.DidNotReceiveWithAnyArgs().GetTokenAsync(default!, default!, default);
    }

    [Fact]
    public void NoEnvToken_UsesStoredCredential()
    {
        _store.GetTokenAsync("helix.dot.net", "helix-api-token", Arg.Any<CancellationToken>()).Returns("stored-token");
        var accessor = new ChainedHelixTokenAccessor(_store, envToken: null);

        Assert.Equal("stored-token", accessor.GetAccessToken());
        Assert.Equal(TokenSource.StoredCredential, accessor.Source);
    }

    [Fact]
    public void StoreThrows_ResolvesToNoneInsteadOfThrowing()
    {
        _store.GetTokenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));
        var accessor = new ChainedHelixTokenAccessor(_store, envToken: null);

        Assert.Null(accessor.GetAccessToken());
        Assert.Equal(TokenSource.None, accessor.Source);
    }
}

public class StdioMcpServiceWiringTests
{
    [Fact]
    public void LiveMode_ResolvesChainedTokenAccessorOverFallbackCredentialStore()
    {
        var services = new ServiceCollection();
        Commands.ConfigureStdioMcpServices(services, evalSnapshotDir: null);
        using var provider = services.BuildServiceProvider();

        // Only resolve types; never call GetAccessToken, which would hit git/keychain.
        Assert.IsType<ChainedHelixTokenAccessor>(provider.GetRequiredService<IHelixTokenAccessor>());
        Assert.IsType<FallbackCredentialStore>(provider.GetRequiredService<ICredentialStore>());
    }
}
