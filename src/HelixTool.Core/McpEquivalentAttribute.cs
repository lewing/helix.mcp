namespace HelixTool.Core;

[AttributeUsage(AttributeTargets.Method)]
public sealed class McpEquivalentAttribute : Attribute
{
    public string McpToolName { get; }

    public McpEquivalentAttribute(string mcpToolName)
    {
        McpToolName = mcpToolName;
    }
}
