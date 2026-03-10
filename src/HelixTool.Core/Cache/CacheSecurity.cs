namespace HelixTool.Core.Cache;

/// <summary>
/// Security helpers to prevent path traversal attacks in cache operations.
/// </summary>
internal static class CacheSecurity
{
    /// <summary>
    /// Validates that <paramref name="path"/> resolves to a location within <paramref name="root"/>.
    /// Throws <see cref="ArgumentException"/> if the resolved path escapes the root directory.
    /// </summary>
    public static void ValidatePathWithinRoot(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        var rootWithoutSeparator = Path.TrimEndingDirectorySeparator(fullRoot);

        // Ensure root ends with separator for exact child-boundary comparison.
        var rootedPrefix = rootWithoutSeparator + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootedPrefix, StringComparison.Ordinal) &&
            !string.Equals(fullPath, rootWithoutSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Path traversal detected: resolved path '{fullPath}' is outside root '{rootedPrefix}'.");
        }
    }

    /// <summary>
    /// Sanitizes a single path segment (file name, work item name, etc.) by removing
    /// directory traversal sequences and replacing path separators with underscores.
    /// </summary>
    public static string SanitizePathSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment))
            return segment;

        // Replace path separators with underscore
        var sanitized = segment
            .Replace('/', '_')
            .Replace('\\', '_');

        // Remove any remaining ".." sequences
        sanitized = sanitized.Replace("..", "_");

        return sanitized;
    }

    /// <summary>
    /// Sanitizes a value used in cache key construction by stripping path separators
    /// and traversal sequences. Cache keys use ':' as delimiter — embedded separators
    /// could corrupt key structure or cause path traversal when keys map to file paths.
    /// </summary>
    public static string SanitizeCacheKeySegment(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return value
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace("..", "_");
    }
}
