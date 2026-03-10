using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.Cache;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public class CachingAzdoApiClientTests
{
    private readonly IAzdoApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly CachingAzdoApiClient _sut;

    public CachingAzdoApiClientTests()
    {
        _inner = Substitute.For<IAzdoApiClient>();
        _cache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        _sut = new CachingAzdoApiClient(_inner, _cache, opts);
    }

    // ── GetBuildAsync: cache hit ─────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_CacheHit_ReturnsFromCacheSkipsInner()
    {
        var cached = JsonSerializer.Serialize(new AzdoBuild { Id = 42, Status = "completed" });
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await _sut.GetBuildAsync("org", "proj", 42);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        await _inner.DidNotReceive().GetBuildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── GetBuildAsync: cache miss ────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_CacheMiss_CallsInnerAndCaches()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 42, Status = "completed" };
        _inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await _sut.GetBuildAsync("org", "proj", 42);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        await _inner.Received(1).GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>());
        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── GetBuildAsync: null result not cached ────────────────────────

    [Fact]
    public async Task GetBuildAsync_NullResult_NotCached()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns((AzdoBuild?)null);

        var result = await _sut.GetBuildAsync("org", "proj", 42);

        Assert.Null(result);
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildAsync_NullResult_InnerCalledAgain()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns((AzdoBuild?)null);

        await _sut.GetBuildAsync("org", "proj", 42);
        await _sut.GetBuildAsync("org", "proj", 42);

        await _inner.Received(2).GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>());
    }

    // ── Dynamic TTL: completed vs in-progress ───────────────────────

    [Fact]
    public async Task GetBuildAsync_CompletedBuild_UsesLongTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 1, Status = "completed" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 1);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildAsync_InProgressBuild_UsesShortTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 1, Status = "inProgress" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 1);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromSeconds(15),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildAsync_CompletedBuild_SetsJobCompletedTrue()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 1, Status = "completed" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 1);

        await _cache.Received(1).SetJobCompletedAsync(
            Arg.Any<string>(), true, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildAsync_InProgressBuild_SetsJobCompletedFalse()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 1, Status = "inProgress" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 1);

        await _cache.Received(1).SetJobCompletedAsync(
            Arg.Any<string>(), false, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── GetTimelineAsync: caching depends on build state ────────────

    [Fact]
    public async Task GetTimelineAsync_CompletedBuild_CachesResult()
    {
        // Build state is "completed" via IsJobCompletedAsync
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var timeline = new AzdoTimeline { Id = "tl1" };
        _inner.GetTimelineAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _sut.GetTimelineAsync("org", "proj", 1);

        Assert.NotNull(result);
        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTimelineAsync_RunningBuild_SkipsCache()
    {
        // Build state is "running" — IsJobCompletedAsync returns false
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var timeline = new AzdoTimeline { Id = "tl1" };
        _inner.GetTimelineAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await _sut.GetTimelineAsync("org", "proj", 1);

        Assert.NotNull(result);
        // Cache SetMetadataAsync should NOT be called for timeline
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Is<string>(k => k.Contains("timeline")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTimelineAsync_CompletedBuild_CacheHit_ReturnsFromCache()
    {
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var cached = JsonSerializer.Serialize(new AzdoTimeline { Id = "cached-tl" });
        _cache.GetMetadataAsync(Arg.Is<string>(k => k.Contains("timeline")), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await _sut.GetTimelineAsync("org", "proj", 1);

        Assert.NotNull(result);
        Assert.Equal("cached-tl", result!.Id);
        await _inner.DidNotReceive().GetTimelineAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── GetBuildLogAsync: caching ────────────────────────────────────

    [Fact]
    public async Task GetBuildLogAsync_CacheMiss_CallsInnerAndCaches()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetBuildLogAsync("org", "proj", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("log content");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("log content", result);
        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(4), // immutable TTL
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildLogAsync_CacheHit_ReturnsFromCache()
    {
        var cached = JsonSerializer.Serialize("cached log");
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cached);

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("cached log", result);
        await _inner.DidNotReceive().GetBuildLogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetBuildLogAsync_NullResult_NotCached()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetBuildLogAsync("org", "proj", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Null(result);
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── ListBuildsAsync: short TTL ──────────────────────────────────

    [Fact]
    public async Task ListBuildsAsync_CacheMiss_UsesShortTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.ListBuildsAsync("org", "proj", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuild>());

        await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter());

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromSeconds(30), // list TTL
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListBuildsAsync_CacheHit_ReturnsFromCache()
    {
        var builds = new List<AzdoBuild> { new() { Id = 1 } };
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(builds));

        var result = await _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter());

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);
        await _inner.DidNotReceive().ListBuildsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>());
    }

    // ── Test runs/results caching ───────────────────────────────────

    [Fact]
    public async Task GetTestRunsAsync_CacheMiss_UsesTestTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetTestRunsAsync("org", "proj", 1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestRun>());

        await _sut.GetTestRunsAsync("org", "proj", 1);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(1), // test TTL
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTestResultsAsync_CacheMiss_UsesTestTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetTestResultsAsync("org", "proj", 77, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoTestResult>());

        await _sut.GetTestResultsAsync("org", "proj", 77);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(1), // test TTL
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTestRunsAsync_CacheHit_SkipsInner()
    {
        var runs = new List<AzdoTestRun> { new() { Id = 1, Name = "run1" } };
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(runs));

        var result = await _sut.GetTestRunsAsync("org", "proj", 1);

        Assert.Single(result);
        await _inner.DidNotReceive().GetTestRunsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── GetBuildChangesAsync: immutable TTL ──────────────────────────

    [Fact]
    public async Task GetBuildChangesAsync_CacheMiss_UsesImmutableTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetBuildChangesAsync("org", "proj", 1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildChange>());

        await _sut.GetBuildChangesAsync("org", "proj", 1);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(4), // immutable TTL
            Arg.Any<CancellationToken>());
    }

    // ── Cache disabled (MaxSizeBytes=0) ─────────────────────────────

    [Fact]
    public async Task CacheDisabled_PassesThroughToInner()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(_inner, disabledCache, opts);

        var build = new AzdoBuild { Id = 1, Status = "completed" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        var result = await sut.GetBuildAsync("org", "proj", 1);

        Assert.NotNull(result);
        Assert.Equal(1, result!.Id);
        await disabledCache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheDisabled_TimelinePassesThrough()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(_inner, disabledCache, opts);

        var timeline = new AzdoTimeline { Id = "tl1" };
        _inner.GetTimelineAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(timeline);

        var result = await sut.GetTimelineAsync("org", "proj", 1);

        Assert.NotNull(result);
        await disabledCache.DidNotReceive().IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CacheDisabled_LogPassesThrough()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(_inner, disabledCache, opts);

        _inner.GetBuildLogAsync("org", "proj", 1, 5, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("log text");

        var result = await sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("log text", result);
        await disabledCache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Cache key includes azdo: prefix ─────────────────────────────

    [Fact]
    public async Task GetBuildAsync_CacheKeyContainsAzdoPrefix()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var build = new AzdoBuild { Id = 1, Status = "completed" };
        _inner.GetBuildAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 1);

        await _cache.Received(1).SetMetadataAsync(
            Arg.Is<string>(k => k.StartsWith("azdo:")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── GetTimelineAsync: null timeline not cached ──────────────────

    [Fact]
    public async Task GetTimelineAsync_NullResult_NotCached()
    {
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetTimelineAsync("org", "proj", 1, Arg.Any<CancellationToken>())
            .Returns((AzdoTimeline?)null);

        var result = await _sut.GetTimelineAsync("org", "proj", 1);

        Assert.Null(result);
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Is<string>(k => k.Contains("timeline")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── Second call hits cache ──────────────────────────────────────

    [Fact]
    public async Task GetBuildAsync_SecondCall_ReturnsCachedWithoutCallingInner()
    {
        var build = new AzdoBuild { Id = 42, Status = "completed" };
        var serialized = JsonSerializer.Serialize(build);

        // First call: miss, second call: hit
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null, serialized);

        _inner.GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>())
            .Returns(build);

        await _sut.GetBuildAsync("org", "proj", 42);
        var result = await _sut.GetBuildAsync("org", "proj", 42);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        // Inner should only be called once (for the first miss)
        await _inner.Received(1).GetBuildAsync("org", "proj", 42, Arg.Any<CancellationToken>());
    }
}
