using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class FallbackCredentialStoreTests
{
    private const string Host = "helix.dot.net";
    private const string User = "helix-api-token";

    private readonly ICredentialStore _primary = Substitute.For<ICredentialStore>();
    private readonly ICredentialStore _fallback = Substitute.For<ICredentialStore>();

    [Fact]
    public async Task GetToken_PrimaryHit_DoesNotCallFallback()
    {
        _primary.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns("primary-token");
        var store = new FallbackCredentialStore(_primary, _fallback);

        Assert.Equal("primary-token", await store.GetTokenAsync(Host, User));
        await _fallback.DidNotReceiveWithAnyArgs().GetTokenAsync(default!, default!, default);
    }

    [Fact]
    public async Task GetToken_PrimaryNull_ReturnsFallbackToken()
    {
        _primary.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns((string?)null);
        _fallback.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns("keychain-token");
        var store = new FallbackCredentialStore(_primary, _fallback);

        Assert.Equal("keychain-token", await store.GetTokenAsync(Host, User));
    }

    [Fact]
    public async Task GetToken_PrimaryThrows_ReturnsFallbackToken()
    {
        _primary.GetTokenAsync(Host, User, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));
        _fallback.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns("keychain-token");
        var store = new FallbackCredentialStore(_primary, _fallback);

        Assert.Equal("keychain-token", await store.GetTokenAsync(Host, User));
    }

    [Fact]
    public async Task GetToken_NoFallbackAndPrimaryThrows_Rethrows()
    {
        var error = new InvalidOperationException("git not found");
        _primary.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).ThrowsAsync(error);
        var store = new FallbackCredentialStore(_primary, readFallback: null);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetTokenAsync(Host, User));
        Assert.Same(error, thrown);
    }

    [Fact]
    public async Task GetToken_CallerCancellation_PropagatesWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _primary.GetTokenAsync(Host, User, cts.Token)
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var store = new FallbackCredentialStore(_primary, _fallback);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetTokenAsync(Host, User, cts.Token));
        await _fallback.DidNotReceiveWithAnyArgs().GetTokenAsync(default!, default!, default);
    }

    [Fact]
    public async Task StoreAndDelete_GoOnlyToPrimary()
    {
        var store = new FallbackCredentialStore(_primary, _fallback);

        await store.StoreTokenAsync(Host, User, "new-token");
        await store.DeleteTokenAsync(Host, User);

        await _primary.Received(1).StoreTokenAsync(Host, User, "new-token", Arg.Any<CancellationToken>());
        await _primary.Received(1).DeleteTokenAsync(Host, User, Arg.Any<CancellationToken>());
        await _fallback.DidNotReceiveWithAnyArgs().StoreTokenAsync(default!, default!, default!, default);
        await _fallback.DidNotReceiveWithAnyArgs().DeleteTokenAsync(default!, default!, default);
    }

    [Fact]
    public async Task Delete_FallbackStillHasToken_ThrowsWithoutLeakingToken()
    {
        _fallback.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns("leftover-secret");
        var store = new FallbackCredentialStore(_primary, _fallback);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteTokenAsync(Host, User));

        await _primary.Received(1).DeleteTokenAsync(Host, User, Arg.Any<CancellationToken>());
        Assert.Contains(Host, ex.Message);
        Assert.DoesNotContain("leftover-secret", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Delete_FallbackHasNoToken_Succeeds(string? remaining)
    {
        _fallback.GetTokenAsync(Host, User, Arg.Any<CancellationToken>()).Returns(remaining);
        var store = new FallbackCredentialStore(_primary, _fallback);

        await store.DeleteTokenAsync(Host, User);

        await _primary.Received(1).DeleteTokenAsync(Host, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_FallbackLookupFails_Succeeds()
    {
        _fallback.GetTokenAsync(Host, User, Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("security timed out"));
        var store = new FallbackCredentialStore(_primary, _fallback);

        await store.DeleteTokenAsync(Host, User);

        await _primary.Received(1).DeleteTokenAsync(Host, User, Arg.Any<CancellationToken>());
    }
}
