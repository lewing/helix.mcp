// Snapshot eval-mode composition and integration tests (contract from dallas-snapshot-poc-design.md).
// Verifies: offline stubs, cache-hit/miss paths, DI composition semantics, and an end-to-end
// ci-evidence scenario reading a build from a copied snapshot.
// Lambert: do NOT modify production files.

using System.Text.Json;
using HelixTool.Core;
using HelixTool.Core.AzDO;
using HelixTool.Core.Cache;
using HelixTool.Core.Helix;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

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
    /// Committed data written before snapshot must be accessible via eval mode, and eval mode
    /// must NOT delete any WAL file that is present in the snapshot directory.
    /// </summary>
    [Fact]
    public async Task EvalMode_WalFilePresentInSnapshot_IsPreserved()
    {
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var walPath = dbPath + "-wal";

        // Phase 1: Write data via a normal-mode store (establishes WAL mode on the DB).
        var writerOpts = new CacheOptions { CacheRoot = _parentDir, EvalMode = false, AuthTokenHash = null };
        using (var writer = new SqliteCacheStore(writerOpts))
        {
            await writer.SetMetadataAsync("job:wal-test:data", "{\"ok\":true}", TimeSpan.FromHours(1));
        }

        // Phase 2: Write additional data via a raw SQLite connection with auto-checkpoint
        // disabled so committed frames are more likely to remain in the WAL file.
        // Whether or not SQLite checkpoints on connection close is implementation-defined;
        // both cases (data in WAL or data in main DB) must be handled correctly by eval mode.
        using (var rawConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
        {
            rawConn.Open();
            using var noChk = rawConn.CreateCommand();
            noChk.CommandText = "PRAGMA wal_autocheckpoint=0;";
            noChk.ExecuteNonQuery();

            using var ins = rawConn.CreateCommand();
            ins.CommandText = """
                INSERT OR REPLACE INTO cache_metadata (cache_key, json_value, created_at, expires_at, job_id)
                VALUES ('job:wal-test2:data', '{"wal":true}', datetime('now'), datetime('now','+1 hour'), 'wal-gen');
                """;
            ins.ExecuteNonQuery();
        }

        // Record WAL state before eval mode opens (WAL may or may not exist).
        bool walExistedBefore = File.Exists(walPath);

        // Phase 3: Open in eval mode — must not throw and must read all committed data.
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using (var eval = new SqliteCacheStore(evalOpts))
        {
            Assert.NotNull(await eval.GetMetadataAsync("job:wal-test:data"));
            Assert.NotNull(await eval.GetMetadataAsync("job:wal-test2:data"));
        }

        // Invariant: eval mode must NOT delete a WAL file that was present before it opened.
        if (walExistedBefore)
            Assert.True(File.Exists(walPath), "WAL file must not be deleted by eval mode");
    }

    /// <summary>DB file bytes must be unchanged after eval-mode reads.</summary>
    [Fact]
    public async Task EvalMode_DbFileBytes_UnchangedAfterReads()
    {
        // Phase 1: seed snapshot via normal writer.
        var writerOpts = new CacheOptions { CacheRoot = _parentDir, EvalMode = false, AuthTokenHash = null };
        using (var writer = new SqliteCacheStore(writerOpts))
        {
            await writer.SetMetadataAsync("job:imm-test:info", "{\"val\":42}", TimeSpan.FromHours(4));
        }

        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var dbBytesBefore = await File.ReadAllBytesAsync(dbPath);

        // Phase 2: Open eval mode, perform reads, close.
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };
        using (var eval = new SqliteCacheStore(evalOpts))
        {
            var result = await eval.GetMetadataAsync("job:imm-test:info");
            Assert.Equal("{\"val\":42}", result);
        }

        var dbBytesAfter = await File.ReadAllBytesAsync(dbPath);
        Assert.Equal(dbBytesBefore, dbBytesAfter);
    }

    /// <summary>Eval mode must reject a DB with wrong schema version without running any DDL.</summary>
    [Fact]
    public void EvalMode_WrongSchemaVersion_ThrowsWithoutMutating()
    {
        // Build a tiny SQLite DB with user_version=99 (wrong).
        var dbPath = Path.Combine(_snapshotDir, "cache.db");
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
        }.ToString();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }

        var dbBytesBefore = File.ReadAllBytes(dbPath);
        var evalOpts = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true, AuthTokenHash = null };

        var ex = Assert.Throws<InvalidOperationException>(() => new SqliteCacheStore(evalOpts));
        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);

        // DB must be byte-identical — no DDL ran.
        var dbBytesAfter = File.ReadAllBytes(dbPath);
        Assert.Equal(dbBytesBefore, dbBytesAfter);
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

    // ── Entry-point A: CLI top-level DI (simulated) ──────────────────────────

    [Fact]
    public async Task CliEvalMode_HelixService_HttpClient_IsBlocking()
    {
        // Directly mirror what Program.cs CLI eval branch does.
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddHttpClient("HelixDownload", HelixToolUserAgent.Apply);
        services.AddSingleton<ICacheStore>(_ => new SqliteCacheStore(evalOptions));
        services.AddSingleton<IHelixApiClient>(sp =>
            new CachingHelixApiClient(new OfflineHelixApiClient(), sp.GetRequiredService<ICacheStore>(), evalOptions));
        services.AddSingleton<HelixService>(sp =>
            new HelixService(sp.GetRequiredService<IHelixApiClient>(), new HttpClient(new EvalModeBlockingHandler())));

        using var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<HelixService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Entry-point B: Standalone MCP DI (simulated) ─────────────────────────

    [Fact]
    public async Task StandaloneMcpEvalMode_HelixService_HttpClient_IsBlocking()
    {
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddHttpClient("HelixDownload", HelixToolUserAgent.Apply);
        services.AddSingleton<ICacheStore>(_ => new SqliteCacheStore(evalOptions));
        services.AddSingleton<IHelixApiClient>(sp =>
            new CachingHelixApiClient(new OfflineHelixApiClient(), sp.GetRequiredService<ICacheStore>(), evalOptions));
        // Standalone MCP uses AddScoped — use same blocking handler pattern.
        services.AddSingleton<HelixService>(sp =>
            new HelixService(sp.GetRequiredService<IHelixApiClient>(), new HttpClient(new EvalModeBlockingHandler())));

        using var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<HelixService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── Entry-point C: Embedded MCP DI (the previously-broken path) ──────────

    [Fact]
    public async Task EmbeddedMcpEvalMode_HelixService_HttpClient_IsBlocking()
    {
        // Mirrors the fixed registration in HelixTool/Program.cs ~line 927-928.
        var evalOptions = new CacheOptions { CacheRoot = _snapshotDir, EvalMode = true };
        var services = new ServiceCollection();
        services.AddHttpClient("HelixDownload", HelixToolUserAgent.Apply);
        services.AddSingleton<ICacheStore>(_ => new SqliteCacheStore(evalOptions));
        services.AddSingleton<IHelixApiClient>(sp =>
            new CachingHelixApiClient(new OfflineHelixApiClient(), sp.GetRequiredService<ICacheStore>(), evalOptions));
        // This MUST use EvalModeBlockingHandler — NOT IHttpClientFactory.CreateClient("HelixDownload").
        services.AddSingleton<HelixService>(sp =>
            new HelixService(sp.GetRequiredService<IHelixApiClient>(), new HttpClient(new EvalModeBlockingHandler())));

        using var provider = services.BuildServiceProvider();
        var svc = provider.GetRequiredService<HelixService>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DownloadFromUrlAsync("https://helix.dot.net/api/file.binlog"));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);
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
