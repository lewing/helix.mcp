namespace HelixTool.Mcp;

/// <summary>
/// Middleware that validates requests against a pre-shared API key.
/// Active only when HLX_API_KEY environment variable is set.
/// Expects the key in the X-Api-Key header.
/// </summary>
public sealed class ApiKeyMiddleware
{
    public const string HeaderName = "X-Api-Key";
    public const string EnvVarName = "HLX_API_KEY";

    private readonly RequestDelegate _next;
    private readonly string _apiKey;

    public ApiKeyMiddleware(RequestDelegate next, string apiKey)
    {
        _next = next;
        _apiKey = apiKey;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var providedKey) ||
            !string.Equals(_apiKey, providedKey.ToString(), StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Invalid or missing API key.");
            return;
        }

        await _next(context);
    }
}

public static class ApiKeyMiddlewareExtensions
{
    /// <summary>
    /// Adds API key authentication middleware if HLX_API_KEY is set.
    /// Returns true if auth was enabled, false if skipped.
    /// </summary>
    public static bool UseApiKeyAuthIfConfigured(this IApplicationBuilder app)
    {
        var apiKey = Environment.GetEnvironmentVariable(ApiKeyMiddleware.EnvVarName);
        if (string.IsNullOrEmpty(apiKey))
            return false;

        app.UseMiddleware<ApiKeyMiddleware>(apiKey);
        return true;
    }
}
