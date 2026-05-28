using System.ComponentModel;
using System.Reflection;
using HelixTool.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace HelixTool.Tests;

public class McpToolDescriptionTests
{
    [Fact]
    public void McpServerToolParameters_HaveDiscoverableDescriptions()
    {
        var failures = GetMcpToolMethods()
            .SelectMany(method => GetUserVisibleParameters(method)
                .Where(parameter => string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
                .Select(parameter => $"{GetToolName(method)}.{parameter.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(failures);
    }

    private static IEnumerable<MethodInfo> GetMcpToolMethods()
    {
        var toolTypes = new[]
        {
            typeof(AzdoMcpTools),
            typeof(HelixMcpTools),
            typeof(CiKnowledgeTool)
        };

        return toolTypes.SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null);
    }

    private static IEnumerable<ParameterInfo> GetUserVisibleParameters(MethodInfo method)
    {
        return method.GetParameters().Where(parameter => !IsProgressParameter(parameter));
    }

    private static bool IsProgressParameter(ParameterInfo parameter)
    {
        var parameterType = parameter.ParameterType;
        return parameterType.IsGenericType && parameterType.GetGenericTypeDefinition() == typeof(IProgress<>);
    }

    private static string GetToolName(MethodInfo method)
    {
        return method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;
    }
}
