using System.Net;
using System.Text;
using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using HelixTool.Core.Delivery;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class AzdoBuildSetQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), ".test-runtime", "buildsets", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateTimeOffset Capture = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    private static AzdoBuild Build(int id, string result = "failed") => new()
    {
        Id = id, BuildNumber = "run-" + id, Status = "completed", Result = result, Reason = "pullRequest",
        SourceBranch = "refs/pull/42/merge", QueueTime = Capture.AddMinutes(-id), FinishTime = Capture.AddMinutes(-id + 1),
        Definition = new() { Id = 3, Name = "runtime", Path = "\\runtime" },
        Repository = new() { Id = "dotnet/runtime", Type = "GitHub", Name = "dotnet/runtime" }, Tags = ["A,B", "Exact"]
    };
    private static HttpResponseMessage Json(object value, string? token = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        if (token is not null) response.Headers.Add("x-ms-continuationtoken", token);
        return response;
    }
    private static Dictionary<string, string> Query(Uri uri) => uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "");
    private (CachingAzdoApiClient Client, SqliteCacheStore Store, CacheOptions Options, Handler Handler) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new CacheOptions { CacheRoot = Path.Combine(_root, "cache") };
        var store = new SqliteCacheStore(options); _disposables.Add(store);
        var handler = new Handler(respond); var http = new HttpClient(handler); _disposables.Add(http);
        var token = Substitute.For<IAzdoTokenAccessor>();
        var api = new AzdoApiClient(http, token, options);
        return (new CachingAzdoApiClient(api, store, options, token), store, options, handler);
    }
    private static AzdoBuildFilter PrFilter() => new() { PrNumber = "42", RepositoryId = "dotnet/runtime", RepositoryType = "GitHub", MaxTime = Capture };
    private AzdoBuildCollector Collector(IAzdoApiClient client, ICacheStore store, CacheOptions options)
    {
        var token = Substitute.For<IAzdoTokenAccessor>();
        token.AuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AzdoAuthStatus { Path = "anonymous" });
        var helix = Substitute.For<IHelixApiClient>();
        var http = new HttpClient(); _disposables.Add(http);
        return new(new AzdoService(client, null, cacheOptions: options), client,
            new HelixService(helix, http), helix, token, Substitute.For<IHelixTokenAccessor>(), options, store);
    }

    private static HttpResponseMessage EmptyBuildEvidence(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.EndsWith("timeline", StringComparison.Ordinal)
            ? Json(new { id = "timeline", records = new[] { new { id = "00000000-0000-0000-0000-000000000001",
                name = "Build", type = "Stage", state = "completed", result = "succeeded" } } })
            : int.TryParse(request.RequestUri!.Segments.Last(), out var id) ? Json(Build(id)) : Json(new { value = Array.Empty<object>() });

    [Fact]
    public async Task ProviderFiltersAreForwarded_AndCommaTagsAreFilteredLocally()
    {
        var c = Client(request =>
        {
            var q = Query(request.RequestUri!);
            Assert.Equal("failed", q["resultFilter"]); Assert.Equal("pullRequest", q["reasonFilter"]);
            Assert.Equal("dotnet/runtime", q["repositoryId"]); Assert.Equal("GitHub", q["repositoryType"]);
            Assert.Equal("refs/pull/42/merge", q["branchName"]); Assert.False(q.ContainsKey("tagFilters"));
            Assert.Equal("run-*", q["buildNumber"]);
            return Json(new { value = new[] { Build(1), Build(2) with { Tags = ["Exact"] } }, count = 9999 });
        });
        var result = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { Result = "failed", Reason = "pullRequest", Tags = ["A,B"], BuildNumber = "run-*" } });
        Assert.Single(result.Results); Assert.Equal(1, result.Total); Assert.True(result.SourceComplete);
        Assert.Contains("resultFilter=failed", c.Handler.Urls.Single());
    }

    [Fact]
    public async Task LocalGlobFindsLaterPages_AndInteractiveTopIsNotACollectionQuota()
    {
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? Json(new { value = Enumerable.Range(201, 150).Select(i => Build(i) with { BuildNumber = "selected-" + i }) })
            : Json(new { value = Enumerable.Range(1, 200).Select(i => Build(i)) }, "next"));
        var result = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { Top = 120, BuildNumber = "*selected*" } });
        Assert.Equal(120, result.Count); Assert.Equal(150, result.Total); Assert.Equal(350, result.Scanned);
        Assert.Equal(201, result[0].Id); Assert.Equal(2, c.Handler.Urls.Count); Assert.False(result.Complete);
    }

    [Fact]
    public async Task DefinitionExactNamesUnionDuplicates_AndZeroIsCompleteEmpty()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("definitions", StringComparison.Ordinal)
            ? Json(new { value = new[] { new { id = 3, name = "runtime", path = "\\A" }, new { id = 4, name = "runtime", path = "\\B" }, new { id = 5, name = "runtime-extra", path = "\\C" } } })
            : Json(new { value = new[] { Build(1) } }));
        var result = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime" } });
        Assert.Equal(new[] { 3, 4 }, result.ResolvedDefinitions.Select(d => d.Id));
        Assert.Contains("definitions=3,4", c.Handler.Urls.Last());
        var empty = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "missing" } });
        Assert.True(empty.Complete); Assert.Equal(0, empty.Total);
    }

    [Fact]
    public async Task CheckpointResumesOriginalCeiling_AndDoesNotPublishIncompleteAllKey()
    {
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? Json(new { value = Enumerable.Range(201, 3).Select(i => Build(i)) })
            : Json(new { value = Enumerable.Range(1, 200).Select(i => Build(i)) }, "next"));
        var request = new AzdoBuildQueryRequest { Filter = PrFilter() with { MaxTime = null }, All = true, MaxDiscoveryBuilds = 50 };
        var prefix = await c.Client.QueryBuildsAsync("o", "p", request);
        Assert.False(prefix.SourceComplete); Assert.Null(prefix.Total); Assert.Contains(":window:", prefix.CacheKey!);
        Assert.Null(await c.Store.GetMetadataAsync(prefix.CacheKey!.Replace(":window:0:50", ":all", StringComparison.Ordinal)));
        var complete = await c.Client.QueryBuildsAsync("o", "p", request with { MaxDiscoveryBuilds = null, Frozen = true, ResumeCacheKey = prefix.CacheKey });
        Assert.True(complete.Complete); Assert.Equal(203, complete.Count);
        Assert.Equal(prefix.EffectiveQuery.MaxTime, complete.EffectiveQuery.MaxTime);
        Assert.Equal(2, c.Handler.Urls.Count);
    }

    [Fact]
    public async Task ResumingWithoutIncreasingThresholdRetainsThePartialCacheKey()
    {
        var c = Client(_ => Json(new { value = Enumerable.Range(1, 200).Select(i => Build(i)) }, "next"));
        var request = new AzdoBuildQueryRequest { Filter = PrFilter(), All = true, MaxDiscoveryBuilds = 50 };
        var first = await c.Client.QueryBuildsAsync("o", "p", request);
        var resumed = await c.Client.QueryBuildsAsync("o", "p", request with { Frozen = true, ResumeCacheKey = first.CacheKey });
        Assert.False(resumed.SourceComplete);
        Assert.Equal(first.CacheKey, resumed.CacheKey);
        Assert.NotNull(await c.Store.GetMetadataIgnoringTtlAsync(resumed.CacheKey!));
        Assert.Single(c.Handler.Urls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    public async Task MissingResumeCheckpointDoesNotRediscoverDifferentMembership(string? checkpoint)
    {
        var c = Client(_ => Json(new { value = new[] { Build(99) } }));
        var key = AzdoCacheKeys.MetadataKey(c.Options, "o", "p", $"builds:v2:{AzdoBuildQueries.Hash(PrFilter())}:window:0:50");
        if (checkpoint is not null) await c.Store.SetMetadataAsync(key, checkpoint, TimeSpan.FromHours(4));
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Client.QueryBuildsAsync("o", "p",
            new() { Filter = PrFilter(), All = true, Frozen = true, ResumeCacheKey = key }));
        Assert.Empty(c.Handler.Urls);
    }

    [Fact]
    public async Task FreshDiscoveryDoesNotReplaceFrozenCheckpointOrItsBackingPages()
    {
        var firstCapture = true;
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? Json(new { value = new[] { Build(2) } })
            : firstCapture ? Json(new { value = new[] { Build(1) } }, "next") : Json(new { value = new[] { Build(99) } }));
        var request = new AzdoBuildQueryRequest { Filter = PrFilter(), All = true, MaxDiscoveryBuilds = 1 };
        var prefix = await c.Client.QueryBuildsAsync("o", "p", request);
        firstCapture = false;
        var fresh = await c.Client.QueryBuildsAsync("o", "p", request with { MaxDiscoveryBuilds = null });
        Assert.Equal(99, Assert.Single(fresh.Results).Id);
        var resumed = await c.Client.QueryBuildsAsync("o", "p",
            request with { MaxDiscoveryBuilds = null, Frozen = true, ResumeCacheKey = prefix.CacheKey });
        Assert.Equal(new[] { 1, 2 }, resumed.Results.Select(b => b.Id));
        Assert.Equal(prefix.CapturedAt, resumed.CapturedAt);
    }

    [Fact]
    public async Task DefinitionsFollowAllPagesAndPrTriggerRejectsAnotherPr()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("definitions", StringComparison.Ordinal)
            ? Query(request.RequestUri!).ContainsKey("continuationToken")
                ? Json(new { value = new[] { new { id = 4, name = "runtime", path = "\\B" } } })
                : Json(new { value = new[] { new { id = 3, name = "runtime", path = "\\A" } } }, "definitions-next")
            : Json(new { value = new[] { Build(1), Build(2) with { Definition = new() { Id = 4 }, TriggerInfo = new() { PrNumber = "43" } } } }));
        var result = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime" }, All = true });
        Assert.Equal(new[] { 3, 4 }, result.ResolvedDefinitions.Select(d => d.Id));
        Assert.Equal(1, Assert.Single(result.Results).Id);
        Assert.True(result.SourceComplete);
        Assert.Equal(3, c.Handler.Urls.Count);
    }

    [Fact]
    public async Task ScanThresholdCheckpointsWithoutClaimingExhaustionAndAllowLargeContinues()
    {
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? Json(new { value = new[] { Build(5001) } })
            : Json(new { value = Enumerable.Range(1, 5000).Select(i => Build(i, "succeeded")) }, "next"));
        var request = new AzdoBuildQueryRequest { Filter = PrFilter() with { Result = "failed" }, All = true, MaxScanBuilds = 5000 };
        var first = await c.Client.QueryBuildsAsync("o", "p", request);
        Assert.Empty(first.Results); Assert.False(first.SourceComplete); Assert.Null(first.Total);
        Assert.Equal(5000, first.Scanned);
        var resumed = await c.Client.QueryBuildsAsync("o", "p", request with { MaxScanBuilds = null, Frozen = true, ResumeCacheKey = first.CacheKey });
        Assert.Equal(5001, Assert.Single(resumed.Results).Id);
        Assert.True(resumed.Complete); Assert.Equal(5001, resumed.Scanned);
    }

    [Fact]
    public async Task DefaultBrowsingStopsAfterOneBatchAndContinuationExtendsTheSameCapturedView()
    {
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? Json(new { value = Enumerable.Range(201, 5).Select(i => Build(i)) })
            : Json(new { value = Enumerable.Range(1, 200).Select(i => Build(i)) }, "next"));
        var first = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() });
        Assert.Equal(20, first.Count); Assert.False(first.SourceComplete); Assert.Null(first.Total); Assert.Single(c.Handler.Urls);
        var following = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter(), Offset = 200, Limit = 20, ViewId = first.ViewId });
        Assert.Equal(5, following.Count); Assert.True(following.SourceComplete); Assert.Equal(205, following.Total);
        Assert.Equal(first.ViewId, following.ViewId); Assert.Equal(2, c.Handler.Urls.Count);
        Assert.NotNull(await c.Store.GetMetadataAsync(following.CacheKey!));
    }

    [Fact]
    public async Task RepeatedTokenIsARealAcquisitionError()
    {
        var c = Client(_ => Json(new { value = new[] { Build(1) } }, "cycle"));
        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter(), All = true }));
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, ex.Error.Kind); Assert.Equal(2, c.Handler.Urls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyRecognizedStatusOrderCompatibilityDropsProviderStatus(bool recognized)
    {
        var c = Client(request => Query(request.RequestUri!).ContainsKey("statusFilter")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(recognized ? "statusFilter is incompatible with queryOrder" : "generic bad request", Encoding.UTF8, "application/json") }
            : Json(new { value = new[] { Build(1), Build(2) with { Status = "inProgress" } } }));
        var query = new AzdoBuildQueryRequest { Filter = PrFilter() with { StatusFilter = "completed" } };
        if (!recognized) { await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.QueryBuildsAsync("o", "p", query)); Assert.Single(c.Handler.Urls); return; }
        var result = await c.Client.QueryBuildsAsync("o", "p", query);
        Assert.Single(result.Results); Assert.Equal("client", result.EffectiveQuery.StatusApplied);
        Assert.All(c.Handler.Urls, u => Assert.Contains("queueTimeDescending", u));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("evidence")]
    public async Task CollectionExportsAndReplaysExactAndCoveredSubsetWithoutCredentials(string scope)
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("definitions", StringComparison.Ordinal)
            ? Json(new { value = new[] { new { id = 3, name = "runtime", path = "\\A" } } })
            : request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
                ? Json(new { value = new[] { Build(1), Build(2, "succeeded"), Build(3) } })
                : request.RequestUri!.AbsolutePath.EndsWith("timeline", StringComparison.Ordinal)
                    ? Json(new { id = "timeline", records = new[] { new { id = "00000000-0000-0000-0000-000000000001", name = "Build", type = "Stage", state = "completed", result = "succeeded" } } })
                    : int.TryParse(request.RequestUri!.Segments.Last(), out var id) ? Json(Build(id)) : Json(new { value = Array.Empty<object>() }));
        var token = Substitute.For<IAzdoTokenAccessor>(); token.AuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AzdoAuthStatus { Path = "anonymous" });
        var helix = Substitute.For<IHelixApiClient>();
        using var http = new HttpClient();
        var service = new AzdoService(c.Client, null, cacheOptions: c.Options);
        var collector = new AzdoBuildCollector(service, c.Client, new HelixService(helix, http), helix, token, Substitute.For<IHelixTokenAccessor>(), c.Options, c.Store);
        var snapshot = Path.Combine(_root, "snapshot");
        var result = await collector.CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", DefinitionName = "runtime", PrNumber = "42", RepositoryId = "dotnet/runtime", RepositoryType = "GitHub", MaxTime = Capture },
            BuildScope = scope, Collection = new() { ExportPath = snapshot, ManifestPath = Path.Combine(_root, "manifest.json") }
        });
        Assert.True(result.Manifest.Complete, JsonSerializer.Serialize(new { result.Manifest.IncompleteDetails, failures = result.Manifest.Attempts.Where(a => a.Outcome is "failed" or "recorded_failure") }, AzdoBuildQueries.JsonOptions)); Assert.Equal(2, result.Manifest.SchemaVersion);
        Assert.Equal(3, result.Manifest.Builds!.Count); Assert.True(result.Manifest.Snapshot.Validated);
        var offlineOptions = new CacheOptions { CacheRoot = snapshot, EvalMode = true };
        using var offlineStore = new SqliteCacheStore(offlineOptions);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), offlineStore, offlineOptions);
        var all = await offline.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime" }, All = true });
        Assert.Equal(3, all.Count);
        var failed = await offline.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime", Result = "failed" }, Limit = 1, Projection = "compact" });
        Assert.Single(failed.Results); Assert.Equal(2, failed.Total); Assert.False(failed.Complete);
        var last = await offline.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime", Result = "failed" }, Offset = 1, Limit = 1, Projection = "compact", ViewId = failed.ViewId });
        Assert.Single(last.Results); Assert.False(last.Complete); Assert.Null(last.NextOffset);
        var wider = await Assert.ThrowsAsync<HlxAcquisitionException>(() => offline.QueryBuildsAsync("o", "p", new() { Filter = PrFilter() with { DefinitionName = "runtime", RepositoryId = "other" }, All = true }));
        Assert.Equal(AcquisitionErrorKind.NotInSnapshot, wider.Error.Kind); Assert.True(wider.Error.Resource.ContainsKey("recovery"));
    }

    [Fact]
    public async Task OversizedFullBuildUsesOwnedVerifiedFileInsteadOfSpilling()
    {
        var c = Client(_ => Json(new { value = new[] { Build(1) with { Properties = new Dictionary<string, JsonElement> { ["large"] = JsonSerializer.SerializeToElement(new string('x', 40000)) } } } }));
        var store = new FileEvidenceDeliveryStore(c.Options);
        var tools = new AzdoMcpTools(new AzdoService(c.Client, null, cacheOptions: c.Options), Substitute.For<IAzdoTokenAccessor>(), store);
        var result = await tools.Builds(org: "o", project: "p", maxTime: Capture, all: true);
        Assert.Empty(result.Results); Assert.True(result.Delivery!.Complete); Assert.NotNull(result.Recovery);
        Assert.True(McpPresentationBudget.MeasureAny(result) + 256 <= 12288);
        var text = await File.ReadAllTextAsync(result.Delivery.LocalPath);
        Assert.Contains(new string('x', 40000), text); Assert.Equal(AzdoBuildQueries.Digest(text), result.Delivery.Sha256);
    }

    [Fact]
    public async Task ExplicitAllTestsExceedTenThousandAndReplayThroughPagedIndex()
    {
        var c = Client(request =>
        {
            var q = Query(request.RequestUri!); var offset = int.Parse(q["$skip"], System.Globalization.CultureInfo.InvariantCulture);
            return Json(new { value = Enumerable.Range(offset + 1, Math.Min(1000, Math.Max(0, 10005 - offset)))
                .Select(i => new { id = i, outcome = i % 1000 == 0 ? "Failed" : "Passed" }) });
        });
        var rows = await c.Client.GetTestResultsAsync("o", "p", 1, int.MaxValue, "Passed,Failed");
        Assert.Equal(10005, rows.Count); Assert.Equal(10005, rows.Last().Id);
        Assert.Equal(11, c.Handler.Urls.Count);
        var failed = await c.Client.GetTestResultsAsync("o", "p", 1, int.MaxValue, "Failed"); Assert.Equal(10, failed.Count);
        Assert.Equal(11, c.Handler.Urls.Count);
        var snapshot = Path.Combine(_root, "test-snapshot");
        await SnapshotExporter.ExportAsync(c.Options.GetEffectiveCacheRoot(), snapshot);
        Assert.True((await SnapshotValidator.ValidateAsync(snapshot)).IsValid);
        var offlineOptions = new CacheOptions { CacheRoot = snapshot, EvalMode = true };
        using var offlineStore = new SqliteCacheStore(offlineOptions);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), offlineStore, offlineOptions);
        Assert.Equal(10005, (await offline.GetTestResultsAsync("o", "p", 1, int.MaxValue, "Passed,Failed")).Count);
        Assert.Equal(10, (await offline.GetTestResultsAsync("o", "p", 1, int.MaxValue, "Failed")).Count);
    }

    [Fact]
    public async Task InterruptedTestResultStreamingVerifiesCheckpointBeforeContinuing()
    {
        var interrupted = true;
        var c = Client(request =>
        {
            var offset = int.Parse(Query(request.RequestUri!)["$skip"], System.Globalization.CultureInfo.InvariantCulture);
            if (offset == 1000 && interrupted)
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied") };
            return Json(new { value = Enumerable.Range(offset + 1, offset == 0 ? 1000 : 1).Select(id => new { id, outcome = "Failed" }) });
        });
        await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        var checkpointKey = AzdoListCacheKeys.TestResultsWindow(c.Options, "o", "p", 7, "Failed", 0, int.MaxValue);
        using var checkpoint = JsonDocument.Parse((await c.Store.GetMetadataIgnoringTtlAsync(checkpointKey))!);
        var pageKey = checkpoint.RootElement.GetProperty("pages")[0].GetProperty("key").GetString()!;
        await c.Store.SetMetadataAsync(pageKey, "[]", TimeSpan.FromHours(4));
        interrupted = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        Assert.Equal(2, c.Handler.Urls.Count);
        Assert.Null(await c.Store.GetMetadataIgnoringTtlAsync(AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed")));
    }

    [Fact]
    public async Task ExpiredCompletedTestResultsStartANewAcquisitionAtZero()
    {
        var hasFailure = true;
        var c = Client(_ => Json(new { value = hasFailure ? new[] { new { id = 1, outcome = "Failed" } } : [] }));
        Assert.Single(await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        var completeKey = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed");
        await c.Store.SetMetadataAsync(completeKey, (await c.Store.GetMetadataAsync(completeKey))!, TimeSpan.FromSeconds(-1));
        hasFailure = false;

        Assert.Empty(await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        Assert.Equal(new[] { "0", "0" }, c.Handler.Urls.Select(url => Query(new Uri(url))["$skip"]));
        var checkpointKey = AzdoListCacheKeys.TestResultsWindow(c.Options, "o", "p", 7, "Failed", 0, int.MaxValue);
        using var checkpoint = JsonDocument.Parse((await c.Store.GetMetadataAsync(checkpointKey))!);
        Assert.True(checkpoint.RootElement.GetProperty("sourceComplete").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedTestResultsContinueOnlyWhileCheckpointIsFresh(bool expired)
    {
        var firstAcquisition = true;
        var c = Client(request =>
        {
            var offset = int.Parse(Query(request.RequestUri!)["$skip"], System.Globalization.CultureInfo.InvariantCulture);
            if (firstAcquisition && offset > 0)
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied") };
            return Json(new { value = firstAcquisition ? Enumerable.Range(1, 1000).Select(id => new { id, outcome = "Failed" }) : [] });
        });
        await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        var checkpointKey = AzdoListCacheKeys.TestResultsWindow(c.Options, "o", "p", 7, "Failed", 0, int.MaxValue);
        if (expired)
            await c.Store.SetMetadataAsync(checkpointKey, (await c.Store.GetMetadataAsync(checkpointKey))!, TimeSpan.FromSeconds(-1));
        firstAcquisition = false;

        var rows = await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed");
        Assert.Equal(expired ? 0 : 1000, rows.Count);
        Assert.Equal(expired ? "0" : "1000", Query(new Uri(c.Handler.Urls.Last()))["$skip"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, AcquisitionErrorKind.AccessDenied)]
    [InlineData(HttpStatusCode.NotFound, AcquisitionErrorKind.NotFound)]
    public async Task StreamedTestResultFailureIsRecordedAndReplayedFromSnapshot(HttpStatusCode status, AcquisitionErrorKind kind)
    {
        var c = Client(_ => new(status) { Content = new StringContent("Provider denied test results") });
        var live = await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        Assert.Equal(kind, live.Error.Kind);
        var key = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed");
        Assert.Equal(kind, (await c.Store.GetAcquisitionErrorAsync(key))?.Kind);
        var snapshot = Path.Combine(_root, "failure-snapshot");
        await SnapshotExporter.ExportAsync(c.Options.GetEffectiveCacheRoot(), snapshot);
        var offlineOptions = c.Options with { CacheRoot = snapshot, EvalMode = true };
        using var offlineStore = new SqliteCacheStore(offlineOptions);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), offlineStore, offlineOptions);

        var replay = await Assert.ThrowsAsync<HlxAcquisitionException>(() => offline.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed"));
        Assert.Equal(kind, replay.Error.Kind);
        Assert.True(replay.Error.Replayed);
        Assert.Equal("snapshot", replay.Error.Source);
        Assert.Single(c.Handler.Urls);
    }

    [Fact]
    public async Task CoveredReplayRejectsDescriptorThatNoLongerMatchesManifestCapture()
    {
        var c = Client(_ => Json(new { value = new[] { Build(1) } }));
        var source = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = PrFilter(), All = true });
        var json = (await c.Store.GetMetadataIgnoringTtlAsync(source.CacheKey!))!;
        var index = JsonSerializer.Deserialize<AzdoBuildQuerySource>(json, AzdoBuildQueries.JsonOptions)!;
        var manifestDirectory = Path.Combine(c.Options.GetEffectiveCacheRoot(), "manifest");
        Directory.CreateDirectory(manifestDirectory);
        await File.WriteAllTextAsync(Path.Combine(manifestDirectory, "hlx-collect-manifest.json"),
            JsonSerializer.Serialize(new CollectManifest
            {
                SchemaVersion = 2, Kind = "hlx.collect.azdo-builds",
                Source = new()
                {
                    Org = "o", Project = "p", Query = source.Query, EffectiveQuery = source.EffectiveQuery, QueryHash = source.QueryHash,
                    Discovery = new(source.CacheKey!, true, 1, 1, 1, 1, source.CapturedAt, source.ViewId)
                }
            }, AzdoBuildQueries.JsonOptions));
        await c.Store.SetMetadataAsync(source.CacheKey!, JsonSerializer.Serialize(index with { CapturedAt = index.CapturedAt.AddMinutes(1) },
            AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4));
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), c.Store,
            c.Options with { EvalMode = true, CacheRoot = c.Options.GetEffectiveCacheRoot() });
        await Assert.ThrowsAsync<InvalidDataException>(() => offline.QueryBuildsAsync("o", "p",
            new() { Filter = PrFilter() with { Result = "failed" }, All = true }));
        Assert.Single(c.Handler.Urls);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("evidence")]
    public async Task FailedBuildDoesNotDropOtherMembersAndManifestRoundTrips(string scope)
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = new[] { Build(1), Build(2) } })
            : request.RequestUri!.AbsolutePath.EndsWith("/builds/1", StringComparison.Ordinal)
                ? new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied") }
                : EmptyBuildEvidence(request));
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture }, BuildScope = scope,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), RetryCount = 1 }
        });
        Assert.False(result.Manifest.Complete); Assert.Equal(2, result.Manifest.ExitCode);
        Assert.Collection(result.Manifest.Builds!,
            member => { Assert.Equal(1, member.BuildId); Assert.False(member.Complete); Assert.Equal(2, member.ExitCode); },
            member => { Assert.Equal(2, member.BuildId); Assert.True(member.Complete); Assert.Equal(0, member.ExitCode); });
        var persisted = JsonSerializer.Deserialize<CollectManifest>(await File.ReadAllTextAsync(result.ManifestPath), AzdoBuildQueries.JsonOptions)!;
        Assert.Equal(2, persisted.SchemaVersion); Assert.Equal("hlx.collect.azdo-builds", persisted.Kind);
        Assert.Equal(2, persisted.Builds!.Count);
        Assert.All(persisted.Attempts.Where(a => a.Id.StartsWith("build:", StringComparison.Ordinal)),
            a => Assert.True(a.Resource.ContainsKey("buildId")));
        Assert.Equal(persisted.Attempts.Count, persisted.Attempts.Select(a => a.Id).Distinct().Count());
        Assert.DoesNotContain(Directory.EnumerateFiles(c.Options.GetEffectiveCacheRoot(), "*", SearchOption.AllDirectories),
            file => Path.GetFileName(file).IndexOfAny([':', '<', '>', '|', '?', '*']) >= 0);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task OptionalBytePhaseFailureDoesNotDropLaterBuilds(bool hardError, int exitCode)
    {
        var c = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal))
                return Json(new { value = new[] { Build(1), Build(2) } });
            if (request.RequestUri!.AbsolutePath.EndsWith("/test/runs", StringComparison.Ordinal) &&
                Query(request.RequestUri!)["buildUri"].EndsWith("/1", StringComparison.Ordinal))
            {
                if (hardError) throw new InvalidOperationException("private diagnostic not for the manifest");
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied") };
            }
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestScope = "none",
                DownloadTestAttachments = "*", RetryCount = 1 }
        });
        Assert.Equal(2, result.Manifest.Builds!.Count);
        Assert.Contains(c.Handler.Urls, url => new Uri(url).AbsolutePath.EndsWith("/test/runs", StringComparison.Ordinal) &&
            Query(new Uri(url))["buildUri"].EndsWith("/2", StringComparison.Ordinal));
        Assert.Contains(result.Manifest.Attempts, a => a.Id.StartsWith("build:1:", StringComparison.Ordinal) &&
            a.Phase == "azdo_bytes" && a.Error?.Kind == (hardError ? AcquisitionErrorKind.TransportError : AcquisitionErrorKind.AccessDenied));
        Assert.False(result.Manifest.Builds![0].Complete);
        Assert.Equal(exitCode, result.Manifest.Builds![0].ExitCode);
        Assert.Equal(exitCode, result.Manifest.ExitCode);
        Assert.DoesNotContain("private diagnostic", await File.ReadAllTextAsync(result.ManifestPath));
    }

    [Fact]
    public async Task ArtifactByteSelectionUsesEachMembersBuildUrl()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = new[] { Build(1), Build(2) } }) : EmptyBuildEvidence(request));
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), DownloadAzdoArtifacts = "*" }
        });
        Assert.Equal(2, result.Manifest.ExitCode);
        Assert.Equal(2, result.Manifest.Attempts.Count(a => a.Phase == "azdo_bytes" && a.Skip?.Kind == "no_matches"));
        Assert.DoesNotContain(result.Manifest.IncompleteDetails, d => d.Code == "collector_hard_error");
    }

    [Fact]
    public async Task SetAttachmentAllowanceOverridesDefaultPerBuildPlanningThreshold()
    {
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 7, totalTests = 1001 } } });
            if (path.EndsWith("/results", StringComparison.Ordinal))
            {
                var skip = Query(request.RequestUri!).TryGetValue("$skip", out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                return Json(new { value = Enumerable.Range(skip + 1, Math.Min(1000, Math.Max(0, 1001 - skip)))
                    .Select(id => new { id, outcome = "Failed" }) });
            }
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture },
            MaxSetTestAttachments = 1001,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), RetryCount = 1 }
        });
        Assert.True(result.Manifest.Complete, JsonSerializer.Serialize(result.Manifest.IncompleteDetails));
        Assert.Equal(1001, result.Manifest.Policy.Caps.MaxTestAttachments);
        Assert.Equal(1001, result.Manifest.Attempts.Count(a => a.Operation == "list_test_attachments"));
    }

    [Fact]
    public async Task ExplicitlyDisabledEvidencePhasesAreNotIncomplete()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = new[] { Build(1), Build(2) } }) : EmptyBuildEvidence(request));
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestScope = "none", HelixScope = "none", LogScope = "none" }
        });
        Assert.True(result.Manifest.Complete); Assert.Equal(0, result.Manifest.ExitCode);
        Assert.All(result.Manifest.Builds!, member => Assert.True(member.Complete));
        Assert.Contains(result.Manifest.Attempts, a => a.Skip?.Kind == "policy_excluded");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalVerificationDetectsResultPageLossAfterAnEarlierMemberCompleted(bool priorPolicyDeferral)
    {
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1), Build(2) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal))
                return Query(request.RequestUri!)["buildUri"].EndsWith("/1", StringComparison.Ordinal)
                    ? Json(new { value = Enumerable.Range(7, priorPolicyDeferral ? 2 : 1).Select(id => new { id, totalTests = 1 }) })
                    : Json(new { value = Array.Empty<object>() });
            if (path.EndsWith("/results", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 1, outcome = "Failed" } } });
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            MaxSetTestResults = priorPolicyDeferral ? 1 : 10000,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestAttachmentScope = "none",
                MaxConcurrency = 1, AllowIncomplete = priorPolicyDeferral }
        }, message =>
        {
            if (message.StartsWith("Collecting build 2 ", StringComparison.Ordinal))
            {
                var pageKey = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed")[..^3] + "page:0";
                c.Store.SetMetadataAsync(pageKey, "[]", TimeSpan.FromHours(4)).GetAwaiter().GetResult();
            }
        });
        Assert.False(result.Manifest.Complete); Assert.Equal(2, result.Manifest.ExitCode);
        Assert.False(result.Manifest.Builds![0].Complete); Assert.True(result.Manifest.Builds![1].Complete);
        Assert.Contains(result.Manifest.Attempts, a => a.Id.StartsWith("build:1:", StringComparison.Ordinal) &&
            a.Operation == "list_test_results" && a.Error?.Kind == AcquisitionErrorKind.InvalidResponse);
        if (priorPolicyDeferral)
        {
            Assert.Contains(result.Manifest.IncompleteDetails, d => d.Code == "test_result_limit");
            Assert.Contains(result.Manifest.IncompleteDetails, d => d.Code == "fetch_failed");
        }
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public async Task PolicyOnlyDeferralHonorsAllowIncompleteWithoutPretendingComplete(bool allowIncomplete, int exitCode)
    {
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 7, totalTests = 2 } } });
            if (path.EndsWith("/results", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 1, outcome = "Failed" }, new { id = 2, outcome = "Failed" } } });
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture }, MaxSetTestAttachments = 1,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), AllowIncomplete = allowIncomplete }
        });
        Assert.False(result.Manifest.Complete); Assert.Equal(exitCode, result.Manifest.ExitCode);
        Assert.False(Assert.Single(result.Manifest.Builds!).Complete);
        Assert.Equal(exitCode, result.Manifest.Builds![0].ExitCode);
        Assert.Contains(result.Manifest.IncompleteDetails, d => d.Code == "test_attachment_limit" && d.Message.Contains("--allow-large", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Manifest.IncompleteDetails, d => d.Code == "evidence_incomplete");
    }

    [Fact]
    public void QueryHashAndCoveragePreserveCaseAndExcludeOutputCaps()
    {
        Assert.Equal(AzdoBuildQueries.Hash(PrFilter() with { Top = 1 }), AzdoBuildQueries.Hash(PrFilter() with { Top = 500, QueryOrder = "queueTimeDescending" }));
        Assert.Equal(AzdoBuildQueries.Hash(PrFilter() with { DefinitionId = 3 }), AzdoBuildQueries.Hash(PrFilter() with { DefinitionName = "runtime", DefinitionIds = [3] }));
        Assert.NotEqual(AzdoBuildQueries.Hash(PrFilter() with { Tags = ["a"] }), AzdoBuildQueries.Hash(PrFilter() with { Tags = ["A"] }));
        Assert.True(AzdoBuildQueries.Covers(PrFilter(), PrFilter() with { Result = "failed" }));
        Assert.False(AzdoBuildQueries.Covers(PrFilter() with { Result = "failed" }, PrFilter()));
        Assert.False(AzdoBuildQueries.Covers(PrFilter(), PrFilter() with { QueryOrder = "finishTimeDescending" }));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AzdoBuildSetQuery>("{\"schemaVersion\":1,\"top\":5}", AzdoBuildQueries.JsonOptions));
    }

    [Fact]
    public async Task BuildSetDefaultDeferralHasOneFlagRecoveryAndResumesTheOriginalMembership()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = Enumerable.Range(1, 60).Select(i => Build(i)) })
            : Json(Build(int.Parse(request.RequestUri!.Segments.Last(), System.Globalization.CultureInfo.InvariantCulture))));
        var token = Substitute.For<IAzdoTokenAccessor>(); token.AuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AzdoAuthStatus { Path = "anonymous" });
        var helix = Substitute.For<IHelixApiClient>(); using var http = new HttpClient();
        var collector = new AzdoBuildCollector(new AzdoService(c.Client, null, cacheOptions: c.Options), c.Client,
            new HelixService(helix, http), helix, token, Substitute.For<IHelixTokenAccessor>(), c.Options, c.Store);
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture }, BuildScope = "metadata",
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json") }
        };
        var deferred = await collector.CollectAzdoBuildsAsync(policy);
        Assert.False(deferred.Manifest.Complete); Assert.Equal(50, deferred.Manifest.Builds!.Count);
        var recovery = Assert.Single(deferred.Manifest.IncompleteDetails, detail => detail.Code == "build_set_threshold");
        var argv = Assert.IsAssignableFrom<IReadOnlyList<string>>(recovery.Resource!["argv"]);
        Assert.Equal(1, argv.Count(arg => arg == "--allow-large")); Assert.Contains("azdo-builds", argv); Assert.Contains("--resume", argv);
        var resumed = await collector.CollectAzdoBuildsAsync(policy with { Collection = policy.Collection with { Resume = true, AllowLarge = true } });
        Assert.True(resumed.Manifest.Complete); Assert.Equal(60, resumed.Manifest.Builds!.Count);
        Assert.Equal(deferred.Manifest.Source.EffectiveQuery!.MaxTime, resumed.Manifest.Source.EffectiveQuery!.MaxTime);
        Assert.Equal(1, c.Handler.Urls.Count(url => new Uri(url).AbsolutePath.EndsWith("builds", StringComparison.Ordinal)));
        Assert.Equal(50, resumed.Manifest.Attempts.Count(a => a.Outcome == "cached"));
        Assert.Equal(resumed.Manifest.Attempts.Count, resumed.Manifest.Attempts.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public async Task CompleteDiscoveryCaptureSurvivesRefreshingTheSameQueryBeforeResume()
    {
        var original = true;
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = original ? Enumerable.Range(1, 60).Select(i => Build(i)) : [Build(99)] })
            : Json(Build(int.Parse(request.RequestUri!.Segments.Last(), System.Globalization.CultureInfo.InvariantCulture))));
        var collector = Collector(c.Client, c.Store, c.Options);
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture },
            BuildScope = "metadata",
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json") }
        };
        var deferred = await collector.CollectAzdoBuildsAsync(policy);
        Assert.False(deferred.Manifest.Complete);
        Assert.Equal(50, deferred.Manifest.Builds!.Count);
        Assert.True(deferred.Manifest.Source.Discovery!.SourceComplete);
        var originalKey = deferred.Manifest.Source.Discovery.CacheKey;
        var originalJson = await c.Store.GetMetadataAsync(originalKey);
        var sharedKey = AzdoCacheKeys.MetadataKey(c.Options, "o", "p", $"builds:v2:{deferred.Manifest.Source.QueryHash}:all");
        await c.Store.SetMetadataAsync(sharedKey, (await c.Store.GetMetadataAsync(sharedKey))!, TimeSpan.FromSeconds(-1));
        original = false;
        var refreshed = await c.Client.QueryBuildsAsync("o", "p", new() { Filter = policy.Query.ToFilter(), All = true });
        Assert.Equal(99, Assert.Single(refreshed.Results).Id);
        Assert.Equal(originalJson, await c.Store.GetMetadataIgnoringTtlAsync(originalKey));

        var resumed = await collector.CollectAzdoBuildsAsync(policy with { Collection = policy.Collection with { Resume = true, AllowLarge = true } });
        Assert.True(resumed.Manifest.Complete);
        Assert.Equal(0, resumed.Manifest.ExitCode);
        Assert.Equal(Enumerable.Range(1, 60), resumed.Manifest.Builds!.Select(build => build.BuildId));
        Assert.Equal(deferred.Manifest.Source.Discovery.CapturedAt, resumed.Manifest.Source.Discovery!.CapturedAt);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("view")]
    [InlineData("membership")]
    [InlineData("missing")]
    public async Task MismatchedDiscoveryOffersExactFreshCaptureRecoveryThatSucceeds(string changed)
    {
        var original = true;
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
            ? Json(new { value = original ? Enumerable.Range(1, 60).Select(i => Build(i)) : [Build(99)] })
            : Json(Build(int.Parse(request.RequestUri!.Segments.Last(), System.Globalization.CultureInfo.InvariantCulture))));
        var collector = Collector(c.Client, c.Store, c.Options);
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", PrNumber = "42", MaxTime = Capture },
            BuildScope = "metadata",
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json") }
        };
        var deferred = await collector.CollectAzdoBuildsAsync(policy);
        var key = deferred.Manifest.Source.Discovery!.CacheKey;
        var source = JsonSerializer.Deserialize<AzdoBuildQuerySource>((await c.Store.GetMetadataAsync(key))!, AzdoBuildQueries.JsonOptions)!;
        var changedSource = changed switch
        {
            "capture" => source with { CapturedAt = source.CapturedAt.AddMinutes(1) },
            "view" => source with { ViewId = new string('f', 64) },
            _ => source with { SelectedCount = 1 }
        };
        if (changed == "membership")
        {
            var pageKey = AzdoCacheKeys.MetadataKey(c.Options, "o", "p", $"builds:v2:{source.QueryHash}:page:999");
            var pageJson = JsonSerializer.Serialize(Enumerable.Range(1, 59).Select(i => Build(i)).Append(Build(99)), AzdoBuildQueries.JsonOptions);
            await c.Store.SetMetadataAsync(pageKey, pageJson, TimeSpan.FromHours(4));
            changedSource = source with { PageKeys = [pageKey], PageHashes = new Dictionary<string, string> { [pageKey] = AzdoBuildQueries.Digest(pageJson) } };
        }
        await c.Store.SetMetadataAsync(key, changed == "missing" ? "null" : JsonSerializer.Serialize(changedSource, AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4));
        original = false;

        var rejected = await collector.CollectAzdoBuildsAsync(policy with { Collection = policy.Collection with { Resume = true, AllowLarge = true } });
        Assert.False(rejected.Manifest.Complete);
        Assert.Equal(2, rejected.Manifest.ExitCode);
        var recovery = Assert.Single(rejected.Manifest.IncompleteDetails, detail => detail.Code == "discovery_recapture_required");
        var argv = Assert.IsType<string[]>(recovery.Resource!["argv"]);
        Assert.DoesNotContain("--resume", argv);
        Assert.Contains("--allow-large", argv);
        Assert.Contains((string)recovery.Resource["command"]!, recovery.Message);
        var nextPath = argv[Array.IndexOf(argv, "--manifest") + 1];
        Assert.NotEqual(deferred.ManifestPath, nextPath);
        Assert.Single(c.Handler.Urls, url => new Uri(url).AbsolutePath.EndsWith("builds", StringComparison.Ordinal));

        var fresh = await collector.CollectAzdoBuildsAsync(policy with
        {
            Collection = policy.Collection with { ManifestPath = nextPath, Argv = argv.Skip(1).ToArray(), AllowLarge = true }
        });
        Assert.True(fresh.Manifest.Complete);
        Assert.Equal(0, fresh.Manifest.ExitCode);
        Assert.Equal(99, Assert.Single(fresh.Manifest.Builds!).BuildId);
        var persisted = JsonSerializer.Deserialize<CollectManifest>(await File.ReadAllTextAsync(rejected.ManifestPath), AzdoBuildQueries.JsonOptions)!;
        Assert.False(persisted.Complete);
    }

    [Fact]
    public async Task AllowLargeCollectionRetainsMemberIdsRatherThanLazyFullBuildObjects()
    {
        var c = Client(_ => throw new InvalidOperationException("Unexpected provider request"));
        var client = Substitute.For<IAzdoApiClient>();
        var builds = new RetentionCheckingBuildList();
        var filter = new AzdoBuildFilter();
        var hash = AzdoBuildQueries.Hash(filter);
        var key = AzdoCacheKeys.MetadataKey(c.Options, "o", "p", $"builds:v2:{hash}:all");
        await c.Store.SetMetadataAsync(key, JsonSerializer.Serialize(new AzdoBuildQuerySource { Query = filter, QueryHash = hash }, AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4));
        foreach (var id in Enumerable.Range(1, builds.Count))
            await c.Store.SetMetadataAsync(AzdoCacheKeys.MetadataKey(c.Options, "o", "p", $"build:{id}"),
                JsonSerializer.Serialize(Build(id), AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4));
        client.QueryBuildsAsync("o", "p", Arg.Any<AzdoBuildQueryRequest>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new AzdoBuildQueryResult { Results = builds, SourceComplete = true, Complete = true, CacheKey = key, Query = filter, QueryHash = hash });
        client.GetBuildAsync("o", "p", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Build(call.ArgAt<int>(2)));
        var result = await Collector(client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p" },
            BuildScope = "metadata",
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), AllowLarge = true }
        });

        Assert.True(result.Manifest.Complete, string.Join("; ", result.Manifest.IncompleteDetails.Select(detail => detail.Message)));
        Assert.Equal(builds.Count, result.Manifest.Builds!.Count);
        Assert.Equal(1, builds.Enumerations);
        Assert.True(builds.RetentionChecks > 0);
    }

    [Fact]
    public void LegacyHashesStayPinnedAndNewSuffixesParseInAuthPartitions()
    {
        Assert.Equal("9410869a1ad2", AzdoCacheKeys.HashFilter(new() { Top = 20 }));
        Assert.Equal("68b3a5757094", AzdoCacheKeys.HashFilter(new() { Top = 7, Branch = "refs/heads/Main", StatusFilter = "completed" }));
        var hash = new string('a', 64);
        foreach (var suffix in new[] { $"builds:v2:{hash}:all", $"builds:v2:{hash}:window:0:50", $"builds:v2:{hash}:page:1", $"builds:v2:{hash}:capture:{hash}", $"definitions:v1:{hash}:all", $"definitions:v1:{hash}:window:0:25" })
            Assert.True(AzdoCacheKeys.TryParse("azdo:abcdef12:o:p:" + suffix, out _));
    }

    [Fact]
    public async Task LargeDefinitionResolutionIsInteractiveButCollectorCanCheckpointBeforeBuildScan()
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("definitions", StringComparison.Ordinal)
            ? Json(new { value = Enumerable.Range(1, 26).Select(id => new { id, name = "runtime-" + id, path = "\\runtime" }) })
            : Json(new { value = new[] { Build(1) } }));
        var request = new AzdoBuildQueryRequest { Filter = PrFilter() with { DefinitionName = "runtime-*" }, All = true, MaxDefinitions = 25 };
        var deferred = await c.Client.QueryBuildsAsync("o", "p", request);
        Assert.False(deferred.SourceComplete); Assert.Null(deferred.Total); Assert.Single(c.Handler.Urls);
        var explicitQuery = await c.Client.QueryBuildsAsync("o", "p", request with { MaxDefinitions = null, ResumeCacheKey = deferred.CacheKey, Frozen = true, FrozenDefinitions = deferred.ResolvedDefinitions });
        Assert.True(explicitQuery.Complete); Assert.Equal(26, explicitQuery.ResolvedDefinitions.Count); Assert.Equal(2, c.Handler.Urls.Count);
    }

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables)) disposable.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Urls.Add(request.RequestUri!.AbsoluteUri); return Task.FromResult(respond(request)); }
    }

    private sealed class RetentionCheckingBuildList : IReadOnlyList<AzdoBuild>
    {
        private readonly List<WeakReference<AzdoBuild>> _created = [];
        public int Count => 60;
        public int Enumerations { get; private set; }
        public int RetentionChecks { get; private set; }
        public AzdoBuild this[int index] => throw new NotSupportedException();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private AzdoBuild Create(int id)
        {
            var build = Build(id) with { Properties = new Dictionary<string, JsonElement> { ["full"] = JsonSerializer.SerializeToElement(new string('x', 10000)) } };
            _created.Add(new(build));
            return build;
        }

        public IEnumerator<AzdoBuild> GetEnumerator()
        {
            Enumerations++;
            for (var id = 1; id <= Count; id++)
            {
                if (id % 10 == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Assert.All(_created.Take(Math.Max(0, _created.Count - 2)), reference => Assert.False(reference.TryGetTarget(out _),
                        "Collector retained full discovery builds instead of projecting member IDs."));
                    RetentionChecks++;
                }
                yield return Create(id);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
