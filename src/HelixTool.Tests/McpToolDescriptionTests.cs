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
    /// at the work-item level must also expose an optional <c>workItem</c> parameter with
    /// the exact declared shape: <c>string? workItem = null</c>.
    ///
    /// Checked contract (all three conditions must hold):
    /// <list type="number">
    ///   <item>A parameter named <c>workItem</c> exists.</item>
    ///   <item>Its type is <c>string</c> (i.e. <c>typeof(string)</c>).</item>
    ///   <item>It is optional with a <c>null</c> default (<c>HasDefaultValue &amp;&amp; DefaultValue is null</c>).</item>
    /// </list>
    ///
    /// Tools that intentionally operate at the job level (not per-work-item) are listed
    /// in <c>intentionallyJobScopedTools</c> — adding a new job-scoped tool requires an
    /// explicit entry there with a comment explaining why <c>workItem</c> is not applicable.
    /// </summary>
    [Fact]
    public void HelixJobIdTools_HaveWorkItemOrAreExplicitlyJobScoped()
    {
        // Tools that intentionally operate at the JOB level and do not accept workItem.
        // To opt out of the workItem requirement, add the MCP tool name here with a comment
        // explaining why per-work-item scoping is not applicable to this tool.
        var intentionallyJobScopedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "helix_status",       // summarises all work items across an entire job — workItem would narrow to a single item, defeating the purpose
            "helix_batch_status", // operates on multiple jobs simultaneously, not individual items
        };

        var failures = new List<string>();

        foreach (var method in GetMcpToolMethods()
            .Where(m => m.DeclaringType == typeof(HelixMcpTools))
            .Where(m => m.GetParameters().Any(p => p.Name == "jobId"))
            .Where(m => !intentionallyJobScopedTools.Contains(GetToolName(m))))
        {
            var toolName = GetToolName(method);
            var workItemParam = method.GetParameters().FirstOrDefault(p => p.Name == "workItem");

            if (workItemParam is null)
            {
                failures.Add($"  - {toolName}: missing 'workItem' parameter — declare 'string? workItem = null'");
            }
            else if (workItemParam.ParameterType != typeof(string))
            {
                failures.Add($"  - {toolName}: 'workItem' has type '{workItemParam.ParameterType.Name}' — must be 'string'");
            }
            else if (!workItemParam.HasDefaultValue || workItemParam.DefaultValue is not null)
            {
                failures.Add($"  - {toolName}: 'workItem' is not optional with a null default — declare as 'string? workItem = null'");
            }
        }

        Assert.True(failures.Count == 0,
            "The following Helix tools do not conform to the required workItem contract. " +
            "Every Helix tool that accepts 'jobId' must declare '[Description(\"...\")] string? workItem = null' " +
            "so callers can scope to a single work item. " +
            "To opt out, add the tool name to 'intentionallyJobScopedTools' with a justification:\n" +
            string.Join("\n", failures));
    }
}
