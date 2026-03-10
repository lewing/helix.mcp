using System.ComponentModel;
using ModelContextProtocol.Server;

using HelixTool.Core;

namespace HelixTool.Mcp.Tools;

[McpServerToolType]
public sealed class CiKnowledgeTool
{
    [McpServerTool(Name = "helix_ci_guide", Title = "CI Investigation Guide", ReadOnly = true),
     Description("Get CI investigation guidance for a .NET repository. Returns repo-specific profiles including failure patterns, recommended tool sequences, exit code meanings, known gotchas, and pipeline details. Covers 9 repos: runtime, aspnetcore, sdk, roslyn, efcore, vmr, maui, macios, android. Call with no arguments for an overview of all repos. ⚠️ macios and android are on devdiv (not dnceng) — standard tools won't work for those. Use this BEFORE investigating CI failures to avoid wasted tool calls.")]
    public string GetGuide(
        [Description("Repository name (e.g., 'runtime', 'aspnetcore', 'sdk', 'roslyn', 'efcore', 'vmr', 'maui', 'macios', 'android'). Also accepts 'dotnet/runtime', 'xamarin/macios', etc. Omit for an overview of all repos.")] string? repo = null)
    {
        if (string.IsNullOrWhiteSpace(repo))
            return CiKnowledgeService.GetOverview();

        return CiKnowledgeService.GetGuide(repo);
    }
}
