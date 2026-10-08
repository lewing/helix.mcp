using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Cache;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Disk-backed <see cref="IEvidenceDeliveryStore"/>. Entries live under
/// <c>{EffectiveCacheRoot}/delivery/{cachePartition}/{evidenceId}.bin</c> plus a sidecar
/// <c>.meta.json</c> descriptor, so repeated reads across requests resolve the same id without
/// re-materializing the content. This directory sits outside read-only snapshot exports: derived
/// views are always reconstructable from the backing cache key, so they add no new snapshot key.
/// <para>
/// <see cref="EvidenceDescriptor.EvidenceId"/>s are content-addressed (a SHA-256 of the bytes plus media type, not a
/// random GUID): identical payloads materialized more than once under the same partition —
/// a common case, since the same oversized timeline page or record can be requested again,
/// retried, or paged alongside a sibling continuation — reuse the same on-disk entry instead of
/// growing the delivery directory without bound.
/// </para>
/// <para>
/// Retention is bounded and runs opportunistically after each <see cref="PutBytes"/>/
/// <see cref="PutText"/>, derived from the same <see cref="CacheOptions.MaxSizeBytes"/>/
/// <see cref="CacheOptions.ArtifactMaxAge"/> configuration that already governs the primary
/// SQLite-backed artifact cache, so one retention policy covers both: entries older than
/// <see cref="CacheOptions.ArtifactMaxAge"/> (by last access) are deleted outright, then any
/// remaining entries are evicted oldest-access-first until the partition's total bytes are back
/// under <see cref="CacheOptions.MaxSizeBytes"/>. A delete that fails because the file is open is
/// treated as "an in-flight <c>hlx_read_evidence</c> call may be reading it" and is skipped rather
/// than retried in the same sweep (best-effort: Windows actually enforces this via its share-mode
/// locks; POSIX allows unlinking an open file, so this protection does not apply on Linux/macOS).
/// </para>
/// </summary>
public sealed class FileEvidenceDeliveryStore(CacheOptions cacheOptions) : IEvidenceDeliveryStore
{
    private readonly CacheOptions _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));

    private string RootFor(string cachePartition)
    {
        // Runtime derived files are intentionally outside any immutable snapshot export. In eval
        // mode GetEffectiveCacheRoot() returns the snapshot directory itself, so delivery uses a
        // genuinely separate location instead of nesting inside the read-only snapshot — a private,
        // per-user runtime directory rather than a shared, predictable OS-temp path with default
        // permissions (see ResolveDeliveryRuntimeRoot).
        var baseRoot = _cacheOptions.EvalMode
            ? ResolveDeliveryRuntimeRoot()
            : _cacheOptions.GetEffectiveCacheRoot();
        return Path.Combine(baseRoot, "delivery", CacheSecurity.SanitizeCacheKeySegment(cachePartition));
    }

    /// <summary>
    /// Resolves a private, owner-only eval-mode runtime delivery root instead of a fixed,
    /// world-guessable <c>{temp}/hlx-delivery-runtime</c> path: on Unix, prefers
    /// <c>XDG_RUNTIME_DIR</c> (already a per-user, OS-managed 0700 directory) and otherwise falls
    /// back to a per-user-named directory under the OS temp root (so two different OS users never
    /// contend for, or can plant a symlink into, the same path); on Windows, uses
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which is already per-user.
    /// Every directory level this method itself creates is additionally hardened via
    /// <see cref="EnsureSecurePrivateDir"/>.
    /// </summary>
    private static string ResolveDeliveryRuntimeRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hlxDir = EnsureSecurePrivateDir(Path.Combine(localAppData, "hlx"));
            return EnsureSecurePrivateDir(Path.Combine(hlxDir, "delivery-runtime"));
        }

        var xdgRuntimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrEmpty(xdgRuntimeDir) && Directory.Exists(xdgRuntimeDir))
            return EnsureSecurePrivateDir(Path.Combine(xdgRuntimeDir, "hlx-delivery-runtime"));

        // No XDG_RUNTIME_DIR (common outside an interactive systemd session, e.g. CI/cron/SSH):
        // fall back to the shared OS temp dir, but scope to a directory named for this user, and
        // enforce owner-only permissions at both levels rather than relying on the temp dir's
        // default (often world-traversable) permissions.
        var userTag = CacheSecurity.SanitizeCacheKeySegment(Environment.UserName);
        var userDir = EnsureSecurePrivateDir(Path.Combine(Path.GetTempPath(), $"hlx-delivery-{userTag}"));
        return EnsureSecurePrivateDir(Path.Combine(userDir, "hlx-delivery-runtime"));
    }

    /// <summary>
    /// Ensures <paramref name="path"/> is a private, owner-only directory before any delivery
    /// content is written beneath it. Rejects a symlinked directory entry outright — at a
    /// predictable eval-mode temp path, an attacker who can plant a symlink first could otherwise
    /// redirect delivery writes anywhere else on disk the current user can write to. Rejects a
    /// pre-existing directory whose permissions are not exactly owner-only (0700 on Unix): the BCL
    /// has no direct "stat owner uid" API, so owner-only permission bits are used as the portable
    /// proxy for "created and still controlled by this user" — anyone else who could have created
    /// or altered the directory would need a deliberately narrowed (non-default) umask to pass this
    /// check, which is the same bar a legitimate prior run of this user's own process would pass.
    /// </summary>
    private static string EnsureSecurePrivateDir(string path)
    {
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
            {
                throw new InvalidOperationException(
                    $"Refusing to use delivery runtime directory '{path}': it is a symbolic link or reparse point, not a plain directory. Remove it and retry.");
            }

            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                if (mode != ownerOnly)
                {
                    throw new InvalidOperationException(
                        $"Refusing to use delivery runtime directory '{path}': it already exists with permissions '{mode}' instead of owner-only (0700), so it may not be owned/controlled by this user. Remove it (or chmod 700 it) and retry.");
                }
            }

            return path;
        }

        if (File.Exists(path))
            throw new InvalidOperationException($"Refusing to use delivery runtime directory '{path}': a file already exists at that path.");

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public EvidenceDescriptor PutText(
        string content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition)
    {
        ArgumentNullException.ThrowIfNull(content);
        return PutBytes(Encoding.UTF8.GetBytes(content), mediaType, sourceFingerprint, backingCacheKey, cachePartition);
    }

    public EvidenceDescriptor PutBytes(
        ReadOnlySpan<byte> content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition)
    {
        var root = RootFor(cachePartition);
        Directory.CreateDirectory(root);

        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        // Content-addressed id (hash of bytes + media type, not a random GUID): identical payloads
        // materialized again under this partition resolve to the same id/file instead of growing
        // the delivery directory without bound.
        var evidenceId = ComputeContentAddressedId(content, mediaType);
        var dataPath = Path.Combine(root, $"{evidenceId}.bin");
        var metaPath = Path.Combine(root, $"{evidenceId}.meta.json");

        bool complete;
        if (File.Exists(dataPath) && File.Exists(metaPath) && new FileInfo(dataPath).Length == content.Length)
        {
            // Dedupe: bytes already materialized (same content hash + length) — skip rewriting them
            // and just refresh last-access for retention purposes.
            complete = true;
            File.SetLastAccessTimeUtc(dataPath, DateTime.UtcNow);
        }
        else
        {
            complete = WriteAndVerify(dataPath, content, sha256);
        }

        var descriptor = new EvidenceDescriptor
        {
            EvidenceId = evidenceId,
            ResourceUri = $"hlx-evidence://{cachePartition}/{evidenceId}",
            LocalPath = dataPath,
            MediaType = mediaType,
            Encoding = "binary",
            Bytes = content.Length,
            Sha256 = sha256,
            Complete = complete,
            SourceFingerprint = sourceFingerprint,
            BackingCacheKey = backingCacheKey,
            CapturedAt = DateTimeOffset.UtcNow,
            CachePartition = cachePartition
        };

        File.WriteAllText(metaPath, JsonSerializer.Serialize(descriptor, DescriptorJsonOptions));
        EnforceRetention(root);
        return descriptor;
    }

    public EvidenceDescriptor? TryGetDescriptor(string evidenceId, string cachePartition)
    {
        var metaPath = Path.Combine(RootFor(cachePartition), $"{SanitizeId(evidenceId)}.meta.json");
        if (!File.Exists(metaPath))
            return null;
        return JsonSerializer.Deserialize<EvidenceDescriptor>(File.ReadAllText(metaPath), DescriptorJsonOptions);
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

        // Mark as recently accessed before opening, so a concurrent retention sweep (from another
        // Put on this same partition) treats this entry as fresh rather than an eviction candidate.
        try { File.SetLastAccessTimeUtc(dataPath, DateTime.UtcNow); } catch (IOException) { /* best-effort */ }

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

    /// <summary>Writes content to <paramref name="dataPath"/> and re-reads/re-hashes it to verify the write before declaring <c>complete=true</c>, per the exact-recovery contract.</summary>
    private static bool WriteAndVerify(string dataPath, ReadOnlySpan<byte> content, string expectedSha256)
    {
        File.WriteAllBytes(dataPath, content.ToArray());
        var verifyBytes = File.ReadAllBytes(dataPath);
        var verifySha = Convert.ToHexString(SHA256.HashData(verifyBytes)).ToLowerInvariant();
        return verifyBytes.Length == content.Length && string.Equals(verifySha, expectedSha256, StringComparison.Ordinal);
    }

    /// <summary>
    /// Bounded age + total-size retention sweep for one partition's delivery directory (see the
    /// class remarks for the overall policy). Runs synchronously and opportunistically after each
    /// <see cref="PutBytes"/>; a failed delete (most likely an in-flight <see cref="Read"/> holding
    /// the file open) is treated as "skip this entry, it is still wanted" rather than an error.
    /// </summary>
    private void EnforceRetention(string root)
    {
        if (_cacheOptions.MaxSizeBytes <= 0)
            return;

        List<(string DataPath, string MetaPath, long Size, DateTime LastAccessUtc)> entries;
        try
        {
            entries = [.. Directory.EnumerateFiles(root, "*.bin")
                .Select(dataPath =>
                {
                    var info = new FileInfo(dataPath);
                    var metaPath = Path.ChangeExtension(dataPath, null) + ".meta.json";
                    return (DataPath: dataPath, MetaPath: metaPath, Size: info.Exists ? info.Length : 0,
                        LastAccessUtc: info.Exists ? info.LastAccessTimeUtc : DateTime.MinValue);
                })];
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        bool TryDelete((string DataPath, string MetaPath, long Size, DateTime LastAccessUtc) entry)
        {
            try
            {
                if (File.Exists(entry.DataPath)) File.Delete(entry.DataPath);
                if (File.Exists(entry.MetaPath)) File.Delete(entry.MetaPath);
                return true;
            }
            catch (IOException)
            {
                // Most likely open for an in-flight hlx_read_evidence read — leave it for a later
                // sweep rather than starving that read (best-effort: this protection relies on
                // Windows share-mode locking and is not enforced on POSIX).
                return false;
            }
        }

        var now = DateTime.UtcNow;
        var maxAge = _cacheOptions.ArtifactMaxAge;
        var survivors = new List<(string DataPath, string MetaPath, long Size, DateTime LastAccessUtc)>();
        foreach (var entry in entries)
        {
            if (maxAge > TimeSpan.Zero && now - entry.LastAccessUtc > maxAge && TryDelete(entry))
                continue;
            survivors.Add(entry);
        }

        var totalSize = survivors.Sum(e => e.Size);
        if (totalSize <= _cacheOptions.MaxSizeBytes)
            return;

        foreach (var entry in survivors.OrderBy(e => e.LastAccessUtc))
        {
            if (totalSize <= _cacheOptions.MaxSizeBytes)
                break;
            if (TryDelete(entry))
                totalSize -= entry.Size;
        }
    }

    private static readonly JsonSerializerOptions DescriptorJsonOptions = new() { PropertyNameCaseInsensitive = true };
}
