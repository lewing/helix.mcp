using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

namespace HelixTool.Tests;

internal static class CacheTestCleanup
{
    internal const int DeleteAttempts = 11;
    private static readonly ConcurrentDictionary<string, string> RegisteredPools = new();

    // Pool groups are keyed by the literal connection string, not the resolved database path.
    internal static SqliteConnection CreatePooledConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        var options = new SqliteConnectionStringBuilder(connectionString);
        if (options.Pooling && options.Mode != SqliteOpenMode.Memory && options.DataSource != ":memory:")
            RegisteredPools[connectionString] = Path.GetFullPath(options.DataSource);
        return connection;
    }

    // Call only after fixture hosts, stores, and leased connections have been disposed.
    public static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;

        ClearPools(root);
        DeleteWithRetry(() =>
        {
            RemoveReparsePoints(new DirectoryInfo(root));
            Directory.Delete(root, recursive: true);
        }, Thread.Sleep, () =>
        {
            if (OperatingSystem.IsWindows())
            {
                // A finalized statement can be the last owner of a sqlite3_close_v2 zombie handle.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                ClearPools(root);
            }
        });

        foreach (var pool in RegisteredPools.Where(pool => IsUnderRoot(root, pool.Value)))
            RegisteredPools.TryRemove(pool.Key, out _);
    }

    private static void RemoveReparsePoints(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Delete the link itself, never traverse a directory symlink or junction.
                entry.Delete();
            }
            else if (entry is DirectoryInfo child)
            {
                RemoveReparsePoints(child);
            }
        }
    }

    internal static void ClearPools(string root)
    {
        var connectionStrings = new HashSet<string>(
            RegisteredPools.Where(pool => IsUnderRoot(root, pool.Value)).Select(pool => pool.Key));
        foreach (var database in Directory.EnumerateFiles(root, "cache.db", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }))
        {
            // Include the default pool used by raw SQL fixture probes as well as store pools.
            connectionStrings.Add($"Data Source={database}");
            connectionStrings.Add($"Data Source={database};Cache=Shared");
            connectionStrings.Add($"Data Source={database};Cache=Private");
            connectionStrings.Add($"Data Source={database};Mode=ReadOnly");
        }

        foreach (var connectionString in connectionStrings)
        {
            using var poolHandle = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(poolHandle);
        }
    }

    private static bool IsUnderRoot(string root, string database)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), database);
        return !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static void DeleteWithRetry(Action delete, Action<TimeSpan> delay, Action? beforeRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                delete();
                return;
            }
            catch (IOException ex) when (
                ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
                && attempt < DeleteAttempts - 1)
            {
                beforeRetry?.Invoke();
                delay(TimeSpan.FromMilliseconds(Math.Min(2000, 25 * (1 << attempt))));
            }
        }
    }
}
