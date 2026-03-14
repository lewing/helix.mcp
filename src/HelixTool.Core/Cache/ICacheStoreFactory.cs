using System.Collections.Concurrent;

namespace HelixTool.Core.Cache;

/// <summary>
/// Factory for obtaining cache stores by stable cache-root partition.
/// Needed for HTTP mode where multiple auth contexts coexist in one process.
/// Thread-safe: concurrent requests with the same effective cache root share the same cache store.
/// </summary>
public interface ICacheStoreFactory
{
    ICacheStore GetOrCreate(CacheOptions options);
}

public sealed class CacheStoreFactory : ICacheStoreFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<ICacheStore>> _stores = new();

    public ICacheStore GetOrCreate(CacheOptions options)
    {
        var key = options.GetEffectiveCacheRoot();
        return _stores.GetOrAdd(key, _ => new Lazy<ICacheStore>(
            () => new SqliteCacheStore(options))).Value;
    }

    public void Dispose()
    {
        foreach (var lazy in _stores.Values)
        {
            if (lazy.IsValueCreated)
                lazy.Value.Dispose();
        }
        _stores.Clear();
    }
}
