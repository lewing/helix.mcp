using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.Cache;

/// <summary>
/// Validates a snapshot directory for use with <c>HLX_EVAL_SNAPSHOT</c>, including its SQLite
/// integrity, schema, sidecar-free single-link layout, and artifact references and sizes.
/// </summary>
public static class SnapshotValidator
{
    private const int ExpectedSchemaVersion = 1;
    private const int BusyTimeoutSeconds = 5;
    private const int BusyTimeoutMilliseconds = BusyTimeoutSeconds * 1000;
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

        if (!Directory.Exists(snapshotPath))
        {
            errors.Add($"Snapshot directory does not exist: {snapshotPath}");
            return Task.FromResult(Fail(errors, warnings));
        }

        try
        {
            snapshotPath =
                SnapshotExporter.CanonicalizeExistingPath(snapshotPath, requireDirectory: true);
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Snapshot directory cannot be resolved safely: {ex.Message}");
            return Task.FromResult(Fail(errors, warnings));
        }

        var lexicalDbPath = Path.Combine(snapshotPath, "cache.db");
        if (!File.Exists(lexicalDbPath))
        {
            errors.Add($"Missing required database file: {lexicalDbPath}");
            return Task.FromResult(Fail(errors, warnings));
        }

        string dbPath;
        try
        {
            dbPath =
                SnapshotExporter.CanonicalizeExistingPath(lexicalDbPath, requireDirectory: false);
            if (!IsStrictDescendant(dbPath, snapshotPath))
            {
                errors.Add(
                    $"Snapshot database must resolve beneath the physical snapshot directory: {dbPath}");
                return Task.FromResult(Fail(errors, warnings));
            }
        }
        catch (InvalidOperationException ex)
        {
            errors.Add($"Snapshot database cannot be resolved safely: {ex.Message}");
            return Task.FromResult(Fail(errors, warnings));
        }

        if (HasSidecar(lexicalDbPath, errors) ||
            (!string.Equals(dbPath, lexicalDbPath, PathComparison) && HasSidecar(dbPath, errors)))
        {
            return Task.FromResult(Fail(errors, warnings));
        }

        try
        {
            _ = SnapshotExporter.HashFileRequiringExactlyOneLink(dbPath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Snapshot database must be a readable regular file with exactly one hard " +
                $"link: {ex.Message}");
            return Task.FromResult(Fail(errors, warnings));
        }

        var artifactsPath = Path.Combine(snapshotPath, "artifacts");
        string? physicalArtifactsPath = null;
        if (Directory.Exists(artifactsPath))
        {
            try
            {
                physicalArtifactsPath = SnapshotExporter.CanonicalizeExistingPath(
                    artifactsPath,
                    requireDirectory: true);
                if (!IsStrictDescendant(physicalArtifactsPath, snapshotPath))
                {
                    errors.Add(
                        "The artifacts/ directory must resolve beneath the physical snapshot " +
                        $"directory: {physicalArtifactsPath}");
                    return Task.FromResult(Fail(errors, warnings));
                }
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"The artifacts/ directory cannot be resolved safely: {ex.Message}");
                return Task.FromResult(Fail(errors, warnings));
            }
        }
        else
        {
            try
            {
                if (SnapshotExporter.PathEntryExists(artifactsPath))
                {
                    errors.Add(
                        $"The artifacts/ path is not a safely resolvable directory: {artifactsPath}");
                    return Task.FromResult(Fail(errors, warnings));
                }
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"The artifacts/ path cannot be inspected safely: {ex.Message}");
                return Task.FromResult(Fail(errors, warnings));
            }

            warnings.Add(
                $"The artifacts/ directory is absent ({artifactsPath}). " +
                "The snapshot may have no cached artifact files, which is valid but unusual.");
        }

        ct.ThrowIfCancellationRequested();

        var metadataEntries = 0;
        var artifactEntries = 0;
        var missingFiles = 0;
        var artifactRows = new List<ArtifactRow>();

        try
        {
            using (var connection = OpenReadOnlyConnection(dbPath))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                using (var reader = command.ExecuteReader())
                {
                    var integrityRows = new List<string>();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        integrityRows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
                    }

                    if (integrityRows.Count != 1 ||
                        !string.Equals(integrityRows[0], "ok", StringComparison.Ordinal))
                    {
                        var detail = integrityRows.Count == 0
                            ? "no result"
                            : string.Join("; ", integrityRows);
                        errors.Add($"Database integrity check failed: {detail}");
                    }
                }

                command.Parameters.Clear();
                command.CommandText = "PRAGMA user_version;";
                var version =
                    Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (version != ExpectedSchemaVersion)
                {
                    errors.Add(
                        $"Schema version mismatch: expected {ExpectedSchemaVersion}, found {version}. " +
                        "The snapshot was created with a different version of hlx and cannot be used.");
                }

                var hasAllTables = true;
                foreach (var table in new[] { "cache_metadata", "cache_artifacts", "cache_job_state" })
                {
                    command.Parameters.Clear();
                    command.CommandText =
                        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;";
                    command.Parameters.AddWithValue("@table", table);
                    var count =
                        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (count == 0)
                    {
                        hasAllTables = false;
                        errors.Add(
                            $"Missing required table '{table}'. " +
                            "The snapshot database may be incomplete or corrupt.");
                    }
                }

                if (errors.Count == 0 && hasAllTables)
                {
                    command.Parameters.Clear();
                    command.CommandText = "SELECT COUNT(*) FROM cache_metadata;";
                    metadataEntries =
                        Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

                    command.Parameters.Clear();
                    command.CommandText = """
                        SELECT file_path, file_size
                        FROM cache_artifacts
                        ORDER BY file_path;
                        """;
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        artifactRows.Add(new ArtifactRow(reader.GetString(0), reader.GetInt64(1)));
                    }
                }
            }

            if (HasSidecar(lexicalDbPath, errors) ||
                (!string.Equals(dbPath, lexicalDbPath, PathComparison) &&
                 HasSidecar(dbPath, errors)))
            {
                return Task.FromResult(new SnapshotValidationResult(
                    false,
                    errors,
                    warnings,
                    metadataEntries,
                    artifactRows.Count,
                    missingFiles));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to open or read snapshot database: {ex.Message}");
        }

        if (errors.Count > 0)
        {
            return Task.FromResult(new SnapshotValidationResult(
                false,
                errors,
                warnings,
                metadataEntries,
                artifactRows.Count,
                missingFiles));
        }

        artifactEntries = artifactRows.Count;
        foreach (var row in artifactRows)
        {
            ct.ThrowIfCancellationRequested();
            if (row.FileSize < 0)
            {
                errors.Add(
                    $"Artifact has an invalid persisted size ({row.FileSize}): {row.FilePath}");
                continue;
            }

            if (!SnapshotExporter.TryGetArtifactCandidate(
                    artifactsPath,
                    row.FilePath,
                    out var candidate,
                    out var pathError))
            {
                errors.Add(pathError);
                continue;
            }

            if (physicalArtifactsPath == null)
            {
                AddMissingFile(row.FilePath, errors, ref missingFiles);
                continue;
            }

            bool candidateExists;
            try
            {
                candidateExists = SnapshotExporter.PathEntryExists(candidate);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(
                    $"Artifact path cannot be inspected safely: {row.FilePath} ({ex.Message})");
                continue;
            }

            if (!candidateExists)
            {
                bool safelyMissing;
                string unsafeReason;
                try
                {
                    safelyMissing =
                        IsSafelyMissing(candidate, physicalArtifactsPath, out unsafeReason);
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add(
                        $"Artifact path cannot be inspected safely: {row.FilePath} ({ex.Message})");
                    continue;
                }

                if (safelyMissing)
                {
                    AddMissingFile(row.FilePath, errors, ref missingFiles);
                }
                else
                {
                    errors.Add(
                        $"Artifact path cannot be resolved safely: {row.FilePath} ({unsafeReason})");
                }

                continue;
            }

            if (!File.Exists(candidate))
            {
                errors.Add($"Artifact path does not reference a regular file: {row.FilePath}");
                continue;
            }

            string physicalArtifactPath;
            try
            {
                physicalArtifactPath =
                    SnapshotExporter.CanonicalizeExistingPath(candidate, requireDirectory: false);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(
                    $"Artifact path cannot be resolved safely: {row.FilePath} ({ex.Message})");
                continue;
            }

            if (!SnapshotExporter.IsEqualOrDescendant(
                    physicalArtifactPath,
                    physicalArtifactsPath))
            {
                errors.Add(
                    $"Artifact path escapes the physical artifacts/ directory: {row.FilePath}");
                continue;
            }

            SnapshotFileHash artifactHash;
            try
            {
                artifactHash = SnapshotExporter.HashFileRequiringExactlyOneLink(
                    physicalArtifactPath,
                    ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add(
                    $"Artifact file cannot be inspected safely: {row.FilePath} ({ex.Message})");
                continue;
            }

            if (artifactHash.FileSize != row.FileSize)
            {
                errors.Add(
                    $"Artifact file size mismatch for '{row.FilePath}': " +
                    $"expected {row.FileSize}, found {artifactHash.FileSize}.");
            }
        }

        if (missingFiles > MaxMissingFileErrors)
        {
            errors.Add(
                $"...and {missingFiles - MaxMissingFileErrors} more missing artifact file(s). " +
                $"Total missing: {missingFiles}.");
        }

        return Task.FromResult(new SnapshotValidationResult(
            errors.Count == 0,
            errors,
            warnings,
            metadataEntries,
            artifactEntries,
            missingFiles));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsStrictDescendant(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, PathComparison))
            return false;

        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootPrefix, PathComparison);
    }

    private static SqliteConnection OpenReadOnlyConnection(string dbPath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static bool HasSidecar(string dbPath, List<string> errors)
    {
        var found = false;
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = dbPath + suffix;
            try
            {
                if (!SnapshotExporter.PathEntryExists(sidecar))
                    continue;
            }
            catch (InvalidOperationException ex)
            {
                errors.Add($"Unable to verify SQLite sidecar absence: {ex.Message}");
                found = true;
                continue;
            }

            errors.Add($"Snapshot must not contain SQLite sidecar file: {sidecar}");
            found = true;
        }

        return found;
    }

    private static bool IsSafelyMissing(
        string candidate,
        string physicalArtifactsRoot,
        out string reason)
    {
        reason = string.Empty;
        var current = Path.GetDirectoryName(candidate);
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
        {
            if (SnapshotExporter.PathEntryExists(current))
            {
                reason = $"path component is not a resolvable directory: {current}";
                return false;
            }

            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrEmpty(current))
        {
            reason = "no existing parent directory could be established";
            return false;
        }

        try
        {
            var physicalParent =
                SnapshotExporter.CanonicalizeExistingPath(current, requireDirectory: true);
            if (!SnapshotExporter.IsEqualOrDescendant(
                    physicalParent,
                    physicalArtifactsRoot))
            {
                reason = "an existing parent resolves outside the artifacts/ directory";
                return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            reason = ex.Message;
            return false;
        }

        return true;
    }

    private static void AddMissingFile(
        string relativePath,
        List<string> errors,
        ref int missingFiles)
    {
        missingFiles++;
        if (missingFiles <= MaxMissingFileErrors)
            errors.Add($"Missing artifact file referenced in database: {relativePath}");
    }

    private static SnapshotValidationResult Fail(
        List<string> errors,
        List<string> warnings)
        => new(false, errors, warnings, 0, 0, 0);

    private sealed record ArtifactRow(string FilePath, long FileSize);
}
