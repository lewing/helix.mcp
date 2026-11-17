namespace HelixTool.Core.Helix;

/// <summary>
/// Abstraction for resolving the current Helix access token.
/// CLI and stdio MCP: env var then stored credential (<see cref="ChainedHelixTokenAccessor"/>). HTTP: returns token from HttpContext.
/// </summary>
public interface IHelixTokenAccessor
{
    string? GetAccessToken();
}

/// <summary>Fixed token supplied by the caller (e.g. an environment variable value).</summary>
public sealed class EnvironmentHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly string? _token;
    public EnvironmentHelixTokenAccessor(string? token) => _token = token;
    public string? GetAccessToken() => _token;
}
