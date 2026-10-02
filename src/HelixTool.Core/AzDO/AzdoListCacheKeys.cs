using HelixTool.Core.Cache;

namespace HelixTool.Core.AzDO;

internal static class AzdoListCacheKeys
{
    public static string ChangesComplete(CacheOptions options, string org, string project, int buildId)
        => MetadataKey(options, org, project, $"changes:v2:{buildId}:all");

    public static string ChangesWindow(CacheOptions options, string org, string project, int buildId, int offset, int limit)
        => MetadataKey(options, org, project, $"changes:v2:{buildId}:window:{offset}:{limit}");

    public static string ChangesLegacy(CacheOptions options, string org, string project, int buildId, int? top)
        => MetadataKey(options, org, project, $"changes:{buildId}:{top}");

    public static string TestRunsComplete(CacheOptions options, string org, string project, int buildId)
        => MetadataKey(options, org, project, $"testruns:v3:{buildId}:all");

    public static string TestRunsWindow(CacheOptions options, string org, string project, int buildId, int offset, int limit)
        => MetadataKey(options, org, project, $"testruns:v3:{buildId}:window:{offset}:{limit}");

    public static string TestRunsLegacy(CacheOptions options, string org, string project, int buildId, int? top)
        => MetadataKey(options, org, project, $"testruns:v2:{buildId}:{top}");

    public static string TestResultsComplete(CacheOptions options, string org, string project, int runId, string? outcomes)
        => MetadataKey(options, org, project, $"testresults:v3:{runId}:{NormalizeOutcomes(outcomes)}:all");

    public static string TestResultsWindow(CacheOptions options, string org, string project, int runId, string? outcomes, int offset, int limit)
        => MetadataKey(options, org, project, $"testresults:v3:{runId}:{NormalizeOutcomes(outcomes)}:window:{offset}:{limit}");

    public static string TestResultsLegacy(CacheOptions options, string org, string project, int runId, int top, string? outcomes)
        => MetadataKey(options, org, project, $"testresults:v2:{runId}:{top}:{NormalizeOutcomes(outcomes)}");

    public static string TestAttachmentsComplete(CacheOptions options, string org, string project, int runId, int resultId)
        => MetadataKey(options, org, project, $"testattachments:v2:{runId}:{resultId}:all");

    public static string TestAttachmentsWindow(CacheOptions options, string org, string project, int runId, int resultId, int offset, int limit)
        => MetadataKey(options, org, project, $"testattachments:v2:{runId}:{resultId}:window:{offset}:{limit}");

    public static string TestAttachmentsLegacy(CacheOptions options, string org, string project, int runId, int resultId, int top)
        => MetadataKey(options, org, project, $"testattachments:{runId}:{resultId}:{top}");

    public static string ArtifactsComplete(CacheOptions options, string org, string project, int buildId)
        => MetadataKey(options, org, project, $"artifacts:{buildId}");

    private static string NormalizeOutcomes(string? outcomes)
        => string.IsNullOrWhiteSpace(outcomes) ? AzdoBuildFilterDefaults.Outcomes : outcomes.Trim();

    private static string MetadataKey(CacheOptions options, string org, string project, string suffix)
        => AzdoCacheKeys.MetadataKey(options, org, project, suffix);
}
