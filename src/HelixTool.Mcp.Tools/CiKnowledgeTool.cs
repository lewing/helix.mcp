using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class CiKnowledgeTool
{
    [McpServerTool(Name = "helix_ci_guide", Title = "CI Investigation Guide", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Repo-specific CI guidance: tool selection, failure patterns, exit codes, pipeline details, gotchas. Covers 9 repos. Omit repo for overview. ⚠️ macios/android use devdiv — auth required.")]
    public string GetGuide(
        [Description("Repository name; omit for overview")] string? repo = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repo))
                return CiKnowledgeService.GetOverview();

            return CiKnowledgeService.GetGuide(repo);
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            throw new McpException($"Failed to get CI guidance: {ex.Message}", ex);
        }
    }
}
