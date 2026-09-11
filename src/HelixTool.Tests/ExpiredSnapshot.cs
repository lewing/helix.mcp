// Deterministic construction of eval-mode snapshots that contain already-expired rows.
//
// The eval-mode no-op tests need rows whose expires_at is already in the past by the time
// the eval store validates the snapshot. Seeding such a row directly through a live writer
// is not safe even now that startup maintenance is tracked and joined (lewing/helix.mcp#129):
// the startup pass pins its eviction cutoff at construction time, so a TimeSpan.Zero row
// written *before* construction is a legitimate target for that writer's own startup pass,
// and an explicit EvictExpiredAsync() call on that writer would remove it too. Backdating
// only after the writer is closed, in a private copy no SqliteCacheStore ever opens,
// sidesteps both.
//
// The seeding here is race-free by construction: rows are written through the real writer
// with a live TTL (so no eviction pass — startup or explicit — can match them while the
// writer is open), the database is backed up to the snapshot path, and expires_at is
// backdated only in that copy. No SqliteCacheStore instance ever targets the copy, so no
// eviction can reach it.

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
