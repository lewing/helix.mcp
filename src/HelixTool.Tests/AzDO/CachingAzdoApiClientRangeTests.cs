using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

/// <summary>
/// Tests for the hybrid caching + delta-append + range-extraction behavior
/// in <see cref="CachingAzdoApiClient.GetBuildLogAsync"/>.
/// </summary>
public class CachingAzdoApiClientRangeTests
{
    private readonly IAzdoApiClient _inner = Substitute.For<IAzdoApiClient>();
    private readonly ICacheStore _cache = Substitute.For<ICacheStore>();
    private readonly CachingAzdoApiClient _sut;

    public CachingAzdoApiClientRangeTests()
    {
        var opts = new CacheOptions { MaxSizeBytes = 1024 * 1024 };
        _sut = new CachingAzdoApiClient(_inner, _cache, opts);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Matches the content cache key (contains "log:" but not "log-fresh:" or "logslist:").</summary>
    private static bool IsContentKey(string k) =>
        k.Contains("log:") && !k.Contains("log-fresh:") && !k.Contains("logslist:");

    /// <summary>Matches the freshness marker key.</summary>
    private static bool IsFreshKey(string k) => k.Contains("log-fresh:");

    /// <summary>Set up cache for "content hit + fresh".</summary>
    private void SetupCacheHitFresh(string content)
    {
        _cache.GetMetadataAsync(Arg.Is<string>(k => IsContentKey(k)), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(content));
        _cache.GetMetadataAsync(Arg.Is<string>(k => IsFreshKey(k)), Arg.Any<CancellationToken>())
            .Returns("\"1\"");
    }

    /// <summary>Set up cache for "content hit + stale" (freshness key miss).</summary>
    private void SetupCacheHitStale(string content)
    {
        _cache.GetMetadataAsync(Arg.Is<string>(k => IsContentKey(k)), Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Serialize(content));
        _cache.GetMetadataAsync(Arg.Is<string>(k => IsFreshKey(k)), Arg.Any<CancellationToken>())
            .Returns((string?)null);
    }

    /// <summary>Set up cache for "content miss" (both keys miss).</summary>
    private void SetupCacheMiss()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
    }

    /// <summary>Mark build as in-progress or completed via the state cache.</summary>
    private void SetBuildCompleted(bool completed)
    {
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)completed);
    }

    // ── C-1: Full fetch, not cached → fetches from inner, caches result ─

    [Fact]
    public async Task C01_FullFetch_NotCached_FetchesFromInnerAndCaches()
    {
        SetupCacheMiss();
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("full log content");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("full log content", result);
        await _inner.Received(1).GetBuildLogAsync("org", "proj", 1, 5,
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        // Content key stored with 4h TTL
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsContentKey(k)),
            Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    // ── C-2: Full fetch, cached + fresh → returns from cache, no inner call ─

    [Fact]
    public async Task C02_FullFetch_CachedAndFresh_ReturnsCachedNoInnerCall()
    {
        SetupCacheHitFresh("cached log content");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("cached log content", result);
        await _inner.DidNotReceive().GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── C-3: Range fetch, full log cached + fresh → extracted range ─

    [Fact]
    public async Task C03_RangeFetch_CachedAndFresh_ReturnsExtractedRange()
    {
        SetupCacheHitFresh("line0\nline1\nline2\nline3\nline4");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5, startLine: 1, endLine: 3);

        Assert.Equal("line1\nline2\nline3", result);
        await _inner.DidNotReceive().GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── C-4: Range fetch, not cached → passes range to inner, no caching ─

    [Fact]
    public async Task C04_RangeFetch_NotCached_PassesThroughNoCache()
    {
        SetupCacheMiss();

        _inner.GetBuildLogAsync("org", "proj", 1, 5, 10, 20, Arg.Any<CancellationToken>())
            .Returns("range content");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5, startLine: 10, endLine: 20);

        Assert.Equal("range content", result);
        await _inner.Received(1).GetBuildLogAsync("org", "proj", 1, 5, 10, 20, Arg.Any<CancellationToken>());
        // Should NOT cache partial results
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Is<string>(k => IsContentKey(k)),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    // ── C-5: ExtractRange startLine=0, endLine=2 → first 3 lines ─

    [Fact]
    public void C05_ExtractRange_StartZeroEndTwo_ReturnsFirstThreeLines()
    {
        var content = "line0\nline1\nline2\nline3\nline4";

        var result = CachingAzdoApiClient.ExtractRange(content, 0, 2);

        Assert.Equal("line0\nline1\nline2", result);
    }

    // ── C-6: ExtractRange startLine=3, endLine=null → last 2 lines ─

    [Fact]
    public void C06_ExtractRange_StartThreeEndNull_ReturnsLastTwoLines()
    {
        var content = "line0\nline1\nline2\nline3\nline4";

        var result = CachingAzdoApiClient.ExtractRange(content, 3, null);

        Assert.Equal("line3\nline4", result);
    }

    // ── C-7: ExtractRange with out-of-bounds endLine → clamps ─

    [Fact]
    public void C07_ExtractRange_OutOfBoundsEndLine_ClampsToLastLine()
    {
        var content = "line0\nline1\nline2\nline3\nline4";

        var result = CachingAzdoApiClient.ExtractRange(content, 0, 100);

        Assert.Equal("line0\nline1\nline2\nline3\nline4", result);
    }

    // ── C-8: Range fetch after full fetch → uses cache ─

    [Fact]
    public async Task C08_RangeFetchAfterFullFetch_UsesCache()
    {
        // Use a dictionary-backed cache to simulate real caching behavior
        var store = new Dictionary<string, string?>();
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => store.GetValueOrDefault(ci.ArgAt<string>(0)));
        _cache.When(x => x.SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(ci => store[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));
        SetBuildCompleted(true);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("line0\nline1\nline2\nline3\nline4");

        // First call: full fetch (populates cache)
        var full = await _sut.GetBuildLogAsync("org", "proj", 1, 5);
        Assert.Equal("line0\nline1\nline2\nline3\nline4", full);

        // Second call: range fetch (served from cache)
        var range = await _sut.GetBuildLogAsync("org", "proj", 1, 5, startLine: 1, endLine: 3);
        Assert.Equal("line1\nline2\nline3", range);

        // Inner should only have been called once (the full fetch)
        await _inner.Received(1).GetBuildLogAsync("org", "proj", 1, 5,
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── C-9: Cache disabled → range requests pass through ─

    [Fact]
    public async Task C09_CacheDisabled_RangePassesThrough()
    {
        var disabledOpts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingAzdoApiClient(_inner, _cache, disabledOpts);

        _inner.GetBuildLogAsync("org", "proj", 1, 5, 10, 20, Arg.Any<CancellationToken>())
            .Returns("pass-through content");

        var result = await sut.GetBuildLogAsync("org", "proj", 1, 5, startLine: 10, endLine: 20);

        Assert.Equal("pass-through content", result);
        await _cache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── C-10: In-progress first fetch → content key (4h) AND freshness key (15s) ─

    [Fact]
    public async Task C10_InProgressFirstFetch_SetsContentAndFreshnessKeys()
    {
        SetupCacheMiss();
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("log content");

        await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        // Content key with 4h TTL
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsContentKey(k)),
            Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());

        // Freshness key with 15s TTL (in-progress)
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsFreshKey(k)),
            Arg.Any<string>(),
            TimeSpan.FromSeconds(15),
            Arg.Any<CancellationToken>());
    }

    // ── C-11: Completed first fetch → content key (4h) AND freshness key (4h) ─

    [Fact]
    public async Task C11_CompletedFirstFetch_BothKeysGet4hTtl()
    {
        SetupCacheMiss();
        SetBuildCompleted(true);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("completed log");

        await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        // Both content and freshness keys should have 4h TTL
        await _cache.Received(2).SetMetadataAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    // ── C-12: In-progress, cached + fresh → returns cached, no inner call ─

    [Fact]
    public async Task C12_InProgressCachedFresh_ReturnsCachedNoInnerCall()
    {
        SetupCacheHitFresh("in-progress cached content");
        SetBuildCompleted(false);

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("in-progress cached content", result);
        await _inner.DidNotReceive().GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── C-13: In-progress, cached + stale → inner called with startLine=cachedLineCount ─

    [Fact]
    public async Task C13_InProgressCachedStale_DeltaFetchWithStartLine()
    {
        var cached = "a\nb\n";
        var cachedLineCount = CachingAzdoApiClient.CountLines(cached);
        SetupCacheHitStale(cached);
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                cachedLineCount, null, Arg.Any<CancellationToken>())
            .Returns("c\n");

        await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        await _inner.Received(1).GetBuildLogAsync("org", "proj", 1, 5,
            cachedLineCount, null, Arg.Any<CancellationToken>());
    }

    // ── C-14: Delta returns new lines → appended, content key updated ─

    [Fact]
    public async Task C14_DeltaReturnsNewLines_AppendedAndUpdated()
    {
        var cached = "a\nb\n";
        var cachedLineCount = CachingAzdoApiClient.CountLines(cached);
        SetupCacheHitStale(cached);
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                cachedLineCount, null, Arg.Any<CancellationToken>())
            .Returns("c\n");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("a\nb\nc\n", result);
        // Content key should be updated with appended content
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsContentKey(k)),
            JsonSerializer.Serialize("a\nb\nc\n"),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    // ── C-15: Delta returns empty → content unchanged, freshness still reset ─

    [Fact]
    public async Task C15_DeltaReturnsEmpty_ContentUnchangedFreshnessReset()
    {
        var cached = "a\nb\n";
        SetupCacheHitStale(cached);
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("");

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("a\nb\n", result);
        // Content key should NOT be updated (delta was empty)
        await _cache.DidNotReceive().SetMetadataAsync(
            Arg.Is<string>(k => IsContentKey(k)),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        // Freshness key should still be reset
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsFreshKey(k)),
            Arg.Any<string>(),
            TimeSpan.FromSeconds(15),
            Arg.Any<CancellationToken>());
    }

    // ── C-16: Build transitions to completed → freshness key gets 4h TTL ─

    [Fact]
    public async Task C16_BuildTransitionsToCompleted_FreshnessGets4hTtl()
    {
        var cached = "a\nb\n";
        SetupCacheHitStale(cached);
        SetBuildCompleted(true); // Now completed

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns("c\n");

        await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        // Freshness key should have 4h TTL (completed)
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => IsFreshKey(k)),
            Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    // ── C-17: Range request on stale cache → delta first, then range extracted ─

    [Fact]
    public async Task C17_RangeOnStaleCache_DeltaThenExtract()
    {
        // Cached: 5 lines. Stale. Delta adds 1 more.
        var cached = "L0\nL1\nL2\nL3\nL4\n";
        var cachedLineCount = CachingAzdoApiClient.CountLines(cached);
        SetupCacheHitStale(cached);
        SetBuildCompleted(false);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                cachedLineCount, null, Arg.Any<CancellationToken>())
            .Returns("L5\n");

        // Request range that spans into new content
        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5, startLine: 4, endLine: 5);

        // After delta-append, content = "L0\nL1\nL2\nL3\nL4\nL5\n"
        // lines = ["L0","L1","L2","L3","L4","L5",""], range [4..5] = ["L4","L5"]
        Assert.Equal("L4\nL5", result);
    }

    // ── C-18: CountLines with trailing newline ─

    [Fact]
    public void C18_CountLines_TrailingNewline_CorrectCount()
    {
        // "a\nb\n" has 2 actual lines (trailing newline doesn't add a line)
        Assert.Equal(2, CachingAzdoApiClient.CountLines("a\nb\n"));
    }

    // ── C-19: CountLines on empty string ─

    [Fact]
    public void C19_CountLines_EmptyString_ReturnsZero()
    {
        // Empty string has 0 lines
        Assert.Equal(0, CachingAzdoApiClient.CountLines(""));
    }

    // ── C-19b: CountLines without trailing newline ─

    [Fact]
    public void C19b_CountLines_NoTrailingNewline_CorrectCount()
    {
        // "a\nb" has 2 lines (no trailing newline)
        Assert.Equal(2, CachingAzdoApiClient.CountLines("a\nb"));
    }

    // ── C-20: Completed log, both keys present → no delta fetch ─

    [Fact]
    public async Task C20_CompletedCachedFresh_NoApiCall()
    {
        SetupCacheHitFresh("completed log output");
        SetBuildCompleted(true);

        var result = await _sut.GetBuildLogAsync("org", "proj", 1, 5);

        Assert.Equal("completed log output", result);
        await _inner.DidNotReceive().GetBuildLogAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(),
            Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    // ── C-21: Multiple stale refreshes accumulate correctly ─

    [Fact]
    public async Task C21_MultipleStaleRefreshes_AccumulateContent()
    {
        // Use dictionary-backed cache for multi-step simulation
        var store = new Dictionary<string, string?>();
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => store.GetValueOrDefault(ci.ArgAt<string>(0)));
        _cache.When(x => x.SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()))
            .Do(ci => store[ci.ArgAt<string>(0)] = ci.ArgAt<string>(1));
        SetBuildCompleted(false);

        // Generate content blocks (each with trailing newline)
        var initial100 = string.Join('\n', Enumerable.Range(0, 100).Select(i => $"L{i}")) + "\n";
        var delta50 = string.Join('\n', Enumerable.Range(100, 50).Select(i => $"L{i}")) + "\n";
        var delta30 = string.Join('\n', Enumerable.Range(150, 30).Select(i => $"L{i}")) + "\n";

        // Set up inner mock to return content based on startLine
        var countAfterInitial = CachingAzdoApiClient.CountLines(initial100);
        var countAfterDelta1 = CachingAzdoApiClient.CountLines(initial100 + delta50);

        _inner.GetBuildLogAsync("org", "proj", 1, 5,
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var sl = ci.ArgAt<int?>(4);
                return sl switch
                {
                    null => initial100,
                    _ when sl == countAfterInitial => delta50,
                    _ when sl == countAfterDelta1 => delta30,
                    _ => (string?)null
                };
            });

        // Call 1: full fetch (cache miss → populates cache)
        var result1 = await _sut.GetBuildLogAsync("org", "proj", 1, 5);
        Assert.Equal(initial100, result1);

        // Simulate staleness: remove the freshness key
        var freshKey = store.Keys.FirstOrDefault(k => IsFreshKey(k));
        Assert.NotNull(freshKey);
        store.Remove(freshKey);

        // Call 2: stale → delta fetch appends 50 lines
        var result2 = await _sut.GetBuildLogAsync("org", "proj", 1, 5);
        Assert.Equal(initial100 + delta50, result2);

        // Simulate staleness again
        store.Remove(freshKey);

        // Call 3: stale → delta fetch appends 30 more lines
        var result3 = await _sut.GetBuildLogAsync("org", "proj", 1, 5);
        var expected = initial100 + delta50 + delta30;
        Assert.Equal(expected, result3);

        // Verify final content has 180 actual lines (trailing newline excluded)
        Assert.Equal(180, CachingAzdoApiClient.CountLines(expected));
    }
}
