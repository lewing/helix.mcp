namespace HelixTool.Core;

/// <summary>
/// Abstraction for storing and retrieving credentials (e.g., API tokens).
/// </summary>
public interface ICredentialStore
{
    /// <summary>Retrieve a stored token for the given host and username, or null if none.</summary>
    Task<string?> GetTokenAsync(string host, string username, CancellationToken ct = default);

    /// <summary>Store a token for the given host and username.</summary>
    Task StoreTokenAsync(string host, string username, string token, CancellationToken ct = default);

    /// <summary>Delete a stored token for the given host and username.</summary>
    Task DeleteTokenAsync(string host, string username, CancellationToken ct = default);
}
