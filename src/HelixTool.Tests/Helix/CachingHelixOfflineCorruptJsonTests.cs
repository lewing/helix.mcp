using HelixTool.Core.Acquisition;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public sealed class CachingHelixOfflineCorruptJsonTests
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "System.Runtime.Tests";

    public static IEnumerable<object[]> CachedHelixEndpoints()
    {
        yield return ["job", (Func<CachingHelixApiClient, Task>)(client => client.GetJobDetailsAsync(JobId))];
        yield return ["work items", (Func<CachingHelixApiClient, Task>)(client => client.ListWorkItemsAsync(JobId))];
        yield return ["work item details", (Func<CachingHelixApiClient, Task>)(client => client.GetWorkItemDetailsAsync(WorkItem, JobId))];
        yield return ["work item files", (Func<CachingHelixApiClient, Task>)(client => client.ListWorkItemFilesAsync(WorkItem, JobId))];
    }

    [Theory]
    [MemberData(nameof(CachedHelixEndpoints))]
    public async Task EvalMode_CorruptCachedJson_ThrowsInvalidResponse(string endpoint, Func<CachingHelixApiClient, Task> invoke)
    {
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("{ this is not valid json");
        cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)null);
        var client = new CachingHelixApiClient(
            new OfflineHelixApiClient(),
            cache,
            new CacheOptions { EvalMode = true, MaxSizeBytes = 1024 * 1024 });

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => invoke(client));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "cache", "deserialize_cache_entry");
        Assert.True(error.Resource.ContainsKey("key"), $"Expected corrupt {endpoint} cache error to identify the cache key.");
    }
}
