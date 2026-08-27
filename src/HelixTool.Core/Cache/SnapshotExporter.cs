using System.Globalization;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.Cache;

/// <summary>
/// Exports a portable eval snapshot from the current cache root.
/// <para>
/// The live database is captured with SQLite's online backup API. Artifact files are then selected
/// from that backed-up database, copied into a same-parent temporary directory, validated, and
/// published with an atomic directory rename.
/// </para>
/// </summary>
public static class SnapshotExporter
{
    internal const int SchemaVersion = 1;

    private const int BusyTimeoutSeconds = 5;
    private const int BusyTimeoutMilliseconds = BusyTimeoutSeconds * 1000;
    private const int MaxLinkResolutions = 64;

    private static StringComparison BoundaryPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    /// Export a snapshot of <paramref name="sourceRoot"/> to <paramref name="destination"/>.
    /// </summary>
    /// <param name="sourceRoot">
    /// Effective cache root to export (from <see cref="CacheOptions.GetEffectiveCacheRoot"/>).
    /// Must contain a valid <c>cache.db</c>.
    /// </param>
    /// <param name="destination">
    /// Destination directory path. Must not already exist or be within the physical source cache
    /// or artifact tree.
    /// </param>
    /// <param name="progress">Optional progress reporter; receives human-readable status strings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="ExportResult"/> describing the completed export.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the source is invalid, a path cannot be resolved safely, the destination is
    /// unsafe or already exists, or the temporary snapshot fails validation.
    /// </exception>
    public static async Task<ExportResult> ExportAsync(
        string sourceRoot,
        string destination,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var lexicalSourceRoot = Path.GetFullPath(sourceRoot);
        var lexicalDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        if (!Directory.Exists(lexicalSourceRoot))
            throw new InvalidOperationException(
                $"Source cache root does not exist: {lexicalSourceRoot}");

        var lexicalDbPath = Path.Combine(lexicalSourceRoot, "cache.db");
        if (!File.Exists(lexicalDbPath))
            throw new InvalidOperationException(
                $"Source cache database not found: {lexicalDbPath}. " +
                "Run hlx (without HLX_EVAL_SNAPSHOT) to populate the cache first.");

        var destinationLeaf = Path.GetFileName(lexicalDestination);
        var lexicalDestinationParent = Path.GetDirectoryName(lexicalDestination);
        if (string.IsNullOrEmpty(destinationLeaf) || string.IsNullOrEmpty(lexicalDestinationParent))
            throw new InvalidOperationException(
                $"Destination must name a new directory beneath an existing parent: {lexicalDestination}");
        if (!Directory.Exists(lexicalDestinationParent))
            throw new InvalidOperationException(
                $"Destination parent directory does not exist: {lexicalDestinationParent}");

        // Resolve all physical boundaries before creating a temp directory, opening a destination
        // database, or reading artifact rows.
        var physicalSourceRoot = CanonicalizeExistingPath(lexicalSourceRoot, requireDirectory: true);
        var physicalDbPath = CanonicalizeExistingPath(lexicalDbPath, requireDirectory: false);

        var lexicalSourceArtifacts = Path.Combine(lexicalSourceRoot, "artifacts");
        string? physicalSourceArtifacts = null;
        if (Directory.Exists(lexicalSourceArtifacts))
        {
            physicalSourceArtifacts =
                CanonicalizeExistingPath(lexicalSourceArtifacts, requireDirectory: true);
        }
        else if (PathEntryExists(lexicalSourceArtifacts))
        {
            throw new InvalidOperationException(
                $"Source artifacts path cannot be resolved as a directory: {lexicalSourceArtifacts}");
        }

        var physicalDestinationParent =
            CanonicalizeExistingPath(lexicalDestinationParent, requireDirectory: true);
        var physicalDestination =
            Path.GetFullPath(Path.Combine(physicalDestinationParent, destinationLeaf));

        RejectDestinationWithinSource(
            physicalDestination,
            physicalSourceRoot,
            physicalSourceArtifacts);

        if (PathEntryExists(physicalDestination))
            throw new InvalidOperationException(
                $"Destination already exists: {physicalDestination}. " +
                "Remove it first or choose a different path to avoid overwriting a snapshot.");

        progress?.Report("Validating source cache schema...");
        ValidateSourceSchema(physicalDbPath);

        var tempDestination = Path.Combine(
            physicalDestinationParent,
            $"{destinationLeaf}.tmp.{Guid.NewGuid():N}");
        while (PathEntryExists(tempDestination))
        {
            tempDestination = Path.Combine(
                physicalDestinationParent,
                $"{destinationLeaf}.tmp.{Guid.NewGuid():N}");
        }

        string? tempDestinationToClean = tempDestination;
        try
        {
            Directory.CreateDirectory(tempDestination);
            var tempDbPath = Path.Combine(tempDestination, "cache.db");
            var tempArtifacts = Path.Combine(tempDestination, "artifacts");
            Directory.CreateDirectory(tempArtifacts);

            progress?.Report("Backing up cache.db...");
            BackupDatabase(physicalDbPath, tempDbPath, ct);
            EnsureNoDatabaseSidecars(tempDbPath);

            progress?.Report("Reading artifact references from the backed-up database...");
            var artifactReferences = ReadArtifactReferences(tempDbPath, ct);

            progress?.Report("Copying referenced artifacts...");
            var artifactCount = await CopyArtifactsAsync(
                artifactReferences,
                physicalSourceArtifacts,
                tempArtifacts,
                ct);
            progress?.Report($"Copied {artifactCount} artifact file(s).");

            EnsureNoDatabaseSidecars(tempDbPath);

            progress?.Report("Validating temporary snapshot...");
            var validation = await SnapshotValidator.ValidateAsync(tempDestination, ct);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    "Temporary snapshot validation failed: " +
                    string.Join(" ", validation.Errors));
            }

            EnsureNoDatabaseSidecars(tempDbPath);
            var dbSize = new FileInfo(tempDbPath).Length;

            // Re-resolve the caller-supplied parent so retargeting an alias cannot redirect
            // publication. The move itself always uses the selected physical parent.
            var currentPhysicalDestinationParent =
                CanonicalizeExistingPath(lexicalDestinationParent, requireDirectory: true);
            if (!string.Equals(
                    currentPhysicalDestinationParent,
                    physicalDestinationParent,
                    BoundaryPathComparison))
            {
                throw new InvalidOperationException(
                    "Destination parent changed while the snapshot was being exported.");
            }

            if (PathEntryExists(physicalDestination))
                throw new InvalidOperationException(
                    $"Destination already exists: {physicalDestination}. " +
                    "It was not overwritten.");

            progress?.Report("Finalizing snapshot (atomic rename)...");
            Directory.Move(tempDestination, physicalDestination);
            tempDestinationToClean = null;

            return new ExportResult(
                Destination: lexicalDestination,
                ArtifactCount: artifactCount,
                DbSizeBytes: dbSize);
        }
        catch
        {
            if (tempDestinationToClean != null)
            {
                try
                {
                    Directory.Delete(tempDestinationToClean, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup preserves the original export failure.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Validate the source database schema without modifying the database.
    /// </summary>
    internal static void ValidateSourceSchema(string dbPath)
    {
        using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);

        if (version == 0)
            throw new InvalidOperationException(
                "Source cache database has schema version 0. " +
                "The cache may be empty or was never fully initialized. " +
                "Run hlx in normal mode to populate the cache before exporting.");

        if (version != SchemaVersion)
            throw new InvalidOperationException(
                $"Source cache schema version {version} is not supported (expected {SchemaVersion}). " +
                "Ensure hlx is up-to-date or use the version of hlx that populated this cache.");

        foreach (var table in new[] { "cache_metadata", "cache_artifacts", "cache_job_state" })
        {
            command.Parameters.Clear();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@table;";
            command.Parameters.AddWithValue("@table", table);
            var count = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count == 0)
                throw new InvalidOperationException(
                    $"Source cache database is missing expected table '{table}'. " +
                    "The database may be corrupt or from an unsupported version.");
        }
    }

    internal static string CanonicalizeExistingPath(string path, bool requireDirectory)
    {
        var fullPath = Path.GetFullPath(path);
        try
        {
            return CanonicalizeExistingPathCore(
                fullPath,
                requireDirectory,
                new HashSet<string>(PathComparer));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Unable to establish the physical path for '{fullPath}': {ex.Message}",
                ex);
        }
    }

    internal static bool IsEqualOrDescendant(string candidate, string root)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, BoundaryPathComparison))
            return true;

        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootPrefix, BoundaryPathComparison);
    }

    internal static bool PathEntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return true;

        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Unable to determine whether path exists: {Path.GetFullPath(path)}",
                ex);
        }

        try
        {
            return new FileInfo(path).LinkTarget != null ||
                new DirectoryInfo(path).LinkTarget != null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Unable to determine whether path exists: {Path.GetFullPath(path)}",
                ex);
        }
    }

    internal static bool TryGetArtifactCandidate(
        string artifactsRoot,
        string relativePath,
        out string candidate,
        out string error)
    {
        candidate = string.Empty;
        error = string.Empty;

        if (string.IsNullOrEmpty(relativePath))
        {
            error = "Artifact path is empty.";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = $"Artifact path is rooted: {relativePath}";
            return false;
        }

        var separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? new[] { Path.DirectorySeparatorChar }
            : new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        if (relativePath
            .Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(component => component == ".."))
        {
            error = $"Artifact path contains parent traversal: {relativePath}";
            return false;
        }

        try
        {
            candidate = Path.GetFullPath(Path.Combine(artifactsRoot, relativePath));
        }
        catch (Exception ex) when (
            ex is ArgumentException or IOException or NotSupportedException)
        {
            error = $"Artifact path cannot be normalized safely: {relativePath} ({ex.Message})";
            return false;
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(candidate),
                Path.TrimEndingDirectorySeparator(artifactsRoot),
                BoundaryPathComparison) ||
            !IsEqualOrDescendant(candidate, artifactsRoot))
        {
            error = $"Artifact path escapes the artifacts/ directory: {relativePath}";
            candidate = string.Empty;
            return false;
        }

        return true;
    }

    private static string CanonicalizeExistingPathCore(
        string fullPath,
        bool requireDirectory,
        HashSet<string> visitedLinks)
    {
        fullPath = Path.GetFullPath(fullPath);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            throw new InvalidOperationException(
                $"Path root does not exist or cannot be accessed: {fullPath}");

        var current = Path.GetFullPath(root);
        var remainder = fullPath[root.Length..];
        var separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? new[] { Path.DirectorySeparatorChar }
            : new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var components = remainder.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        if (components.Length == 0)
        {
            if (!requireDirectory)
                throw new InvalidOperationException($"Expected a file but found a directory: {fullPath}");
            return current;
        }

        for (var index = 0; index < components.Length; index++)
        {
            var isLast = index == components.Length - 1;
            var candidate = Path.Combine(current, components[index]);
            var isDirectory = Directory.Exists(candidate);
            var isFile = File.Exists(candidate);

            FileSystemInfo info;
            string? linkTarget;
            if (isDirectory)
            {
                info = new DirectoryInfo(candidate);
                linkTarget = info.LinkTarget;
            }
            else if (isFile)
            {
                info = new FileInfo(candidate);
                linkTarget = info.LinkTarget;
            }
            else
            {
                var directoryInfo = new DirectoryInfo(candidate);
                linkTarget = directoryInfo.LinkTarget;
                if (linkTarget != null)
                {
                    info = directoryInfo;
                }
                else
                {
                    var fileInfo = new FileInfo(candidate);
                    linkTarget = fileInfo.LinkTarget;
                    info = fileInfo;
                }

                if (linkTarget == null)
                    throw new InvalidOperationException(
                        $"Path component does not exist or cannot be resolved: {candidate}");
            }

            var isReparsePoint =
                (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            if (isReparsePoint || linkTarget != null)
            {
                if (linkTarget == null)
                    throw new InvalidOperationException(
                        $"Unsupported reparse point cannot be resolved safely: {candidate}");
                if (visitedLinks.Count == MaxLinkResolutions)
                    throw new InvalidOperationException(
                        $"Too many symbolic-link or reparse-point resolutions while resolving '{fullPath}'.");

                var linkPath = Path.GetFullPath(info.FullName);
                if (!visitedLinks.Add(linkPath))
                    throw new InvalidOperationException(
                        $"Symbolic-link or reparse-point cycle detected at: {linkPath}");

                FileSystemInfo? resolvedTarget;
                try
                {
                    resolvedTarget = info.ResolveLinkTarget(returnFinalTarget: true);
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve symbolic link or reparse point '{candidate}': {ex.Message}",
                        ex);
                }

                if (resolvedTarget == null)
                    throw new InvalidOperationException(
                        $"Dangling or unsupported symbolic link or reparse point: {candidate}");

                current = CanonicalizeExistingPathCore(
                    resolvedTarget.FullName,
                    requireDirectory: !isLast || requireDirectory,
                    visitedLinks);
                continue;
            }

            if (!isLast && !isDirectory)
                throw new InvalidOperationException(
                    $"Non-directory path component encountered while resolving: {candidate}");
            if (isLast && requireDirectory && !isDirectory)
                throw new InvalidOperationException($"Expected a directory: {candidate}");
            if (isLast && !requireDirectory && !isFile)
                throw new InvalidOperationException($"Expected a file: {candidate}");

            current = Path.GetFullPath(candidate);
        }

        return current;
    }

    private static void RejectDestinationWithinSource(
        string destination,
        string sourceRoot,
        string? sourceArtifacts)
    {
        if (IsEqualOrDescendant(destination, sourceRoot))
            throw new InvalidOperationException(
                $"Destination must not be the source cache root or a child of it: {destination}");

        if (sourceArtifacts != null && IsEqualOrDescendant(destination, sourceArtifacts))
            throw new InvalidOperationException(
                $"Destination must not be the source artifacts directory or a child of it: {destination}");
    }

    private static SqliteConnection OpenConnection(string dbPath, SqliteOpenMode mode)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = mode,
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

    private static void BackupDatabase(
        string sourceDbPath,
        string destinationDbPath,
        CancellationToken ct)
    {
        using var sourceConnection = OpenConnection(sourceDbPath, SqliteOpenMode.ReadOnly);
        using var destinationConnection =
            OpenConnection(destinationDbPath, SqliteOpenMode.ReadWriteCreate);

        ct.ThrowIfCancellationRequested();
        sourceConnection.BackupDatabase(destinationConnection);
        ct.ThrowIfCancellationRequested();

        using var command = destinationConnection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        var journalMode = Convert.ToString(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
        if (!string.Equals(journalMode, "delete", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Exported database could not switch to DELETE journaling (reported '{journalMode}').");
    }

    private static IReadOnlyList<ArtifactReference> ReadArtifactReferences(
        string dbPath,
        CancellationToken ct)
    {
        var references = new List<ArtifactReference>();
        using var connection = OpenConnection(dbPath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_path, file_size
            FROM cache_artifacts
            ORDER BY file_path;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            references.Add(new ArtifactReference(reader.GetString(0), reader.GetInt64(1)));
        }

        return references;
    }

    private static async Task<int> CopyArtifactsAsync(
        IReadOnlyList<ArtifactReference> references,
        string? sourceArtifacts,
        string destinationArtifacts,
        CancellationToken ct)
    {
        var copiedDestinations = new Dictionary<string, long>(PathComparer);

        foreach (var reference in references)
        {
            ct.ThrowIfCancellationRequested();
            if (reference.FileSize < 0)
                throw new InvalidOperationException(
                    $"Artifact '{reference.FilePath}' has an invalid persisted size: {reference.FileSize}.");
            if (sourceArtifacts == null)
                throw new InvalidOperationException(
                    $"Artifact '{reference.FilePath}' is referenced by the database, " +
                    "but the source artifacts directory does not exist.");
            if (!TryGetArtifactCandidate(
                    sourceArtifacts,
                    reference.FilePath,
                    out var lexicalSourcePath,
                    out var pathError))
            {
                throw new InvalidOperationException(pathError);
            }

            string physicalSourcePath;
            try
            {
                physicalSourcePath =
                    CanonicalizeExistingPath(lexicalSourcePath, requireDirectory: false);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    $"Artifact '{reference.FilePath}' is unavailable or unsafe: {ex.Message}",
                    ex);
            }

            if (!IsEqualOrDescendant(physicalSourcePath, sourceArtifacts))
                throw new InvalidOperationException(
                    $"Artifact path escapes the physical artifacts directory: {reference.FilePath}");

            var normalizedRelativePath = Path.GetRelativePath(sourceArtifacts, lexicalSourcePath);
            var destinationPath =
                Path.GetFullPath(Path.Combine(destinationArtifacts, normalizedRelativePath));
            if (!IsEqualOrDescendant(destinationPath, destinationArtifacts) ||
                string.Equals(
                    Path.TrimEndingDirectorySeparator(destinationPath),
                    Path.TrimEndingDirectorySeparator(destinationArtifacts),
                    BoundaryPathComparison))
            {
                throw new InvalidOperationException(
                    $"Artifact destination path is unsafe: {reference.FilePath}");
            }

            if (copiedDestinations.TryGetValue(destinationPath, out var copiedSize))
            {
                if (copiedSize != reference.FileSize)
                    throw new InvalidOperationException(
                        $"Artifact rows resolve to the same file with conflicting sizes: " +
                        $"{reference.FilePath}");
                continue;
            }

            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException(
                    $"Artifact destination has no parent directory: {reference.FilePath}");
            Directory.CreateDirectory(destinationDirectory);
            await CopyArtifactAsync(
                physicalSourcePath,
                destinationPath,
                reference.FilePath,
                reference.FileSize,
                ct);
            copiedDestinations.Add(destinationPath, reference.FileSize);
        }

        return copiedDestinations.Count;
    }

    private static async Task CopyArtifactAsync(
        string sourcePath,
        string destinationPath,
        string relativePath,
        long expectedSize,
        CancellationToken ct)
    {
        long copiedSize;
        await using (var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (source.Length != expectedSize)
                throw new InvalidOperationException(
                    $"Artifact '{relativePath}' size changed or does not match the database " +
                    $"(expected {expectedSize}, found {source.Length}).");

            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, ct);
            await destination.FlushAsync(ct);
            copiedSize = destination.Length;

            if (source.Length != expectedSize)
                throw new InvalidOperationException(
                    $"Artifact '{relativePath}' changed while it was being copied.");
        }

        if (copiedSize != expectedSize)
            throw new InvalidOperationException(
                $"Copied artifact '{relativePath}' has the wrong size " +
                $"(expected {expectedSize}, copied {copiedSize}).");
    }

    private static void EnsureNoDatabaseSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecarPath = dbPath + suffix;
            if (!PathEntryExists(sidecarPath))
                continue;
            if (!File.Exists(sidecarPath))
                throw new InvalidOperationException(
                    $"Unexpected SQLite sidecar entry was created: {sidecarPath}");

            var sidecar = new FileInfo(sidecarPath);
            if ((sidecar.Attributes & FileAttributes.ReparsePoint) != 0 ||
                sidecar.LinkTarget != null)
            {
                throw new InvalidOperationException(
                    $"Unexpected linked SQLite sidecar was created: {sidecarPath}");
            }
            if (sidecar.Length != 0)
                throw new InvalidOperationException(
                    $"Unexpected non-empty SQLite sidecar was created: {sidecarPath}");

            File.Delete(sidecarPath);
            if (PathEntryExists(sidecarPath))
                throw new InvalidOperationException(
                    $"Unable to remove empty SQLite sidecar: {sidecarPath}");
        }
    }

    private sealed record ArtifactReference(string FilePath, long FileSize);
}

/// <summary>Result of a successful snapshot export operation.</summary>
/// <param name="Destination">Absolute path to the created snapshot directory.</param>
/// <param name="ArtifactCount">Number of distinct artifact files copied.</param>
/// <param name="DbSizeBytes">Size of the exported <c>cache.db</c> in bytes.</param>
public sealed record ExportResult(
    string Destination,
    int ArtifactCount,
    long DbSizeBytes);
