using System.Diagnostics;
using System.Globalization;
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

internal sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Requires /bin/sleep and /usr/bin/pgrep.";
    }
}

public class ProcessRunnerTests
{
    [UnixOnlyFact]
    public async Task CallerCancellation_ThrowsOperationCanceledAndKillsChild()
    {
        // Unique duration lets pgrep find exactly this child without a PID seam.
        var marker = (3000 + Random.Shared.Next(1, 999_999) / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ProcessRunner().RunAsync("/bin/sleep", [marker], TimeSpan.FromMinutes(1), cts.Token));

        Assert.IsNotType<TimeoutException>(ex);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"Cancellation took {sw.Elapsed}.");

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (IsRunning(marker) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.False(IsRunning(marker), "Child /bin/sleep should be killed on caller cancellation.");
    }

    private static bool IsRunning(string marker)
    {
        using var pgrep = Process.Start(new ProcessStartInfo("/usr/bin/pgrep", ["-f", $"sleep {marker}"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        pgrep.StandardOutput.ReadToEnd();
        pgrep.WaitForExit();
        return pgrep.ExitCode == 0;
    }
}
