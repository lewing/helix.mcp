using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using HelixTool.Core.Helix;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed partial class AzdoBuildSetQueryTests
{
    [Fact]
    public async Task ConcurrentBuildCapturesKeepTheirOwnImmutablePages()
    {
        var c = Client(_ => throw new InvalidOperationException("No HTTP expected."));
        var store = Substitute.For<ICacheStore>();
        store.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => c.Store.GetMetadataAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        store.GetMetadataIgnoringTtlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => c.Store.GetMetadataIgnoringTtlAsync(call.Arg<string>(), call.Arg<CancellationToken>()));
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = 0;
        store.SetMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var key = call.ArgAt<string>(0);
                if (key.Contains(":page:", StringComparison.Ordinal))
                {
                    if (Interlocked.Increment(ref writers) == 2) publishing.SetResult();
                    await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
                await c.Store.SetMetadataAsync(key, call.ArgAt<string>(1), call.Arg<TimeSpan>(), call.Arg<CancellationToken>());
            });
        var api = Substitute.For<IAzdoApiClient>();
        var sequence = 0;
        api.ListBuildsAsync("o", "p", Arg.Any<AzdoBuildFilter>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<AzdoBuild>>([Build(Interlocked.Increment(ref sequence))]));
        var executor = new AzdoBuildQueryExecutor(api, store, c.Options);
        var request = new AzdoBuildQueryRequest
        {
            Filter = PrFilter(), All = true, FreshCapture = true,
            Checkpoint = (_, _, _) => Task.CompletedTask
        };
        var captures = await Task.WhenAll(executor.ExecuteAsync("o", "p", request), executor.ExecuteAsync("o", "p", request));
        Assert.NotEqual(captures[0].CacheKey, captures[1].CacheKey);
        foreach (var capture in captures)
        {
            var replay = await executor.ExecuteAsync("o", "p", request with
            {
                FreshCapture = false, Frozen = true, ResumeCacheKey = capture.CacheKey
            });
            Assert.Equal(Assert.Single(capture.Results).Id, Assert.Single(replay.Results).Id);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("hash-mismatch")]
    public async Task LazyPageFailuresAreClassifiedCacheErrors(string? body)
    {
        var c = Client(_ => throw new InvalidOperationException("No HTTP expected."));
        var key = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed");
        var pageKey = key[..^3] + "page:damaged";
        if (body is not null) await c.Store.SetMetadataAsync(pageKey, body, TimeSpan.FromHours(4));
        var index = new AzdoPagedEvidence(true, true,
            [new(pageKey, 1, AzdoBuildQueries.Digest(body == "hash-mismatch" ? "[]" : body ?? ""))]);
        await c.Store.SetMetadataAsync(key, JsonSerializer.Serialize(index, AzdoBuildQueries.JsonOptions), TimeSpan.FromHours(4));
        var rows = await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed");
        var error = Assert.Throws<HlxAcquisitionException>(() => rows.ToArray());
        Assert.Equal(AcquisitionErrorKind.InvalidResponse, error.Error.Kind);
        Assert.Equal("cache", error.Error.Provider);
        Assert.Equal(pageKey, error.Error.Resource["key"]);
        Assert.Contains(pageKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshAndDerivedFailedPagesDoNotOverwriteEarlierLazyResults(bool deriveFailed)
    {
        var initial = true;
        var c = Client(_ => Json(new { value = initial
            ? new[] { new { id = 1, outcome = "Failed" }, new { id = 2, outcome = "Failed" } }
            : new[] { new { id = 1, outcome = "Passed" }, new { id = 3, outcome = "Failed" } } }));
        var first = await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed");
        var key = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed");
        var originalIndex = (await c.Store.GetMetadataAsync(key))!;
        initial = false;
        if (!deriveFailed) await c.Store.SetMetadataAsync(key, originalIndex, TimeSpan.FromSeconds(-1));
        var second = await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, deriveFailed ? "Passed,Failed" : "Failed");
        Assert.Equal(new[] { 1, 2 }, first.Select(row => row.Id));
        Assert.Equal(new[] { 1, 3 }, second.Select(row => row.Id));
        if (deriveFailed)
            Assert.Equal(3, Assert.Single(await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed")).Id);
        var original = JsonSerializer.Deserialize<AzdoPagedEvidence>(originalIndex, AzdoBuildQueries.JsonOptions)!;
        Assert.Equal(new[] { 1, 2 }, new AzdoPagedEvidenceList<AzdoTestResult>(c.Store, key, original).Select(row => row.Id));
    }

    [Fact]
    public async Task MissingResumeManifestIsRejectedBeforeDiscovery()
    {
        var c = Client(_ => throw new InvalidOperationException("Discovery must not run."));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p" },
            Collection = new() { Resume = true, ManifestPath = Path.Combine(_root, "missing.json") }
        }));
        Assert.Contains("Omit --resume to start a fresh capture", error.Message, StringComparison.Ordinal);
        Assert.Empty(c.Handler.Urls);
        Assert.False(File.Exists(Path.Combine(_root, "missing.json")));
    }

    [Theory]
    [InlineData("builds", false, 2)]
    [InlineData("builds", true, 0)]
    [InlineData("definitions", true, 0)]
    [InlineData("scan", true, 0)]
    public async Task BuildSetThresholdIsPolicyOnlyIncomplete(string threshold, bool allowIncomplete, int exitCode)
    {
        var c = Client(request => request.RequestUri!.AbsolutePath.EndsWith("definitions", StringComparison.Ordinal)
            ? Json(new { value = new[] { new { id = 3, name = "runtime" }, new { id = 4, name = "runtime" } } })
            : request.RequestUri!.AbsolutePath.EndsWith("builds", StringComparison.Ordinal)
                ? Json(new { value = new[] { Build(1), Build(2) } }, threshold == "scan" ? "next" : null)
                : EmptyBuildEvidence(request));
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", DefinitionName = threshold == "definitions" ? "runtime" : null,
                Result = threshold == "scan" ? "succeeded" : null, MaxTime = Capture },
            BuildScope = "metadata", MaxBuilds = threshold == "builds" ? 1 : 50,
            MaxDefinitions = threshold == "definitions" ? 1 : 25, MaxScanBuilds = threshold == "scan" ? 1 : 5000,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), AllowIncomplete = allowIncomplete }
        });
        Assert.False(result.Manifest.Complete);
        Assert.Equal(exitCode, result.Manifest.ExitCode);
        Assert.Contains(result.Manifest.IncompleteDetails, detail => detail.Code == "build_set_threshold");
    }

    [Theory]
    [InlineData(403, AcquisitionErrorKind.AccessDenied, 2)]
    [InlineData(408, AcquisitionErrorKind.Timeout, 2)]
    [InlineData(0, AcquisitionErrorKind.TransportError, 1)]
    public async Task DiscoveryFailurePreservesAcquisitionClassification(int status, AcquisitionErrorKind kind, int exitCode)
    {
        var c = Client(_ => status == 0 ? throw new InvalidOperationException("internal https://example.invalid/log?sig=private-signature")
            : new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("provider failure") });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p" }, BuildScope = "metadata",
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), AllowIncomplete = true,
                RetryCount = 2, RetryInitialDelay = TimeSpan.Zero, RetryMaxDelay = TimeSpan.Zero }
        });
        Assert.False(result.Manifest.Complete);
        Assert.Equal(exitCode, result.Manifest.ExitCode);
        Assert.Equal(kind, Assert.Single(result.Manifest.Attempts, attempt => attempt.Id == "azdo.builds").Error!.Kind);
        Assert.Contains(result.Manifest.IncompleteDetails, detail => detail.Code == (status == 0 ? "collector_hard_error" : "fetch_failed"));
        Assert.Equal(status == 408 ? 2 : 1, c.Handler.Urls.Count);
        if (status == 0)
        {
            Assert.Equal("hlx collect failed unexpectedly due to an unclassified internal error.",
                Assert.Single(result.Manifest.IncompleteDetails, detail => detail.Code == "collector_hard_error").Message);
            var persisted = await File.ReadAllTextAsync(result.ManifestPath);
            Assert.DoesNotContain("private-signature", persisted);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentMembersHelixBytesAreChargedOnceBeforeAzdoBytes(bool resume)
    {
        const string jobId = "d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a";
        const string workItem = "Runtime.Tests";
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/timeline", StringComparison.Ordinal))
                return Json(new { id = "timeline", records = new object[]
                {
                    new { id = "monitor", type = "Job", name = "Monitor Helix Jobs", state = "completed", result = "failed" },
                    new { id = "task", parentId = "monitor", type = "Task", name = "Monitor Helix Jobs", state = "completed",
                        result = "failed", issues = new[] { new { type = "warning",
                            message = $"Work item '{workItem}' in job 'runtime leg - queue ({jobId})' failed (Finished, exit code -3)." } } }
                } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 7, totalTests = 0 } } });
            if (path.EndsWith("/attachments", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 1, fileName = "azdo.bin" } } });
            if (path.EndsWith("/attachments/1", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[70]) };
            return EmptyBuildEvidence(request);
        });
        var helix = Substitute.For<IHelixApiClient>();
        var job = Substitute.For<IJobDetails>();
        job.Name.Returns(jobId); job.Finished.Returns("2026-09-30T12:00:00Z");
        helix.GetJobDetailsAsync(jobId, Arg.Any<CancellationToken>()).Returns(job);
        var detail = Substitute.For<IWorkItemDetails>();
        detail.State.Returns("Finished"); detail.ExitCode.Returns(-3);
        helix.GetWorkItemDetailsAsync(workItem, jobId, Arg.Any<CancellationToken>()).Returns(detail);
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns("helix.bin"); file.Link.Returns("https://helix.dot.net/files/helix.bin");
        helix.ListWorkItemFilesAsync(workItem, jobId, Arg.Any<CancellationToken>()).Returns(new[] { file });
        helix.GetConsoleLogAsync(workItem, jobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream("required"u8.ToArray())));
        helix.GetFileAsync("helix.bin", workItem, jobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[30])));
        var cachedHelix = new CachingHelixApiClient(helix, c.Store, c.Options);
        var token = Substitute.For<IAzdoTokenAccessor>();
        token.AuthStatusAsync(Arg.Any<CancellationToken>()).Returns(new AzdoAuthStatus { Path = "anonymous" });
        using var http = new HttpClient();
        var collector = new AzdoBuildCollector(new AzdoService(c.Client, cachedHelix, cacheOptions: c.Options), c.Client,
            new HelixService(cachedHelix, http), cachedHelix, token, Substitute.For<IHelixTokenAccessor>(), c.Options, c.Store);
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestScope = "none", LogScope = "none",
                DownloadHelixFiles = "*.bin", DownloadTestAttachments = "*.bin", MaxTotalBytes = 100, MaxTotalStreamBytes = 100 }
        };
        var result = await collector.CollectAzdoBuildsAsync(policy);
        if (resume)
        {
            helix.GetFileAsync("helix.bin", workItem, jobId, Arg.Any<CancellationToken>())
                .Returns<Task<Stream>>(_ => throw new InvalidOperationException("Previously collected Helix files must not be downloaded again."));
            result = await collector.CollectAzdoBuildsAsync(policy with { Collection = policy.Collection with { Resume = true } });
            var reused = Assert.Single(result.Manifest.Attempts, attempt => attempt.Operation == "download_helix_file");
            Assert.Equal("cached", reused.Outcome);
            Assert.StartsWith("build:1:helix.", reused.Id);
            Assert.DoesNotContain("build:1:build:1:", reused.Id);
            await helix.Received(1).GetFileAsync("helix.bin", workItem, jobId, Arg.Any<CancellationToken>());
            Assert.DoesNotContain(result.Manifest.IncompleteDetails, detail => detail.Code is "fetch_failed" or "collector_hard_error");
        }
        Assert.Equal(30, Assert.Single(result.Manifest.Attempts, attempt => attempt.Operation == "download_helix_file").Bytes);
        Assert.Equal(70, Assert.Single(result.Manifest.ByteDownloads!).Bytes);
        Assert.Contains(result.Manifest.Attempts, attempt => attempt.Operation == "download_azdo_bytes" && attempt.Bytes == 70 && attempt.Outcome is "ok" or "cached");
    }

    [Fact]
    public async Task DiscoveryFailureWhileResumingPreservesOriginalCapture()
    {
        var failResume = false;
        var c = Client(request => Query(request.RequestUri!).ContainsKey("continuationToken")
            ? failResume ? new(HttpStatusCode.Forbidden) { Content = new StringContent("forbidden") }
                : Json(new { value = new[] { Build(2) } })
            : Json(new { value = new[] { Build(1) } }, "next"));
        var request = new AzdoBuildQueryRequest
        {
            Filter = PrFilter() with { MaxTime = null }, All = true, MaxDiscoveryBuilds = 1,
            FreshCapture = true, Checkpoint = (_, _, _) => Task.CompletedTask
        };
        var first = await c.Client.QueryBuildsAsync("o", "p", request);
        Assert.False(first.SourceComplete);
        var capture = await c.Store.GetMetadataIgnoringTtlAsync(first.CacheKey!);
        var resume = request with { FreshCapture = false, MaxDiscoveryBuilds = null, Frozen = true, ResumeCacheKey = first.CacheKey };
        failResume = true;
        var failure = await Assert.ThrowsAsync<HlxAcquisitionException>(() => c.Client.QueryBuildsAsync("o", "p", resume));
        Assert.Equal(AcquisitionErrorKind.AccessDenied, failure.Error.Kind);
        Assert.Equal(capture, await c.Store.GetMetadataIgnoringTtlAsync(first.CacheKey!));
        Assert.Equal(AcquisitionErrorKind.AccessDenied, (await c.Store.GetAcquisitionErrorAsync(first.CacheKey + ":error"))!.Kind);
        failResume = false;
        var completed = await c.Client.QueryBuildsAsync("o", "p", resume);
        Assert.True(completed.SourceComplete);
        Assert.Equal(new[] { 1, 2 }, completed.Results.Select(row => row.Id));
        Assert.Equal(first.CapturedAt, completed.CapturedAt);
        Assert.Equal(first.EffectiveQuery.MaxTime, completed.EffectiveQuery.MaxTime);
        Assert.Equal(3, c.Handler.Urls.Count);
        Assert.All(c.Handler.Urls.Skip(1), url => Assert.Contains("continuationToken=next", url, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AllOutcomeCollectionRebuildsEvictedFailedIndexFromCapturedPages(bool resume)
    {
        var resultRequests = 0;
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal)) return Json(new { value = new[] { new { id = 7, totalTests = 2 } } });
            if (path.EndsWith("/results", StringComparison.Ordinal))
            {
                resultRequests++;
                return Json(new { value = new[] { new { id = 1, outcome = "Passed" }, new { id = 2, outcome = "Failed" } } });
            }
            return EmptyBuildEvidence(request);
        });
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestScope = "all", TestAttachmentScope = "none" }
        };
        var collector = Collector(c.Client, c.Store, c.Options);
        var first = await collector.CollectAzdoBuildsAsync(policy);
        Assert.True(first.Manifest.Complete, JsonSerializer.Serialize(first.Manifest.IncompleteDetails));
        var key = AzdoListCacheKeys.TestResultsComplete(c.Options, "o", "p", 7, "Failed");
        await c.Store.SetMetadataAsync(key, "[]", TimeSpan.FromSeconds(-1));
        await c.Store.EvictExpiredAsync();
        Assert.Null(await c.Store.GetMetadataIgnoringTtlAsync(key));
        var repeated = await collector.CollectAzdoBuildsAsync(policy with { Collection = policy.Collection with { Resume = resume } });
        Assert.True(repeated.Manifest.Complete, JsonSerializer.Serialize(repeated.Manifest.IncompleteDetails));
        Assert.Equal(0, repeated.Manifest.ExitCode);
        Assert.Equal(1, resultRequests);
        Assert.Equal(2, Assert.Single(await c.Client.GetTestResultsAsync("o", "p", 7, int.MaxValue, "Failed")).Id);
        Assert.Contains(repeated.Manifest.Attempts, attempt => attempt.Id == "build:1:azdo.test-results-derived:7:Failed" && attempt.Outcome == "ok");
    }

    [Theory]
    [InlineData("SELECTED", "MEMBERS")]
    [InlineData("SELECTED", "members")]
    [InlineData("selected", "MEMBERS")]
    [InlineData("selected", "members")]
    [InlineData("SeLeCtEd", "MeMbErS")]
    public async Task BuildSetArtifactByteScopeAndModeAreNormalizedBeforeBothPasses(string scope, string mode)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = new StreamWriter(zip.CreateEntry("logs/Build.binlog").Open());
            entry.Write("build log");
        }
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/artifacts", StringComparison.Ordinal))
                return Json(new { value = new[] { new { id = 10, name = "selected-artifact", resource = new
                    { type = "PipelineArtifact", downloadUrl = "https://dev.azure.com/o/p/archive?format=zip" } } } });
            if (path.EndsWith("/archive", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes.ToArray()) };
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), ArtifactByteScope = scope, ArtifactByteMode = mode }
        });
        Assert.True(result.Manifest.Complete, JsonSerializer.Serialize(result.Manifest.IncompleteDetails));
        var report = Assert.Single(result.Manifest.ByteDownloads!);
        var artifact = Assert.Single(report.Results);
        Assert.Equal("members", artifact.Format);
        Assert.Equal("logs/Build.binlog", Assert.Single(artifact.Members).MemberPath);
        Assert.Contains(result.Manifest.Attempts, attempt => attempt.Operation == "catalog_azdo_members");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemberThresholdRecoveryUsesFreshAggregateExportDestination(bool attachmentThreshold)
    {
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal))
                return Json(new { value = Enumerable.Range(7, attachmentThreshold ? 1 : 2).Select(id => new { id, totalTests = 2 }) });
            if (path.EndsWith("/results", StringComparison.Ordinal))
                return Json(new { value = new[] { new { id = 1, outcome = "Failed" }, new { id = 2, outcome = "Failed" } } });
            return EmptyBuildEvidence(request);
        });
        var export = Path.Combine(_root, "snapshot");
        var policy = new CollectBuildSetPolicy
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            MaxSetTestResults = attachmentThreshold ? 10000 : 1, MaxSetTestAttachments = attachmentThreshold ? 1 : 1000,
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), ExportPath = export, MaxConcurrency = 1 }
        };
        var collector = Collector(c.Client, c.Store, c.Options);
        var first = await collector.CollectAzdoBuildsAsync(policy);
        Assert.Equal(2, first.Manifest.ExitCode);
        Assert.True(first.Manifest.Snapshot.Exported);
        var skip = Assert.Single(first.Manifest.Attempts, attempt => attempt.Skip?.Kind == (attachmentThreshold ? "test_attachment_limit" : "test_result_limit")).Skip!;
        Assert.Contains("'azdo-builds'", skip.Message, StringComparison.Ordinal);
        Assert.Contains("'--allow-large'", skip.Message, StringComparison.Ordinal);
        Assert.Contains("'--resume'", skip.Message, StringComparison.Ordinal);
        var exportArgument = Regex.Match(skip.Message, "'--export' '([^']+)'");
        Assert.True(exportArgument.Success, skip.Message);
        var nextExport = exportArgument.Groups[1].Value;
        Assert.StartsWith(export + ".resumed-", nextExport);
        Assert.False(Directory.Exists(nextExport));
        var initialManifest = await File.ReadAllTextAsync(Path.Combine(export, "manifest", "hlx-collect-manifest.json"));
        var resumed = await collector.CollectAzdoBuildsAsync(policy with
        {
            Collection = policy.Collection with { AllowLarge = true, Resume = true, ExportPath = nextExport }
        });
        Assert.True(resumed.Manifest.Complete, JsonSerializer.Serialize(resumed.Manifest.IncompleteDetails));
        Assert.Equal(0, resumed.Manifest.ExitCode);
        Assert.True(resumed.Manifest.Snapshot.Validated);
        Assert.Equal(nextExport, resumed.SnapshotPath);
        Assert.Equal(initialManifest, await File.ReadAllTextAsync(Path.Combine(export, "manifest", "hlx-collect-manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialBytePhaseTransfersStillConsumeSetBudgets(bool streamBudget)
    {
        var c = Client(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("builds", StringComparison.Ordinal)) return Json(new { value = new[] { Build(1), Build(2) } });
            if (path.EndsWith("/test/runs", StringComparison.Ordinal))
                return Json(new { value = new[] { new { id = Query(request.RequestUri!)["buildUri"].EndsWith("/1", StringComparison.Ordinal) ? 7 : 8 } } });
            if (path.EndsWith("/attachments", StringComparison.Ordinal))
                return Json(new { value = new[] { new { id = 1, fileName = "azdo.bin" } } });
            if (path.EndsWith("/attachments/1", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[60]) };
            if (path.EndsWith("/7/results", StringComparison.Ordinal))
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("Access denied after attachment transfer.") };
            return EmptyBuildEvidence(request);
        });
        var result = await Collector(c.Client, c.Store, c.Options).CollectAzdoBuildsAsync(new()
        {
            Query = new() { Org = "o", Project = "p", MaxTime = Capture },
            Collection = new() { ManifestPath = Path.Combine(_root, "manifest.json"), TestScope = "none", HelixScope = "none",
                LogScope = "none", DownloadTestAttachments = "*.bin", MaxTotalBytes = streamBudget ? 1000 : 100,
                MaxTotalStreamBytes = streamBudget ? 100 : 1000, RetryCount = 1 }
        });
        Assert.Equal(2, result.Manifest.ExitCode);
        Assert.Contains(result.Manifest.Attempts, attempt => attempt.Resource.TryGetValue("buildId", out var id) && Equals(id, 1) &&
            attempt.Operation == "download_azdo_bytes" && attempt.Bytes == 60 && attempt.Outcome == "ok");
        Assert.Equal(60, result.Manifest.ByteDownloads!.Sum(report => report.Bytes));
        Assert.True(result.Manifest.ByteDownloads!.Sum(report => report.StreamedBytes) <= 100);
        Assert.Contains(result.Manifest.Attempts, attempt => attempt.Resource.TryGetValue("buildId", out var id) && Equals(id, 2) &&
            attempt.Operation == "download_azdo_bytes" && attempt.Outcome is not ("ok" or "cached"));
        Assert.DoesNotContain(result.Manifest.IncompleteDetails, detail => detail.Code == "collector_hard_error");
    }
}
