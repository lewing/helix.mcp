// Tests for CachingHelixApiClient decorator (L-CACHE-1 through L-CACHE-5, L-CACHE-10).
// Written against Dallas's design review — will compile once Ripley's Cache/ types are in place.

using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class CachingHelixApiClientTests
{
    private const string JobId = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";
    private const string WorkItem = "System.Runtime.Tests";

    private readonly IHelixApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly CachingHelixApiClient _sut;

    public CachingHelixApiClientTests()
    {
        _inner = Substitute.For<IHelixApiClient>();
        _cache = Substitute.For<ICacheStore>();
        _sut = new CachingHelixApiClient(_inner, _cache, new CacheOptions());
    }

    // =========================================================================
    // L-CACHE-1: Cache hit — inner client NOT called
    // =========================================================================

    [Fact]
    public async Task GetJobDetailsAsync_CacheHit_ReturnsFromCacheAndSkipsInner()
    {
        // Arrange: cache has metadata for this key
        _cache.GetMetadataAsync(Arg.Is<string>(k => k.Contains(JobId)), Arg.Any<CancellationToken>())
            .Returns("{\"Name\":\"cached-job\"}");

        // Act
        var result = await _sut.GetJobDetailsAsync(JobId);

        // Assert: inner client was NOT called
        await _inner.DidNotReceive().GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListWorkItemsAsync_CacheHit_ReturnsFromCacheAndSkipsInner()
    {
        _cache.GetMetadataAsync(Arg.Is<string>(k => k.Contains(JobId)), Arg.Any<CancellationToken>())
            .Returns("[{\"Name\":\"wi1\"}]");

        var result = await _sut.ListWorkItemsAsync(JobId);

        await _inner.DidNotReceive().ListWorkItemsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetWorkItemDetailsAsync_CacheHit_ReturnsFromCacheAndSkipsInner()
    {
        _cache.GetMetadataAsync(Arg.Is<string>(k => k.Contains(WorkItem)), Arg.Any<CancellationToken>())
            .Returns("{\"ExitCode\":0}");

        var result = await _sut.GetWorkItemDetailsAsync(WorkItem, JobId);

        await _inner.DidNotReceive().GetWorkItemDetailsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListWorkItemFilesAsync_CacheHit_ReturnsFromCacheAndSkipsInner()
    {
        _cache.GetMetadataAsync(Arg.Is<string>(k => k.Contains(WorkItem)), Arg.Any<CancellationToken>())
            .Returns("[{\"Name\":\"file1.txt\",\"Link\":\"https://example.com\"}]");

        var result = await _sut.ListWorkItemFilesAsync(WorkItem, JobId);

        await _inner.DidNotReceive().ListWorkItemFilesAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetConsoleLogAsync_CacheHit_CompletedJob_ReturnsFromCacheAndSkipsInner()
    {
        // Arrange: job is completed, and artifact is cached
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetArtifactAsync(Arg.Is<string>(k => k.Contains("console")), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("cached log")));

        var result = await _sut.GetConsoleLogAsync(WorkItem, JobId);

        await _inner.DidNotReceive().GetConsoleLogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetFileAsync_CacheHit_ReturnsFromCacheAndSkipsInner()
    {
        _cache.GetArtifactAsync(Arg.Is<string>(k => k.Contains("testfile.binlog")), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await _sut.GetFileAsync("testfile.binlog", WorkItem, JobId);

        await _inner.DidNotReceive().GetFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    // =========================================================================
    // L-CACHE-2: Cache miss — inner client called, result stored
    // =========================================================================

    [Fact]
    public async Task GetJobDetailsAsync_CacheMiss_CallsInnerAndStoresResult()
    {
        // Arrange: cache returns null (miss)
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)null);

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("fresh-job");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z");
        _inner.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        // Act
        var result = await _sut.GetJobDetailsAsync(JobId);

        // Assert: inner client was called
        await _inner.Received(1).GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>());
        // Assert: result was stored in cache
        await _cache.Received(1).SetMetadataAsync(
            Arg.Is<string>(k => k.Contains(JobId)),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListWorkItemsAsync_CacheMiss_CallsInnerAndStoresResult()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");
        _inner.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var result = await _sut.ListWorkItemsAsync(JobId);

        await _inner.Received(1).ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>());
        await _cache.Received(1).SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetConsoleLogAsync_CacheMiss_CompletedJob_CallsInnerAndStoresArtifact()
    {
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(true);

        // First GetArtifactAsync returns null (cache miss), second returns the stored stream
        var storedStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("live log content"));
        _cache.GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Stream?)null, storedStream);

        // GetMetadataAsync needed for the internal GetJobDetailsAsync call in IsJobCompletedAsync path
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        _inner.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("live log content")));

        var result = await _sut.GetConsoleLogAsync(WorkItem, JobId);

        await _inner.Received(1).GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>());
        await _cache.Received(1).SetArtifactAsync(
            Arg.Is<string>(k => k.Contains("console")),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    // =========================================================================
    // L-CACHE-3: TTL selection — running vs completed
    // =========================================================================

    [Fact]
    public async Task GetJobDetailsAsync_RunningJob_Uses15SecondTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)null); // unknown → treat as running until details fetched

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("running-job");
        jobDetails.Finished.Returns((string?)null); // not finished = running
        _inner.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        await _sut.GetJobDetailsAsync(JobId);

        await _cache.Received().SetMetadataAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            TimeSpan.FromSeconds(15),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetJobDetailsAsync_CompletedJob_Uses4HourTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((bool?)null);

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("done-job");
        jobDetails.Finished.Returns("2025-07-18T10:30:00Z"); // finished = completed
        _inner.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        await _sut.GetJobDetailsAsync(JobId);

        await _cache.Received().SetMetadataAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            TimeSpan.FromHours(4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListWorkItemsAsync_RunningJob_Uses15SecondTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(false); // running

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");
        _inner.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        await _sut.ListWorkItemsAsync(JobId);

        await _cache.Received().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromSeconds(15), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListWorkItemsAsync_CompletedJob_Uses4HourTtl()
    {
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(true); // completed

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");
        _inner.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        await _sut.ListWorkItemsAsync(JobId);

        await _cache.Received().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromHours(4), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListWorkItemFilesAsync_RunningJob_Uses30SecondTtl()
    {
        // Per design: ListWorkItemFilesAsync uses 30s TTL for running jobs (not 15s)
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(false);

        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("console.log");
        _inner.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemFile> { file });

        await _sut.ListWorkItemFilesAsync(WorkItem, JobId);

        await _cache.Received().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            TimeSpan.FromSeconds(30), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // L-CACHE-4: Console log bypass for running jobs — must NOT cache
    // =========================================================================

    [Fact]
    public async Task GetConsoleLogAsync_RunningJob_DoesNotCache()
    {
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(false); // running

        _inner.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("streaming log")));

        var result = await _sut.GetConsoleLogAsync(WorkItem, JobId);

        // Assert: inner client was called directly (pass-through)
        await _inner.Received(1).GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>());
        // Assert: nothing stored in cache
        await _cache.DidNotReceive().SetArtifactAsync(
            Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task GetConsoleLogAsync_RunningJob_DoesNotCheckArtifactCache()
    {
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(false);

        _inner.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("live data")));

        await _sut.GetConsoleLogAsync(WorkItem, JobId);

        // Should not even attempt to read from artifact cache for running jobs
        await _cache.DidNotReceive().GetArtifactAsync(
            Arg.Is<string>(k => k.Contains("console")), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // L-CACHE-5: Console log cached for completed jobs — 1h TTL
    // =========================================================================

    [Fact]
    public async Task GetConsoleLogAsync_CompletedJob_CacheMiss_StoresArtifact()
    {
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Stream?)null);

        _inner.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("completed log")));

        await _sut.GetConsoleLogAsync(WorkItem, JobId);

        await _cache.Received(1).SetArtifactAsync(
            Arg.Is<string>(k => k.Contains("console")),
            Arg.Any<Stream>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConsoleLogAsync_CompletedJob_CacheHit_ReturnsStreamWithoutCallingInner()
    {
        var cachedStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("cached completed log"));
        _cache.IsJobCompletedAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(true);
        _cache.GetArtifactAsync(Arg.Is<string>(k => k.Contains("console")), Arg.Any<CancellationToken>())
            .Returns(cachedStream);

        var result = await _sut.GetConsoleLogAsync(WorkItem, JobId);

        await _inner.DidNotReceive().GetConsoleLogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);

        // Verify we can read the cached content
        using var reader = new StreamReader(result);
        var content = await reader.ReadToEndAsync();
        Assert.Equal("cached completed log", content);
    }

    // =========================================================================
    // L-CACHE-10: Cache disabled when max size = 0
    // =========================================================================

    [Fact]
    public async Task GetJobDetailsAsync_CacheDisabled_PassesThroughToInner()
    {
        // Arrange: create decorator with CacheOptions.MaxSizeBytes = 0
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingHelixApiClient(_inner, disabledCache, opts);

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("uncached-job");
        _inner.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        // Act
        var result = await sut.GetJobDetailsAsync(JobId);

        // Assert: inner client called, cache NOT consulted
        await _inner.Received(1).GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().SetMetadataAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListWorkItemsAsync_CacheDisabled_PassesThroughToInner()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingHelixApiClient(_inner, disabledCache, opts);

        var wi = Substitute.For<IWorkItemSummary>();
        wi.Name.Returns("wi1");
        _inner.ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(new List<IWorkItemSummary> { wi });

        var result = await sut.ListWorkItemsAsync(JobId);

        await _inner.Received(1).ListWorkItemsAsync(JobId, Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConsoleLogAsync_CacheDisabled_PassesThroughToInner()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingHelixApiClient(_inner, disabledCache, opts);

        _inner.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("direct log")));

        var result = await sut.GetConsoleLogAsync(WorkItem, JobId);

        await _inner.Received(1).GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileAsync_CacheDisabled_PassesThroughToInner()
    {
        var disabledCache = Substitute.For<ICacheStore>();
        var opts = new CacheOptions { MaxSizeBytes = 0 };
        var sut = new CachingHelixApiClient(_inner, disabledCache, opts);

        _inner.GetFileAsync("test.binlog", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));

        var result = await sut.GetFileAsync("test.binlog", WorkItem, JobId);

        await _inner.Received(1).GetFileAsync("test.binlog", WorkItem, JobId, Arg.Any<CancellationToken>());
        await disabledCache.DidNotReceive().GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // =========================================================================
    // Constructor validation
    // =========================================================================

    // =========================================================================
    // Constructor — Ripley's impl does not null-guard (design choice)
    // but we verify the disabled-cache path works with MaxSizeBytes=0
    // =========================================================================

    [Fact]
    public async Task Constructor_MaxSizeZero_DisablesCache()
    {
        var disabledOpts = new CacheOptions { MaxSizeBytes = 0 };
        var disabledSut = new CachingHelixApiClient(_inner, _cache, disabledOpts);

        var jobDetails = Substitute.For<IJobDetails>();
        jobDetails.Name.Returns("direct-job");
        _inner.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>())
            .Returns(jobDetails);

        await disabledSut.GetJobDetailsAsync(JobId);

        // Cache is never consulted when disabled
        await _cache.DidNotReceive().GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
