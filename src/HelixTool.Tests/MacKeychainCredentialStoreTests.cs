using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

// Uses a fake IProcessRunner; never touches the real macOS Keychain.
public class MacKeychainCredentialStoreTests
{
    private const string Host = "helix.dot.net";
    private const string User = "helix-api-token";

    private readonly IProcessRunner _runner = Substitute.For<IProcessRunner>();

    private void SetupResult(int exitCode, string stdout)
        => _runner.RunAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ProcessRunResult(exitCode, stdout));

    [Fact]
    public async Task GetToken_RunsSecurityFindInternetPasswordForHostAndAccount()
    {
        SetupResult(0, "secret\n");
        var store = new MacKeychainCredentialStore(_runner);

        await store.GetTokenAsync(Host, User);

        await _runner.Received(1).RunAsync(
            "/usr/bin/security",
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "find-internet-password", "-s", Host, "-a", User, "-r", "htps", "-w" })),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("secret\n")]
    [InlineData("secret\r\n")]
    public async Task GetToken_Success_TrimsTrailingNewline(string stdout)
    {
        SetupResult(0, stdout);
        var store = new MacKeychainCredentialStore(_runner);

        Assert.Equal("secret", await store.GetTokenAsync(Host, User));
    }

    [Fact]
    public async Task GetToken_NonZeroExit_ReturnsNull()
    {
        // security exits 44 when the item is not found.
        SetupResult(44, "ignored\n");
        var store = new MacKeychainCredentialStore(_runner);

        Assert.Null(await store.GetTokenAsync(Host, User));
    }

    [Fact]
    public async Task GetToken_EmptyOutput_ReturnsNull()
    {
        SetupResult(0, "\n");
        var store = new MacKeychainCredentialStore(_runner);

        Assert.Null(await store.GetTokenAsync(Host, User));
    }
}
