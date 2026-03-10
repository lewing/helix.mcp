using System.ComponentModel;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerResourceType]
public sealed class CiKnowledgeResource
{
    [McpServerResource(UriTemplate = "ci://profiles",
        Name = "CI Profiles Overview",
        Title = "CI Investigation Profiles for .NET Repositories",
        MimeType = "text/markdown"),
     Description("Overview of CI investigation patterns for all .NET repositories")]
    public static string GetOverview() => CiKnowledgeService.GetOverview();

    [McpServerResource(UriTemplate = "ci://profiles/{repo}",
        Name = "CI Repo Profile",
        Title = "CI Investigation Guide for a .NET Repository",
        MimeType = "text/markdown"),
     Description("CI investigation guide for a specific .NET repository")]
    public static string GetProfile(string repo) => CiKnowledgeService.GetGuide(repo);
}
