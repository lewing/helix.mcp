using System.Security.Cryptography;
using System.Text;

namespace HelixTool.Core.AzDO;

public static class AzdoDownloadPaths
{
    public static void ValidateOutputOutsideSnapshot(string path, Cache.CacheOptions options)
    {
        if (!options.EvalMode) return;
        var output = ResolvePhysicalPath(Path.GetFullPath(path));
        var snapshot = Path.TrimEndingDirectorySeparator(ResolvePhysicalPath(Path.GetFullPath(options.GetEffectiveCacheRoot())));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (output.Equals(snapshot, comparison) || output.StartsWith(snapshot + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Output cannot modify an active read-only snapshot. Re-run with an output directory outside HLX_EVAL_SNAPSHOT.");
    }

    public static string ResolvePhysicalPath(string fullPath) => ResolvePhysicalPath(fullPath, 0);

    private static string ResolvePhysicalPath(string fullPath, int depth)
    {
        if (depth > 64)
            throw new IOException("Cannot safely resolve a cyclic or excessively nested symlink path.");
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var components = fullPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var component in components)
        {
            current = Path.Combine(current, component);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists && info.LinkTarget is null) continue;
            var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is not null)
                current = ResolvePhysicalPath(target, depth + 1);
        }
        return current;
    }
    public static string NormalizeMember(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path[0] is '/' or '\\' || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.Any(char.IsControl) || HasEncodedTraversal(path))
            throw new ArgumentException("Member paths must be unencoded relative slash-separated paths.");
        var parts = path.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new ArgumentException("Member paths cannot contain empty, traversal, or ambiguous segments.");
        return string.Join('/', parts);
    }

    public static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();

    public static bool MatchesMember(string path, string pattern)
    {
        var pieces = path.Split('/');
        var selectors = pattern.Split('/');
        var previous = new bool[pieces.Length + 1];
        previous[0] = true;
        foreach (var selector in selectors)
        {
            var next = new bool[pieces.Length + 1];
            var globstar = selector.Length >= 2 && selector.All(c => c == '*');
            if (globstar) next[0] = previous[0];
            for (var i = 1; i <= pieces.Length; i++)
                next[i] = globstar ? previous[i] || next[i - 1] : previous[i - 1] && MatchSegment(pieces[i - 1], selector);
            previous = next;
        }
        return previous[pieces.Length];
    }

    private static bool HasEncodedTraversal(string path)
    {
        for (var i = 0; i + 2 < path.Length; i++)
            if (path[i] == '%' && byte.TryParse(path.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value is 0 or 0x2e or 0x2f or 0x5c or 0x25)
                return true;
        return false;
    }

    private static bool MatchSegment(string value, string pattern)
    {
        var v = 0;
        var p = 0;
        var star = -1;
        var restart = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; restart = v; }
            else if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == value[v])) { p++; v++; }
            else if (star >= 0) { p = star + 1; v = ++restart; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static string OutputPath(string root, string resource, string name, string hash)
    {
        root = Path.GetFullPath(root);
        var current = new DirectoryInfo(root);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Output directories cannot traverse symlinks or reparse points.");
            current = current.Parent;
        }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var leaf = name.Split('/').Last();
        leaf = new string(leaf.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        if (leaf.Length > 90) leaf = leaf[^90..];
        var path = Path.Combine(root, $"azdo-{resource}-{hash[..12]}-{leaf}");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Output files cannot be symlinks or reparse points.");
        return path;
    }

    public static FileStream CreatePrivateOutput(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 65536,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
