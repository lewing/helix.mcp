using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core;

/// <summary>
/// SQLite-backed implementation of <see cref="ICacheStore"/>.
/// Uses WAL mode for concurrent read access across processes.
/// Artifact files are stored on disk and tracked in SQLite.
/// </summary>
public sealed class SqliteCacheStore : ICacheStore
{
    private const int SchemaVersion = 1;
    private const string Iso8601Format = "O";

    private readonly CacheOptions _options;
    private readonly string _dbPath;
    private readonly string _artifactsDir;
    private readonly SqliteConnection _connection;

    public SqliteCacheStore(CacheOptions options)
    {
        _options = options;
        var root = options.GetEffectiveCacheRoot();
        Directory.CreateDirectory(root);

        _dbPath = Path.Combine(root, "cache.db");
        _artifactsDir = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(_artifactsDir);

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        InitializeSchema();
        // Fire-and-forget eviction on startup
        _ = Task.Run(() => EvictExpiredAsync());
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();

        // Check schema version
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version != SchemaVersion)
        {
            // Destructive migration — cache is regenerable data
            if (version > 0)
            {
                cmd.CommandText = """
                    DROP TABLE IF EXISTS cache_metadata;
                    DROP TABLE IF EXISTS cache_artifacts;
                    DROP TABLE IF EXISTS cache_job_state;
                    """;
                cmd.ExecuteNonQuery();
            }
        }

        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS cache_metadata (
                cache_key   TEXT PRIMARY KEY,
                json_value  TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                job_id      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_metadata_expires ON cache_metadata(expires_at);
            CREATE INDEX IF NOT EXISTS idx_metadata_job ON cache_metadata(job_id);

            CREATE TABLE IF NOT EXISTS cache_artifacts (
                cache_key       TEXT PRIMARY KEY,
                file_path       TEXT NOT NULL,
                file_size       INTEGER NOT NULL,
                created_at      TEXT NOT NULL,
                last_accessed   TEXT NOT NULL,
                job_id          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_artifacts_accessed ON cache_artifacts(last_accessed);
            CREATE INDEX IF NOT EXISTS idx_artifacts_job ON cache_artifacts(job_id);

            CREATE TABLE IF NOT EXISTS cache_job_state (
                job_id       TEXT PRIMARY KEY,
                is_completed INTEGER NOT NULL,
                finished_at  TEXT,
                cached_at    TEXT NOT NULL,
                expires_at   TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
        cmd.ExecuteNonQuery();
    }

    public Task<string?> GetMetadataAsync(string cacheKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT json_value FROM cache_metadata WHERE cache_key = @key AND expires_at > @now;";
        cmd.Parameters.AddWithValue("@key", cacheKey);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString(Iso8601Format, CultureInfo.InvariantCulture));

        var result = cmd.ExecuteScalar();
        return Task.FromResult(result as string);
    }

    public Task SetMetadataAsync(string cacheKey, string jsonValue, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        // Extract jobId from cache key (format: "job:{jobId}:...")
        var jobId = ExtractJobId(cacheKey);

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO cache_metadata (cache_key, json_value, created_at, expires_at, job_id)
            VALUES (@key, @value, @created, @expires, @jobId);
            """;
        cmd.Parameters.AddWithValue("@key", cacheKey);
        cmd.Parameters.AddWithValue("@value", jsonValue);
        cmd.Parameters.AddWithValue("@created", now.ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@expires", (now + ttl).ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<Stream?> GetArtifactAsync(string cacheKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT file_path FROM cache_artifacts WHERE cache_key = @key;";
        cmd.Parameters.AddWithValue("@key", cacheKey);

        var relPath = cmd.ExecuteScalar() as string;
        if (relPath == null) return Task.FromResult<Stream?>(null);

        var fullPath = Path.Combine(_artifactsDir, relPath);
        CacheSecurity.ValidatePathWithinRoot(fullPath, _artifactsDir);
        if (!File.Exists(fullPath))
        {
            // Stale row — remove it
            using var del = _connection.CreateCommand();
            del.CommandText = "DELETE FROM cache_artifacts WHERE cache_key = @key;";
            del.Parameters.AddWithValue("@key", cacheKey);
            del.ExecuteNonQuery();
            return Task.FromResult<Stream?>(null);
        }

        // Update last_accessed
        using var upd = _connection.CreateCommand();
        upd.CommandText = "UPDATE cache_artifacts SET last_accessed = @now WHERE cache_key = @key;";
        upd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString(Iso8601Format, CultureInfo.InvariantCulture));
        upd.Parameters.AddWithValue("@key", cacheKey);
        upd.ExecuteNonQuery();

        return Task.FromResult<Stream?>(File.OpenRead(fullPath));
    }

    public async Task SetArtifactAsync(string cacheKey, Stream content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var jobId = ExtractJobId(cacheKey);

        // Build relative path: {jobId[0:8]}/{rest-of-key-sanitized}
        var prefix = CacheSecurity.SanitizePathSegment(jobId.Length >= 8 ? jobId[..8] : jobId);
        var safeName = CacheSecurity.SanitizePathSegment(cacheKey.Replace(':', '_'));
        var relPath = Path.Combine(prefix, safeName);
        var fullPath = Path.Combine(_artifactsDir, relPath);
        CacheSecurity.ValidatePathWithinRoot(fullPath, _artifactsDir);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        // Write-then-rename pattern
        var tempPath = fullPath + ".tmp";
        await using (var fs = File.Create(tempPath))
            await content.CopyToAsync(fs, ct);
        File.Move(tempPath, fullPath, overwrite: true);

        var fileSize = new FileInfo(fullPath).Length;
        var now = DateTimeOffset.UtcNow;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO cache_artifacts (cache_key, file_path, file_size, created_at, last_accessed, job_id)
            VALUES (@key, @path, @size, @created, @accessed, @jobId);
            """;
        cmd.Parameters.AddWithValue("@key", cacheKey);
        cmd.Parameters.AddWithValue("@path", relPath);
        cmd.Parameters.AddWithValue("@size", fileSize);
        cmd.Parameters.AddWithValue("@created", now.ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@accessed", now.ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.ExecuteNonQuery();

        // Evict if over cap
        await EvictLruIfOverCapAsync(ct);
    }

    public Task<bool?> IsJobCompletedAsync(string jobId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT is_completed FROM cache_job_state WHERE job_id = @jobId AND expires_at > @now;";
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString(Iso8601Format, CultureInfo.InvariantCulture));

        var result = cmd.ExecuteScalar();
        if (result == null) return Task.FromResult<bool?>(null);
        return Task.FromResult<bool?>(Convert.ToInt32(result, CultureInfo.InvariantCulture) != 0);
    }

    public Task SetJobCompletedAsync(string jobId, bool completed, TimeSpan ttl, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO cache_job_state (job_id, is_completed, finished_at, cached_at, expires_at)
            VALUES (@jobId, @completed, @finished, @cached, @expires);
            """;
        cmd.Parameters.AddWithValue("@jobId", jobId);
        cmd.Parameters.AddWithValue("@completed", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@finished", completed ? now.ToString(Iso8601Format, CultureInfo.InvariantCulture) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@cached", now.ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@expires", (now + ttl).ToString(Iso8601Format, CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Delete artifact files
        try
        {
            if (Directory.Exists(_artifactsDir))
                Directory.Delete(_artifactsDir, recursive: true);
            Directory.CreateDirectory(_artifactsDir);
        }
        catch (IOException) { /* Best effort */ }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM cache_metadata;
            DELETE FROM cache_artifacts;
            DELETE FROM cache_job_state;
            """;
        cmd.ExecuteNonQuery();

        return Task.CompletedTask;
    }

    public Task<CacheStatus> GetStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int metadataCount;
        int artifactCount;
        long totalSize;
        string? oldest = null;
        string? newest = null;

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM cache_metadata;";
            metadataCount = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(file_size), 0) FROM cache_artifacts;";
            using var reader = cmd.ExecuteReader();
            reader.Read();
            artifactCount = reader.GetInt32(0);
            totalSize = reader.GetInt64(1);
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT MIN(ts), MAX(ts) FROM (
                    SELECT MIN(created_at) AS ts FROM cache_metadata
                    UNION ALL
                    SELECT MAX(created_at) AS ts FROM cache_metadata
                    UNION ALL
                    SELECT MIN(created_at) AS ts FROM cache_artifacts
                    UNION ALL
                    SELECT MAX(created_at) AS ts FROM cache_artifacts
                );
                """;
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                oldest = reader.IsDBNull(0) ? null : reader.GetString(0);
                newest = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }

        var oldestOffset = oldest != null ? DateTimeOffset.Parse(oldest, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
        var newestOffset = newest != null ? DateTimeOffset.Parse(newest, CultureInfo.InvariantCulture) : (DateTimeOffset?)null;

        return Task.FromResult(new CacheStatus(
            totalSize,
            metadataCount,
            artifactCount,
            oldestOffset,
            newestOffset,
            _options.MaxSizeBytes));
    }

    public Task EvictExpiredAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow.ToString(Iso8601Format, CultureInfo.InvariantCulture);
        var cutoff = (DateTimeOffset.UtcNow - _options.ArtifactMaxAge).ToString(Iso8601Format, CultureInfo.InvariantCulture);

        // Remove expired metadata
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM cache_metadata WHERE expires_at < @now;";
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }

        // Remove expired job state
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM cache_job_state WHERE expires_at < @now;";
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }

        // Remove old artifacts (by last access)
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT cache_key, file_path FROM cache_artifacts WHERE last_accessed < @cutoff;";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            using var reader = cmd.ExecuteReader();

            var toDelete = new List<(string Key, string Path)>();
            while (reader.Read())
                toDelete.Add((reader.GetString(0), reader.GetString(1)));

            reader.Close();
            DeleteArtifactRows(toDelete);
        }

        return Task.CompletedTask;
    }

    private Task EvictLruIfOverCapAsync(CancellationToken ct)
    {
        if (_options.MaxSizeBytes <= 0) return Task.CompletedTask;

        // Get total artifact size
        long totalSize;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(SUM(file_size), 0) FROM cache_artifacts;";
            totalSize = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (totalSize <= _options.MaxSizeBytes) return Task.CompletedTask;

        // LRU eviction — delete oldest-accessed until under cap
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT cache_key, file_path, file_size FROM cache_artifacts ORDER BY last_accessed ASC;";
            using var reader = cmd.ExecuteReader();

            var toDelete = new List<(string Key, string Path)>();
            while (reader.Read() && totalSize > _options.MaxSizeBytes)
            {
                var key = reader.GetString(0);
                var path = reader.GetString(1);
                var size = reader.GetInt64(2);
                toDelete.Add((key, path));
                totalSize -= size;
            }

            reader.Close();
            DeleteArtifactRows(toDelete);
        }

        return Task.CompletedTask;
    }

    private void DeleteArtifactRows(List<(string Key, string Path)> items)
    {
        foreach (var (key, relPath) in items)
        {
            // Delete file
            try
            {
                var fullPath = Path.Combine(_artifactsDir, relPath);
                CacheSecurity.ValidatePathWithinRoot(fullPath, _artifactsDir);
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch (IOException) { /* file may have been deleted externally */ }

            // Delete row
            using var del = _connection.CreateCommand();
            del.CommandText = "DELETE FROM cache_artifacts WHERE cache_key = @key;";
            del.Parameters.AddWithValue("@key", key);
            del.ExecuteNonQuery();
        }
    }

    private static string ExtractJobId(string cacheKey)
    {
        // Cache keys are "job:{jobId}:..." — extract the jobId segment
        if (cacheKey.StartsWith("job:", StringComparison.Ordinal))
        {
            var end = cacheKey.IndexOf(':', 4);
            if (end > 4) return cacheKey[4..end];
        }
        return cacheKey;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
