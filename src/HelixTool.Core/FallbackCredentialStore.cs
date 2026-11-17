namespace HelixTool.Core;

/// <summary>
/// Composite <see cref="ICredentialStore"/>: reads from <c>primary</c> and, when that returns null
/// or throws, from an optional read-only <c>readFallback</c>. Store and delete always go to <c>primary</c>.
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

    public Task DeleteTokenAsync(string host, string username, CancellationToken ct = default)
        => _primary.DeleteTokenAsync(host, username, ct);
}
