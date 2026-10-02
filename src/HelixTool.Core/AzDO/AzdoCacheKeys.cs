using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HelixTool.Core.Cache;

namespace HelixTool.Core.AzDO;

internal static class AzdoCacheKeys
{
    private static readonly JsonSerializerOptions s_stableCacheKeyOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static typeInfo =>
                {
                    if (typeInfo.Kind != JsonTypeInfoKind.Object) return;
                    var sorted = typeInfo.Properties
                        .OrderBy(p => p.Name, StringComparer.Ordinal)
                        .ToList();
                    typeInfo.Properties.Clear();
                    foreach (var p in sorted)
                        typeInfo.Properties.Add(p);
                }
            }
        }
    };

    public static string MetadataKey(CacheOptions options, string org, string project, string suffix)
    {
        var safeOrg = CacheSecurity.SanitizeCacheKeySegment(org);
        var safeProject = CacheSecurity.SanitizeCacheKeySegment(project);
        var authHash = GetSafeAuthTokenHash(options);
        return string.IsNullOrEmpty(authHash)
            ? $"azdo:{safeOrg}:{safeProject}:{suffix}"
            : $"azdo:{authHash}:{safeOrg}:{safeProject}:{suffix}";
    }

    public static string BuildStateKey(CacheOptions options, string org, string project, int buildId)
    {
        var safeOrg = CacheSecurity.SanitizeCacheKeySegment(org);
        var safeProject = CacheSecurity.SanitizeCacheKeySegment(project);
        var authHash = GetSafeAuthTokenHash(options);
        return string.IsNullOrEmpty(authHash)
            ? $"azdo-build:{safeOrg}:{safeProject}:{buildId}"
            : $"azdo-build:{authHash}:{safeOrg}:{safeProject}:{buildId}";
    }

    public static string HashFilter(AzdoBuildFilter filter)
    {
        var normalized = AzdoBuildFilterNormalizer.Normalize(filter);
        var json = JsonSerializer.Serialize(normalized, s_stableCacheKeyOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    private static string? GetSafeAuthTokenHash(CacheOptions options)
    {
        if (string.IsNullOrEmpty(options.AuthTokenHash))
            return null;

        return CacheSecurity.SanitizeCacheKeySegment(options.AuthTokenHash).Replace(':', '_');
    }
}
