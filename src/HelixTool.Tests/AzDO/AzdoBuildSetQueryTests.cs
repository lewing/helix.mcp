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
    public void LegacyHashesStayPinnedAndNewSuffixesParseInAuthPartitions()
    {
        Assert.Equal("9410869a1ad2", AzdoCacheKeys.HashFilter(new() { Top = 20 }));
        Assert.Equal("68b3a5757094", AzdoCacheKeys.HashFilter(new() { Top = 7, Branch = "refs/heads/Main", StatusFilter = "completed" }));
        var hash = new string('a', 64);
        foreach (var suffix in new[] { $"builds:v2:{hash}:all", $"builds:v2:{hash}:window:0:50", $"builds:v2:{hash}:page:1", $"definitions:v1:{hash}:all", $"definitions:v1:{hash}:window:0:25" })
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
}
