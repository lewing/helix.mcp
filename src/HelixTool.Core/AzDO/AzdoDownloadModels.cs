using HelixTool.Core.Acquisition;
using HelixTool.Core.Delivery;
using System.Text.Json.Serialization;

namespace HelixTool.Core.AzDO;

public sealed record AzdoDownloadOptions
{
    public string? ArtifactName { get; init; }
    public int? AttachmentId { get; init; }
    public string? Pattern { get; init; }
    public string? MemberPath { get; init; }
    public string? MemberPattern { get; init; }
    public int MemberOffset { get; init; }
    public int MemberLimit { get; init; } = 10;
    public string ContentKind { get; init; } = "auto";
    public int Offset { get; init; }
    public int Limit { get; init; } = 10;
    public string? ViewId { get; init; }
    public long? MaxFileBytes { get; init; }
    public long? MaxArchiveBytes { get; init; }
    public long? MaxTotalBytes { get; init; }
    public long? MaxTotalStreamBytes { get; init; }
    public bool AllowLarge { get; init; }
    public bool All { get; init; }
    public bool AllMembers { get; init; }
    public string Delivery { get; init; } = "auto";
    public bool Resume { get; init; } = true;
    public long MaxResponseBytes { get; init; } = 12288;
    public string? OutputDir { get; init; }
    public bool Overwrite { get; init; }
}

public sealed record AzdoByteSource
{
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("transferMode")]
    public required string TransferMode { get; init; }
    [JsonPropertyName("sourceBytes")]
    public long SourceBytes { get; init; }
    [JsonPropertyName("sourceComplete")]
    public bool SourceComplete { get; init; }
    [JsonPropertyName("archiveBytes")]
    public long? ArchiveBytes { get; init; }
    [JsonPropertyName("archiveSha256")]
    public string? ArchiveSha256 { get; init; }
    [JsonPropertyName("catalogFingerprint")]
    public string? CatalogFingerprint { get; init; }
    [JsonPropertyName("providerIdentity")]
    public string? ProviderIdentity { get; init; }
    [JsonPropertyName("resumeMode")]
    public string ResumeMode { get; init; } = "verified-files; current upstream file restarts without Range";
}

public sealed record AzdoMemberCatalogEntry(string MemberPath, long Bytes, uint Crc32);
public sealed record AzdoMemberCatalog(string Fingerprint, IReadOnlyList<AzdoMemberCatalogEntry> Members, AzdoByteSource Source);

public sealed record AzdoByteDescriptor
{
    public int SchemaVersion { get; init; } = 1;
    public string Provider { get; init; } = "azdo";
    public required string Resource { get; init; }
    public required string Name { get; init; }
    public string? MemberPath { get; init; }
    public required string Format { get; init; }
    public required string ContentType { get; init; }
    public required string ContentKind { get; init; }
    public long Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required AzdoByteSource Source { get; init; }
    public DateTimeOffset DownloadedAt { get; init; }
}

public sealed record AzdoDownloadedMember
{
    [JsonPropertyName("memberPath")]
    public required string MemberPath { get; init; }
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "ok";
    [JsonPropertyName("localPath")]
    public string? LocalPath { get; init; }
    [JsonPropertyName("cacheKey")]
    public required string CacheKey { get; init; }
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
    [JsonPropertyName("delivery")]
    public EvidenceDescriptor? Delivery { get; init; }
    [JsonPropertyName("error")]
    public AcquisitionError? Error { get; init; }
    [JsonPropertyName("skip")]
    public string? Skip { get; init; }
}

public sealed record AzdoDownloadResult
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = "azdo";
    [JsonPropertyName("buildId")]
    public int? BuildId { get; init; }
    [JsonPropertyName("runId")]
    public int? RunId { get; init; }
    [JsonPropertyName("resultId")]
    public int? ResultId { get; init; }
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
    [JsonPropertyName("artifactId")]
    public int? ArtifactId { get; init; }
    [JsonPropertyName("attachmentId")]
    public int? AttachmentId { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = "ok";
    [JsonPropertyName("localPath")]
    public string? LocalPath { get; init; }
    [JsonPropertyName("cacheKey")]
    public string? CacheKey { get; init; }
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }
    [JsonPropertyName("contentType")]
    public string? ContentType { get; init; }
    [JsonPropertyName("format")]
    public string? Format { get; init; }
    [JsonPropertyName("contentKind")]
    public string ContentKind { get; init; } = "auto";
    [JsonPropertyName("source")]
    public AzdoByteSource? Source { get; init; }
    [JsonPropertyName("error")]
    public AcquisitionError? Error { get; init; }
    [JsonPropertyName("skip")]
    public string? Skip { get; init; }
    [JsonPropertyName("delivery")]
    public EvidenceDescriptor? Delivery { get; init; }
    [JsonPropertyName("members")]
    public IReadOnlyList<AzdoDownloadedMember> Members { get; init; } = [];
    [JsonPropertyName("membersReturned")]
    public int MembersReturned { get; init; }
    [JsonPropertyName("membersTotal")]
    public int MembersTotal { get; init; }
    [JsonPropertyName("memberOffset")]
    public int MemberOffset { get; init; }
    [JsonPropertyName("memberLimit")]
    public int MemberLimit { get; init; }
    [JsonPropertyName("membersComplete")]
    public bool MembersComplete { get; init; } = true;
    [JsonPropertyName("membersTruncated")]
    public bool MembersTruncated { get; init; }
    [JsonPropertyName("memberNext")]
    public AzdoDownloadAction? MemberNext { get; init; }
}

public sealed record AzdoDownloadAction(
    [property: JsonPropertyName("tool")] string Tool,
    [property: JsonPropertyName("arguments")] IReadOnlyDictionary<string, object?> Arguments);

public sealed record AzdoDownloadReport
{
    [JsonPropertyName("maxResponseBytes")]
    public long MaxResponseBytes { get; init; } = 12288;
    [JsonPropertyName("requestedMaxResponseBytes")]
    public long RequestedMaxResponseBytes { get; init; } = 12288;
    [JsonPropertyName("ok")]
    public bool Ok { get; init; } = true;
    [JsonPropertyName("results")]
    public IReadOnlyList<AzdoDownloadResult> Results { get; init; } = [];
    [JsonPropertyName("returned")]
    public int Returned { get; init; }
    [JsonPropertyName("total")]
    public int Total { get; init; }
    [JsonPropertyName("offset")]
    public int Offset { get; init; }
    [JsonPropertyName("limit")]
    public int Limit { get; init; }
    [JsonPropertyName("complete")]
    public bool Complete { get; init; }
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
    [JsonPropertyName("next")]
    public AzdoDownloadAction? Next { get; init; }
    [JsonPropertyName("viewId")]
    public required string ViewId { get; init; }
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }
    [JsonPropertyName("streamedBytes")]
    public long StreamedBytes { get; init; }
    [JsonPropertyName("planning")]
    public object? Planning { get; init; }
    [JsonPropertyName("headroom")]
    public object? Headroom { get; init; }
    [JsonPropertyName("delivery")]
    public EvidenceDescriptor? Delivery { get; init; }
    [JsonPropertyName("continuation")]
    public AzdoDownloadAction? Continuation { get; init; }
    [JsonPropertyName("recovery")]
    public object? Recovery { get; init; }
    [JsonPropertyName("cache")]
    public object? Cache { get; init; }
    [JsonPropertyName("incompleteDetails")]
    public IReadOnlyList<object> IncompleteDetails { get; init; } = [];
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}
