using Microsoft.Data.Sqlite;
using Xunit;

namespace HelixTool.Tests;

public sealed class CacheTestCleanupTests
{
    [Fact]
    public void DeleteRoot_ClearsLiveAndEvalPoolsInNestedPartitions_LeavesSiblingPoolWarm()
    {
        var root = Path.GetFullPath($"hlx-cleanup-{Guid.NewGuid():N}");
        var sibling = root + "-sibling";
        var siblingDatabase = Path.Combine(sibling, "cache.db");
        var siblingConnectionString = $"Data Source={siblingDatabase};Cache=Shared;Pooling=True;Default Timeout=7";
        Directory.CreateDirectory(sibling);
        try
        {
            using (var connection = CacheTestCleanup.CreatePooledConnection(siblingConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TEMP TABLE pool_marker (value INTEGER); INSERT INTO pool_marker VALUES (42);";
                command.ExecuteNonQuery();
            }

            foreach (var partition in new[] { "public", Path.Combine("auth", "abcdef12"), "snapshot" })
            {
                var directory = Path.Combine(root, partition);
                Directory.CreateDirectory(directory);
                var database = Path.Combine(directory, "cache.db");
                using (var live = new SqliteConnection($"Data Source={database};Cache=Shared"))
                {
                    live.Open();
                    using var command = live.CreateCommand();
                    command.CommandText = "CREATE TABLE fixture (value INTEGER);";
                    command.ExecuteNonQuery();
                }
                using (var eval = new SqliteConnection($"Data Source={database};Mode=ReadOnly"))
                    eval.Open();
            }

            CacheTestCleanup.ClearPools(root);
            AssertNoDatabaseHandles(root);
            CacheTestCleanup.DeleteRoot(root);
            Assert.False(Directory.Exists(root));
            CacheTestCleanup.DeleteRoot(root);

            using (var siblingConnection = CacheTestCleanup.CreatePooledConnection(siblingConnectionString))
            {
                siblingConnection.Open();
                using var siblingCommand = siblingConnection.CreateCommand();
                siblingCommand.CommandText = "SELECT value FROM pool_marker;";
                Assert.Equal(42L, siblingCommand.ExecuteScalar());
            }
        }
        finally
        {
            CacheTestCleanup.DeleteRoot(root);
            CacheTestCleanup.DeleteRoot(sibling);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Cache=Shared;Pooling=True;Default Timeout=7;Foreign Keys=True")]
    [InlineData(";Mode=ReadOnly;Pooling=True;Default Timeout=7")]
    public void DeleteRoot_ClearsExactRegisteredPool_ForEquivalentPathAndExtraKeywords(string suffix)
    {
        var root = Path.GetFullPath($"hlx-cleanup-variant-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "auth", "abcdef12");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "cache.db");
        try
        {
            using (var seed = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                seed.Open();
                using var command = seed.CreateCommand();
                command.CommandText = "CREATE TABLE fixture (value INTEGER);";
                command.ExecuteNonQuery();
            }

            // Both mixed separators on Windows and ".." path segments create distinct pools.
            var equivalentPath = Path.Combine(root, "auth/abcdef12", "..", "abcdef12", "cache.db");
            using (var connection = CacheTestCleanup.CreatePooledConnection($"Data Source={equivalentPath}{suffix}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM fixture;";
                Assert.Equal(0L, command.ExecuteScalar());
            }

            CacheTestCleanup.ClearPools(root);
            AssertNoDatabaseHandles(root);
            CacheTestCleanup.DeleteRoot(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            CacheTestCleanup.DeleteRoot(root);
        }
    }

    [Fact]
    public void DeleteRoot_ClearsUnregisteredDefaultProbePool()
    {
        var root = Path.GetFullPath($"hlx-cleanup-default-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = Path.Combine(root, "cache.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version=99;";
                command.ExecuteNonQuery();
            }

            CacheTestCleanup.ClearPools(root);
            AssertNoDatabaseHandles(root);
            CacheTestCleanup.DeleteRoot(root);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            CacheTestCleanup.DeleteRoot(root);
        }
    }

    private static void AssertNoDatabaseHandles(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var database in Directory.EnumerateFiles(root, "cache.db", SearchOption.AllDirectories))
            {
                using var exclusive = new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // Unix unlink succeeds with native SQLite descriptors still open; deletion alone
            // is not a regression test for Windows sharing violations.
            var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            var handles = Directory.EnumerateFiles("/proc/self/fd")
                .Select(path => new FileInfo(path).LinkTarget)
                .Where(target => target?.StartsWith(prefix, StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Empty(handles);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void DeleteWithRetry_SharingAndLockViolations_BackOffThenSucceed(int hresult)
    {
        var attempts = 0;
        var retryCallbacks = 0;
        var delays = new List<TimeSpan>();
        CacheTestCleanup.DeleteWithRetry(() =>
        {
            if (++attempts < 4)
                throw new IOException("Locked fixture", hresult);
        }, delays.Add, () => retryCallbacks++);

        Assert.Equal(4, attempts);
        Assert.Equal(3, retryCallbacks);
        Assert.Equal(new[] { 25d, 50d, 100d }, delays.Select(d => d.TotalMilliseconds));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void DeleteWithRetry_ExhaustedBudget_PropagatesOriginalFailure(int hresult)
    {
        var failure = new IOException("Still locked", hresult);
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var thrown = Assert.Throws<IOException>(() => CacheTestCleanup.DeleteWithRetry(() =>
        {
            attempts++;
            throw failure;
        }, delays.Add));

        Assert.Same(failure, thrown);
        Assert.Equal(CacheTestCleanup.DeleteAttempts, attempts);
        Assert.Equal(new[] { 25d, 50d, 100d, 200d, 400d, 800d, 1600d, 2000d, 2000d, 2000d },
            delays.Select(d => d.TotalMilliseconds));
        Assert.Equal(9175, delays.Sum(d => d.TotalMilliseconds));
    }

    [Fact]
    public void DeleteWithRetry_OtherFailures_PropagateWithoutRetry()
    {
        foreach (var failure in new Exception[]
        {
            new IOException("Other IO failure", unchecked((int)0x80070070)),
            new UnauthorizedAccessException("Access denied"),
            new InvalidOperationException("Unexpected failure")
        })
        {
            var attempts = 0;
            var delays = new List<TimeSpan>();
            var thrown = Record.Exception(() => CacheTestCleanup.DeleteWithRetry(() =>
            {
                attempts++;
                throw failure;
            }, delays.Add));

            Assert.Same(failure, thrown);
            Assert.Equal(1, attempts);
            Assert.Empty(delays);
        }
    }
}
