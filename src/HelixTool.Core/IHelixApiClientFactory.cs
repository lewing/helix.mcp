namespace HelixTool.Core;

/// <summary>
/// Factory for creating <see cref="IHelixApiClient"/> instances with a specific auth token.
/// Used in HTTP/SSE MCP transport where each client has its own token.
/// </summary>
public interface IHelixApiClientFactory
{
    IHelixApiClient Create(string? accessToken);
}

public sealed class HelixApiClientFactory : IHelixApiClientFactory
{
    public IHelixApiClient Create(string? accessToken) => new HelixApiClient(accessToken);
}
