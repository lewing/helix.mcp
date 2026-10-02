using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core.Pipeline;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Collect;
using HelixTool.Core.Helix;
using Microsoft.Data.Sqlite;
using Microsoft.DotNet.Helix.Client;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Collect;

public sealed partial class IndependentReviewRegressionTests
{
    private const int BuildId = 1621466;
    private const int RunId = 7001;
    private const string Org = "dnceng-public";
    private const string Project = "public";
    private const string JobId = "d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a";
    private const string WorkItem = "System.Diagnostics.Process.Tests";
    private static readonly string ConsoleKey = $"job:{JobId}:wi:{WorkItem}:console";

    [Theory]
    [InlineData("build", "io", "get_build", AcquisitionErrorKind.TransportError)]
    [InlineData("timeline", "http", "get_timeline", AcquisitionErrorKind.TransportError)]
    [InlineData("logs-list", "io", "list_build_logs", AcquisitionErrorKind.TransportError)]
    [InlineData("log", "io", "get_build_log", AcquisitionErrorKind.TransportError)]
    [InlineData("log", "http", "get_build_log", AcquisitionErrorKind.TransportError)]
    [InlineData("log", "timeout", "get_build_log", AcquisitionErrorKind.Timeout)]
    [InlineData("build", "timeout", "get_build", AcquisitionErrorKind.Timeout)]
    [InlineData("logs-list", "timeout", "list_build_logs", AcquisitionErrorKind.Timeout)]
    public async Task AzdoBodyReadFailure_IsClassifiedAtRealClientBoundary_IndepReview1(
        string endpoint, string exceptionKind, string operation, AcquisitionErrorKind expectedKind)
    {
        using var fixture = new Fixture();
        fixture.Handler.FaultEndpoint = endpoint;
        fixture.Handler.BodyException = exceptionKind;

        var error = await Assert.ThrowsAsync<HlxAcquisitionException>(async () =>
        {
            switch (endpoint)
            {
                case "build":
                    await fixture.Azdo.GetBuildAsync(Org, Project, BuildId);
                    break;
                case "timeline":
                    await fixture.Azdo.GetTimelineAsync(Org, Project, BuildId);
                    break;
                case "logs-list":
                    await fixture.Azdo.GetBuildLogsListAsync(Org, Project, BuildId);
                    break;
                case "log":
                    await fixture.Azdo.GetBuildLogAsync(Org, Project, BuildId, 11);
                    break;
                default:
                    Assert.Fail($"Unknown endpoint {endpoint}.");
                    break;
            }
        });

        AcquisitionAssertions.Error(error, expectedKind, "azdo", operation);
        AcquisitionAssertions.Resource(error.Error, "buildId", BuildId);
        if (endpoint == "log")
            AcquisitionAssertions.Resource(error.Error, "logId", 11);
        Assert.Equal(1, fixture.Handler.FaultResponses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collector_BodyReadFailuresRetryAndAlwaysWriteManifest_IndepReview1(bool recover)
    {
        using var fixture = new Fixture();
        fixture.Handler.FaultEndpoint = "log";
        fixture.Handler.FaultsRemaining = recover ? 1 : int.MaxValue;
        var consoleCalls = 0;
        fixture.Helix.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                consoleCalls++;
                return Task.FromResult<Stream>(recover && consoleCalls > 1
                    ? new MemoryStream(Encoding.UTF8.GetBytes("complete console\n"))
                    : new MidBodyFailureStream("partial console\n", new IOException("reset during console copy")));
            });

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy);

        Assert.True(File.Exists(result.ManifestPath));
        var azdo = Assert.Single(result.Manifest.Attempts, a => a.Operation == "get_build_log");
        var helix = Assert.Single(result.Manifest.Attempts, a => a.Operation == "get_helix_console_log");
        Assert.Equal(recover ? 1 : 3, fixture.Handler.FaultResponses);
        Assert.Equal(recover ? 2 : 3, consoleCalls);
        foreach (var attempt in new[] { azdo, helix })
        {
            Assert.Equal(recover ? 2 : 3, attempt.AttemptCount);
            Assert.Equal(recover ? "ok" : "failed", attempt.Outcome);
            if (!recover)
            {
                Assert.Equal(AcquisitionErrorKind.TransportError, attempt.Error?.Kind);
                Assert.Null(await fixture.Store.GetAcquisitionErrorAsync(attempt.CacheKey!));
            }
        }
        Assert.Equal(recover, result.Manifest.Complete);
        Assert.Equal(recover ? 0 : 2, result.Manifest.ExitCode);
        await using var cachedConsole = await fixture.Store.GetArtifactAsync(ConsoleKey);
        if (recover)
        {
            Assert.NotNull(cachedConsole);
            using var reader = new StreamReader(cachedConsole);
            Assert.Equal("complete console\n", await reader.ReadToEndAsync());
        }
        else
        {
            Assert.Null(cachedConsole);
        }
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(result.ManifestPath));
        Assert.Equal(result.Manifest.Complete, persisted.RootElement.GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData("job", HttpStatusCode.Forbidden, false, "get_helix_job")]
    [InlineData("job", HttpStatusCode.NotFound, false, "get_helix_job")]
    [InlineData("work-items", HttpStatusCode.Forbidden, false, "list_helix_work_items")]
    [InlineData("work-items", HttpStatusCode.NotFound, false, "list_helix_work_items")]
    [InlineData("work-item", HttpStatusCode.Forbidden, false, "get_helix_work_item")]
    [InlineData("work-item", HttpStatusCode.NotFound, false, "get_helix_work_item")]
    [InlineData("files", HttpStatusCode.Forbidden, false, "list_helix_work_item_files")]
    [InlineData("files", HttpStatusCode.NotFound, false, "list_helix_work_item_files")]
    [InlineData("console", HttpStatusCode.Forbidden, false, "get_helix_console_log")]
    [InlineData("console", HttpStatusCode.NotFound, false, "get_helix_console_log")]
    [InlineData("file", HttpStatusCode.Forbidden, false, "download_helix_file")]
    [InlineData("file", HttpStatusCode.NotFound, false, "download_helix_file")]
    [InlineData("files", HttpStatusCode.Forbidden, true, "list_helix_work_item_files")]
    [InlineData("files", HttpStatusCode.NotFound, true, "list_helix_work_item_files")]
    public async Task RealHelixClient_ClassifiesRecordsAndReplaysProviderFailures_IndepReview2(
        string endpoint, HttpStatusCode status, bool throwHttpException, string operation)
    {
        using var handler = new HelixFailureHandler(status, throwHttpException);
        using var http = new HttpClient(handler);
        var live = CreateRealHelixClient(http);
        using var fixture = new Fixture(liveHelix: live);
        await fixture.Store.SetJobCompletedAsync(JobId, true, TimeSpan.FromHours(1));
        var expectedKind = status == HttpStatusCode.Forbidden ? AcquisitionErrorKind.AccessDenied : AcquisitionErrorKind.NotFound;
        var key = endpoint switch
        {
            "job" => $"job:{JobId}:details",
            "work-items" => $"job:{JobId}:workitems",
            "work-item" => $"job:{JobId}:wi:{WorkItem}:details",
            "files" => $"job:{JobId}:wi:{WorkItem}:files",
            "console" => ConsoleKey,
            "file" => $"job:{JobId}:wi:{WorkItem}:file:results.trx",
            _ => throw new InvalidOperationException($"Unknown endpoint {endpoint}.")
        };

        var providerFailure = await Assert.ThrowsAsync<HlxAcquisitionException>(() => InvokeHelixAsync(live, endpoint));
        AcquisitionAssertions.Error(providerFailure, expectedKind, "helix", operation, (int)status);
        var cachedFailure = await Assert.ThrowsAsync<HlxAcquisitionException>(() => InvokeHelixAsync(fixture.CachedHelix, endpoint));
        AcquisitionAssertions.Error(cachedFailure, expectedKind, "helix", operation, (int)status);
        AcquisitionAssertions.Resource(cachedFailure.Error, "jobId", JobId);
        if (endpoint is not ("job" or "work-items"))
            AcquisitionAssertions.Resource(cachedFailure.Error, "workItem", WorkItem);
        if (endpoint == "file")
            AcquisitionAssertions.Resource(cachedFailure.Error, "fileName", "results.trx");
        var recorded = await fixture.Store.GetAcquisitionErrorAsync(key);
        Assert.NotNull(recorded);
        Assert.Equal(expectedKind, recorded.Kind);
        Assert.Equal(operation, recorded.Operation);
        Assert.Equal(2, handler.Calls);

        await SnapshotExporter.ExportAsync(fixture.Options.GetEffectiveCacheRoot(), fixture.SnapshotPath);
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var offline = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        var replay = await Assert.ThrowsAsync<HlxAcquisitionException>(() => InvokeHelixAsync(offline, endpoint));
        AcquisitionAssertions.Error(replay, expectedKind, "helix", operation, (int)status);
        Assert.True(replay.Error.Replayed);
        Assert.Equal("snapshot", replay.Error.Source);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Collector_RealHelixFailuresAreRecordedRatherThanSnapshotMisses_IndepReview2(HttpStatusCode status)
    {
        using var handler = new HelixFailureHandler(status);
        using var http = new HttpClient(handler);
        using var fixture = new Fixture(liveHelix: CreateRealHelixClient(http));
        await fixture.Store.SetJobCompletedAsync(JobId, true, TimeSpan.FromHours(1));

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { ExportPath = fixture.SnapshotPath });

        Assert.False(result.Manifest.Complete);
        Assert.Equal(2, result.Manifest.ExitCode);
        var attempts = result.Manifest.Attempts.Where(a => a.Provider == "helix").ToList();
        Assert.Equal(3, attempts.Count);
        Assert.All(attempts, a =>
        {
            Assert.Equal("recorded_failure", a.Outcome);
            Assert.Equal(a.Operation, a.Error?.Operation);
            Assert.Equal((int)status, a.Error?.HttpStatus);
        });
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var offline = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        foreach (var endpoint in new[] { "work-item", "console", "files" })
        {
            var error = await Assert.ThrowsAsync<HlxAcquisitionException>(() => InvokeHelixAsync(offline, endpoint));
            Assert.Equal(status == HttpStatusCode.Forbidden ? AcquisitionErrorKind.AccessDenied : AcquisitionErrorKind.NotFound, error.Error.Kind);
            Assert.True(error.Error.Replayed);
            Assert.Equal("snapshot", error.Error.Source);
        }
    }

    [Fact]
    public async Task CliExportWithoutCacheDir_ExcludesOtherBuildAndAuthPartition_IndepReview3()
    {
        using var fixture = new Fixture();
        var otherBuildKey = AzdoCacheKeys.MetadataKey(fixture.Options, Org, Project, $"build:{BuildId + 1}");
        var otherPartition = fixture.Options with { AuthTokenHash = "deadbeef" };
        var privateKey = AzdoCacheKeys.MetadataKey(otherPartition, Org, Project, $"build:{BuildId}");
        const string foreignConsoleKey = "job:aaaaaaaa-bbbb-cccc-dddd-000000000002:wi:private:console";
        using (var sharedStore = new SqliteCacheStore(fixture.Options))
        {
            await sharedStore.StartupMaintenance;
            await sharedStore.SetMetadataAsync(otherBuildKey, """{"id":1621467,"private":"other-build"}""", TimeSpan.FromHours(1));
            await sharedStore.SetMetadataAsync(privateKey, """{"id":1621466,"private":"other-identity"}""", TimeSpan.FromHours(1));
            await using var bytes = new MemoryStream(Encoding.UTF8.GetBytes("unrelated-private-console"));
            await sharedStore.SetArtifactAsync(foreignConsoleKey, bytes);
            await sharedStore.SetJobCompletedAsync("aaaaaaaa-bbbb-cccc-dddd-000000000002", true, TimeSpan.FromHours(1));
            await sharedStore.SetAcquisitionErrorAsync(
                "job:aaaaaaaa-bbbb-cccc-dddd-000000000002:wi:private:files",
                AcquisitionErrorFactory.Create(AcquisitionErrorKind.AccessDenied, "helix", "list_helix_work_item_files",
                    new Dictionary<string, object?> { ["jobId"] = "aaaaaaaa-bbbb-cccc-dddd-000000000002" }, "private failure"),
                TimeSpan.FromHours(1));
        }

        var manifest = await fixture.InvokeCliAsync(cacheDir: null, export: fixture.SnapshotPath);

        Assert.True(manifest.Complete);
        Assert.Equal(0, manifest.ExitCode);
        var validation = await SnapshotValidator.ValidateAsync(fixture.SnapshotPath);
        Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        var keys = ReadKeys(fixture.SnapshotPath);
        Assert.DoesNotContain(otherBuildKey, keys);
        Assert.DoesNotContain(privateKey, keys);
        Assert.DoesNotContain(keys, key => key.Contains("deadbeef", StringComparison.Ordinal) ||
            key.Contains("aaaaaaaa-bbbb-cccc-dddd-000000000002", StringComparison.Ordinal));
        Assert.Contains(AzdoCacheKeys.MetadataKey(fixture.Options, Org, Project, $"build:{BuildId}"), keys);
        var allowedKeys = manifest.Attempts.Select(a => a.CacheKey).Where(key => key is not null).ToHashSet();
        Assert.All(keys.Where(key => key.StartsWith("azdo:", StringComparison.Ordinal)),
            key => Assert.True(allowedKeys.Contains(key) || key.Contains($":{BuildId}:", StringComparison.Ordinal) ||
                key.EndsWith($":{BuildId}", StringComparison.Ordinal), $"Unrelated AzDO key exported: {key}"));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(fixture.SnapshotPath, "artifacts"), "*", SearchOption.AllDirectories),
            path => File.ReadAllText(path).Contains("unrelated-private-console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SnapshotValidationFailure_FailsClosedInBothManifests_IndepReview4()
    {
        using var fixture = new Fixture();
        var invalidated = false;
        var result = await fixture.Collector.CollectAzdoBuildAsync(
            fixture.Policy with { ExportPath = fixture.SnapshotPath },
            progress: message =>
            {
                if (message != "Validating exported snapshot...")
                    return;
                File.Delete(Assert.Single(Directory.GetFiles(Path.Combine(fixture.SnapshotPath, "artifacts"), "*", SearchOption.AllDirectories)));
                invalidated = true;
            });

        Assert.False(result.Manifest.Complete);
        Assert.True(invalidated, "The exported snapshot must be validated after publication.");
        Assert.Equal(1, result.Manifest.ExitCode);
        Assert.True(result.Manifest.Snapshot.Exported);
        Assert.False(result.Manifest.Snapshot.Validated);
        Assert.NotEmpty(result.Manifest.Snapshot.ValidationErrors);
        Assert.Contains(result.Manifest.IncompleteDetails, d => d.Code == "snapshot_validation_failed");
        foreach (var path in new[] { result.ManifestPath, Path.Combine(fixture.SnapshotPath, "manifest", "hlx-collect-manifest.json") })
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.False(manifest.RootElement.GetProperty("complete").GetBoolean());
            Assert.Equal(1, manifest.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains(manifest.RootElement.GetProperty("incompleteDetails").EnumerateArray(),
                d => d.GetProperty("code").GetString() == "snapshot_validation_failed");
        }
    }

    [Fact]
    public async Task TestScopeAll_RequestsAndStoresEverySupportedOutcome_IndepReview5()
    {
        using var fixture = new Fixture();
        var result = await fixture.Collector.CollectAzdoBuildAsync(
            fixture.Policy with { TestScope = "all", ExportPath = fixture.SnapshotPath });

        Assert.True(result.Manifest.Complete);
        Assert.Equal(0, result.Manifest.ExitCode);
        var request = Assert.Single(fixture.Handler.Requests, uri => uri.AbsolutePath.EndsWith($"/test/runs/{RunId}/results", StringComparison.Ordinal));
        var outcomes = QueryValue(request, "outcomes");
        Assert.True(outcomes is null ||
            AzdoHandler.AllOutcomes.All(outcome => outcomes.Split(',').Contains(outcome, StringComparer.OrdinalIgnoreCase)),
            $"--test-scope all sent a restrictive outcomes filter: {outcomes}");
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var offline = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options);
        var rows = await offline.GetTestResultsAsync(Org, Project, RunId, outcomes: outcomes);
        Assert.Equal(AzdoHandler.AllOutcomes.Order(), rows.Select(row => row.Outcome!).Order());
        Assert.All(rows.Where(row => IsDiagnosticOutcome(row.Outcome)), row => Assert.Contains(result.Manifest.Attempts,
            attempt => attempt.Operation == "list_test_attachments" && attempt.Outcome == "ok" &&
                Convert.ToInt32(attempt.Resource["resultId"]) == row.Id));
    }

    [Fact]
    public async Task OptionalDownloads_CannotEvictRequiredEvidenceUnderSmallCacheCap_IndepReview6()
    {
        using var fixture = new Fixture(maxSizeBytes: 20 * 1024);
        var files = new[] { FileEntry("budget.bin"), FileEntry("oversized.bin") };
        fixture.Helix.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(files);
        fixture.Helix.GetFileAsync("budget.bin", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[64 * 1024])));
        fixture.Helix.GetFileAsync("oversized.bin", WorkItem, JobId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream>(new MemoryStream(new byte[128 * 1024])));

        var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with
        {
            ExportPath = fixture.SnapshotPath,
            DownloadHelixFiles = "*.bin",
            MaxFileBytes = 96 * 1024,
            MaxTotalBytes = 1024 * 1024,
            MaxConcurrency = 1
        });

        Assert.True(result.Manifest.Complete, string.Join("\n", result.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, result.Manifest.ExitCode);
        Assert.All(result.Manifest.Attempts.Where(a => a.Required), a => Assert.Equal("ok", a.Outcome));
        var downloads = result.Manifest.Attempts.Where(a => a.Operation == "download_helix_file").ToList();
        Assert.Equal(2, downloads.Count);
        Assert.All(downloads, a =>
        {
            Assert.Equal("skipped", a.Outcome);
            Assert.Contains(a.Skip?.Kind, new[] { "total_size_limit", "size_limit" });
        });
        Assert.Contains(downloads, a => a.Skip?.Kind == "total_size_limit");
        await using var console = await fixture.Store.GetArtifactAsync(ConsoleKey);
        Assert.NotNull(console);
        Assert.InRange((await fixture.Store.GetStatusAsync()).TotalSizeBytes, 1, fixture.Options.MaxSizeBytes);
        var validation = await SnapshotValidator.ValidateAsync(fixture.SnapshotPath);
        Assert.True(validation.IsValid, string.Join("\n", validation.Errors));
        using var replayStore = new SqliteCacheStore(new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true });
        var replay = new CachingHelixApiClient(new OfflineHelixApiClient(), replayStore,
            new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true });
        await using var replayedConsole = await replay.GetConsoleLogAsync(WorkItem, JobId);
        using var reader = new StreamReader(replayedConsole);
        Assert.Contains("required console", await reader.ReadToEndAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalVerification_IgnoresExpiredButPresentTestRows_IndepReview7()
    {
        using var fixture = new Fixture();
        await fixture.Store.StartupMaintenance;
        var expiredRows = 0;
        var result = await fixture.Collector.CollectAzdoBuildAsync(
            fixture.Policy with { TestScope = "all", ExportPath = fixture.SnapshotPath },
            progress: message =>
            {
                if (message != "Collecting Helix suggested fetches...")
                    return;
                using var connection = OpenDatabase(fixture.Options.GetEffectiveCacheRoot());
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE cache_metadata SET expires_at = @expired
                    WHERE cache_key LIKE '%:testruns:%'
                       OR cache_key LIKE '%:testresults:%'
                       OR cache_key LIKE '%:testattachments:%';
                    """;
                command.Parameters.AddWithValue("@expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
                expiredRows = command.ExecuteNonQuery();
            });

        Assert.True(expiredRows > 0, "Expiry injection must affect physically present test metadata.");
        Assert.Null(await fixture.Store.GetMetadataAsync(AzdoListCacheKeys.TestRunsComplete(fixture.Options, Org, Project, BuildId)));
        Assert.True(result.Manifest.Complete, string.Join("\n", result.Manifest.IncompleteDetails.Select(d => d.Message)));
        Assert.Equal(0, result.Manifest.ExitCode);
        Assert.All(result.Manifest.Attempts.Where(a => a.Outcome != "skipped" &&
            a.Operation is "list_test_runs" or "list_test_results" or "list_test_attachments"),
            a => Assert.Equal("ok", a.Outcome));
        var options = new CacheOptions { CacheRoot = fixture.SnapshotPath, EvalMode = true };
        using var store = new SqliteCacheStore(options);
        var replay = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options);
        Assert.Single(await replay.GetTestRunsAsync(Org, Project, BuildId));
        Assert.Single(await replay.GetTestResultsAsync(Org, Project, RunId, outcomes: "Failed"));
    }

    [Fact]
    public async Task DefaultManifestPath_StaysUnderCacheInsteadOfWorkingDirectory_IndepReview8()
    {
        using var fixture = new Fixture();
        var cwdManifest = Path.Combine(Environment.CurrentDirectory, "hlx-collect-manifest.json");
        var originalBytes = File.Exists(cwdManifest) ? await File.ReadAllBytesAsync(cwdManifest) : null;

        try
        {
            var result = await fixture.Collector.CollectAzdoBuildAsync(fixture.Policy with { ManifestPath = null });

            Assert.NotEqual(cwdManifest, result.ManifestPath);
            Assert.StartsWith(Path.GetFullPath(fixture.Options.GetBaseCacheRoot()) + Path.DirectorySeparatorChar,
                Path.GetFullPath(result.ManifestPath), StringComparison.Ordinal);
            Assert.True(File.Exists(result.ManifestPath));
            if (originalBytes is null)
                Assert.False(File.Exists(cwdManifest));
            else
                Assert.Equal(originalBytes, await File.ReadAllBytesAsync(cwdManifest));
        }
        finally
        {
            if (originalBytes is null && File.Exists(cwdManifest))
                File.Delete(cwdManifest);
        }
    }

    private static IWorkItemFile FileEntry(string name)
    {
        var file = Substitute.For<IWorkItemFile>();
        file.Name.Returns(name);
        file.Link.Returns($"https://helix.dot.net/files/{name}");
        return file;
    }

    private static HelixApiClient CreateRealHelixClient(HttpClient http)
    {
        var options = new HelixApiOptions { Transport = new HttpClientTransport(http) };
        options.Retry.MaxRetries = 0;
        return HelixApiClient.CreateForTesting(options);
    }

    private static async Task InvokeHelixAsync(IHelixApiClient client, string endpoint)
    {
        switch (endpoint)
        {
            case "job":
                await client.GetJobDetailsAsync(JobId);
                break;
            case "work-items":
                await client.ListWorkItemsAsync(JobId);
                break;
            case "work-item":
                await client.GetWorkItemDetailsAsync(WorkItem, JobId);
                break;
            case "files":
                await client.ListWorkItemFilesAsync(WorkItem, JobId);
                break;
            case "console":
                await using (await client.GetConsoleLogAsync(WorkItem, JobId)) { }
                break;
            case "file":
                await using (await client.GetFileAsync("results.trx", WorkItem, JobId)) { }
                break;
            default:
                Assert.Fail($"Unknown endpoint {endpoint}.");
                break;
        }
    }

    private static SqliteConnection OpenDatabase(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "cache.db")};Pooling=False");
        connection.Open();
        return connection;
    }

    private static List<string> ReadKeys(string snapshot)
    {
        using var connection = OpenDatabase(snapshot);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cache_key FROM cache_metadata
            UNION ALL SELECT cache_key FROM cache_artifacts
            UNION ALL SELECT cache_key FROM cache_acquisition_errors
            UNION ALL SELECT job_id FROM cache_job_state;
            """;
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read())
            keys.Add(reader.GetString(0));
        return keys;
    }

    private static string? QueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]) == key)
                return parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        return null;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-independent-review-{Guid.NewGuid():N}");
        private readonly ServiceProvider _services;
        private readonly HttpClient _http;

        public Fixture(long maxSizeBytes = 128 * 1024 * 1024, IHelixApiClient? liveHelix = null)
        {
            Directory.CreateDirectory(_root);
            Options = new CacheOptions { CacheRoot = Path.Combine(_root, "cache"), MaxSizeBytes = maxSizeBytes };
            Handler = new AzdoHandler();
            _http = new HttpClient(Handler);
            var azdoToken = Substitute.For<IAzdoTokenAccessor>();
            azdoToken.AuthStatusAsync(Arg.Any<CancellationToken>())
                .Returns(new AzdoAuthStatus { IsAuthenticated = false, Source = "anonymous", Path = "anonymous" });
            var helixToken = Substitute.For<IHelixTokenAccessor>();
            Azdo = new AzdoApiClient(_http, azdoToken, Options);
            Helix = Substitute.For<IHelixApiClient>();
            var details = Substitute.For<IJobDetails>();
            details.Name.Returns(JobId);
            details.Finished.Returns("2026-10-02T18:00:00Z");
            Helix.GetJobDetailsAsync(JobId, Arg.Any<CancellationToken>()).Returns(details);
            var workItem = Substitute.For<IWorkItemDetails>();
            workItem.State.Returns("Finished");
            workItem.ExitCode.Returns(-3);
            Helix.GetWorkItemDetailsAsync(WorkItem, JobId, Arg.Any<CancellationToken>()).Returns(workItem);
            Helix.ListWorkItemFilesAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
                .Returns(Array.Empty<IWorkItemFile>());
            Helix.GetConsoleLogAsync(WorkItem, JobId, Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("required console\n"))));

            var services = new ServiceCollection();
            services.AddSingleton(Options);
            services.AddSingleton(azdoToken);
            services.AddSingleton(helixToken);
            services.AddSingleton<ICacheStore>(_ => new SqliteCacheStore(Options));
            services.AddSingleton<IAzdoApiClient>(p => new CachingAzdoApiClient(Azdo, p.GetRequiredService<ICacheStore>(), Options, azdoToken));
            services.AddSingleton<IHelixApiClient>(p => new CachingHelixApiClient(liveHelix ?? Helix, p.GetRequiredService<ICacheStore>(), Options));
            services.AddSingleton(p => new AzdoService(p.GetRequiredService<IAzdoApiClient>(), p.GetRequiredService<IHelixApiClient>(),
                new CachingAzdoAcquisitionFailureRecorder(p.GetRequiredService<ICacheStore>(), Options), Options));
            services.AddSingleton(p => new HelixService(p.GetRequiredService<IHelixApiClient>(), _http));
            services.AddSingleton<AzdoBuildCollector>();
            _services = services.BuildServiceProvider();
            Policy = new CollectPolicy
            {
                BuildIdOrUrl = BuildId.ToString(),
                ManifestPath = Path.Combine(_root, "manifest.json"),
                RetryCount = 3,
                RetryInitialDelay = TimeSpan.Zero,
                RetryMaxDelay = TimeSpan.Zero,
                MaxConcurrency = 1,
                Options = new Dictionary<string, object?> { ["cacheDir"] = Options.CacheRoot }
            };
        }

        public CacheOptions Options { get; }
        public AzdoHandler Handler { get; }
        public AzdoApiClient Azdo { get; }
        public IHelixApiClient Helix { get; }
        public CollectPolicy Policy { get; }
        public SqliteCacheStore Store => (SqliteCacheStore)_services.GetRequiredService<ICacheStore>();
        public IHelixApiClient CachedHelix => _services.GetRequiredService<IHelixApiClient>();
        public AzdoBuildCollector Collector => _services.GetRequiredService<AzdoBuildCollector>();
        public string SnapshotPath => Path.Combine(_root, "snapshot");
        public string LastStdout { get; private set; } = "";
        public string LastStderr { get; private set; } = "";

        public async Task<CollectManifest> InvokeCliAsync(
            string? cacheDir, string? export, string testScope = "failed", long? maxTestResults = null,
            string testAttachmentScope = "diagnostic", long? maxTestAttachments = null,
            bool allowIncomplete = false, int expectedExit = 0, bool resume = false)
        {
            await TestConsoleCapture.Lock.WaitAsync();
            var originalOut = Console.Out;
            var originalError = Console.Error;
            var originalExit = Environment.ExitCode;
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Environment.ExitCode = 0;
                await new global::CollectCommands(_services).AzdoBuild(BuildId.ToString(),
                    cacheDir: cacheDir, manifest: Policy.ManifestPath, export: export, json: true,
                    testScope: testScope, maxTestResults: maxTestResults, testAttachmentScope: testAttachmentScope,
                    maxTestAttachments: maxTestAttachments, allowIncomplete: allowIncomplete, resume: resume,
                    retryInitialDelay: "00:00:00", retryMaxDelay: "00:00:00", maxConcurrency: 1);
                LastStdout = stdout.ToString();
                LastStderr = stderr.ToString();
                Assert.True(Environment.ExitCode == expectedExit,
                    $"CLI exited {Environment.ExitCode}.\n{stderr}\n{stdout}");
                return Assert.IsType<CollectManifest>(JsonSerializer.Deserialize<CollectManifest>(
                    stdout.ToString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        Converters = { new AcquisitionErrorKindJsonConverter() }
                    }));
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Environment.ExitCode = originalExit;
                TestConsoleCapture.Lock.Release();
            }
        }

        public void Dispose()
        {
            _services.Dispose();
            _http.Dispose();
            var isolatedCache = Options.GetBaseCacheRoot();
            var temporaryCollectRoot = Path.Combine(Path.GetTempPath(), "hlx-collect-cache") + Path.DirectorySeparatorChar;
            if (isolatedCache.StartsWith(temporaryCollectRoot, StringComparison.Ordinal) && Directory.Exists(isolatedCache))
                Directory.Delete(isolatedCache, recursive: true);
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class HelixFailureHandler(HttpStatusCode status, bool throwHttpException = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (throwHttpException)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Helix provider rejected the request", null, status));
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent("""{"message":"Helix provider rejected the request"}""", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AzdoHandler : HttpMessageHandler
    {
        public static readonly string[] AllOutcomes =
        [
            "Passed", "Failed", "NotExecuted", "Inconclusive", "Timeout", "Aborted", "Error", "NotApplicable",
            "Blocked", "Warning", "NotImpacted", "InProgress", "Paused", "None", "Unspecified"
        ];
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public string? FaultEndpoint { get; set; }
        public string BodyException { get; set; } = "io";
        public int FaultsRemaining { get; set; } = int.MaxValue;
        public int FaultResponses { get; private set; }
        public IReadOnlyList<AzdoTestRun> TestRuns { get; set; } =
            [new() { Id = RunId, Name = "Runtime Tests", State = "completed", TotalTests = 15, UnanalyzedTests = 1 }];
        public Dictionary<int, IReadOnlyList<AzdoTestResult>> ResultsByRun { get; } = new();
        public TaskCompletionSource? ResultsGate { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            var path = uri.AbsolutePath;
            if (ResultsGate is not null && path.EndsWith("/results", StringComparison.Ordinal))
                return WaitForResultsAsync(uri, cancellationToken);
            var endpoint = path.EndsWith("/timeline", StringComparison.Ordinal) ? "timeline"
                : path.EndsWith("/logs/11", StringComparison.Ordinal) ? "log"
                : path.EndsWith("/logs", StringComparison.Ordinal) ? "logs-list"
                : path.EndsWith($"/builds/{BuildId}", StringComparison.Ordinal) ? "build"
                : "other";
            if (endpoint == FaultEndpoint && FaultsRemaining-- > 0)
            {
                FaultResponses++;
                Exception exception = BodyException switch
                {
                    "http" => new HttpRequestException("connection reset mid-body"),
                    "timeout" => new TaskCanceledException("response body timed out"),
                    _ => new IOException("connection reset mid-body")
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MidBodyFailureStream("partial", exception))
                });
            }

            if (endpoint == "log")
                return Task.FromResult(Response("required AzDO log\n", "text/plain"));
            var json = endpoint switch
            {
                "build" => """{"id":1621466,"status":"completed","result":"failed","definition":{"id":123,"name":"runtime"},"finishTime":"2026-10-02T18:00:00Z"}""",
                "timeline" => $$$"""
                    {"id":"timeline","records":[
                        {"id":"failed-job","type":"Job","name":"linux tests","state":"completed","result":"failed","log":{"id":11}},
                        {"id":"monitor-job","type":"Job","name":"Monitor Helix Jobs","state":"completed","result":"failed"},
                        {"id":"monitor-task","parentId":"monitor-job","type":"Task","name":"Monitor Helix Jobs","state":"completed","result":"failed","issues":[
                            {"type":"warning","message":"Work item '{{{WorkItem}}}' in job 'runtime leg - queue ({{{JobId}}})' failed (Finished, exit code -3)."}
                        ]}
                    ]}
                    """,
                "logs-list" => """{"value":[{"id":11,"lineCount":1}]}""",
                _ when path.EndsWith("/artifacts", StringComparison.Ordinal) =>
                    """{"value":[{"id":9001,"name":"Logs_Build_linux_tests","source":"failed-job","resource":{"type":"PipelineArtifact","downloadUrl":"https://example.invalid/logs"}}]}""",
                _ when path.EndsWith("/test/runs", StringComparison.Ordinal) =>
                    JsonSerializer.Serialize(new { value = TestRuns }),
                _ when path.EndsWith("/results", StringComparison.Ordinal) => ResultsJson(uri),
                _ when path.EndsWith("/attachments", StringComparison.Ordinal) => """{"value":[]}""",
                _ => throw new InvalidOperationException($"Unexpected fake AzDO request: {uri}")
            };
            return Task.FromResult(Response(json, "application/json"));
        }

        private async Task<HttpResponseMessage> WaitForResultsAsync(Uri uri, CancellationToken ct)
        {
            await ResultsGate!.Task.WaitAsync(ct);
            return Response(ResultsJson(uri), "application/json");
        }

        private string ResultsJson(Uri uri)
        {
            var outcomes = QueryValue(uri, "outcomes");
            var filter = outcomes?.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skip = int.TryParse(QueryValue(uri, "$skip"), out var offset) ? offset : 0;
            var runId = int.Parse(uri.AbsolutePath.Split('/')[^2], System.Globalization.CultureInfo.InvariantCulture);
            var results = ResultsByRun.TryGetValue(runId, out var configured) ? configured :
                AllOutcomes.Select((outcome, index) => new AzdoTestResult { Id = 8001 + index, Outcome = outcome, TestCaseTitle = $"test-{outcome}" }).ToList();
            var rows = results.Where(row => filter is null || filter.Contains(row.Outcome!))
                .Skip(skip)
                .ToArray();
            return JsonSerializer.Serialize(new { value = rows });
        }

        private static HttpResponseMessage Response(string body, string mediaType) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    private static bool IsDiagnosticOutcome(string? outcome) =>
        new[] { "Failed", "Error", "Timeout", "Aborted", "Inconclusive", "Blocked", "Warning" }
            .Contains(outcome, StringComparer.OrdinalIgnoreCase);

    private sealed class MidBodyFailureStream(string prefix, Exception exception) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        private bool _readPrefix;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_readPrefix)
                throw exception;
            _readPrefix = true;
            return _prefix.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_readPrefix)
                return ValueTask.FromException<int>(exception);
            _readPrefix = true;
            return ValueTask.FromResult(_prefix.Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _prefix.Dispose();
            base.Dispose(disposing);
        }
    }
}
