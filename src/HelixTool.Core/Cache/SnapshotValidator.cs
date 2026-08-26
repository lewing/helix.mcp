using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.Cache;

/// <summary>
/// Validates a snapshot directory for use with <c>HLX_EVAL_SNAPSHOT</c>.
/// <para>
/// Checks performed (in order):
/// <list type="number">
///   <item>Directory existence.</item>
///   <item>Presence of <c>cache.db</c>.</item>
///   <item>Presence of <c>artifacts/</c> directory (warning only — snapshots may have no artifacts).</item>
///   <item>Database schema version matches the expected value.</item>
///   <item>All expected tables (<c>cache_metadata</c>, <c>cache_artifacts</c>, <c>cache_job_state</c>) exist.</item>
///   <item>Every artifact row in <c>cache_artifacts</c> has a corresponding file on disk.</item>
/// </list>
/// </para>
/// </summary>
public static class SnapshotValidator
{
    private const int ExpectedSchemaVersion = 1;
    // Limit per-file error reporting to avoid flooding output for badly broken snapshots
    private const int MaxMissingFileErrors = 10;

    /// <summary>
    /// Validate the snapshot at <paramref name="snapshotPath"/>.
    /// </summary>
    /// <param name="snapshotPath">Path to the snapshot directory to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SnapshotValidationResult"/> describing all errors and warnings found.</returns>
    public static Task<SnapshotValidationResult> ValidateAsync(
        string snapshotPath,
        CancellationToken ct = default)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        var errors = new List<string>();
        var warnings = new List<string>();

        // ── Layout checks ─────────────────────────────────────────────────────
        if (!Directory.Exists(snapshotPath))
        {
            errors.Add($"Snapshot directory does not exist: {snapshotPath}");
            return Task.FromResult(Fail(errors, warnings));
        }

        var dbPath = Path.Combine(snapshotPath, "cache.db");
        if (!File.Exists(dbPath))
        {
            errors.Add($"Missing required database file: {dbPath}");
            return Task.FromResult(Fail(errors, warnings));
        }

        var artifactsDir = Path.Combine(snapshotPath, "artifacts");
        if (!Directory.Exists(artifactsDir))
            warnings.Add(
                $"The artifacts/ directory is absent ({artifactsDir}). " +
                "The snapshot may have no cached artifact files, which is valid but unusual.");

        // ── Schema checks ─────────────────────────────────────────────────────
        int metadataEntries = 0, artifactEntries = 0, missingFiles = 0;
        try
        {
            var connString = $"Data Source={dbPath};Mode=ReadOnly";
            using var conn = new SqliteConnection(connString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            if (version != ExpectedSchemaVersion)
                errors.Add(
                    $"Schema version mismatch: expected {ExpectedSchemaVersion}, found {version}. " +
                    "The snapshot was created with a different version of hlx and cannot be used.");

            foreach (var table in new[] { "cache_metadata", "cache_artifacts", "cache_job_state" })
            {
                cmd.Parameters.Clear();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@t;";
                cmd.Parameters.AddWithValue("@t", table);
                var count = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (count == 0)
                    errors.Add(
                        $"Missing required table '{table}'. " +
                        "The snapshot database may be incomplete or corrupt.");
            }

            // Stop early if structural errors exist — artifact checks need valid tables
            if (errors.Count > 0)
            {
                conn.Close();
                SqliteConnection.ClearAllPools();
                return Task.FromResult(Fail(errors, warnings));
            }

            // ── Row counts ────────────────────────────────────────────────────
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT COUNT(*) FROM cache_metadata;";
            metadataEntries = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

            // ── Artifact file reference validation ────────────────────────────
            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT file_path FROM cache_artifacts;";
            using var reader = cmd.ExecuteReader();

            var relPaths = new List<string>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                relPaths.Add(reader.GetString(0));
            }
            reader.Close();

            artifactEntries = relPaths.Count;
            foreach (var relPath in relPaths)
            {
                ct.ThrowIfCancellationRequested();
                // Artifact files must be inside the artifacts/ directory (same security contract as
                // CacheSecurity.ValidatePathWithinRoot in SqliteCacheStore).
                var fullPath = Path.GetFullPath(Path.Combine(artifactsDir, relPath));
                if (!fullPath.StartsWith(artifactsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    !string.Equals(fullPath, artifactsDir, StringComparison.Ordinal))
                {
                    errors.Add($"Artifact path escapes the artifacts/ directory: {relPath}");
                    missingFiles++;
                    continue;
                }

                if (!File.Exists(fullPath))
                {
                    missingFiles++;
                    if (missingFiles <= MaxMissingFileErrors)
                        errors.Add($"Missing artifact file referenced in database: {relPath}");
                }
            }

            if (missingFiles > MaxMissingFileErrors)
                errors.Add(
                    $"...and {missingFiles - MaxMissingFileErrors} more missing artifact file(s). " +
                    $"Total missing: {missingFiles}.");

            conn.Close();
            SqliteConnection.ClearAllPools();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to open or read snapshot database: {ex.Message}");
        }

        var isValid = errors.Count == 0;
        return Task.FromResult(new SnapshotValidationResult(
            isValid, errors, warnings, metadataEntries, artifactEntries, missingFiles));
    }

    private static SnapshotValidationResult Fail(List<string> errors, List<string> warnings)
        => new(false, errors, warnings, 0, 0, 0);
}
