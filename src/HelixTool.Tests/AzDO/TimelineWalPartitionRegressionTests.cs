using System.Diagnostics;
using System.Text.Json;
using HelixTool.Core.Acquisition;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HelixTool.Tests.AzDO;

[Collection("AzdoTokenEnv")]
public sealed class TimelineWalPartitionRegressionTests : IDisposable
{
    private readonly string? _originalPartition = Environment.GetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable);

    public TimelineWalPartitionRegressionTests() =>
        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReReview1_AutoDiscovery_SeesAuthMetadataOrAcquisitionErrorCommittedOnlyInWal(bool errorOnly)
    {
        using var fixture = await WalFixture.CreateAsync(publicInMain: false, errorOnly);
        var before = await fixture.CapturePersistentAsync();
        var selected = EvalSnapshotAzdoPartitionSelector.Select(fixture.Snapshot);
        Assert.Equal("cache-feedface", selected.Partition);
        Assert.Equal("feedface", selected.AuthTokenHash);
        var cli = await fixture.RunCliAsync(partition: null);
        using var response = JsonDocument.Parse(cli.Stdout);
        if (errorOnly)
        {
            Assert.Equal(1, cli.ExitCode);
            Assert.Equal("access_denied", response.RootElement.GetProperty("error").GetProperty("kind").GetString());
        }
        else
        {
            Assert.Equal(0, cli.ExitCode);
            Assert.Equal("Authenticated WAL record", response.RootElement.GetProperty("results")[0].GetProperty("name").GetString());
        }
        Assert.Equal(before.OrderBy(p => p.Key), (await fixture.CapturePersistentAsync()).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReReview1_AutoDiscovery_PublicMainPlusAuthWal_FailsClosedWithoutMutatingEvidence(bool errorOnly)
    {
        using var fixture = await WalFixture.CreateAsync(publicInMain: true, errorOnly);
        var before = await fixture.CapturePersistentAsync();
        var error = Assert.Throws<InvalidOperationException>(() => EvalSnapshotAzdoPartitionSelector.Select(fixture.Snapshot));
        Assert.Contains("multiple", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cache-feedface", error.Message, StringComparison.Ordinal);
        var cli = await fixture.RunCliAsync(partition: null);
        Assert.NotEqual(0, cli.ExitCode);
        Assert.Contains("multiple", cli.Stdout + cli.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before.OrderBy(p => p.Key), (await fixture.CapturePersistentAsync()).OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReReview1_ExplicitWalPartition_IsAccepted_AndCliReplaysMetadataOrRecordedFailure(bool errorOnly)
    {
        using var fixture = await WalFixture.CreateAsync(publicInMain: true, errorOnly);
        var before = await fixture.CapturePersistentAsync();
        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, "cache-feedface");
        var selected = EvalSnapshotAzdoPartitionSelector.Select(fixture.Snapshot);
        Assert.Equal("feedface", selected.AuthTokenHash);
        var cli = await fixture.RunCliAsync("cache-feedface");
        using var response = JsonDocument.Parse(cli.Stdout);
        if (errorOnly)
        {
            Assert.Equal(1, cli.ExitCode);
            Assert.Equal("access_denied", response.RootElement.GetProperty("error").GetProperty("kind").GetString());
            Assert.Equal("get_timeline", response.RootElement.GetProperty("error").GetProperty("operation").GetString());
        }
        else
        {
            Assert.Equal(0, cli.ExitCode);
            Assert.Equal("Authenticated WAL record", Assert.Single(response.RootElement.GetProperty("results").EnumerateArray())
                .GetProperty("name").GetString());
        }
        Assert.Equal(before.OrderBy(p => p.Key), (await fixture.CapturePersistentAsync()).OrderBy(p => p.Key));
    }

    public void Dispose() =>
        Environment.SetEnvironmentVariable(EvalSnapshotAzdoPartitionSelector.EnvironmentVariable, _originalPartition);

    private sealed class WalFixture : IDisposable
    {
        private const string AuthKey = "azdo:feedface:dnceng-public:public:timeline:1621192";
        private readonly string _root;
        private readonly SqliteConnection _writer;
        private readonly string[] _initialFiles;
        public string Snapshot { get; }

        private WalFixture(string root, string snapshot, SqliteConnection writer)
        {
            _root = root; Snapshot = snapshot; _writer = writer;
            _initialFiles = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(snapshot, p)).Order().ToArray();
        }

        public static async Task<WalFixture> CreateAsync(bool publicInMain, bool errorOnly)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hlx-wal-partition-{Guid.NewGuid():N}");
            var options = new CacheOptions { CacheRoot = root };
            using (var store = new SqliteCacheStore(options))
            {
                await store.StartupMaintenance;
                var key = publicInMain ? "azdo:dnceng-public:public:timeline:1621192" : "job:main-only:info";
                var value = publicInMain ? JsonSerializer.Serialize(TimelineViewFixture.Real(1621192)) : "{}";
                await store.SetMetadataAsync(key, value, TimeSpan.FromHours(4));
            }
            var snapshot = options.GetEffectiveCacheRoot();
            var path = Path.Combine(snapshot, "cache.db");
            var writer = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
            }.ToString());
            await writer.OpenAsync();
            using (var setup = writer.CreateCommand())
            {
                setup.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);";
                await setup.ExecuteNonQueryAsync();
            }
            var mainHash = await SnapshotEvalTestHarness.HashSharedAsync(path);
            using (var transaction = writer.BeginTransaction())
            using (var insert = writer.CreateCommand())
            {
                insert.Transaction = transaction;
                if (errorOnly)
                {
                    insert.CommandText = """
                        INSERT INTO cache_acquisition_errors
                          (cache_key,error_json,kind,provider,operation,recorded_at,expires_at,job_id)
                        VALUES (@key,@json,'access_denied','azdo','get_timeline',datetime('now'),'2099-01-01T00:00:00Z','1621192');
                        """;
                    insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(AcquisitionAssertions.Exception(
                        AcquisitionErrorKind.AccessDenied, "azdo", "get_timeline",
                        new Dictionary<string, object?> { ["buildId"] = 1621192 }, httpStatus: 403).Error));
                }
                else
                {
                    insert.CommandText = """
                        INSERT INTO cache_metadata (cache_key,json_value,created_at,expires_at,job_id)
                        VALUES (@key,@json,datetime('now'),'2099-01-01T00:00:00Z','1621192');
                        """;
                    var source = TimelineViewFixture.EscapedLongName(1);
                    source = source with { Records = [source.Records[0] with { Name = "Authenticated WAL record" }] };
                    insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(source));
                }
                insert.Parameters.AddWithValue("@key", AuthKey);
                await insert.ExecuteNonQueryAsync();
                transaction.Commit();
            }
            Assert.Equal(mainHash, await SnapshotEvalTestHarness.HashSharedAsync(path));
            Assert.True(new FileInfo(path + "-wal").Length > 0);
            return new(root, snapshot, writer);
        }

        public async Task<Dictionary<string, string>> CapturePersistentAsync()
        {
            Assert.Equal(_initialFiles, Directory.EnumerateFiles(Snapshot, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Snapshot, p)).Order());
            Dictionary<string, string> hashes = [];
            foreach (var path in Directory.EnumerateFiles(Snapshot, "*", SearchOption.AllDirectories))
            {
                // Existing SHM is SQLite's transient reader-lock state, not committed evidence.
                // The database/WAL and all other evidence files must remain byte-identical.
                if (!path.EndsWith("-shm", StringComparison.Ordinal))
                    hashes[Path.GetRelativePath(Snapshot, path)] = await SnapshotEvalTestHarness.HashSharedAsync(path);
            }
            return hashes;
        }

        public async Task<(string Stdout, string Stderr, int ExitCode)> RunCliAsync(string? partition)
        {
            var start = TimelineStdioProcess.CreateStartInfo(Snapshot, "azdo", "timeline", "1621192",
                "--filter", "all", "--projection", "full", "--expand", "none", "--all", "--json");
            if (partition is not null)
                start.Environment[EvalSnapshotAzdoPartitionSelector.EnvironmentVariable] = partition;
            using var process = new Process { StartInfo = start };
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

        public void Dispose()
        {
            _writer.Dispose();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
