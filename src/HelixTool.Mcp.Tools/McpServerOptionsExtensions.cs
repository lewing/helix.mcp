using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace HelixTool.Mcp.Tools;

public static class McpServerOptionsExtensions
{
    // Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.InvokeCoreAsync uses
    // paramName = "arguments" when SDK binder validation fails; Ash verified this in stderr during the 2026-05-28 investigation.
    private const string BinderArgumentsParamName = "arguments";

    // First match wins; insertion order is significant when multiple aliases are present without a canonical key.
    private static readonly Dictionary<string, string> s_argumentAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["build_id"] = "buildIdOrUrl",
        ["buildId"] = "buildIdOrUrl",
        ["buildUrl"] = "buildIdOrUrl",
    };

    public static McpServerOptions AddBindingErrorFilter(this McpServerOptions options, ILogger? logger = null)
    {
        options.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
        {
            NormalizeArgumentAliases(request.Params, logger ?? CreateLogger(request.Services));

            try
            {
                return await next(request, ct);
            }
            catch (ArgumentException ex) when (ex.ParamName == BinderArgumentsParamName)
            {
                throw new McpException(
                    $"Parameter binding error for '{request.Params?.Name}': {ex.Message}", ex);
            }
        });

        return options;
    }

    private static ILogger? CreateLogger(IServiceProvider? services)
        => services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(McpServerOptionsExtensions));

    private static void NormalizeArgumentAliases(CallToolRequestParams? parameters, ILogger? logger = null)
    {
        var arguments = parameters?.Arguments;
        if (arguments is null)
        {
            return;
        }

        foreach (var (alias, canonical) in s_argumentAliases)
        {
            if (HasArgument(arguments, canonical))
            {
                continue;
            }

            var aliasKey = FindArgumentKey(arguments, alias);
            if (aliasKey is null)
            {
                continue;
            }

            arguments[canonical] = arguments[aliasKey];
            logger?.LogDebug(
                "Argument alias resolved: '{Alias}' → '{Canonical}' for tool '{ToolName}'",
                aliasKey,
                canonical,
                parameters?.Name);
            return;
        }
    }

    private static bool HasArgument(IDictionary<string, System.Text.Json.JsonElement> arguments, string key)
        => FindArgumentKey(arguments, key) is not null;

    private static string? FindArgumentKey(IDictionary<string, System.Text.Json.JsonElement> arguments, string key)
    {
        foreach (var argumentKey in arguments.Keys)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(argumentKey, key))
            {
                return argumentKey;
            }
        }

        return null;
    }
}
