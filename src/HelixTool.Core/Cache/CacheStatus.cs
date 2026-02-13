namespace HelixTool.Core;

/// <summary>Status summary returned by <see cref="ICacheStore.GetStatusAsync"/>.</summary>
public record CacheStatus(
    long TotalSizeBytes,
    int MetadataEntryCount,
    int ArtifactFileCount,
    DateTimeOffset? OldestEntry,
    DateTimeOffset? NewestEntry,
    long MaxSizeBytes);
