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
/// </summary>
public sealed class FileEvidenceDeliveryStore(CacheOptions cacheOptions) : IEvidenceDeliveryStore
{
    private readonly CacheOptions _cacheOptions = cacheOptions ?? throw new ArgumentNullException(nameof(cacheOptions));

    private string RootFor(string cachePartition)
    {
        // Runtime derived files are intentionally outside any immutable snapshot export. In eval
        // mode GetEffectiveCacheRoot() returns the snapshot directory itself, so delivery uses a
        // genuinely separate OS temp location instead of nesting inside the read-only snapshot.
        var baseRoot = _cacheOptions.EvalMode
            ? Path.Combine(Path.GetTempPath(), "hlx-delivery-runtime")
            : _cacheOptions.GetEffectiveCacheRoot();
        return Path.Combine(baseRoot, "delivery", CacheSecurity.SanitizeCacheKeySegment(cachePartition));
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

        var evidenceId = Guid.NewGuid().ToString("n");
        var dataPath = Path.Combine(root, $"{evidenceId}.bin");
        var metaPath = Path.Combine(root, $"{evidenceId}.meta.json");

        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        File.WriteAllBytes(dataPath, content.ToArray());
        // Verify the write before declaring complete=true, per the exact-recovery contract.
        var verifyBytes = File.ReadAllBytes(dataPath);
        var verifySha = Convert.ToHexString(SHA256.HashData(verifyBytes)).ToLowerInvariant();
        var complete = verifyBytes.Length == content.Length && string.Equals(verifySha, sha256, StringComparison.Ordinal);

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
            var window = (int)Math.Min(remaining, lengthBytes + 4);
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

    private static readonly JsonSerializerOptions DescriptorJsonOptions = new() { PropertyNameCaseInsensitive = true };
}
