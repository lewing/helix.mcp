using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.Cache;

/// <summary>
/// Exports a portable eval snapshot from the current cache root.
/// <para>
/// The export sequence is:
/// 1. Validate the source schema (schema version and expected tables).
/// 2. Checkpoint the WAL into the main database file (<c>PRAGMA wal_checkpoint(TRUNCATE)</c>).
/// 3. Copy <c>cache.db</c>, any WAL/SHM side-files, and the <c>artifacts/</c> tree to a
///    temporary directory adjacent to the destination.
/// 4. Atomically rename the temp directory to the final destination.
/// </para>
/// <para>
/// Auth-scoped key limitation: Cache entries written under an AzDO auth context are stored
/// with an auth-hash prefix in the cache key. When the snapshot is used in eval mode
/// (<c>HLX_EVAL_SNAPSHOT</c>), lookups will only match those entries if the eval-mode client
/// uses the same auth context hash. Public (unauthenticated) Helix cache entries are always
/// accessible regardless of auth context. This limitation is by design and is not corrected
/// by key normalization.
/// </para>
/// </summary>
public static class SnapshotExporter
{
    internal const int SchemaVersion = 1;

    /// <summary>
    /// Export a snapshot of <paramref name="sourceRoot"/> to <paramref name="destination"/>.
    /// </summary>
    /// <param name="sourceRoot">
    /// Effective cache root to export (from <see cref="CacheOptions.GetEffectiveCacheRoot"/>).
    /// Must contain a valid <c>cache.db</c>.
    /// </param>
    /// <param name="destination">
    /// Destination directory path. Must not already exist — overwrite is refused to prevent
    /// partial-success states. Delete the destination first if you need to re-export.
    /// </param>
    /// <param name="progress">Optional progress reporter; receives human-readable status strings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="ExportResult"/> describing the completed export.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the source is invalid, the destination already exists, or the schema is incompatible.
    /// </exception>
    public static async Task<ExportResult> ExportAsync(
        string sourceRoot,
        string destination,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        destination = Path.GetFullPath(destination);

        // ── Validate source ───────────────────────────────────────────────────
        if (!Directory.Exists(sourceRoot))
            throw new InvalidOperationException(
                $"Source cache root does not exist: {sourceRoot}");

        var dbPath = Path.Combine(sourceRoot, "cache.db");
        if (!File.Exists(dbPath))
            throw new InvalidOperationException(
                $"Source cache database not found: {dbPath}. " +
                "Run hlx (without HLX_EVAL_SNAPSHOT) to populate the cache first.");

        // ── Validate destination ──────────────────────────────────────────────
        if (Directory.Exists(destination))
            throw new InvalidOperationException(
                $"Destination already exists: {destination}. " +
                "Remove it first or choose a different path to avoid overwriting a snapshot.");
        if (File.Exists(destination))
            throw new InvalidOperationException(
                $"Destination path exists as a file: {destination}.");

        var destParent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destParent) && !Directory.Exists(destParent))
            throw new InvalidOperationException(
                $"Destination parent directory does not exist: {destParent}");

        // ── Schema validation (read-only, before touching anything) ───────────
        progress?.Report("Validating source cache schema...");
        ValidateSourceSchema(dbPath);

        // ── WAL checkpoint ────────────────────────────────────────────────────
        progress?.Report("Checkpointing WAL (PRAGMA wal_checkpoint(TRUNCATE))...");
        var (busy, walPagesTotal, walPagesWritten) = CheckpointWal(dbPath);
        if (busy)
        {
            progress?.Report(
                $"WAL checkpoint partially completed ({walPagesWritten}/{walPagesTotal} pages written). " +
                "A reader was active — WAL side-files will be included in the snapshot.");
        }
        else
        {
            progress?.Report(
                $"WAL checkpoint complete: {walPagesWritten}/{walPagesTotal} pages written.");
        }

        // ── Copy to temp, then atomic rename ──────────────────────────────────
        var tempDest = destination + $".tmp.{Guid.NewGuid():N}";
        string? tempDestToClean = tempDest;
        try
        {
            Directory.CreateDirectory(tempDest);

            // Copy cache.db
            progress?.Report("Copying cache.db...");
            var tempDbPath = Path.Combine(tempDest, "cache.db");
            await CopyFileAsync(dbPath, tempDbPath, ct);

            // Copy WAL and SHM side-files if present.
            // After a TRUNCATE checkpoint these are empty or near-empty, but we copy them
            // rather than deleting them so the destination is a self-consistent SQLite file set.
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                ct.ThrowIfCancellationRequested();
                var sidePath = dbPath + suffix;
                if (File.Exists(sidePath))
                {
                    progress?.Report($"Copying cache.db{suffix}...");
                    await CopyFileAsync(sidePath, tempDbPath + suffix, ct);
                }
            }

            // Copy artifacts/ tree (relative paths — makes the snapshot portable)
            var sourceArtifacts = Path.Combine(sourceRoot, "artifacts");
            int artifactCount = 0;
            if (Directory.Exists(sourceArtifacts))
            {
                progress?.Report("Copying artifacts/...");
                var tempArtifacts = Path.Combine(tempDest, "artifacts");
                artifactCount = await CopyDirectoryAsync(sourceArtifacts, tempArtifacts, ct);
                progress?.Report($"Copied {artifactCount} artifact file(s).");
            }
            else
            {
                // Ensure artifacts/ dir always exists so snapshot layout is consistent
                Directory.CreateDirectory(Path.Combine(tempDest, "artifacts"));
            }

            // Atomic rename — either succeeds fully or the destination is unchanged
            progress?.Report("Finalizing snapshot (atomic rename)...");
            Directory.Move(tempDest, destination);
            tempDestToClean = null; // ownership transferred — do not clean up

            var dbSize = new FileInfo(Path.Combine(destination, "cache.db")).Length;
            return new ExportResult(
                Destination: destination,
                ArtifactCount: artifactCount,
                DbSizeBytes: dbSize,
                WalBusy: busy,
                WalPagesTotal: walPagesTotal,
                WalPagesWritten: walPagesWritten);
        }
        catch
        {
            if (tempDestToClean != null)
            {
                try { Directory.Delete(tempDestToClean, recursive: true); } catch { /* best effort */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Validate the source database schema.
    /// Opens the DB read-only and checks schema version and expected table presence.
    /// Does not perform any DDL or write operations.
    /// </summary>
    internal static void ValidateSourceSchema(string dbPath)
    {
        var connString = $"Data Source={dbPath};Mode=ReadOnly";
        using var conn = new SqliteConnection(connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version == 0)
            throw new InvalidOperationException(
                $"Source cache database has schema version 0. " +
                "The cache may be empty or was never fully initialized. " +
                "Run hlx in normal mode to populate the cache before exporting.");

        if (version != SchemaVersion)
            throw new InvalidOperationException(
                $"Source cache schema version {version} is not supported (expected {SchemaVersion}). " +
                "Ensure hlx is up-to-date or use the version of hlx that populated this cache.");

        foreach (var table in new[] { "cache_metadata", "cache_artifacts", "cache_job_state" })
        {
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t;";
            cmd.Parameters.AddWithValue("@t", table);
            var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count == 0)
                throw new InvalidOperationException(
                    $"Source cache database is missing expected table '{table}'. " +
                    "The database may be corrupt or from an unsupported version.");
        }

        conn.Close();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// Run <c>PRAGMA wal_checkpoint(TRUNCATE)</c> on the database at <paramref name="dbPath"/>.
    /// TRUNCATE mode writes all WAL frames to the main database file and, if no reader holds
    /// a read lock, truncates the WAL file to 0 bytes.
    /// </summary>
    /// <returns>
    /// A tuple of (<c>busy</c>, <c>pagesInWal</c>, <c>pagesWritten</c>) matching the three
    /// columns returned by SQLite's wal_checkpoint pragma:
    /// <list type="bullet">
    ///   <item><c>busy</c> — true if the checkpoint could not complete because a reader was active.</item>
    ///   <item><c>pagesInWal</c> — number of modified pages in the WAL at the start of the checkpoint.</item>
    ///   <item><c>pagesWritten</c> — number of pages actually written back to the main database file.</item>
    /// </list>
    /// </returns>
    internal static (bool Busy, int PagesInWal, int PagesWritten) CheckpointWal(string dbPath)
    {
        // Open with the same Cache=Shared mode used by SqliteCacheStore in normal (non-eval) mode
        // so that the checkpoint operates within the same connection pool and sees any
        // in-memory cached pages already held open by the running store.
        var connString = $"Data Source={dbPath};Cache=Shared";
        using var conn = new SqliteConnection(connString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        // PRAGMA wal_checkpoint(TRUNCATE) returns three columns: busy, log, checkpointed
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        using var reader = cmd.ExecuteReader();

        if (!reader.Read())
        {
            conn.Close();
            SqliteConnection.ClearAllPools();
            return (false, 0, 0);
        }

        var busy = reader.GetInt32(0) != 0;
        var pagesInWal = reader.GetInt32(1);
        var pagesWritten = reader.GetInt32(2);

        conn.Close();
        // Release any shared-cache pool slots so subsequent read-only opens don't see stale state.
        SqliteConnection.ClearAllPools();

        return (busy, pagesInWal, pagesWritten);
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        // Open source with shared read+delete access so we can copy a file that another
        // process might have open (e.g. the running SqliteCacheStore).
        await using var src = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        await using var dst = new FileStream(
            dest, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static async Task<int> CopyDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(dest, relative);
            var destDir = Path.GetDirectoryName(destFile)!;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            await CopyFileAsync(file, destFile, ct);
            count++;
        }
        return count;
    }
}

/// <summary>Result of a successful snapshot export operation.</summary>
/// <param name="Destination">Absolute path to the created snapshot directory.</param>
/// <param name="ArtifactCount">Number of artifact files copied.</param>
/// <param name="DbSizeBytes">Size of the exported <c>cache.db</c> in bytes.</param>
/// <param name="WalBusy">True if the WAL checkpoint was blocked by an active reader.</param>
/// <param name="WalPagesTotal">Total modified pages in the WAL before the checkpoint.</param>
/// <param name="WalPagesWritten">Pages written to the main database file during the checkpoint.</param>
public sealed record ExportResult(
    string Destination,
    int ArtifactCount,
    long DbSizeBytes,
    bool WalBusy,
    int WalPagesTotal,
    int WalPagesWritten);
