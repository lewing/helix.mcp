// Integration tests for SqliteCacheStore (L-CACHE-6 through L-CACHE-8).
// Uses temp directories with real SQLite database files for proper integration testing.
// SqliteCacheStore requires file-based SQLite (constructor calls Directory.CreateDirectory).

using HelixTool.Core;
using HelixTool.Core.Cache;
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

// =========================================================================
// Eval mode integration tests for SqliteCacheStore.
// Seeding uses a normal (non-eval) writer against a parent dir so the DB
// lands at  parentDir/public/cache.db = snapshotDir/cache.db.
// The eval store opens snapshotDir directly (EvalMode bypasses the /public suffix).
// =========================================================================

public class SqliteCacheStoreEvalModeTests : IDisposable
{
    // _parentDir:   normal-mode CacheRoot  → effective root = _parentDir/public
    // _snapshotDir: eval-mode  CacheRoot   → effective root = _snapshotDir  (no suffix)
    // Both resolve to the same physical cache.db.
    private readonly string _parentDir;
    private readonly string _snapshotDir; // = _parentDir/public

    public SqliteCacheStoreEvalModeTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-eval-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { /* best-effort */ }
    }

    // Writer: normal mode, CacheRoot = _parentDir → DB at _parentDir/public/cache.db = _snapshotDir/cache.db
    private SqliteCacheStore CreateWriterStore()
        => new(new CacheOptions { CacheRoot = _parentDir, EvalMode = false });

    // Eval store: EvalMode, CacheRoot = _snapshotDir → DB at _snapshotDir/cache.db (same path)
    private SqliteCacheStore OpenEvalStore()
        => new(new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true });

    private string DbPath => Path.Combine(_snapshotDir, "cache.db");

    // =========================================================================
    // TTL bypass
    // =========================================================================

    [Fact]
    public async Task EvalMode_Metadata_ExpiredEntry_ReturnsValue()
    {
        using var writer = CreateWriterStore();
        const string key = "job:abc123:details";
        const string json = "{\"Name\":\"expired-job\"}";
        // Delay to let the fire-and-forget background eviction complete on the empty DB
        // before we write a TimeSpan.Zero-TTL row (otherwise eviction races the write).
        await Task.Delay(30);
        await writer.SetMetadataAsync(key, json, TimeSpan.Zero); // expires immediately
        writer.Dispose();

        using var evalStore = OpenEvalStore();
        var result = await evalStore.GetMetadataAsync(key);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task NormalMode_Metadata_ExpiredEntry_ReturnsNull()
    {
        // Regression: normal mode must still honour TTL.
        using var store = CreateWriterStore();
        await store.SetMetadataAsync("job:abc123:details", "{}", TimeSpan.Zero);

        Assert.Null(await store.GetMetadataAsync("job:abc123:details"));
    }

    [Fact]
    public async Task EvalMode_JobState_ExpiredEntry_ReturnsValue()
    {
        using var writer = CreateWriterStore();
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await Task.Delay(30); // wait for background eviction on empty DB
        await writer.SetJobCompletedAsync(jobId, completed: true, TimeSpan.Zero);
        writer.Dispose();

        using var evalStore = OpenEvalStore();
        var result = await evalStore.IsJobCompletedAsync(jobId);

        Assert.True(result);
    }

    [Fact]
    public async Task NormalMode_JobState_ExpiredEntry_ReturnsNull()
    {
        using var store = CreateWriterStore();
        const string jobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
        await store.SetJobCompletedAsync(jobId, completed: false, TimeSpan.Zero);

        Assert.Null(await store.IsJobCompletedAsync(jobId));
    }

    // =========================================================================
    // EvictExpiredAsync is a no-op in eval mode
    // =========================================================================

    [Fact]
    public async Task EvalMode_EvictExpired_IsNoOp_ExpiredEntriesRemain()
    {
        using var writer = CreateWriterStore();
        const string key = "job:old123:details";
        const string jobId = "old-job-id";
        await Task.Delay(30); // wait for background eviction on empty DB before writing zero-TTL rows
        await writer.SetMetadataAsync(key, "{\"stale\":true}", TimeSpan.Zero);
        await writer.SetJobCompletedAsync(jobId, true, TimeSpan.Zero);
        writer.Dispose();

        using var evalStore = OpenEvalStore();
        await evalStore.EvictExpiredAsync();

        // Both entries must survive the no-op eviction.
        Assert.NotNull(await evalStore.GetMetadataAsync(key));
        Assert.True(await evalStore.IsJobCompletedAsync(jobId));
    }

    [Fact]
    public async Task NormalMode_EvictExpired_RemovesExpiredRows()
    {
        // Regression: normal store still evicts expired rows.
        using var store = CreateWriterStore();
        await store.SetMetadataAsync("job:abc:details", "{}", TimeSpan.Zero);
        await store.EvictExpiredAsync();

        Assert.Null(await store.GetMetadataAsync("job:abc:details"));
    }

    // =========================================================================
    // Eval mode writes are no-ops (snapshot is read-only)
    // =========================================================================

    [Fact]
    public async Task EvalMode_SetMetadata_IsNoOp_DoesNotPersist()
    {
        // Seed a valid entry first.
        using var writer = CreateWriterStore();
        const string key = "job:abc123:details";
        await writer.SetMetadataAsync(key, "{\"original\":true}", TimeSpan.FromHours(4));
        writer.Dispose();

        // In eval mode, SetMetadataAsync must not overwrite.
        using var evalStore = OpenEvalStore();
        await evalStore.SetMetadataAsync(key, "{\"tampered\":true}", TimeSpan.FromHours(4));
        var result = await evalStore.GetMetadataAsync(key);

        Assert.Contains("original", result!);
        Assert.DoesNotContain("tampered", result!);
    }

    [Fact]
    public async Task EvalMode_SetJobCompleted_IsNoOp_DoesNotPersist()
    {
        using var writer = CreateWriterStore();
        const string jobId = "job-abc-123";
        await writer.SetJobCompletedAsync(jobId, completed: true, TimeSpan.FromHours(4));
        writer.Dispose();

        using var evalStore = OpenEvalStore();
        await evalStore.SetJobCompletedAsync(jobId, completed: false, TimeSpan.FromHours(4));
        var result = await evalStore.IsJobCompletedAsync(jobId);

        // Original (true) must win — the write was discarded.
        Assert.True(result);
    }

    // =========================================================================
    // Artifact read does NOT update last_accessed in eval mode
    // =========================================================================

    [Fact]
    public async Task EvalMode_ArtifactRead_DoesNotMutateLastAccessed()
    {
        // Seed an artifact via writer.
        using var writer = CreateWriterStore();
        const string key = "job:abc123:wi:test-wi:console";
        await writer.SetArtifactAsync(key, new MemoryStream(System.Text.Encoding.UTF8.GetBytes("log")));
        writer.Dispose();

        // Force a known old timestamp directly in the DB.
        const string oldTimestamp = "2020-01-01T00:00:00.0000000+00:00";
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE cache_artifacts SET last_accessed = @ts WHERE cache_key = @key;";
            cmd.Parameters.AddWithValue("@ts", oldTimestamp);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.ExecuteNonQuery();
        }

        // Read via eval store.
        using var evalStore = OpenEvalStore();
        using var stream = await evalStore.GetArtifactAsync(key);
        Assert.NotNull(stream);
        stream!.Dispose();
        evalStore.Dispose();

        // Verify last_accessed was NOT updated.
        string? lastAccessed;
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT last_accessed FROM cache_artifacts WHERE cache_key = @key;";
            cmd.Parameters.AddWithValue("@key", key);
            lastAccessed = cmd.ExecuteScalar() as string;
        }

        Assert.Equal(oldTimestamp, lastAccessed);
    }

    // =========================================================================
    // Schema mismatch: eval mode throws instead of destructive migration
    // =========================================================================

    [Fact]
    public void EvalMode_WrongSchemaVersion_ThrowsInvalidOperationException()
    {
        // Create a DB with wrong user_version=99.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        var opts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var ex = Assert.Throws<InvalidOperationException>(() => new SqliteCacheStore(opts));
        Assert.Contains("schema", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalMode_WrongSchemaVersion_MigratesDestructively_NoException()
    {
        // Normal mode drops and recreates — must not throw.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        using var store = CreateWriterStore();
        // Must be usable after silent migration.
        await store.SetMetadataAsync("job:k:details", "{}", TimeSpan.FromHours(1));
        Assert.NotNull(await store.GetMetadataAsync("job:k:details"));
    }

    // =========================================================================
    // WAL/SHM cleanup on open in eval mode
    // =========================================================================

    [Fact]
    public async Task EvalMode_AfterWriterClose_DataReadableInEvalMode()
    {
        // Seed a valid DB via writer (establishes WAL mode; WAL/SHM files may or may not
        // remain after the writer disposes, depending on SQLite checkpoint behaviour).
        using (var writer = CreateWriterStore())
        {
            await writer.SetMetadataAsync("job:x:details", "{}", TimeSpan.FromHours(4));
        }

        // Open in eval mode: must not throw and must expose the committed data.
        // SQLite Mode=ReadOnly follows any valid WAL frames without checkpointing or deleting them.
        using var evalStore = OpenEvalStore();

        Assert.NotNull(await evalStore.GetMetadataAsync("job:x:details"));
    }
}
