using HelixTool.Core;
using Microsoft.AspNetCore.Http;

namespace HelixTool.Mcp;

/// <summary>
/// Extracts Helix token from the HTTP Authorization header.
/// Supports "Bearer {token}" and "token {token}" formats (case-insensitive scheme).
/// Falls back to HELIX_ACCESS_TOKEN env var if no header present.
/// </summary>
public sealed class HttpContextHelixTokenAccessor : IHelixTokenAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string? _fallbackToken;

    public HttpContextHelixTokenAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _fallbackToken = Environment.GetEnvironmentVariable("HELIX_ACCESS_TOKEN");
    }

    public string? GetAccessToken()
    {
        var authHeader = _httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authHeader))
        {
            var spaceIdx = authHeader.IndexOf(' ');
            if (spaceIdx > 0)
            {
                var scheme = authHeader[..spaceIdx];
                if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
                    scheme.Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    var value = authHeader[(spaceIdx + 1)..].Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            // Unknown scheme or no space — fall back to env var
            return _fallbackToken;
        }
        return _fallbackToken;
    }
}
