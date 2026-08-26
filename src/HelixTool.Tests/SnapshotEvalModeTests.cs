// Snapshot eval-mode composition and integration tests (contract from dallas-snapshot-poc-design.md).
// Verifies: offline stubs, cache-hit/miss paths, DI composition semantics, and an end-to-end
// ci-evidence scenario reading a build from a copied snapshot.
// Lambert: do NOT modify production files.

using System.Security.Cryptography;
using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using HelixTool.Mcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

internal static class SnapshotEvalTestHarness
{
    private const int BufferSize = 81920;

    public static async Task CreateStableSnapshotAsync(
        string liveCacheRoot,
        string snapshotRoot,
        Func<SqliteCacheStore, Task> seedAsync)
    {
        Directory.CreateDirectory(snapshotRoot);

        using (var writer = new SqliteCacheStore(new CacheOptions { CacheRoot = liveCacheRoot }))
        {
            await seedAsync(writer);
        }

        var liveRoot = Path.Combine(liveCacheRoot, "public");
        var liveDbPath = Path.Combine(liveRoot, "cache.db");
        var snapshotDbPath = Path.Combine(snapshotRoot, "cache.db");
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = liveDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using (var source = new SqliteConnection(sourceConnectionString))
        using (var destination = new SqliteConnection(destinationConnectionString))
        {
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        var liveArtifacts = Path.Combine(liveRoot, "artifacts");
        var snapshotArtifacts = Path.Combine(snapshotRoot, "artifacts");
        if (Directory.Exists(liveArtifacts))
        {
            foreach (var sourcePath in Directory.GetFiles(liveArtifacts, "*", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                var relativePath = Path.GetRelativePath(liveArtifacts, sourcePath);
                var destinationPath = Path.Combine(snapshotArtifacts, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await CopySharedAsync(sourcePath, destinationPath);
            }
        }
    }

    public static async Task CopySharedAsync(string sourcePath, string destinationPath)
    {
        await using var source = OpenSharedRead(sourcePath);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination);
    }

    public static async Task<string> HashSharedAsync(string path)
    {
        await using var stream = OpenSharedRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    public static async Task<IReadOnlyDictionary<string, string>> CaptureArtifactHashesAsync(string snapshotRoot)
    {
        var artifactsRoot = Path.Combine(snapshotRoot, "artifacts");
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(artifactsRoot))
            return result;

        foreach (var path in Directory.GetFiles(artifactsRoot, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            result.Add(
                Path.GetRelativePath(artifactsRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
                await HashSharedAsync(path));
        }

        return result;
    }

    public static string? ReadMetadata(string dbPath, string cacheKey)
    {
        using var connection = OpenUnpooledReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json_value FROM cache_metadata WHERE cache_key = @key;";
        command.Parameters.AddWithValue("@key", cacheKey);
        return command.ExecuteScalar() as string;
    }

    public static long ReadUserVersion(string dbPath)
    {
        using var connection = OpenUnpooledReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    private static FileStream OpenSharedRead(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static SqliteConnection OpenUnpooledReadOnly(string dbPath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }
}

// =========================================================================
// OfflineAzdoApiClient stub contract
// =========================================================================

public class OfflineAzdoApiClientTests
{
    // OfflineAzdoApiClient is internal but accessible via InternalsVisibleTo.
    private readonly OfflineAzdoApiClient _sut = new();

    [Fact]
    public async Task GetBuildAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetBuildAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListBuildsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ListBuildsAsync("org", "proj", new AzdoBuildFilter()));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTimelineAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTimelineAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBuildLogAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetBuildLogAsync("org", "proj", 1, 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBuildChangesAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetBuildChangesAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTestRunsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTestRunsAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTestResultsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTestResultsAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBuildArtifactsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetBuildArtifactsAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTestAttachmentsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetTestAttachmentsAsync("org", "proj", 1, 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetBuildLogsListAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetBuildLogsListAsync("org", "proj", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// =========================================================================
// OfflineHelixApiClient stub contract
// =========================================================================

public class OfflineHelixApiClientTests
{
    private readonly OfflineHelixApiClient _sut = new();

    [Fact]
    public async Task GetJobDetailsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetJobDetailsAsync("job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListWorkItemsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ListWorkItemsAsync("job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWorkItemDetailsAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetWorkItemDetailsAsync("wi", "job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListWorkItemFilesAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ListWorkItemFilesAsync("wi", "job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetConsoleLogAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetConsoleLogAsync("wi", "job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetFileAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.GetFileAsync("file.txt", "wi", "job-abc"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListJobNamesByBuildAsync_ThrowsWithEvalModeMessage()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.ListJobNamesByBuildAsync("source", "123"));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

// =========================================================================
// Composition tests: CachingAzdoApiClient + OfflineAzdoApiClient + SqliteCacheStore
// =========================================================================

public class EvalModeCompositionTests : IDisposable
{
    // Snapshot dir layout:
    //   _parentDir/public/cache.db   ← writer (normal mode) puts DB here
    //   _snapshotDir = _parentDir/public ← eval store CacheRoot (DB accessed as-is)
    private readonly string _parentDir;
    private readonly string _snapshotDir;

    public EvalModeCompositionTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-comp-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    private (SqliteCacheStore store, CachingAzdoApiClient client) BuildEvalComposition()
    {
        // Pre-seed schema: a normal-mode store creates the DB with correct schema_version.
        // Eval mode requires an existing valid DB; it won't create one on an empty dir.
        using var seed = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir, EvalMode = false });
        seed.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        var store = new SqliteCacheStore(evalOpts);
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), store, evalOpts);
        return (store, client);
    }

    // ── Cache hit: offline stub never reached ─────────────────────────

    [Fact]
    public async Task CacheHit_ReturnsCachedBuild_WithoutCallingOfflineStub()
    {
        // Seed the snapshot: write a cached build using a normal writer store.
        // Then access it via the eval composition.
        var writerOpts = new CacheOptions { CacheRoot = _parentDir, EvalMode = false, AuthTokenHash = null };
        using var writerStore = new SqliteCacheStore(writerOpts);

        // The cache key produced by CachingAzdoApiClient for GetBuildAsync is:
        // "azdo:{org}:{project}:build:{id}" (no auth hash in eval mode)
        const string cacheKey = "azdo:dnceng-public:public:build:42";
        var build = new AzdoBuild { Id = 42, Status = "completed", BuildNumber = "20240101.1" };
        await writerStore.SetMetadataAsync(cacheKey, JsonSerializer.Serialize(build), TimeSpan.FromHours(4));
        writerStore.Dispose();

        // Open eval composition.
        var (store, client) = BuildEvalComposition();
        using (store)
        {
            var result = await client.GetBuildAsync("dnceng-public", "public", 42);

            Assert.NotNull(result);
            Assert.Equal(42, result!.Id);
            Assert.Equal("completed", result.Status);
            Assert.Equal("20240101.1", result.BuildNumber);
        }
    }

    // ── Cache miss: offline stub throws with "eval mode" ─────────────

    [Fact]
    public async Task CacheMiss_ThrowsInvalidOperationException_WithEvalModeMessage()
    {
        // Snapshot is empty — cache miss must throw, NOT call live AzDO.
        var (store, client) = BuildEvalComposition();
        using (store)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetBuildAsync("dnceng-public", "public", 9999));
            Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task CacheMiss_Timeline_ThrowsWithEvalModeMessage()
    {
        var (store, client) = BuildEvalComposition();
        using (store)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.GetTimelineAsync("dnceng-public", "public", 9999));
            Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Path resolution: relative vs absolute ────────────────────────

    [Fact]
    public void EvalSnapshotPath_RelativePath_ResolvedToAbsolute()
    {
        // The DI wiring in both Program.cs files calls Path.GetFullPath(evalSnapshotDir).
        // This test verifies the contract: a relative path is resolved to an absolute path.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), _snapshotDir);
        var resolved = Path.GetFullPath(relative);

        Assert.True(Path.IsPathRooted(resolved), "Resolved path must be absolute");
        Assert.Equal(Path.GetFullPath(_snapshotDir), resolved);
    }

    [Fact]
    public void EvalSnapshotPath_AbsolutePath_ReturnedUnchanged()
    {
        var absolute = _snapshotDir; // already absolute
        var resolved = Path.GetFullPath(absolute);

        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void GetEffectiveCacheRoot_WithEvalModeAndAbsolutePath_MatchesSnapshotDir()
    {
        // Verify the DI-wired CacheOptions round-trip: CacheRoot = Path.GetFullPath(envVar) → GetEffectiveCacheRoot() = that path.
        var resolved = Path.GetFullPath(_snapshotDir);
        var opts = new CacheOptions { CacheRoot = resolved, EvalMode = true };

        Assert.Equal(resolved, opts.GetEffectiveCacheRoot());
    }
}

// =========================================================================
// End-to-end scenario: ci-evidence operation from a copied snapshot.
// Simulates: warm up normal cache → copy to snapshot dir → read via eval mode.
// =========================================================================

public class SnapshotCiEvidenceScenarioTests : IDisposable
{
    private readonly string _parentDir;
    private readonly string _snapshotDir; // = _parentDir/public

    public SnapshotCiEvidenceScenarioTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-e2e-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EvalMode_CachedBuildAndTimeline_ReturnedWithoutNetworkCalls()
    {
        // Phase 1: Populate a snapshot using normal-mode store (simulates a warm cache capture).
        const string org = "dnceng-public";
        const string project = "public";
        const int buildId = 12345;

        var writerOpts = new CacheOptions { CacheRoot = _parentDir, EvalMode = false, AuthTokenHash = null };
        using (var writerStore = new SqliteCacheStore(writerOpts))
        {
            var build = new AzdoBuild { Id = buildId, Status = "completed", BuildNumber = "20240815.5" };
            var timeline = new AzdoTimeline
            {
                Id = Guid.NewGuid().ToString(),
                Records = new List<AzdoTimelineRecord>
                {
                    new() { Id = Guid.NewGuid().ToString(), Name = "Build", State = "completed", Result = "succeeded" }
                }
            };

            var buildKey = $"azdo:{org}:{project}:build:{buildId}";
            var timelineKey = $"azdo:{org}:{project}:timeline:{buildId}";

            await writerStore.SetMetadataAsync(buildKey, JsonSerializer.Serialize(build), TimeSpan.FromHours(4));
            await writerStore.SetMetadataAsync(timelineKey, JsonSerializer.Serialize(timeline), TimeSpan.FromHours(4));
        }

        // Phase 2: Open snapshot in eval mode and verify reads work — no network calls.
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var evalAzdo = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);
        var evalHelix = new CachingHelixApiClient(new OfflineHelixApiClient(), evalStore, evalOpts);

        var resultBuild = await evalAzdo.GetBuildAsync(org, project, buildId);
        var resultTimeline = await evalAzdo.GetTimelineAsync(org, project, buildId);

        Assert.NotNull(resultBuild);
        Assert.Equal(buildId, resultBuild!.Id);
        Assert.Equal("20240815.5", resultBuild.BuildNumber);

        Assert.NotNull(resultTimeline);
        Assert.Single(resultTimeline!.Records!);
        Assert.Equal("Build", resultTimeline.Records![0].Name);
    }

    [Fact]
    public async Task EvalMode_ExpiredSnapshotEntries_StillReadable()
    {
        // Verify TTL bypass: entries expired 4 hours ago must still be returned.
        var writerOpts = new CacheOptions { CacheRoot = _parentDir, EvalMode = false, AuthTokenHash = null };
        using (var writerStore = new SqliteCacheStore(writerOpts))
        {
            var build = new AzdoBuild { Id = 99, Status = "completed" };
            var buildKey = "azdo:dnceng-public:public:build:99";
            // Delay lets the background eviction complete on the empty DB before writing
            // a TimeSpan.Zero-TTL row, avoiding a race where eviction deletes the just-written row.
            await Task.Delay(30);
            await writerStore.SetMetadataAsync(buildKey, JsonSerializer.Serialize(build), TimeSpan.Zero);
        }

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var evalAzdo = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);

        // Should NOT throw (cache hit despite expired TTL).
        var result = await evalAzdo.GetBuildAsync("dnceng-public", "public", 99);
        Assert.NotNull(result);
        Assert.Equal(99, result!.Id);
    }

    [Fact]
    public async Task EvalMode_CacheMiss_ThrowsDescriptiveException_NeverCallsLiveBackend()
    {
        // Snapshot has correct schema but no data; cache miss must throw "eval mode", not call live AzDO.
        // Pre-seed schema only (no data rows) using a normal writer so the eval store can open.
        using (var seed = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir, EvalMode = false }))
            seed.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var evalAzdo = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => evalAzdo.GetBuildAsync("dnceng-public", "public", 999));

        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NormalMode_UnchangedBehavior_CacheHitAndMissWorkNormally()
    {
        // Regression: normal mode (no eval) behaves exactly as before.
        var opts = new CacheOptions { CacheRoot = _parentDir, MaxSizeBytes = 1024 * 1024 };
        using var store = new SqliteCacheStore(opts);

        // Normal set/get
        await store.SetMetadataAsync("job:abc:details", "{\"v\":1}", TimeSpan.FromHours(1));
        var result = await store.GetMetadataAsync("job:abc:details");
        Assert.Equal("{\"v\":1}", result);

        // Expired returns null
        await store.SetMetadataAsync("job:old:details", "{}", TimeSpan.Zero);
        Assert.Null(await store.GetMetadataAsync("job:old:details"));
    }
}

// =========================================================================
// Eval-mode AzDO auth: environment-only, network-free cache partition replay.
// =========================================================================

[Collection("AzdoTokenEnv")]
public class EvalModeAzdoAuthTests : IDisposable
{
    private readonly string? _originalToken = Environment.GetEnvironmentVariable("AZDO_TOKEN");
    private readonly string? _originalTokenType = Environment.GetEnvironmentVariable("AZDO_TOKEN_TYPE");
    private readonly string _parentDir;
    private readonly string _snapshotDir;

    public EvalModeAzdoAuthTests()
    {
        Environment.SetEnvironmentVariable("AZDO_TOKEN", null);
        Environment.SetEnvironmentVariable("AZDO_TOKEN_TYPE", null);
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-eval-auth-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZDO_TOKEN", _originalToken);
        Environment.SetEnvironmentVariable("AZDO_TOKEN_TYPE", _originalTokenType);
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task EvalAccessor_EnvironmentPat_ReplaysSameAuthScopedCacheKey()
    {
        const string token = "eval-replay-pat";
        Environment.SetEnvironmentVariable("AZDO_TOKEN", token);
        Environment.SetEnvironmentVariable("AZDO_TOKEN_TYPE", "pat");

        var identity = AzdoCredential.BuildCacheIdentity("env:AZDO_TOKEN:pat", token);
        var authHash = CacheOptions.ComputeAuthContextHash(identity);
        var authKey = $"azdo:{authHash}:org:proj:build:42";

        using (var writer = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir }))
        {
            await writer.SetMetadataAsync(
                authKey,
                JsonSerializer.Serialize(new AzdoBuild { Id = 42, Status = "completed" }),
                TimeSpan.FromHours(4));
        }

        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);
        using var provider = services.BuildServiceProvider();

        var build = await provider.GetRequiredService<IAzdoApiClient>()
            .GetBuildAsync("org", "proj", 42);

        Assert.Equal(42, build?.Id);
        Assert.Equal(identity, evalOptions.AuthCacheIdentity);
        Assert.Equal(authHash, evalOptions.AuthTokenHash);
    }

    [Fact]
    public async Task EvalAccessor_WithoutToken_DoesNotGuessAuthPartition_AndMissIsExplicit()
    {
        using (var writer = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir }))
        {
            await writer.SetMetadataAsync(
                "azdo:authonly:org:proj:build:42",
                JsonSerializer.Serialize(new AzdoBuild { Id = 42, Status = "completed" }),
                TimeSpan.FromHours(4));
        }

        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);
        using var provider = services.BuildServiceProvider();

        var status = await provider.GetRequiredService<IAzdoTokenAccessor>().AuthStatusAsync();
        Assert.False(status.IsAuthenticated);
        Assert.Null(evalOptions.AuthTokenHash);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetRequiredService<IAzdoApiClient>().GetBuildAsync("org", "proj", 42));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(evalOptions.AuthTokenHash);
    }
}

// =========================================================================
// Bishop integrity tests: DB/WAL byte immutability and HttpClient blocking.
// =========================================================================

public class EvalModeSnapshotImmutabilityTests : IDisposable
{
    private readonly string _parentDir;
    private readonly string _snapshotDir; // = _parentDir/public

    public EvalModeSnapshotImmutabilityTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-imm-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Committed data written before snapshot must be accessible via eval mode while
    /// a writer connection remains open (preventing auto-checkpoint so frames stay in WAL).
    /// </summary>
    [Fact]
    public async Task EvalMode_WalFilePresentInSnapshot_IsPreserved()
    {
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var walPath = dbPath + "-wal";
        var mainDbOnlyCopyPath = Path.Combine(_snapshotDir, "cache-main-only.db");

        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(
            Path.Combine(_parentDir, "live"),
            _snapshotDir,
            writer => writer.SetMetadataAsync(
                "job:wal-test:data",
                "{\"ok\":true}",
                TimeSpan.FromHours(1)));

        var writerConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();
        using (var rawConn = new SqliteConnection(writerConnectionString))
        {
            rawConn.Open();
            using (var configure = rawConn.CreateCommand())
            {
                configure.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA wal_checkpoint(TRUNCATE);
                    PRAGMA wal_autocheckpoint=0;
                    """;
                configure.ExecuteNonQuery();
            }

            using (var transaction = rawConn.BeginTransaction())
            using (var insert = rawConn.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO cache_metadata (cache_key, json_value, created_at, expires_at, job_id)
                    VALUES ('job:wal-test2:data', '{"wal":true}', datetime('now'), datetime('now','+1 hour'), 'wal-gen');
                    """;
                insert.ExecuteNonQuery();
                transaction.Commit();
            }

            // The committed row must genuinely depend on the WAL. A byte-for-byte copy
            // of only the main database has no companion WAL and therefore cannot see it.
            await SnapshotEvalTestHarness.CopySharedAsync(dbPath, mainDbOnlyCopyPath);
            var copyConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = mainDbOnlyCopyPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using (var mainDbOnly = new SqliteConnection(copyConnectionString))
            {
                mainDbOnly.Open();
                using var absent = mainDbOnly.CreateCommand();
                absent.CommandText =
                    "SELECT COUNT(*) FROM cache_metadata WHERE cache_key = 'job:wal-test2:data';";
                Assert.Equal(0L, (long)absent.ExecuteScalar()!);
            }

            // Capture both persistent files before eval opens the live snapshot. SHM is
            // deliberately excluded: SQLite may legitimately update shared-memory reader
            // metadata, whereas a read-only connection must not change DB or WAL content.
            Assert.True(File.Exists(walPath), "WAL file must exist with writer open and autocheckpoint=0");
            var dbHashBefore = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
            var walHashBefore = await SnapshotEvalTestHarness.HashSharedAsync(walPath);
            Assert.True(new FileInfo(walPath).Length > 0, "WAL must contain the committed row");

            var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
            using (var eval = new SqliteCacheStore(evalOpts))
            {
                Assert.NotNull(await eval.GetMetadataAsync("job:wal-test:data"));
                Assert.NotNull(await eval.GetMetadataAsync("job:wal-test2:data"));
            }

            Assert.True(File.Exists(walPath), "WAL file must not be deleted by eval mode");
            Assert.True(new FileInfo(walPath).Length > 0, "WAL must remain nonempty while writer is open");
            Assert.Equal(dbHashBefore, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
            Assert.Equal(walHashBefore, await SnapshotEvalTestHarness.HashSharedAsync(walPath));

            // Prove eval did not invalidate or replace the original writer connection.
            using (var transaction = rawConn.BeginTransaction())
            using (var postEvalWrite = rawConn.CreateCommand())
            {
                postEvalWrite.Transaction = transaction;
                postEvalWrite.CommandText = """
                    INSERT OR REPLACE INTO cache_metadata (cache_key, json_value, created_at, expires_at, job_id)
                    VALUES ('job:wal-writer-still-valid:data', '{"ok":true}', datetime('now'), datetime('now','+1 hour'), 'wal-gen');
                    SELECT COUNT(*) FROM cache_metadata WHERE cache_key = 'job:wal-writer-still-valid:data';
                    """;
                Assert.Equal(1L, (long)postEvalWrite.ExecuteScalar()!);
                transaction.Commit();
            }
        }
    }

    /// <summary>DB file bytes must be unchanged after eval-mode reads.</summary>
    [Fact]
    public async Task EvalMode_DbFileBytes_UnchangedAfterReads()
    {
        var artifactBytes = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(
            Path.Combine(_parentDir, "live"),
            _snapshotDir,
            async writer =>
            {
                await writer.SetMetadataAsync("job:imm-test:info", "{\"val\":42}", TimeSpan.FromHours(4));
                await using var artifact = new MemoryStream(artifactBytes);
                await writer.SetArtifactAsync("job:imm-test:artifact", artifact);
            });
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var dbHashBefore = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
        var artifactsBefore = await SnapshotEvalTestHarness.CaptureArtifactHashesAsync(_snapshotDir);
        Assert.Equal("{\"val\":42}", SnapshotEvalTestHarness.ReadMetadata(dbPath, "job:imm-test:info"));

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using (var eval = new SqliteCacheStore(evalOpts))
        {
            var result = await eval.GetMetadataAsync("job:imm-test:info");
            Assert.Equal("{\"val\":42}", result);
            await using var artifact = await eval.GetArtifactAsync("job:imm-test:artifact");
            Assert.NotNull(artifact);
            using var artifactCopy = new MemoryStream();
            await artifact!.CopyToAsync(artifactCopy);
            Assert.Equal(artifactBytes, artifactCopy.ToArray());
        }

        Assert.Equal(dbHashBefore, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
        Assert.Equal("{\"val\":42}", SnapshotEvalTestHarness.ReadMetadata(dbPath, "job:imm-test:info"));
        var artifactsAfter = await SnapshotEvalTestHarness.CaptureArtifactHashesAsync(_snapshotDir);
        Assert.Equal(artifactsBefore.ToArray(), artifactsAfter.ToArray());
    }

    /// <summary>Eval mode must reject a DB with wrong schema version without running any DDL.</summary>
    [Fact]
    public async Task EvalMode_WrongSchemaVersion_ThrowsWithoutMutating()
    {
        // Build a tiny SQLite DB with user_version=99 (wrong).
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using (var conn = new SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        var dbHashBefore = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
        var modifiedBefore = File.GetLastWriteTimeUtc(dbPath);
        var schemaBefore = SnapshotEvalTestHarness.ReadUserVersion(dbPath);
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };

        var ex = Assert.Throws<InvalidOperationException>(() => new SqliteCacheStore(evalOpts));
        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(dbHashBefore, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
        Assert.Equal(modifiedBefore, File.GetLastWriteTimeUtc(dbPath));
        Assert.Equal(schemaBefore, SnapshotEvalTestHarness.ReadUserVersion(dbPath));
    }
}

// =========================================================================
// Regression: primary evidence returned independent of freshness markers /
// completion-state rows in eval mode.
// =========================================================================

public class EvalModePrimaryEvidenceTests : IDisposable
{
    private readonly string _parentDir;
    private readonly string _snapshotDir;

    public EvalModePrimaryEvidenceTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-primary-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Cached log content must be served in eval mode even when the short-lived
    /// freshness marker (log-fresh) has been evicted from the snapshot.
    /// </summary>
    [Fact]
    public async Task GetBuildLogAsync_EvalMode_ServesCachedContent_WhenFreshMarkerAbsent()
    {
        const string org = "dnceng-public";
        const string project = "public";
        const int buildId = 1;
        const int logId = 7;

        // Seed snapshot: write log content WITHOUT the fresh marker (simulates evicted marker).
        using var writerStore = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir });
        const string logKey = "azdo:dnceng-public:public:log:1:7";
        const string expectedLog = "log line one\nlog line two\n";
        await writerStore.SetMetadataAsync(logKey, "\0raw\n" + expectedLog, TimeSpan.FromHours(4));
        // Deliberately omit the log-fresh key to simulate expiry.
        writerStore.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOpts);
        // OfflineAzdoApiClient would throw if reached — used as inner to detect regression.
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);

        var result = await client.GetBuildLogAsync(org, project, buildId, logId);

        Assert.Equal(expectedLog, result);
    }

    [Fact]
    public async Task GetBuildLogsListAsync_EvalMode_ServesCachedList_WhenBuildMarkersAbsent()
    {
        const string key = "azdo:dnceng-public:public:logslist:17";
        var expected = new List<AzdoBuildLogEntry>
        {
            new() { Id = 4, Type = "Container", Url = "https://example.invalid/log/4" }
        };

        using var writerStore = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir });
        await writerStore.SetMetadataAsync(
            key,
            JsonSerializer.Serialize(expected),
            TimeSpan.FromHours(4));
        // Deliberately omit both azdo-build state and cached build metadata markers.
        writerStore.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);

        var result = await client.GetBuildLogsListAsync("dnceng-public", "public", 17);

        var entry = Assert.Single(result);
        Assert.Equal(4, entry.Id);
    }

    [Fact]
    public async Task GetBuildLogsListAsync_EvalMode_CacheMissFailsExplicitly()
    {
        using var writerStore = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir });
        writerStore.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOpts);
        var client = new CachingAzdoApiClient(new OfflineAzdoApiClient(), evalStore, evalOpts);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetBuildLogsListAsync("dnceng-public", "public", 17));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("snapshot", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Cached console-log artifact must be served in eval mode even when the job completion
    /// state row is absent from the snapshot (it may have been evicted before the artifact).
    /// </summary>
    [Fact]
    public async Task GetConsoleLogAsync_EvalMode_ServesArtifact_WhenCompletionStateAbsent()
    {
        const string jobId = "eval-job-1";
        const string workItem = "item-A";
        const string logContent = "console output line";

        // Seed snapshot: write the console artifact WITHOUT any job state row.
        using var writerStore = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir });
        var artifactKey = $"job:{jobId}:wi:{workItem}:console";
        using var logStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(logContent));
        await writerStore.SetArtifactAsync(artifactKey, logStream);
        // Deliberately omit SetJobCompletedAsync to simulate absent completion state.
        writerStore.Dispose();

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        using var evalStore = new SqliteCacheStore(evalOpts);
        // OfflineHelixApiClient would throw if reached — used as inner to detect regression.
        var client = new CachingHelixApiClient(new OfflineHelixApiClient(), evalStore, evalOpts);

        using var stream = await client.GetConsoleLogAsync(workItem, jobId);
        using var reader = new StreamReader(stream);
        var text = await reader.ReadToEndAsync();

        Assert.Equal(logContent, text);
    }
}

// =========================================================================
// Regression: ClearAsync rejects in eval mode before mutating any state.
// =========================================================================

public class EvalModeClearRejectionTests : IDisposable
{
    private readonly string _parentDir;
    private readonly string _snapshotDir;

    public EvalModeClearRejectionTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-clear-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { }
    }

    /// <summary>
    /// ClearAsync must throw InvalidOperationException in eval mode without deleting
    /// any artifact files or modifying the database.
    /// </summary>
    [Fact]
    public async Task ClearAsync_EvalMode_ThrowsAndPreservesSnapshot()
    {
        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(
            Path.Combine(_parentDir, "live"),
            _snapshotDir,
            async writer =>
            {
                await writer.SetMetadataAsync("job:clr-test:info", "{\"v\":1}", TimeSpan.FromHours(4));
                await using var artifact = new MemoryStream(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
                await writer.SetArtifactAsync("job:clr-test:artifact", artifact);
            });
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var dbHashBefore = await SnapshotEvalTestHarness.HashSharedAsync(dbPath);
        var artifactsBefore = await SnapshotEvalTestHarness.CaptureArtifactHashesAsync(_snapshotDir);

        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        using (var evalStore = new SqliteCacheStore(evalOpts))
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => evalStore.ClearAsync());
            Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("read-only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(dbHashBefore, await SnapshotEvalTestHarness.HashSharedAsync(dbPath));
        Assert.Equal("{\"v\":1}", SnapshotEvalTestHarness.ReadMetadata(dbPath, "job:clr-test:info"));
        var artifactsAfter = await SnapshotEvalTestHarness.CaptureArtifactHashesAsync(_snapshotDir);
        Assert.Equal(artifactsBefore.ToArray(), artifactsAfter.ToArray());
    }
}

/// <summary>
/// Exercises the three DI composition entry points under HLX_EVAL_SNAPSHOT to ensure
/// every HelixService instance has a blocking HttpClient, never a real one.
/// This is a regression guard: a factory-resolved client would let file downloads
/// reach the network even in offline eval mode.
/// </summary>
public class EvalModeHelixServiceCompositionTests : IDisposable
{
    // Follow the same parent-dir/public pattern used by EvalModeSnapshotImmutabilityTests:
    //   _parentDir          → normal-mode CacheRoot  (DB at _parentDir/public/cache.db)
    //   _snapshotDir        → eval-mode  CacheRoot   (= _parentDir/public)
    // A schema-only seed is created in the constructor so the read-only SqliteCacheStore
    // can open an existing cache.db — it cannot create the file in ReadOnly mode.
    private readonly string _parentDir;
    private readonly string _snapshotDir;

    public EvalModeHelixServiceCompositionTests()
    {
        _parentDir = Path.Combine(Path.GetTempPath(), $"hlx-eval-composition-{Guid.NewGuid():N}");
        _snapshotDir = Path.Combine(_parentDir, "public");
        Directory.CreateDirectory(_snapshotDir);
        // Seed a valid schema-only cache.db so eval-mode SqliteCacheStore can open it.
        using var seed = new SqliteCacheStore(new CacheOptions { CacheRoot = _parentDir, EvalMode = false });
    }

    public void Dispose()
    {
        try { Directory.Delete(_parentDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Entry-point A: CLI + embedded MCP (singleton lifetime) ──────────────────

    /// <summary>
    /// Uses the production <see cref="EvalModeServices.AddEvalModeCore"/> helper
    /// with Singleton lifetime, exactly as the CLI/embedded-MCP Program.cs does.
    /// </summary>
    [Fact]
    public async Task CliEvalMode_HelixService_HttpClient_IsBlocking()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);   // production helper, Singleton

        using var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<HelixService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CliEvalMode_ResolvesProductionAzdoCommand_WithSingletonAccessor()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);
        services.AddSingleton<AzdoService>(sp =>
            new AzdoService(sp.GetRequiredService<IAzdoApiClient>(), sp.GetRequiredService<IHelixApiClient>()));
        services.AddSingleton<global::AzdoCommands>();

        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<IAzdoTokenAccessor>();

        Assert.NotNull(provider.GetRequiredService<global::AzdoCommands>());
        Assert.Same(accessor, provider.GetRequiredService<IAzdoTokenAccessor>());
    }

    // ── Entry-point B: Standalone MCP (scoped lifetime) ─────────────────────────

    /// <summary>
    /// Uses the production <see cref="EvalModeServices.AddEvalModeCore"/> helper
    /// with Scoped lifetime, exactly as the standalone MCP Program.cs does.
    /// </summary>
    [Fact]
    public async Task StandaloneMcpEvalMode_HelixService_HttpClient_IsBlocking()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions, ServiceLifetime.Scoped);  // production helper, Scoped

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<HelixService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandaloneMcpEvalMode_ResolvesProductionTool_WithScopedAccessor()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions, ServiceLifetime.Scoped);
        services.AddScoped<AzdoService>(sp =>
            new AzdoService(sp.GetRequiredService<IAzdoApiClient>(), sp.GetRequiredService<IHelixApiClient>()));
        services.AddScoped<AzdoMcpTools>();

        using var provider = services.BuildServiceProvider();
        IAzdoTokenAccessor firstAccessor;
        using (var firstScope = provider.CreateScope())
        {
            firstAccessor = firstScope.ServiceProvider.GetRequiredService<IAzdoTokenAccessor>();
            Assert.NotNull(firstScope.ServiceProvider.GetRequiredService<AzdoMcpTools>());
            Assert.Same(
                firstAccessor,
                firstScope.ServiceProvider.GetRequiredService<IAzdoTokenAccessor>());
        }

        using var secondScope = provider.CreateScope();
        Assert.NotSame(
            firstAccessor,
            secondScope.ServiceProvider.GetRequiredService<IAzdoTokenAccessor>());
    }

    // ── Entry-point C: Embedded MCP (singleton lifetime + AzdoService extra) ───

    /// <summary>
    /// Mirrors the embedded-MCP Program.cs path: calls the shared
    /// <see cref="EvalModeServices.AddEvalModeCore"/> helper (Singleton) and then
    /// registers the embedded-MCP-specific <see cref="AzdoService"/> on top.
    /// Verifies that AzdoService is resolvable and that HelixService still blocks.
    /// </summary>
    [Fact]
    public async Task EmbeddedMcpEvalMode_AzdoServiceResolvable_And_HelixServiceIsBlocking()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);   // Singleton — same as embedded MCP
        services.AddSingleton<AzdoService>(sp =>
            new AzdoService(sp.GetRequiredService<IAzdoApiClient>(), sp.GetRequiredService<IHelixApiClient>()));
        services.AddSingleton<AzdoMcpTools>();

        using var provider = services.BuildServiceProvider();

        // Embedded-MCP-specific extra: AzdoService must be resolvable.
        var azdo = provider.GetRequiredService<AzdoService>();
        Assert.NotNull(azdo);
        Assert.NotNull(provider.GetRequiredService<AzdoMcpTools>());

        // Core eval guarantee: HelixService still uses the blocking HTTP handler.
        var svc = provider.GetRequiredService<HelixService>();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── IAzdoApiClient is also offline ────────────────────────────────────────

    /// <summary>Production helper must register an offline (non-network) AzDO client.</summary>
    [Fact]
    public async Task EvalModeCore_AzdoApiClient_IsOffline()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddEvalModeCore(evalOptions);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAzdoApiClient>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetBuildAsync("org", "project", 1));
        Assert.Contains("eval mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Negative: verify a factory-resolved client would pass through (non-blocking) ──

    [Fact]
    public async Task FactoryResolvedClient_WithoutBlockingHandler_DoesNotThrowOnCreation()
    {
        // This shows the regression: if IHttpClientFactory.CreateClient is used in eval mode,
        // HelixService construction succeeds but real HTTP is reachable.
        var services = new ServiceCollection();
        services.AddHttpClient("HelixDownload", HelixToolUserAgent.Apply);
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient("HelixDownload");

        // The client itself does NOT throw — the blocking is absent.
        // We verify that EvalModeBlockingHandler IS what makes it throw.
        var mockApi = NSubstitute.Substitute.For<IHelixApiClient>();
        var svcWithFactory = new HelixService(mockApi, client);
        // DownloadFromUrlAsync would reach the network here (no throw from handler).
        // We just verify the service constructs without error — the difference from the eval-mode tests above.
        Assert.NotNull(svcWithFactory);
    }
}

public class EvalModeBlockingHandlerTests
{
    [Fact]
    public async Task Send_ThrowsInvalidOperationException()
    {
        using var client = new HttpClient(new EvalModeBlockingHandler());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetAsync("https://helix.dot.net/api/test"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("eval", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_AnyHttpMethod_Throws()
    {
        using var client = new HttpClient(new EvalModeBlockingHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.PostAsync("https://example.com/data", new StringContent("{}")));
    }

    [Fact]
    public async Task HelixService_DownloadFromUrl_InEvalMode_ThrowsViaBlockingHandler()
    {
        var mockApi = NSubstitute.Substitute.For<IHelixApiClient>();
        var svc = new HelixService(mockApi, new HttpClient(new EvalModeBlockingHandler()));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
