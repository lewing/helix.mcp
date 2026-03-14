using System.ComponentModel;
using System.Reflection;
using HelixTool.Generated;
using HelixTool.Mcp.Tools;
using ModelContextProtocol.Server;
using Xunit;

namespace HelixTool.Tests;

public class DescribeTests
{
    private static readonly string[] ExpectedHelixRoutes =
    [
        "status",
        "logs",
        "files",
        "download",
        "find-files",
        "work-item",
        "batch-status",
        "search-log",
        "parse-uploaded-trx"
    ];

    private static readonly string[] ExpectedAzdoRoutes =
    [
        "azdo build",
        "azdo builds",
        "azdo timeline",
        "azdo log",
        "azdo search-log",
        "azdo changes",
        "azdo test-runs",
        "azdo test-results",
        "azdo artifacts",
        "azdo search-timeline",
        "azdo test-attachments"
    ];

    [Fact]
    public void CommandRegistry_ContainsAllHelixCommands()
    {
        var routes = CommandRegistry.Commands
            .Where(command => string.Equals(command.Category, "Helix", StringComparison.Ordinal))
            .Select(command => command.Route)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedHelixRoutes.OrderBy(route => route, StringComparer.Ordinal), routes);
    }

    [Fact]
    public void CommandRegistry_ContainsAllAzdoCommands()
    {
        var routes = CommandRegistry.Commands
            .Where(command => string.Equals(command.Category, "AzDO", StringComparison.Ordinal))
            .Select(command => command.Route)
            .OrderBy(route => route, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedAzdoRoutes.OrderBy(route => route, StringComparer.Ordinal), routes);
    }

    [Fact]
    public void CommandRegistry_AllCommandsHaveDescriptions()
    {
        Assert.All(CommandRegistry.Commands, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Description), $"Command '{command.Route}' should have a description.");
        });
    }

    [Fact]
    public void CommandRegistry_AllCommandsHaveParameters()
    {
        Assert.All(CommandRegistry.Commands, command => Assert.NotEmpty(command.Parameters));
    }

    [Fact]
    public void CommandRegistry_McpToolNamesMatchMcpTools()
    {
        var mcpTools = GetMcpToolDescriptions();

        Assert.All(CommandRegistry.Commands, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.McpToolName), $"Command '{command.Route}' should declare an MCP tool name.");
            Assert.True(mcpTools.ContainsKey(command.McpToolName!), $"Command '{command.Route}' points to missing MCP tool '{command.McpToolName}'.");
        });
    }

    [Fact]
    public void CommandRegistry_DescriptionsMatchMcpDescriptions()
    {
        var mcpTools = GetMcpToolDescriptions();

        Assert.All(CommandRegistry.Commands, command =>
        {
            Assert.NotNull(command.McpToolName);
            Assert.Equal(mcpTools[command.McpToolName!], command.Description);
        });
    }

    [Fact]
    public void CommandRegistry_HelixCommandsHaveHelixCategory()
    {
        Assert.All(ExpectedHelixRoutes, route => Assert.Equal("Helix", GetCommand(route).Category));
    }

    [Fact]
    public void CommandRegistry_AzdoCommandsHaveAzdoCategory()
    {
        Assert.All(ExpectedAzdoRoutes, route => Assert.Equal("AzDO", GetCommand(route).Category));
    }

    [Fact]
    public void CommandRegistry_GetByRoute_ReturnsCorrectCommand()
    {
        var command = CommandRegistry.Get("status");

        Assert.NotNull(command);
        Assert.Equal("status", command.Route);
        Assert.Equal("helix_status", command.McpToolName);
    }

    [Fact]
    public void CommandRegistry_GetByRoute_CaseInsensitive()
    {
        var command = CommandRegistry.Get("STATUS");

        Assert.NotNull(command);
        Assert.Equal("status", command.Route);
    }

    [Fact]
    public void CommandRegistry_GetByRoute_NotFound_ReturnsNull()
    {
        Assert.Null(CommandRegistry.Get("nonexistent"));
    }

    [Fact]
    public void StatusCommand_HasExpectedParameters()
    {
        var command = GetCommand("status");

        Assert.Collection(
            command.Parameters,
            parameter => AssertParameter(parameter, "jobId", "String", null, isPositional: true),
            parameter => AssertParameter(parameter, "filter", "String", "failed", isPositional: true),
            parameter => AssertParameter(parameter, "json", "Boolean", "false", isPositional: false),
            parameter => AssertParameter(parameter, "schema", "Boolean", "false", isPositional: false));
    }

    [Fact]
    public void AzdoBuildsCommand_HasExpectedParameters()
    {
        var command = GetCommand("azdo builds");

        Assert.Collection(
            command.Parameters,
            parameter => AssertParameter(parameter, "org", "String", "dnceng-public", isPositional: false),
            parameter => AssertParameter(parameter, "project", "String", "public", isPositional: false),
            parameter => AssertParameter(parameter, "top", "Int32", "20", isPositional: false),
            parameter => AssertParameter(parameter, "branch", "String", "null", isPositional: false),
            parameter => AssertParameter(parameter, "prNumber", "String", "null", isPositional: false),
            parameter => AssertParameter(parameter, "definitionId", "Int32?", "null", isPositional: false),
            parameter => AssertParameter(parameter, "status", "String", "null", isPositional: false),
            parameter => AssertParameter(parameter, "json", "Boolean", "false", isPositional: false),
            parameter => AssertParameter(parameter, "schema", "Boolean", "false", isPositional: false));
    }

    private static CommandRegistry.CommandInfo GetCommand(string route)
    {
        var command = CommandRegistry.Get(route);
        Assert.NotNull(command);
        return command!;
    }

    private static void AssertParameter(CommandRegistry.ParamInfo parameter, string name, string type, string? defaultValue, bool isPositional)
    {
        Assert.Equal(name, parameter.Name);
        Assert.Equal(type, parameter.Type);
        Assert.Equal(defaultValue, parameter.Default);
        Assert.Equal(isPositional, parameter.IsPositional);
    }

    private static IReadOnlyDictionary<string, string> GetMcpToolDescriptions()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var method in typeof(AzdoMcpTools).Assembly
                     .GetTypes()
                     .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                     .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)))
        {
            var tool = method.GetCustomAttribute<McpServerToolAttribute>();
            var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (tool?.Name is { Length: > 0 } name && !string.IsNullOrWhiteSpace(description))
                descriptions[name] = description;
        }

        return descriptions;
    }
}
