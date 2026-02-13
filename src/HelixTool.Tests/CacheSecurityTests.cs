// Security tests for CacheSecurity path traversal hardening.
// Covers ValidatePathWithinRoot, SanitizePathSegment, SanitizeCacheKeySegment,
// and integration with SqliteCacheStore and CachingHelixApiClient.

using HelixTool.Core;
using NSubstitute;
using Xunit;

namespace HelixTool.Tests;

public class CacheSecurityTests
{
    // =========================================================================
    // ValidatePathWithinRoot
    // =========================================================================

    [Fact]
    public void ValidatePathWithinRoot_PathInsideRoot_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root");
        var child = Path.Combine(root, "subdir", "file.db");

        var ex = Record.Exception(() => CacheSecurity.ValidatePathWithinRoot(child, root));

        Assert.Null(ex);
    }

    [Fact]
    public void ValidatePathWithinRoot_PathEscapingWithDotDot_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root");
        var escaped = Path.Combine(root, "..", "outside-file.txt");

        Assert.Throws<ArgumentException>(() =>
            CacheSecurity.ValidatePathWithinRoot(escaped, root));
    }

    [Fact]
    public void ValidatePathWithinRoot_DeepTraversal_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root", "deep", "nested");
        var escaped = Path.Combine(root, "..", "..", "..", "etc", "passwd");

        Assert.Throws<ArgumentException>(() =>
            CacheSecurity.ValidatePathWithinRoot(escaped, root));
    }

    [Fact]
    public void ValidatePathWithinRoot_DotDotInMiddleSegment_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root");
        var escaped = Path.Combine(root, "subdir", "..", "..", "outside.txt");

        Assert.Throws<ArgumentException>(() =>
            CacheSecurity.ValidatePathWithinRoot(escaped, root));
    }

    [Fact]
    public void ValidatePathWithinRoot_PathEqualToRoot_DoesNotThrow()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root");

        var ex = Record.Exception(() => CacheSecurity.ValidatePathWithinRoot(root, root));

        Assert.Null(ex);
    }

    [Fact]
    public void ValidatePathWithinRoot_ForwardSlashTraversal_Throws()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-root");
        // Use forward slashes — Path.GetFullPath normalizes these on Windows
        var escaped = root + "/../outside.txt";

        Assert.Throws<ArgumentException>(() =>
            CacheSecurity.ValidatePathWithinRoot(escaped, root));
    }

    [Fact]
    public void ValidatePathWithinRoot_UrlEncodedSequences_HandledAsLiteral()
    {
        // URL-encoded ".." (%2e%2e) is NOT decoded by Path.GetFullPath,
        // so it becomes a literal directory name "%2e%2e" inside root — should NOT throw.
        var root = Path.Combine(Path.GetTempPath(), "test-root");
        var path = Path.Combine(root, "%2e%2e", "file.txt");

        var ex = Record.Exception(() => CacheSecurity.ValidatePathWithinRoot(path, root));

        Assert.Null(ex);
    }

    // =========================================================================
    // SanitizePathSegment
    // =========================================================================

    [Fact]
    public void SanitizePathSegment_NormalFileName_Unchanged()
    {
        var result = CacheSecurity.SanitizePathSegment("console.log");

        Assert.Equal("console.log", result);
    }

    [Fact]
    public void SanitizePathSegment_DotDot_ReplacedWithUnderscore()
    {
        var result = CacheSecurity.SanitizePathSegment("..test");

        Assert.Equal("_test", result);
    }

    [Fact]
    public void SanitizePathSegment_ForwardSlash_ReplacedWithUnderscore()
    {
        var result = CacheSecurity.SanitizePathSegment("path/segment");

        Assert.Equal("path_segment", result);
    }

    [Fact]
    public void SanitizePathSegment_Backslash_ReplacedWithUnderscore()
    {
        var result = CacheSecurity.SanitizePathSegment(@"path\segment");

        Assert.Equal("path_segment", result);
    }

    [Fact]
    public void SanitizePathSegment_FullTraversalAttack_FullySanitized()
    {
        var result = CacheSecurity.SanitizePathSegment("../../etc/passwd");

        // ".." → "_", "/" → "_" — result should have no traversal characters
        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    [Fact]
    public void SanitizePathSegment_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", CacheSecurity.SanitizePathSegment(""));
    }

    [Fact]
    public void SanitizePathSegment_Null_ReturnsNull()
    {
        Assert.Null(CacheSecurity.SanitizePathSegment(null!));
    }

    // =========================================================================
    // SanitizeCacheKeySegment
    // =========================================================================

    [Fact]
    public void SanitizeCacheKeySegment_NormalGuid_Unchanged()
    {
        const string guid = "d1f9a7c3-2b4e-4f8a-9c0d-e5f6a7b8c9d0";

        var result = CacheSecurity.SanitizeCacheKeySegment(guid);

        Assert.Equal(guid, result);
    }

    [Fact]
    public void SanitizeCacheKeySegment_WithPathSeparators_Replaced()
    {
        var result = CacheSecurity.SanitizeCacheKeySegment("path/to\\value");

        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
        Assert.Equal("path_to_value", result);
    }

    [Fact]
    public void SanitizeCacheKeySegment_WithTraversalSequence_Replaced()
    {
        var result = CacheSecurity.SanitizeCacheKeySegment("../../../etc/passwd");

        Assert.DoesNotContain("..", result);
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void SanitizeCacheKeySegment_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", CacheSecurity.SanitizeCacheKeySegment(""));
    }

    [Fact]
    public void SanitizeCacheKeySegment_Null_ReturnsNull()
    {
        Assert.Null(CacheSecurity.SanitizeCacheKeySegment(null!));
    }
}

// =========================================================================
// Integration: SqliteCacheStore artifact path security
// =========================================================================

public class SqliteCacheStoreSecurityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteCacheStore _store;

    public SqliteCacheStoreSecurityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"hlx-sec-{Guid.NewGuid():N}");
        _store = new SqliteCacheStore(new CacheOptions { CacheRoot = _tempDir });
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task SetArtifactAsync_TraversalInJobId_ArtifactStaysWithinCacheDir()
    {
        // Job ID contains ".." — after sanitization, artifact file must stay inside cache dir
        const string maliciousKey = "job:../../escape:wi:test:console";
        var content = new byte[] { 1, 2, 3 };

        await _store.SetArtifactAsync(maliciousKey, new MemoryStream(content));

        // Verify artifact is retrievable (stored successfully within cache dir)
        var result = await _store.GetArtifactAsync(maliciousKey);
        Assert.NotNull(result);
        using var reader = new StreamReader(result!);
        var bytes = await reader.ReadToEndAsync();
        Assert.NotEmpty(bytes);
        result.Dispose();

        // Verify all artifact files are within the cache directory (not escaped)
        // Note: GetEffectiveCacheRoot() appends "public" subdir when no auth token is set
        var effectiveRoot = Path.GetFullPath(new CacheOptions { CacheRoot = _tempDir }.GetEffectiveCacheRoot());
        var artifactsDir = Path.GetFullPath(Path.Combine(effectiveRoot, "artifacts")) + Path.DirectorySeparatorChar;
        var allFiles = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories);
        foreach (var file in allFiles)
        {
            // Skip the SQLite database files
            if (file.EndsWith(".db") || file.EndsWith(".db-shm") || file.EndsWith(".db-wal"))
                continue;

            var fullPath = Path.GetFullPath(file);
            Assert.True(fullPath.StartsWith(artifactsDir, StringComparison.OrdinalIgnoreCase),
                $"Artifact file '{fullPath}' is outside artifacts dir '{artifactsDir}'");
        }
    }

    [Fact]
    public async Task GetArtifactAsync_ManipulatedDbRow_ThrowsOrReturnsNull()
    {
        // Use a dedicated temp dir for this test to avoid cross-test interference
        var tamperedDir = Path.Combine(Path.GetTempPath(), $"hlx-tamper-{Guid.NewGuid():N}");
        var opts = new CacheOptions { CacheRoot = tamperedDir };
        var effectiveRoot = opts.GetEffectiveCacheRoot();
        try
        {
            const string key = "job:legit123:wi:test:console";

            // Phase 1: Store a legitimate artifact
            using (var store1 = new SqliteCacheStore(opts))
            {
                await store1.SetArtifactAsync(key, new MemoryStream(new byte[] { 42 }));
            }

            // Phase 2: Tamper with the DB row to point outside artifacts dir
            var dbPath = Path.Combine(effectiveRoot, "cache.db");
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using (var pragma = conn.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    pragma.ExecuteNonQuery();
                }
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE cache_artifacts SET file_path = @path WHERE cache_key = @key;";
                cmd.Parameters.AddWithValue("@path", "../../outside-artifact.txt");
                cmd.Parameters.AddWithValue("@key", key);
                var rows = cmd.ExecuteNonQuery();
                Assert.Equal(1, rows);
            }

            // Phase 3: Re-open and verify the tampered entry is blocked
            using var store2 = new SqliteCacheStore(opts);
            var threw = false;
            Stream? result = null;
            try
            {
                result = await store2.GetArtifactAsync(key);
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            Assert.True(threw || result == null,
                "GetArtifactAsync with tampered DB row must throw ArgumentException or return null");
            result?.Dispose();
        }
        finally
        {
            try { Directory.Delete(tamperedDir, recursive: true); } catch { }
        }
    }
}

// =========================================================================
// Integration: CachingHelixApiClient cache key sanitization
// =========================================================================

public class CachingHelixApiClientSecurityTests
{
    private readonly IHelixApiClient _inner;
    private readonly ICacheStore _cache;
    private readonly CachingHelixApiClient _sut;

    public CachingHelixApiClientSecurityTests()
    {
        _inner = Substitute.For<IHelixApiClient>();
        _cache = Substitute.For<ICacheStore>();
        _sut = new CachingHelixApiClient(_inner, _cache, new CacheOptions());
    }

    [Fact]
    public async Task GetJobDetailsAsync_JobIdWithPathSeparators_CacheKeyIsSanitized()
    {
        // Arrange: cache miss (null) so inner client is called
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var mockDetails = Substitute.For<IJobDetails>();
        mockDetails.Name.Returns("test-job");
        mockDetails.Finished.Returns("2025-01-01");
        _inner.GetJobDetailsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockDetails);

        // Act with a malicious job ID containing path separators
        await _sut.GetJobDetailsAsync("path/to\\evil");

        // Assert: cache key used for storage must NOT contain raw path separators
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => !k.Contains('/') && !k.Contains('\\')),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetWorkItemDetailsAsync_WorkItemWithDotDot_CacheKeyIsSanitized()
    {
        // Arrange: cache miss
        _cache.GetMetadataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var mockDetails = Substitute.For<IWorkItemDetails>();
        _inner.GetWorkItemDetailsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(mockDetails);
        // Need job state for TTL lookup
        _cache.IsJobCompletedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<bool?>(true));

        // Act with traversal in work item name
        await _sut.GetWorkItemDetailsAsync("../../../etc/passwd", "valid-job-id");

        // Assert: cache key must not contain ".."
        await _cache.Received().SetMetadataAsync(
            Arg.Is<string>(k => !k.Contains("..")),
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFileAsync_FileNameWithTraversal_CacheKeyIsSanitized()
    {
        // Arrange
        _inner.GetFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1 }));
        // Return null from artifact cache to force inner call
        _cache.GetArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null), Task.FromResult<Stream?>(new MemoryStream(new byte[] { 1 })));

        // Act with traversal in file name
        await _sut.GetFileAsync("/../../secret.txt", "workitem", "valid-job-id");

        // Assert: cache key stored via SetArtifactAsync must be sanitized
        await _cache.Received().SetArtifactAsync(
            Arg.Is<string>(k => !k.Contains("..") && !k.Contains('/') && !k.Contains('\\')),
            Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }
}
