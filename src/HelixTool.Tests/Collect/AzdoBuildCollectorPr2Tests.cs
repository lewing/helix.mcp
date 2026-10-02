using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.Collect;

public sealed class AzdoBuildCollectorPr2ContractTests
{
    [Fact]
    public void CollectorPr2_UsesExactDesignTypeNames()
    {
        Assert.NotNull(FindType("HelixTool.Core.Collect.AzdoBuildCollector"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectManifest"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectPolicy"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectFetchAttempt"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectRetryPolicy"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectOutcome"));
        Assert.NotNull(FindType("HelixTool.Core.Collect.CollectCompleteness"));
        Assert.NotNull(FindType("CollectCommands", required: false) ?? FindType("HelixTool.CollectCommands"));
    }

    private static Type? FindType(string fullName, bool required = true)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);

        if (type is null && required)
            Assert.Fail($"Expected design type '{fullName}' to exist.");

        return type;
    }
}

public sealed class AzdoBuildCollectorPr2Tests : IDisposable
{
    private const int BuildId = 1621466;
    private const string HelixJobId = "d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a";
    private const string SecondHelixJobId = "aaaaaaaa-bbbb-cccc-dddd-000000000002";
    private const string WorkItem = "System.Diagnostics.Process.Tests";
    private const string SecondWorkItem = "System.Net.Http.Tests";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-collect-pr2-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, null);
        Environment.SetEnvironmentVariable("AZDO_TOKEN", null);
        Environment.SetEnvironmentVariable("AZDO_TOKEN_TYPE", null);
        TryDelete(_root);
    }

    [Fact]
    public async Task CompleteRuntimeLikeBuild_CollectsEvidenceExportsSnapshotAndReplaysEveryOkManifestItem()
    {
        using var harness = CollectHarness.Create(_root, RuntimeLikeScenario());

        var result = await harness.RunAsync();

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var manifest = manifestDocument.RootElement;
        AssertManifestHeader(manifest, complete: true, exitCode: 0);
        Assert.Equal("manifest/hlx-collect-manifest.json", manifest.GetProperty("snapshot").GetProperty("manifestPath").GetString());
        Assert.True(File.Exists(Path.Combine(harness.ExportPath, "manifest", "hlx-collect-manifest.json")));

        var validation = await SnapshotValidator.ValidateAsync(harness.ExportPath);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));

        AssertAttemptOk(manifest, "get_build");
        AssertAttemptOk(manifest, "get_timeline");
        AssertAttemptOk(manifest, "list_artifacts");
        AssertAttemptOk(manifest, "list_build_logs");
        AssertAttemptOk(manifest, "get_build_log");
        AssertAttemptOk(manifest, "list_test_runs");
        AssertAttemptOk(manifest, "list_test_results");
        AssertAttemptOk(manifest, "list_test_attachments");
        AssertAttemptOk(manifest, "get_helix_work_item");
        AssertAttemptOk(manifest, "get_helix_console_log");
        AssertAttemptOk(manifest, "list_helix_work_item_files");
        AssertNoSecrets(manifest.GetRawText());

        await AssertEvalSnapshotCanServeCollectedManifestItemsAsync(harness.ExportPath);
    }

    [Fact]
    public async Task UncollectedOptionalFile_ReturnsNotInSnapshotInEvalMode()
    {
        using var harness = CollectHarness.Create(_root, RuntimeLikeScenario());

        var result = await harness.RunAsync();

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        var tools = CreateEvalHelixTools(harness.ExportPath);

        var ex = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => tools.Download(HelixJobId, WorkItem, pattern: "results.trx"));
        Assert.Equal(AcquisitionErrorKind.NotInSnapshot, ex.Error.Kind);
        Assert.Equal("cache", ex.Error.Provider);
        Assert.Equal("snapshot", ex.Error.Source);
    }

    [Fact]
    public async Task ScopeFlags_ChangeCollectionPlanAndRecordExplicitPolicySkips()
    {
        using var harness = CollectHarness.Create(_root, RuntimeLikeScenario());

        var result = await harness.RunAsync(options =>
        {
            options.LogScope = "none";
            options.TestScope = "none";
            options.HelixScope = "none";
            options.AllowIncomplete = true;
        });

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var manifest = manifestDocument.RootElement;
        AssertAttemptSkipped(manifest, "get_build_log", "policy_excluded");
        AssertAttemptSkipped(manifest, "list_test_runs", "policy_excluded");
        AssertAttemptSkipped(manifest, "helix_suggested_fetches", "policy_excluded");
        Assert.DoesNotContain(
            Attempts(manifest),
            attempt => IsAttemptOutcome(attempt, "ok") && Operation(attempt) is "get_build_log" or "list_test_runs" or "get_helix_console_log");
    }

    [Fact]
    public async Task IncompleteEvidencePlan_WritesManifestAndExitsTwoByDefault()
    {
        var scenario = RuntimeLikeScenario();
        using var harness = CollectHarness.Create(_root, scenario);

        var result = await harness.RunAsync(options =>
        {
            options.ArtifactPattern = "NoSuchArtifact*";
            options.HelixScope = "none";
            options.TestScope = "none";
            options.LogScope = "none";
        });

        Assert.Null(result.Thrown);
        Assert.Equal(2, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var manifest = manifestDocument.RootElement;
        AssertManifestHeader(manifest, complete: false, exitCode: 2);
        Assert.Contains(
            manifest.GetProperty("incompleteDetails").EnumerateArray(),
            detail => detail.GetProperty("code").GetString() == "artifact_missing");
    }

    [Fact]
    public async Task TransientRetries_RecordTransientAfterBudgetAndBoundConcurrency()
    {
        var scenario = RuntimeLikeScenario(helixFailureCount: 2);
        scenario.Helix.FailConsoleForWorkItems[WorkItem] = new Queue<HlxAcquisitionException>(
        [
            Transient(AcquisitionErrorKind.RateLimited, retryAfterSeconds: 0),
            Transient(AcquisitionErrorKind.Timeout),
            Transient(AcquisitionErrorKind.TransportError)
        ]);
        scenario.Helix.ConsoleDelay = TimeSpan.FromMilliseconds(25);
        using var harness = CollectHarness.Create(_root, scenario);

        var result = await harness.RunAsync(options =>
        {
            options.MaxConcurrency = 1;
            options.RetryCount = 3;
            options.RetryInitialDelay = TimeSpan.Zero;
            options.RetryMaxDelay = TimeSpan.Zero;
            options.TestScope = "none";
            options.LogScope = "none";
        });

        Assert.Null(result.Thrown);
        Assert.Equal(2, result.ExitCode);
        Assert.True(scenario.Helix.MaxObservedConsoleConcurrency <= 1, $"Observed concurrency {scenario.Helix.MaxObservedConsoleConcurrency}.");
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var failedConsole = Attempts(manifestDocument.RootElement)
            .Single(attempt => Operation(attempt) == "get_helix_console_log" && ResourceString(attempt, "workItem") == WorkItem);
        Assert.Equal("failed", failedConsole.GetProperty("outcome").GetString());
        Assert.Equal(3, failedConsole.GetProperty("attemptCount").GetInt32());
        var error = failedConsole.GetProperty("error");
        Assert.Equal("transport_error", error.GetProperty("kind").GetString());
        Assert.Null(await harness.GetAcquisitionErrorAsync(HelixConsoleCacheKey(HelixJobId, WorkItem)));
    }

    [Fact]
    public async Task DeterministicFailure_IsRecordedAndReplayedFromExportedSnapshot()
    {
        var scenario = RuntimeLikeScenario();
        scenario.Helix.FailConsoleForWorkItems[WorkItem] = new Queue<HlxAcquisitionException>([
            AcquisitionAssertions.Exception(
                AcquisitionErrorKind.NotFound,
                "helix",
                "get_helix_console_log",
                new Dictionary<string, object?> { ["jobId"] = HelixJobId, ["workItem"] = WorkItem })
        ]);
        using var harness = CollectHarness.Create(_root, scenario);

        var result = await harness.RunAsync(options =>
        {
            options.TestScope = "none";
            options.LogScope = "none";
        });

        Assert.Null(result.Thrown);
        Assert.Equal(2, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var attempt = Attempts(manifestDocument.RootElement).Single(a => Operation(a) == "get_helix_console_log");
        Assert.Equal("recorded_failure", attempt.GetProperty("outcome").GetString());
        Assert.Equal("not_found", attempt.GetProperty("error").GetProperty("kind").GetString());

        var tools = CreateEvalHelixTools(harness.ExportPath);
        var replayed = await Assert.ThrowsAsync<HlxAcquisitionException>(() => tools.Logs(HelixJobId, WorkItem));
        Assert.Equal(AcquisitionErrorKind.NotFound, replayed.Error.Kind);
        Assert.Equal("snapshot", replayed.Error.Source);
        Assert.True(replayed.Error.Replayed);
    }

    [Fact]
    public async Task SizeCapSkipsAreRecordedExplicitlyWithoutDownloadingArbitraryArtifactBytes()
    {
        var scenario = RuntimeLikeScenario();
        scenario.Helix.Files[WorkItem] =
        [
            new FakeWorkItemFile("small.binlog", "https://helix.dot.net/file/small.binlog?sig=SECRET-SAS-SIGNATURE"),
            new FakeWorkItemFile("oversized.binlog", "https://helix.dot.net/file/oversized.binlog?sig=SECRET-SAS-SIGNATURE")
        ];
        scenario.Helix.FileContent["small.binlog"] = Encoding.UTF8.GetBytes("ok");
        scenario.Helix.FileContent["oversized.binlog"] = Encoding.UTF8.GetBytes("too-large-for-cap");
        using var harness = CollectHarness.Create(_root, scenario);

        var result = await harness.RunAsync(options =>
        {
            options.DownloadHelixFiles = "*.binlog";
            options.MaxFileBytes = 3;
            options.TestScope = "none";
            options.LogScope = "none";
        });

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var ok = Attempts(manifestDocument.RootElement)
            .Single(attempt => Operation(attempt) == "download_helix_file" && ResourceString(attempt, "fileName") == "small.binlog");
        Assert.Equal("ok", ok.GetProperty("outcome").GetString());
        Assert.Equal(2, ok.GetProperty("bytes").GetInt64());

        var skip = Attempts(manifestDocument.RootElement)
            .Single(attempt => Operation(attempt) == "download_helix_file" && ResourceString(attempt, "fileName") == "oversized.binlog");
        Assert.Equal("skipped", skip.GetProperty("outcome").GetString());
        Assert.Equal("size_limit", skip.GetProperty("skip").GetProperty("kind").GetString());
        AssertNoSecrets(manifestDocument.RootElement.GetRawText());

        var tools = CreateEvalHelixTools(harness.ExportPath);
        var download = await tools.Download(HelixJobId, WorkItem, pattern: "small.binlog");
        var downloadedPath = Assert.Single(download.DownloadedFiles);
        Assert.Equal("ok", await File.ReadAllTextAsync(downloadedPath));

        var oversizedMiss = await Assert.ThrowsAsync<HlxAcquisitionException>(
            () => tools.Download(HelixJobId, WorkItem, pattern: "oversized.binlog"));
        Assert.Equal(AcquisitionErrorKind.NotInSnapshot, oversizedMiss.Error.Kind);
    }

    [Fact]
    public async Task TotalCapSkipsSelectedFilesAfterBudgetIsExhausted()
    {
        var scenario = RuntimeLikeScenario();
        scenario.Helix.Files[WorkItem] =
        [
            new FakeWorkItemFile("one.binlog", "https://helix.dot.net/file/one.binlog"),
            new FakeWorkItemFile("two.binlog", "https://helix.dot.net/file/two.binlog")
        ];
        scenario.Helix.FileContent["one.binlog"] = Encoding.UTF8.GetBytes("1234");
        scenario.Helix.FileContent["two.binlog"] = Encoding.UTF8.GetBytes("5678");
        using var harness = CollectHarness.Create(_root, scenario);

        var result = await harness.RunAsync(options =>
        {
            options.DownloadHelixFiles = "*.binlog";
            options.MaxFileBytes = 10;
            options.MaxTotalBytes = 4;
            options.TestScope = "none";
            options.LogScope = "none";
        });

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        Assert.Contains(Attempts(manifestDocument.RootElement), attempt =>
            Operation(attempt) == "download_helix_file"
            && attempt.GetProperty("outcome").GetString() == "skipped"
            && attempt.GetProperty("skip").GetProperty("kind").GetString() == "total_size_limit");
    }

    [Fact]
    public async Task AuthScopedCollectedSnapshot_ReplaysWithoutAzdoToken()
    {
        var credential = new AzdoCredential("super-secret-token", "Bearer", "environment")
        {
            CacheIdentity = "env:AZDO_TOKEN:pat:partition-test",
            DisplayToken = "redacted-display-token"
        };
        using var harness = CollectHarness.Create(_root, RuntimeLikeScenario(), credential);

        var result = await harness.RunAsync();

        Assert.Null(result.Thrown);
        Assert.Equal(0, result.ExitCode);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        var cachePartition = manifestDocument.RootElement.GetProperty("auth").GetProperty("azdo").GetProperty("cachePartition").GetString();
        Assert.StartsWith("cache-", cachePartition, StringComparison.Ordinal);
        Assert.Equal("snapshot_partition", manifestDocument.RootElement.GetProperty("auth").GetProperty("azdo").GetProperty("replay").GetString());
        AssertNoSecrets(manifestDocument.RootElement.GetRawText());

        Environment.SetEnvironmentVariable("AZDO_TOKEN", null);
        Environment.SetEnvironmentVariable("AZDO_TOKEN_TYPE", null);
        var tools = CreateEvalAzdoTools(harness.ExportPath);
        var plan = await tools.EvidencePlan(BuildId.ToString());
        Assert.NotEmpty(plan.HelixFailures);
        var runs = await tools.TestRuns(BuildId.ToString());
        Assert.Single(runs);
    }

    [Fact]
    public async Task MultiPartitionSnapshot_FailsClosedWithoutSelectionAndReplaysWhenSelected()
    {
        var workspace = Path.Combine(_root, "multi-partition");
        var cacheHome = Path.Combine(workspace, "cache-home");
        var snapshot = Path.Combine(workspace, "snapshot");
        Directory.CreateDirectory(workspace);
        var options = new CacheOptions { CacheRoot = cacheHome, MaxSizeBytes = 1024 * 1024 };
        using (var store = new SqliteCacheStore(options))
        {
            await store.SetMetadataAsync(
                "azdo:dnceng-public:public:build:1",
                JsonSerializer.Serialize(new AzdoBuild { Id = 1, Status = "completed" }),
                TimeSpan.FromHours(1));
            options.AuthTokenHash = "abcdef12";
            await store.SetMetadataAsync(
                "azdo:abcdef12:dnceng-public:public:build:2",
                JsonSerializer.Serialize(new AzdoBuild { Id = 2, Status = "completed" }),
                TimeSpan.FromHours(1));
        }
        await SnapshotExporter.ExportAsync(Path.Combine(cacheHome, "public"), snapshot);

        var ambiguous = Assert.Throws<InvalidOperationException>(() => EvalSnapshotAzdoPartitionSelector.Select(snapshot));
        Assert.Contains(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, ambiguous.Message, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, "cache-abcdef12");
        var selected = EvalSnapshotAzdoPartitionSelector.Select(snapshot);
        Assert.Equal("abcdef12", selected.AuthTokenHash);

        var evalOptions = new CacheOptions { CacheRoot = snapshot, EvalMode = true, AuthTokenHash = selected.AuthTokenHash };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);
        using var provider = services.BuildServiceProvider();
        var build = await provider.GetRequiredService<IAzdoApiClient>().GetBuildAsync("dnceng-public", "public", 2);
        Assert.Equal(2, build?.Id);
    }

    [Fact]
    public async Task Resume_VerifiesPriorOkEntriesAndDoesNotRefetchStableResources()
    {
        var scenario = RuntimeLikeScenario();
        using var harness = CollectHarness.Create(_root, scenario);

        var first = await harness.RunAsync();
        var buildCallsAfterFirstRun = scenario.Azdo.GetBuildCalls;
        var consoleCallsAfterFirstRun = scenario.Helix.ConsoleCalls;
        var second = await harness.RunAsync(options =>
        {
            options.Resume = true;
            options.Export = null;
        });

        Assert.Null(first.Thrown);
        Assert.Null(second.Thrown);
        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(buildCallsAfterFirstRun, scenario.Azdo.GetBuildCalls);
        Assert.Equal(consoleCallsAfterFirstRun, scenario.Helix.ConsoleCalls);
        using var manifestDocument = JsonDocument.Parse(File.ReadAllText(harness.ManifestPath));
        Assert.Contains(Attempts(manifestDocument.RootElement), attempt => attempt.GetProperty("outcome").GetString() == "cached");
        AssertAttemptOk(manifestDocument.RootElement, "get_build_log");
        AssertAttemptOk(manifestDocument.RootElement, "list_test_runs");
        AssertAttemptOk(manifestDocument.RootElement, "list_test_results");
        AssertAttemptOk(manifestDocument.RootElement, "get_helix_work_item");
        AssertAttemptOk(manifestDocument.RootElement, "get_helix_console_log");
        AssertAttemptOk(manifestDocument.RootElement, "list_helix_work_item_files");
    }

    private static CollectScenario RuntimeLikeScenario(int helixFailureCount = 1)
    {
        var azdo = new RecordingAzdoApiClient(BuildId, helixFailureCount);
        var helix = new RecordingHelixApiClient();
        if (helixFailureCount > 1)
            helix.AddWorkItem(SecondHelixJobId, SecondWorkItem);
        return new CollectScenario(azdo, helix);
    }

    private static void AssertManifestHeader(JsonElement manifest, bool complete, int exitCode)
    {
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("hlx.collect.azdo-build", manifest.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(manifest.GetProperty("manifestId").GetString()));
        Assert.Equal("hlx collect azdo-build", manifest.GetProperty("command").GetProperty("name").GetString());
        Assert.Equal(BuildId, manifest.GetProperty("source").GetProperty("buildId").GetInt32());
        Assert.Equal(complete, manifest.GetProperty("complete").GetBoolean());
        Assert.Equal(exitCode, manifest.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(default, manifest.GetProperty("generatedAt").GetDateTimeOffset());
        Assert.NotEqual(default, manifest.GetProperty("completedAt").GetDateTimeOffset());
        Assert.True(manifest.GetProperty("summary").GetProperty("attempted").GetInt32() > 0);
        Assert.Equal(6, manifest.GetProperty("policy").GetProperty("maxConcurrency").GetInt32());
        Assert.Equal(52428800, manifest.GetProperty("policy").GetProperty("caps").GetProperty("maxFileBytes").GetInt64());
    }

    private static void AssertAttemptOk(JsonElement manifest, string operation)
    {
        var attempt = Attempts(manifest).FirstOrDefault(attempt => Operation(attempt) == operation);
        Assert.NotEqual(JsonValueKind.Undefined, attempt.ValueKind);
        Assert.True(
            attempt.GetProperty("outcome").GetString() is "ok" or "cached",
            $"{operation} outcome was {attempt.GetProperty("outcome").GetString()}.");
        AssertAttemptShape(attempt);
    }

    private static void AssertAttemptSkipped(JsonElement manifest, string operation, string skipKind)
    {
        var attempt = Attempts(manifest).FirstOrDefault(attempt => Operation(attempt) == operation);
        Assert.NotEqual(JsonValueKind.Undefined, attempt.ValueKind);
        Assert.Equal("skipped", attempt.GetProperty("outcome").GetString());
        Assert.Equal(skipKind, attempt.GetProperty("skip").GetProperty("kind").GetString());
        AssertAttemptShape(attempt);
    }

    private static void AssertAttemptShape(JsonElement attempt)
    {
        Assert.False(string.IsNullOrWhiteSpace(attempt.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(attempt.GetProperty("phase").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(attempt.GetProperty("provider").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(attempt.GetProperty("operation").GetString()));
        Assert.Equal(JsonValueKind.Object, attempt.GetProperty("resource").ValueKind);
        Assert.NotEqual(default, attempt.GetProperty("startedAt").GetDateTimeOffset());
        Assert.NotEqual(default, attempt.GetProperty("finishedAt").GetDateTimeOffset());
        Assert.True(attempt.GetProperty("durationMs").GetInt64() >= 0);
        var minimumAttemptCount = attempt.GetProperty("outcome").GetString() == "skipped" ? 0 : 1;
        Assert.True(attempt.GetProperty("attemptCount").GetInt32() >= minimumAttemptCount);
        Assert.True(attempt.TryGetProperty("bytes", out var bytes), "Every attempt must serialize a stable bytes property.");
        if (attempt.GetProperty("outcome").GetString() is "skipped" or "failed")
            Assert.Equal(JsonValueKind.Null, bytes.ValueKind);
    }

    private static async Task AssertEvalSnapshotCanServeCollectedManifestItemsAsync(string snapshotPath)
    {
        var azdoTools = CreateEvalAzdoTools(snapshotPath);
        var helixTools = CreateEvalHelixTools(snapshotPath);

        var plan = await azdoTools.EvidencePlan(BuildId.ToString());
        Assert.NotEmpty(plan.HelixFailures);

        var log = await azdoTools.Log(BuildId.ToString(), 11);
        Assert.Contains("failed job log", log, StringComparison.Ordinal);

        var runs = await azdoTools.TestRuns(BuildId.ToString());
        Assert.Single(runs);

        var results = await azdoTools.TestResults(BuildId.ToString(), 7001);
        Assert.Single(results);

        var workItem = await helixTools.WorkItem(HelixJobId, WorkItem);
        Assert.Equal(WorkItem, workItem.Name);

        var helixLog = await helixTools.Logs(HelixJobId, WorkItem);
        Assert.Contains("helix console", helixLog, StringComparison.Ordinal);

        var files = await helixTools.Files(HelixJobId, WorkItem);
        Assert.NotEmpty(files.TestResults);
    }

    private static AzdoMcpTools CreateEvalAzdoTools(string snapshotPath)
    {
        var partition = EvalSnapshotAzdoPartitionSelector.Select(snapshotPath);
        var options = new CacheOptions { CacheRoot = snapshotPath, EvalMode = true, AuthTokenHash = partition.AuthTokenHash };
        var store = new SqliteCacheStore(options);
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options);
        var helix = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        return new AzdoMcpTools(new AzdoService(client, helix, new CachingAzdoAcquisitionFailureRecorder(store, options), options), Substitute.For<IAzdoTokenAccessor>());
    }

    private static HelixMcpTools CreateEvalHelixTools(string snapshotPath)
    {
        var options = new CacheOptions { CacheRoot = snapshotPath, EvalMode = true };
        var store = new SqliteCacheStore(options);
        var client = new CachingHelixApiClient(new OfflineHelixApiClient(), store, options);
        return new HelixMcpTools(new HelixService(client, new HttpClient()), Substitute.For<IHelixTokenAccessor>());
    }

    private static IEnumerable<JsonElement> Attempts(JsonElement manifest) =>
        manifest.GetProperty("attempts").EnumerateArray();

    private static string Operation(JsonElement attempt) =>
        attempt.GetProperty("operation").GetString()!;

    private static bool IsAttemptOutcome(JsonElement attempt, string outcome) =>
        attempt.GetProperty("outcome").GetString() == outcome;

    private static string? ResourceString(JsonElement attempt, string name)
    {
        var resource = attempt.GetProperty("resource");
        return resource.TryGetProperty(name, out var value) ? value.GetString() : null;
    }

    private static HlxAcquisitionException Transient(AcquisitionErrorKind kind, int? retryAfterSeconds = null) =>
        AcquisitionAssertions.Exception(
            kind,
            "helix",
            "get_helix_console_log",
            new Dictionary<string, object?> { ["jobId"] = HelixJobId, ["workItem"] = WorkItem },
            retryAfterSeconds: retryAfterSeconds);

    private static string HelixConsoleCacheKey(string jobId, string workItem) =>
        $"job:{SanitizeCacheKeySegment(jobId)}:wi:{SanitizeCacheKeySegment(workItem)}:console";

    private static string SanitizeCacheKeySegment(string value) =>
        value.Replace('/', '_').Replace('\\', '_').Replace("..", "_");

    private static void AssertNoSecrets(string json)
    {
        Assert.DoesNotContain("AZDO_TOKEN", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HELIX_ACCESS_TOKEN", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer ", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET-SAS-SIGNATURE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", json, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed record CollectScenario(RecordingAzdoApiClient Azdo, RecordingHelixApiClient Helix);

    private sealed class CollectHarness : IDisposable
    {
        private readonly string _workspace;
        private readonly RecordingAzdoApiClient _azdo;
        private readonly RecordingHelixApiClient _helix;
        private readonly CacheOptions _cacheOptions;
        private readonly SqliteCacheStore _store;
        private readonly CachingAzdoApiClient _azdoClient;
        private readonly CachingHelixApiClient _helixClient;
        private readonly AzdoService _azdoService;
        private readonly HelixService _helixService;
        private readonly IAzdoTokenAccessor _azdoTokenAccessor = Substitute.For<IAzdoTokenAccessor>();
        private readonly IHelixTokenAccessor _helixTokenAccessor = Substitute.For<IHelixTokenAccessor>();

        private CollectHarness(string root, CollectScenario scenario, AzdoCredential? azdoCredential = null)
        {
            _workspace = Path.Combine(root, Guid.NewGuid().ToString("N"));
            CacheRoot = Path.Combine(_workspace, "cache");
            ManifestPath = Path.Combine(_workspace, "hlx-collect-manifest.json");
            ExportPath = Path.Combine(_workspace, "snapshot");
            Directory.CreateDirectory(_workspace);

            _azdo = scenario.Azdo;
            _helix = scenario.Helix;
            _cacheOptions = new CacheOptions { CacheRoot = CacheRoot, MaxSizeBytes = 128L * 1024 * 1024 };
            _store = new SqliteCacheStore(_cacheOptions);
            _azdoTokenAccessor.GetAccessTokenAsync(Arg.Any<CancellationToken>())
                .Returns(azdoCredential);
            _azdoTokenAccessor.AuthStatusAsync(Arg.Any<CancellationToken>())
                .Returns(azdoCredential is null
                    ? new AzdoAuthStatus { IsAuthenticated = false, Path = "anonymous", Source = "anonymous" }
                    : new AzdoAuthStatus { IsAuthenticated = true, Path = "environment", Source = "AZDO_TOKEN" });
            _azdoClient = new CachingAzdoApiClient(_azdo, _store, _cacheOptions, _azdoTokenAccessor);
            _helixClient = new CachingHelixApiClient(_helix, _store, _cacheOptions);
            _azdoService = new AzdoService(_azdoClient, _helixClient, new CachingAzdoAcquisitionFailureRecorder(_store, _cacheOptions), _cacheOptions);
            _helixService = new HelixService(_helixClient, new HttpClient());
        }

        public string CacheRoot { get; }
        public string ManifestPath { get; }
        public string ExportPath { get; }

        public static CollectHarness Create(string root, CollectScenario scenario, AzdoCredential? azdoCredential = null) =>
            new(root, scenario, azdoCredential);

        public Task<AcquisitionError?> GetAcquisitionErrorAsync(string cacheKey) =>
            _store.GetAcquisitionErrorAsync(cacheKey);

        public async Task<CollectRunResult> RunAsync(Action<CollectInvocationOptions>? configure = null)
        {
            var options = new CollectInvocationOptions
            {
                CacheDir = CacheRoot,
                Manifest = ManifestPath,
                Export = ExportPath
            };
            configure?.Invoke(options);
            if (!options.Resume && Directory.Exists(ExportPath))
                Directory.Delete(ExportPath, recursive: true);

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
                object? returnValue = null;
                try
                {
                    await global::CliAcquisitionErrorPipeline.InvokeAsync(
                        async ct => returnValue = await InvokeCollectAsync(options, ct),
                        options.ToCliArguments(),
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    thrown = ex;
                }

                var exitCode = ExtractExitCode(returnValue, Environment.ExitCode);
                return new CollectRunResult(stdout.ToString(), stderr.ToString(), exitCode, thrown);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
                Environment.ExitCode = originalExitCode;
                TestConsoleCapture.Lock.Release();
            }
        }

        public void Dispose()
        {
            _store.Dispose();
        }

        private async Task<object?> InvokeCollectAsync(CollectInvocationOptions options, CancellationToken ct)
        {
            var commandType = FindRuntimeType("CollectCommands") ?? FindRuntimeType("HelixTool.CollectCommands");
            if (commandType is not null)
            {
                var command = CreateInstance(commandType);
                var method = FindCollectAzdoBuildMethod(commandType);
                var args = BuildArguments(method, options, ct);
                return await InvokeAsync(command, method, args);
            }

            var collectorType = FindRuntimeType("HelixTool.Core.Collect.AzdoBuildCollector");
            Assert.NotNull(collectorType);
            var collector = CreateInstance(collectorType!);
            var collectMethod = collectorType!.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(method => method.Name.Contains("Collect", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(method => method.GetParameters().Length)
                .FirstOrDefault();
            Assert.NotNull(collectMethod);
            return await InvokeAsync(collector, collectMethod!, BuildArguments(collectMethod!, options, ct));
        }

        private object CreateInstance(Type type)
        {
            var constructor = type.GetConstructors()
                .OrderByDescending(ctor => ctor.GetParameters().Length)
                .FirstOrDefault();
            Assert.NotNull(constructor);
            var args = constructor!.GetParameters().Select(parameter => Resolve(parameter.ParameterType)).ToArray();
            return constructor.Invoke(args);
        }

        private object? Resolve(Type type)
        {
            if (type.IsAssignableFrom(typeof(AzdoService))) return _azdoService;
            if (type.IsAssignableFrom(typeof(HelixService))) return _helixService;
            if (type.IsAssignableFrom(typeof(IAzdoApiClient))) return _azdoClient;
            if (type.IsAssignableFrom(typeof(IHelixApiClient))) return _helixClient;
            if (type.IsAssignableFrom(typeof(ICacheStore))) return _store;
            if (type.IsAssignableFrom(typeof(CacheOptions))) return _cacheOptions;
            if (type.IsAssignableFrom(typeof(IAzdoTokenAccessor))) return _azdoTokenAccessor;
            if (type.IsAssignableFrom(typeof(IHelixTokenAccessor))) return _helixTokenAccessor;
            if (type == typeof(IServiceProvider)) return new HarnessServiceProvider(this);
            if (type == typeof(HttpClient)) return new HttpClient();
            if (type == typeof(Lazy<HelixService>)) return new Lazy<HelixService>(() => _helixService);
            if (type.IsValueType) return Activator.CreateInstance(type);
            if (type == typeof(string)) return "";
            if (type.Namespace?.Contains("Collect", StringComparison.OrdinalIgnoreCase) == true && type.GetConstructor(Type.EmptyTypes) is not null)
                return Activator.CreateInstance(type);

            Assert.Fail($"No test dependency mapping for collector constructor parameter type '{type.FullName}'.");
            return null;
        }

        private sealed class HarnessServiceProvider(CollectHarness owner) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                if (serviceType.Name.Equals("AzdoBuildCollector", StringComparison.Ordinal))
                    return owner.CreateInstance(serviceType);
                return owner.Resolve(serviceType);
            }
        }

        private static MethodInfo FindCollectAzdoBuildMethod(Type commandType)
        {
            var methods = commandType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
            var byAttribute = methods.FirstOrDefault(method => method.GetCustomAttributes()
                .Any(attribute => attribute.GetType().Name.Contains("Command", StringComparison.Ordinal)
                                  && AttributeContains(attribute, "collect azdo-build")));
            if (byAttribute is not null)
                return byAttribute;

            var byName = methods.FirstOrDefault(method =>
                method.Name.Equals("AzdoBuild", StringComparison.OrdinalIgnoreCase)
                || method.Name.Equals("AzdoBuildAsync", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("CollectAzdoBuild", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(byName);
            return byName!;
        }

        private static bool AttributeContains(Attribute attribute, string expected)
        {
            foreach (var property in attribute.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetValue(attribute) is string text
                    && text.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return attribute.ToString()?.Contains(expected, StringComparison.OrdinalIgnoreCase) == true;
        }

        private static Type? FindRuntimeType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(name, throwOnError: false)
                    ?? assembly.GetTypes().FirstOrDefault(type => type.Name.Equals(name, StringComparison.Ordinal)))
                .FirstOrDefault(type => type is not null);

        private static object?[] BuildArguments(MethodInfo method, CollectInvocationOptions options, CancellationToken ct)
        {
            var parameters = method.GetParameters();
            var args = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                args[i] = ValueFor(parameters[i], options, ct);
            }
            return args;
        }

        private static object? ValueFor(ParameterInfo parameter, CollectInvocationOptions options, CancellationToken ct)
        {
            var name = parameter.Name ?? "";
            var type = parameter.ParameterType;
            if (type == typeof(CancellationToken)) return ct;
            if (LooksLikeCollectOptions(type))
                return CreateCollectOptions(type, options);
            if (type == typeof(string))
                return StringValue(name, options);
            if (type == typeof(bool)) return BoolValue(name, options);
            if (type == typeof(int) || type == typeof(int?)) return IntValue(name, options);
            if (type == typeof(long) || type == typeof(long?)) return LongValue(name, options);
            if (type == typeof(TimeSpan) || type == typeof(TimeSpan?)) return TimeSpanValue(name, options);
            if (type.IsEnum) return EnumValue(type, StringValue(name, options) ?? "");
            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            Assert.Fail($"No test argument mapping for collect parameter '{name}' ({type.FullName}).");
            return null;
        }

        private static bool LooksLikeCollectOptions(Type type) =>
            type.Namespace?.Contains("Collect", StringComparison.OrdinalIgnoreCase) == true
            && (type.Name.IndexOf("Options", StringComparison.OrdinalIgnoreCase) >= 0
                || type.Name.IndexOf("Request", StringComparison.OrdinalIgnoreCase) >= 0
                || type.Name.IndexOf("Policy", StringComparison.OrdinalIgnoreCase) >= 0);

        private static object CreateCollectOptions(Type type, CollectInvocationOptions options)
        {
            var instance = Activator.CreateInstance(type);
            Assert.NotNull(instance);
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(p => p.CanWrite))
            {
                var value = PropertyValue(property.Name, property.PropertyType, options);
                if (!ReferenceEquals(value, Missing.Value))
                    property.SetValue(instance, value);
            }
            return instance!;
        }

        private static object? PropertyValue(string name, Type type, CollectInvocationOptions options)
        {
            if (name.Equals("Argv", StringComparison.OrdinalIgnoreCase))
                return options.ToCliArguments().ToArray();
            if (name.Equals("Options", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object?>
                {
                    ["logScope"] = options.LogScope,
                    ["testScope"] = options.TestScope,
                    ["helixScope"] = options.HelixScope,
                    ["maxConcurrency"] = options.MaxConcurrency,
                    ["maxFileBytes"] = options.MaxFileBytes,
                    ["maxTotalBytes"] = options.MaxTotalBytes
                };
            if (name.Equals("RetryKinds", StringComparison.OrdinalIgnoreCase)
                && typeof(IEnumerable<AcquisitionErrorKind>).IsAssignableFrom(type))
            {
                return new HashSet<AcquisitionErrorKind>
                {
                    AcquisitionErrorKind.RateLimited,
                    AcquisitionErrorKind.Timeout,
                    AcquisitionErrorKind.TransportError
                };
            }
            if (type == typeof(string) || type == typeof(string[]))
                return StringValue(name, options);
            if (type == typeof(bool) || type == typeof(bool?))
                return BoolValue(name, options);
            if (type == typeof(int) || type == typeof(int?))
                return IntValue(name, options);
            if (type == typeof(long) || type == typeof(long?))
                return LongValue(name, options);
            if (type == typeof(TimeSpan) || type == typeof(TimeSpan?))
                return TimeSpanValue(name, options);
            if (type.IsEnum)
                return EnumValue(type, StringValue(name, options) ?? "");
            if (name.Equals("JobResults", StringComparison.OrdinalIgnoreCase)
                && typeof(IEnumerable<string>).IsAssignableFrom(type))
                return options.JobResults.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Missing.Value;
        }

        private static string? StringValue(string name, CollectInvocationOptions options)
        {
            if (name.Contains("build", StringComparison.OrdinalIgnoreCase)) return BuildId.ToString();
            if (name.Contains("cache", StringComparison.OrdinalIgnoreCase)) return options.CacheDir;
            if (name.Contains("manifest", StringComparison.OrdinalIgnoreCase)) return options.Manifest;
            if (name.Contains("export", StringComparison.OrdinalIgnoreCase) || name.Contains("snapshot", StringComparison.OrdinalIgnoreCase)) return options.Export;
            if (name.Contains("retryKinds", StringComparison.OrdinalIgnoreCase)) return options.RetryKinds;
            if (name.Contains("retryInitialDelay", StringComparison.OrdinalIgnoreCase)) return options.RetryInitialDelay.ToString();
            if (name.Contains("retryMaxDelay", StringComparison.OrdinalIgnoreCase)) return options.RetryMaxDelay.ToString();
            if (name.Contains("artifactPattern", StringComparison.OrdinalIgnoreCase)) return options.ArtifactPattern;
            if (name.Contains("artifactJobPrefix", StringComparison.OrdinalIgnoreCase)) return options.ArtifactJobPrefix;
            if (name.Contains("match", StringComparison.OrdinalIgnoreCase)) return options.Match;
            if (name.Contains("jobResults", StringComparison.OrdinalIgnoreCase)) return options.JobResults;
            if (name.Contains("logScope", StringComparison.OrdinalIgnoreCase)) return options.LogScope;
            if (name.Contains("testScope", StringComparison.OrdinalIgnoreCase)) return options.TestScope;
            if (name.Contains("helixScope", StringComparison.OrdinalIgnoreCase)) return options.HelixScope;
            if (name.Contains("downloadHelixFiles", StringComparison.OrdinalIgnoreCase)) return options.DownloadHelixFiles;
            return null;
        }

        private static object? BoolValue(string name, CollectInvocationOptions options)
        {
            if (name.Contains("json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Contains("resume", StringComparison.OrdinalIgnoreCase)) return options.Resume;
            if (name.Contains("allowIncomplete", StringComparison.OrdinalIgnoreCase)) return options.AllowIncomplete;
            if (name.Contains("keepAttemptPrefix", StringComparison.OrdinalIgnoreCase)) return options.KeepAttemptPrefix;
            return false;
        }

        private static object? IntValue(string name, CollectInvocationOptions options)
        {
            if (name.Contains("maxConcurrency", StringComparison.OrdinalIgnoreCase)) return options.MaxConcurrency;
            if (name.Contains("retryCount", StringComparison.OrdinalIgnoreCase)) return options.RetryCount;
            if (name.Contains("helixFailureLimit", StringComparison.OrdinalIgnoreCase)) return options.HelixFailureLimit;
            if (name.Contains("helixFailureOffset", StringComparison.OrdinalIgnoreCase)) return 0;
            return null;
        }

        private static object? LongValue(string name, CollectInvocationOptions options)
        {
            if (name.Contains("maxFileBytes", StringComparison.OrdinalIgnoreCase)) return options.MaxFileBytes;
            if (name.Contains("maxTotalBytes", StringComparison.OrdinalIgnoreCase)) return options.MaxTotalBytes;
            return null;
        }

        private static object? TimeSpanValue(string name, CollectInvocationOptions options)
        {
            if (name.Contains("initial", StringComparison.OrdinalIgnoreCase)) return options.RetryInitialDelay;
            if (name.Contains("max", StringComparison.OrdinalIgnoreCase)) return options.RetryMaxDelay;
            return null;
        }

        private static object EnumValue(Type enumType, string wireValue)
        {
            foreach (var name in Enum.GetNames(enumType))
            {
                if (name.Equals(wireValue, StringComparison.OrdinalIgnoreCase)
                    || ToKebab(name).Equals(wireValue, StringComparison.OrdinalIgnoreCase))
                    return Enum.Parse(enumType, name);
            }
            return Enum.GetValues(enumType).GetValue(0)!;
        }

        private static string ToKebab(string value)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsUpper(c) && i > 0)
                    builder.Append('-');
                builder.Append(char.ToLowerInvariant(c));
            }
            return builder.ToString();
        }

        private static async Task<object?> InvokeAsync(object target, MethodInfo method, object?[] args)
        {
            try
            {
                var result = method.Invoke(target, args);
                if (result is Task task)
                {
                    await task;
                    var resultProperty = task.GetType().GetProperty("Result");
                    return resultProperty?.GetValue(task);
                }
                return result;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                throw ex.InnerException;
            }
        }

        private static int ExtractExitCode(object? result, int environmentExitCode)
        {
            if (result is int code)
                return code;
            if (result is not null)
            {
                var property = result.GetType().GetProperty("ExitCode", BindingFlags.Instance | BindingFlags.Public);
                if (property?.GetValue(result) is int resultCode)
                    return resultCode;
            }
            return environmentExitCode;
        }
    }

    private sealed record CollectRunResult(string Stdout, string Stderr, int ExitCode, Exception? Thrown);

    private sealed class CollectInvocationOptions
    {
        public string CacheDir { get; init; } = "";
        public string Manifest { get; init; } = "";
        public string? Export { get; set; } = "";
        public bool Resume { get; set; }
        public bool AllowIncomplete { get; set; }
        public int MaxConcurrency { get; set; } = 6;
        public int RetryCount { get; set; } = 3;
        public string RetryKinds { get; set; } = "rate_limited,timeout,transport_error";
        public TimeSpan RetryInitialDelay { get; set; } = TimeSpan.Zero;
        public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.Zero;
        public string ArtifactPattern { get; set; } = "*";
        public string? ArtifactJobPrefix { get; set; }
        public bool KeepAttemptPrefix { get; set; }
        public string Match { get; set; } = "auto";
        public string JobResults { get; set; } = "failed,canceled";
        public string LogScope { get; set; } = "failed";
        public string TestScope { get; set; } = "failed";
        public string HelixScope { get; set; } = "suggested";
        public string? DownloadHelixFiles { get; set; }
        public long MaxFileBytes { get; set; } = 50L * 1024 * 1024;
        public long MaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public int HelixFailureLimit { get; set; } = AzdoEvidencePlan.MaxHelixFailureLimit;

        public IReadOnlyList<string> ToCliArguments()
        {
            var args = new List<string>
            {
                "collect", "azdo-build", BuildId.ToString(),
                "--cache-dir", CacheDir,
                "--manifest", Manifest,
                "--json",
                "--max-concurrency", MaxConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--retry-count", RetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--retry-initial-delay", RetryInitialDelay.ToString(),
                "--retry-max-delay", RetryMaxDelay.ToString(),
                "--log-scope", LogScope,
                "--test-scope", TestScope,
                "--helix-scope", HelixScope,
                "--max-file-bytes", MaxFileBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--max-total-bytes", MaxTotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--helix-failure-limit", HelixFailureLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (!string.IsNullOrWhiteSpace(Export))
                args.AddRange(["--export", Export]);
            if (Resume) args.Add("--resume");
            if (AllowIncomplete) args.Add("--allow-incomplete");
            if (DownloadHelixFiles is not null)
                args.AddRange(["--download-helix-files", DownloadHelixFiles]);
            return args;
        }
    }

    private sealed class RecordingAzdoApiClient : IAzdoApiClient
    {
        private readonly int _buildId;
        private readonly int _helixFailureCount;
        public int GetBuildCalls { get; private set; }

        public RecordingAzdoApiClient(int buildId, int helixFailureCount)
        {
            _buildId = buildId;
            _helixFailureCount = helixFailureCount;
        }

        public Task<AzdoBuild?> GetBuildAsync(string org, string project, int buildId, CancellationToken ct = default)
        {
            GetBuildCalls++;
            return Task.FromResult<AzdoBuild?>(new AzdoBuild
            {
                Id = _buildId,
                BuildNumber = "runtime-20261002.1",
                Status = "completed",
                Result = "failed",
                Definition = new AzdoBuildDefinition { Id = 123, Name = "runtime" },
                Project = new AzdoTeamProjectRef { Name = "public" },
                Repository = new AzdoBuildRepository { Name = "dotnet/runtime", Type = "GitHub", Id = "dotnet/runtime" },
                SourceBranch = "refs/pull/123/head",
                SourceVersion = "abc123",
                Reason = "pullRequest",
                FinishTime = DateTimeOffset.Parse("2026-10-02T18:00:00Z")
            });
        }

        public Task<IReadOnlyList<AzdoBuild>> ListBuildsAsync(string org, string project, AzdoBuildFilter filter, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoBuild>>([]);

        public Task<AzdoTimeline?> GetTimelineAsync(string org, string project, int buildId, CancellationToken ct = default) =>
            Task.FromResult<AzdoTimeline?>(new AzdoTimeline
            {
                Id = "timeline",
                Records =
                [
                    MonitorJob(order: 1),
                    MonitorTask(MonitorWarnings(_helixFailureCount), order: 2),
                    new()
                    {
                        Id = "failed-job",
                        Type = "Job",
                        Name = "linux-x64 Debug Libraries_CheckedCoreCLR",
                        State = "completed",
                        Result = "failed",
                        Log = new AzdoLogReference { Id = 11 },
                        Issues = [new AzdoIssue { Type = "error", Message = "Tests failed." }]
                    },
                    new()
                    {
                        Id = "succeeded-job",
                        Type = "Job",
                        Name = "build",
                        State = "completed",
                        Result = "succeeded",
                        Log = new AzdoLogReference { Id = 12 }
                    }
                ]
            });

        public Task<string?> GetBuildLogAsync(string org, string project, int buildId, int logId, int? startLine = null, int? endLine = null, CancellationToken ct = default) =>
            Task.FromResult<string?>(logId switch
            {
                11 => "failed job log\nerror line\n",
                21 => "monitor log\nmonitor issue\n",
                _ => "other log\n"
            });

        public Task<IReadOnlyList<AzdoBuildChange>> GetBuildChangesAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoBuildChange>>([new() { Id = "abc123", Message = "Change" }]);

        public Task<IReadOnlyList<AzdoTestRun>> GetTestRunsAsync(string org, string project, int buildId, int? top = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoTestRun>>([new() { Id = 7001, Name = "Runtime Tests", State = "completed", TotalTests = 2, FailedTests = 1 }]);

        public Task<IReadOnlyList<AzdoTestResult>> GetTestResultsAsync(string org, string project, int runId, int top = 200, string? outcomes = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoTestResult>>([new() { Id = 8001, TestCaseTitle = "System.Diagnostics.Process.Tests", Outcome = "Failed" }]);

        public Task<IReadOnlyList<AzdoBuildArtifact>> GetBuildArtifactsAsync(string org, string project, int buildId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoBuildArtifact>>([
                new()
                {
                    Id = 9001,
                    Name = "Logs_Build_linux-x64_Debug_Libraries_CheckedCoreCLR",
                    Source = "failed-job",
                    Resource = new AzdoArtifactResource
                    {
                        Type = "PipelineArtifact",
                        DownloadUrl = "https://dev.azure.com/dnceng-public/public/_apis/build/builds/1621466/artifacts?format=zip&sig=SECRET-SAS-SIGNATURE",
                        Properties = new Dictionary<string, string> { ["artifactsize"] = "1024" }
                    }
                }
            ]);

        public Task<IReadOnlyList<AzdoTestAttachment>> GetTestAttachmentsAsync(string org, string project, int runId, int resultId, int top = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoTestAttachment>>([new() { Id = 8100, FileName = "failure.trx", Size = 128 }]);

        public Task<IReadOnlyList<AzdoBuildLogEntry>> GetBuildLogsListAsync(string org, string project, int buildId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AzdoBuildLogEntry>>([
                new() { Id = 11, LineCount = 2 },
                new() { Id = 12, LineCount = 1 },
                new() { Id = 21, LineCount = 2 }
            ]);

        private static AzdoTimelineRecord MonitorJob(int order) => new()
        {
            Id = "monitor-job",
            Type = "Job",
            Name = "Monitor Helix Jobs",
            State = "completed",
            Result = "failed",
            Order = order,
            Attempt = 1,
            Log = new AzdoLogReference { Id = 21 }
        };

        private static AzdoTimelineRecord MonitorTask(IReadOnlyList<AzdoIssue> issues, int order) => new()
        {
            Id = "monitor-task",
            ParentId = "monitor-job",
            Type = "Task",
            Name = "Monitor Helix Jobs",
            State = "completed",
            Result = "failed",
            Order = order,
            Attempt = 1,
            Log = new AzdoLogReference { Id = 21 },
            Issues = issues
        };

        private static IReadOnlyList<AzdoIssue> MonitorWarnings(int count) =>
            Enumerable.Range(0, count).Select(index => new AzdoIssue
            {
                Type = "warning",
                Category = "General",
                Message = $"Work item '{(index == 0 ? WorkItem : SecondWorkItem)}' in job 'runtime leg {index} - queue ({(index == 0 ? HelixJobId : GuidForIndex(index))})' failed (Finished, exit code -3)."
            }).ToList();

        private static string GuidForIndex(int index) =>
            index == 1 ? SecondHelixJobId : $"aaaaaaaa-bbbb-cccc-dddd-{index:000000000000}";
    }

    private sealed class RecordingHelixApiClient : IHelixApiClient
    {
        private int _currentConsoleConcurrency;
        public int MaxObservedConsoleConcurrency { get; private set; }
        public int ConsoleCalls { get; private set; }
        public TimeSpan ConsoleDelay { get; set; }
        public ConcurrentDictionary<string, Queue<HlxAcquisitionException>> FailConsoleForWorkItems { get; } = new();
        public Dictionary<string, IReadOnlyList<IWorkItemFile>> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, byte[]> FileContent { get; } = new(StringComparer.OrdinalIgnoreCase);

        public RecordingHelixApiClient()
        {
            AddWorkItem(HelixJobId, WorkItem);
        }

        public void AddWorkItem(string jobId, string workItem)
        {
            Files[workItem] =
            [
                new FakeWorkItemFile("results.trx", "https://helix.dot.net/file/results.trx")
            ];
        }

        public Task<IJobDetails> GetJobDetailsAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<IJobDetails>(new FakeJobDetails(jobId, Finished: "2026-10-02T18:10:00Z"));

        public Task<IReadOnlyList<IWorkItemSummary>> ListWorkItemsAsync(string jobId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IWorkItemSummary>>([new FakeWorkItemSummary(WorkItem, -3, null)]);

        public Task<IWorkItemDetails> GetWorkItemDetailsAsync(string workItemName, string jobId, CancellationToken ct = default) =>
            Task.FromResult<IWorkItemDetails>(new FakeWorkItemDetails(-3, "Finished", "machine-1", DateTimeOffset.Parse("2026-10-02T18:01:00Z"), DateTimeOffset.Parse("2026-10-02T18:02:00Z")));

        public Task<IReadOnlyList<IWorkItemFile>> ListWorkItemFilesAsync(string workItemName, string jobId, CancellationToken ct = default) =>
            Task.FromResult(Files.TryGetValue(workItemName, out var files) ? files : []);

        public async Task<Stream> GetConsoleLogAsync(string workItemName, string jobId, CancellationToken ct = default)
        {
            ConsoleCalls++;
            var current = Interlocked.Increment(ref _currentConsoleConcurrency);
            MaxObservedConsoleConcurrency = Math.Max(MaxObservedConsoleConcurrency, current);
            try
            {
                if (ConsoleDelay > TimeSpan.Zero)
                    await Task.Delay(ConsoleDelay, ct);
                if (FailConsoleForWorkItems.TryGetValue(workItemName, out var queue) && queue.Count > 0)
                    throw queue.Dequeue();
                return new MemoryStream(Encoding.UTF8.GetBytes($"helix console for {workItemName}\n"));
            }
            finally
            {
                Interlocked.Decrement(ref _currentConsoleConcurrency);
            }
        }

        public Task<Stream> GetFileAsync(string fileName, string workItemName, string jobId, CancellationToken ct = default)
        {
            var bytes = FileContent.TryGetValue(fileName, out var content) ? content : Encoding.UTF8.GetBytes("file");
            return Task.FromResult<Stream>(new MemoryStream(bytes));
        }

        public Task<IReadOnlyList<IHelixJobSummary>> ListJobsByBuildAsync(string source, string buildId, int count = 100_000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IHelixJobSummary>>([]);
    }

    private sealed record FakeJobDetails(string Name, string? Finished) : IJobDetails
    {
        public string? QueueId => "queue";
        public string? QueueAlias => "queue";
        public string? Creator => "tester";
        public string? Source => "source";
        public string? Created => "2026-10-02T18:00:00Z";
        public string? DockerTag => null;
    }

    private sealed record FakeWorkItemSummary(string Name, int? ExitCode, string? ConsoleOutputUri) : IWorkItemSummary;

    private sealed record FakeWorkItemDetails(
        int? ExitCode,
        string? State,
        string? MachineName,
        DateTimeOffset? Started,
        DateTimeOffset? Finished) : IWorkItemDetails;

    private sealed record FakeWorkItemFile(string Name, string? Link) : IWorkItemFile;
}
