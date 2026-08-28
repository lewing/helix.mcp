// Deterministic construction of eval-mode snapshots that contain already-expired rows.
//
// A non-eval SqliteCacheStore schedules a fire-and-forget EvictExpiredAsync from its
// constructor. That work item can land at any later point — including after a test has
// written its rows, and after the writer store has been disposed — so seeding rows with
// TimeSpan.Zero through a live writer is a race that no fixed delay can close. It surfaced
// as a windows-latest CI failure (EvalMode_EvictExpired_IsNoOp_ExpiredEntriesRemain) once
// thread-pool contention pushed the eviction past the guard delay.
//
// The seeding here is race-free by construction: rows are written through the real writer
// with a live TTL (so eviction cannot match them whenever it runs), the database is backed
// up to the snapshot path, and expires_at is backdated only in that copy. No SqliteCacheStore
// instance ever targets the copy, so no eviction can reach it.

using System.Globalization;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;

namespace HelixTool.Tests;

internal static class ExpiredSnapshot
{
    private const string Iso8601Format = "O";

    /// <summary>TTL callers must use when seeding rows that <see cref="CreateAsync"/> will backdate.</summary>
    public static readonly TimeSpan SeedTtl = TimeSpan.FromHours(4);

    /// <summary>How far in the past the backdated rows are placed.</summary>
    public static readonly TimeSpan ExpiredBy = TimeSpan.FromHours(4);

    /// <summary>
    /// Seeds rows through a real writer store rooted at <paramref name="stagingCacheRoot"/>, copies the
    /// database into <paramref name="snapshotDir"/>, then backdates <c>expires_at</c> for the named rows.
    /// </summary>
    /// <param name="stagingCacheRoot">
    /// Writer cache root. Must not resolve to <paramref name="snapshotDir"/>, otherwise the writer's
    /// pending eviction would still be able to reach the snapshot database.
    /// </param>
    public static async Task CreateAsync(
        string stagingCacheRoot,
        string snapshotDir,
        Func<SqliteCacheStore, Task> seedAsync,
        string[]? metadataKeys = null,
        string[]? jobIds = null)
    {
        await SnapshotEvalTestHarness.CreateStableSnapshotAsync(stagingCacheRoot, snapshotDir, seedAsync);
        Backdate(Path.Combine(snapshotDir, "cache.db"), metadataKeys ?? [], jobIds ?? []);
    }

    private static void Backdate(string dbPath, string[] metadataKeys, string[] jobIds)
    {
        var expiredAt = (DateTimeOffset.UtcNow - ExpiredBy).ToString(Iso8601Format, CultureInfo.InvariantCulture);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();

        foreach (var cacheKey in metadataKeys)
            Update(connection, "UPDATE cache_metadata SET expires_at = @expires WHERE cache_key = @id;", cacheKey, expiredAt);

        foreach (var jobId in jobIds)
            Update(connection, "UPDATE cache_job_state SET expires_at = @expires WHERE job_id = @id;", jobId, expiredAt);
    }

    private static void Update(SqliteConnection connection, string sql, string id, string expiredAt)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@expires", expiredAt);
        command.Parameters.AddWithValue("@id", id);

        // Guard against a vacuous pass: the row must exist before the test asserts it survives.
        var rows = command.ExecuteNonQuery();
        if (rows != 1)
            throw new InvalidOperationException(
                $"Expected to backdate exactly one snapshot row for '{id}', but updated {rows}.");
    }
}
