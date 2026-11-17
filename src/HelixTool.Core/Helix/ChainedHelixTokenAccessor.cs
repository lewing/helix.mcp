namespace HelixTool.Core.Helix;

/// <summary>Indicates where the Helix token was resolved from.</summary>
public enum TokenSource
{
    None,
    EnvironmentVariable,
    StoredCredential
}

/// <summary>
/// Token accessor that chains: env var → stored credential → null.
/// Used by CLI and stdio MCP modes. A credential-store failure (e.g. git not on PATH)
/// resolves to <see cref="TokenSource.None"/> rather than throwing.
/// </summary>
public sealed class ChainedHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly string? _envToken;
    private readonly ICredentialStore _store;
    private string? _cachedToken;
    private bool _resolved;

    public TokenSource Source { get; private set; }

    public ChainedHelixTokenAccessor(ICredentialStore store)
        : this(store, Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN"))
    {
    }

    internal ChainedHelixTokenAccessor(ICredentialStore store, string? envToken)
    {
        _envToken = envToken;
        _store = store;
    }

    public string? GetAccessToken()
    {
        if (!string.IsNullOrEmpty(_envToken))
        {
            Source = TokenSource.EnvironmentVariable;
            return _envToken;
        }

        if (!_resolved)
        {
            try
            {
                _cachedToken = _store.GetTokenAsync("helix.dot.net", "helix-api-token")
                    .GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                _cachedToken = null;
            }
            _resolved = true;
            Source = _cachedToken is not null ? TokenSource.StoredCredential : TokenSource.None;
        }

        return _cachedToken;
    }
}
