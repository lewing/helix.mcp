using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;

namespace HelixTool.Core.Helix;

/// <summary>
/// Decorator that adds SQLite-backed caching to any <see cref="IHelixApiClient"/>.
/// TTL varies based on whether the job is completed or still running.
/// Console logs for running jobs are never cached (append-only streams).
/// Pass-through when <see cref="CacheOptions.MaxSizeBytes"/> is 0 (disabled).
/// </summary>
public sealed class CachingHelixApiClient : IHelixApiClient
{
    private static readonly TimeSpan RunningShortTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RunningMediumTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CompletedLongTtl = TimeSpan.FromHours(4);
    private static readonly TimeSpan ConsoleLogTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan JobStateTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan JobStateCompletedTtl = TimeSpan.FromHours(4);

    private readonly IHelixApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly bool _enabled;
    private readonly CacheOptions _options;

    public CachingHelixApiClient(IHelixApiClient inner, ICacheStore cache, CacheOptions options)
    {
        _inner = inner;
        _cache = cache;
        _options = options;
        _enabled = options.MaxSizeBytes > 0;
    }

    public async Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetJobDetailsAsync(jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:details";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dto = TryDeserialize<JobDetailsDto>(cached, cacheKey);
            if (dto is null)
                return await FetchAndCacheAsync();
            return dto;
        }

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);

        return await FetchAndCacheAsync();

        async Task<IJobDetails> FetchAndCacheAsync()
        {
            var result = await CallAndMaybeRecordAsync(
                () => _inner.GetJobDetailsAsync(jobId, ct),
                cacheKey,
                CompletedLongTtl,
                async () => await _cache.IsJobCompletedAsync(jobId, ct) == true,
                ct);

            // Update job state cache
            var isCompleted = result.Finished != null;
            await _cache.SetJobCompletedAsync(jobId, isCompleted,
                isCompleted ? JobStateCompletedTtl : JobStateTtl, ct);

            var ttl = isCompleted ? CompletedLongTtl : RunningShortTtl;
            var json = JsonSerializer.Serialize(JobDetailsDto.From(result));
            await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

            return result;
        }
    }

    public async Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListWorkItemsAsync(jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:workitems";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dtos = TryDeserialize<List<WorkItemSummaryDto>>(cached, cacheKey);
            if (dtos is null)
                return await FetchAndCacheAsync();
            return dtos.Cast<IWorkItemSummary>().ToList();
        }

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);

        return await FetchAndCacheAsync();

        async Task<IReadOnlyList<IWorkItemSummary>> FetchAndCacheAsync()
        {
            var result = await CallAndMaybeRecordAsync(
                () => _inner.ListWorkItemsAsync(jobId, ct),
                cacheKey,
                CompletedLongTtl,
                () => IsJobCompletedAsync(jobId, ct),
                ct);
            var ttl = await GetTtlAsync(jobId, RunningShortTtl, CompletedLongTtl, ct);
            var json = JsonSerializer.Serialize(result.Select(WorkItemSummaryDto.From).ToList());
            await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

            return result;
        }
    }

    public async Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetWorkItemDetailsAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:details";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dto = TryDeserialize<WorkItemDetailsDto>(cached, cacheKey);
            if (dto is null)
                return await FetchAndCacheAsync();
            return dto;
        }

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);

        return await FetchAndCacheAsync();

        async Task<IWorkItemDetails> FetchAndCacheAsync()
        {
            var result = await CallAndMaybeRecordAsync(
                () => _inner.GetWorkItemDetailsAsync(workItemName, jobId, ct),
                cacheKey,
                CompletedLongTtl,
                () => IsJobCompletedAsync(jobId, ct),
                ct);
            var ttl = await GetTtlAsync(jobId, RunningShortTtl, CompletedLongTtl, ct);
            var json = JsonSerializer.Serialize(WorkItemDetailsDto.From(result));
            await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

            return result;
        }
    }

    public async Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListWorkItemFilesAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:files";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dtos = TryDeserialize<List<WorkItemFileDto>>(cached, cacheKey);
            if (dtos is null)
                return await FetchAndCacheAsync();
            return dtos.Cast<IWorkItemFile>().ToList();
        }

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);

        return await FetchAndCacheAsync();

        async Task<IReadOnlyList<IWorkItemFile>> FetchAndCacheAsync()
        {
            var result = await CallAndMaybeRecordAsync(
                () => _inner.ListWorkItemFilesAsync(workItemName, jobId, ct),
                cacheKey,
                CompletedLongTtl,
                () => IsJobCompletedAsync(jobId, ct),
                ct);
            var ttl = await GetTtlAsync(jobId, RunningMediumTtl, CompletedLongTtl, ct);
            var json = JsonSerializer.Serialize(result.Select(WorkItemFileDto.From).ToList());
            await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

            return result;
        }
    }

    public async Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetConsoleLogAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:console";

        // In eval mode: serve the cached artifact immediately if present; missing completion
        // state must not invalidate existing primary evidence.
        if (_options.EvalMode)
        {
            var cachedStreamEval = await _cache.GetArtifactAsync(cacheKey, ct);
            if (cachedStreamEval != null)
                return cachedStreamEval;
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);
            return await _inner.GetConsoleLogAsync(workItemName, jobId, ct);
        }

        // Never cache console logs for running jobs (append-only streams)
        var isCompleted = await IsJobCompletedAsync(jobId, ct);
        if (!isCompleted)
        {
            return await CallAndMaybeRecordAsync(
                () => _inner.GetConsoleLogAsync(workItemName, jobId, ct),
                cacheKey,
                ConsoleLogTtl,
                static () => Task.FromResult(false),
                ct);
        }

        var cachedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (cachedStream != null)
            return cachedStream;

        var stream = await CallAndMaybeRecordAsync(
            () => _inner.GetConsoleLogAsync(workItemName, jobId, ct),
            cacheKey,
            ConsoleLogTtl,
            static () => Task.FromResult(true),
            ct);
        await _cache.SetArtifactAsync(cacheKey, stream, ct);
        await stream.DisposeAsync();

        var storedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (storedStream != null)
            return storedStream;
        return await _inner.GetConsoleLogAsync(workItemName, jobId, ct);
    }

    public async Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetFileAsync(fileName, workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:file:{CacheSecurity.SanitizeCacheKeySegment(fileName)}";
        var cachedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (cachedStream != null)
            return cachedStream;

        if (_options.EvalMode)
            await ThrowReplayedSnapshotErrorIfPresentAsync(cacheKey, ct);

        var stream = await CallAndMaybeRecordAsync(
            () => _inner.GetFileAsync(fileName, workItemName, jobId, ct),
            cacheKey,
            CompletedLongTtl,
            () => IsJobCompletedAsync(jobId, ct),
            ct);
        await _cache.SetArtifactAsync(cacheKey, stream, ct);
        await stream.DisposeAsync();

        var storedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (storedStream != null)
            return storedStream;
        return await _inner.GetFileAsync(fileName, workItemName, jobId, ct);
    }

    /// <inheritdoc />
    /// <remarks>Not cached — source-scoped queries span many jobs; callers get fresh results.</remarks>
    public Task<IReadOnlyList<IHelixJobSummary>> ListJobsByBuildAsync(
        string source, string buildId, int count = 100_000, CancellationToken ct = default)
        => _inner.ListJobsByBuildAsync(source, buildId, count, ct);

    private async Task<bool> IsJobCompletedAsync(string jobId, CancellationToken ct)
    {
        var cached = await _cache.IsJobCompletedAsync(jobId, ct);
        if (cached.HasValue) return cached.Value;

        // Fetch job details to determine state (this call is itself cached)
        var details = await GetJobDetailsAsync(jobId, ct);
        return details.Finished != null;
    }

    private async Task<TimeSpan> GetTtlAsync(string jobId, TimeSpan runningTtl, TimeSpan completedTtl, CancellationToken ct)
    {
        var isCompleted = await IsJobCompletedAsync(jobId, ct);
        return isCompleted ? completedTtl : runningTtl;
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
            var shouldRecord = AcquisitionFailureRecorderPolicy.IsRecordable(ex.Error);
            if (shouldRecord && ex.Error.Kind == AcquisitionErrorKind.NotFound)
                shouldRecord = await TryShouldRecordNotFoundAsync(shouldRecordNotFoundAsync);

            if (shouldRecord)
            {
                await _cache.SetAcquisitionErrorAsync(key, ex.Error, ttl, ct);
            }

            throw;
        }
    }

    private static async Task<bool> TryShouldRecordNotFoundAsync(Func<Task<bool>> shouldRecordNotFoundAsync)
    {
        try
        {
            return await shouldRecordNotFoundAsync();
        }
        catch
        {
            return false;
        }
    }

    private T? TryDeserialize<T>(string cached, string key)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(cached);
        }
        catch (JsonException ex)
        {
            if (_options.EvalMode)
            {
                throw new HlxAcquisitionException(AcquisitionErrorFactory.Create(
                    AcquisitionErrorKind.InvalidResponse,
                    "cache",
                    "deserialize_cache_entry",
                    new Dictionary<string, object?> { ["key"] = key },
                    "Cached Helix snapshot entry is corrupt or not valid JSON."),
                    ex);
            }

            return default;
        }
    }

    // DTOs for JSON serialization of interface types

    private record JobDetailsDto(string? Name, string? QueueId, string? QueueAlias, string? Creator, string? Source, string? Created, string? Finished, string? DockerTag) : IJobDetails
    {
        public static JobDetailsDto From(IJobDetails d) => new(d.Name, d.QueueId, d.QueueAlias, d.Creator, d.Source, d.Created, d.Finished, d.DockerTag);
    }

    private record WorkItemSummaryDto(string Name, int? ExitCode, string? ConsoleOutputUri) : IWorkItemSummary
    {
        public static WorkItemSummaryDto From(IWorkItemSummary s) => new(s.Name, s.ExitCode, s.ConsoleOutputUri);
    }

    private record WorkItemDetailsDto(int? ExitCode, string? State, string? MachineName, DateTimeOffset? Started, DateTimeOffset? Finished) : IWorkItemDetails
    {
        public static WorkItemDetailsDto From(IWorkItemDetails d) => new(d.ExitCode, d.State, d.MachineName, d.Started, d.Finished);
    }

    private record WorkItemFileDto(string Name, string? Link) : IWorkItemFile
    {
        public static WorkItemFileDto From(IWorkItemFile f) => new(f.Name, f.Link);
    }
}
