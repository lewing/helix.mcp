using System.ComponentModel;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class CiKnowledgeTool
{
    [McpServerTool(Name = "helix_ci_guide", Title = "CI Investigation Guide", ReadOnly = true),
     Description("Get CI investigation guidance for a .NET repository. Returns repo-specific patterns for finding test failures, recommended tool sequences, and common failure categories. Call with no arguments for an overview of all repos, or pass a repo name (e.g., 'runtime', 'aspnetcore', 'sdk') for detailed guidance. Use this BEFORE investigating CI failures to avoid wasted tool calls.")]
    public string GetGuide(
        [Description("Repository name (e.g., 'runtime', 'aspnetcore', 'sdk', 'roslyn', 'efcore', 'vmr'). Omit for an overview of all repos.")] string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return CiKnowledgeService.GetOverview();

        return CiKnowledgeService.GetGuide(repo);
    }
}
