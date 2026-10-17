using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Content-addressed evidence inside the existing per-user cache trust boundary. Live entries
/// use the effective (auth-partitioned) cache root's delivery directory. Immutable snapshots
/// instead use the normal user cache root's eval-delivery/{snapshot-path-hash} directory;
/// disabled caches use base-root/delivery-ephemeral. Overlapping or unwritable eval roots
/// fall back to an external per-user delivery-ephemeral directory, never into a snapshot.
/// No separate ownership/ACL policy is imposed: this has the same trust model as cache.db.
/// </summary>
/// <remarks>
/// Every publication, lookup and read renews a 24-hour pin. A small delivery.db stores descriptors,
/// pins and accounting; immediate SQLite transactions serialize publication and eviction across
/// processes. Retention rechecks pins inside that transaction before deleting files. The delivery
/// sub-budget is one quarter of MaxSizeBytes (minimum one byte), or 256 MiB when caching is disabled.
/// Age eviction follows ArtifactMaxAge; pinned evidence always wins over the soft budget, with
/// overage reported by TryGetRetentionStatus. Content-addressed dedupe renews the existing pin.
/// </remarks>
public sealed class FileEvidenceDeliveryStore(CacheOptions cacheOptions) : IEvidenceDeliveryStore
{
    /// <summary>Minimum protection after a put, dedupe hit, lookup or read.</summary>
    public static readonly TimeSpan MinRetentionWindow = TimeSpan.FromHours(24);

    public FileStream CreateSpool(string cachePartition)
    {
        var root = RootFor(cachePartition);
        CreateDirectory(root);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
            BufferSize = 65536, Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(root, $"spool-{Guid.NewGuid():N}.partial"), options);
    }

    public FileStream CreateTransferSpool(string cachePartition, string transferId)
    {
        var root = RootFor(cachePartition);
        CreateDirectory(root);
        var path = Path.Combine(root, $"transfer-{SanitizeId(transferId)}.partial");
        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.Delete,
            BufferSize = 65536, Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public async Task<EvidenceDescriptor> PutStreamAsync(Stream content, string mediaType,
        string? sourceFingerprint, string? backingCacheKey, string cachePartition, CancellationToken ct = default)
    {
        var root = RootFor(cachePartition);
        await using var spool = CreateSpool(cachePartition);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        var disk = ResolveDrive(spool.Name);
        long bytes = 0;
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) != 0)
        {
            if (disk.AvailableFreeSpace < read + 1024L * 1024)
                throw new IOException($"insufficient_disk_headroom: required={read + 1024L * 1024}, available={disk.AvailableFreeSpace}; select a cache directory on sufficient storage.");
            await spool.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer.AsSpan(0, read));
            identity.AppendData(buffer.AsSpan(0, read));
            bytes += read;
        }
        await spool.FlushAsync(ct);
        var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // Include the media type in identity, as for PutBytes, without loading the payload.
        identity.AppendData(Encoding.UTF8.GetBytes(mediaType));
        var id = Convert.ToHexString(identity.GetHashAndReset()).ToLowerInvariant();
        var path = Path.Combine(root, $"{id}.bin");
        spool.Position = 0;
        var staging = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            CheckHeadroom(staging, bytes);
            await using (var output = OpenPrivateFile(staging, FileMode.CreateNew))
                await spool.CopyToAsync(output, 65536, ct);
            await using (var verify = File.OpenRead(staging))
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)), sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Evidence verification failed.");
            using var connection = OpenDatabase(root);
            using var transaction = connection.BeginTransaction(deferred: false);
            var reusable = false;
            if (File.Exists(path) && new FileInfo(path).Length == bytes)
            {
                await using var existing = File.OpenRead(path);
                reusable = Convert.ToHexString(await SHA256.HashDataAsync(existing, ct)).Equals(sha, StringComparison.OrdinalIgnoreCase);
            }
            if (!reusable)
            {
                if (File.Exists(path)) File.Replace(staging, path, null);
                else File.Move(staging, path);
            }
            var now = DateTimeOffset.UtcNow;
            var descriptor = BuildDescriptor(id, cachePartition, path, mediaType, sha, bytes, true,
                sourceFingerprint ?? sha, backingCacheKey, now) with { PinnedUntilUtc = now + MinRetentionWindow };
            SaveDescriptor(connection, transaction, descriptor, now);
            EnforceRetentionCore(root, connection, transaction);
            transaction.Commit();
            return descriptor;
        }

        finally { File.Delete(staging); }
    }

    public static void CheckHeadroom(string path, long additionalBytes)
    {
        var available = ResolveDrive(path).AvailableFreeSpace;
        if (additionalBytes > available - 1024L * 1024)
            throw new IOException($"insufficient_disk_headroom: required={additionalBytes + 1024L * 1024}, available={available}; use a cache/output directory on sufficient storage.");
    }

    public static DriveInfo ResolveDrive(string path)
    {
        var full = NormalizePath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Unix Path.GetPathRoot is "/" even for another mounted output/cache volume.
        return DriveInfo.GetDrives().OrderByDescending(d => d.Name.Length).FirstOrDefault(d =>
        {
            var root = Path.TrimEndingDirectorySeparator(d.Name);
            return full.Equals(root, comparison) || full.StartsWith(
                Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, comparison);
        }) ?? new DriveInfo(Path.GetPathRoot(full)!);
    }

    private readonly CacheOptions _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));
    private static readonly JsonSerializerOptions DescriptorJsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Invoked while the immediate SQLite transaction holds the cross-process write reservation.
    internal Action? RetentionSnapshotCapturedForTesting;

    private long DeliveryCapBytes => _cacheOptions.MaxSizeBytes > 0
        ? Math.Max(1, _cacheOptions.MaxSizeBytes / 4)
        : 256L * 1024 * 1024;

    private string RootFor(string cachePartition)
    {
        string root;
        if (_cacheOptions.EvalMode)
        {
            // CacheRoot denotes the immutable snapshot in eval mode, not the user's normal root.
            var snapshotPath = NormalizePath(_cacheOptions.GetEffectiveCacheRoot());
            var snapshotIdentity = snapshotPath;
            if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
                snapshotIdentity = snapshotIdentity.ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotIdentity))).ToLowerInvariant();
            var baseRoot = new CacheOptions().GetBaseCacheRoot();
            root = Path.Combine(baseRoot, "eval-delivery", hash);
            if (PathsOverlap(baseRoot, snapshotPath) ||
                PathsOverlap(Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition)), snapshotPath) ||
                !IsWritable(Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition))))
                root = ExternalFallbackRoot(snapshotPath, hash, cachePartition);
        }
        else if (_cacheOptions.MaxSizeBytes <= 0)
        {
            root = Path.Combine(_cacheOptions.GetBaseCacheRoot(), "delivery-ephemeral");
            if (!IsWritable(Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition))))
                root = ExternalFallbackRoot(null, null, cachePartition);
        }
        else
            root = Path.Combine(_cacheOptions.GetEffectiveCacheRoot(), "delivery");
        return Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition));
    }

    private static string ExternalFallbackRoot(string? snapshotPath, string? snapshotHash, string cachePartition)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hlx", "delivery-ephemeral"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hlx", "delivery-ephemeral")
        };
        foreach (var candidate in candidates)
        {
            var root = snapshotHash is null ? candidate : Path.Combine(candidate, snapshotHash);
            if ((snapshotPath is null ||
                 !PathsOverlap(Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition)), snapshotPath)) &&
                IsWritable(Path.Combine(root, CacheSecurity.SanitizeCacheKeySegment(cachePartition))))
                return root;
        }
        throw new IOException("No writable per-user evidence delivery directory outside the snapshot is available.");
    }

    private static bool PathsOverlap(string first, string second)
    {
        first = NormalizePath(first);
        second = NormalizePath(second);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(first, second, comparison) ||
            first.StartsWith(Path.EndsInDirectorySeparator(second) ? second : second + Path.DirectorySeparatorChar, comparison) ||
            second.StartsWith(Path.EndsInDirectorySeparator(first) ? first : first + Path.DirectorySeparatorChar, comparison);
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath)!;
        // Resolve existing ancestors too: the delivery directory itself usually does not exist yet.
        foreach (var segment in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current))
                current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool IsWritable(string root)
    {
        try
        {
            CreateDirectory(root);
            var probe = Path.Combine(root, $".write-probe-{Guid.NewGuid():N}");
            using var file = OpenPrivateFile(probe, FileMode.CreateNew, FileShare.None);
            file.Dispose();
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static FileStream OpenPrivateFile(string path, FileMode mode, FileShare share = FileShare.Read)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = share };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static SqliteConnection OpenDatabase(string root)
    {
        CreateDirectory(root);
        var path = Path.Combine(root, "delivery.db");
        // Only the first creator uses FileStream: reopening an active SQLite database can
        // conflict with SQLite's platform locks. A concurrent creator may win this race.
        if (!File.Exists(path))
        {
            try
            {
                using var file = OpenPrivateFile(path, FileMode.CreateNew, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 30
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS entries (
                    id TEXT PRIMARY KEY, descriptor TEXT NOT NULL, last_access INTEGER NOT NULL,
                    pinned_until INTEGER NOT NULL, size_bytes INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS retention_status (singleton INTEGER PRIMARY KEY, json TEXT NOT NULL);
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public EvidenceDescriptor PutText(string content, string mediaType, string? sourceFingerprint,
        string? backingCacheKey, string cachePartition)
    {
        ArgumentNullException.ThrowIfNull(content);
        return PutBytes(Encoding.UTF8.GetBytes(content), mediaType, sourceFingerprint, backingCacheKey, cachePartition);
    }

    public EvidenceDescriptor PutBytes(ReadOnlySpan<byte> content, string mediaType, string? sourceFingerprint,
        string? backingCacheKey, string cachePartition)
    {
        var root = RootFor(cachePartition);
        using var connection = OpenDatabase(root);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = DateTimeOffset.UtcNow;
        var evidenceId = ComputeContentAddressedId(content, mediaType);
        var dataPath = Path.Combine(root, $"{evidenceId}.bin");
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var existing = LoadDescriptor(connection, transaction, evidenceId);
        if (existing is null || !File.Exists(dataPath) || new FileInfo(dataPath).Length != content.Length)
            WriteAndVerify(dataPath, content, sha256);
        // Preserve content identity, but provenance belongs to this publication, not the first
        // request that happened to materialize identical bytes from a different backing key.
        var descriptor = BuildDescriptor(evidenceId, cachePartition, dataPath, mediaType, sha256,
            content.Length, true, sourceFingerprint, backingCacheKey, now)
            with { PinnedUntilUtc = now + MinRetentionWindow };
        SaveDescriptor(connection, transaction, descriptor, now);
        EnforceRetentionCore(root, connection, transaction);
        transaction.Commit();
        return descriptor;
    }

    private static EvidenceDescriptor BuildDescriptor(
        string evidenceId,
        string cachePartition,
        string dataPath,
        string mediaType,
        string sha256,
        long length,
        bool complete,
        string? sourceFingerprint,
        string? backingCacheKey,
        DateTimeOffset now) => new()
        {
            EvidenceId = evidenceId,
            ResourceUri = $"hlx-evidence://{cachePartition}/{evidenceId}",
            LocalPath = dataPath,
            MediaType = mediaType,
            Encoding = "binary",
            Bytes = length,
            Sha256 = sha256,
            Complete = complete,
            SourceFingerprint = sourceFingerprint,
            BackingCacheKey = backingCacheKey,
            CapturedAt = now,
            CachePartition = cachePartition
        };

    public EvidenceDescriptor? TryGetDescriptor(string evidenceId, string cachePartition)
    {
        var root = RootFor(cachePartition);
        if (!File.Exists(Path.Combine(root, "delivery.db")))
            return null;
        using var connection = OpenDatabase(root);
        using var transaction = connection.BeginTransaction(deferred: false);
        var descriptor = LoadDescriptor(connection, transaction, SanitizeId(evidenceId));
        if (descriptor is null || !File.Exists(Path.Combine(root, $"{SanitizeId(evidenceId)}.bin")))
            return null;
        var now = DateTimeOffset.UtcNow;
        var refreshed = descriptor with
        {
            CachePartition = cachePartition,
            LocalPath = Path.Combine(root, $"{SanitizeId(evidenceId)}.bin"),
            PinnedUntilUtc = now + MinRetentionWindow
        };
        SaveDescriptor(connection, transaction, refreshed, now);
        transaction.Commit();
        return refreshed;
    }

    private static EvidenceDescriptor? LoadDescriptor(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT descriptor FROM entries WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<EvidenceDescriptor>(json, DescriptorJsonOptions) : null;
    }

    private static void SaveDescriptor(SqliteConnection connection, SqliteTransaction transaction,
        EvidenceDescriptor descriptor, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO entries (id, descriptor, last_access, pinned_until, size_bytes)
            VALUES (@id, @descriptor, @access, @pin, @size)
            """;
        command.Parameters.AddWithValue("@id", descriptor.EvidenceId);
        command.Parameters.AddWithValue("@descriptor", JsonSerializer.Serialize(descriptor, DescriptorJsonOptions));
        command.Parameters.AddWithValue("@access", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@pin", descriptor.PinnedUntilUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("@size", descriptor.Bytes);
        command.ExecuteNonQuery();
    }

    public EvidenceReadResult Read(
        string evidenceId,
        long offsetBytes,
        long lengthBytes,
        string encoding,
        string cachePartition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offsetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        var descriptor = TryGetDescriptor(evidenceId, cachePartition)
            ?? throw new EvidenceNotFoundException(evidenceId);

        var dataPath = descriptor.LocalPath;
        if (!File.Exists(dataPath))
            throw new EvidenceNotFoundException(evidenceId);

        using var stream = File.OpenRead(dataPath);
        var totalBytes = stream.Length;
        var effectiveEncoding = ResolveEncoding(encoding, descriptor.MediaType);

        if (effectiveEncoding == "auto-binary")
        {
            // Binary auto mode offers a delivery/recovery action rather than an unsolicited whole
            // base64 dump; the caller must explicitly request encoding="base64" to read bytes.
            return new EvidenceReadResult
            {
                EvidenceId = evidenceId,
                Encoding = "auto",
                Text = null,
                Data = null,
                OffsetBytes = offsetBytes,
                ReturnedBytes = 0,
                TotalBytes = totalBytes,
                Sha256 = descriptor.Sha256,
                SourceComplete = descriptor.Complete,
                RangeComplete = false,
                Next = new EvidenceContinuation
                {
                    Arguments = new Dictionary<string, object?>
                    {
                        ["evidenceId"] = evidenceId,
                        ["offsetBytes"] = offsetBytes,
                        ["lengthBytes"] = lengthBytes,
                        ["encoding"] = "base64"
                    }
                },
                Delivery = descriptor
            };
        }

        if (offsetBytes >= totalBytes)
        {
            return BuildEmptyResult(descriptor, offsetBytes, totalBytes, effectiveEncoding);
        }

        var remaining = totalBytes - offsetBytes;
        var toRead = (int)Math.Min(lengthBytes, remaining);

        if (effectiveEncoding == "utf8")
        {
            // Advance by whole UTF-8 scalars: decode forward from offsetBytes, accumulating
            // complete scalars until at least lengthBytes have been consumed, then include the
            // complete final scalar even if it extends past the requested length. This never
            // splits a scalar and never truncates a too-small request into a replacement character.
            //
            // Cap to `remaining` (a real, file-size-bounded value) before adding the 4-byte lookahead
            // slack — adding 4 directly to a caller-supplied lengthBytes near long.MaxValue would
            // overflow to a negative number, truncating `window` to zero/garbage and silently
            // returning zero bytes with a next.offset that never advances.
            var cappedLength = Math.Min(lengthBytes, remaining);
            var window = (int)Math.Min(remaining, cappedLength + 4);
            stream.Position = offsetBytes;
            var probe = new byte[window];
            var probeRead = stream.ReadAtLeast(probe, window, throwOnEndOfStream: false);
            var consumed = 0;
            var i = 0;
            while (i < probeRead)
            {
                var seqLen = Utf8SequenceLength(probe[i]);
                if (seqLen <= 0 || i + seqLen > probeRead)
                    break; // malformed lead byte or sequence truncated at the read window's edge
                if (consumed >= lengthBytes)
                    break;
                consumed += seqLen;
                i += seqLen;
            }
            toRead = consumed > 0 ? consumed : Math.Min(probeRead, toRead);
        }

        stream.Position = offsetBytes;
        var buffer = new byte[toRead];
        var read = stream.ReadAtLeast(buffer, toRead, throwOnEndOfStream: false);
        var chunk = buffer.AsSpan(0, read).ToArray();

        var rangeComplete = (offsetBytes + read) >= totalBytes || read >= lengthBytes;
        var nextOffset = offsetBytes + read;
        var hasNext = nextOffset < totalBytes;

        return new EvidenceReadResult
        {
            EvidenceId = evidenceId,
            Encoding = effectiveEncoding,
            Text = effectiveEncoding == "utf8" ? Encoding.UTF8.GetString(chunk) : null,
            Data = effectiveEncoding == "base64" ? Convert.ToBase64String(chunk) : null,
            OffsetBytes = offsetBytes,
            ReturnedBytes = read,
            TotalBytes = totalBytes,
            Sha256 = descriptor.Sha256,
            SourceComplete = descriptor.Complete,
            RangeComplete = rangeComplete,
            Next = hasNext
                ? new EvidenceContinuation
                {
                    Arguments = new Dictionary<string, object?>
                    {
                        ["evidenceId"] = evidenceId,
                        ["offsetBytes"] = nextOffset,
                        ["lengthBytes"] = lengthBytes,
                        ["encoding"] = encoding
                    }
                }
                : null,
            Delivery = descriptor
        };
    }

    private static EvidenceReadResult BuildEmptyResult(EvidenceDescriptor descriptor, long offsetBytes, long totalBytes, string effectiveEncoding) => new()
    {
        EvidenceId = descriptor.EvidenceId,
        Encoding = effectiveEncoding,
        Text = effectiveEncoding == "utf8" ? "" : null,
        Data = effectiveEncoding == "base64" ? "" : null,
        OffsetBytes = offsetBytes,
        ReturnedBytes = 0,
        TotalBytes = totalBytes,
        Sha256 = descriptor.Sha256,
        SourceComplete = descriptor.Complete,
        RangeComplete = true,
        Next = null,
        Delivery = descriptor
    };

    private static string ResolveEncoding(string requested, string mediaType) => requested switch
    {
        "utf8" => "utf8",
        "base64" => "base64",
        _ => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
             || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            ? "utf8"
            : "auto-binary"
    };

    private static string SanitizeId(string evidenceId) => CacheSecurity.SanitizeCacheKeySegment(evidenceId);

    /// <summary>UTF-8 lead-byte sequence length (1-4), or -1 for a continuation/invalid lead byte.</summary>
    private static int Utf8SequenceLength(byte leadByte)
    {
        if ((leadByte & 0b1000_0000) == 0) return 1;
        if ((leadByte & 0b1110_0000) == 0b1100_0000) return 2;
        if ((leadByte & 0b1111_0000) == 0b1110_0000) return 3;
        if ((leadByte & 0b1111_1000) == 0b1111_0000) return 4;
        return -1;
    }

    /// <summary>Content-addressed evidence id: SHA-256 over the bytes plus the claimed media type (so identically-shaped bytes under two different declared media types never collide), hex-encoded.</summary>
    private static string ComputeContentAddressedId(ReadOnlySpan<byte> content, string mediaType)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(content);
        hasher.AppendData(Encoding.UTF8.GetBytes(mediaType ?? ""));
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void WriteAndVerify(string dataPath, ReadOnlySpan<byte> content, string expectedSha256)
    {
        var pendingPath = $"{dataPath}.{Guid.NewGuid():N}.pending";
        try
        {
            using (var stream = OpenPrivateFile(pendingPath, FileMode.CreateNew))
                stream.Write(content);
            using (var stream = File.OpenRead(pendingPath))
            {
                if (stream.Length != content.Length ||
                    !Convert.ToHexString(SHA256.HashData(stream)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Evidence write verification failed.");
            }
            File.Move(pendingPath, dataPath, overwrite: true);
        }
        finally
        {
            File.Delete(pendingPath);
        }
    }

    private void EnforceRetention(string root)
    {
        if (!Directory.Exists(root))
            return;
        using var connection = OpenDatabase(root);
        using var transaction = connection.BeginTransaction(deferred: false);
        EnforceRetentionCore(root, connection, transaction);
        transaction.Commit();
    }

    private void EnforceRetentionCore(string root, SqliteConnection connection, SqliteTransaction transaction)
    {
        var entries = new List<(string Id, long Size, long LastAccess)>();
        long totalBytes = 0;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, size_bytes, last_access FROM entries ORDER BY last_access";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                entries.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
                totalBytes += reader.GetInt64(1);
            }
        }
        RetentionSnapshotCapturedForTesting?.Invoke();
        var now = DateTimeOffset.UtcNow;
        var cutoff = _cacheOptions.ArtifactMaxAge > TimeSpan.Zero
            ? now.Subtract(_cacheOptions.ArtifactMaxAge).ToUnixTimeMilliseconds() : long.MinValue;
        foreach (var entry in entries)
        {
            if (entry.LastAccess >= cutoff && totalBytes <= DeliveryCapBytes)
                continue;
            // Re-read the authoritative pin under this same immediate transaction, never from
            // an earlier filesystem snapshot. Other processes cannot renew/publish mid-delete.
            command.CommandText = "SELECT pinned_until FROM entries WHERE id = @id";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@id", entry.Id);
            if (command.ExecuteScalar() is not long pin || pin > now.ToUnixTimeMilliseconds())
                continue;
            try
            {
                File.Delete(Path.Combine(root, $"{SanitizeId(entry.Id)}.bin"));
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            command.CommandText = "DELETE FROM entries WHERE id = @id";
            command.ExecuteNonQuery();
            totalBytes -= entry.Size;
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT COALESCE(SUM(size_bytes), 0), COUNT(*) FROM entries WHERE pinned_until > @now";
        command.Parameters.AddWithValue("@now", now.ToUnixTimeMilliseconds());
        long pinnedBytes;
        int pinnedCount;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            pinnedBytes = reader.GetInt64(0);
            pinnedCount = reader.GetInt32(1);
        }
        var status = new RetentionStatus
        {
            LastSweepUtc = now, TotalBytes = totalBytes, CapBytes = DeliveryCapBytes,
            CapExceeded = totalBytes > DeliveryCapBytes, PinnedBytes = pinnedBytes, PinnedEntryCount = pinnedCount,
            Message = totalBytes > DeliveryCapBytes
                ? $"Delivery soft cap of {DeliveryCapBytes} bytes exceeded ({totalBytes} bytes). " +
                  $"{pinnedCount} pinned entries ({pinnedBytes} bytes) remain protected; pinned evidence is never evicted. " +
                  "Unpinned files that could not be deleted will be retried on a later sweep."
                : null
        };
        command.Parameters.Clear();
        command.CommandText = "INSERT OR REPLACE INTO retention_status (singleton, json) VALUES (1, @json)";
        command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(status, DescriptorJsonOptions));
        command.ExecuteNonQuery();
    }

    /// <summary>Last sweep's size accounting and soft-cap overage, including when caching is disabled.</summary>
    public RetentionStatus? TryGetRetentionStatus(string cachePartition)
    {
        var root = RootFor(cachePartition);
        if (!File.Exists(Path.Combine(root, "delivery.db")))
            return null;
        using var connection = OpenDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM retention_status WHERE singleton = 1";
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<RetentionStatus>(json, DescriptorJsonOptions) : null;
    }
}
