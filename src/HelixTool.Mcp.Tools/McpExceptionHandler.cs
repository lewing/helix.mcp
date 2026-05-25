using ModelContextProtocol;

namespace HelixTool.Mcp.Tools;

internal static class McpExceptionHandler
{
    public static async Task<T> RunServiceCallAsync<T>(
        Func<Task<T>> call,
        string action,
        Func<Exception, string?>? getSpecialMessage = null,
        Func<Exception, bool>? isKnownException = null)
    {
        try
        {
            return await call();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapServiceException(ex, action, getSpecialMessage, isKnownException);
        }
    }

    public static T RunServiceCall<T>(
        Func<T> call,
        string action,
        Func<Exception, string?>? getSpecialMessage = null,
        Func<Exception, bool>? isKnownException = null)
    {
        try
        {
            return call();
        }
        catch (McpException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw WrapServiceException(ex, action, getSpecialMessage, isKnownException);
        }
    }

    private static McpException WrapServiceException(
        Exception ex,
        string action,
        Func<Exception, string?>? getSpecialMessage,
        Func<Exception, bool>? isKnownException)
    {
        if (ex is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            return WrapServiceException(aggregate.InnerExceptions[0], action, getSpecialMessage, isKnownException);

        var specialMessage = getSpecialMessage?.Invoke(ex);
        if (!string.IsNullOrWhiteSpace(specialMessage))
            return new McpException(specialMessage, ex);

        if (IsDefaultKnownException(ex) || isKnownException?.Invoke(ex) == true)
            return new McpException($"Failed to {action}: {ex.Message}", ex);

        return new McpException($"Unexpected error during {action}: {ex.Message}", ex);
    }

    private static bool IsDefaultKnownException(Exception ex)
        => ex is InvalidOperationException or HttpRequestException or ArgumentException or TaskCanceledException or OperationCanceledException;
}
