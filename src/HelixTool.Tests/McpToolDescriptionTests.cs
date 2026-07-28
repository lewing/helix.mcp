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

    /// <summary>
    /// Schema consistency guard: any Helix tool that accepts a <c>jobId</c> and operates
    /// at the work-item level must also expose an optional <c>workItem</c> parameter.
    /// Tools that intentionally operate at the job level (not per-work-item) are listed
    /// in <c>intentionallyJobScopedTools</c> below — adding a new job-scoped tool requires
    /// an explicit entry there with a comment explaining why workItem is not needed.
    ///
    /// This test catches the whole class automatically: any new Helix tool that takes
    /// <c>jobId</c> but omits <c>workItem</c> will fail here unless it is declared
    /// job-scoped.
    /// </summary>
    [Fact]
    public void HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped()
    {
        // Tools that intentionally operate at the JOB level and do not accept workItem.
        // When adding a new job-scoped tool, add its MCP name here with a comment.
        var intentionallyJobScopedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "helix_status",       // summarises all work items across an entire job
            "helix_batch_status", // operates on multiple jobs simultaneously, not individual items
        };

        var failures = GetMcpToolMethods()
            .Where(m => m.DeclaringType == typeof(HelixMcpTools))
            .Where(m => m.GetParameters().Any(p => p.Name == "jobId"))
            .Where(m => !intentionallyJobScopedTools.Contains(GetToolName(m)))
            .Where(m => !m.GetParameters().Any(p => p.Name == "workItem"))
            .Select(m => GetToolName(m))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(failures.Count == 0,
            $"The following Helix tools accept 'jobId' but are missing the optional 'workItem' parameter. " +
            $"Either add 'workItem' to each tool so callers can scope to a single work item, " +
            $"or add the tool name to 'intentionallyJobScopedTools' with a justification:\n" +
            string.Join("\n", failures.Select(n => $"  - {n}")));
    }
}
