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
    /// Optional auth-context hash used to isolate cache data per auth context.
    /// For Helix this is derived from HELIX_ACCESS_TOKEN; for AzDO it is derived from stable credential-source identity metadata.
    /// Null means unauthenticated (public cache).
    /// </summary>
    public string? AuthTokenHash { get; set; }

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
    /// Resolve the auth-context-specific cache root.
    /// Unauthenticated: {base}/public
    /// Authenticated:   {base}/cache-{hash}
    /// </summary>
    public string GetEffectiveCacheRoot()
    {
        var baseRoot = GetBaseCacheRoot();
        if (string.IsNullOrEmpty(AuthTokenHash))
            return Path.Combine(baseRoot, "public");
        return Path.Combine(baseRoot, $"cache-{AuthTokenHash}");
    }

    /// <summary>
    /// Set the auth-context hash if it has not already been established for this cache options instance.
    /// </summary>
    public void TrySetAuthTokenHash(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || !string.IsNullOrEmpty(AuthTokenHash))
            return;

        AuthTokenHash = hash;
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
    /// Compute a short deterministic hash of an access token for cache isolation.
    /// Returns null if token is null/empty.
    /// </summary>
    public static string? ComputeTokenHash(string? token) => ComputeAuthContextHash(token);
}
