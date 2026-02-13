namespace HelixTool.Core;

/// <summary>Configuration for the SQLite-backed cache layer.</summary>
public record CacheOptions
{
    /// <summary>Maximum cache size in bytes. Default: 1 GB. Set to 0 to disable caching.</summary>
    public long MaxSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>Cache root directory. Default: platform-appropriate XDG path.</summary>
    public string? CacheRoot { get; init; }

    /// <summary>Artifact expiry (last access). Default: 7 days.</summary>
    public TimeSpan ArtifactMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Resolve the actual cache root, respecting XDG conventions.</summary>
    public string GetEffectiveCacheRoot()
    {
        if (!string.IsNullOrEmpty(CacheRoot)) return CacheRoot;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hlx");
        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return Path.Combine(
            !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
            "hlx");
    }
}
