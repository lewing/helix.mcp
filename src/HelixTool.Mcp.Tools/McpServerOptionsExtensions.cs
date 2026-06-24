using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
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

    /// <summary>
    /// Adds a CallToolFilter that rejects unknown parameters BEFORE SDK dispatch, with
    /// "did you mean" hints derived from Levenshtein distance.
    ///
    /// Filter pipeline order (must register in this order):
    ///   1. AddBindingErrorFilter  — normalizes aliases + wraps SDK ArgumentException
    ///   2. AddUnknownParameterFilter — proactive unknown-param check (this method)
    ///   3. SDK dispatch (WithToolsFromAssembly + UnmappedMemberHandling.Disallow as safety net)
    ///
    /// The canonical-param map is built once at registration time from the tool assembly's
    /// InputSchema properties and captured in the filter closure — no per-request parsing.
    /// </summary>
    public static McpServerOptions AddUnknownParameterFilter(
        this McpServerOptions options,
        Assembly toolsAssembly,
        ILogger? logger = null)
    {
        var toolParamMap = BuildToolParamMap(toolsAssembly, logger);

        options.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
        {
            var toolName = request.Params?.Name;
            var arguments = request.Params?.Arguments;

            if (toolName is not null
                && arguments is not null
                && arguments.Count > 0
                && toolParamMap.TryGetValue(toolName, out var paramInfo))
            {
                var unknowns = new List<string>();
                foreach (var key in arguments.Keys)
                {
                    if (!paramInfo.CanonicalSet.Contains(key))
                        unknowns.Add(key);
                }

                if (unknowns.Count > 0)
                {
                    var msg = BuildUnknownParamMessage(toolName, unknowns, paramInfo);
                    throw new McpException(msg);
                }
            }

            return await next(request, ct);
        });

        return options;
    }

    // ── Per-tool canonical-param info (built once at startup) ────────────────

    internal readonly record struct ToolParamInfo(
        HashSet<string> CanonicalSet,   // OrdinalIgnoreCase — used for Contains()
        string[] OrderedNames);         // schema declaration order — used in error messages

    private static Dictionary<string, ToolParamInfo> BuildToolParamMap(
        Assembly assembly,
        ILogger? logger)
    {
        var map = new Dictionary<string, ToolParamInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in assembly.GetExportedTypes())
        {
            object? shell = null;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null)
                    continue;

                var toolName = attr.Name ?? method.Name;
                try
                {
                    // Use an uninitialized shell — no constructor runs, no DI needed.
                    // We only need the ProtocolTool.InputSchema; the tool is never invoked here.
                    shell ??= RuntimeHelpers.GetUninitializedObject(type);
                    var tool = McpServerTool.Create(method, shell, options: null);
                    var info = ExtractToolParamInfo(tool.ProtocolTool.InputSchema, toolName, logger);
                    if (info.HasValue)
                        map[toolName] = info.Value;
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex,
                        "Could not extract schema for tool '{ToolName}'; unknown-param filter skipped for it",
                        toolName);
                }
            }
        }

        return map;
    }

    internal static ToolParamInfo? ExtractToolParamInfo(
        JsonElement schema,
        string toolName,
        ILogger? logger)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            logger?.LogWarning(
                "Tool '{ToolName}' has missing InputSchema; unknown-param filter skipped for it",
                toolName);
            return null;
        }

        // additionalProperties: true — schema explicitly allows arbitrary extra keys; skip filtering.
        if (schema.TryGetProperty("additionalProperties", out var additionalProps)
            && additionalProps.ValueKind == JsonValueKind.True)
        {
            logger?.LogDebug(
                "Tool '{ToolName}' has additionalProperties: true; unknown-param filter skipped for it",
                toolName);
            return null;
        }

        var orderedNames = new List<string>();
        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in properties.EnumerateObject())
                orderedNames.Add(prop.Name);
        }
        // If no "properties" key: parameterless tool; any argument is unknown — orderedNames stays empty.

        var canonicalSet = new HashSet<string>(orderedNames, StringComparer.OrdinalIgnoreCase);
        return new ToolParamInfo(canonicalSet, [.. orderedNames]);
    }

    // ── Error message construction ────────────────────────────────────────────

    private static string BuildUnknownParamMessage(
        string toolName,
        List<string> unknowns,
        ToolParamInfo paramInfo)
    {
        var allowedList = paramInfo.OrderedNames.Length > 0
            ? string.Join(", ", paramInfo.OrderedNames)
            : "(none)";

        var sb = new StringBuilder();
        if (unknowns.Count == 1)
        {
            sb.Append($"Unknown parameter '{unknowns[0]}' for tool '{toolName}'.");
            var hint = FindClosestMatch(unknowns[0], paramInfo.CanonicalSet);
            if (hint is not null)
                sb.Append($"\nDid you mean: {hint}?");
        }
        else
        {
            sb.AppendLine($"Unknown parameters for tool '{toolName}':");
            foreach (var unknown in unknowns)
            {
                var hint = FindClosestMatch(unknown, paramInfo.CanonicalSet);
                sb.Append($"  '{unknown}'");
                if (hint is not null)
                    sb.Append($" — did you mean: {hint}?");
                sb.AppendLine();
            }
        }
        sb.Append($"Allowed parameters: {allowedList}.");
        return sb.ToString();
    }

    internal static string? FindClosestMatch(string unknown, HashSet<string> candidates)
    {
        if (candidates.Count == 0)
            return null;

        var lowerUnknown = unknown.ToLowerInvariant();
        string? best = null;
        int bestDist = LevenshteinThreshold + 1;

        foreach (var candidate in candidates)
        {
            var dist = Levenshtein(lowerUnknown, candidate.ToLowerInvariant());
            if (dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    // Maximum Levenshtein distance for a "did you mean" suggestion.
    // Threshold 6 (not 3): the key regression case minFinishTime→minTime has distance 6.
    // Threshold 3 catches typos only; threshold 6 also catches hallucinated compound names
    // that share a prefix/suffix with the canonical (e.g. minFinishTime → minTime).
    // The full allowed-params list is always shown, so a false-positive hint is harmless.
    private const int LevenshteinThreshold = 6;

    internal static int Levenshtein(string s, string t)
    {
        var m = s.Length;
        var n = t.Length;
        if (m == 0) return n;
        if (n == 0) return m;

        var d = new int[m + 1, n + 1];
        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;

        for (int j = 1; j <= n; j++)
        {
            for (int i = 1; i <= m; i++)
            {
                var cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
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
