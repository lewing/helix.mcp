using System.Collections.Concurrent;

namespace HelixTool.Core;

/// <summary>
/// Factory for obtaining cache stores by auth context.
/// Needed for HTTP mode where multiple auth contexts coexist in one process.
/// Thread-safe: concurrent requests with the same token share the same cache store.
/// </summary>
public interface ICacheStoreFactory
{
    ICacheStore GetOrCreate(CacheOptions options);
}

public sealed class CacheStoreFactory : ICacheStoreFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, ICacheStore> _stores = new();

    public ICacheStore GetOrCreate(CacheOptions options)
    {
        var key = options.AuthTokenHash ?? "public";
        return _stores.GetOrAdd(key, _ => new SqliteCacheStore(options));
    }

    public void Dispose()
    {
        foreach (var store in _stores.Values)
            store.Dispose();
        _stores.Clear();
    }
}
