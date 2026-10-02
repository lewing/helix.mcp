using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;

namespace HelixTool.Core.AzDO;

public sealed class CachingAzdoAcquisitionFailureRecorder : IAzdoAcquisitionFailureRecorder
{
    private static readonly TimeSpan ImmutableTtl = TimeSpan.FromHours(4);

    private readonly ICacheStore _cache;
    private readonly CacheOptions _options;

    public CachingAzdoAcquisitionFailureRecorder(ICacheStore cache, CacheOptions options)
    {
        _cache = cache;
        _options = options;
    }

    public async Task RecordBuildLogFailureAsync(
        string org,
        string project,
        int buildId,
        int logId,
        AcquisitionError error,
        CancellationToken ct = default)
    {
        if (!AcquisitionFailureRecorderPolicy.IsRecordable(error))
            return;

        var key = AzdoCacheKeys.MetadataKey(_options, org, project, $"log:{buildId}:{logId}");
        await _cache.SetAcquisitionErrorAsync(key, error, ImmutableTtl, ct);
    }
}

internal static class AcquisitionFailureRecorderPolicy
{
    public static bool IsRecordable(AcquisitionError error)
        => error.Kind is AcquisitionErrorKind.NotFound
            or AcquisitionErrorKind.AccessDenied
            or AcquisitionErrorKind.InvalidResponse;

    public static AcquisitionError ReplayFromSnapshot(AcquisitionError error)
        => error with
        {
            Source = "snapshot",
            Replayed = true,
            RecordedAt = error.RecordedAt
        };
}
