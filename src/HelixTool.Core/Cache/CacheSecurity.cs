namespace HelixTool.Core;

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

        // Ensure root ends with separator for prefix comparison
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fullPath, fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Path traversal detected: resolved path '{fullPath}' is outside root '{fullRoot}'.");
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
