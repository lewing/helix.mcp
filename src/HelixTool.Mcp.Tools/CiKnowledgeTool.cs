using System.ComponentModel;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class CiKnowledgeTool
{
    [McpServerTool(Name = "helix_ci_guide", Title = "CI Investigation Guide", ReadOnly = true),
     Description("Get repo-specific CI guidance that helps you choose between helix_test_results, azdo_test_runs + azdo_test_results, and helix_search_log before you start digging. Returns failure patterns, recommended tool order, exit code meanings, known gotchas, and pipeline details for 9 repos: runtime, aspnetcore, sdk, roslyn, efcore, vmr, maui, macios, android. Call with no arguments for an overview of all repos. ⚠️ macios and android are on devdiv (not dnceng) — standard tools won't work for those.")]
    public string GetGuide(
        [Description("Repository name (e.g., 'runtime', 'aspnetcore', 'sdk', 'roslyn', 'efcore', 'vmr', 'maui', 'macios', 'android'). Also accepts 'dotnet/runtime', 'xamarin/macios', etc. Omit for an overview of all repos.")] string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return CiKnowledgeService.GetOverview();

        return CiKnowledgeService.GetGuide(repo);
    }
}
