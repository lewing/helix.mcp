// Tests for ICacheStoreFactory (L-HTTP-3).
// CacheStoreFactory maps token hashes to shared ICacheStore instances.
// Uses ConcurrentDictionary internally — thread safety is critical.
// Will compile once Ripley's ICacheStoreFactory.cs and implementation land.

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class CacheStoreFactoryTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CacheStoreFactory _factory;

    public CacheStoreFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hlx-factory-test-{Guid.NewGuid():N}");
        _factory = new CacheStoreFactory();
    }

    public void Dispose()
    {
        // CacheStoreFactory may implement IDisposable to clean up stores
        if (_factory is IDisposable disposable)
            disposable.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    [Fact]
    public void GetOrCreate_SameTokenHash_ReturnsSameInstance()
    {
        var opts1 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "abc12345" };
        var opts2 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "abc12345" };

        var store1 = _factory.GetOrCreate(opts1);
        var store2 = _factory.GetOrCreate(opts2);

        Assert.Same(store1, store2);
    }

    [Fact]
    public void GetOrCreate_DifferentTokenHashes_ReturnsDifferentInstances()
    {
        var opts1 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "hash_aaa" };
        var opts2 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "hash_bbb" };

        var store1 = _factory.GetOrCreate(opts1);
        var store2 = _factory.GetOrCreate(opts2);

        Assert.NotSame(store1, store2);
    }

    [Fact]
    public void GetOrCreate_NullAuthTokenHash_UsesPublicKey()
    {
        var opts1 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = null };
        var opts2 = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = null };

        var store1 = _factory.GetOrCreate(opts1);
        var store2 = _factory.GetOrCreate(opts2);

        // Two calls with null AuthTokenHash should return the same "public" store
        Assert.Same(store1, store2);
    }

    [Fact]
    public void GetOrCreate_NullTokenHash_DifferentFromExplicitHash()
    {
        var publicOpts = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = null };
        var authOpts = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "abc12345" };

        var publicStore = _factory.GetOrCreate(publicOpts);
        var authStore = _factory.GetOrCreate(authOpts);

        Assert.NotSame(publicStore, authStore);
    }

    [Fact]
    public void GetOrCreate_ThreadSafety_ParallelCallsReturnSameInstance()
    {
        var opts = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "concurrent" };
        var stores = new ICacheStore[20];

        Parallel.For(0, stores.Length, i =>
        {
            stores[i] = _factory.GetOrCreate(opts);
        });

        // All entries should be the exact same instance
        for (int i = 1; i < stores.Length; i++)
        {
            Assert.Same(stores[0], stores[i]);
        }
    }

    [Fact]
    public void GetOrCreate_ThreadSafety_DifferentKeys_AllSucceed()
    {
        var stores = new ICacheStore[10];

        Parallel.For(0, stores.Length, i =>
        {
            var opts = new CacheOptions
            {
                CacheRoot = _tempDir,
                AuthTokenHash = $"thread-{i:D4}"
            };
            stores[i] = _factory.GetOrCreate(opts);
        });

        // All stores should be non-null
        foreach (var store in stores)
            Assert.NotNull(store);

        // All stores should be distinct (different hashes)
        var distinct = stores.Distinct().Count();
        Assert.Equal(stores.Length, distinct);
    }

    [Fact]
    public void GetOrCreate_ReturnsICacheStore()
    {
        var opts = new CacheOptions { CacheRoot = _tempDir, AuthTokenHash = "typecheck" };

        var store = _factory.GetOrCreate(opts);

        Assert.IsAssignableFrom<ICacheStore>(store);
    }
}
