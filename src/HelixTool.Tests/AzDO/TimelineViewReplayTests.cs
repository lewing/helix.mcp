using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Mcp.Tools;
using Microsoft.Data.Sqlite;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests.AzDO;

public sealed class TimelineViewReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hlx-timeline-view-{Guid.NewGuid():N}");
    private string Snapshot => Path.Combine(_root, "snapshot");

    [Theory]
    [InlineData("triage")]
    [InlineData("compact")]
    [InlineData("full")]
    [InlineData("summary")]
    public async Task SnapshotOnly_EveryProjection_ReplaysOldCompleteTimelineKeyWithoutNetworkOrNewKeys(string projection)
    {
        var source = TimelineViewFixture.Real(1600801);
        await SeedAsync(source, 1600801);
        var dbPath = Path.Combine(Snapshot, "cache.db");
        var before = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
        var options = new CacheOptions { CacheRoot = Snapshot, EvalMode = true };
        using (var store = new SqliteCacheStore(options))
        {
            var api = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options);
            await using var offline = await TimelineViewHost.StartAsync(source, 1600801, api, options);
            await using var live = await TimelineViewHost.StartAsync(source, 1600801);
            var arguments = new Dictionary<string, object?> { ["filter"] = "all", ["projection"] = projection, ["limit"] = 2 };
            var actual = await offline.TimelineAsync(new(arguments));
            var expected = await live.TimelineAsync(new(arguments));
            Assert.Equal(expected.GetProperty("viewId").GetString(), actual.GetProperty("viewId").GetString());
            Assert.True(JsonElement.DeepEquals(expected.GetProperty("counts"), actual.GetProperty("counts")));
            var replayRows = await offline.ReadPagesAsync(actual);
            var liveRows = await live.ReadPagesAsync(expected);
            Assert.Equal(liveRows.Select(r => r.GetRawText()), replayRows.Select(r => r.GetRawText()));
            Assert.Equal(JsonSerializer.Serialize(source), await store.GetMetadataAsync("azdo:dnceng-public:public:timeline:1600801"));
        }
        Assert.Equal(before, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
    }

    [Fact]
    public async Task SnapshotOnly_OversizedIssue_UsesPrivateDeliveryOutsideSnapshot_AndScopedEvidenceReader()
    {
        var message = string.Concat(Enumerable.Repeat("immutable full issue \U0001f680 \"\\\"\n", 40000));
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            new AzdoIssue { Type = "warning", Message = message });
        await SeedAsync(source);
        var dbPath = Path.Combine(Snapshot, "cache.db");
        var before = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
        var options = new CacheOptions { CacheRoot = Snapshot, EvalMode = true };
        using (var store = new SqliteCacheStore(options))
        {
            await using var host = await TimelineViewHost.StartAsync(source,
                api: new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, options), cacheOptions: options);
            var view = await host.TimelineAsync(new()
            {
                ["recordId"] = TimelineViewFixture.MonitorTask, ["projection"] = "full", ["expand"] = "none", ["delivery"] = "file"
            });
            var delivery = view.GetProperty("delivery");
            var path = Path.GetFullPath(delivery.GetProperty("localPath").GetString()!);
            Assert.False(Path.GetRelativePath(Snapshot, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal));
            Assert.True(File.Exists(path));
            Assert.True(delivery.TryGetProperty("resourceUri", out _));
            var payload = await host.ReadEvidenceAsync(delivery);
            using var document = JsonDocument.Parse(payload);
            Assert.Equal(message, document.RootElement.GetProperty("records")[0].GetProperty("issues")[0].GetProperty("message").GetString());
            await using var otherContext = await TimelineViewHost.StartAsync(source);
            var denied = await otherContext.CallAsync("hlx_read_evidence", new()
            {
                ["evidenceId"] = delivery.GetProperty("evidenceId").GetString()
            });
            Assert.True(denied.IsError, "An evidence ID must not authorize a different cache/credential context.");
        }
        Assert.Equal(before, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
    }

    [Theory]
    [InlineData(1621192)]
    [InlineData(1600801)]
    public async Task StdioWireSize_DefaultEveryPage_IncludesSdkDuplicationAndJsonRpcFraming(int buildId)
    {
        var source = TimelineViewFixture.Real(buildId);
        source = source with
        {
            Records = source.Records.Select(r => r with
            {
                Name = r.Name + string.Concat(Enumerable.Repeat(" quote\" slash\\ \U0001f680 ", 10))
            }).ToArray()
        };
        await AssertStdioDefaultPagesAsync(source, buildId);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(200)]
    [InlineData(400)]
    public async Task StdioDefaultWireBudget_SingleEscapedLongName_StaysWithinBudget(int repeats)
    {
        await AssertStdioDefaultPagesAsync(TimelineViewFixture.EscapedLongName(repeats), 1621192);
    }

    [Theory]
    [InlineData(120)]
    [InlineData(130)]
    [InlineData(140)]
    public async Task StdioDefaultWireBudget_PagedEscapedLongName_EveryPageStaysWithinTarget(int repeats)
    {
        await AssertStdioDefaultPagesAsync(TimelineViewFixture.EscapedLongNameWithSecondRow(repeats), 1621192);
    }

    private async Task AssertStdioDefaultPagesAsync(AzdoTimeline source, int buildId)
    {
        await SeedAsync(source, buildId);
        await using var process = await TimelineStdioProcess.StartAsync(Snapshot);
        var response = await process.CallAsync("azdo_timeline", new() { ["buildIdOrUrl"] = buildId.ToString() });
        var first = response.GetProperty("result");
        var page = TimelineViewHost.Payload(JsonSerializer.Deserialize<CallToolResult>(first, McpJsonUtilities.DefaultOptions)!);
        var viewId = page.GetProperty("viewId").GetString();
        HashSet<string> ids = [];
        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(process.LastResponseBytes, 1, 12288);
            foreach (var id in TimelineViewContractTests.Ids(page))
                Assert.True(ids.Add(id), $"Duplicate record {id} in continuation.");
            Assert.Equal(viewId, page.GetProperty("viewId").GetString());
            if (page.GetProperty("next").ValueKind == JsonValueKind.Null)
            {
                if (page.TryGetProperty("delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object)
                {
                    var path = delivery.GetProperty("localPath").GetString()!;
                    try
                    {
                        Assert.True(delivery.GetProperty("complete").GetBoolean());
                        using var full = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
                        Assert.Equal(page.GetProperty("selectedTotal").GetInt32(), full.RootElement.GetProperty("records").GetArrayLength());
                    }
                    finally
                    {
                        File.Delete(path);
                        File.Delete(Path.ChangeExtension(path, ".meta.json"));
                    }
                    return;
                }
                Assert.Equal(page.GetProperty("selectedTotal").GetInt32(), ids.Count);
                return;
            }
            var action = page.GetProperty("continuation");
            response = await process.CallAsync(action.GetProperty("tool").GetString()!,
                JsonSerializer.Deserialize<Dictionary<string, object?>>(action.GetProperty("arguments").GetRawText())!);
            page = TimelineViewHost.Payload(JsonSerializer.Deserialize<CallToolResult>(response.GetProperty("result"), McpJsonUtilities.DefaultOptions)!);
        }
        Assert.Fail("Stdio pagination did not terminate.");
    }

    [Fact]
    public async Task CliJson_UsesListEnvelope_RawJsonPreservesLegacyFullPhaseShape()
    {
        var source = TimelineViewFixture.Real(1600801);
        await SeedAsync(source, 1600801);
        var json = await RunCliAsync("azdo", "timeline", "1600801", "--json", "--limit", "2");
        Assert.Equal(2, json.ExitCode);
        using (var document = JsonDocument.Parse(json.Stdout))
        {
            var root = document.RootElement;
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal("triage", root.GetProperty("projection").GetString());
            Assert.Equal(root.GetProperty("returned").GetInt32(), root.GetProperty("results").GetArrayLength());
            Assert.True(root.GetProperty("truncated").GetBoolean());
            Assert.False(root.GetProperty("complete").GetBoolean());
            Assert.True(root.GetProperty("next").GetProperty("offset").GetInt32() > 0);
            Assert.True(root.TryGetProperty("cache", out _));
            Assert.True(root.TryGetProperty("continuation", out _));
        }
        var raw = await RunCliAsync("azdo", "timeline", "1600801", "--filter", "all", "--raw-json");
        Assert.Equal(0, raw.ExitCode);
        using var legacy = JsonDocument.Parse(raw.Stdout);
        Assert.Equal(source.Id, legacy.RootElement.GetProperty("id").GetString());
        Assert.False(legacy.RootElement.TryGetProperty("results", out _));
        Assert.False(legacy.RootElement.TryGetProperty("projection", out _));
        Assert.Equal(source.Records.Count, legacy.RootElement.GetProperty("records").GetArrayLength());
        Assert.Contains(legacy.RootElement.GetProperty("records").EnumerateArray(), r => r.GetProperty("type").GetString() == "Phase");
        var warning = Assert.Single(legacy.RootElement.GetProperty("records").EnumerateArray(),
            r => r.GetProperty("id").GetString() == TimelineViewFixture.WarningTask);
        Assert.Equal(6, warning.GetProperty("issues").GetArrayLength());
        Assert.Equal(TimelineViewFixture.AndroidWarning, warning.GetProperty("issues")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CliOutput_ExplicitLargeFullSelection_IsVerifiedCompleteAndExitsZero()
    {
        var message = new string('x', 1024 * 1024);
        var source = TimelineViewFixture.WithIssues(TimelineViewFixture.Real(1621192), TimelineViewFixture.MonitorTask,
            new AzdoIssue { Type = "warning", Message = message });
        await SeedAsync(source);
        var output = Path.Combine(_root, "record.json");
        var result = await RunCliAsync("azdo", "timeline", "1621192", "--record-id", TimelineViewFixture.MonitorTask,
            "--projection", "full", "--expand", "none", "--all", "--output", output, "--json");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(output), result.Stderr);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output));
        var root = document.RootElement;
        var records = root.TryGetProperty("results", out var results) ? results : root.GetProperty("records");
        Assert.Equal(message, records[0].GetProperty("issues")[0].GetProperty("message").GetString());
        Assert.InRange(Encoding.UTF8.GetByteCount(result.Stdout), 1, 12288);
        using var receipt = JsonDocument.Parse(result.Stdout);
        var delivery = receipt.RootElement.GetProperty("delivery");
        Assert.True(delivery.GetProperty("complete").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(output))),
            delivery.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Views_DoNotRegressEvidencePlanMonitorParserOrAzdoHelixJobs()
    {
        var timeline = TimelineViewFixture.Real(1621192);
        var api = Substitute.For<IAzdoApiClient>();
        api.GetTimelineAsync("dnceng-public", "public", 1621192, Arg.Any<CancellationToken>()).Returns(timeline);
        api.GetBuildAsync("dnceng-public", "public", 1621192, Arg.Any<CancellationToken>())
            .Returns(new AzdoBuild { Id = 1621192, Status = "completed", Result = "failed" });
        api.GetBuildArtifactsAsync("dnceng-public", "public", 1621192, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<AzdoBuildArtifact>());
        var service = new AzdoService(api);
        var before = await service.GetEvidencePlanAsync("1621192", new AzdoEvidencePlanOptions());
        await using var host = await TimelineViewHost.StartAsync(timeline, api: api);
        foreach (var projection in new[] { "triage", "compact", "full", "summary" })
            await host.TimelineAsync(new() { ["projection"] = projection });
        var after = await service.GetEvidencePlanAsync("1621192", new AzdoEvidencePlanOptions());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after with { GeneratedAt = before.GeneratedAt }));
        var failure = Assert.Single(after.HelixFailures);
        Assert.Equal("System.Diagnostics.Process.Tests", failure.WorkItem);
        Assert.Equal("d0b6dc7c-c1e1-4fe3-953d-2c97a59d024a", failure.HelixJobId);
        Assert.True(after.Complete);
        var jobs = await service.GetHelixJobsAsync("1621192");
        var job = Assert.Single(jobs.Jobs);
        Assert.Equal(failure.HelixJobId, job.HelixJobId);
        Assert.Contains(failure.WorkItem, job.FailedWorkItems);
        Assert.Equal(TimelineViewFixture.MonitorMessage, timeline.Records[0].Issues![0].Message);
    }

    internal async Task SeedAsync(AzdoTimeline source, int buildId = 1621192)
    {
        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(Path.Combine(_root, "live"), Snapshot, async store =>
        {
            await store.SetMetadataAsync($"azdo:dnceng-public:public:timeline:{buildId}", JsonSerializer.Serialize(source), TimeSpan.FromHours(4));
            await store.SetMetadataAsync($"azdo:dnceng-public:public:build:{buildId}",
                JsonSerializer.Serialize(new AzdoBuild { Id = buildId, Status = "completed" }), TimeSpan.FromHours(4));
        });
        // File-to-file SQLite backup inherits WAL mode; production exports serialize an
        // in-memory database. Normalize before freezing so reads need no new sidecar files.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Snapshot, "cache.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE;";
        Assert.Equal("delete", (string?)await command.ExecuteScalarAsync());
    }

    internal async Task<(string Stdout, string Stderr, int ExitCode)> RunCliAsync(params string[] arguments)
    {
        using var process = new Process { StartInfo = TimelineStdioProcess.CreateStartInfo(Snapshot, arguments) };
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            return (await stdout, await stderr, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    internal string SnapshotPath => Snapshot;
    internal string TemporaryRoot => _root;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

internal sealed class TimelineStdioProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private int _id;
    public int LastResponseBytes { get; private set; }

    private TimelineStdioProcess(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static ProcessStartInfo CreateStartInfo(string snapshot, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory
        };
        // TestHost supplies shared ASP.NET assemblies that the copied CLI deps file
        // alone cannot resolve. Use the running test's dependency graph for the child.
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add("--runtimeconfig");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "HelixTool.Tests.runtimeconfig.json"));
        info.ArgumentList.Add("--depsfile");
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "HelixTool.Tests.deps.json"));
        info.ArgumentList.Add(typeof(global::AzdoCommands).Assembly.Location);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["HLX_EVAL_SNAPSHOT"] = snapshot;
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        info.Environment.Remove("HLX_EVAL_AZDO_PARTITION");
        info.Environment.Remove("AZDO_TOKEN");
        info.Environment.Remove("HELIX_TOKEN");
        return info;
    }

    public static async Task<TimelineStdioProcess> StartAsync(string snapshot)
    {
        var process = new Process { StartInfo = CreateStartInfo(snapshot, "mcp") };
        Assert.True(process.Start());
        var result = new TimelineStdioProcess(process);
        try
        {
            await result.RequestAsync("initialize", new
            {
                protocolVersion = "2025-11-25", capabilities = new { },
                clientInfo = new { name = "timeline-contract-tests", version = "1" }
            });
            await process.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
            await process.StandardInput.FlushAsync();
            return result;
        }
        catch
        {
            await result.DisposeAsync();
            throw;
        }
    }

    public Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments) =>
        RequestAsync("tools/call", new { name = tool, arguments });

    private async Task<JsonElement> RequestAsync(string method, object parameters)
    {
        var id = ++_id;
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await _process.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line is not null, _process.HasExited ? await _stderr : "Stdio server closed stdout.");
            using var response = JsonDocument.Parse(line!);
            if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                LastResponseBytes = Encoding.UTF8.GetByteCount(line!) + 1;
                Assert.False(response.RootElement.TryGetProperty("error", out _), line);
                return response.RootElement.Clone();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        await _stderr;
        _process.Dispose();
    }
}
