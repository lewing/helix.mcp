namespace HelixTool.Core;

/// <summary>
/// Abstract cache storage for API metadata and artifact files.
/// Implementations handle persistence (SQLite+disk) and eviction.
/// </summary>
public interface ICacheStore : IDisposable
{
    /// <summary>Get cached JSON metadata by key, or null if missing/expired.</summary>
    Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default);

    /// <summary>Store JSON metadata with a TTL.</summary>
    Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Get a cached artifact file as a read stream, or null if missing.</summary>
    Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default);

    /// <summary>Store an artifact file on disk, tracked by SQLite.</summary>
    Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default);

    /// <summary>Check whether a job is completed (true), running (false), or unknown (null).</summary>
    Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default);

    /// <summary>Cache job completion state.</summary>
    Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default);

    /// <summary>Delete all cached data (SQLite rows + artifact files).</summary>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>Get cache status summary.</summary>
    Task<CacheStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Remove expired metadata and old artifact files; LRU-evict if over max size.</summary>
    Task EvictExpiredAsync(CancellationToken ct = default);
}
