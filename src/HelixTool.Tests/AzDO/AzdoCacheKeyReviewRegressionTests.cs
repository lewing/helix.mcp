using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class AzdoCacheKeyReviewRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-cache-key-review-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, null);
        TryDelete(_root);
    }

    [Fact]
    public async Task AuthenticatedSnapshotWithSuffixNamedProject_ReplaysFromAuthPartition_Finding4169759118()
    {
        const string authHash = "abcdef12";
        const string org = "dnceng-public";
        var projects = new[] { "build", "timeline", "log", "log-fresh", "public" };
        var snapshot = Path.Combine(_root, "snapshot");
        var writerOptions = new CacheOptions { CacheRoot = snapshot, MaxSizeBytes = 1024 * 1024, AuthTokenHash = authHash };
        using (var store = new SqliteCacheStore(writerOptions))
        {
            foreach (var project in projects)
            {
                await store.SetMetadataAsync(
                    AzdoCacheKeys.MetadataKey(writerOptions, org, project, "timeline:42"),
                    JsonSerializer.Serialize(new AzdoTimeline { Id = $"timeline-{project}", Records = [] }),
                    TimeSpan.FromHours(1));
            }
        }

        var snapshotRoot = writerOptions.GetEffectiveCacheRoot();
        var inferred = EvalSnapshotAzdoPartitionSelector.Select(snapshotRoot);
        Assert.Equal($"cache-{authHash}", inferred.Partition);
        Assert.Equal(authHash, inferred.AuthTokenHash);

        foreach (var project in projects)
        {
            var evalOptions = new CacheOptions
            {
                CacheRoot = snapshotRoot,
                EvalMode = true,
                AuthTokenHash = inferred.AuthTokenHash
            };
            using var evalStore = new SqliteCacheStore(evalOptions);
            var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOptions);

            var timeline = await client.GetTimelineAsync(org, project, 42);

            Assert.Equal($"timeline-{project}", timeline?.Id);
        }
    }

    [Fact]
    public async Task SnapshotValidator_UsesCacheKeySchemaForRawLogRows_Finding4169759267()
    {
        var snapshot = Path.Combine(_root, "validator");
        var options = new CacheOptions { CacheRoot = snapshot, MaxSizeBytes = 1024 * 1024 };
        using (var store = new SqliteCacheStore(options))
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:log-fresh:log:42:7",
                CachingAzdoApiClient.RawTextPrefix,
                TimeSpan.FromHours(1));
            await store.SetMetadataAsync(
                "azdo:dnceng-public:log:timeline:42",
                CachingAzdoApiClient.RawTextPrefix,
                TimeSpan.FromHours(1));
        }

        var result = await SnapshotValidator.ValidateAsync(options.GetEffectiveCacheRoot());

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("azdo:dnceng-public:log-fresh:log:42:7", StringComparison.Ordinal)
                     && error.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            result.Errors,
            error => error.Contains("azdo:dnceng-public:log:timeline:42", StringComparison.Ordinal));
    }

    [Fact]
    public void AzdoCacheKeyParser_RoundTripsAllBuildersWithAdversarialSegments()
    {
        var names = new[] { "build", "log", "timeline", "abcdef12", "public", "project-log", "log-fresh" };

        foreach (var authHash in new string?[] { null, "abcdef12" })
        foreach (var org in names)
        foreach (var project in names)
        {
            var options = new CacheOptions { AuthTokenHash = authHash };
            var expectedPartition = authHash is null ? "public" : $"cache-{authHash}";

            foreach (var (key, expectedKind) in BuiltKeys(options, org, project))
            {
                Assert.True(AzdoCacheKeys.TryParse(key, out var parsed), $"Expected key to parse: {key}");
                Assert.Equal(expectedPartition, parsed.Partition);
                Assert.Equal(authHash, parsed.AuthTokenHash);
                Assert.Equal(org, parsed.Org);
                Assert.Equal(project, parsed.Project);
                Assert.Equal(expectedKind, parsed.ResourceKind);
                Assert.NotEmpty(parsed.SuffixSegments);
            }
        }
    }

    private static IEnumerable<(string Key, string Kind)> BuiltKeys(CacheOptions options, string org, string project)
    {
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, "build:42"), "build");
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, $"builds:{AzdoCacheKeys.HashFilter(new AzdoBuildFilter { Top = 7 })}"), "builds");
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, "timeline:42"), "timeline");
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, "log:42:7"), "log");
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, "log-fresh:42:7"), "log-fresh");
        yield return (AzdoCacheKeys.MetadataKey(options, org, project, "logslist:42"), "logslist");
        yield return (AzdoCacheKeys.BuildStateKey(options, org, project, 42), "build-state");

        yield return (AzdoListCacheKeys.ArtifactsComplete(options, org, project, 42), "artifacts");
        yield return (AzdoListCacheKeys.ChangesComplete(options, org, project, 42), "changes");
        yield return (AzdoListCacheKeys.ChangesWindow(options, org, project, 42, 0, 10), "changes");
        yield return (AzdoListCacheKeys.ChangesLegacy(options, org, project, 42, top: null), "changes");
        yield return (AzdoListCacheKeys.TestRunsComplete(options, org, project, 42), "testruns");
        yield return (AzdoListCacheKeys.TestRunsWindow(options, org, project, 42, 0, 10), "testruns");
        yield return (AzdoListCacheKeys.TestRunsLegacy(options, org, project, 42, top: null), "testruns");
        yield return (AzdoListCacheKeys.TestResultsComplete(options, org, project, 101, "Failed"), "testresults");
        yield return (AzdoListCacheKeys.TestResultsWindow(options, org, project, 101, "Passed,Failed", 0, 200), "testresults");
        yield return (AzdoListCacheKeys.TestResultsLegacy(options, org, project, 101, 200, null), "testresults");
        yield return (AzdoListCacheKeys.TestAttachmentsComplete(options, org, project, 101, 202), "testattachments");
        yield return (AzdoListCacheKeys.TestAttachmentsWindow(options, org, project, 101, 202, 0, 50), "testattachments");
        yield return (AzdoListCacheKeys.TestAttachmentsLegacy(options, org, project, 101, 202, 50), "testattachments");
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
