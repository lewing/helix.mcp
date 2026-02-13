// Integration tests for SqliteCacheStore (L-CACHE-6 through L-CACHE-8).
// Uses temp directories with real SQLite database files for proper integration testing.
// SqliteCacheStore requires file-based SQLite (constructor calls Directory.CreateDirectory).

using HelixTool.Core;
using Xunit;

namespace HelixTool.Tests;

public class SqliteCacheStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly CacheOptions _opts;
    private readonly SqliteCacheStore _store;

    public SqliteCacheStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hlx-test-{Guid.NewGuid():N}");
        _opts = new CacheOptions { CacheRoot = _tempDir };
        _store = new SqliteCacheStore(_opts);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* cleanup best-effort */ }
    }

    // =========================================================================
    // L-CACHE-6: CRUD operations
    // =========================================================================

    // --- Metadata ---

    [Fact]
    public async Task Metadata_SetThenGet_ReturnsStoredValue()
    {
        const string key = "job:abc123:details";
        const string json = "{\"Name\":\"test-job\"}";

        await _store.SetMetadataAsync(key, json, TimeSpan.FromHours(4));
        var result = await _store.GetMetadataAsync(key);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task Metadata_GetNonExistent_ReturnsNull()
    {
        var result = await _store.GetMetadataAsync("nonexistent-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task Metadata_SetTwice_OverwritesPreviousValue()
    {
        const string key = "job:abc123:details";
        await _store.SetMetadataAsync(key, "{\"v\":1}", TimeSpan.FromHours(1));
        await _store.SetMetadataAsync(key, "{\"v\":2}", TimeSpan.FromHours(1));

        var result = await _store.GetMetadataAsync(key);

        Assert.Equal("{\"v\":2}", result);
    }

    [Fact]
    public async Task Metadata_ExpiredEntry_ReturnsNull()
    {
        const string key = "job:expired:details";
        // Set with TTL of 0 (already expired)
        await _store.SetMetadataAsync(key, "{\"old\":true}", TimeSpan.Zero);

        var result = await _store.GetMetadataAsync(key);

        Assert.Null(result);
    }

    // --- Artifacts ---

    [Fact]
    public async Task Artifact_SetThenGet_ReturnsStreamWithSameContent()
    {
        const string key = "job:abc123:wi:test:console";
        var content = System.Text.Encoding.UTF8.GetBytes("console log content here");
        using var inputStream = new MemoryStream(content);

        await _store.SetArtifactAsync(key, inputStream);
        var result = await _store.GetArtifactAsync(key);

        Assert.NotNull(result);
        using var reader = new StreamReader(result!);
        var text = await reader.ReadToEndAsync();
        Assert.Equal("console log content here", text);
    }

    [Fact]
    public async Task Artifact_GetNonExistent_ReturnsNull()
    {
        var result = await _store.GetArtifactAsync("nonexistent-artifact");

        Assert.Null(result);
    }

    // --- Job state ---

    [Fact]
    public async Task JobState_SetCompleted_ReturnsTrue()
    {
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await _store.SetJobCompletedAsync(jobId, completed: true, TimeSpan.FromHours(4));

        var result = await _store.IsJobCompletedAsync(jobId);

        Assert.True(result);
    }

    [Fact]
    public async Task JobState_SetRunning_ReturnsFalse()
    {
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await _store.SetJobCompletedAsync(jobId, completed: false, TimeSpan.FromSeconds(15));

        var result = await _store.IsJobCompletedAsync(jobId);

        Assert.False(result);
    }

    [Fact]
    public async Task JobState_UnknownJob_ReturnsNull()
    {
        var result = await _store.IsJobCompletedAsync("unknown-job-id");

        Assert.Null(result);
    }

    [Fact]
    public async Task JobState_ExpiredEntry_ReturnsNull()
    {
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await _store.SetJobCompletedAsync(jobId, completed: false, TimeSpan.Zero);

        var result = await _store.IsJobCompletedAsync(jobId);

        Assert.Null(result);
    }

    // --- Clear ---

    [Fact]
    public async Task Clear_RemovesAllEntries()
    {
        const string metaKey = "job:abc:details";
        const string jobId = "abc";

        await _store.SetMetadataAsync(metaKey, "{}", TimeSpan.FromHours(1));
        await _store.SetJobCompletedAsync(jobId, true, TimeSpan.FromHours(1));

        await _store.ClearAsync();

        Assert.Null(await _store.GetMetadataAsync(metaKey));
        Assert.Null(await _store.IsJobCompletedAsync(jobId));
    }

    [Fact]
    public async Task Clear_RemovesArtifactFiles()
    {
        const string key = "job:abc:wi:test:console";
        await _store.SetArtifactAsync(key, new MemoryStream(new byte[] { 1, 2, 3 }));

        // Verify file exists before clear
        var beforeClear = await _store.GetArtifactAsync(key);
        Assert.NotNull(beforeClear);
        beforeClear!.Dispose();

        await _store.ClearAsync();

        var afterClear = await _store.GetArtifactAsync(key);
        Assert.Null(afterClear);
    }

    // --- Status ---

    [Fact]
    public async Task GetStatus_EmptyStore_ReturnsZeroCounts()
    {
        var status = await _store.GetStatusAsync();

        Assert.Equal(0, status.MetadataEntryCount);
        Assert.Equal(0, status.ArtifactFileCount);
        Assert.Null(status.OldestEntry);
        Assert.Null(status.NewestEntry);
    }

    [Fact]
    public async Task GetStatus_WithEntries_ReportsCorrectCounts()
    {
        await _store.SetMetadataAsync("job:a:details", "{}", TimeSpan.FromHours(1));
        await _store.SetMetadataAsync("job:b:details", "{}", TimeSpan.FromHours(1));

        var status = await _store.GetStatusAsync();

        Assert.Equal(2, status.MetadataEntryCount);
        Assert.NotNull(status.OldestEntry);
        Assert.NotNull(status.NewestEntry);
    }

    [Fact]
    public async Task GetStatus_ReportsMaxSizeBytes()
    {
        var status = await _store.GetStatusAsync();

        Assert.Equal(_opts.MaxSizeBytes, status.MaxSizeBytes);
    }

    // =========================================================================
    // L-CACHE-7: Eviction — TTL expiry + LRU when over max
    // =========================================================================

    [Fact]
    public async Task EvictExpired_RemovesExpiredMetadata()
    {
        // Set entry with zero TTL (already expired)
        await _store.SetMetadataAsync("job:old:details", "{\"stale\":true}", TimeSpan.Zero);
        // Set entry with long TTL (still valid)
        await _store.SetMetadataAsync("job:fresh:details", "{\"fresh\":true}", TimeSpan.FromHours(4));

        await _store.EvictExpiredAsync();

        Assert.Null(await _store.GetMetadataAsync("job:old:details"));
        Assert.NotNull(await _store.GetMetadataAsync("job:fresh:details"));
    }

    [Fact]
    public async Task EvictExpired_RemovesExpiredJobState()
    {
        await _store.SetJobCompletedAsync("old-job", false, TimeSpan.Zero);
        await _store.SetJobCompletedAsync("fresh-job", true, TimeSpan.FromHours(4));

        await _store.EvictExpiredAsync();

        Assert.Null(await _store.IsJobCompletedAsync("old-job"));
        Assert.True(await _store.IsJobCompletedAsync("fresh-job"));
    }

    [Fact]
    public async Task LruEviction_WhenOverMaxSize_RemovesLeastRecentlyUsed()
    {
        // Use a very small max size to trigger LRU eviction
        var tinyDir = Path.Combine(Path.GetTempPath(), $"hlx-lru-{Guid.NewGuid():N}");
        var tinyOpts = new CacheOptions { CacheRoot = tinyDir, MaxSizeBytes = 100 };
        using var tinyStore = new SqliteCacheStore(tinyOpts);
        try
        {
            // Write artifacts that exceed the max size
            var largeContent = new byte[60];
            Array.Fill(largeContent, (byte)'A');

            await tinyStore.SetArtifactAsync("job:a:wi:test:file:old.bin", new MemoryStream(largeContent));
            // Write a second file — total exceeds 100 bytes → LRU eviction should fire
            await tinyStore.SetArtifactAsync("job:a:wi:test:file:new.bin", new MemoryStream(largeContent));

            // After writing the second artifact, total exceeds 100 bytes → LRU eviction should fire
            var status = await tinyStore.GetStatusAsync();
            Assert.True(status.TotalSizeBytes <= tinyOpts.MaxSizeBytes,
                $"Total size {status.TotalSizeBytes} should be <= max {tinyOpts.MaxSizeBytes} after LRU eviction");
        }
        finally
        {
            tinyStore.Dispose();
            try { Directory.Delete(tinyDir, recursive: true); } catch { }
        }
    }

    // =========================================================================
    // L-CACHE-8: Schema creation idempotent
    // =========================================================================

    [Fact]
    public async Task SchemaCreation_OpenTwice_NoErrors()
    {
        // Opening two SqliteCacheStore instances on the same directory should not throw.
        var sharedDir = Path.Combine(Path.GetTempPath(), $"hlx-schema-{Guid.NewGuid():N}");
        try
        {
            var fileOpts = new CacheOptions { CacheRoot = sharedDir };
            using var store1 = new SqliteCacheStore(fileOpts);
            using var store2 = new SqliteCacheStore(fileOpts); // second open — must not throw

            // Verify both can operate
            await store1.SetMetadataAsync("job:key1:details", "{}", TimeSpan.FromHours(1));
            var result = await store2.GetMetadataAsync("job:key1:details");
            Assert.Equal("{}", result);
        }
        finally
        {
            try { Directory.Delete(sharedDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SchemaCreation_TablesCreatedAutomatically()
    {
        // Verify that a freshly constructed store can perform all operations
        await _store.SetMetadataAsync("job:test:details", "{}", TimeSpan.FromMinutes(5));
        var result = await _store.GetMetadataAsync("job:test:details");
        Assert.Equal("{}", result);

        await _store.SetJobCompletedAsync("j1", true, TimeSpan.FromMinutes(5));
        Assert.True(await _store.IsJobCompletedAsync("j1"));

        await _store.SetArtifactAsync("job:test:wi:w1:console", new MemoryStream(new byte[] { 42 }));
        var artifact = await _store.GetArtifactAsync("job:test:wi:w1:console");
        Assert.NotNull(artifact);
        artifact!.Dispose();
    }
}
