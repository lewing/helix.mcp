using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class CiKnowledgeTool
{
    [McpServerTool(Name = "helix_ci_guide", Title = "CI Investigation Guide", ReadOnly = true, Idempotent = true, OpenWorld = false),
     Description("Repo-specific CI guidance: tool selection, failure patterns, and exit codes.")]
    public string GetGuide(
        [Description("Repository name; omit for overview")] string? repo = null)
    {
        return McpExceptionHandler.RunServiceCall(() =>
        {
            if (string.IsNullOrWhiteSpace(repo))
                return CiKnowledgeService.GetOverview();

            return CiKnowledgeService.GetGuide(repo);
        }, "get CI guidance");
    }
}
