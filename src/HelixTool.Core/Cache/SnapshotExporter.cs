using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

    private static StringComparison ConservativeDenyListPathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparison PositivePathProofComparison => OperatingSystem.IsWindows()
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
    /// or artifact tree. Its parent must be a trusted namespace: no other same-principal process
    /// may rename, replace, or mutate entries in that parent while export is in progress.
    /// </param>
    /// <param name="progress">
    /// Optional synchronous callback that receives human-readable status strings.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="ExportResult"/> describing the completed export.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the source is invalid, a path cannot be resolved safely, the destination is
    /// unsafe or already exists, or the temporary snapshot fails validation.
    /// </exception>
    /// <remarks>
    /// The destination parent is selected and retained before the first progress callback.
    /// Cooperative parent moves and alias retargeting are checked at explicit revalidation points.
    /// These point-in-time checks are not a security boundary against a process with write access
    /// to that namespace. Progress callbacks run synchronously. After the final callback returns,
    /// the exporter validates the serialized database, artifact correspondence, exact staged tree,
    /// single-link file ownership, and absence of SQLite sidecars, then publishes with an atomic
    /// no-replace rename and no further callbacks.
    /// </remarks>
    public static async Task<ExportResult> ExportAsync(
        string sourceRoot,
        string destination,
        Action<string>? progress = null,
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

        var sourceRootIdentity =
            SnapshotDestinationDirectory.GetDirectoryIdentityNoFollow(physicalSourceRoot);
        SnapshotDirectoryIdentity? sourceArtifactsIdentity = physicalSourceArtifacts == null
            ? null
            : SnapshotDestinationDirectory.GetDirectoryIdentityNoFollow(physicalSourceArtifacts);

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

        // Select and retain the destination parent before invoking any user callback.
        using var destinationDirectory =
            SnapshotDestinationDirectory.Open(physicalDestinationParent);
        ValidateRetainedDestination();

        string ValidateRetainedDestination()
        {
            var retainedDestinationParent = destinationDirectory.GetCurrentPhysicalPath(
                lexicalDestinationParent);
            var retainedDestination =
                Path.GetFullPath(Path.Combine(retainedDestinationParent, destinationLeaf));
            RejectDestinationWithinSource(
                retainedDestination,
                physicalSourceRoot,
                physicalSourceArtifacts);
            SnapshotDestinationDirectory.RejectSourceIdentityInDestinationAncestors(
                retainedDestinationParent,
                sourceRootIdentity,
                sourceArtifactsIdentity);
            return retainedDestinationParent;
        }

        using var sourceConnection =
            OpenConnection(physicalDbPath, SqliteOpenMode.ReadOnly);

        progress?.Invoke("Validating source cache schema...");
        ValidateSourceSchema(sourceConnection);

        ValidateRetainedDestination();
        using var temporaryDirectory =
            destinationDirectory.CreateTemporaryDirectory(destinationLeaf);
        try
        {
            temporaryDirectory.CreateDirectory("artifacts");

            progress?.Invoke("Backing up cache.db...");
            using var stagedDatabase = OpenMemoryConnection();
            BackupDatabase(sourceConnection, stagedDatabase, ct);
            var serializedDatabase = SerializeDatabase(stagedDatabase);
            await WriteDatabaseAsync(
                temporaryDirectory,
                "cache.db",
                serializedDatabase,
                ct);
            var expectedDatabaseHash =
                Convert.ToHexString(SHA256.HashData(serializedDatabase));
            EnsureNoDatabaseSidecars(
                Path.Combine(temporaryDirectory.GetCurrentPath(), "cache.db"));

            progress?.Invoke("Reading artifact references from the backed-up database...");
            var artifactReferences = ReadArtifactReferences(stagedDatabase, ct);

            progress?.Invoke("Copying referenced artifacts...");
            var copiedArtifacts = await CopyArtifactsAsync(
                artifactReferences,
                physicalSourceArtifacts,
                temporaryDirectory,
                Path.Combine(temporaryDirectory.GetCurrentPath(), "artifacts"),
                ct);
            progress?.Invoke($"Copied {copiedArtifacts.Count} artifact file(s).");

            progress?.Invoke("Validating temporary snapshot...");
            progress?.Invoke("Finalizing snapshot (atomic rename)...");

            // There are no progress callbacks after this point. Revalidate the complete staged
            // tree and publish it immediately, without another caller-controlled callback.
            ct.ThrowIfCancellationRequested();
            var retainedParent = ValidateRetainedDestination();
            var retainedDestination = Path.Combine(retainedParent, destinationLeaf);
            if (PathEntryExists(retainedDestination))
            {
                throw new InvalidOperationException(
                    $"Destination already exists: {retainedDestination}. It was not overwritten.");
            }

            var tempDestination = temporaryDirectory.GetCurrentPath();
            var tempDbPath = Path.Combine(tempDestination, "cache.db");
            EnsureNoDatabaseSidecars(tempDbPath);
            ValidatePortableLayout(
                tempDestination,
                expectedDatabaseHash,
                copiedArtifacts,
                ct);

            var validation = await SnapshotValidator.ValidateAsync(tempDestination, ct);
            if (!validation.IsValid)
            {
                throw new InvalidOperationException(
                    "Temporary snapshot validation failed: " +
                    string.Join(" ", validation.Errors));
            }

            EnsureNoDatabaseSidecars(tempDbPath);
            var dbSize = new FileInfo(tempDbPath).Length;
            ct.ThrowIfCancellationRequested();
            ValidateRetainedDestination();
            destinationDirectory.Publish(
                temporaryDirectory,
                lexicalDestinationParent,
                destinationLeaf,
                ct);

            return new ExportResult(
                Destination: lexicalDestination,
                ArtifactCount: copiedArtifacts.Count,
                DbSizeBytes: dbSize);
        }

        catch (Exception exportFailure)
        {
            try
            {
                // Cleanup must still run when the exception was the caller's cancellation.
                destinationDirectory.Cleanup(
                    temporaryDirectory,
                    CancellationToken.None);
            }
            catch (Exception cleanupFailure)
            {
                exportFailure.Data["SnapshotCleanupFailure"] = cleanupFailure;
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
        ValidateSourceSchema(connection);
    }

    private static void ValidateSourceSchema(SqliteConnection connection)
    {
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
        => IsEqualOrDescendantUsingComparison(candidate, root, PositivePathProofComparison);

    internal static bool CouldBeEqualOrDescendant(string candidate, string root)
        => IsEqualOrDescendantUsingComparison(
            candidate,
            root,
            ConservativeDenyListPathComparison);

    private static bool IsEqualOrDescendantUsingComparison(
        string candidate,
        string root,
        StringComparison comparison)
    {
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(normalizedCandidate, normalizedRoot, comparison))
            return true;

        var rootPrefix = Path.EndsInDirectorySeparator(normalizedRoot)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootPrefix, comparison);
    }

    internal static bool PathsAreEqualForPositiveProof(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            PositivePathProofComparison);

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

        if (PathsAreEqualForPositiveProof(candidate, artifactsRoot) ||
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
        if (CouldBeEqualOrDescendant(destination, sourceRoot))
            throw new InvalidOperationException(
                $"Destination must not be the source cache root or a child of it: {destination}");

        if (sourceArtifacts != null &&
            CouldBeEqualOrDescendant(destination, sourceArtifacts))
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

    private static SqliteConnection OpenMemoryConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void BackupDatabase(
        SqliteConnection sourceConnection,
        SqliteConnection destinationConnection,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        sourceConnection.BackupDatabase(destinationConnection);
        ct.ThrowIfCancellationRequested();

        using var command = destinationConnection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var journalMode = Convert.ToString(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture);
        if (!string.Equals(journalMode, "memory", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"In-memory snapshot database reported unexpected journaling mode '{journalMode}'.");
    }

    private static IReadOnlyList<ArtifactReference> ReadArtifactReferences(
        SqliteConnection connection,
        CancellationToken ct)
    {
        var references = new List<ArtifactReference>();
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

    private static byte[] SerializeDatabase(SqliteConnection connection)
    {
        var pointer = SQLitePCL.raw.sqlite3_serialize(
            connection.Handle,
            "main",
            out var size,
            0);
        if (pointer == IntPtr.Zero || size <= 0)
            throw new InvalidOperationException("Unable to serialize the in-memory snapshot database.");
        if (size > Array.MaxLength)
        {
            SQLitePCL.raw.sqlite3_free(pointer);
            throw new InvalidOperationException(
                $"The snapshot database is too large to serialize safely ({size} bytes).");
        }

        try
        {
            var bytes = GC.AllocateUninitializedArray<byte>((int)size);
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            NormalizeSerializedDatabaseHeader(bytes);
            return bytes;
        }
        finally
        {
            SQLitePCL.raw.sqlite3_free(pointer);
        }
    }

    private static void NormalizeSerializedDatabaseHeader(byte[] database)
    {
        ReadOnlySpan<byte> sqliteHeader = "SQLite format 3\0"u8;
        if (database.Length < 100 ||
            !database.AsSpan(0, sqliteHeader.Length).SequenceEqual(sqliteHeader) ||
            database[18] is not (1 or 2) ||
            database[19] is not (1 or 2))
        {
            throw new InvalidOperationException(
                "The in-memory backup did not serialize as a valid SQLite database image.");
        }

        // In-memory databases report MEMORY journaling but retain WAL header versions copied
        // from a WAL-mode source. A standalone serialized image must request rollback journaling.
        database[18] = 1;
        database[19] = 1;
    }

    private static async Task WriteDatabaseAsync(
        SnapshotTemporaryDirectory temporaryDirectory,
        string destinationRelativePath,
        byte[] database,
        CancellationToken ct)
    {
        await using var destination =
            temporaryDirectory.CreateNewFile(destinationRelativePath);
        await destination.WriteAsync(database, ct);
        await destination.FlushAsync(ct);
        if (destination.Length != database.LongLength)
        {
            throw new IOException(
                $"Serialized snapshot database write was incomplete " +
                $"(expected {database.LongLength}, wrote {destination.Length}).");
        }
    }

    private static async Task<IReadOnlyDictionary<string, CopiedArtifact>> CopyArtifactsAsync(
        IReadOnlyList<ArtifactReference> references,
        string? sourceArtifacts,
        SnapshotTemporaryDirectory temporaryDirectory,
        string destinationArtifacts,
        CancellationToken ct)
    {
        var copiedDestinations = new Dictionary<string, CopiedArtifact>(PathComparer);

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
                PathsAreEqualForPositiveProof(destinationPath, destinationArtifacts))
            {
                throw new InvalidOperationException(
                    $"Artifact destination path is unsafe: {reference.FilePath}");
            }

            var normalizedDestination =
                Path.GetRelativePath(destinationArtifacts, destinationPath);
            if (copiedDestinations.TryGetValue(
                    normalizedDestination,
                    out var copiedArtifact))
            {
                if (copiedArtifact.FileSize != reference.FileSize)
                    throw new InvalidOperationException(
                        $"Artifact rows resolve to the same file with conflicting sizes: " +
                        $"{reference.FilePath}");
                continue;
            }

            _ = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException(
                    $"Artifact destination has no parent directory: {reference.FilePath}");
            var stagingRelativePath = Path.Combine("artifacts", normalizedRelativePath);
            var stagingDirectory = Path.GetDirectoryName(stagingRelativePath)
                ?? throw new InvalidOperationException(
                    $"Artifact staging destination has no parent: {reference.FilePath}");
            temporaryDirectory.CreateDirectory(stagingDirectory);
            var sha256 = await CopyArtifactAsync(
                physicalSourcePath,
                temporaryDirectory,
                stagingRelativePath,
                reference.FilePath,
                reference.FileSize,
                ct);
            copiedDestinations.Add(
                normalizedDestination,
                new CopiedArtifact(reference.FileSize, sha256));
        }

        return copiedDestinations;
    }

    private static async Task<string> CopyArtifactAsync(
        string sourcePath,
        SnapshotTemporaryDirectory temporaryDirectory,
        string destinationRelativePath,
        string relativePath,
        long expectedSize,
        CancellationToken ct)
    {
        long copiedSize;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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

            await using var destination =
                temporaryDirectory.CreateNewFile(destinationRelativePath);
            var buffer = GC.AllocateUninitializedArray<byte>(81920);
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, ct)) != 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }
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

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ValidatePortableLayout(
        string snapshotPath,
        string expectedDatabaseHash,
        IReadOnlyDictionary<string, CopiedArtifact> copiedArtifacts,
        CancellationToken ct)
    {
        var rootEntries = new DirectoryInfo(snapshotPath)
            .EnumerateFileSystemInfos()
            .ToDictionary(entry => entry.Name, PathComparer);
        if (rootEntries.Count != 2 ||
            !rootEntries.TryGetValue("cache.db", out var databaseEntry) ||
            databaseEntry is not FileInfo ||
            IsFileSystemLink(databaseEntry) ||
            !rootEntries.TryGetValue("artifacts", out var artifactsEntry) ||
            artifactsEntry is not DirectoryInfo artifactsDirectory ||
            IsFileSystemLink(artifactsEntry))
        {
            throw new InvalidOperationException(
                "Temporary snapshot must contain only a direct cache.db file and artifacts directory.");
        }

        var databaseHash = HashFileRequiringExactlyOneLink(databaseEntry.FullName, ct);
        if (!string.Equals(
                databaseHash.Sha256,
                expectedDatabaseHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The serialized cache.db changed before final validation.");
        }
        var expectedDirectories = new HashSet<string>(PathComparer);
        foreach (var relativePath in copiedArtifacts.Keys)
        {
            var directory = Path.GetDirectoryName(relativePath);
            while (!string.IsNullOrEmpty(directory) && directory != ".")
            {
                expectedDirectories.Add(directory);
                directory = Path.GetDirectoryName(directory);
            }
        }

        var remainingArtifacts = new HashSet<string>(copiedArtifacts.Keys, PathComparer);
        ValidateArtifactDirectory(artifactsDirectory, string.Empty);
        if (remainingArtifacts.Count != 0)
        {
            throw new InvalidOperationException(
                "The temporary snapshot is missing one or more copied artifact files.");
        }

        void ValidateArtifactDirectory(DirectoryInfo directory, string relativeDirectory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();
                if (IsFileSystemLink(entry))
                {
                    throw new InvalidOperationException(
                        $"The temporary snapshot contains an unsafe filesystem link: {entry.FullName}");
                }

                var relativePath = string.IsNullOrEmpty(relativeDirectory)
                    ? entry.Name
                    : Path.Combine(relativeDirectory, entry.Name);
                if (entry is DirectoryInfo childDirectory)
                {
                    if (!expectedDirectories.Contains(relativePath))
                    {
                        throw new InvalidOperationException(
                            $"The temporary snapshot contains an unreferenced artifact directory: " +
                            $"{relativePath}");
                    }

                    ValidateArtifactDirectory(childDirectory, relativePath);
                    continue;
                }

                if (entry is not FileInfo file ||
                    !copiedArtifacts.TryGetValue(relativePath, out var expectedArtifact))
                {
                    throw new InvalidOperationException(
                        $"The temporary snapshot contains an unreferenced artifact entry: " +
                        $"{relativePath}");
                }

                var artifactHash = HashFileRequiringExactlyOneLink(file.FullName, ct);
                if (artifactHash.FileSize != expectedArtifact.FileSize ||
                    !string.Equals(
                        artifactHash.Sha256,
                        expectedArtifact.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Copied artifact '{relativePath}' changed before final validation.");
                }

                remainingArtifacts.Remove(relativePath);
            }
        }
    }

    private static bool IsFileSystemLink(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
        entry.LinkTarget != null;

    internal static SnapshotFileHash HashFileRequiringExactlyOneLink(
        string path,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.SequentialScan);
        SnapshotDestinationDirectory.EnsureExactlyOneHardLink(
            stream.SafeFileHandle,
            path);
        var expectedLength = stream.Length;
        var buffer = GC.AllocateUninitializedArray<byte>(81920);
        int bytesRead;
        while ((bytesRead = stream.Read(buffer)) != 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, bytesRead);
        }

        var sha256 = Convert.ToHexString(hash.GetHashAndReset());
        SnapshotDestinationDirectory.EnsureExactlyOneHardLink(
            stream.SafeFileHandle,
            path);
        ct.ThrowIfCancellationRequested();
        if (stream.Length != expectedLength)
            throw new InvalidOperationException($"Snapshot file changed while hashing: {path}");

        return new SnapshotFileHash(
            expectedLength,
            sha256);
    }

    private static void EnsureNoDatabaseSidecars(string dbPath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecarPath = dbPath + suffix;
            if (!PathEntryExists(sidecarPath))
                continue;
            throw new InvalidOperationException(
                $"Unexpected SQLite sidecar entry was created: {sidecarPath}");
        }
    }

    private sealed record ArtifactReference(string FilePath, long FileSize);

    private sealed record CopiedArtifact(long FileSize, string Sha256);
}

internal readonly record struct SnapshotFileHash(long FileSize, string Sha256);

/// <summary>Result of a successful snapshot export operation.</summary>
/// <param name="Destination">Absolute path to the created snapshot directory.</param>
/// <param name="ArtifactCount">Number of distinct artifact files copied.</param>
/// <param name="DbSizeBytes">Size of the exported <c>cache.db</c> in bytes.</param>
public sealed record ExportResult(
    string Destination,
    int ArtifactCount,
    long DbSizeBytes);
