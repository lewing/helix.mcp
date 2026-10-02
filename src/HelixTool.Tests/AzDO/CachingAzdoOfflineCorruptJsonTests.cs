using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class CachingAzdoOfflineCorruptJsonTests
{
    public static IEnumerable<object[]> CachedAzdoEndpoints()
    {
        yield return ["build", "get_build", (Func<CachingAzdoApiClient, Task>)(client => client.GetBuildAsync("org", "project", 42))];
        yield return ["builds list", "list_builds", (Func<CachingAzdoApiClient, Task>)(client => client.ListBuildsAsync("org", "project", new AzdoBuildFilter { Top = 5 }))];
        yield return ["changes", "list_build_changes", (Func<CachingAzdoApiClient, Task>)(client => client.GetBuildChangesAsync("org", "project", 42, top: 5))];
        yield return ["test runs", "list_test_runs", (Func<CachingAzdoApiClient, Task>)(client => client.GetTestRunsAsync("org", "project", 42, top: 5))];
        yield return ["test results", "list_test_results", (Func<CachingAzdoApiClient, Task>)(client => client.GetTestResultsAsync("org", "project", 101, top: 5))];
        yield return ["artifacts", "list_artifacts", (Func<CachingAzdoApiClient, Task>)(client => client.GetBuildArtifactsAsync("org", "project", 42))];
        yield return ["attachments", "list_test_attachments", (Func<CachingAzdoApiClient, Task>)(client => client.GetTestAttachmentsAsync("org", "project", 101, 202, top: 5))];
        yield return ["timeline", "get_timeline", (Func<CachingAzdoApiClient, Task>)(client => client.GetTimelineAsync("org", "project", 42))];
        yield return ["log", "get_build_log", (Func<CachingAzdoApiClient, Task>)(client => client.GetBuildLogAsync("org", "project", 42, 7))];
        yield return ["log list", "list_build_logs", (Func<CachingAzdoApiClient, Task>)(client => client.GetBuildLogsListAsync("org", "project", 42))];
    }

    [Theory]
    [MemberData(nameof(CachedAzdoEndpoints))]
    public async Task EvalMode_CorruptCachedJson_ThrowsInvalidResponseNotCacheMiss(
        string endpoint,
        string offlineOperation,
        Func<CachingAzdoApiClient, Task> invoke)
    {
        var cache = Substitute.For<ICacheStore>();
        cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("{ this is not valid json");
        cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)null);
        var client = new CachingAzdoApiClient(
            new OfflineAzdoApiClient(),
            cache,
            new CacheOptions { EvalMode = true, MaxSizeBytes = 1024 * 1024 });

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => invoke(client));

        var error = AcquisitionAssertions.Error(ex, AcquisitionErrorKind.InvalidResponse, "cache", "deserialize_cache_entry");
        Assert.NotEqual(offlineOperation, error.Operation);
        Assert.True(error.Resource.ContainsKey("key"), $"Expected corrupt {endpoint} cache error to identify the cache key.");
    }
}
