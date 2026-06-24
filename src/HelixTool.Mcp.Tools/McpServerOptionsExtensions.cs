using System.Text.Json;
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

    // First match wins per canonical; tuple order is the documented precedence when multiple aliases for the same
    // canonical are present without the canonical key. All aliases are processed in a single pass so that callers
    // passing aliases for different canonicals (e.g. build_id + result on azdo_search_timeline) have all entries
    // renamed before strict-mode unmapped-member checking fires.
    private static readonly (string Alias, string Canonical)[] s_argumentAliases =
    [
        ("build_id", "buildIdOrUrl"),
        ("buildId", "buildIdOrUrl"),
        ("buildUrl", "buildIdOrUrl"),
        // azdo_search_timeline exposes the filter param as 'resultFilter'; callers historically pass 'result'.
        ("result", "resultFilter"),
    ];

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

            arguments[canonical] = CoerceToStringElement(arguments[aliasKey]);
            arguments.Remove(aliasKey);
            logger?.LogDebug(
                "Argument alias resolved: '{Alias}' → '{Canonical}' for tool '{ToolName}'",
                aliasKey,
                canonical,
                parameters?.Name);
            // Continue processing — caller may have passed aliases for multiple distinct canonicals
            // (e.g. build_id + result on azdo_search_timeline). The alias key is removed so strict-mode
            // unmapped-member checking does not flag it after this filter runs.
        }
    }

    private static JsonElement CoerceToStringElement(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value,
            // Number and Boolean are coerced to their string representation so the SDK binder
            // can bind the value to a string parameter (e.g. buildIdOrUrl).
            // GetRawText() on a Number returns bare digits ("2989057"); serializing that C# string
            // produces a JSON string element whose ValueKind == String, which is what the binder needs.
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                => JsonSerializer.SerializeToElement(value.GetRawText()),
            // Object/array/null — leave untouched and let the binder surface its own error.
            _ => value,
        };

    private static bool HasArgument(IDictionary<string, JsonElement> arguments, string key)
        => FindArgumentKey(arguments, key) is not null;

    private static string? FindArgumentKey(IDictionary<string, JsonElement> arguments, string key)
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
