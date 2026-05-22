using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace HelixTool.Mcp.Tools;

internal static class McpToolResultFactory
{
    public static CallToolResult CreateStructuredJson<T>(T value)
    {
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions) }],
            StructuredContent = JsonSerializer.SerializeToElement(value, McpJsonUtilities.DefaultOptions)
        };
    }
}
