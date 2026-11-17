using System.Security.Cryptography;
using System.Text;

namespace HelixTool.Core.Cache;

/// <summary>Configuration for the SQLite-backed cache layer.</summary>
public record CacheOptions
{
    /// <summary>Maximum cache size in bytes. Default: 1 GB. Set to 0 to disable caching.</summary>
    public long MaxSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Cache root directory. Default: platform-appropriate XDG path.</summary>
    public string? CacheRoot { get; set; }

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

    /// <summary>When true, ignore expires_at on all reads and block network access (eval/snapshot mode).</summary>
    public bool EvalMode { get; init; }

    /// <summary>
    /// Resolve the stable cache root for this cache store instance.
    /// In eval mode, returns <see cref="CacheRoot"/> directly (snapshot dir is used as-is).
    /// Shared/public: {base}/public
    /// Partitioned:   {base}/cache-{hash}
    /// </summary>
    public string GetEffectiveCacheRoot()
    {
        if (EvalMode && !string.IsNullOrEmpty(CacheRoot))
            return CacheRoot;
        var baseRoot = GetBaseCacheRoot();
        if (string.IsNullOrEmpty(CacheRootHash))
            return Path.Combine(baseRoot, "public");
        return Path.Combine(baseRoot, $"cache-{CacheRootHash}");
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
    /// Live (non-eval) options for the CLI and stdio MCP hosts. The cache root is partitioned by the
    /// resolved Helix token (<c>{base}/cache-{hash}</c>); a null/empty token uses the shared
    /// <c>{base}/public</c> root. <paramref name="maxSizeMb"/> is the raw <c>HLX_CACHE_MAX_SIZE_MB</c> value.
    /// </summary>
    public static CacheOptions ForHelixToken(string? helixToken, string? maxSizeMb)
    {
        var opts = new CacheOptions { CacheRootHash = ComputeTokenHash(helixToken) };
        if (int.TryParse(maxSizeMb, out var mb))
            opts = opts with { MaxSizeBytes = (long)mb * 1024 * 1024 };
        return opts;
    }

    /// <summary>
    /// Compute a short deterministic hash of an access token or auth context for cache partitioning.
    /// Returns null if token is null/empty.
    /// </summary>
    public static string? ComputeTokenHash(string? token) => ComputeAuthContextHash(token);
}
