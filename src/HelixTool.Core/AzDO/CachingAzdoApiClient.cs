using System.Text.Json;
using HelixTool.Core.Acquisition;
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
    private const string TestRunsCacheKeyPrefix = "testruns:v2";
    private const string TestResultsCacheKeyPrefix = "testresults:v2";

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
    private readonly CacheOptions _options;
    private readonly IAzdoTokenAccessor? _tokenAccessor;

    public CachingAzdoApiClient(IAzdoApiClient inner, ICacheStore cache, CacheOptions options)
        : this(inner, cache, options, tokenAccessor: null)
    {
    }

    public CachingAzdoApiClient(IAzdoApiClient inner, ICacheStore cache, CacheOptions options, IAzdoTokenAccessor? tokenAccessor)
    {
        _inner = inner;
        _cache = cache;
        _options = options;
        _tokenAccessor = tokenAccessor;
        _enabled = options.MaxSizeBytes > 0;
    }

    public async Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildAsync(org, project, buildId, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"build:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<AzdoBuild>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetBuildAsync(org, project, buildId, ct),
            key,
            CompletedTtl,
            static () => Task.FromResult(true),
            ct);
        if (result is null)
            return null;

        var isCompleted = IsCompletedStatus(result.Status);
        var stateKey = BuildStateKey(org, project, buildId);
        await _cache.SetJobCompletedAsync(stateKey, isCompleted,
            isCompleted ? BuildStateCompletedTtl : BuildStateTtl, ct);

        var ttl = isCompleted ? CompletedTtl : InProgressTtl;
        key = BuildCacheKey(org, project, $"build:{buildId}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ttl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListBuildsAsync(org, project, filter, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var filterHash = AzdoCacheKeys.HashFilter(filter);
        var key = BuildCacheKey(org, project, $"builds:{filterHash}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoBuild>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.ListBuildsAsync(org, project, filter, ct),
            key,
            ListTtl,
            static () => Task.FromResult(true),
            ct);
        key = BuildCacheKey(org, project, $"builds:{filterHash}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ListTtl, ct);

        return result;
    }

    public async Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTimelineAsync(org, project, buildId, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"timeline:{buildId}");

        // In eval mode: serve cached timeline immediately; do not gate on completion state.
        if (_options.EvalMode)
        {
            var cachedEval = await _cache.GetMetadataAsync(key, ct);
            var deserializedEval = TryDeserialize<AzdoTimeline>(cachedEval, throwOnCorrupt: true, key);
            if (deserializedEval is not null)
                return deserializedEval;
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);
            return await _inner.GetTimelineAsync(org, project, buildId, ct);
        }

        // Never cache timeline while the build is running — it changes constantly
        var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
        if (!isCompleted)
        {
            return await CallAndMaybeRecordAsync(
                () => _inner.GetTimelineAsync(org, project, buildId, ct),
                key,
                InProgressTtl,
                static () => Task.FromResult(false),
                ct);
        }

        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<AzdoTimeline>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetTimelineAsync(org, project, buildId, ct),
            key,
            CompletedTtl,
            static () => Task.FromResult(true),
            ct);
        if (result is not null)
        {
            key = BuildCacheKey(org, project, $"timeline:{buildId}");
            await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), CompletedTtl, ct);
        }

        return result;
    }

    public async Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default)
    {
        if (!_enabled)
            return await _inner.GetBuildLogAsync(org, project, buildId, logId, startLine, endLine, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var contentKey = BuildCacheKey(org, project, $"log:{buildId}:{logId}");
        var freshKey = BuildCacheKey(org, project, $"log-fresh:{buildId}:{logId}");

        var cachedRaw = await _cache.GetMetadataAsync(contentKey, ct);
        string? fullContent = DeserializeLogContent(cachedRaw, _options.EvalMode, contentKey);

        if (fullContent is not null)
        {
            // In eval mode: serve cached primary log content immediately — freshness marker may
            // have been evicted while primary evidence outlives it.
            if (_options.EvalMode)
            {
                if (startLine is null && endLine is null)
                    return fullContent;
                return ExtractRange(fullContent, startLine, endLine);
            }

            // Check freshness — stale means the 15s marker expired
            var isFresh = await _cache.GetMetadataAsync(freshKey, ct) is not null;

            if (!isFresh)
            {
                // Stale: delta-append instead of full re-download
                var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
                var cachedLineCount = CountLines(fullContent);
                var delta = await CallAndMaybeRecordAsync(
                    () => _inner.GetBuildLogAsync(
                        org, project, buildId, logId, startLine: cachedLineCount, ct: ct),
                    contentKey,
                    isCompleted ? ImmutableTtl : InProgressTtl,
                    () => Task.FromResult(isCompleted),
                    ct);

                if (!string.IsNullOrEmpty(delta))
                {
                    // Ensure newline separator so delta doesn't merge with last cached line
                    fullContent = (fullContent.Length > 0 && !fullContent.EndsWith('\n'))
                        ? string.Concat(fullContent, "\n", delta)
                        : string.Concat(fullContent, delta);
                    // Only write back if our appended result is at least as long as what's
                    // currently cached (guards against concurrent refresh losing lines)
                    contentKey = BuildCacheKey(org, project, $"log:{buildId}:{logId}");
                    var currentRaw = await _cache.GetMetadataAsync(contentKey, ct);
                    var currentContent = DeserializeLogContent(currentRaw);
                    if (currentContent is null || fullContent.Length >= currentContent.Length)
                    {
                        await _cache.SetMetadataAsync(contentKey,
                            SerializeLogContent(fullContent), ImmutableTtl, ct);
                    }
                }

                freshKey = BuildCacheKey(org, project, $"log-fresh:{buildId}:{logId}");
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

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(contentKey, ct);

        // Not cached — range request with no cached full log: pass through, don't cache partial
        if (startLine is not null || endLine is not null)
        {
            return await CallAndMaybeRecordAsync(
                () => _inner.GetBuildLogAsync(org, project, buildId, logId, startLine, endLine, ct),
                contentKey,
                ImmutableTtl,
                () => IsBuildCompletedAsync(org, project, buildId, ct),
                ct);
        }

        // Full log first fetch
        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetBuildLogAsync(org, project, buildId, logId, ct: ct),
            contentKey,
            ImmutableTtl,
            () => IsBuildCompletedAsync(org, project, buildId, ct),
            ct);
        if (result is null) return null;
        if (result.Length == 0) return result;

        var completed = await IsBuildCompletedAsync(org, project, buildId, ct);
        contentKey = BuildCacheKey(org, project, $"log:{buildId}:{logId}");
        freshKey = BuildCacheKey(org, project, $"log-fresh:{buildId}:{logId}");
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

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"changes:{buildId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoBuildChange>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetBuildChangesAsync(org, project, buildId, top, ct),
            key,
            ImmutableTtl,
            () => IsBuildCompletedAsync(org, project, buildId, ct),
            ct);
        key = BuildCacheKey(org, project, $"changes:{buildId}:{top}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ImmutableTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestRunsAsync(org, project, buildId, top, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"{TestRunsCacheKeyPrefix}:{buildId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoTestRun>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetTestRunsAsync(org, project, buildId, top, ct),
            key,
            TestTtl,
            () => IsBuildCompletedAsync(org, project, buildId, ct),
            ct);
        key = BuildCacheKey(org, project, $"{TestRunsCacheKeyPrefix}:{buildId}:{top}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, string? outcomes = null, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestResultsAsync(org, project, runId, top, outcomes, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var normalizedOutcomes = string.IsNullOrWhiteSpace(outcomes) ? null : outcomes.Trim();
        var key = BuildCacheKey(org, project, $"{TestResultsCacheKeyPrefix}:{runId}:{top}:{normalizedOutcomes ?? AzdoBuildFilterDefaults.Outcomes}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoTestResult>>(cached, _options.EvalMode, key);
        if (deserialized is { Count: > 0 })
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetTestResultsAsync(org, project, runId, top, normalizedOutcomes, ct),
            key,
            TestTtl,
            static () => Task.FromResult(true),
            ct);
        if (result.Count > 0)
        {
            key = BuildCacheKey(org, project, $"{TestResultsCacheKeyPrefix}:{runId}:{top}:{normalizedOutcomes ?? AzdoBuildFilterDefaults.Outcomes}");
            await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);
        }

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildArtifactsAsync(org, project, buildId, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"artifacts:{buildId}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoBuildArtifact>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetBuildArtifactsAsync(org, project, buildId, ct),
            key,
            ImmutableTtl,
            () => IsBuildCompletedAsync(org, project, buildId, ct),
            ct);
        // Artifacts are immutable once the build publishes them
        key = BuildCacheKey(org, project, $"artifacts:{buildId}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ImmutableTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetTestAttachmentsAsync(org, project, runId, resultId, top, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"testattachments:{runId}:{resultId}:{top}");
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoTestAttachment>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetTestAttachmentsAsync(org, project, runId, resultId, top, ct),
            key,
            TestTtl,
            static () => Task.FromResult(true),
            ct);
        key = BuildCacheKey(org, project, $"testattachments:{runId}:{resultId}:{top}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), TestTtl, ct);

        return result;
    }

    public async Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetBuildLogsListAsync(org, project, buildId, ct);

        await EnsureAuthTokenHashAsync(ct).ConfigureAwait(false);

        var key = BuildCacheKey(org, project, $"logslist:{buildId}");

        // In eval mode the cached list is primary evidence. Its build-state marker may
        // have expired or been omitted from the snapshot, so inspect it only on a miss.
        if (_options.EvalMode)
        {
            var cachedEval = await _cache.GetMetadataAsync(key, ct);
            var deserializedEval = TryDeserialize<List<AzdoBuildLogEntry>>(cachedEval, throwOnCorrupt: true, key);
            if (deserializedEval is not null)
                return deserializedEval;

            await ThrowReplayedSnapshotErrorIfPresentAsync(key, ct);
            return await _inner.GetBuildLogsListAsync(org, project, buildId, ct);
        }

        var isCompleted = await IsBuildCompletedAsync(org, project, buildId, ct);
        var ttl = isCompleted ? CompletedTtl : InProgressTtl;
        var cached = await _cache.GetMetadataAsync(key, ct);
        var deserialized = TryDeserialize<List<AzdoBuildLogEntry>>(cached, _options.EvalMode, key);
        if (deserialized is not null)
            return deserialized;

        var result = await CallAndMaybeRecordAsync(
            () => _inner.GetBuildLogsListAsync(org, project, buildId, ct),
            key,
            ttl,
            () => Task.FromResult(isCompleted),
            ct);
        key = BuildCacheKey(org, project, $"logslist:{buildId}");
        await _cache.SetMetadataAsync(key, JsonSerializer.Serialize(result), ttl, ct);

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task EnsureAuthTokenHashAsync(CancellationToken ct)
    {
        if (_tokenAccessor is null)
            return;

        var credential = await _tokenAccessor.GetAccessTokenAsync(ct).ConfigureAwait(false);
        var authContext = credential is null
            ? null
            : credential.CacheIdentity ?? AzdoCredential.BuildCacheIdentity(credential.Source, credential.DisplayToken);
        _options.UpdateAuthContext(authContext);
    }

    private string BuildCacheKey(string org, string project, string suffix)
        => AzdoCacheKeys.MetadataKey(_options, org, project, suffix);

    private string BuildStateKey(string org, string project, int buildId)
        => AzdoCacheKeys.BuildStateKey(_options, org, project, buildId);

    private static bool IsCompletedStatus(string? status)
        => status?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Store log content as plain text with a prefix, avoiding JSON escaping overhead.</summary>
    private static string SerializeLogContent(string content) => string.Concat(RawTextPrefix, content);

    /// <summary>
    /// Read log content from cache. Supports both raw: prefixed (new format) and
    /// JSON-wrapped (legacy) entries for backward compatibility.
    /// Returns null on corrupt entries (treated as cache miss).
    /// </summary>
    private static string? DeserializeLogContent(string? cached, bool throwOnCorrupt = false, string? key = null)
    {
        if (cached is null) return null;
        if (cached.StartsWith(RawTextPrefix, StringComparison.Ordinal))
            return cached[RawTextPrefix.Length..];
        // Legacy JSON-wrapped format — graceful migration
        return TryDeserialize<string>(cached, throwOnCorrupt, key);
    }

    /// <summary>
    /// Safely deserialize a cached JSON value. Returns default(T) if the cached
    /// data is corrupt or unparseable, treating it as a cache miss rather than crashing.
    /// </summary>
    private static T? TryDeserialize<T>(string? cached, bool throwOnCorrupt = false, string? key = null)
    {
        if (cached is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (JsonException ex)
        {
            if (throwOnCorrupt)
            {
                throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.InvalidResponse,
                    "cache",
                    "deserialize_cache_entry",
                    new Dictionary<string, object?> { ["key"] = key ?? "(unknown)" },
                    "Cached AzDO snapshot entry is corrupt or not valid JSON."),
                    ex);
            }

            return default;
        }
    }

    private async Task ThrowReplayedSnapshotErrorIfPresentAsync(string key, CancellationToken ct)
    {
        var error = await _cache.GetAcquisitionErrorAsync(key, ct);
        if (error is not null)
            throw new HlxAcquisitionException(AcquisitionFailureRecorderPolicy.ReplayFromSnapshot(error));
    }

    private async Task<T> CallAndMaybeRecordAsync<T>(
        Func<Task<T>> call,
        string key,
        TimeSpan ttl,
        Func<Task<bool>> shouldRecordNotFoundAsync,
        CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (HlxAcquisitionException ex)
        {
            if (AcquisitionFailureRecorderPolicy.IsRecordable(ex.Error)
                && (ex.Error.Kind != AcquisitionErrorKind.NotFound || await shouldRecordNotFoundAsync()))
            {
                await _cache.SetAcquisitionErrorAsync(key, ex.Error, ttl, ct);
            }

            throw;
        }
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
