namespace HelixTool.Core;

/// <summary>
/// Read-only macOS Keychain lookup via <c>/usr/bin/security find-internet-password</c>.
/// Used as a fallback when <c>git credential fill</c> cannot reach the keychain (for example,
/// when a bundled git without <c>credential.helper</c> is first on PATH). Reads the internet
/// password that <c>git-credential-osxkeychain</c> stores for the host/account pair.
/// </summary>
public sealed class MacKeychainCredentialStore : ICredentialStore
{
    internal const string SecurityPath = "/usr/bin/security";
    internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);

    private readonly IProcessRunner _processRunner;

    public MacKeychainCredentialStore(IProcessRunner? processRunner = null)
    {
        _processRunner = processRunner ?? new ProcessRunner();
    }

    /// <summary>
    /// Returns the stored password, or null if <c>security</c> exits non-zero or prints nothing.
    /// Throws if the process cannot be started or times out.
    /// </summary>
    public async Task<string?> GetTokenAsync(string host, string username, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(username);

        var result = await _processRunner.RunAsync(
            SecurityPath,
            ["find-internet-password", "-s", host, "-a", username, "-w"],
            ProcessTimeout,
            ct);

        if (result.ExitCode != 0)
            return null;

        var token = result.StandardOutput.TrimEnd('\r', '\n');
        return token.Length == 0 ? null : token;
    }

    /// <summary>Not supported; this store is read-only. Use <see cref="GitCredentialStore"/> to store.</summary>
    public Task StoreTokenAsync(string host, string username, string token, CancellationToken ct = default)
        => throw new NotSupportedException("MacKeychainCredentialStore is read-only.");

    /// <summary>Not supported; this store is read-only. Use <see cref="GitCredentialStore"/> to delete.</summary>
    public Task DeleteTokenAsync(string host, string username, CancellationToken ct = default)
        => throw new NotSupportedException("MacKeychainCredentialStore is read-only.");
}
