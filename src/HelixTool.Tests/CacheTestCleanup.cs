using Microsoft.Data.Sqlite;

namespace HelixTool.Tests;

internal static class CacheTestCleanup
{
    internal const int DeleteAttempts = 7;

    // Call only after fixture hosts, stores, and leased connections have been disposed.
    public static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var database in Directory.EnumerateFiles(root, "cache.db", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        }))
        {
            // Match SqliteCacheStore's exact live and eval connection strings.
            foreach (var suffix in new[] { "Cache=Shared", "Mode=ReadOnly" })
            {
                using var poolHandle = new SqliteConnection($"Data Source={database};{suffix}");
                SqliteConnection.ClearPool(poolHandle);
            }
        }

        DeleteWithRetry(() => Directory.Delete(root, recursive: true), Thread.Sleep);
    }

    internal static void DeleteWithRetry(Action delete, Action<TimeSpan> delay)
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
                delay(TimeSpan.FromMilliseconds(25 * (1 << attempt)));
            }
        }
    }
}
