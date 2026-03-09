using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Decorator that adds SQLite-backed caching to any <see cref="IAzdoApiClient"/>.
/// TTL varies by endpoint and build status:
///   - Completed builds: 4h, in-progress: 15s
///   - Timelines: never cached while build is running
///   - Logs/changes: 4h (immutable once written)
///   - Build lists: 30s (browsing queries)
///   - Test runs/results: 1h (stable after build)
/// Pass-through when <see cref="CacheOptions.MaxSizeBytes"/> is 0 (disabled).
/// </summary>
public sealed class CachingAzdoApiClient : IAzdoApiClient
{
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan InProgressTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ImmutableTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan TestTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan BuildStateTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BuildStateCompletedTtl = TimeSpan.FromHours(4);

    private readonly IAzdoApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly bool _enabled;

    public CachingAzdoApiClient(IAzdoApiClient inner, ICacheStore cache, CacheOptions options)
    {
        _inner = inner;
        _cache = cache;
        _enabled = options.MaxSizeBytes > 0;
    }

    public async Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildAsync(org, project, buildId, ct);

        var key = BuildCacheKey(org, project, $"build:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<AzdoBuild>(cached);

        var result = await _inner.GetBuildAsync(org, project, buildId, ct);
        if (result is null)
            return null;

        var isCompleted = IsCompletedStatus(result.Status);
        var stateKey = BuildStateKey(org, project, buildId);
        await _cache.SetJobCompletedAsync(stateKey, isCompleted,
            isCompleted ? BuildStateCompletedTtl : BuildStateTtl, ct);

        var ttl = isCompleted ? CompletedTtl : InProgressTtl;
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ttl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListBuildsAsync(org, project, filter, ct);

        var filterHash = HashFilter(filter);
        var key = BuildCacheKey(org, project, $"builds:{filterHash}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoBuild>>(cached) ?? [];

        var result = await _inner.ListBuildsAsync(org, project, filter, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ListTtl, ct);

        return result;
    }

    public async Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTimelineAsync(org, project, buildId, ct);

        // Never cache timeline while the build is running — it changes constantly
        var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
        if (!isCompleted)
            return await _inner.GetTimelineAsync(org, project, buildId, ct);

        var key = BuildCacheKey(org, project, $"timeline:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<AzdoTimeline>(cached);

        var result = await _inner.GetTimelineAsync(org, project, buildId, ct);
        if (result is not null)
            await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), CompletedTtl, ct);

        return result;
    }

    public async Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default)
    {
        if (!_enabled)
            return await _inner.GetBuildLogAsync(org, project, buildId, logId, startLine, endLine, ct);

        var contentKey = BuildCacheKey(org, project, $"log:{buildId}:{logId}");
        var freshKey = BuildCacheKey(org, project, $"log-fresh:{buildId}:{logId}");

        var cachedJson = await _cache.GetMetadataAsync(contentKey, ct);
        string? fullContent = cachedJson is not null
            ? JsonSerializer.Deserialize<string>(cachedJson)
            : null;

        if (fullContent is not null)
        {
            // Check freshness — stale means the 15s marker expired
            var isFresh = await _cache.GetMetadataAsync(freshKey, ct) is not null;

            if (!isFresh)
            {
                // Stale: delta-append instead of full re-download
                var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
                var cachedLineCount = CountLines(fullContent);
                var delta = await _inner.GetBuildLogAsync(
                    org, project, buildId, logId, startLine: cachedLineCount, ct: ct);

                if (!string.IsNullOrEmpty(delta))
                {
                    fullContent += delta;
                    await _cache.SetMetadataAsync(contentKey,
                        JsonSerializer.Serialize(fullContent), ImmutableTtl, ct);
                }

                if (!isCompleted)
                    await _cache.SetMetadataAsync(freshKey, "\"1\"", InProgressTtl, ct);
                else
                    await _cache.SetMetadataAsync(freshKey, "\"1\"", ImmutableTtl, ct);
            }

            // Serve full or range from (possibly refreshed) cached content
            if (startLine is null && endLine is null)
                return fullContent;

            return ExtractRange(fullContent, startLine, endLine);
        }

        // Not cached — range request with no cached full log: pass through, don't cache partial
        if (startLine is not null || endLine is not null)
            return await _inner.GetBuildLogAsync(org, project, buildId, logId, startLine, endLine, ct);

        // Full log first fetch
        var result = await _inner.GetBuildLogAsync(org, project, buildId, logId, ct: ct);
        if (result is null) return null;

        var completed = await IsBuildCompletedAsync(org, project, buildId, ct);
        await _cache.SetMetadataAsync(contentKey, JsonSerializer.Serialize(result), ImmutableTtl, ct);

        if (!completed)
            await _cache.SetMetadataAsync(freshKey, "\"1\"", InProgressTtl, ct);
        else
            await _cache.SetMetadataAsync(freshKey, "\"1\"", ImmutableTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildChangesAsync(org, project, buildId, top, ct);

        var key = BuildCacheKey(org, project, $"changes:{buildId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoBuildChange>>(cached) ?? [];

        var result = await _inner.GetBuildChangesAsync(org, project, buildId, top, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ImmutableTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestRunsAsync(org, project, buildId, top, ct);

        var key = BuildCacheKey(org, project, $"testruns:{buildId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoTestRun>>(cached) ?? [];

        var result = await _inner.GetTestRunsAsync(org, project, buildId, top, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestResultsAsync(org, project, runId, top, ct);

        var key = BuildCacheKey(org, project, $"testresults:{runId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoTestResult>>(cached) ?? [];

        var result = await _inner.GetTestResultsAsync(org, project, runId, top, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildArtifactsAsync(org, project, buildId, ct);

        var key = BuildCacheKey(org, project, $"artifacts:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoBuildArtifact>>(cached) ?? [];

        var result = await _inner.GetBuildArtifactsAsync(org, project, buildId, ct);
        // Artifacts are immutable once the build publishes them
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ImmutableTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestAttachmentsAsync(org, project, runId, resultId, top, ct);

        var key = BuildCacheKey(org, project, $"testattachments:{runId}:{resultId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoTestAttachment>>(cached) ?? [];

        var result = await _inner.GetTestAttachmentsAsync(org, project, runId, resultId, top, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildLogsListAsync(org, project, buildId, ct);

        var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
        var ttl = isCompleted ? CompletedTtl : InProgressTtl;
        var key = BuildCacheKey(org, project, $"logslist:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<AzdoBuildLogEntry>>(cached) ?? [];

        var result = await _inner.GetBuildLogsListAsync(org, project, buildId, ct);
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ttl, ct);

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string BuildCacheKey(string org, string project, string suffix)
    {
        var safeOrg = CacheSecurity.SanitizeCacheKeySegment(org);
        var safeProject = CacheSecurity.SanitizeCacheKeySegment(project);
        return $"azdo:{safeOrg}:{safeProject}:{suffix}";
    }

    private static string BuildStateKey(string org, string project, int buildId)
        => $"azdo-build:{CacheSecurity.SanitizeCacheKeySegment(org)}:{CacheSecurity.SanitizeCacheKeySegment(project)}:{buildId}";

    private static bool IsCompletedStatus(string? status)
        => status?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true;

    private async Task<bool> IsBuildCompletedAsync(string org, string project, int buildId, CancellationToken ct)
    {
        var stateKey = BuildStateKey(org, project, buildId);
        var cached = await _cache.IsJobCompletedAsync(stateKey, ct);
        if (cached.HasValue)
            return cached.Value;

        // Fetch the build to determine status (this call is itself cached)
        var build = await GetBuildAsync(org, project, buildId, ct);
        return build is not null && IsCompletedStatus(build.Status);
    }

    private static string HashFilter(AzdoBuildFilter filter)
    {
        var raw = $"{filter.PrNumber}|{filter.Branch}|{filter.DefinitionId}|{filter.Top}|{filter.StatusFilter}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    internal static int CountLines(string content)
    {
        if (string.IsNullOrEmpty(content)) return 0;
        var count = content.Split('\n').Length;
        if (content.EndsWith('\n')) count--;
        return count;
    }

    internal static string? ExtractRange(string content, int? startLine, int? endLine)
    {
        var lines = content.Split('\n');
        var start = startLine ?? 0;
        var end = endLine ?? (lines.Length - 1);

        if (start >= lines.Length || start < 0)
            return null;

        end = Math.Min(end, lines.Length - 1);
        if (end < start)
            return null;

        return string.Join('\n', lines[start..(end + 1)]);
    }
}
