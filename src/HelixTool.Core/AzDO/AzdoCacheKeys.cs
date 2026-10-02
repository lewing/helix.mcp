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

    public static bool TryParse(string key, out AzdoCacheKey parsed)
    {
        parsed = default!;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var parts = key.Split(':', StringSplitOptions.None);
        if (parts.Length == 0)
            return false;

        return parts[0] switch
        {
            "azdo" => TryParseMetadata(parts, out parsed),
            "azdo-build" => TryParseBuildState(parts, out parsed),
            _ => false
        };
    }

    private static string? GetSafeAuthTokenHash(CacheOptions options)
    {
        if (string.IsNullOrEmpty(options.AuthTokenHash))
            return null;

        return CacheSecurity.SanitizeCacheKeySegment(options.AuthTokenHash).Replace(':', '_');
    }

    private static bool TryParseMetadata(string[] parts, out AzdoCacheKey parsed)
    {
        parsed = default!;

        if (parts.Length >= 5 &&
            IsAuthHash(parts[1]) &&
            TryParseMetadataSuffix(parts.AsSpan(4), out var resourceKind, out var suffixSegments))
        {
            parsed = new AzdoCacheKey(
                Prefix: "azdo",
                Partition: $"cache-{parts[1].ToLowerInvariant()}",
                AuthTokenHash: parts[1].ToLowerInvariant(),
                Org: parts[2],
                Project: parts[3],
                ResourceKind: resourceKind,
                SuffixSegments: suffixSegments);
            return true;
        }

        if (parts.Length >= 4 &&
            TryParseMetadataSuffix(parts.AsSpan(3), out resourceKind, out suffixSegments))
        {
            parsed = new AzdoCacheKey(
                Prefix: "azdo",
                Partition: "public",
                AuthTokenHash: null,
                Org: parts[1],
                Project: parts[2],
                ResourceKind: resourceKind,
                SuffixSegments: suffixSegments);
            return true;
        }

        return false;
    }

    private static bool TryParseBuildState(string[] parts, out AzdoCacheKey parsed)
    {
        parsed = default!;

        if (parts.Length == 5 && IsAuthHash(parts[1]) && IsInteger(parts[4]))
        {
            parsed = new AzdoCacheKey(
                Prefix: "azdo-build",
                Partition: $"cache-{parts[1].ToLowerInvariant()}",
                AuthTokenHash: parts[1].ToLowerInvariant(),
                Org: parts[2],
                Project: parts[3],
                ResourceKind: "build-state",
                SuffixSegments: [parts[4]]);
            return true;
        }

        if (parts.Length == 4 && IsInteger(parts[3]))
        {
            parsed = new AzdoCacheKey(
                Prefix: "azdo-build",
                Partition: "public",
                AuthTokenHash: null,
                Org: parts[1],
                Project: parts[2],
                ResourceKind: "build-state",
                SuffixSegments: [parts[3]]);
            return true;
        }

        return false;
    }

    private static bool TryParseMetadataSuffix(
        ReadOnlySpan<string> suffix,
        out string resourceKind,
        out IReadOnlyList<string> suffixSegments)
    {
        resourceKind = "";
        suffixSegments = [];
        if (suffix.Length == 0)
            return false;

        if (!IsValidMetadataSuffix(suffix))
            return false;

        resourceKind = suffix[0];
        suffixSegments = suffix[1..].ToArray();
        return true;
    }

    private static bool IsValidMetadataSuffix(ReadOnlySpan<string> suffix)
    {
        if (suffix.Length == 0)
            return false;

        return suffix[0] switch
        {
            "build" => suffix.Length == 2 && IsInteger(suffix[1]),
            "builds" => suffix.Length == 2,
            "timeline" => suffix.Length == 2 && IsInteger(suffix[1]),
            "log" => suffix.Length == 3 && IsInteger(suffix[1]) && IsInteger(suffix[2]),
            "log-fresh" => suffix.Length == 3 && IsInteger(suffix[1]) && IsInteger(suffix[2]),
            "logslist" => suffix.Length == 2 && IsInteger(suffix[1]),
            "artifacts" => suffix.Length == 2 && IsInteger(suffix[1]),
            "changes" => IsValidChangesSuffix(suffix),
            "testruns" => IsValidTestRunsSuffix(suffix),
            "testresults" => IsValidTestResultsSuffix(suffix),
            "testattachments" => IsValidTestAttachmentsSuffix(suffix),
            _ => false
        };
    }

    private static bool IsValidChangesSuffix(ReadOnlySpan<string> suffix)
        => suffix.Length == 3 && IsInteger(suffix[1]) && IsOptionalInteger(suffix[2])
        || suffix.Length == 4 && suffix[1] == "v2" && IsInteger(suffix[2]) && suffix[3] == "all"
        || suffix.Length == 6 && suffix[1] == "v2" && IsInteger(suffix[2]) && suffix[3] == "window" && IsInteger(suffix[4]) && IsInteger(suffix[5]);

    private static bool IsValidTestRunsSuffix(ReadOnlySpan<string> suffix)
        => suffix.Length == 4 && suffix[1] == "v2" && IsInteger(suffix[2]) && IsOptionalInteger(suffix[3])
        || suffix.Length == 4 && suffix[1] == "v3" && IsInteger(suffix[2]) && suffix[3] == "all"
        || suffix.Length == 6 && suffix[1] == "v3" && IsInteger(suffix[2]) && suffix[3] == "window" && IsInteger(suffix[4]) && IsInteger(suffix[5]);

    private static bool IsValidTestResultsSuffix(ReadOnlySpan<string> suffix)
        => suffix.Length == 5 && suffix[1] == "v2" && IsInteger(suffix[2]) && IsInteger(suffix[3])
        || suffix.Length == 5 && suffix[1] == "v3" && IsInteger(suffix[2]) && suffix[4] == "all"
        || suffix.Length == 7 && suffix[1] == "v3" && IsInteger(suffix[2]) && suffix[4] == "window" && IsInteger(suffix[5]) && IsInteger(suffix[6]);

    private static bool IsValidTestAttachmentsSuffix(ReadOnlySpan<string> suffix)
        => suffix.Length == 4 && IsInteger(suffix[1]) && IsInteger(suffix[2]) && IsInteger(suffix[3])
        || suffix.Length == 5 && suffix[1] == "v2" && IsInteger(suffix[2]) && IsInteger(suffix[3]) && suffix[4] == "all"
        || suffix.Length == 7 && suffix[1] == "v2" && IsInteger(suffix[2]) && IsInteger(suffix[3]) && suffix[4] == "window" && IsInteger(suffix[5]) && IsInteger(suffix[6]);

    private static bool IsAuthHash(string value)
        => value.Length == 8 && value.All(Uri.IsHexDigit);

    private static bool IsInteger(string value)
        => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static bool IsOptionalInteger(string value)
        => value.Length == 0 || IsInteger(value);
}

internal sealed record AzdoCacheKey(
    string Prefix,
    string Partition,
    string? AuthTokenHash,
    string Org,
    string Project,
    string ResourceKind,
    IReadOnlyList<string> SuffixSegments);
