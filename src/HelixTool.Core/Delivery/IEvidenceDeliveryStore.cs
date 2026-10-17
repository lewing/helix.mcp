namespace HelixTool.Core.Delivery;

/// <summary>
/// Scoped store for materializing oversized response payloads into the per-user cache delivery
/// directory and reading them back in bounded text/base64 chunks. This is not an arbitrary
/// filesystem reader: reads are scoped to the credential/cache partition that produced the
/// descriptor, and every entry is reconstructable from the existing complete backing cache key.
/// </summary>
public interface IEvidenceDeliveryStore
{
    /// <summary>Stream a payload to private disk without whole-payload memory buffering.</summary>
    Task<EvidenceDescriptor> PutStreamAsync(Stream content, string mediaType, string? sourceFingerprint,
        string? backingCacheKey, string cachePartition, CancellationToken ct = default)
        => throw new NotSupportedException("This delivery store does not support streaming.");

    /// <summary>Create an owned private seekable spool outside immutable snapshots.</summary>
    FileStream CreateSpool(string cachePartition)
        => throw new NotSupportedException("This delivery store does not support spooling.");
    /// <summary>Open an owned persistent prefix spool for checkpoint/restart, never an exported complete blob.</summary>
    FileStream CreateTransferSpool(string cachePartition, string transferId) => CreateSpool(cachePartition);

    /// <summary>
    /// Materialize UTF-8 text content as a new evidence entry. Returns the complete descriptor;
    /// <see cref="EvidenceDescriptor.Complete"/> is true once the write is verified.
    /// </summary>
    EvidenceDescriptor PutText(
        string content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition);

    /// <summary>Materialize binary content as a new evidence entry.</summary>
    EvidenceDescriptor PutBytes(
        ReadOnlySpan<byte> content,
        string mediaType,
        string? sourceFingerprint,
        string? backingCacheKey,
        string cachePartition);

    /// <summary>
    /// Read a bounded range of a previously materialized evidence entry. <paramref name="encoding"/>
    /// is <c>auto</c>/<c>utf8</c>/<c>base64</c>; auto resolves to utf8 for text media types and
    /// base64 otherwise. Throws <see cref="EvidenceNotFoundException"/> when the id is unknown or
    /// scoped to a different cache partition.
    /// </summary>
    EvidenceReadResult Read(
        string evidenceId,
        long offsetBytes,
        long lengthBytes,
        string encoding,
        string cachePartition);

    /// <summary>Look up a previously issued descriptor without reading its content.</summary>
    EvidenceDescriptor? TryGetDescriptor(string evidenceId, string cachePartition);
}

/// <summary>Thrown when an evidenceId is unknown, expired, or outside the caller's cache partition.</summary>
public sealed class EvidenceNotFoundException(string evidenceId) : Exception(
    $"Evidence reference '{evidenceId}' was not found in this credential/cache partition. " +
    "It may have come from a different auth context, or the cache delivery directory was cleared. " +
    "Re-run the originating tool call to regenerate it.")
{
    public string EvidenceId { get; } = evidenceId;
}
