using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public sealed class SnapshotMissErrorShapeTests
{
    [Fact]
    public void AcquisitionErrorKindJsonConverter_NotInSnapshot_RoundTripsWireValue()
    {
        var kind = SnapshotMissTestSupport.NotInSnapshotKind();

        var json = JsonSerializer.Serialize(kind, AcquisitionJsonOptions.Default);
        var roundTrip = JsonSerializer.Deserialize<AcquisitionErrorKind>(json, AcquisitionJsonOptions.Default);

        Assert.Equal("\"not_in_snapshot\"", json);
        Assert.Equal(kind, roundTrip);
    }

    [Fact]
    public void AcquisitionErrorKindJsonConverter_UnknownWireKind_StillThrows()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<AcquisitionErrorKind>("\"not_a_real_kind\"", AcquisitionJsonOptions.Default));
    }

    [Fact]
    public void AcquisitionError_SnapshotReplayFields_AreSerializedWithStableNames()
    {
        var recordedAt = DateTimeOffset.Parse("2026-10-02T17:44:36.2220000Z", null, System.Globalization.DateTimeStyles.RoundtripKind);
        var error = SnapshotMissTestSupport.WithSnapshotFields(
            SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build_log", httpStatus: 404),
            source: "snapshot",
            replayed: true,
            recordedAt);

        var json = JsonSerializer.Serialize(error, AcquisitionJsonOptions.Default);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("not_found", root.GetProperty("kind").GetString());
        Assert.Equal("snapshot", root.GetProperty("source").GetString());
        Assert.True(root.GetProperty("replayed").GetBoolean());
        Assert.Equal(recordedAt, root.GetProperty("recordedAt").GetDateTimeOffset());
    }

    [Theory]
    [MemberData(nameof(OfflineAzdoCalls))]
    public async Task OfflineAzdoApiClient_TrueMisses_AreNotInSnapshotWithSnapshotSource(
        string operation,
        Func<OfflineAzdoApiClient, Task> call)
    {
        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => call(new OfflineAzdoApiClient()));

        SnapshotMissTestSupport.AssertSnapshotMiss(ex.Error, operation);
    }

    [Theory]
    [MemberData(nameof(OfflineHelixCalls))]
    public async Task OfflineHelixApiClient_TrueMisses_AreNotInSnapshotWithSnapshotSource(
        string operation,
        Func<OfflineHelixApiClient, Task> call)
    {
        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => call(new OfflineHelixApiClient()));

        SnapshotMissTestSupport.AssertSnapshotMiss(ex.Error, operation);
    }

    [Fact]
    public async Task McpStructuredContent_TrueEvalMiss_IncludesNotInSnapshotAndSnapshotSource()
    {
        var options = new McpServerOptions().AddAcquisitionErrorFilter();
        var filter = Assert.Single(options.Filters.Request.CallToolFilters);
        var handler = filter(async (_, _) =>
        {
            await new OfflineAzdoApiClient().GetBuildAsync("org", "project", 1);
            return new CallToolResult();
        });

        var result = await handler(SnapshotMissTestSupport.CreateRequest("azdo_build"), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        var error = result.StructuredContent.Value.GetProperty("error");
        Assert.Equal("not_in_snapshot", error.GetProperty("kind").GetString());
        Assert.Equal("cache", error.GetProperty("provider").GetString());
        Assert.Equal("snapshot", error.GetProperty("source").GetString());
    }

    [Fact]
    public async Task CliJsonEnvelope_TrueEvalMiss_IncludesNotInSnapshotAndSnapshotSource()
    {
        var commands = new global::AzdoCommands(new AzdoService(new OfflineAzdoApiClient()), Substitute.For<IAzdoTokenAccessor>());

        var (stdout, _, exitCode, thrown) = await SnapshotMissTestSupport.CaptureConsoleAsync(
            () => commands.Build("1", json: true),
            "--json");

        Assert.Null(thrown);
        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("not_in_snapshot", error.GetProperty("kind").GetString());
        Assert.Equal("cache", error.GetProperty("provider").GetString());
        Assert.Equal("snapshot", error.GetProperty("source").GetString());
    }

    public static IEnumerable<object[]> OfflineAzdoCalls()
    {
        yield return ["get_build", (Func<OfflineAzdoApiClient, Task>)(client => client.GetBuildAsync("org", "project", 1))];
        yield return ["list_builds", (Func<OfflineAzdoApiClient, Task>)(client => client.ListBuildsAsync("org", "project", new AzdoBuildFilter()))];
        yield return ["get_timeline", (Func<OfflineAzdoApiClient, Task>)(client => client.GetTimelineAsync("org", "project", 1))];
        yield return ["get_build_log", (Func<OfflineAzdoApiClient, Task>)(client => client.GetBuildLogAsync("org", "project", 1, 2))];
        yield return ["list_build_changes", (Func<OfflineAzdoApiClient, Task>)(client => client.GetBuildChangesAsync("org", "project", 1))];
        yield return ["list_test_runs", (Func<OfflineAzdoApiClient, Task>)(client => client.GetTestRunsAsync("org", "project", 1))];
        yield return ["list_test_results", (Func<OfflineAzdoApiClient, Task>)(client => client.GetTestResultsAsync("org", "project", 1))];
        yield return ["list_artifacts", (Func<OfflineAzdoApiClient, Task>)(client => client.GetBuildArtifactsAsync("org", "project", 1))];
        yield return ["list_test_attachments", (Func<OfflineAzdoApiClient, Task>)(client => client.GetTestAttachmentsAsync("org", "project", 1, 2))];
        yield return ["list_build_logs", (Func<OfflineAzdoApiClient, Task>)(client => client.GetBuildLogsListAsync("org", "project", 1))];
    }

    public static IEnumerable<object[]> OfflineHelixCalls()
    {
        yield return ["get_helix_job", (Func<OfflineHelixApiClient, Task>)(client => client.GetJobDetailsAsync("job"))];
        yield return ["list_helix_work_items", (Func<OfflineHelixApiClient, Task>)(client => client.ListWorkItemsAsync("job"))];
        yield return ["get_helix_work_item", (Func<OfflineHelixApiClient, Task>)(client => client.GetWorkItemDetailsAsync("workItem", "job"))];
        yield return ["list_helix_work_item_files", (Func<OfflineHelixApiClient, Task>)(client => client.ListWorkItemFilesAsync("workItem", "job"))];
        yield return ["get_helix_console_log", (Func<OfflineHelixApiClient, Task>)(client => client.GetConsoleLogAsync("workItem", "job"))];
        yield return ["download_helix_file", (Func<OfflineHelixApiClient, Task>)(client => client.GetFileAsync("file.trx", "workItem", "job"))];
        yield return ["list_helix_jobs_by_build", (Func<OfflineHelixApiClient, Task>)(client => client.ListJobsByBuildAsync("source", "build"))];
    }
}

public sealed class SnapshotMissCacheStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-snapshot-miss-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NewStore_CreatesSchemaV2NegativeTableAndIndexes()
    {
        using (new SqliteCacheStore(new CacheOptions { CacheRoot = _root })) { }

        var dbPath = SnapshotMissTestSupport.DbPathForNormalRoot(_root);
        Assert.Equal(2, SnapshotMissTestSupport.ReadUserVersion(dbPath));
        Assert.True(SnapshotMissTestSupport.TableExists(dbPath, "cache_acquisition_errors"));
        Assert.True(SnapshotMissTestSupport.IndexExists(dbPath, "idx_acquisition_errors_expires"));
        Assert.True(SnapshotMissTestSupport.IndexExists(dbPath, "idx_acquisition_errors_job"));
    }

    [Fact]
    public async Task LiveV1Database_MigratesToV2WithoutDeletingExistingRows()
    {
        var effectiveRoot = Path.Combine(_root, "public");
        Directory.CreateDirectory(effectiveRoot);
        var dbPath = Path.Combine(effectiveRoot, "cache.db");
        SnapshotMissTestSupport.CreateSchemaV1Database(dbPath);
        SnapshotMissTestSupport.InsertMetadataRow(dbPath, "job:abc:details", "{\"Name\":\"kept\"}");

        using (new SqliteCacheStore(new CacheOptions { CacheRoot = _root })) { }

        Assert.Equal(2, SnapshotMissTestSupport.ReadUserVersion(dbPath));
        Assert.True(SnapshotMissTestSupport.TableExists(dbPath, "cache_acquisition_errors"));
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _root });
        Assert.Equal("{\"Name\":\"kept\"}", await store.GetMetadataAsync("job:abc:details"));
    }

    [Fact]
    public async Task AcquisitionErrorRows_StoreRehydrateAndAreDeletedByPositiveMetadata()
    {
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _root });
        const string key = "azdo:org:project:build:42";
        var error = SnapshotMissTestSupport.Error(
            AcquisitionErrorKind.AccessDenied,
            "azdo",
            "get_build",
            httpStatus: 403);

        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(store, key, error, TimeSpan.FromHours(1));
        var stored = await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, key);

        Assert.NotNull(stored);
        Assert.Equal(AcquisitionErrorKind.AccessDenied, stored!.Kind);
        Assert.Equal("azdo", stored.Provider);
        Assert.Equal("get_build", stored.Operation);
        Assert.Equal(403, stored.HttpStatus);

        await store.SetMetadataAsync(key, JsonSerializer.Serialize(new AzdoBuild { Id = 42 }), TimeSpan.FromHours(1));

        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, key));
    }

    [Fact]
    public async Task AcquisitionErrorRows_AreDeletedByPositiveArtifactAndClear()
    {
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _root });
        const string artifactKey = "job:job1:wi:workItem:file:results.trx";
        var error = SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "helix", "download_helix_file", httpStatus: 404);

        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(store, artifactKey, error, TimeSpan.FromHours(1));
        await store.SetArtifactAsync(artifactKey, new MemoryStream("content"u8.ToArray()));

        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, artifactKey));

        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(store, artifactKey, error, TimeSpan.FromHours(1));
        await store.ClearAsync();

        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, artifactKey));
    }

    [Fact]
    public async Task SetAcquisitionError_DeletesSameKeyPositiveMetadataAndArtifactRows()
    {
        using var store = new SqliteCacheStore(new CacheOptions { CacheRoot = _root });
        const string metadataKey = "azdo:org:project:build:42";
        const string artifactKey = "job:job1:wi:workItem:file:results.trx";
        var error = SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404);

        await store.SetMetadataAsync(metadataKey, "{\"Id\":42}", TimeSpan.FromHours(1));
        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(store, metadataKey, error, TimeSpan.FromHours(1));

        Assert.Null(await store.GetMetadataAsync(metadataKey));
        Assert.NotNull(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, metadataKey));

        await store.SetArtifactAsync(artifactKey, new MemoryStream("artifact"u8.ToArray()));
        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
            store,
            artifactKey,
            SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "helix", "download_helix_file", httpStatus: 404),
            TimeSpan.FromHours(1));

        Assert.Null(await store.GetArtifactAsync(artifactKey));
        Assert.NotNull(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, artifactKey));
    }

    [Fact]
    public async Task AcquisitionErrorRows_ExpireInLiveModeButReplayInEvalMode()
    {
        var writerOptions = new CacheOptions { CacheRoot = _root };
        var key = "azdo:org:project:build:42";
        using (var writer = new SqliteCacheStore(writerOptions))
        {
            await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
                writer,
                key,
                SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404),
                TimeSpan.Zero);
        }

        using (var liveStore = new SqliteCacheStore(writerOptions))
        {
            Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(liveStore, key));
        }

        using var evalStore = new SqliteCacheStore(new CacheOptions { CacheRoot = writerOptions.GetEffectiveCacheRoot(), EvalMode = true });
        var replayed = await SnapshotMissTestSupport.GetAcquisitionErrorAsync(evalStore, key);
        Assert.NotNull(replayed);
        Assert.Equal(AcquisitionErrorKind.NotFound, replayed!.Kind);
    }

    [Fact]
    public async Task ExpiredAcquisitionErrorRows_AreEvictedAndAbsentFromExportedSnapshot()
    {
        var options = new CacheOptions { CacheRoot = _root };
        var destination = Path.Combine(Path.GetTempPath(), $"hlx-expired-negative-export-{Guid.NewGuid():N}");
        try
        {
            using (var store = new SqliteCacheStore(options))
            {
                await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
                    store,
                    "azdo:org:project:build:42",
                    SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404),
                    TimeSpan.Zero);

                await store.EvictExpiredAsync();

                Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:org:project:build:42"));
            }

            await SnapshotExporter.ExportAsync(options.GetEffectiveCacheRoot(), destination);

            var validation = await SnapshotValidator.ValidateAsync(destination);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
            Assert.Equal(0, validation.AcquisitionErrorEntries);
        }
        finally
        {
            try { Directory.Delete(destination, recursive: true); } catch { }
        }
    }
}

public sealed class SnapshotMissSnapshotValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-snapshot-validate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task V1Snapshot_ValidatesWithCompatibilityWarning()
    {
        Directory.CreateDirectory(_root);
        SnapshotMissTestSupport.CreateSchemaV1Database(Path.Combine(_root, "cache.db"));
        Directory.CreateDirectory(Path.Combine(_root, "artifacts"));

        var result = await SnapshotValidator.ValidateAsync(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("schema version 1", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains("not_in_snapshot", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task V2Snapshot_ReportsAcquisitionErrorEntryCount()
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "cache.db");
        SnapshotMissTestSupport.CreateSchemaV2Database(dbPath);
        SnapshotMissTestSupport.InsertNegativeRow(dbPath, "azdo:org:project:build:42",
            SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404));
        Directory.CreateDirectory(Path.Combine(_root, "artifacts"));

        var result = await SnapshotValidator.ValidateAsync(_root);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, SnapshotMissTestSupport.GetIntProperty(result, "AcquisitionErrorEntries"));
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("malformed-json")]
    [InlineData("mismatched-columns")]
    [InlineData("invalid-timestamps")]
    [InlineData("positive-negative-conflict")]
    public async Task V2Snapshot_InvalidNegativeRows_AreValidationErrors(string scenario)
    {
        Directory.CreateDirectory(_root);
        var dbPath = Path.Combine(_root, "cache.db");
        SnapshotMissTestSupport.CreateSchemaV2Database(dbPath);
        Directory.CreateDirectory(Path.Combine(_root, "artifacts"));
        var error = SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404);

        switch (scenario)
        {
            case "missing-table":
                SnapshotMissTestSupport.DropTable(dbPath, "cache_acquisition_errors");
                break;
            case "malformed-json":
                SnapshotMissTestSupport.InsertNegativeRow(dbPath, "azdo:org:project:build:42", error, errorJson: "{ no json");
                break;
            case "mismatched-columns":
                SnapshotMissTestSupport.InsertNegativeRow(dbPath, "azdo:org:project:build:42", error, provider: "helix");
                break;
            case "invalid-timestamps":
                SnapshotMissTestSupport.InsertNegativeRow(dbPath, "azdo:org:project:build:42", error, recordedAt: "not-a-date");
                break;
            case "positive-negative-conflict":
                SnapshotMissTestSupport.InsertNegativeRow(dbPath, "azdo:org:project:build:42", error);
                SnapshotMissTestSupport.InsertMetadataRow(dbPath, "azdo:org:project:build:42", "{\"Id\":42}");
                break;
        }

        var result = await SnapshotValidator.ValidateAsync(_root);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }
}

public sealed class SnapshotMissReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-snapshot-replay-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task AzdoEvalMode_ReplaysRecordedProviderFailureWithSnapshotFields()
    {
        var writerOptions = new CacheOptions { CacheRoot = _root };
        const string key = "azdo:org:project:build:42";
        using (var writer = new SqliteCacheStore(writerOptions))
        {
            await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
                writer,
                key,
                SnapshotMissTestSupport.Error(AcquisitionErrorKind.AccessDenied, "azdo", "get_build", httpStatus: 403),
                TimeSpan.FromHours(1));
        }

        var evalOptions = new CacheOptions { CacheRoot = writerOptions.GetEffectiveCacheRoot(), EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOptions);
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOptions);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => client.GetBuildAsync("org", "project", 42));

        SnapshotMissTestSupport.AssertReplayed(ex.Error, AcquisitionErrorKind.AccessDenied, "azdo", "get_build", 403);
    }

    [Fact]
    public async Task AzdoLiveMode_RecordableFailuresPersistButTransientFailuresDoNot()
    {
        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        var inner = Substitute.For<IAzdoApiClient>();
        inner.GetBuildAsync("org", "project", 42, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AzdoBuild?>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.NotFound, "azdo", "get_build", 404)));
        var client = new CachingAzdoApiClient(inner, store, options);

        await Assert.ThrowsAsync<HlxAcquisitionException>(() => client.GetBuildAsync("org", "project", 42));

        Assert.NotNull(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:org:project:build:42"));

        inner.GetBuildAsync("org", "project", 43, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AzdoBuild?>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.Timeout, "azdo", "get_build")));

        await Assert.ThrowsAsync<HlxAcquisitionException>(() => client.GetBuildAsync("org", "project", 43));

        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:org:project:build:43"));
    }

    [Fact]
    public async Task AzdoLiveMode_NotFoundCompletionProbeFailure_RethrowsOriginalAndDoesNotRecord()
    {
        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        var inner = Substitute.For<IAzdoApiClient>();
        inner.GetBuildChangesAsync("org", "project", 42, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<AzdoBuildChange>>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.NotFound, "azdo", "list_build_changes", 404)));
        inner.GetBuildAsync("org", "project", 42, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<AzdoBuild?>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.AccessDenied, "azdo", "get_build", 403)));
        var client = new CachingAzdoApiClient(inner, store, options);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetBuildChangesAsync("org", "project", 42));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "list_build_changes", 404);
        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:org:project:changes:42:"));
    }

    [Fact]
    public async Task HelixLiveMode_NotFoundCompletionProbeFailure_RethrowsOriginalAndDoesNotRecord()
    {
        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        var inner = Substitute.For<IHelixApiClient>();
        inner.GetFileAsync("results.trx", "workItem", "job1", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Stream>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.NotFound, "helix", "download_helix_file", 404)));
        inner.GetJobDetailsAsync("job1", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IJobDetails>(
                SnapshotMissTestSupport.Exception(AcquisitionErrorKind.AccessDenied, "helix", "get_helix_job", 403)));
        var client = new CachingHelixApiClient(inner, store, options);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() =>
            client.GetFileAsync("results.trx", "workItem", "job1"));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "helix", "download_helix_file", 404);
        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "job:job1:wi:workItem:file:results.trx"));
    }

    [Fact]
    public async Task AzdoLiveMode_NeverServesNegativeRowsAndPositiveEvidenceWins()
    {
        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        const string key = "azdo:org:project:build:42";
        await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
            store,
            key,
            SnapshotMissTestSupport.Error(AcquisitionErrorKind.NotFound, "azdo", "get_build", httpStatus: 404),
            TimeSpan.FromHours(1));
        var inner = Substitute.For<IAzdoApiClient>();
        inner.GetBuildAsync("org", "project", 42, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 42, Status = "completed" });
        var client = new CachingAzdoApiClient(inner, store, options);

        var build = await client.GetBuildAsync("org", "project", 42);

        Assert.Equal(42, build!.Id);
        await inner.Received(1).GetBuildAsync("org", "project", 42, Arg.Any<CancellationToken>());
        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, key));

        var evalOptions = new CacheOptions { CacheRoot = options.GetEffectiveCacheRoot(), EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOptions);
        var evalClient = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOptions);
        var evalBuild = await evalClient.GetBuildAsync("org", "project", 42);
        Assert.Equal(42, evalBuild!.Id);
    }

    [Fact]
    public async Task HelixEvalMode_ReplaysRecordedFileDownloadFailure()
    {
        var writerOptions = new CacheOptions { CacheRoot = _root };
        const string key = "job:job1:wi:workItem:file:results.trx";
        using (var writer = new SqliteCacheStore(writerOptions))
        {
            await SnapshotMissTestSupport.SetAcquisitionErrorAsync(
                writer,
                key,
                SnapshotMissTestSupport.Error(AcquisitionErrorKind.AccessDenied, "helix", "download_helix_file", httpStatus: 403),
                TimeSpan.FromHours(1));
        }

        var evalOptions = new CacheOptions { CacheRoot = writerOptions.GetEffectiveCacheRoot(), EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOptions);
        var client = new CachingHelixApiClient(new OfflineHelixApiClient(), evalStore, evalOptions);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => client.GetFileAsync("results.trx", "workItem", "job1"));

        SnapshotMissTestSupport.AssertReplayed(ex.Error, AcquisitionErrorKind.AccessDenied, "helix", "download_helix_file", 403);
    }

    [Fact]
    public async Task EmptyBuildLogAbsentFromMetadata_RecordsAndReplaysNotFoundForLogKey()
    {
        var recorderType = Type.GetType("HelixTool.Core.AzDO.CachingAzdoAcquisitionFailureRecorder, HelixTool.Core");
        Assert.NotNull(recorderType);
        var recorderInterface = Type.GetType("HelixTool.Core.AzDO.IAzdoAcquisitionFailureRecorder, HelixTool.Core");
        Assert.NotNull(recorderInterface);

        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        var recorder = Activator.CreateInstance(recorderType!, store, options);
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 999999, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        api.GetBuildLogsListAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 1, LineCount = 10 } });
        api.GetTimelineAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Records = [new AzdoTimelineRecord { Id = "task", Log = new AzdoLogReference { Id = 2 } }] });
        api.GetBuildAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 12345, Status = "completed" });
        var service = SnapshotMissTestSupport.CreateAzdoService(api, recorderInterface!, recorder!);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.GetBuildLogAsync("12345", 999999, tailLines: null));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "get_build_log");
        var stored = await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:dnceng-public:public:log:12345:999999");
        Assert.NotNull(stored);
        Assert.Equal("get_build_log", stored!.Operation);
    }

    [Theory]
    [InlineData("inProgress")]
    [InlineData(null)]
    public async Task EmptyBuildLogAbsentFromMetadata_DoesNotRecordWhenBuildIsNotTerminal(string? status)
    {
        var recorderType = Type.GetType("HelixTool.Core.AzDO.CachingAzdoAcquisitionFailureRecorder, HelixTool.Core");
        Assert.NotNull(recorderType);
        var recorderInterface = Type.GetType("HelixTool.Core.AzDO.IAzdoAcquisitionFailureRecorder, HelixTool.Core");
        Assert.NotNull(recorderInterface);

        var options = new CacheOptions { CacheRoot = _root };
        using var store = new SqliteCacheStore(options);
        var recorder = Activator.CreateInstance(recorderType!, store, options);
        var api = Substitute.For<IAzdoApiClient>();
        api.GetBuildLogAsync("dnceng-public", "public", 12345, 999999, Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        api.GetBuildLogsListAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new List<AzdoBuildLogEntry> { new() { Id = 1, LineCount = 10 } });
        api.GetTimelineAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(new AzdoTimeline { Records = [new AzdoTimelineRecord { Id = "task", Log = new AzdoLogReference { Id = 2 } }] });
        api.GetBuildAsync("dnceng-public", "public", 12345, Arg.Any<CancellationToken>())
            .Returns(status is null ? null : new AzdoBuild { Id = 12345, Status = status });
        var service = SnapshotMissTestSupport.CreateAzdoService(api, recorderInterface!, recorder!);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(() => service.GetBuildLogAsync("12345", 999999, tailLines: null));

        AcquisitionAssertions.Error(ex, AcquisitionErrorKind.NotFound, "azdo", "get_build_log");
        Assert.Null(await SnapshotMissTestSupport.GetAcquisitionErrorAsync(store, "azdo:dnceng-public:public:log:12345:999999"));
    }
}

internal static class SnapshotMissTestSupport
{
    public static AcquisitionErrorKind NotInSnapshotKind() =>
        (AcquisitionErrorKind)Enum.Parse(typeof(AcquisitionErrorKind), "NotInSnapshot");

    public static AcquisitionError Error(
        AcquisitionErrorKind kind,
        string provider,
        string operation,
        int? httpStatus = null,
        IReadOnlyDictionary<string, object?>? resource = null)
        => new()
        {
            Kind = kind,
            Provider = provider,
            Operation = operation,
            Resource = resource ?? new Dictionary<string, object?> { ["operation"] = operation },
            HttpStatus = httpStatus,
            Message = $"{provider} {operation} failed"
        };

    public static HlxAcquisitionException Exception(AcquisitionErrorKind kind, string provider, string operation, int? httpStatus = null) =>
        new(Error(kind, provider, operation, httpStatus));

    public static AcquisitionError WithSnapshotFields(
        AcquisitionError error,
        string source,
        bool replayed,
        DateTimeOffset recordedAt)
    {
        var withSource = SetProperty(error, "Source", source);
        var withReplayed = SetProperty(withSource, "Replayed", replayed);
        return SetProperty(withReplayed, "RecordedAt", recordedAt);
    }

    private static AcquisitionError SetProperty(AcquisitionError error, string name, object value)
    {
        var property = typeof(AcquisitionError).GetProperty(name);
        Assert.NotNull(property);
        property.SetValue(error, value);
        return error;
    }

    public static void AssertSnapshotMiss(AcquisitionError error, string operation)
    {
        Assert.Equal("\"not_in_snapshot\"", JsonSerializer.Serialize(error.Kind, AcquisitionJsonOptions.Default));
        Assert.Equal("cache", error.Provider);
        Assert.Equal(operation, error.Operation);
        Assert.Equal("snapshot", GetStringProperty(error, "Source"));
    }

    public static void AssertReplayed(
        AcquisitionError error,
        AcquisitionErrorKind kind,
        string provider,
        string operation,
        int? httpStatus)
    {
        Assert.Equal(kind, error.Kind);
        Assert.Equal(provider, error.Provider);
        Assert.Equal(operation, error.Operation);
        Assert.Equal(httpStatus, error.HttpStatus);
        Assert.Equal("snapshot", GetStringProperty(error, "Source"));
        Assert.True(GetBoolProperty(error, "Replayed"));
        Assert.NotNull(GetNullableProperty(error, "RecordedAt"));
    }

    private static string? GetStringProperty(AcquisitionError error, string name)
    {
        var property = typeof(AcquisitionError).GetProperty(name);
        Assert.NotNull(property);
        return property.GetValue(error) as string;
    }

    private static bool? GetBoolProperty(AcquisitionError error, string name)
    {
        var property = typeof(AcquisitionError).GetProperty(name);
        Assert.NotNull(property);
        return property.GetValue(error) as bool?;
    }

    private static object? GetNullableProperty(AcquisitionError error, string name)
    {
        var property = typeof(AcquisitionError).GetProperty(name);
        Assert.NotNull(property);
        return property.GetValue(error);
    }

    public static RequestContext<CallToolRequestParams> CreateRequest(string toolName)
        => new(
            server: Substitute.For<McpServer>(),
            jsonRpcRequest: new JsonRpcRequest { Method = "tools/call" },
            parameters: new CallToolRequestParams { Name = toolName });

    public static async Task<(string Stdout, string Stderr, int ExitCode, Exception? Thrown)> CaptureConsoleAsync(Func<Task> action, params string[] commandArguments)
    {
        await TestConsoleCapture.Lock.WaitAsync();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        var originalExitCode = Environment.ExitCode;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            Exception? thrown = null;
            try
            {
                await global::CliAcquisitionErrorPipeline.InvokeAsync(_ => action(), commandArguments);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            return (stdout.ToString(), stderr.ToString(), Environment.ExitCode, thrown);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.ExitCode = originalExitCode;
            TestConsoleCapture.Lock.Release();
        }
    }

    public static string DbPathForNormalRoot(string root)
        => Path.Combine(new CacheOptions { CacheRoot = root }.GetEffectiveCacheRoot(), "cache.db");

    public static long ReadUserVersion(string dbPath)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool TableExists(string dbPath, string table)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public static bool IndexExists(string dbPath, string index)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;";
        command.Parameters.AddWithValue("@name", index);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public static async Task SetAcquisitionErrorAsync(ICacheStore store, string key, AcquisitionError error, TimeSpan ttl)
    {
        var method = typeof(ICacheStore).GetMethod("SetAcquisitionErrorAsync");
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(store, [key, error, ttl, CancellationToken.None]));
        await task;
    }

    public static async Task<AcquisitionError?> GetAcquisitionErrorAsync(ICacheStore store, string key)
    {
        var method = typeof(ICacheStore).GetMethod("GetAcquisitionErrorAsync");
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task>(method.Invoke(store, [key, CancellationToken.None]));
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return result as AcquisitionError;
    }

    public static void CreateSchemaV1Database(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version=1;
            CREATE TABLE cache_metadata (
                cache_key   TEXT PRIMARY KEY,
                json_value  TEXT NOT NULL,
                created_at  TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                job_id      TEXT NOT NULL
            );
            CREATE TABLE cache_artifacts (
                cache_key       TEXT PRIMARY KEY,
                file_path       TEXT NOT NULL,
                file_size       INTEGER NOT NULL,
                created_at      TEXT NOT NULL,
                last_accessed   TEXT NOT NULL,
                job_id          TEXT NOT NULL
            );
            CREATE TABLE cache_job_state (
                job_id       TEXT PRIMARY KEY,
                is_completed INTEGER NOT NULL,
                finished_at  TEXT,
                cached_at    TEXT NOT NULL,
                expires_at   TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public static void CreateSchemaV2Database(string dbPath)
    {
        CreateSchemaV1Database(dbPath);
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE cache_acquisition_errors (
                cache_key   TEXT PRIMARY KEY,
                error_json  TEXT NOT NULL,
                kind        TEXT NOT NULL,
                provider    TEXT NOT NULL,
                operation   TEXT NOT NULL,
                recorded_at TEXT NOT NULL,
                expires_at  TEXT NOT NULL,
                job_id      TEXT NOT NULL
            );
            CREATE INDEX idx_acquisition_errors_expires ON cache_acquisition_errors(expires_at);
            CREATE INDEX idx_acquisition_errors_job ON cache_acquisition_errors(job_id);
            PRAGMA user_version=2;
            """;
        command.ExecuteNonQuery();
    }

    public static void InsertMetadataRow(string dbPath, string key, string json)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            INSERT OR REPLACE INTO cache_metadata (cache_key, json_value, created_at, expires_at, job_id)
            VALUES (@key, @json, @created, @expires, @jobId);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@json", json);
        command.Parameters.AddWithValue("@created", now.ToString("O"));
        command.Parameters.AddWithValue("@expires", now.AddHours(1).ToString("O"));
        command.Parameters.AddWithValue("@jobId", "job");
        command.ExecuteNonQuery();
    }

    public static void InsertNegativeRow(
        string dbPath,
        string key,
        AcquisitionError error,
        string? errorJson = null,
        string? provider = null,
        string? recordedAt = null)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            INSERT OR REPLACE INTO cache_acquisition_errors
                (cache_key, error_json, kind, provider, operation, recorded_at, expires_at, job_id)
            VALUES (@key, @json, @kind, @provider, @operation, @recorded, @expires, @jobId);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@json", errorJson ?? JsonSerializer.Serialize(error, AcquisitionJsonOptions.Default));
        command.Parameters.AddWithValue("@kind", JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(error.Kind, AcquisitionJsonOptions.Default))!);
        command.Parameters.AddWithValue("@provider", provider ?? error.Provider);
        command.Parameters.AddWithValue("@operation", error.Operation);
        command.Parameters.AddWithValue("@recorded", recordedAt ?? now.ToString("O"));
        command.Parameters.AddWithValue("@expires", now.AddHours(1).ToString("O"));
        command.Parameters.AddWithValue("@jobId", "job");
        command.ExecuteNonQuery();
    }

    public static void DropTable(string dbPath, string table)
    {
        using var connection = OpenConnection(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE {table};";
        command.ExecuteNonQuery();
    }

    public static int GetIntProperty(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Convert.ToInt32(property.GetValue(value), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static AzdoService CreateAzdoService(IAzdoApiClient api, Type recorderInterface, object recorder)
    {
        var constructors = typeof(AzdoService).GetConstructors()
            .Where(ctor => ctor.GetParameters().Any(parameter => parameter.ParameterType == recorderInterface))
            .ToList();
        Assert.NotEmpty(constructors);
        var constructor = constructors[0];
        var args = constructor.GetParameters()
            .Select(parameter =>
            {
                if (parameter.ParameterType == typeof(IAzdoApiClient))
                    return api;
                if (parameter.ParameterType == typeof(IHelixApiClient))
                    return null;
                if (parameter.ParameterType == recorderInterface)
                    return recorder;
                return parameter.HasDefaultValue ? parameter.DefaultValue : null;
            })
            .ToArray();
        return (AzdoService)constructor.Invoke(args);
    }

    private static SqliteConnection OpenConnection(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}
