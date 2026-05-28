using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace HelixTool.Mcp.Tools;

public static class McpServerOptionsExtensions
{
    // Microsoft.Extensions.AI.AIFunctionFactory.ReflectionAIFunction.InvokeCoreAsync uses
    // paramName = "arguments" when SDK binder validation fails; Ash verified this in stderr during the 2026-05-28 investigation.
    private const string BinderArgumentsParamName = "arguments";

    public static McpServerOptions AddBindingErrorFilter(this McpServerOptions options)
    {
        options.Filters.Request.CallToolFilters.Add(next => async (request, ct) =>
        {
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
}
