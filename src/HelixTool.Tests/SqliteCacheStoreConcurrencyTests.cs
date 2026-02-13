// Concurrency tests for SqliteCacheStore (L-HTTP-4).
// Exercises the connection-per-operation pattern under concurrent access.
// These tests validate that SqliteCacheStore is safe for HTTP/SSE multi-client scenarios
// where multiple requests hit the same cache store simultaneously.

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class SqliteCacheStoreConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CacheOptions _opts;
    private readonly SqliteCacheStore _store;

    public SqliteCacheStoreConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hlx-concurrency-{Guid.NewGuid():N}");
        _opts = new CacheOptions { CacheRoot = _tempDir };
        _store = new SqliteCacheStore(_opts);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    // =========================================================================
    // Concurrent reads
    // =========================================================================

    [Fact]
    public async Task ConcurrentReads_DoNotThrow()
    {
        // Seed some data
        const string key = "job:concurrent-read:details";
        await _store.SetMetadataAsync(key, "{\"data\":\"test\"}", TimeSpan.FromHours(1));

        // Read concurrently from multiple threads
        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var result = await _store.GetMetadataAsync(key);
            Assert.Equal("{\"data\":\"test\"}", result);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentReads_NonExistentKeys_ReturnNull()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var result = await _store.GetMetadataAsync($"nonexistent-key-{i}");
            Assert.Null(result);
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentJobStateReads_DoNotThrow()
    {
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await _store.SetJobCompletedAsync(jobId, true, TimeSpan.FromHours(4));

        var tasks = Enumerable.Range(0, 20).Select(async _ =>
        {
            var result = await _store.IsJobCompletedAsync(jobId);
            Assert.True(result);
        });

        await Task.WhenAll(tasks);
    }

    // =========================================================================
    // Concurrent writes
    // =========================================================================

    [Fact]
    public async Task ConcurrentWrites_DifferentKeys_DoNotCorrupt()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var key = $"job:concurrent-write-{i}:details";
            await _store.SetMetadataAsync(key, $"{{\"i\":{i}}}", TimeSpan.FromHours(1));
        });

        await Task.WhenAll(tasks);

        // Verify all entries were written correctly
        for (int i = 0; i < 20; i++)
        {
            var result = await _store.GetMetadataAsync($"job:concurrent-write-{i}:details");
            Assert.Equal($"{{\"i\":{i}}}", result);
        }
    }

    [Fact]
    public async Task ConcurrentWrites_SameKey_LastWriteWins()
    {
        const string key = "job:same-key:details";

        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            await _store.SetMetadataAsync(key, $"{{\"writer\":{i}}}", TimeSpan.FromHours(1));
        });

        await Task.WhenAll(tasks);

        // One of the writers should have won — verify the result is valid JSON
        var result = await _store.GetMetadataAsync(key);
        Assert.NotNull(result);
        Assert.StartsWith("{\"writer\":", result);
    }

    [Fact]
    public async Task ConcurrentJobStateWrites_DoNotCorrupt()
    {
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var jobId = $"concurrent-job-{i}";
            await _store.SetJobCompletedAsync(jobId, i % 2 == 0, TimeSpan.FromHours(4));
        });

        await Task.WhenAll(tasks);

        // Verify all job states were recorded
        for (int i = 0; i < 20; i++)
        {
            var result = await _store.IsJobCompletedAsync($"concurrent-job-{i}");
            Assert.NotNull(result);
            Assert.Equal(i % 2 == 0, result);
        }
    }

    // =========================================================================
    // Concurrent read + write
    // =========================================================================

    [Fact]
    public async Task ConcurrentReadAndWrite_IsSafe()
    {
        const string key = "job:rw-test:details";
        await _store.SetMetadataAsync(key, "{\"initial\":true}", TimeSpan.FromHours(1));

        // Mix reads and writes concurrently
        var tasks = Enumerable.Range(0, 30).Select(async i =>
        {
            if (i % 2 == 0)
            {
                // Writer
                await _store.SetMetadataAsync(key, $"{{\"iteration\":{i}}}", TimeSpan.FromHours(1));
            }
            else
            {
                // Reader — should get a valid JSON value (not corrupted)
                var result = await _store.GetMetadataAsync(key);
                // Result can be any of the written values (or null if expired between write+read)
                // The key test is that it doesn't throw or return garbage
                if (result != null)
                    Assert.StartsWith("{\"", result);
            }
        });

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task ConcurrentArtifactReadAndWrite_IsSafe()
    {
        const string key = "job:artifact-rw:wi:test:file:data.bin";
        var content = new byte[64];
        Array.Fill(content, (byte)'X');

        // Write initial artifact
        await _store.SetArtifactAsync(key, new MemoryStream(content));

        // Mix reads and writes concurrently
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            if (i % 3 == 0)
            {
                // Writer
                var data = new byte[64];
                Array.Fill(data, (byte)('A' + (i % 26)));
                await _store.SetArtifactAsync(key, new MemoryStream(data));
            }
            else
            {
                // Reader
                var stream = await _store.GetArtifactAsync(key);
                if (stream != null)
                {
                    using (stream)
                    {
                        var buffer = new byte[1024];
                        var read = await stream.ReadAsync(buffer);
                        // Should read some content — the exact value depends on timing
                        Assert.True(read > 0);
                    }
                }
            }
        });

        await Task.WhenAll(tasks);
    }

    // =========================================================================
    // Concurrent status + eviction
    // =========================================================================

    [Fact]
    public async Task ConcurrentStatusAndEviction_IsSafe()
    {
        // Seed data
        for (int i = 0; i < 10; i++)
            await _store.SetMetadataAsync($"job:evict-{i}:details", $"{{\"i\":{i}}}", TimeSpan.FromHours(1));

        // Run status checks and eviction concurrently
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            if (i % 4 == 0)
                await _store.EvictExpiredAsync();
            else
                await _store.GetStatusAsync();
        });

        await Task.WhenAll(tasks);
    }

    // =========================================================================
    // Separate SqliteCacheStore instances on same DB (simulates process-level concurrency)
    // =========================================================================

    [Fact]
    public async Task TwoStoreInstances_SameDb_ConcurrentAccess_IsSafe()
    {
        // Two separate SqliteCacheStore instances pointing at the same directory
        // simulates two scoped resolutions in HTTP mode sharing the same cache
        using var store2 = new SqliteCacheStore(_opts);

        var tasks = new List<Task>();

        // Store1 writes, Store2 reads
        for (int i = 0; i < 10; i++)
        {
            var idx = i;
            tasks.Add(Task.Run(async () =>
            {
                await _store.SetMetadataAsync($"job:dual-{idx}:details", $"{{\"s\":1}}", TimeSpan.FromHours(1));
            }));
            tasks.Add(Task.Run(async () =>
            {
                // May return null (written by store1 hasn't committed yet) — that's OK
                await store2.GetMetadataAsync($"job:dual-{idx}:details");
            }));
        }

        await Task.WhenAll(tasks);

        // After all tasks complete, store2 should be able to read at least some entries
        var found = 0;
        for (int i = 0; i < 10; i++)
        {
            var result = await store2.GetMetadataAsync($"job:dual-{i}:details");
            if (result != null) found++;
        }
        Assert.True(found > 0, "Store2 should read at least some entries written by store1");
    }
}
