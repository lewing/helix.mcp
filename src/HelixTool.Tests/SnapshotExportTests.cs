// Tests for snapshot export (SnapshotExporter) and snapshot validation (SnapshotValidator).
// Uses real SQLite databases in temp directories — no mocks for the file/SQLite layer.

using System.Globalization;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HelixTool.Tests;

// =============================================================================
// Shared helper
// =============================================================================

file static class SnapshotTestHelper
{
    /// <summary>
    /// Create a populated SqliteCacheStore in a fresh temp directory.
    /// Returns:
    ///   EffectiveRoot — the path passed to ExportAsync (opts.GetEffectiveCacheRoot(), which
    ///                   includes a "/public" suffix when CacheRootHash is null).
    ///   BaseRoot      — the raw temp dir; add this to _tempDirs for cleanup.
    ///   Store         — the store instance.
    /// </summary>
    public static (string EffectiveRoot, string BaseRoot, SqliteCacheStore Store) CreatePopulatedStore()
    {
        var baseRoot = Path.Combine(Path.GetTempPath(), $"hlx-snap-src-{Guid.NewGuid():N}");
        var opts = new CacheOptions { CacheRoot = baseRoot };
        var effectiveRoot = opts.GetEffectiveCacheRoot(); // e.g. baseRoot/public
        var store = new SqliteCacheStore(opts);
        return (effectiveRoot, baseRoot, store);
    }

    /// <summary>
    /// Seed minimal data into <paramref name="store"/> so that the DB has rows and WAL is active.
    /// </summary>
    public static async Task SeedAsync(SqliteCacheStore store, int metadataRows = 3, int artifactRows = 2)
    {
        for (var i = 0; i < metadataRows; i++)
        {
            var key = $"job:seed{i:D4}:details";
            await store.SetMetadataAsync(key, $"{{\"i\":{i}}}", TimeSpan.FromHours(24));
        }

        for (var i = 0; i < artifactRows; i++)
        {
            var key = $"job:seed{i:D4}:artifact";
            var bytes = new byte[] { 0xDE, 0xAD, (byte)i };
            using var ms = new MemoryStream(bytes);
            await store.SetArtifactAsync(key, ms);
        }
    }

    public static string NewTempDir(string tag = "dst")
        => Path.Combine(Path.GetTempPath(), $"hlx-snap-{tag}-{Guid.NewGuid():N}");
}

// =============================================================================
// SnapshotExporter — export behavior
// =============================================================================

public class SnapshotExporterTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempDir(string tag = "t")
    {
        var d = SnapshotTestHelper.NewTempDir(tag);
        _tempDirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_CreatesDestinationWithDbAndArtifacts()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = TempDir("dest");
        var result = await SnapshotExporter.ExportAsync(root, dest);

        Assert.True(Directory.Exists(dest), "Destination directory should exist");
        Assert.True(File.Exists(Path.Combine(dest, "cache.db")), "cache.db should exist");
        Assert.True(Directory.Exists(Path.Combine(dest, "artifacts")), "artifacts/ should exist");
        Assert.Equal(dest, result.Destination);
        Assert.Equal(2, result.ArtifactCount);
        Assert.True(result.DbSizeBytes > 0, "DB should be non-empty");
    }

    [Fact]
    public async Task Export_SnapshotIsReadableByEvalMode()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        const string key = "job:evaltest:details";
        const string json = "{\"EvalMode\":true}";
        using (store)
        {
            await store.SetMetadataAsync(key, json, TimeSpan.FromHours(1));
        }

        var dest = TempDir("eval");
        await SnapshotExporter.ExportAsync(root, dest);

        // Open the snapshot in eval mode and verify the data is present
        var evalOpts = new CacheOptions { CacheRoot = dest, EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var value = await evalStore.GetMetadataAsync(key);
        Assert.Equal(json, value);
    }

    [Fact]
    public async Task Export_ArtifactReferencesAreRelative_PortableAcrossMachines()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store, metadataRows: 1, artifactRows: 1);
        }

        var dest = TempDir("portable");
        await SnapshotExporter.ExportAsync(root, dest);

        // Read the artifact file_path rows from the snapshot DB — they should be relative
        var dbPath = Path.Combine(dest, "cache.db");
        var connString = $"Data Source={dbPath};Mode=ReadOnly";
        using var conn = new SqliteConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM cache_artifacts;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var filePath = reader.GetString(0);
            Assert.False(Path.IsPathRooted(filePath),
                $"Artifact path should be relative, but was: {filePath}");
        }
    }

    [Fact]
    public async Task Export_EmptyArtifacts_StillCreatesArtifactsDir()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store, metadataRows: 1, artifactRows: 0);
        }

        var dest = TempDir("empty-artifacts");
        var result = await SnapshotExporter.ExportAsync(root, dest);

        Assert.Equal(0, result.ArtifactCount);
        Assert.True(Directory.Exists(Path.Combine(dest, "artifacts")));
    }

    // ── WAL checkpoint ────────────────────────────────────────────────────────

    [Fact]
    public async Task Export_WalCheckpoint_IsExplicitlyReported()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = TempDir("wal");
        var result = await SnapshotExporter.ExportAsync(root, dest);

        // WalPagesTotal/Written are non-negative integers; the checkpoint ran
        Assert.True(result.WalPagesWritten >= 0);
        Assert.True(result.WalPagesTotal >= 0);
    }

    [Fact]
    public void CheckpointWal_ReturnsNonNegativePages()
    {
        // Create a real DB with WAL mode to confirm the pragma executes cleanly
        var root = SnapshotTestHelper.NewTempDir("wal-direct");
        _tempDirs.Add(root);
        var opts = new CacheOptions { CacheRoot = root };
        var effectiveRoot = opts.GetEffectiveCacheRoot();
        using var store = new SqliteCacheStore(opts);
        store.Dispose();

        var dbPath = Path.Combine(effectiveRoot, "cache.db");
        var (busy, total, written) = SnapshotExporter.CheckpointWal(dbPath);

        Assert.True(written >= 0);
        Assert.True(total >= 0);
        _ = busy; // busy may be true or false — no assertion, just must not throw
    }

    [Fact]
    public async Task Export_DestinationDbContainsAllSeedData()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            for (var i = 0; i < 5; i++)
                await store.SetMetadataAsync($"job:data{i}:meta", $"{{\"n\":{i}}}", TimeSpan.FromHours(1));
        }

        var dest = TempDir("data-verify");
        await SnapshotExporter.ExportAsync(root, dest);

        // Count rows in the exported DB
        var dbPath = Path.Combine(dest, "cache.db");
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM cache_metadata;";
        var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        Assert.Equal(5, count);
    }

    // ── Atomic copy — temp dir cleanup ────────────────────────────────────────

    [Fact]
    public async Task Export_OnSuccess_NoTempDirectoryRemains()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = TempDir("atomic");
        var destParent = Path.GetDirectoryName(dest)!;

        await SnapshotExporter.ExportAsync(root, dest);

        // No .tmp.* sibling should remain
        var tempSiblings = Directory.GetDirectories(destParent, $"{Path.GetFileName(dest)}.tmp.*");
        Assert.Empty(tempSiblings);
    }

    // ── Validation / rejection cases ──────────────────────────────────────────

    [Fact]
    public async Task Export_Throws_WhenSourceRootDoesNotExist()
    {
        var dest = TempDir("no-src-dest");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync("/nonexistent/path/hlx-test-cache", dest));
        Assert.Contains("does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_Throws_WhenSourceHasNoCacheDb()
    {
        var root = TempDir("no-db-src");
        Directory.CreateDirectory(root);
        var dest = TempDir("no-db-dest");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(root, dest));
        Assert.Contains("cache.db", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_Throws_WhenDestinationAlreadyExists()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = TempDir("existing-dest");
        Directory.CreateDirectory(dest); // pre-create destination

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(root, dest));
        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_Throws_WhenDestinationParentDoesNotExist()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = Path.Combine(Path.GetTempPath(), $"hlx-snap-nonexistent-{Guid.NewGuid():N}", "snapshot");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(root, dest));
        Assert.Contains("parent directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_Throws_WhenSourceSchemaVersionIsZero()
    {
        // Create an empty (uninitialized) DB — schema version 0
        var root = TempDir("schema0-src");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "cache.db");
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        conn.Close();
        SqliteConnection.ClearAllPools();

        var dest = TempDir("schema0-dest");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(root, dest));
        Assert.Contains("schema version 0", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_Throws_WhenSourceSchemaMismatch()
    {
        var root = TempDir("bad-schema-src");
        Directory.CreateDirectory(root);
        var dbPath = Path.Combine(root, "cache.db");

        // Stamp a bad schema version
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
        SqliteConnection.ClearAllPools();

        var dest = TempDir("bad-schema-dest");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => SnapshotExporter.ExportAsync(root, dest));
        Assert.Contains("99", ex.Message);
    }

    // ── ValidateSourceSchema helper ───────────────────────────────────────────

    [Fact]
    public void ValidateSourceSchema_PassesForValidDb()
    {
        var root = SnapshotTestHelper.NewTempDir("vss-valid");
        _tempDirs.Add(root);
        var opts = new CacheOptions { CacheRoot = root };
        var effectiveRoot = opts.GetEffectiveCacheRoot();
        using var store = new SqliteCacheStore(opts);
        store.Dispose();

        var dbPath = Path.Combine(effectiveRoot, "cache.db");
        // Should not throw
        SnapshotExporter.ValidateSourceSchema(dbPath);
    }

    [Fact]
    public void ValidateSourceSchema_Throws_ForMissingTable()
    {
        var root = SnapshotTestHelper.NewTempDir("vss-missing-table");
        _tempDirs.Add(root);
        var dbPath = Path.Combine(root, "cache.db");
        Directory.CreateDirectory(root);

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA user_version={SnapshotExporter.SchemaVersion};";
            cmd.ExecuteNonQuery();
            // Intentionally omit creating any tables
        }
        conn.Close();
        SqliteConnection.ClearAllPools();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SnapshotExporter.ValidateSourceSchema(dbPath));
        Assert.Contains("cache_metadata", ex.Message);
    }
}

// =============================================================================
// SnapshotValidator — validation behavior
// =============================================================================

public class SnapshotValidatorTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempDir(string tag = "t")
    {
        var d = SnapshotTestHelper.NewTempDir($"val-{tag}");
        _tempDirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Valid snapshots ───────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_ValidSnapshot_ReturnsIsValid()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store);
        }

        var dest = TempDir("ok");
        await SnapshotExporter.ExportAsync(root, dest);

        var result = await SnapshotValidator.ValidateAsync(dest);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.True(result.MetadataEntries > 0);
        Assert.True(result.ArtifactEntries > 0);
        Assert.Equal(0, result.MissingArtifactFiles);
    }

    [Fact]
    public async Task Validate_EmptyCache_ReturnsIsValid()
    {
        // A cache with no data is valid — schema is initialized but tables are empty
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        store.Dispose();

        var dest = TempDir("empty");
        await SnapshotExporter.ExportAsync(root, dest);

        var result = await SnapshotValidator.ValidateAsync(dest);
        Assert.True(result.IsValid);
        Assert.Equal(0, result.MetadataEntries);
        Assert.Equal(0, result.ArtifactEntries);
    }

    [Fact]
    public async Task Validate_ReportsMetadataAndArtifactCounts()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store, metadataRows: 4, artifactRows: 3);
        }

        var dest = TempDir("counts");
        await SnapshotExporter.ExportAsync(root, dest);

        var result = await SnapshotValidator.ValidateAsync(dest);

        Assert.True(result.IsValid);
        Assert.Equal(4, result.MetadataEntries);
        Assert.Equal(3, result.ArtifactEntries);
    }

    // ── Layout errors ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_MissingDirectory_ReturnsInvalid()
    {
        var result = await SnapshotValidator.ValidateAsync("/nonexistent/path/hlx-snap-test");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_MissingCacheDb_ReturnsInvalid()
    {
        var snap = TempDir("no-db");
        Directory.CreateDirectory(snap);

        var result = await SnapshotValidator.ValidateAsync(snap);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cache.db", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_MissingArtifactsDir_ReturnsWarningNotError()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        store.Dispose();

        var dest = TempDir("no-artdir");
        await SnapshotExporter.ExportAsync(root, dest);

        // Remove the artifacts/ directory from the snapshot
        var artDir = Path.Combine(dest, "artifacts");
        if (Directory.Exists(artDir)) Directory.Delete(artDir, recursive: true);

        var result = await SnapshotValidator.ValidateAsync(dest);

        // With no artifact rows in the DB, missing artifacts/ is only a warning
        Assert.True(result.IsValid, "No artifact rows → missing artifacts/ should be a warning only");
        Assert.Contains(result.Warnings, w => w.Contains("artifacts", StringComparison.OrdinalIgnoreCase));
    }

    // ── Schema errors ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Validate_WrongSchemaVersion_ReturnsInvalid()
    {
        var snap = TempDir("bad-version");
        Directory.CreateDirectory(snap);
        var dbPath = Path.Combine(snap, "cache.db");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version=42;";
            cmd.ExecuteNonQuery();
        }
        conn.Close();
        SqliteConnection.ClearAllPools();

        var result = await SnapshotValidator.ValidateAsync(snap);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validate_MissingTable_ReturnsInvalid()
    {
        var snap = TempDir("missing-table");
        Directory.CreateDirectory(snap);
        Directory.CreateDirectory(Path.Combine(snap, "artifacts"));
        var dbPath = Path.Combine(snap, "cache.db");

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            // Stamp correct version but only create two of the three tables
            cmd.CommandText = "PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                CREATE TABLE cache_metadata (cache_key TEXT PRIMARY KEY, json_value TEXT NOT NULL,
                    created_at TEXT NOT NULL, expires_at TEXT NOT NULL, job_id TEXT NOT NULL);
                CREATE TABLE cache_artifacts (cache_key TEXT PRIMARY KEY, file_path TEXT NOT NULL,
                    file_size INTEGER NOT NULL, created_at TEXT NOT NULL, last_accessed TEXT NOT NULL, job_id TEXT NOT NULL);
                """;
            cmd.ExecuteNonQuery();
            // Intentionally omit cache_job_state
        }
        conn.Close();
        SqliteConnection.ClearAllPools();

        var result = await SnapshotValidator.ValidateAsync(snap);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cache_job_state", StringComparison.OrdinalIgnoreCase));
    }

    // ── Broken artifact references ────────────────────────────────────────────

    [Fact]
    public async Task Validate_MissingArtifactFile_ReturnsInvalid()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store, metadataRows: 1, artifactRows: 1);
        }

        var dest = TempDir("del-artifact");
        await SnapshotExporter.ExportAsync(root, dest);

        // Delete one artifact file from the snapshot
        var snapArtifacts = Path.Combine(dest, "artifacts");
        var firstArtifact = Directory.GetFiles(snapArtifacts, "*", SearchOption.AllDirectories).First();
        File.Delete(firstArtifact);

        var result = await SnapshotValidator.ValidateAsync(dest);

        Assert.False(result.IsValid);
        Assert.True(result.MissingArtifactFiles > 0);
        Assert.Contains(result.Errors, e => e.Contains("Missing artifact file", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Validate_AllArtifactFilesPresent_NoBrokenRefs()
    {
        var (root, baseRoot, store) = SnapshotTestHelper.CreatePopulatedStore();
        _tempDirs.Add(baseRoot);
        using (store)
        {
            await SnapshotTestHelper.SeedAsync(store, metadataRows: 2, artifactRows: 5);
        }

        var dest = TempDir("full-refs");
        await SnapshotExporter.ExportAsync(root, dest);

        var result = await SnapshotValidator.ValidateAsync(dest);

        Assert.True(result.IsValid);
        Assert.Equal(0, result.MissingArtifactFiles);
    }
}
