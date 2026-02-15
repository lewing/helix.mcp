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

    // =========================================================================
    // Stale row cleanup — file deleted from disk
    // =========================================================================

    [Fact]
    public async Task StaleRowCleanup_FileDeletedFromDisk_ReturnsNullAndCleansUp()
    {
        const string key = "job:stale-row:wi:test:file:data.bin";
        var content = new byte[64];
        Array.Fill(content, (byte)'S');

        // Write an artifact
        await _store.SetArtifactAsync(key, new MemoryStream(content));

        // Verify it's readable
        using (var stream = await _store.GetArtifactAsync(key))
        {
            Assert.NotNull(stream);
        }

        // Delete the artifact file from disk (simulating external cleanup or eviction crash)
        var artifactsDir = Path.Combine(_opts.GetEffectiveCacheRoot(), "artifacts");
        var files = Directory.GetFiles(artifactsDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase)
                     && !f.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var file in files)
            File.Delete(file);

        // First call: should detect missing file, clean up orphan row, return null
        var result1 = await _store.GetArtifactAsync(key);
        Assert.Null(result1);

        // Second call: orphan row should already be cleaned up — fast null
        var result2 = await _store.GetArtifactAsync(key);
        Assert.Null(result2);
    }

    // =========================================================================
    // Eviction during read — open stream remains readable (FileShare.Delete)
    // =========================================================================

    [Fact]
    public async Task EvictionDuringRead_OpenStreamRemainsReadable()
    {
        // Use a separate store with a small MaxSizeBytes to trigger LRU eviction
        var smallOpts = new CacheOptions
        {
            CacheRoot = Path.Combine(Path.GetTempPath(), $"hlx-evict-read-{Guid.NewGuid():N}"),
            MaxSizeBytes = 2048
        };
        using var smallStore = new SqliteCacheStore(smallOpts);

        try
        {
            // Write a 1KB artifact
            const string key = "job:evict-read:wi:test:file:target.bin";
            var content = new byte[1024];
            Array.Fill(content, (byte)'R');
            await smallStore.SetArtifactAsync(key, new MemoryStream(content));

            // Open a read stream (holds the file handle with FileShare.Delete)
            using var readStream = await smallStore.GetArtifactAsync(key);
            Assert.NotNull(readStream);

            // Trigger LRU eviction by writing enough data to exceed MaxSizeBytes
            for (int i = 0; i < 5; i++)
            {
                var filler = new byte[512];
                Array.Fill(filler, (byte)('0' + i));
                await smallStore.SetArtifactAsync($"job:evict-read:wi:test:file:filler{i}.bin", new MemoryStream(filler));
            }

            // The original file may have been evicted from disk, but the open handle
            // should still allow reading (FileShare.Delete behavior on Windows)
            var buffer = new byte[2048];
            var totalRead = 0;
            int bytesRead;
            while ((bytesRead = await readStream.ReadAsync(buffer.AsMemory(totalRead))) > 0)
                totalRead += bytesRead;

            Assert.Equal(1024, totalRead);
            Assert.All(buffer[..1024], b => Assert.Equal((byte)'R', b));
        }
        finally
        {
            smallStore.Dispose();
            try { Directory.Delete(smallOpts.CacheRoot!, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // Concurrent eviction and write — artifact integrity under pressure
    // =========================================================================

    [Fact]
    public async Task ConcurrentEvictionAndWrite_ArtifactIntegrity()
    {
        // Use a separate store with very small capacity to force frequent eviction
        var tinyOpts = new CacheOptions
        {
            CacheRoot = Path.Combine(Path.GetTempPath(), $"hlx-evict-write-{Guid.NewGuid():N}"),
            MaxSizeBytes = 2048
        };
        using var tinyStore = new SqliteCacheStore(tinyOpts);

        try
        {
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            // Concurrently write 20 artifacts (~256 bytes each) and read random ones
            var tasks = Enumerable.Range(0, 40).Select(async i =>
            {
                try
                {
                    if (i < 20)
                    {
                        // Writer: write 256-byte artifacts with distinct fill byte
                        var data = new byte[256];
                        Array.Fill(data, (byte)('A' + (i % 26)));
                        await tinyStore.SetArtifactAsync(
                            $"job:evict-integrity:wi:test:file:item{i}.bin",
                            new MemoryStream(data));
                    }
                    else
                    {
                        // Reader: read a random artifact (may have been evicted)
                        var idx = i % 20;
                        Stream? stream = null;
                        try
                        {
                            stream = await tinyStore.GetArtifactAsync(
                                $"job:evict-integrity:wi:test:file:item{idx}.bin");
                        }
                        catch (FileNotFoundException)
                        {
                            // Known race: file evicted between File.Exists check and FileStream open.
                            // This is the exact concurrency gap the audit identified — tolerate it.
                        }

                        if (stream != null)
                        {
                            using (stream)
                            {
                                using var ms = new MemoryStream();
                                await stream.CopyToAsync(ms);
                                var bytes = ms.ToArray();
                                // Non-null reads must return non-empty, non-corrupted data
                                Assert.True(bytes.Length > 0, "Non-null stream should contain data");
                                // All bytes should be the same fill value (integrity check)
                                var fill = bytes[0];
                                Assert.All(bytes, b => Assert.Equal(fill, b));
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Known concurrency gaps during eviction:
                    // - FileNotFoundException: file evicted between File.Exists and FileStream open
                    // - UnauthorizedAccessException: File.Delete during eviction while another
                    //   thread holds the file open (Windows-specific behavior)
                    // These are expected under high concurrent eviction pressure.
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            await Task.WhenAll(tasks);

            // No exceptions should have been thrown
            Assert.Empty(exceptions);
        }
        finally
        {
            tinyStore.Dispose();
            try { Directory.Delete(tinyOpts.CacheRoot!, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // Concurrent same-key writes — data integrity
    // =========================================================================

    [Fact]
    public async Task ConcurrentCachingClientSimulation_SameKey()
    {
        const string key = "job:same-key-artifact:wi:test:file:shared.bin";
        var contentA = new byte[128];
        Array.Fill(contentA, (byte)'A');
        var contentB = new byte[128];
        Array.Fill(contentB, (byte)'B');

        // Two concurrent SetArtifactAsync calls with the same key but different content
        var taskA = Task.Run(() => _store.SetArtifactAsync(key, new MemoryStream(contentA)));
        var taskB = Task.Run(() => _store.SetArtifactAsync(key, new MemoryStream(contentB)));

        await Task.WhenAll(taskA, taskB);

        // Read the artifact — should be one of the two values (either is acceptable)
        using var result = await _store.GetArtifactAsync(key);
        Assert.NotNull(result);

        using var ms = new MemoryStream();
        await result.CopyToAsync(ms);
        var bytes = ms.ToArray();

        // Must be complete (128 bytes) and uncorrupted (all same fill byte)
        Assert.Equal(128, bytes.Length);
        var fillByte = bytes[0];
        Assert.True(fillByte == (byte)'A' || fillByte == (byte)'B',
            $"Expected fill byte 'A' or 'B', got '{(char)fillByte}'");
        Assert.All(bytes, b => Assert.Equal(fillByte, b));
    }
}
