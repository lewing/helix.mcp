using Microsoft.Data.Sqlite;
using Xunit;

namespace HelixTool.Tests;

public sealed class CacheTestCleanupTests
{
    [Fact]
    public void DeleteRoot_ClearsLiveAndEvalPoolsInNestedPartitions_LeavesSiblingPoolWarm()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hlx-cleanup-{Guid.NewGuid():N}");
        var sibling = root + "-sibling";
        var siblingDatabase = Path.Combine(sibling, "cache.db");
        Directory.CreateDirectory(sibling);
        try
        {
            using (var connection = new SqliteConnection($"Data Source={siblingDatabase};Cache=Shared"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TEMP TABLE pool_marker (value INTEGER); INSERT INTO pool_marker VALUES (42);";
                command.ExecuteNonQuery();
            }

            foreach (var partition in new[] { "public", "auth/abcdef12", "snapshot" })
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
                using var eval = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
                eval.Open();
            }

            CacheTestCleanup.DeleteRoot(root);
            Assert.False(Directory.Exists(root));
            CacheTestCleanup.DeleteRoot(root);

            using var siblingConnection = new SqliteConnection($"Data Source={siblingDatabase};Cache=Shared");
            siblingConnection.Open();
            using var siblingCommand = siblingConnection.CreateCommand();
            siblingCommand.CommandText = "SELECT value FROM pool_marker;";
            Assert.Equal(42L, siblingCommand.ExecuteScalar());
        }
        finally
        {
            CacheTestCleanup.DeleteRoot(root);
            CacheTestCleanup.DeleteRoot(sibling);
        }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void DeleteWithRetry_SharingAndLockViolations_BackOffThenSucceed(int hresult)
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        CacheTestCleanup.DeleteWithRetry(() =>
        {
            if (++attempts < 4)
                throw new IOException("Locked fixture", hresult);
        }, delays.Add);

        Assert.Equal(4, attempts);
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
        Assert.Equal(new[] { 25d, 50d, 100d, 200d, 400d, 800d }, delays.Select(d => d.TotalMilliseconds));
        Assert.Equal(1575, delays.Sum(d => d.TotalMilliseconds));
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
