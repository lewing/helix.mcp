using System.Security.Cryptography;
using System.Text;

namespace HelixTool.Core.Cache;

/// <summary>Configuration for the SQLite-backed cache layer.</summary>
public record CacheOptions
{
    /// <summary>Maximum cache size in bytes. Default: 1 GB. Set to 0 to disable caching.</summary>
    public long MaxSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Cache root directory. Default: platform-appropriate XDG path.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>Artifact expiry (last access). Default: 7 days.</summary>
    public TimeSpan ArtifactMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Optional stable cache-root partition hash established before the cache store is created.
    /// Used by Helix request-scoped hosts to keep different token contexts on separate cache roots.
    /// Null means the shared public cache root.
    /// </summary>
    public string? CacheRootHash { get; init; }

    /// <summary>
    /// Optional AzDO auth-context hash used to isolate AzDO cache keys inside a cache store.
    /// This is intentionally mutable so AzDO can update its key partition when the resolved credential changes.
    /// Null means unauthenticated/public AzDO cache keys.
    /// </summary>
    public string? AuthTokenHash { get; set; }

    /// <summary>
    /// Last resolved stable auth-context identity used to derive <see cref="AuthTokenHash"/>.
    /// Null means the current AzDO cache partition is the shared public context.
    /// </summary>
    public string? AuthCacheIdentity { get; private set; }

    /// <summary>Resolve the base cache directory (before auth context subdivision).</summary>
    public string GetBaseCacheRoot()
    {
        if (!string.IsNullOrEmpty(CacheRoot)) return CacheRoot;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hlx");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(
            !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "hlx");
    }

    /// <summary>
    /// Resolve the stable cache root for this cache store instance.
    /// Shared/public: {base}/public
    /// Partitioned:   {base}/cache-{hash}
    /// </summary>
    public string GetEffectiveCacheRoot()
    {
        var baseRoot = GetBaseCacheRoot();
        if (string.IsNullOrEmpty(CacheRootHash))
            return Path.Combine(baseRoot, "public");
        return Path.Combine(baseRoot, $"cache-{CacheRootHash}");
    }

    /// <summary>
    /// Set the auth-context hash when a new non-empty value is established for this cache options instance.
    /// </summary>
    public void TrySetAuthTokenHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || string.Equals(AuthTokenHash, hash, StringComparison.Ordinal))
            return;

        AuthTokenHash = hash;
    }

    /// <summary>
    /// Update the resolved AzDO auth context and the derived cache-key hash.
    /// </summary>
    public void UpdateAuthContext(string? authContext)
    {
        var authHash = ComputeAuthContextHash(authContext);
        if (string.Equals(AuthCacheIdentity, authContext, StringComparison.Ordinal) &&
            string.Equals(AuthTokenHash, authHash, StringComparison.Ordinal))
        {
            return;
        }

        AuthCacheIdentity = authContext;
        AuthTokenHash = authHash;
    }

    /// <summary>
    /// Compute a short deterministic hash for any stable auth-context string.
    /// Returns null if the input is null/empty.
    /// </summary>
    public static string? ComputeAuthContextHash(string? authContext)
    {
        if (string.IsNullOrEmpty(authContext)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authContext));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Compute a short deterministic hash of an access token or auth context for cache partitioning.
    /// Returns null if token is null/empty.
    /// </summary>
    public static string? ComputeTokenHash(string? token) => ComputeAuthContextHash(token);
}
