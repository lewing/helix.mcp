using System.Text.Json;

namespace HelixTool.Core;

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

    public CachingHelixApiClient(IHelixApiClient inner, ICacheStore cache, CacheOptions options)
    {
        _inner = inner;
        _cache = cache;
        _enabled = options.MaxSizeBytes > 0;
    }

    public async Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetJobDetailsAsync(jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:details";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dto = JsonSerializer.Deserialize<JobDetailsDto>(cached)!;
            return dto;
        }

        var result = await _inner.GetJobDetailsAsync(jobId, ct);

        // Update job state cache
        var isCompleted = result.Finished != null;
        await _cache.SetJobCompletedAsync(jobId, isCompleted,
            isCompleted ? JobStateCompletedTtl : JobStateTtl, ct);

        var ttl = isCompleted ? CompletedLongTtl : RunningShortTtl;
        var json = JsonSerializer.Serialize(JobDetailsDto.From(result));
        await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

        return result;
    }

    public async Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListWorkItemsAsync(jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:workitems";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dtos = JsonSerializer.Deserialize<List<WorkItemSummaryDto>>(cached)!;
            return dtos.Cast<IWorkItemSummary>().ToList();
        }

        var result = await _inner.ListWorkItemsAsync(jobId, ct);
        var ttl = await GetTtlAsync(jobId, RunningShortTtl, CompletedLongTtl, ct);
        var json = JsonSerializer.Serialize(result.Select(WorkItemSummaryDto.From).ToList());
        await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

        return result;
    }

    public async Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetWorkItemDetailsAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:details";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dto = JsonSerializer.Deserialize<WorkItemDetailsDto>(cached)!;
            return dto;
        }

        var result = await _inner.GetWorkItemDetailsAsync(workItemName, jobId, ct);
        var ttl = await GetTtlAsync(jobId, RunningShortTtl, CompletedLongTtl, ct);
        var json = JsonSerializer.Serialize(WorkItemDetailsDto.From(result));
        await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

        return result;
    }

    public async Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.ListWorkItemFilesAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:files";
        var cached = await _cache.GetMetadataAsync(cacheKey, ct);
        if (cached != null)
        {
            var dtos = JsonSerializer.Deserialize<List<WorkItemFileDto>>(cached)!;
            return dtos.Cast<IWorkItemFile>().ToList();
        }

        var result = await _inner.ListWorkItemFilesAsync(workItemName, jobId, ct);
        var ttl = await GetTtlAsync(jobId, RunningMediumTtl, CompletedLongTtl, ct);
        var json = JsonSerializer.Serialize(result.Select(WorkItemFileDto.From).ToList());
        await _cache.SetMetadataAsync(cacheKey, json, ttl, ct);

        return result;
    }

    public async Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetConsoleLogAsync(workItemName, jobId, ct);

        // Never cache console logs for running jobs (append-only streams)
        var isCompleted = await IsJobCompletedAsync(jobId, ct);
        if (!isCompleted)
            return await _inner.GetConsoleLogAsync(workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:console";
        var cachedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (cachedStream != null)
            return cachedStream;

        var stream = await _inner.GetConsoleLogAsync(workItemName, jobId, ct);
        await _cache.SetArtifactAsync(cacheKey, stream, ct);
        await stream.DisposeAsync();

        return (await _cache.GetArtifactAsync(cacheKey, ct))!;
    }

    public async Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
    {
        if (!_enabled) return await _inner.GetFileAsync(fileName, workItemName, jobId, ct);

        var cacheKey = $"job:{CacheSecurity.SanitizeCacheKeySegment(jobId)}:wi:{CacheSecurity.SanitizeCacheKeySegment(workItemName)}:file:{CacheSecurity.SanitizeCacheKeySegment(fileName)}";
        var cachedStream = await _cache.GetArtifactAsync(cacheKey, ct);
        if (cachedStream != null)
            return cachedStream;

        var stream = await _inner.GetFileAsync(fileName, workItemName, jobId, ct);
        await _cache.SetArtifactAsync(cacheKey, stream, ct);
        await stream.DisposeAsync();

        return (await _cache.GetArtifactAsync(cacheKey, ct))!;
    }

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

    // DTOs for JSON serialization of interface types

    private record JobDetailsDto(string? Name, string? QueueId, string? Creator, string? Source, string? Created, string? Finished) : IJobDetails
    {
        public static JobDetailsDto From(IJobDetails d) => new(d.Name, d.QueueId, d.Creator, d.Source, d.Created, d.Finished);
    }

    private record WorkItemSummaryDto(string Name) : IWorkItemSummary
    {
        public static WorkItemSummaryDto From(IWorkItemSummary s) => new(s.Name);
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
