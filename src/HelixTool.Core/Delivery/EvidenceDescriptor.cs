using System.Text.Json.Serialization;

namespace HelixTool.Core.Delivery;

/// <summary>
/// Descriptor for a payload materialized into the private runtime delivery directory because it
/// did not fit an inline shaping target. Not a parallel snapshot/bundle format: the backing data
/// is always reconstructable from the existing complete cache entry named by
/// <see cref="BackingCacheKey"/>, and this descriptor is scoped to the credential/cache partition
/// that produced it (see <see cref="CachePartition"/>).
/// </summary>
public sealed record EvidenceDescriptor
{
    [JsonPropertyName("evidenceId")]
    public required string EvidenceId { get; init; }

    /// <summary>hlx-owned evidence reference, resolved only through <see cref="IEvidenceDeliveryStore"/>; not an unauthenticated HTTP URL.</summary>
    [JsonPropertyName("resourceUri")]
    public required string ResourceUri { get; init; }

    [JsonPropertyName("localPath")]
    public required string LocalPath { get; init; }

    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    [JsonPropertyName("encoding")]
    public required string Encoding { get; init; }

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>True only after all requested bytes were actually written and verified.</summary>
    [JsonPropertyName("complete")]
    public required bool Complete { get; init; }

    [JsonPropertyName("sourceFingerprint")]
    public string? SourceFingerprint { get; init; }

    [JsonPropertyName("backingCacheKey")]
    public string? BackingCacheKey { get; init; }

    [JsonPropertyName("capturedAt")]
    public required DateTimeOffset CapturedAt { get; init; }

    [JsonPropertyName("freshness")]
    public string Freshness { get; init; } = "snapshot";

    /// <summary>Credential/cache partition this descriptor was produced under; scopes <see cref="IEvidenceDeliveryStore.Read"/> access.</summary>
    [JsonIgnore]
    public string CachePartition { get; init; } = "public";
}

/// <summary>Result of a bounded read against a materialized evidence reference.</summary>
public sealed record EvidenceReadResult
{
    [JsonPropertyName("evidenceId")]
    public required string EvidenceId { get; init; }

    [JsonPropertyName("encoding")]
    public required string Encoding { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; init; }

    [JsonPropertyName("offsetBytes")]
    public required long OffsetBytes { get; init; }

    [JsonPropertyName("returnedBytes")]
    public required long ReturnedBytes { get; init; }

    [JsonPropertyName("totalBytes")]
    public required long TotalBytes { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }

    /// <summary>Whole backing source is present (collection completeness), independent of this one read's range.</summary>
    [JsonPropertyName("sourceComplete")]
    public required bool SourceComplete { get; init; }

    /// <summary>This specific requested range was fully returned (no further chunk needed to satisfy it).</summary>
    [JsonPropertyName("rangeComplete")]
    public required bool RangeComplete { get; init; }

    [JsonPropertyName("next")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public EvidenceContinuation? Next { get; init; }

    [JsonPropertyName("delivery")]
    public required EvidenceDescriptor Delivery { get; init; }
}

/// <summary>Exact next <c>hlx_read_evidence</c> invocation to continue a chunked read.</summary>
public sealed record EvidenceContinuation
{
    [JsonPropertyName("tool")]
    public string Tool { get; init; } = "hlx_read_evidence";

    [JsonPropertyName("arguments")]
    public required IReadOnlyDictionary<string, object?> Arguments { get; init; }
}
