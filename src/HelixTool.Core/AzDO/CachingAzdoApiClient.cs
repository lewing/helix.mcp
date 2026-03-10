using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Cache;

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

    /// <summary>Prefix for plain-text cache entries to avoid JSON wrapping overhead.</summary>
    private const string RawTextPrefix = "\0raw\n";

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

        var cachedRaw = await _cache.GetMetadataAsync(contentKey, ct);
        string? fullContent = DeserializeLogContent(cachedRaw);

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
                    // Ensure newline separator so delta doesn't merge with last cached line
                    fullContent = (fullContent.Length > 0 && !fullContent.EndsWith('\n'))
                        ? string.Concat(fullContent, "\n", delta)
                        : string.Concat(fullContent, delta);
                    // Only write back if our appended result is at least as long as what's
                    // currently cached (guards against concurrent refresh losing lines)
                    var currentRaw = await _cache.GetMetadataAsync(contentKey, ct);
                    var currentContent = DeserializeLogContent(currentRaw);
                    if (currentContent is null || fullContent.Length >= currentContent.Length)
                    {
                        await _cache.SetMetadataAsync(contentKey,
                            SerializeLogContent(fullContent), ImmutableTtl, ct);
                    }
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
        await _cache.SetMetadataAsync(contentKey, SerializeLogContent(result), ImmutableTtl, ct);

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

    /// <summary>Store log content as plain text with a prefix, avoiding JSON escaping overhead.</summary>
    private static string SerializeLogContent(string content) => string.Concat(RawTextPrefix, content);

    /// <summary>
    /// Read log content from cache. Supports both raw: prefixed (new format) and
    /// JSON-wrapped (legacy) entries for backward compatibility.
    /// </summary>
    private static string? DeserializeLogContent(string? cached)
    {
        if (cached is null) return null;
        if (cached.StartsWith(RawTextPrefix, StringComparison.Ordinal))
            return cached[RawTextPrefix.Length..];
        // Legacy JSON-wrapped format — graceful migration
        return JsonSerializer.Deserialize<string>(cached);
    }

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
        var count = content.AsSpan().Count('\n');
        // When content doesn't end with '\n', there's one more line after the last '\n'
        if (!content.EndsWith('\n')) count++;
        return count;
    }

    internal static string? ExtractRange(string content, int? startLine, int? endLine)
    {
        var span = content.AsSpan();
        var totalLines = CountLines(content);
        if (totalLines == 0) return null;

        var start = startLine ?? 0;
        var end = endLine ?? (totalLines - 1);

        if (start >= totalLines || start < 0)
            return null;

        end = Math.Min(end, totalLines - 1);
        if (end < start)
            return null;

        // Find the character offset of the start line
        var startOffset = 0;
        for (var i = 0; i < start; i++)
        {
            var nl = span[startOffset..].IndexOf('\n');
            if (nl < 0) return null;
            startOffset += nl + 1;
        }

        // Find the character offset just past the end line's '\n'
        var endOffset = startOffset;
        for (var i = start; i <= end; i++)
        {
            var nl = span[endOffset..].IndexOf('\n');
            if (nl < 0)
            {
                // Last line has no trailing newline
                endOffset = span.Length;
                break;
            }
            endOffset += nl + 1;
        }

        // Trim trailing '\n' from the result to match original Join behavior
        if (endOffset > startOffset && span[endOffset - 1] == '\n')
            endOffset--;

        return content[startOffset..endOffset];
    }
}
