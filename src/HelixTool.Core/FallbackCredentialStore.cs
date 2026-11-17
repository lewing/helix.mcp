namespace HelixTool.Core;

/// <summary>
/// Composite <see cref="ICredentialStore"/>: reads from <c>primary</c> and, when that returns null
/// or throws, from an optional read-only <c>readFallback</c>. Store and delete always go to <c>primary</c>;
/// delete then verifies the credential is no longer readable through <c>readFallback</c>.
/// </summary>
public sealed class FallbackCredentialStore : ICredentialStore
{
    private readonly ICredentialStore _primary;
    private readonly ICredentialStore? _readFallback;

    public FallbackCredentialStore(ICredentialStore primary, ICredentialStore? readFallback)
    {
        ArgumentNullException.ThrowIfNull(primary);
        _primary = primary;
        _readFallback = readFallback;
    }

    /// <summary>
    /// Default store for CLI and stdio MCP: <see cref="GitCredentialStore"/>, plus a
    /// <see cref="MacKeychainCredentialStore"/> read fallback on macOS.
    /// </summary>
    public static ICredentialStore CreateDefault()
        => new FallbackCredentialStore(
            new GitCredentialStore(),
            OperatingSystem.IsMacOS() ? new MacKeychainCredentialStore() : null);

    /// <summary>
    /// Returns the primary's token if non-null. Otherwise (null, or a non-cancellation exception)
    /// returns the fallback's result; fallback exceptions propagate. With no fallback, a primary
    /// exception is rethrown.
    /// </summary>
    public async Task<string?> GetTokenAsync(string host, string username, CancellationToken ct = default)
    {
        var fallback = _readFallback;
        try
        {
            var token = await _primary.GetTokenAsync(host, username, ct);
            if (token is not null || fallback is null)
                return token;
        }
        catch (Exception ex) when (fallback is not null && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
        }

        return await fallback.GetTokenAsync(host, username, ct);
    }

    public Task StoreTokenAsync(string host, string username, string token, CancellationToken ct = default)
        => _primary.StoreTokenAsync(host, username, token, ct);

    /// <summary>
    /// Deletes via the primary, then checks the read fallback. Throws <see cref="InvalidOperationException"/>
    /// if the fallback still returns a non-empty token (e.g. git could not reach the macOS Keychain). A fallback
    /// lookup failure other than cancellation is ignored.
    /// </summary>
    public async Task DeleteTokenAsync(string host, string username, CancellationToken ct = default)
    {
        await _primary.DeleteTokenAsync(host, username, ct);

        if (_readFallback is null)
            return;

        string? remaining;
        try
        {
            remaining = await _readFallback.GetTokenAsync(host, username, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            return;
        }

        if (!string.IsNullOrEmpty(remaining))
        {
            throw new InvalidOperationException(
                $"The credential for {host} is still present in the OS keychain after 'git credential reject' " +
                "(git may not be able to reach the keychain in this environment). " +
                $"On macOS, remove it with: security delete-internet-password -s {host} -a {username} -r htps " +
                "— or run 'hlx logout' from a regular terminal.");
        }
    }
}
