using System.Text.Json;
using HelixTool.Core.Cache;
using Microsoft.Data.Sqlite;

namespace HelixTool.Core.AzDO;

public sealed record EvalSnapshotAzdoPartition(
    string Partition,
    string? AuthTokenHash,
    string Source);

public static class EvalSnapshotAzdoPartitionSelector
{
    public const string EnvironmentVariable = "HLX_EVAL_AZDO_PARTITION";

    public static EvalSnapshotAzdoPartition Select(string snapshotPath)
    {
        snapshotPath = Path.GetFullPath(snapshotPath);
        var explicitPartition = NormalizePartition(Environment.GetEnvironmentVariable(EnvironmentVariable));
        var discovered = DiscoverPartitions(snapshotPath);

        if (explicitPartition is not null)
        {
            if (discovered.Count > 0 && !discovered.Contains(explicitPartition))
            {
                throw new InvalidOperationException(
                    $"{EnvironmentVariable}='{explicitPartition}' was requested, but that AzDO cache partition is not present in snapshot '{snapshotPath}'.");
            }

            return FromPartition(explicitPartition, EnvironmentVariable);
        }

        if (discovered.Count == 1)
            return FromPartition(discovered.Single(), "snapshot");

        if (discovered.Count > 1)
        {
            throw new InvalidOperationException(
                $"Snapshot '{snapshotPath}' contains multiple AzDO cache partitions ({string.Join(", ", discovered.Order(StringComparer.Ordinal))}). " +
                $"Set {EnvironmentVariable}=public or {EnvironmentVariable}=cache-xxxxxxxx to choose one explicitly.");
        }

        var manifestPartition = ReadManifestPartition(snapshotPath);
        if (manifestPartition is not null)
            return FromPartition(manifestPartition, "manifest");

        return FromPartition("public", "default");
    }

    private static EvalSnapshotAzdoPartition FromPartition(string partition, string source)
    {
        partition = NormalizePartition(partition)
            ?? throw new ArgumentException("Partition must not be empty.", nameof(partition));
        return partition == "public"
            ? new EvalSnapshotAzdoPartition("public", null, source)
            : new EvalSnapshotAzdoPartition(partition, partition["cache-".Length..], source);
    }

    private static string? NormalizePartition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Equals("public", StringComparison.OrdinalIgnoreCase))
            return "public";

        if (trimmed.StartsWith("cache-", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["cache-".Length..];

        if (trimmed.Length == 8 && trimmed.All(Uri.IsHexDigit))
            return $"cache-{trimmed.ToLowerInvariant()}";

        throw new InvalidOperationException(
            $"Invalid AzDO eval cache partition '{value}'. Use 'public' or 'cache-xxxxxxxx'.");
    }

    private static SortedSet<string> DiscoverPartitions(string snapshotPath)
    {
        var partitions = new SortedSet<string>(StringComparer.Ordinal);
        var dbPath = Path.Combine(snapshotPath, "cache.db");
        if (!File.Exists(dbPath))
            return partitions;

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();

        ReadKeyPartitions(connection, "cache_metadata", partitions);
        ReadKeyPartitions(connection, "cache_acquisition_errors", partitions);
        return partitions;
    }

    private static void ReadKeyPartitions(SqliteConnection connection, string table, ISet<string> partitions)
    {
        if (!TableExists(connection, table))
            return;

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT cache_key FROM {table} WHERE cache_key LIKE 'azdo:%' OR cache_key LIKE 'azdo-build:%';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var partition = TryParsePartition(reader.GetString(0));
            if (partition is not null)
                partitions.Add(partition);
        }
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
    }

    private static string? TryParsePartition(string key)
    {
        var parts = key.Split(':');
        if (parts.Length < 2)
            return null;

        if (parts[0] is "azdo" or "azdo-build")
        {
            var candidate = parts[1];
            return candidate.Length == 8 && candidate.All(Uri.IsHexDigit)
                ? $"cache-{candidate.ToLowerInvariant()}"
                : "public";
        }

        return null;
    }

    private static string? ReadManifestPartition(string snapshotPath)
    {
        var manifestPath = Path.Combine(snapshotPath, "manifest", "hlx-collect-manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (document.RootElement.TryGetProperty("auth", out var auth)
            && auth.TryGetProperty("azdo", out var azdo)
            && azdo.TryGetProperty("cachePartition", out var cachePartition))
        {
            return NormalizePartition(cachePartition.GetString());
        }

        return null;
    }
}
