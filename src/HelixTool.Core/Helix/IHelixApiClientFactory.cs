namespace HelixTool.Core.Helix;

/// <summary>
/// Factory for creating <see cref="IHelixApiClient"/> instances with a specific auth token.
/// Used in HTTP/SSE MCP transport where each client has its own token.
/// </summary>
public interface IHelixApiClientFactory
{
    IHelixApiClient Create(string? accessToken);
}

public sealed class HelixApiClientFactory(HttpClient? downloadClient = null) : IHelixApiClientFactory
{
    public IHelixApiClient Create(string? accessToken) =>
        new HelixApiClient(accessToken, downloadClient);
}
