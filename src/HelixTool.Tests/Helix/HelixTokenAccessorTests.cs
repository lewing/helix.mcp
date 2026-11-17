// Tests for IHelixTokenAccessor / EnvironmentHelixTokenAccessor (L-HTTP-1).
// EnvironmentHelixTokenAccessor wraps a fixed token — used by stdio/CLI transport.
// Will compile once Ripley's IHelixTokenAccessor.cs lands in HelixTool.Core.

using HelixTool.Core;
using HelixTool.Core.Cache;
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

    // Substitute accessor registered last wins, so no real token/git/keychain lookup happens.
    private static ServiceProvider BuildStdioProvider(string? helixToken)
    {
        var services = new ServiceCollection();
        Commands.ConfigureStdioMcpServices(services, evalSnapshotDir: null);
        var accessor = Substitute.For<IHelixTokenAccessor>();
        accessor.GetAccessToken().Returns(helixToken);
        services.AddSingleton(accessor);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("stdio-secret-token")]
    [InlineData(null)]
    public void LiveMode_CacheRootPartitionedByResolvedHelixToken(string? helixToken)
    {
        using var provider = BuildStdioProvider(helixToken);
        var options = provider.GetRequiredService<CacheOptions>();
        options.CacheRoot = Path.Combine(Path.GetTempPath(), $"hlx-test-{Guid.NewGuid():N}");

        var expectedLeaf = helixToken is null ? "public" : $"cache-{CacheOptions.ComputeTokenHash(helixToken)}";
        Assert.Equal(Path.Combine(options.CacheRoot, expectedLeaf), options.GetEffectiveCacheRoot());
    }

    [Fact]
    public async Task TokenedStdioCache_IsNotVisibleToUnauthenticatedHttpCacheOnSharedBase()
    {
        var sharedBase = Path.Combine(Path.GetTempPath(), $"hlx-test-{Guid.NewGuid():N}");
        try
        {
            using (var provider = BuildStdioProvider("stdio-secret-token"))
            {
                var stdioOptions = provider.GetRequiredService<CacheOptions>();
                stdioOptions.CacheRoot = sharedBase;
                using var stdioStore = new SqliteCacheStore(stdioOptions);
                await stdioStore.SetMetadataAsync("job:private:details", "{\"Name\":\"private\"}", TimeSpan.FromHours(1));
                Assert.NotNull(await stdioStore.GetMetadataAsync("job:private:details"));
            }

            // HTTP MCP without a Helix token: CacheRootHash null on the same base.
            using var httpStore = new SqliteCacheStore(new CacheOptions { CacheRoot = sharedBase });
            Assert.Null(await httpStore.GetMetadataAsync("job:private:details"));
        }
        finally
        {
            CacheTestCleanup.DeleteRoot(sharedBase);
        }
    }
}

public class LogoutCommandTests
{
    [Fact]
    public async Task Logout_TokenStillInKeychain_ReportsFailureWithExitCode1()
    {
        var store = Substitute.For<ICredentialStore>();
        store.DeleteTokenAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("still present for helix.dot.net"));
        var commands = new global::Commands(
            new Lazy<HelixService>(() => throw new InvalidOperationException("HelixService not needed")),
            store,
            new ChainedHelixTokenAccessor(store, envToken: null));

        await TestConsoleCapture.Lock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        int exitCode;
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            await commands.Logout();
            exitCode = Environment.ExitCode;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }

        Assert.Equal(1, exitCode);
        Assert.Contains("Token was not removed", stderr.ToString());
        Assert.DoesNotContain("Token removed", stdout.ToString());
    }
}
