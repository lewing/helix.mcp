using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace HelixTool.Generators;

[Generator]
public sealed class DescribeGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var cliCommands = context.SyntaxProvider.ForAttributeWithMetadataName(
                "HelixTool.Core.McpEquivalentAttribute",
                static (node, _) => node is MethodDeclarationSyntax,
                static (syntaxContext, _) => CreateCliCommandInfo(syntaxContext))
            .Where(static info => info is not null)
            .Select(static (info, _) => info!);

        var mcpTools = context.CompilationProvider.Select(static (compilation, _) => ExtractMcpToolInfo(compilation));
        var combined = cliCommands.Collect().Combine(mcpTools);

        context.RegisterSourceOutput(combined, static (productionContext, source) =>
        {
            var commands = Join(source.Left, source.Right);
            productionContext.AddSource("CommandRegistry.g.cs", GenerateSource(commands));
        });
    }

    private static CliCommandInfo? CreateCliCommandInfo(GeneratorAttributeSyntaxContext syntaxContext)
    {
        if (syntaxContext.TargetSymbol is not IMethodSymbol method)
            return null;

        var mcpEquivalent = syntaxContext.Attributes.FirstOrDefault();
        var mcpToolName = mcpEquivalent?.ConstructorArguments.FirstOrDefault().Value as string;
        if (string.IsNullOrWhiteSpace(mcpToolName))
            return null;

        var route = GetCommandRoute(method);
        if (string.IsNullOrWhiteSpace(route))
            return null;

        var parameters = method.Parameters.Select(CreateParameterInfo).ToImmutableArray();
        var category = GetCategory(method.ContainingType?.Name);
        var order = method.Locations.FirstOrDefault(static location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue;

        return new CliCommandInfo(route!, mcpToolName!, category, parameters, order);
    }

    private static CliParameterInfo CreateParameterInfo(IParameterSymbol parameter)
    {
        var defaultValue = parameter.HasExplicitDefaultValue
            ? FormatDefaultValue(parameter.ExplicitDefaultValue, parameter.Type)
            : null;

        return new CliParameterInfo(
            parameter.Name,
            FormatType(parameter.Type),
            defaultValue,
            HasAttribute(parameter, "ArgumentAttribute"));
    }

    private static ImmutableDictionary<string, string> ExtractMcpToolInfo(Compilation compilation)
    {
        var assembly = compilation.References
            .Select(compilation.GetAssemblyOrModuleSymbol)
            .OfType<IAssemblySymbol>()
            .FirstOrDefault(static symbol => string.Equals(symbol.Name, "HelixTool.Mcp.Tools", StringComparison.Ordinal));

        if (assembly is null)
            return ImmutableDictionary<string, string>.Empty;

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var type in EnumerateTypes(assembly.GlobalNamespace))
        {
            if (!HasAttribute(type, "McpServerToolTypeAttribute"))
                continue;

            foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.MethodKind != MethodKind.Ordinary)
                    continue;

                var toolAttribute = method.GetAttributes().FirstOrDefault(static attribute => IsAttribute(attribute, "McpServerToolAttribute"));
                if (toolAttribute is null)
                    continue;

                var toolName = GetStringAttributeValue(toolAttribute, "Name");
                if (string.IsNullOrWhiteSpace(toolName))
                    continue;

                var description = method.GetAttributes()
                    .FirstOrDefault(static attribute => IsAttribute(attribute, "DescriptionAttribute"))?
                    .ConstructorArguments.FirstOrDefault().Value as string;

                if (!string.IsNullOrWhiteSpace(description))
                    builder[toolName!] = description!;
            }
        }

        return builder.ToImmutable();
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamespaceSymbol namespaceSymbol)
    {
        foreach (var member in namespaceSymbol.GetTypeMembers())
        {
            foreach (var type in EnumerateTypes(member))
                yield return type;
        }

        foreach (var nestedNamespace in namespaceSymbol.GetNamespaceMembers())
        {
            foreach (var type in EnumerateTypes(nestedNamespace))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateTypes(INamedTypeSymbol typeSymbol)
    {
        yield return typeSymbol;

        foreach (var nestedType in typeSymbol.GetTypeMembers())
        {
            foreach (var descendant in EnumerateTypes(nestedType))
                yield return descendant;
        }
    }

    private static ImmutableArray<GeneratedCommandInfo> Join(
        ImmutableArray<CliCommandInfo> cliCommands,
        ImmutableDictionary<string, string> mcpTools)
    {
        var commands = cliCommands
            .Select(static command => command)
            .OrderBy(static command => GetCategoryOrder(command.Category))
            .ThenBy(static command => command.Order)
            .Select(command => new GeneratedCommandInfo(
                command.Route,
                command.McpToolName,
                mcpTools.TryGetValue(command.McpToolName, out var description) ? description : null,
                command.Category,
                command.Parameters))
            .ToImmutableArray();

        return commands;
    }

    private static string GenerateSource(ImmutableArray<GeneratedCommandInfo> commands)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("using System;");
        builder.AppendLine();
        builder.AppendLine("namespace HelixTool.Generated");
        builder.AppendLine("{");
        builder.AppendLine("    public static class CommandRegistry");
        builder.AppendLine("    {");
        builder.AppendLine("        public sealed record ParamInfo(string Name, string Type, string? Default, bool IsPositional);");
        builder.AppendLine("        public sealed record CommandInfo(string Route, string? McpToolName, string? Description, string Category, ParamInfo[] Parameters);");
        builder.AppendLine();

        if (commands.Length == 0)
        {
            builder.AppendLine("        public static readonly CommandInfo[] Commands = Array.Empty<CommandInfo>();");
        }
        else
        {
            builder.AppendLine("        public static readonly CommandInfo[] Commands = new CommandInfo[]");
            builder.AppendLine("        {");
            for (var i = 0; i < commands.Length; i++)
            {
                var command = commands[i];
                builder.Append("            new CommandInfo(")
                    .Append(ToLiteral(command.Route))
                    .Append(", ")
                    .Append(ToNullableLiteral(command.McpToolName))
                    .Append(", ")
                    .Append(ToNullableLiteral(command.Description))
                    .Append(", ")
                    .Append(ToLiteral(command.Category))
                    .Append(", ");

                if (command.Parameters.Length == 0)
                {
                    builder.Append("Array.Empty<ParamInfo>()");
                }
                else
                {
                    builder.AppendLine("new ParamInfo[]");
                    builder.AppendLine("            {");
                    for (var j = 0; j < command.Parameters.Length; j++)
                    {
                        var parameter = command.Parameters[j];
                        builder.Append("                new ParamInfo(")
                            .Append(ToLiteral(parameter.Name))
                            .Append(", ")
                            .Append(ToLiteral(parameter.Type))
                            .Append(", ")
                            .Append(ToNullableLiteral(parameter.DefaultValue))
                            .Append(", ")
                            .Append(parameter.IsPositional ? "true" : "false")
                            .Append(')');

                        if (j < command.Parameters.Length - 1)
                            builder.Append(',');

                        builder.AppendLine();
                    }
                    builder.Append("            }");
                }

                builder.Append(')');
                if (i < commands.Length - 1)
                    builder.Append(',');
                builder.AppendLine();
            }
            builder.AppendLine("        };");
        }

        builder.AppendLine();
        builder.AppendLine("        public static CommandInfo? Get(string route) =>");
        builder.AppendLine("            Array.Find(Commands, command => command.Route.Equals(route, StringComparison.OrdinalIgnoreCase));");
        builder.AppendLine("    }");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static string? GetCommandRoute(IMethodSymbol method)
        => method.GetAttributes()
            .FirstOrDefault(static attribute => IsAttribute(attribute, "CommandAttribute"))?
            .ConstructorArguments.FirstOrDefault().Value as string;

    private static bool HasAttribute(ISymbol symbol, string attributeName)
        => symbol.GetAttributes().Any(attribute => IsAttribute(attribute, attributeName));

    private static bool IsAttribute(AttributeData attribute, string attributeName)
        => string.Equals(attribute.AttributeClass?.Name, attributeName, StringComparison.Ordinal)
            || string.Equals(attribute.AttributeClass?.Name, attributeName.Replace("Attribute", string.Empty), StringComparison.Ordinal);

    private static string? GetStringAttributeValue(AttributeData attribute, string name)
    {
        foreach (var pair in attribute.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal) && pair.Value.Value is string value)
                return value;
        }

        return attribute.ConstructorArguments.FirstOrDefault().Value as string;
    }

    private static string GetCategory(string? containingTypeName)
        => containingTypeName switch
        {
            "Commands" => "Helix",
            "AzdoCommands" => "AzDO",
            _ => "Utility"
        };

    private static int GetCategoryOrder(string category)
        => category switch
        {
            "Helix" => 0,
            "AzDO" => 1,
            _ => 2
        };

    private static string FormatType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol arrayType)
            return FormatType(arrayType.ElementType) + "[]";

        if (type is INamedTypeSymbol namedType && namedType.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return FormatType(namedType.TypeArguments[0]) + "?";

        if (type.SpecialType != SpecialType.None)
        {
            return type.SpecialType switch
            {
                SpecialType.System_Boolean => "Boolean",
                SpecialType.System_Byte => "Byte",
                SpecialType.System_Char => "Char",
                SpecialType.System_Decimal => "Decimal",
                SpecialType.System_Double => "Double",
                SpecialType.System_Int16 => "Int16",
                SpecialType.System_Int32 => "Int32",
                SpecialType.System_Int64 => "Int64",
                SpecialType.System_Object => "Object",
                SpecialType.System_SByte => "SByte",
                SpecialType.System_Single => "Single",
                SpecialType.System_String => "String",
                SpecialType.System_UInt16 => "UInt16",
                SpecialType.System_UInt32 => "UInt32",
                SpecialType.System_UInt64 => "UInt64",
                _ => type.Name
            };
        }

        if (type is INamedTypeSymbol genericType && genericType.IsGenericType)
            return genericType.Name + "<" + string.Join(", ", genericType.TypeArguments.Select(FormatType)) + ">";

        return type.Name;
    }

    private static string? FormatDefaultValue(object? value, ITypeSymbol type)
    {
        if (value is null)
            return "null";

        if (value is string stringValue)
            return stringValue;

        if (value is bool boolValue)
            return boolValue ? "true" : "false";

        if (type.TypeKind == TypeKind.Enum)
            return value.ToString();

        if (value is char charValue)
            return charValue.ToString();

        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return value.ToString();
    }

    private static string ToLiteral(string value)
        => Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private static string ToNullableLiteral(string? value)
        => value is null ? "null" : ToLiteral(value);

    private sealed class CliCommandInfo
    {
        public CliCommandInfo(string route, string mcpToolName, string category, ImmutableArray<CliParameterInfo> parameters, int order)
        {
            Route = route;
            McpToolName = mcpToolName;
            Category = category;
            Parameters = parameters;
            Order = order;
        }

        public string Route { get; }
        public string McpToolName { get; }
        public string Category { get; }
        public ImmutableArray<CliParameterInfo> Parameters { get; }
        public int Order { get; }
    }

    private sealed class CliParameterInfo
    {
        public CliParameterInfo(string name, string type, string? defaultValue, bool isPositional)
        {
            Name = name;
            Type = type;
            DefaultValue = defaultValue;
            IsPositional = isPositional;
        }

        public string Name { get; }
        public string Type { get; }
        public string? DefaultValue { get; }
        public bool IsPositional { get; }
    }

    private sealed class GeneratedCommandInfo
    {
        public GeneratedCommandInfo(string route, string mcpToolName, string? description, string category, ImmutableArray<CliParameterInfo> parameters)
        {
            Route = route;
            McpToolName = mcpToolName;
            Description = description;
            Category = category;
            Parameters = parameters;
        }

        public string Route { get; }
        public string McpToolName { get; }
        public string? Description { get; }
        public string Category { get; }
        public ImmutableArray<CliParameterInfo> Parameters { get; }
    }
}
