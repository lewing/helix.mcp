using System.Web;

namespace HelixTool.Core.AzDO;

/// <summary>
/// Parses Azure DevOps build URLs and plain integer build IDs into
/// (org, project, buildId) tuples.
/// SECURITY: Uses <see cref="Uri.TryCreate"/> for URL parsing — no regex (ReDoS-safe).
/// </summary>
public static class AzdoIdResolver
{
    public const string DefaultOrg = "dnceng-public";
    public const string DefaultProject = "public";

    /// <summary>
    /// Parse an AzDO build URL or plain integer into org, project, and build ID.
    /// </summary>
    /// <remarks>
    /// Supported formats:
    /// <list type="bullet">
    ///   <item>Plain integer: <c>"12345"</c> → uses default org/project</item>
    ///   <item>dev.azure.com: <c>https://dev.azure.com/{org}/{project}/_build/results?buildId={id}</c></item>
    ///   <item>dev.azure.com REST API: <c>https://dev.azure.com/{org}/{project}/_apis/build/builds/{id}</c></item>
    ///   <item>visualstudio.com: <c>https://{org}.visualstudio.com/{project}/_build/results?buildId={id}</c></item>
    ///   <item>visualstudio.com REST API: <c>https://{org}.visualstudio.com/{project}/_apis/build/builds/{id}</c></item>
    /// </list>
    /// </remarks>
    public static (string Org, string Project, int BuildId) Resolve(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        // Plain integer build ID
        if (int.TryParse(input, out var plainId))
            return (DefaultOrg, DefaultProject, plainId);

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && uri.Scheme != "http"))
        {
            throw new ArgumentException(
                $"Invalid AzDO build reference: expected an integer build ID or AzDO URL. Got: '{input}'",
                nameof(input));
        }

        var host = uri.Host;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Try REST API path first: .../_apis/build/builds/{id}
        // Then fall back to query string: ?buildId=NNN
        if (!TryExtractBuildIdFromApiPath(segments, out var buildId))
        {
            var query = HttpUtility.ParseQueryString(uri.Query);
            var buildIdStr = query["buildId"];

            if (string.IsNullOrEmpty(buildIdStr) || !int.TryParse(buildIdStr, out buildId))
            {
                throw new ArgumentException(
                    $"AzDO URL missing or invalid build ID (expected '?buildId=NNN' or '/_apis/build/builds/NNN'): '{input}'",
                    nameof(input));
            }
        }

        // https://dev.azure.com/{org}/{project}/...
        if (host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length < 2)
                throw new ArgumentException(
                    $"AzDO dev.azure.com URL must contain org and project in path: '{input}'",
                    nameof(input));

            return (
                Uri.UnescapeDataString(segments[0]),
                Uri.UnescapeDataString(segments[1]),
                buildId);
        }

        // https://{org}.visualstudio.com/{project}/...
        if (host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            var org = host[..host.IndexOf('.')];
            if (segments.Length < 1)
                throw new ArgumentException(
                    $"AzDO visualstudio.com URL must contain project in path: '{input}'",
                    nameof(input));

            return (
                org,
                Uri.UnescapeDataString(segments[0]),
                buildId);
        }

        throw new ArgumentException(
            $"Unrecognized AzDO host '{host}'. Expected dev.azure.com or *.visualstudio.com: '{input}'",
            nameof(input));
    }

    /// <summary>
    /// Extract build ID from REST API path: .../_apis/build/builds/{id}
    /// </summary>
    private static bool TryExtractBuildIdFromApiPath(string[] segments, out int buildId)
    {
        buildId = 0;
        for (int i = 0; i + 3 < segments.Length; i++)
        {
            if (segments[i].Equals("_apis", StringComparison.OrdinalIgnoreCase) &&
                segments[i + 1].Equals("build", StringComparison.OrdinalIgnoreCase) &&
                segments[i + 2].Equals("builds", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(segments[i + 3], out buildId);
            }
        }
        return false;
    }

    /// <summary>
    /// Try to parse an AzDO build reference. Returns false on invalid input.
    /// </summary>
    public static bool TryResolve(string input, out string org, out string project, out int buildId)
    {
        org = DefaultOrg;
        project = DefaultProject;
        buildId = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            (org, project, buildId) = Resolve(input);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
